using System.Diagnostics;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp;

/// <summary>Why a Desktop that started never became usable, kept apart because each one has a different fix.
/// Every kind shares ONE load-bearing condition: no msmdsrv child of the launched pid was listening within the
/// deadline. Window state and title only refine the diagnosis - they never convict a Desktop on their own,
/// because a healthy document whose NAME contains the splash text would then be killed for its filename.</summary>
internal enum SplashHangKind { NoEnginePort, NoWindow, TitleStuckOpening }

/// <summary>A Desktop that launched but never finished opening. The caller requeues the job once.</summary>
internal sealed class SplashHangException : Exception
{
    internal SplashHangKind Kind { get; }
    internal int LaunchedPid { get; }

    internal SplashHangException(SplashHangKind kind, int launchedPid, string message) : base(message)
    {
        Kind = kind;
        LaunchedPid = launchedPid;
    }
}

/// <summary>
/// The one sanctioned way a pipeline gets a Power BI Desktop and its engine port. This session OWNS the
/// launch, so the port it hands back provably belongs to the Desktop it started: Process.Start ->
/// LaunchedPid -> the msmdsrv child of that pid -> that child's listening port -> the ownership assert.
///
/// It never discovers an engine by port file, by GetProcessesByName or by asking which msmdsrv happens to
/// be on a port, because every one of those can land on somebody else's Desktop - a second job's, or the
/// operator's own interactive one - and a Ctrl+S against the wrong document silently corrupts it.
///
/// Engine disconnect and process teardown are separate operations on purpose: the Ctrl+S save has to happen
/// between them (the engine must be released before Desktop will save, and Desktop must still be alive to
/// receive the keystroke).
///
/// <see cref="ResolveEnginePort"/> is pure and unit-tested offline with fake process maps; <see cref="Launch"/>
/// itself is deliberately untested here (it needs a real Desktop, which no CI box has).
/// </summary>
internal sealed class DesktopSession : IDisposable
{
    /// <summary>Desktop's splash titles the document as "Opening ..." until the model is actually up.</summary>
    private const string OpeningTitle = "Opening";

    private readonly Action<string> _log;
    private TOM.Server? _srv;
    private bool _closed;

    private DesktopSession(Process proc, string pbixPath, DateTime launchedStartUtc, int msmdsrvPid,
        DateTime msmdsrvStartUtc, int port, DateTime deadlineUtc, Action<string> log)
    {
        Process = proc;
        PbixPath = pbixPath;
        LaunchedPid = proc.Id;
        LaunchedStartUtc = launchedStartUtc;
        MsmdsrvPid = msmdsrvPid;
        MsmdsrvStartUtc = msmdsrvStartUtc;
        Port = port;
        DeadlineUtc = deadlineUtc;
        _log = log;
    }

    internal int LaunchedPid { get; }
    /// <summary>Pid alone is never an identity: pids get recycled onto strangers.</summary>
    internal DateTime LaunchedStartUtc { get; }
    internal Process Process { get; }
    /// <summary>Exactly the path handed to Desktop, so the save can prove it is saving that document.</summary>
    internal string PbixPath { get; }
    internal int MsmdsrvPid { get; }
    internal DateTime MsmdsrvStartUtc { get; }
    internal int Port { get; }
    /// <summary>One deadline shared by the port hunt, the splash watch, the connect wait and the model wait.</summary>
    internal DateTime DeadlineUtc { get; }

    /// <summary>
    /// Launch Desktop on <paramref name="pbixPath"/> and block until its OWN engine is listening, or classify
    /// the failure as a splash-hang. A splash-hang never leaks a Desktop: the process tree is torn down before
    /// the exception leaves.
    ///
    /// <paramref name="queue"/> + <paramref name="jobId"/> are the kill-safety recording seam: when both are
    /// given, the launched Desktop and its engine are recorded into the queue (<c>JobQueue.SetDesktop</c>) the
    /// moment their identities (pid + start time) are proven, so a crash anywhere after that leaves a record
    /// the orphan reaper can act on rather than an invisible multi-GB survivor. Without them (the CLI refresh
    /// path has no queue context) nothing is recorded and this Desktop stays structurally invisible to the
    /// reaper - which is the reaper's safety argument, not a gap.
    ///
    /// <paramref name="profileRoot"/> aims the pre-flight quarantine sweep at the profile Desktop actually
    /// runs in; the service-mode caveat is documented at the sweep call below.
    /// </summary>
    internal static DesktopSession Launch(string pbixPath, string exe, int loadTimeoutSec, Action<string>? log = null,
        Jobs.JobQueue? queue = null, string? jobId = null, string? profileRoot = null)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DesktopSession drives Power BI Desktop and only runs on Windows.");
        if (!File.Exists(pbixPath)) throw new FileNotFoundException("file not found: " + pbixPath, pbixPath);

        var sink = log ?? DesktopInterop.Log;

        // Pre-flight hygiene: stale TempSaves, WebView2 locks and AutoRecovery locks left by a crashed prior
        // run are moved aside BEFORE Desktop starts, so it cannot trip over them or restore an orphaned temp
        // save into this job's document. Service-mode caveat: a Windows service (session 0) resolves ITS OWN
        // profile as systemprofile, but Desktop automation runs in the console session under the operator's
        // profile - so with no explicit profileRoot the sweep logs and no-ops in a service session rather than
        // confidently sweeping a tree Desktop never uses. A caller that knows the console session's profile
        // passes it as profileRoot.
        try
        {
            var swept = Jobs.Quarantine.SweepReal(jobId ?? Jobs.JobId.New(), profileRoot, sink);
            int moved = swept.Count(a => a.Outcome == "moved");
            if (moved > 0) sink($"  pre-flight quarantine moved {moved} stale artifact(s) aside");
        }
        catch (Exception ex)
        {
            // Hygiene never costs a launch: a lock this could not move fails the job loudly later instead.
            sink($"  pre-flight quarantine failed ({ex.GetType().Name}); launching anyway");
        }

        // no CreateNoWindow (Desktop must be windowed for Ctrl+S) and no stream redirection (nothing reads them,
        // and a full pipe buffer would wedge the process).
        var proc = Process.Start(new ProcessStartInfo(exe, $"\"{pbixPath}\"") { UseShellExecute = false })
            ?? throw new InvalidOperationException("could not start PBIDesktop.exe");
        try
        {
            var launchedStartUtc = proc.StartTime.ToUniversalTime();
            var deadlineUtc = DateTime.UtcNow.AddSeconds(loadTimeoutSec);

            var (msmdsrvPid, port) = ResolveEnginePort(proc.Id, DesktopInterop.FindChildProcessIds,
                DesktopInterop.FindListeningPort, () => DateTime.UtcNow, deadlineUtc, Thread.Sleep);
            if (port == 0)
            {
                // THE load-bearing splash-hang condition: no msmdsrv child of the launched pid was listening
                // within loadTimeoutSec. Only now may the window be consulted, and only to refine the verdict.
                throw ClassifyLaunchFailure(proc, loadTimeoutSec);
            }

            DesktopInterop.AssertPortOwnedByLaunchedPidWithRetry(port, proc.Id);
            var msmdsrvStartUtc = DesktopInterop.PidStartTimeUtc(msmdsrvPid) ?? DateTime.UtcNow;
            sink($"  engine pid {msmdsrvPid} on port {port} (Desktop pid {proc.Id})");

            // The kill-safety record: pid + start time for BOTH processes, written the moment the identities
            // are proven and before the window wait can burn the deadline. SetDesktop throwing (another live
            // job already owns this pid or port - the structural two-job collision) aborts the launch through
            // the catch below, which tears down the Desktop this call itself started, never somebody else's.
            RecordInto(queue, jobId, proc.Id, launchedStartUtc, msmdsrvPid, msmdsrvStartUtc, port);

            AwaitWindow(() => DesktopInterop.HasWindow(proc), () => WindowTitle(proc), () => DateTime.UtcNow,
                        deadlineUtc, Thread.Sleep, sink);
            return new DesktopSession(proc, pbixPath, launchedStartUtc, msmdsrvPid, msmdsrvStartUtc, port,
                deadlineUtc, sink);
        }
        catch
        {
            DesktopInterop.CleanupDesktop(proc);
            throw;
        }
    }

    /// <summary>The recording half of the kill-safety architecture: writes the launched pids (with the start
    /// times that make them identities) into the queue so the reaper can ever see them. A caller with no queue
    /// context records nothing, deliberately.</summary>
    internal static void RecordInto(Jobs.JobQueue? queue, string? jobId, int desktopPid, DateTime desktopStartUtc,
                                    int msmdsrvPid, DateTime msmdsrvStartUtc, int port)
    {
        if (queue is null || string.IsNullOrEmpty(jobId)) return;
        queue.SetDesktop(jobId, desktopPid, desktopStartUtc, msmdsrvPid, msmdsrvStartUtc, port);
    }

    /// <summary>
    /// Builds the splash-hang verdict once the load-bearing condition is already established (no msmdsrv child
    /// was listening within the deadline). The window is corroborating evidence only: it picks WHICH verdict,
    /// it never creates one.
    /// </summary>
    private static SplashHangException ClassifyLaunchFailure(Process proc, int loadTimeoutSec)
    {
        bool window = DesktopInterop.HasWindow(proc);
        string title = WindowTitle(proc);
        return ClassifyEngineAbsence(window, title) switch
        {
            SplashHangKind.NoWindow => new SplashHangException(SplashHangKind.NoWindow, proc.Id,
                $"no msmdsrv child of pid {proc.Id} was listening within {loadTimeoutSec}s and it never showed " +
                "a window - Desktop is stuck on the splash"),
            SplashHangKind.TitleStuckOpening => new SplashHangException(SplashHangKind.TitleStuckOpening, proc.Id,
                $"no msmdsrv child of pid {proc.Id} was listening within {loadTimeoutSec}s and it is still " +
                $"titled '{title}' - the document never finished opening"),
            _ => new SplashHangException(SplashHangKind.NoEnginePort, proc.Id,
                $"no msmdsrv child of pid {proc.Id} was listening within {loadTimeoutSec}s"),
        };
    }

    /// <summary>Refines an established engine-absence failure into its splash-hang kind. Pure, so the
    /// classification rules are provable offline.</summary>
    internal static SplashHangKind ClassifyEngineAbsence(bool hasWindow, string title)
    {
        if (!hasWindow) return SplashHangKind.NoWindow;
        return TitleIsSplashOpening(title) ? SplashHangKind.TitleStuckOpening : SplashHangKind.NoEnginePort;
    }

    /// <summary>
    /// True only for the splash text itself: "Opening" exactly, or "Opening " as an EXACT prefix (Desktop's
    /// splash titles the document "Opening &lt;file&gt;"). Deliberately not a substring test - a healthy
    /// document titled "Store Openings FY27 - Power BI Desktop" must never read as still-opening, because that
    /// reading once killed the Desktop. A document actually NAMED "Opening ..." can still match, which is why
    /// this is only ever corroborating evidence behind the no-msmdsrv condition, never a conviction on its own.
    /// </summary>
    internal static bool TitleIsSplashOpening(string title)
        => title == OpeningTitle || title.StartsWith(OpeningTitle + " ", StringComparison.Ordinal);

    /// <summary>
    /// The window watch. This only runs once the engine port is proven up, so nothing here may classify a
    /// splash-hang: msmdsrv-child absence is the load-bearing condition, and a title is only ever corroborating
    /// evidence for that already-established failure. At the deadline this logs and returns rather than
    /// throwing - the engine is alive, the model wait and the save step carry their own proofs, and the old
    /// deadline-throw here is what fed healthy Desktops to CleanupDesktop over a filename.
    /// </summary>
    /// <summary>Longest the window watch may corroborate before yielding. The engine is already proven up
    /// when it runs, so a splash-looking title past this point is a document NAMED "Opening ..." - waiting
    /// out the full launch deadline on it would hand AwaitModel an exhausted budget and fail a healthy file.</summary>
    internal static readonly TimeSpan WindowCorroborationCap = TimeSpan.FromSeconds(30);

    internal static void AwaitWindow(Func<bool> hasWindow, Func<string> title, Func<DateTime> nowUtc,
                                     DateTime deadlineUtc, Action<int> sleep, Action<string> log)
    {
        DateTime cap = nowUtc() + WindowCorroborationCap;
        if (deadlineUtc < cap) cap = deadlineUtc;
        while (true)
        {
            if (hasWindow() && !TitleIsSplashOpening(title())) return;
            if (nowUtc() >= cap) break;
            sleep(1000);
        }
        log("  window/title still looks like the splash, but the engine is up - proceeding");
    }

    private static string WindowTitle(Process p)
    {
        if (!OperatingSystem.IsWindows()) return "";
        try { p.Refresh(); return p.MainWindowTitle; } catch { return ""; }
    }

    /// <summary>Connect TOM to this session's own port. Per-attempt failures are swallowed: the engine accepts
    /// sockets before it accepts sessions, and a total failure surfaces as "model never attached".</summary>
    internal TOM.Server Connect(int attempts = 40, int delayMs = 1500)
    {
        // once per bind, deliberately outside the retry loop below: the assert is O(engines x tcp-table).
        DesktopInterop.AssertPortOwnedByLaunchedPid(Port, LaunchedPid);

        var srv = new TOM.Server();
        for (int i = 0; i < attempts; i++)
        {
            try { srv.Connect($"Data Source=localhost:{Port}"); break; }
            catch { Thread.Sleep(delayMs); }
        }
        _srv = srv;
        return srv;
    }

    /// <summary>The model wait always gets a real budget: callers hand it whatever is left of the shared
    /// launch deadline, and an upstream wait (a slow window, a title that reads as the splash) can hand it
    /// nothing - which used to fail a healthy Desktop with "model never attached" before a single probe.</summary>
    internal static DateTime EffectiveModelDeadline(DateTime deadlineUtc, Func<DateTime> nowUtc)
    {
        DateTime floor = nowUtc().AddSeconds(60);
        return deadlineUtc > floor ? deadlineUtc : floor;
    }

    /// <summary>Wait for the model to attach - the engine is reachable well before the database exists.</summary>
    internal TOM.Database AwaitModel(TOM.Server srv, DateTime deadlineUtc)
    {
        DateTime effective = EffectiveModelDeadline(deadlineUtc, () => DateTime.UtcNow);
        TOM.Database? db = null;
        do
        {
            try { srv.Refresh(); } catch { }
            if (srv.Databases.Count > 0 && srv.Databases[0].Model is not null) db = srv.Databases[0];
            else Thread.Sleep(1500);
        }
        while (DateTime.UtcNow < effective && db is null);
        return db ?? throw new TimeoutException("model never attached");
    }

    /// <summary>Release the engine WITHOUT closing Desktop - the save has to happen while it is still alive.</summary>
    internal void DisconnectEngine()
    {
        _srv?.Disconnect();
        _srv = null;
    }

    /// <summary>Dispatch File>Save into this session's own Desktop. True means the .pbix mtime advanced.</summary>
    internal bool SaveDispatch(int saveRetries) => DesktopInterop.SaveViaCtrlS(Process, PbixPath, saveRetries);

    internal void CloseDesktop()
    {
        // CleanupDesktop disposes the Process, so a second pass (a caller's finally plus Dispose) must not run.
        if (_closed) return;
        _closed = true;
        DesktopInterop.CleanupDesktop(Process);
    }

    public void Dispose()
    {
        try { DisconnectEngine(); } catch { }   // Dispose may not throw; the teardown below still has to run
        CloseDesktop();
    }

    /// <summary>
    /// Resolve the engine port strictly by descent from <paramref name="launchedPid"/>, never by asking the box
    /// which engine is on which port. Returns (0, 0) at the deadline rather than falling back to another
    /// Desktop's live engine. Every dependency is injected so a multi-Desktop box can be simulated offline.
    /// </summary>
    internal static (int msmdsrvPid, int port) ResolveEnginePort(
        int launchedPid,
        Func<int, string, IEnumerable<int>> childrenOf,
        Func<int, int> portOfPid,
        Func<DateTime> nowUtc, DateTime deadlineUtc, Action<int> sleep)
    {
        while (nowUtc() < deadlineUtc)
        {
            sleep(1500);
            foreach (int child in childrenOf(launchedPid, "msmdsrv.exe"))
            {
                int p = portOfPid(child);
                if (p > 0) return (child, p);   // returning here disposes the iterator, so its snapshot handle closes
            }
        }
        return (0, 0);
    }
}
