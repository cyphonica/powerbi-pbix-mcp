using System.Globalization;

namespace SuperBiMcp.Jobs;

/// <summary>
/// The one process-wide lifetime for the job subsystem: the open queue, the startup reconcile, the stuck-job
/// watchdog and the orphan reaper, under ONE cancellation model. Two very different hosts share this
/// lifetime - `serve` calls <see cref="Start"/> directly and `service` runs it inside a BackgroundService -
/// and a second lifetime model is how a stop hangs or orphans work mid-kill, so this type owns the only
/// CancellationTokenSource and <see cref="JobMaintenanceService"/> is the only production caller of
/// <see cref="Stop"/>.
///
/// Log sinks are passed in, never resolved: verb dispatch runs before any DI host exists, and the same code
/// logs to console stderr under `serve` and to the engine.log writer under `service`. stdout is the MCP
/// JSON-RPC channel, so no sink here may ever write to it.
/// </summary>
internal static class Runtime
{
    private static readonly object Gate = new();

    private static JobQueue? _queue;
    private static SemaphoreSlim? _heavySlots;
    private static CancellationTokenSource? _cts;
    private static Task? _maintenance;
    private static int? _heavyLimitForTest;
    private static int? _cheapLimitForTest;
    private static int? _maxTotalForTest;

    internal static bool Started => _queue is not null;

    /// <summary>The open queue. Throws when the runtime is not started: a caller must refuse rather than
    /// fall back to a queue handle of its own, or two lifetimes race one file.</summary>
    internal static JobQueue Queue =>
        _queue ?? throw new InvalidOperationException(
            "the job runtime is not started (or its queue failed to open - see the [jobs] startup log).");

    /// <summary>In-process fast guard over the heavy lane. queue.db stays the cross-process truth; this only
    /// stops one process racing itself between two of its own admission decisions.</summary>
    internal static SemaphoreSlim HeavySlots =>
        _heavySlots ?? throw new InvalidOperationException(
            "the job runtime is not started (or its queue failed to open - see the [jobs] startup log).");

    /// <summary>Desktop-touching concurrency. The default of 1 is a proven floor, not a RAM opinion: two
    /// stacked Desktops OOM the box, and they fight for the foreground so a Ctrl+S can land in the wrong
    /// window and save the wrong .pbix.</summary>
    internal static int HeavyLimit => _heavyLimitForTest ?? EnvInt("SUPERBI_MAX_HEAVY", 1, floor: 1);

    internal static int CheapLimit => _cheapLimitForTest ?? EnvInt("SUPERBI_MAX_BUILDS", 3, floor: 1);

    /// <summary>The GLOBAL concurrent in-flight cap - ADMITTED+RUNNING+VERIFYING across BOTH lanes - passed
    /// into <see cref="JobQueue.TryAdmit"/> as maxTotal. SUPERBI_MAX_BUILDS keeps its pre-Phase-0 meaning:
    /// on the production box (=1) at most one job runs engine-wide, byte-preserving the old single-semaphore
    /// serialization; lane parallelism is opt-in by raising it. The lane split alone must never let a heavy
    /// bake and a cheap build stack on an 8GB box whose operator asked for one build at a time.</summary>
    internal static int MaxTotal => _maxTotalForTest ?? EnvInt("SUPERBI_MAX_BUILDS", 3, floor: 1);

    /// <summary>Seconds a caller waits for admission. 0 = wait forever, the pre-queue blocking behaviour
    /// and the sync default.</summary>
    internal static int AdmitWaitSec => EnvInt("SUPERBI_ADMIT_WAIT_SEC", 0, floor: 0);

    /// <summary>Cap on how long a dispatch may wait on a host FLOOR verdict (HoldRam/HoldDisk) before
    /// failing 503 retryable. Waiting on a LANE stays indefinite - the old semaphore semantics - but a floor
    /// can stay breached forever without operator action, and a starved box must degrade loudly, never hang
    /// silently.</summary>
    internal static int FloorWaitSec => EnvInt("SUPERBI_FLOOR_WAIT_SEC", 900, floor: 1);

    /// <summary>Minutes between maintenance passes; 0 turns the loop off.</summary>
    internal static int ReaperMinutes => EnvInt("SUPERBI_REAPER_MINUTES", 5, floor: 0);

    /// <summary>How long an in-flight job may stay silent before the watchdog marks it DEAD.</summary>
    internal static TimeSpan StuckAfter => TimeSpan.FromMinutes(EnvInt("SUPERBI_STUCK_MINUTES", 45, floor: 1));

    /// <summary>
    /// Idempotent: the first successful call opens the lifetime and every later call is a no-op, so whichever
    /// host thread arrives first wins. Creates the root, opens the queue, reconciles away in-flight rows with
    /// no live owner, runs one orphan reap, then starts the maintenance loop on this type's own CTS.
    /// Failures only log - a box that cannot be tidied must still come up, and the loop's timer never dies.
    /// A queue that failed to open leaves <see cref="Started"/> false, so every later <see cref="Queue"/>
    /// access refuses loudly instead of pretending an empty queue and re-running paid work.
    /// </summary>
    internal static void Start(Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        lock (Gate)
        {
            if (_queue is not null) return;

            JobQueue queue;
            try
            {
                JobPaths.CreateRoot();
                queue = JobQueue.Open(JobPaths.QueueDbPath);
            }
            catch (Exception ex)
            {
                log($"start failed - the queue at '{JobPaths.QueueDbPath}' is not available: {ex.Message}");
                return;
            }

            _queue = queue;
            _heavySlots = new SemaphoreSlim(HeavyLimit, HeavyLimit);
            HostResources.JobRootProvider = static () => JobPaths.Root;
            HealthProbe.QueueProvider = static () => _queue;

            try
            {
                // Owner identity is pid + start time (PidAlive's two-arg overload, 1s tolerance) - a recycled
                // pid must not keep a phantom row holding the lane. Passing our incarnation also retires
                // QUEUED rows a dead predecessor enqueued: their waiter threads died with it, so no admission
                // will ever come and, unswept, the oldest would deadlock its lane as a permanent false head.
                int lost = queue.Reconcile(Environment.ProcessId, DesktopInterop.PidAlive, DateTimeOffset.UtcNow,
                                           JobQueue.ProcessIncarnation);
                if (lost > 0) log($"reconcile: marked {lost} orphaned job(s) DEAD (no live owner, or QUEUED by a dead engine)");
            }
            catch (Exception ex) { log($"reconcile failed ({ex.GetType().Name}): {ex.Message}"); }

            try { OrphanReaper.Reap(queue, log, DateTimeOffset.UtcNow); }
            catch (Exception ex) { log($"startup reap failed ({ex.GetType().Name}): {ex.Message}"); }

            _cts = new CancellationTokenSource();
            int minutes = ReaperMinutes;
            if (minutes > 0)
            {
                CancellationToken token = _cts.Token;
                _maintenance = Task.Run(() => MaintenanceLoop(queue, log, TimeSpan.FromMinutes(minutes), token));
            }
            else
            {
                log("maintenance loop off (SUPERBI_REAPER_MINUTES=0)");
            }
        }
    }

    /// <summary>Cancels the maintenance loop and disposes the lifetime. In production only
    /// <see cref="JobMaintenanceService"/> calls this, which is what makes shutdown deterministic; tests go
    /// through <see cref="StopForTest"/>.</summary>
    internal static void Stop()
    {
        Task? loop;
        lock (Gate)
        {
            if (_queue is null && _cts is null) return;
            try { _cts?.Cancel(); } catch (ObjectDisposedException) { }
            loop = _maintenance;
            _maintenance = null;
        }

        // Outside the lock, so concurrent Queue readers are not held for the whole wait: a mid-pass loop
        // gets a moment to observe the cancel before its queue is disposed under it.
        try { loop?.Wait(TimeSpan.FromSeconds(5)); } catch { /* a faulted or slow pass must not block shutdown */ }

        lock (Gate)
        {
            _cts?.Dispose();
            _cts = null;
            _queue?.Dispose();
            _queue = null;
            // Dropped, never disposed: a waiter still holding the old instance must complete its Wait or
            // Release cleanly, not hit ObjectDisposedException mid-flight.
            _heavySlots = null;
        }
    }

    /// <summary>One stderr line carrying the whole resolved configuration, printed at host startup.</summary>
    internal static string StartupBanner()
    {
        long reserve = EnvLong("SUPERBI_HEAVY_RESERVE_MB", AdmissionPolicy.BaseReserveMB);
        long ramFloor = EnvLong("SUPERBI_RAM_FLOOR_MB", AdmissionPolicy.RamFloorMB);
        double diskFloor = EnvDouble("SUPERBI_DISK_FLOOR_GB", AdmissionPolicy.DiskFloorGB);
        int reaper = ReaperMinutes;
        string reaperText = reaper > 0 ? $"reaper {reaper}min" : "reaper off";
        string waitText = AdmitWaitSec == 0 ? "forever" : AdmitWaitSec.ToString(CultureInfo.InvariantCulture) + "s";

        return $"Job queue: {JobPaths.QueueDbPath}  (lanes: cheap {CheapLimit}, heavy {HeavyLimit}, total cap {MaxTotal}; " +
               $"reserve {reserve}MB, ram floor {ramFloor}MB, " +
               $"disk floor {diskFloor.ToString("0.#", CultureInfo.InvariantCulture)}GB per measured volume; " +
               $"{reaperText}, stuck {(int)StuckAfter.TotalMinutes}min, admit-wait {waitText}, floor-wait {FloorWaitSec}s)";
    }

    /// <summary>Points the subsystem at a scratch root with fixed lane limits. It does NOT start anything:
    /// the caller still calls <see cref="Start"/> itself, so the not-started behaviour stays assertable.</summary>
    internal static void ResetForTest(string rootOverride, int heavyLimit = 1, int cheapLimit = 3,
                                      int? maxTotal = null)
    {
        Stop();
        lock (Gate)
        {
            JobPaths.RootForTest = rootOverride;
            _heavyLimitForTest = heavyLimit;
            _cheapLimitForTest = cheapLimit;
            _maxTotalForTest = maxTotal;
        }
    }

    /// <summary>Stops the lifetime and clears every override and seam that ResetForTest or Start installed.</summary>
    internal static void StopForTest()
    {
        Stop();
        lock (Gate)
        {
            JobPaths.RootForTest = null;
            _heavyLimitForTest = null;
            _cheapLimitForTest = null;
            _maxTotalForTest = null;
            HostResources.JobRootProvider = null;
            HealthProbe.QueueProvider = null;
        }
    }

    /// <summary>The watchdog + reaper pass, every <paramref name="period"/> until cancelled. A pass that
    /// fails logs and waits for the next tick: the timer itself never dies.</summary>
    private static async Task MaintenanceLoop(JobQueue queue, Action<string> log, TimeSpan period,
                                              CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(period, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                DateTimeOffset now = DateTimeOffset.UtcNow;
                int stuck = queue.ReapStuck(StuckAfter, now);
                if (stuck > 0)
                    log($"watchdog: marked {stuck} silent job(s) DEAD (no heartbeat for " +
                        $"{StuckAfter.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes)");
                OrphanReaper.Reap(queue, log, now);
            }
            catch (Exception ex)
            {
                try { log($"maintenance pass failed ({ex.GetType().Name}): {ex.Message}"); } catch { }
            }
        }
    }

    // A value that does not parse, or is below the floor, leaves the default standing: a mistyped override
    // must not zero a lane or turn a watchdog into an instant killer.
    private static int EnvInt(string name, int fallback, int floor) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int v) && v >= floor ? v : fallback;

    private static long EnvLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out long v) && v >= 0 ? v : fallback;

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double v) && v >= 0 ? v : fallback;
}
