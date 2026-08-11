using System.Globalization;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Jobs;

/// <summary>
/// The health "host" block: what the box has left, and whether it can still take work.
///
/// A health snapshot may be exposed publicly by a host, so this honours the same bar the ai block's
/// class doc states - coarse host capacity only. Numbers, never a path, never a job id, never a tenant, never
/// key material, never spend. The volume readings are anonymous by construction: the snapshot carries the job
/// root and temp volumes in a fixed order and this emits their free space, never their roots.
///
/// A reading that could not be taken emits null, and a null reading is never read as healthy: with no reading
/// there is no evidence of headroom, which is the rule admission itself applies to an unknown RAM figure.
///
/// Seams:
///   QueueProvider - supplies the open queue, set by the job subsystem once it has one. Unset, null or
///                   throwing reports queue "unavailable" and admitting false - a box whose queue could not
///                   open is serving the legacy dispatch fallback and must degrade LOUDLY, never read as
///                   healthy; the host readings still stand.
/// </summary>
internal static class HealthProbe
{
    /// <summary>Supplies the open queue. Null (the default) means this process has no queue to report on.</summary>
    internal static Func<JobQueue?>? QueueProvider { get; set; }

    /// <summary>
    /// The live reading. Never throws: this answers a route that has to keep answering on a sick box, and a
    /// health probe that throws turns a degraded engine into a dead one.
    /// </summary>
    internal static JsonObject Snapshot()
    {
        try
        {
            JobQueue? queue;
            try { queue = QueueProvider?.Invoke(); }
            catch { queue = null; }   // a provider that throws must not take the whole reading down with it

            return Snapshot(HostResources.Probe(), queue, DateTimeOffset.UtcNow);
        }
        catch
        {
            return Build(null, null, null, null, JsonValue.Create("unavailable"), admitting: false);
        }
    }

    /// <summary>
    /// { ramFreeMB, ramTotalMB, cFreeGB, dFreeGB, queue:{cheapQueued,heavyQueued,cheapActive,heavyActive,
    /// oldestRunningSec}, floors:{ramFloorMB,cFloorGB,dFloorGB}, admitting }. A pure projection over the
    /// reading and the queue, so the whole payload is provable with no host access at all. No queue at all
    /// (it failed to open - the engine is on the legacy dispatch fallback) reports queue "unavailable" AND
    /// admitting false, whatever the floors say: a box that cannot durably queue is not admitting queue
    /// work, and the watchdog must see that. A queue that merely failed one READ keeps the floors' verdict -
    /// a transient lock must not flap the operator alert.
    /// </summary>
    internal static JsonObject Snapshot(HostSnapshot host, JobQueue? queue, DateTimeOffset now) =>
        Build(Mb(host.RamFreeMB), Mb(host.RamTotalMB), Gb(host.CFreeGB), Gb(host.DFreeGB),
              QueueBlock(queue, now), queue is not null && Admitting(host));

    /// <summary>The key set, in one place: a degraded reading reports the same shape, not a shorter one.</summary>
    private static JsonObject Build(JsonNode? ramFreeMB, JsonNode? ramTotalMB, JsonNode? cFreeGB,
                                    JsonNode? dFreeGB, JsonNode? queue, bool admitting) =>
        new()
        {
            ["ramFreeMB"] = ramFreeMB,
            ["ramTotalMB"] = ramTotalMB,
            ["cFreeGB"] = cFreeGB,
            ["dFreeGB"] = dFreeGB,
            ["queue"] = queue,
            ["floors"] = Floors(),
            ["admitting"] = admitting,
        };

    /// <summary>
    /// Whether the box would take a new job at all. The floors are the whole question, so the policy is asked
    /// with a lane that cannot be full and the reserve a heavy job starts from: an Admit means no floor is
    /// breached. It is a statement about the host, never a promise about any particular job - a real admission
    /// re-decides with that job's reserve against the lane's real occupancy.
    /// </summary>
    private static bool Admitting(in HostSnapshot host) =>
        AdmissionPolicy.Evaluate(host, AdmissionPolicy.FromEnv(Lane.Heavy, 0, 0, int.MaxValue))
            == AdmissionVerdict.Admit;

    /// <summary>Queue depth and the oldest in-flight job's age; the string "unavailable" when there is no
    /// queue to read (it failed to open - the legacy dispatch fallback is serving) or the read itself
    /// failed. Never a fabricated depth, and never a silent null: the watchdog must be able to tell "no
    /// queue" from "healthy but idle".</summary>
    private static JsonNode QueueBlock(JobQueue? queue, DateTimeOffset now)
    {
        if (queue is null) return JsonValue.Create("unavailable");
        try
        {
            return new JsonObject
            {
                ["cheapQueued"] = queue.QueuedCount(Lane.Cheap),
                ["heavyQueued"] = queue.QueuedCount(Lane.Heavy),
                ["cheapActive"] = queue.ActiveCount(Lane.Cheap),
                ["heavyActive"] = queue.ActiveCount(Lane.Heavy),
                ["oldestRunningSec"] = Sec(queue.OldestRunningAge(now)),
            };
        }
        // closed, locked or disposed: nothing to report, never a fabricated depth
        catch { return JsonValue.Create("unavailable"); }
    }

    /// <summary>
    /// The floors the verdict was decided against, resolved exactly as AdmissionPolicy resolves them so the
    /// floors reported are the floors applied. ONE disk floor governs every measured volume: cFloorGB and
    /// dFloorGB are that floor read against the two readings above them, not two separate settings.
    /// </summary>
    private static JsonObject Floors()
    {
        double disk = EnvDouble("SUPERBI_DISK_FLOOR_GB", AdmissionPolicy.DiskFloorGB);
        return new JsonObject
        {
            ["ramFloorMB"] = EnvLong("SUPERBI_RAM_FLOOR_MB", AdmissionPolicy.RamFloorMB),
            ["cFloorGB"] = disk,
            ["dFloorGB"] = disk,
        };
    }

    // A negative reading is "unknown", never "full" or "empty": it reports as null rather than as a number an
    // operator would read as a measurement.
    private static JsonNode? Mb(long mb) => mb < 0 ? null : JsonValue.Create(mb);

    private static JsonNode? Gb(double freeGB) => freeGB < 0 ? null : JsonValue.Create(freeGB);

    private static JsonNode? Sec(TimeSpan? age) =>
        age is null ? null : JsonValue.Create((long)Math.Round(age.Value.TotalSeconds));

    // AdmissionPolicy keeps its own env resolution private; these read the same names the same way, and a
    // value that does not parse, or is negative, leaves the default standing.
    private static long EnvLong(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;

    private static double EnvDouble(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float,
            CultureInfo.InvariantCulture, out var v) && v >= 0 ? v : fallback;
}
