using System.Globalization;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Jobs;

/// <summary>
/// The `SuperBiMcp jobs` verb - the operator surface over the durable queue (queue.db under the resolved
/// job root), so a stuck box can be inspected and recovered without a debugger.
///
/// House verb shape: exactly one JSON document to stdout as the result, the human-readable rendering to
/// stderr, exit 0 ok / 1 error / 2 usage. Exit codes 2 and 3 are reserved suite-wide and
/// are never used to carry a count.
///
/// A maintenance surface by design: recovery must work at exactly the moment the operator
/// needs this verb, whatever state the box is in. The invariant
/// that keeps the open door safe: nothing here calls Cli.Build, BulkOps.RunRefresh or any other
/// build capability - this class only reads and maintains queue.db, and `reap` kills only
/// processes the queue itself recorded (pid plus start time, through OrphanReaper), so a process the queue
/// never recorded is invisible to it by construction.
/// </summary>
public static class JobsCli
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1])) return JobsUsage(null);
            return args[1].ToLowerInvariant() switch
            {
                "list" => RunList(args),
                "show" => RunShow(args),
                "requeue" => RunRequeue(args),
                "reconcile" => RunReconcile(),
                "reap" => RunReap(args),
                "health" => RunHealth(),
                _ => JobsUsage($"unknown subcommand '{args[1]}'"),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int JobsUsage(string? error)
    {
        if (!string.IsNullOrEmpty(error)) Console.Error.WriteLine(error);
        Console.Error.WriteLine("usage: SuperBiMcp jobs list  [--lane cheap|heavy] [--state QUEUED|ADMITTED|RUNNING|VERIFYING|DONE|FAILED|DEAD] [--limit N]");
        Console.Error.WriteLine("       SuperBiMcp jobs show  <jobId>");
        Console.Error.WriteLine("       SuperBiMcp jobs requeue <jobId>");
        Console.Error.WriteLine("       SuperBiMcp jobs reconcile");
        Console.Error.WriteLine("       SuperBiMcp jobs reap  [--dry-run]");
        Console.Error.WriteLine("       SuperBiMcp jobs health");
        return 2;
    }

    // ---- subcommands ---------------------------------------------------------------

    private static int RunList(string[] args)
    {
        Lane? lane = null;
        JobState? state = null;
        int limit = 50;

        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--lane":
                    if (++i >= args.Length) return JobsUsage("--lane needs a value");
                    if (string.Equals(args[i], "cheap", StringComparison.OrdinalIgnoreCase)) lane = Lane.Cheap;
                    else if (string.Equals(args[i], "heavy", StringComparison.OrdinalIgnoreCase)) lane = Lane.Heavy;
                    else return JobsUsage($"unknown lane '{args[i]}' (cheap|heavy)");
                    break;
                case "--state":
                    if (++i >= args.Length) return JobsUsage("--state needs a value");
                    state = ParseState(args[i]);
                    if (state is null) return JobsUsage($"unknown state '{args[i]}'");
                    break;
                case "--limit":
                    if (++i >= args.Length
                        || !int.TryParse(args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out limit)
                        || limit <= 0)
                        return JobsUsage("--limit needs a positive integer");
                    break;
                default:
                    return JobsUsage($"unknown option '{args[i]}'");
            }
        }

        using var q = OpenQueue();
        IReadOnlyList<JobRow> rows = q.List(lane, state, limit);

        if (rows.Count == 0)
        {
            Console.Error.WriteLine("no jobs match.");
        }
        else
        {
            Console.Error.WriteLine($"{"JOBID",-26}  {"LANE",-5}  {"STATE",-9}  {"ATT",-4}  {"CREATED (UTC)",-16}  ROUTE");
            foreach (JobRow r in rows)
            {
                string error = r.Error is null ? "" : "  ! " + Truncate(r.Error, 60);
                Console.Error.WriteLine(
                    $"{r.JobId,-26}  {LaneName(r.Lane),-5}  {r.State,-9}  {r.Attempt + "/" + r.MaxAttempts,-4}  " +
                    $"{r.CreatedUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),-16}  {r.Route}{error}");
            }
        }

        var jobs = new JsonArray();
        foreach (JobRow r in rows) jobs.Add(RowJson(r));
        return Emit(new JsonObject { ["ok"] = true, ["count"] = rows.Count, ["jobs"] = jobs });
    }

    private static int RunShow(string[] args)
    {
        if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2])) return JobsUsage("show needs a <jobId>");

        using var q = OpenQueue();
        JobRow? row = q.Get(args[2]);
        if (row is null) return Fail($"unknown job '{args[2]}'");

        Console.Error.WriteLine($"{row.JobId}  {LaneName(row.Lane)}  {row.State}  attempt {row.Attempt}/{row.MaxAttempts}  route {row.Route}");
        Console.Error.WriteLine($"  tenant {row.TenantId}  reserve {row.ReserveMB}MB  root {row.JobRoot}");
        if (row.DesktopPid is not null || row.MsmdsrvPid is not null)
            Console.Error.WriteLine($"  desktop pid {row.DesktopPid?.ToString(CultureInfo.InvariantCulture) ?? "-"}  " +
                                    $"msmdsrv pid {row.MsmdsrvPid?.ToString(CultureInfo.InvariantCulture) ?? "-"}  " +
                                    $"port {row.MsmdsrvPort?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        if (row.Error is not null) Console.Error.WriteLine($"  error: {row.Error}");

        return Emit(new JsonObject { ["ok"] = true, ["job"] = RowJson(row) });
    }

    private static int RunRequeue(string[] args)
    {
        if (args.Length < 3 || string.IsNullOrWhiteSpace(args[2])) return JobsUsage("requeue needs a <jobId>");
        string jobId = args[2];

        using var q = OpenQueue();
        JobRow? row = q.Get(jobId);
        if (row is null) return Fail($"unknown job '{jobId}'");
        if (row.State is not (JobState.FAILED or JobState.DEAD))
            return Fail($"job '{jobId}' is {row.State}; only a FAILED or DEAD row can be requeued");

        // A row that still owns a live recorded process must be reaped first: requeueing it would let a new
        // attempt run beside the old one's Desktop, and two Desktops fight for the foreground.
        if (row.DesktopPid is int dp && row.DesktopStartUtc is DateTime ds && DesktopInterop.PidAlive(dp, ds))
            return Fail($"job '{jobId}' still records a live Desktop (pid {dp}) - run `jobs reap` first");
        if (row.MsmdsrvPid is int mp && row.MsmdsrvStartUtc is DateTime ms && DesktopInterop.PidAlive(mp, ms))
            return Fail($"job '{jobId}' still records a live msmdsrv (pid {mp}) - run `jobs reap` first");

        // Requeue (not TryTransition): created_utc and heartbeat_utc are reset to now, so the row joins the
        // BACK of its lane. Requeueing with the original timestamp made the old row the immortal head of its
        // lane - nothing could ever admit it, and nothing behind it could be admitted past it.
        if (!q.Requeue(jobId, row.State))
            return Fail($"job '{jobId}' changed state under us - re-run `jobs show {jobId}`");

        const string note =
            "requeue only re-queues: the engine executes a QUEUED row when a client resubmits the same jobId " +
            "(today only /ingest 'src-' ids are client-resubmittable; other routes mint a fresh id per request). " +
            "An unadopted requeue turns heartbeat-stale after 60s (invisible to admission) and is swept DEAD " +
            "as 'admission waiter lost' once its 120s creation grace has passed and the next sweep runs.";
        Console.Error.WriteLine($"requeued {jobId} ({row.State} -> QUEUED, at the back of its lane)");
        Console.Error.WriteLine("NOTE: " + note);
        return Emit(new JsonObject
        {
            ["ok"] = true,
            ["jobId"] = jobId,
            ["from"] = row.State.ToString(),
            ["state"] = JobState.QUEUED.ToString(),
            ["requeuedToBack"] = true,
            ["note"] = note,
        });
    }

    private static int RunReconcile()
    {
        using var q = OpenQueue();
        // Owner identity is pid + recorded start time (PidAlive's two-arg overload), never a bare pid. No
        // incarnation is passed: the CLI is not the engine, and QUEUED rows the LIVE service's waiters are
        // polling must not be killed by an operator inspection verb. Stranded QUEUED rows are retired by the
        // engine's own startup reconcile and its waiter-liveness sweep instead.
        int lost = q.Reconcile(Environment.ProcessId, DesktopInterop.PidAlive, DateTimeOffset.UtcNow);
        Console.Error.WriteLine(lost == 0
            ? "reconcile: every in-flight row has a live, verified owner"
            : $"reconcile: marked {lost} ownerless in-flight job(s) DEAD");
        return Emit(new JsonObject { ["ok"] = true, ["reconciled"] = lost });
    }

    private static int RunReap(string[] args)
    {
        bool dryRun = false;
        for (int i = 2; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--dry-run", StringComparison.OrdinalIgnoreCase)) dryRun = true;
            else return JobsUsage($"unknown option '{args[i]}'");
        }

        using var q = OpenQueue();

        if (dryRun)
        {
            IReadOnlyList<Victim> victims = OrphanReaper.Plan(
                q.RecordedProcesses(), DesktopInterop.PidStartTimeUtc, DateTimeOffset.UtcNow, ReapMinAge());

            if (victims.Count == 0) Console.Error.WriteLine("nothing to reap.");
            var arr = new JsonArray();
            foreach (Victim v in victims)
            {
                Console.Error.WriteLine($"would kill {v.Name} pid {v.Pid} of job {v.JobId} ({v.Reason})");
                arr.Add(new JsonObject
                {
                    ["jobId"] = v.JobId,
                    ["pid"] = v.Pid,
                    ["name"] = v.Name,
                    ["reason"] = v.Reason,
                });
            }
            return Emit(new JsonObject { ["ok"] = true, ["dryRun"] = true, ["victims"] = arr });
        }

        int killed = OrphanReaper.Reap(q, m => Console.Error.WriteLine("[jobs] " + m), DateTimeOffset.UtcNow);
        if (killed == 0) Console.Error.WriteLine("nothing to reap.");
        return Emit(new JsonObject { ["ok"] = true, ["killed"] = killed });
    }

    private static int RunHealth()
    {
        // Health must keep answering on a sick box, so a queue that cannot open degrades the reading
        // instead of failing the command: the host block still reports RAM, disk and the admit verdict.
        JobQueue? q = null;
        string? queueError = null;
        try { q = OpenQueue(); }
        catch (Exception ex) { queueError = ex.Message; }

        try
        {
            JsonObject host = HealthProbe.Snapshot(HostResources.Probe(JobPaths.Root), q, DateTimeOffset.UtcNow);

            Console.Error.WriteLine(Runtime.StartupBanner());
            Console.Error.WriteLine(q is null
                ? "queue: NOT OPEN - " + queueError
                : $"queue: cheap {q.QueuedCount(Lane.Cheap)} queued / {q.ActiveCount(Lane.Cheap)} active, " +
                  $"heavy {q.QueuedCount(Lane.Heavy)} queued / {q.ActiveCount(Lane.Heavy)} active");
            Console.Error.WriteLine($"admitting: {(host["admitting"]?.GetValue<bool>() == true ? "yes" : "no")}");

            return Emit(new JsonObject
            {
                ["ok"] = true,
                ["root"] = JobPaths.Root,
                ["queueDb"] = JobPaths.QueueDbPath,
                ["queueOpen"] = q is not null,
                ["queueError"] = queueError,
                ["lanes"] = new JsonObject
                {
                    ["cheapLimit"] = Runtime.CheapLimit,
                    ["heavyLimit"] = Runtime.HeavyLimit,
                },
                ["host"] = host,
            });
        }
        finally { q?.Dispose(); }
    }

    // ---- helpers -------------------------------------------------------------------

    /// <summary>Every subcommand opens its own connection: the runtime is never started here, and queue.db
    /// is built for exactly this cross-process interleaving (WAL + busy_timeout on every connection).</summary>
    private static JobQueue OpenQueue() => JobQueue.Open(JobPaths.QueueDbPath);

    /// <summary>The one JSON document this invocation writes to stdout. Spelled Console.Out to mark it as
    /// the sole deliberate stdout write in this namespace - everything else here goes to stderr, because in
    /// MCP mode stdout is the JSON-RPC channel.</summary>
    private static int Emit(JsonObject payload)
    {
        Console.Out.WriteLine(payload.ToJsonString(Cli.Pretty));
        return 0;
    }

    /// <summary>An expected domain error: still one JSON document, still a stderr line, exit 1.</summary>
    private static int Fail(string error)
    {
        Console.Error.WriteLine(error);
        Console.Out.WriteLine(new JsonObject { ["ok"] = false, ["error"] = error }.ToJsonString(Cli.Pretty));
        return 1;
    }

    /// <summary>Name-only parse: Enum.TryParse would also accept a bare integer, which is not a state.</summary>
    private static JobState? ParseState(string s)
    {
        foreach (string name in Enum.GetNames<JobState>())
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase))
                return Enum.Parse<JobState>(name);
        return null;
    }

    private static string LaneName(Lane lane) => lane == Lane.Heavy ? "heavy" : "cheap";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 3)] + "...";

    /// <summary>The reaper's age floor, resolved the same way OrphanReaper resolves it when planning for
    /// real, so a --dry-run previews the pass that would actually run.</summary>
    private static TimeSpan ReapMinAge() =>
        int.TryParse(Environment.GetEnvironmentVariable("SUPERBI_REAP_MIN_AGE_SEC"), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int sec) && sec >= 0
            ? TimeSpan.FromSeconds(sec)
            : OrphanReaper.DefaultMinAge;

    /// <summary>Internal (not private) as the test seam for the row JSON shape: additive fields like
    /// `priority` are pinned by a test without driving the whole verb through the process-global paths.</summary>
    internal static JsonObject RowJson(JobRow r) => new()
    {
        ["jobId"] = r.JobId,
        ["tenantId"] = r.TenantId,
        ["lane"] = LaneName(r.Lane),
        ["route"] = r.Route,
        ["state"] = r.State.ToString(),
        ["attempt"] = r.Attempt,
        ["maxAttempts"] = r.MaxAttempts,
        ["reserveMB"] = r.ReserveMB,
        ["priority"] = r.Priority,
        ["ownerPid"] = r.OwnerPid,
        ["ownerStartUtc"] = Iso(r.OwnerStartUtc),
        ["desktopPid"] = r.DesktopPid,
        ["desktopStartUtc"] = Iso(r.DesktopStartUtc),
        ["msmdsrvPid"] = r.MsmdsrvPid,
        ["msmdsrvStartUtc"] = Iso(r.MsmdsrvStartUtc),
        ["msmdsrvPort"] = r.MsmdsrvPort,
        ["jobRoot"] = r.JobRoot,
        ["sourcePath"] = r.SourcePath,
        ["workPath"] = r.WorkPath,
        ["retainedPath"] = r.RetainedPath,
        ["artifactSha256"] = r.ArtifactSha256,
        ["artifactBytes"] = r.ArtifactBytes,
        ["error"] = r.Error,
        ["createdUtc"] = Iso(r.CreatedUtc),
        ["admittedUtc"] = Iso(r.AdmittedUtc),
        ["startedUtc"] = Iso(r.StartedUtc),
        ["heartbeatUtc"] = Iso(r.HeartbeatUtc),
        ["finishedUtc"] = Iso(r.FinishedUtc),
        ["incarnation"] = r.Incarnation,
    };

    private static string? Iso(DateTimeOffset? v) =>
        v?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string? Iso(DateTime? v) =>
        v?.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
}
