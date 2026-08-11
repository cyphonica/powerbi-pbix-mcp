using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// THE phantom-connect / collision gate. Before Phase 0 a second job could bind to the FIRST job's
/// engine port (ModelService.Connect(null) -> PortDiscovery.Discover()[0], ordered by a port FILE's mtime with
/// no liveness and no PID binding) and Ctrl+S the FIRST job's Desktop. This drives the real
/// <see cref="JobQueue.TryAdmit"/> against a real queue.db and the real
/// <see cref="DesktopSession.ResolveEnginePort"/> with a simulated two-Desktop box - no live Power BI required.
/// </summary>
[Collection(JobsRootCollection.Name)]
public sealed class HeavyLaneSoakTests : IDisposable
{
    private readonly string? _savedRoot;
    private readonly string _root;
    private readonly JobQueue _q;

    // scratch root: SUPERBI_TEST_SCRATCH override (e.g. to keep scratch off the system drive), temp fallback.
    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        Directory.CreateDirectory(root);
        return root;
    }

    public HeavyLaneSoakTests()
    {
        _savedRoot = JobPaths.RootForTest;
        _root = Path.Combine(NewScratch(), "superbi-soak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        JobPaths.RootForTest = _root;
        _q = JobQueue.Open(JobPaths.QueueDbPath);
    }

    public void Dispose()
    {
        _q.Dispose();
        JobPaths.RootForTest = _savedRoot;
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // the simulated box: Desktop 100 -> msmdsrv 101 -> port 55001 ; Desktop 200 -> msmdsrv 201 -> port 55002
    private static readonly Dictionary<int, int[]> Children = new() { [100] = new[] { 101 }, [200] = new[] { 201 } };
    private static readonly Dictionary<int, int> PortOf = new() { [101] = 55001, [201] = 55002 };
    private static readonly Dictionary<int, int> PidOnPort = new() { [55001] = 101, [55002] = 201 };
    private static readonly Dictionary<int, int> ParentOf = new() { [101] = 100, [201] = 200 };

    [Fact]
    public void TwoHeavyJobs_AreSerialised_AndEachBindsToItsOwnEnginePort()
    {
        string a = JobId.New(), b = JobId.New();
        _q.Enqueue(new JobSubmission(a, "acme", Lane.Heavy, "/ingest", 4096, 1, JobPaths.JobDir(a), null));
        _q.Enqueue(new JobSubmission(b, "acme", Lane.Heavy, "/ingest", 4096, 1, JobPaths.JobDir(b), null));

        // ---- only ONE heavy job is ever admitted, no matter how much RAM is free ----
        Assert.True(_q.TryAdmit(a, laneLimit: 1, ownerPid: 4242, hostAllows: _ => true));
        Assert.False(_q.TryAdmit(b, laneLimit: 1, ownerPid: 4242, hostAllows: _ => true));   // stacked Desktops OOM'd at ~86GB
        Assert.Equal(1, _q.QueuePosition(b));

        // ---- job A launches its OWN Desktop and resolves its port strictly by descent from its launched PID ----
        var (aEngine, aPort) = DesktopSession.ResolveEnginePort(
            launchedPid: 100, childrenOf: (pid, exe) => Children.TryGetValue(pid, out var c) ? c : Array.Empty<int>(),
            portOfPid: pid => PortOf.TryGetValue(pid, out var p) ? p : 0,
            nowUtc: () => DateTime.UtcNow, deadlineUtc: DateTime.UtcNow.AddSeconds(30), sleep: _ => { });
        Assert.Equal(101, aEngine);
        Assert.Equal(55001, aPort);

        Assert.True(_q.TryTransition(a, JobState.ADMITTED, JobState.RUNNING, 4242));
        _q.SetDesktop(a, 100, DateTime.UtcNow, 101, DateTime.UtcNow, 55001);
        Assert.False(_q.TryAdmit(b, laneLimit: 1, ownerPid: 4242, hostAllows: _ => true));   // still held while A RUNS

        // ---- A finishes; B is admitted and binds to ITS OWN port, NOT A's ----
        Assert.True(_q.TryTransition(a, JobState.RUNNING, JobState.DONE, 4242));
        Assert.True(_q.TryAdmit(b, laneLimit: 1, ownerPid: 4242, hostAllows: _ => true));

        var (bEngine, bPort) = DesktopSession.ResolveEnginePort(
            launchedPid: 200, childrenOf: (pid, exe) => Children.TryGetValue(pid, out var c) ? c : Array.Empty<int>(),
            portOfPid: pid => PortOf.TryGetValue(pid, out var p) ? p : 0,
            nowUtc: () => DateTime.UtcNow, deadlineUtc: DateTime.UtcNow.AddSeconds(30), sleep: _ => { });

        Assert.Equal(201, bEngine);
        Assert.Equal(55002, bPort);                 // THE assertion: B never inherits A's 55001
        Assert.NotEqual(aPort, bPort);

        Assert.True(_q.TryTransition(b, JobState.ADMITTED, JobState.RUNNING, 4242));
        _q.SetDesktop(b, 200, DateTime.UtcNow, 201, DateTime.UtcNow, 55002);

        Assert.Equal(55001, _q.Get(a)!.MsmdsrvPort);
        Assert.Equal(55002, _q.Get(b)!.MsmdsrvPort);
    }

    [SkippableFact]
    public void JobB_CannotBindToJobAsPort_TheOwnershipAssertRefusesIt()
    {
        Skip.If(!OperatingSystem.IsWindows(), "the ownership assert has nothing to assert off Windows.");

        // the exact phantom-connect: B (launched pid 200) is handed A's port 55001.
        Assert.Throws<PortOwnershipException>(() =>
            DesktopInterop.AssertPortOwnedByLaunchedPid(55001, launchedPid: 200,
                msmdsrvPidOnPort: p => PidOnPort.TryGetValue(p, out var e) ? e : 0,
                parentOf: pid => ParentOf.TryGetValue(pid, out var par) ? par : 0));

        // and B binding to its OWN port is accepted: the owner chain roots at B's launched pid
        DesktopInterop.AssertPortOwnedByLaunchedPid(55002, launchedPid: 200,
            msmdsrvPidOnPort: p => PidOnPort.TryGetValue(p, out var e) ? e : 0,
            parentOf: pid => ParentOf.TryGetValue(pid, out var par) ? par : 0);
    }

    [Fact]
    public void ResolveEnginePort_NeverReturnsAnotherDesktopsPort_EvenWhenThatOneIsNewerAndLive()
    {
        // a stale-but-live 55001 exists and its port FILE is newer; descent from 200 must still land on 55002.
        var (_, port) = DesktopSession.ResolveEnginePort(
            launchedPid: 200, childrenOf: (pid, exe) => Children.TryGetValue(pid, out var c) ? c : Array.Empty<int>(),
            portOfPid: pid => PortOf.TryGetValue(pid, out var p) ? p : 0,
            nowUtc: () => DateTime.UtcNow, deadlineUtc: DateTime.UtcNow.AddSeconds(5), sleep: _ => { });
        Assert.Equal(55002, port);
    }

    [Fact]
    public void ResolveEnginePort_ReturnsZero_RatherThanGuessing_WhenNoChildEngineAppears()
    {
        var t = DateTime.UtcNow;
        var (engine, port) = DesktopSession.ResolveEnginePort(
            launchedPid: 999, childrenOf: (_, _) => Array.Empty<int>(), portOfPid: _ => 55001,   // a live port EXISTS
            nowUtc: () => t, deadlineUtc: t.AddMilliseconds(-1), sleep: _ => { });
        Assert.Equal(0, engine);
        Assert.Equal(0, port);   // splash-hang, NOT a fallback to somebody else's engine
    }
}
