using System.Text.Json.Nodes;
using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The health "host" block's shape contract, over <see cref="HealthProbe.Snapshot(HostSnapshot, JobQueue?, DateTimeOffset)"/>.
/// An operator watchdog polls the health snapshot and parses today's shape, so the graft must be ADDITIVE: the
/// pre-graft keys (ok, service, auth, ai) may never be shadowed and the host block may never shrink on a
/// degraded box - a shorter payload on the sick box is exactly when the watchdog needs the shape most. A
/// host may expose the snapshot publicly, so the block must also carry numbers only: never a tenant,
/// a job id, a path or key material. Both halves are provable offline because Snapshot is a pure projection
/// over an injected reading and an injected queue.
/// </summary>
[Collection(JobsRootCollection.Name)]
public sealed class HealthProbeShapeTests : IDisposable
{
    private static readonly string[] HostKeys =
        { "ramFreeMB", "ramTotalMB", "cFreeGB", "dFreeGB", "queue", "floors", "admitting" };

    // the floors resolve from the environment; the defaults must stand for these facts to be deterministic.
    private static readonly string[] FloorVars =
        { "SUPERBI_RAM_FLOOR_MB", "SUPERBI_DISK_FLOOR_GB", "SUPERBI_HEAVY_RESERVE_MB", "SUPERBI_HEAVY_RESERVE_CAP_MB" };

    private readonly string? _savedRoot;
    private readonly Func<JobQueue?>? _savedQueueProvider;
    private readonly Dictionary<string, string?> _savedEnv;
    private readonly string _root;
    private readonly JobQueue _q;

    // scratch root: SUPERBI_TEST_SCRATCH override (e.g. to keep scratch off the system drive), temp fallback.
    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        Directory.CreateDirectory(root);
        return root;
    }

    public HealthProbeShapeTests()
    {
        _savedRoot = JobPaths.RootForTest;
        _savedQueueProvider = HealthProbe.QueueProvider;
        _savedEnv = FloorVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        foreach (string v in FloorVars) Environment.SetEnvironmentVariable(v, null);

        _root = Path.Combine(NewScratch(), "superbi-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        JobPaths.RootForTest = _root;
        _q = JobQueue.Open(JobPaths.QueueDbPath);
    }

    public void Dispose()
    {
        _q.Dispose();
        JobPaths.RootForTest = _savedRoot;
        HealthProbe.QueueProvider = _savedQueueProvider;
        foreach (var (name, value) in _savedEnv) Environment.SetEnvironmentVariable(name, value);
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static HostSnapshot Host(long ramFreeMB, long ramTotalMB, double jobRootFreeGB, double tempFreeGB) =>
        new(ramFreeMB, ramTotalMB, jobRootFreeGB, tempFreeGB, DateTimeOffset.UtcNow);

    private static string[] Keys(JsonObject o) => o.Select(kv => kv.Key).ToArray();

    // ---------------- the documented field set ----------------

    [Fact]
    public void Snapshot_CarriesEveryDocumentedField_InTheDocumentedOrder()
    {
        // minted in enqueue order: the admission tie-break on job_id must agree with created_utc.
        string running = JobId.New(), queued = JobId.New();
        _q.Enqueue(new JobSubmission(running, "acme", Lane.Heavy, "/ingest", 4096, 1, JobPaths.JobDir(running), null));
        _q.Enqueue(new JobSubmission(queued, "acme", Lane.Heavy, "/ingest", 4096, 1, JobPaths.JobDir(queued), null));
        Assert.True(_q.TryAdmit(running, laneLimit: 1, ownerPid: 4242, hostAllows: _ => true));

        JsonObject payload = HealthProbe.Snapshot(Host(32768, 65536, 120.5, 80.0), _q, DateTimeOffset.UtcNow);

        Assert.Equal(HostKeys, Keys(payload));
        Assert.Equal(32768, payload["ramFreeMB"]!.GetValue<long>());
        Assert.Equal(65536, payload["ramTotalMB"]!.GetValue<long>());
        Assert.Equal(120.5, payload["cFreeGB"]!.GetValue<double>());
        Assert.Equal(80.0, payload["dFreeGB"]!.GetValue<double>());
        Assert.True(payload["admitting"]!.GetValue<bool>());

        var queue = Assert.IsType<JsonObject>(payload["queue"]);
        Assert.Equal(new[] { "cheapQueued", "heavyQueued", "cheapActive", "heavyActive", "oldestRunningSec" }, Keys(queue));
        Assert.Equal(0, queue["cheapQueued"]!.GetValue<int>());
        Assert.Equal(1, queue["heavyQueued"]!.GetValue<int>());
        Assert.Equal(0, queue["cheapActive"]!.GetValue<int>());
        Assert.Equal(1, queue["heavyActive"]!.GetValue<int>());
        Assert.True(queue["oldestRunningSec"]!.GetValue<long>() >= 0);

        var floors = Assert.IsType<JsonObject>(payload["floors"]);
        Assert.Equal(new[] { "ramFloorMB", "cFloorGB", "dFloorGB" }, Keys(floors));
        Assert.Equal(AdmissionPolicy.RamFloorMB, floors["ramFloorMB"]!.GetValue<long>());
        Assert.Equal(AdmissionPolicy.DiskFloorGB, floors["cFloorGB"]!.GetValue<double>());
        Assert.Equal(AdmissionPolicy.DiskFloorGB, floors["dFloorGB"]!.GetValue<double>());
    }

    // ---------------- additive over the pre-graft /health shape ----------------

    [Fact]
    public void Snapshot_NeverCollidesWithThePreGraftHealthKeys_AndAMissingQueueDegradesLoudly()
    {
        // the pre-graft health payload an operator watchdog parses: { ok, service, auth, ai }. The host
        // block is appended LAST as one nested object, so none of its keys may echo a pre-graft name.
        JsonObject payload = HealthProbe.Snapshot(Host(32768, 65536, 120.5, 80.0), queue: null, DateTimeOffset.UtcNow);

        foreach (string preGraft in new[] { "ok", "service", "auth", "ai" })
            Assert.False(payload.ContainsKey(preGraft), $"the host block must not shadow the pre-graft key '{preGraft}'");

        // UPDATED for the D7 fix (queue-open failure): no queue is the legacy dispatch fallback serving, so
        // it reports "unavailable" (never a silent null) and admitting:false EVEN ON HEALTHY FLOORS - the
        // pre-fix null queue + admitting:true kept WP monitoring green while every route failed. The key
        // set itself is constant so the watchdog's parse is too.
        Assert.Equal(HostKeys, Keys(payload));
        Assert.Equal("unavailable", payload["queue"]!.GetValue<string>());
        Assert.False(payload["admitting"]!.GetValue<bool>());
    }

    [Fact]
    public void Snapshot_WhenTheQueueProviderThrows_StillAnswersWithTheFullShape()
    {
        // HostResources.ProbeForTest is deliberately NOT set here: it is a process-global seam that
        // AdmissionPolicyTests reads through HostResources.Probe from a PARALLEL collection. The queue
        // provider is safe to fault because only this route reads it.
        HealthProbe.QueueProvider = () => throw new InvalidOperationException("queue gone");

        JsonObject payload = HealthProbe.Snapshot();   // the live entry point: it must answer, not throw

        Assert.Equal(HostKeys, Keys(payload));
        // UPDATED for the D7 fix: a throwing provider reads as "no queue", reported loudly - never a
        // thrown route, and never a silent null the watchdog would shrug at.
        Assert.Equal("unavailable", payload["queue"]!.GetValue<string>());
        Assert.False(payload["admitting"]!.GetValue<bool>());
    }

    [Fact]
    public void Snapshot_AnUnknownReading_ReportsNullPerField_NeverANumber()
    {
        var unknown = new HostSnapshot(-1, -1, -1, -1, DateTimeOffset.UtcNow);

        JsonObject payload = HealthProbe.Snapshot(unknown, queue: null, DateTimeOffset.UtcNow);

        Assert.Equal(HostKeys, Keys(payload));
        Assert.Null(payload["ramFreeMB"]);       // -1 is "unknown", and an operator must not read it as 0MB free
        Assert.Null(payload["ramTotalMB"]);
        Assert.Null(payload["cFreeGB"]);
        Assert.Null(payload["dFreeGB"]);
        Assert.False(payload["admitting"]!.GetValue<bool>());
    }

    // ---------------- a breached floor goes unhealthy AND refuses admission ----------------

    [Fact]
    public void Snapshot_ABreachedDiskFloor_FlipsAdmittingFalse_AndTheSameFloorsRefuseAdmission()
    {
        var breached = Host(32768, 65536, jobRootFreeGB: 2.0, tempFreeGB: 80.0);   // below the 10GB floor

        JsonObject payload = HealthProbe.Snapshot(breached, _q, DateTimeOffset.UtcNow);
        Assert.False(payload["admitting"]!.GetValue<bool>());

        // the same floors govern real admission: the job is refused and stays QUEUED, never half-admitted.
        string id = JobId.New();
        _q.Enqueue(new JobSubmission(id, "acme", Lane.Heavy, "/ingest", 4096, 1, JobPaths.JobDir(id), null));
        bool admitted = _q.TryAdmit(id, laneLimit: 1, ownerPid: 4242,
            hostAllows: row => AdmissionPolicy.Evaluate(breached, AdmissionPolicy.FromEnv(row.Lane, 0, 0, 1))
                == AdmissionVerdict.Admit);

        Assert.False(admitted);
        Assert.Equal(JobState.QUEUED, _q.Get(id)!.State);
        Assert.Equal(AdmissionVerdict.HoldDisk,
            AdmissionPolicy.Evaluate(breached, AdmissionPolicy.FromEnv(Lane.Heavy, 0, 0, 1)));
    }

    [Fact]
    public void Snapshot_ABreachedRamFloor_FlipsAdmittingFalse()
    {
        // 5000MB free minus the 4096MB base reserve leaves 904MB, below the 1024MB floor.
        var breached = Host(5000, 65536, jobRootFreeGB: 120.5, tempFreeGB: 80.0);

        JsonObject payload = HealthProbe.Snapshot(breached, _q, DateTimeOffset.UtcNow);

        Assert.False(payload["admitting"]!.GetValue<bool>());
        Assert.Equal(AdmissionVerdict.HoldRam,
            AdmissionPolicy.Evaluate(breached, AdmissionPolicy.FromEnv(Lane.Heavy, 0, 0, 1)));
    }

    // ---------------- the public-route bar ----------------

    [Fact]
    public void Snapshot_NeverLeaksATenantAJobIdOrAPath()
    {
        string tenant = "hyperion-secret-tenant";
        string id = JobId.New();
        _q.Enqueue(new JobSubmission(id, tenant, Lane.Heavy, "/ingest", 4096, 1, JobPaths.JobDir(id),
            Path.Combine(JobPaths.In(id), "source.pbix")));
        Assert.True(_q.TryAdmit(id, laneLimit: 1, ownerPid: 4242, hostAllows: _ => true));
        _q.SetDesktop(id, 100, DateTime.UtcNow, 101, DateTime.UtcNow, 55001);

        string json = HealthProbe.Snapshot(Host(32768, 65536, 120.5, 80.0), _q, DateTimeOffset.UtcNow).ToJsonString();

        // /health is served before the auth gate, so the whole serialized block must stay anonymous.
        Assert.DoesNotContain(tenant, json);
        Assert.DoesNotContain(id, json);
        Assert.DoesNotContain(Path.GetFileName(_root), json);
    }
}
