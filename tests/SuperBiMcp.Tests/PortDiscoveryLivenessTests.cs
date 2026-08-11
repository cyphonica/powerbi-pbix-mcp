using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The liveness gate on workspace discovery, proven over fabricated workspace trees on scratch - no live
/// msmdsrv, no real Desktop, no real LocalAppData. A workspace folder outlives a crashed Desktop, so a
/// <c>msmdsrv.port.txt</c> alone is not an instance: every candidate is gated on the injected pid lookup that
/// stands in for <c>DesktopInterop.FindMsmdsrvPidOnPort</c>, and after OS port reuse the file's port can even
/// belong to a DIFFERENT live engine - which is why the gate must drop a dead candidate rather than trust
/// recency.
///
/// Scope caveat for anyone reproducing a "No open Power BI Desktop model found" report: the production
/// <see cref="PortDiscovery.Discover()"/> only knows the MSI and Store workspace roots. A Desktop installed
/// anywhere else is invisible to it, and that miss reads exactly like this gate firing - check the install
/// path FIRST before blaming a stale port file.
/// </summary>
public sealed class PortDiscoveryLivenessTests
{
    private static PortDiscovery NewDiscovery() => new(NullLogger<PortDiscovery>.Instance);

    /// <summary>Fabricates one workspace folder holding a port file the way Desktop writes it: UTF-16 LE
    /// with a BOM (WriteAllText emits Encoding.Unicode's preamble), nested under a Data subfolder.</summary>
    private static string Workspace(TempDir root, string name, string portText, DateTime lastWriteUtc)
    {
        string dir = Path.Combine(root.Path, name, "Data");
        Directory.CreateDirectory(dir);
        string portFile = Path.Combine(dir, "msmdsrv.port.txt");
        File.WriteAllText(portFile, portText, Encoding.Unicode);
        File.SetLastWriteTimeUtc(portFile, lastWriteUtc);
        return dir;
    }

    // ---------------- the gate itself ----------------

    [Fact]
    public void AStaleWorkspaceWithAPortFileButNoLiveListener_IsDropped_AndTheLiveOneSurvives()
    {
        using var root = Fixtures.NewWorkDir();
        var now = DateTime.UtcNow;
        string liveDir = Workspace(root, "ws-live", "55001", now.AddMinutes(-10));
        // the stale workspace is deliberately NEWER: before the gate, recency alone would have elected it
        Workspace(root, "ws-crashed", "55002", now);

        var found = NewDiscovery().Discover(new[] { root.Path }, port => port == 55001 ? 4242 : 0);

        var live = Assert.Single(found);
        Assert.Equal(55001, live.Port);
        Assert.Equal(4242, live.OwnerPid);
        Assert.Equal(liveDir, live.WorkspaceDir);
    }

    [Fact]
    public void WhenEveryWorkspaceIsStale_DiscoveryIsEmpty_NotAThrow()
    {
        using var root = Fixtures.NewWorkDir();
        Workspace(root, "ws-a", "55001", DateTime.UtcNow.AddHours(-1));
        Workspace(root, "ws-b", "55002", DateTime.UtcNow);

        Assert.Empty(NewDiscovery().Discover(new[] { root.Path }, _ => 0));
    }

    [Fact]
    public void LiveInstances_StayOrderedNewestFirst_ByPortFileLastWrite()
    {
        using var root = Fixtures.NewWorkDir();
        var now = DateTime.UtcNow;
        Workspace(root, "ws-oldest", "55001", now.AddMinutes(-30));
        Workspace(root, "ws-newest", "55003", now);
        Workspace(root, "ws-middle", "55002", now.AddMinutes(-15));

        var found = NewDiscovery().Discover(new[] { root.Path }, port => port);   // all live, pid == port

        Assert.Equal(new[] { 55003, 55002, 55001 }, found.Select(i => i.Port).ToArray());
        Assert.All(found, i => Assert.Equal(i.Port, i.OwnerPid));
    }

    // ---------------- the scan feeding the gate ----------------

    [Fact]
    public void ThePortFileIsReadAsUtf16WithBom_AndTrailingWhitespaceIsTolerated()
    {
        using var root = Fixtures.NewWorkDir();
        Workspace(root, "ws", "55010\r\n", DateTime.UtcNow);

        var found = NewDiscovery().Discover(new[] { root.Path }, _ => 7);

        Assert.Equal(55010, Assert.Single(found).Port);
    }

    [Fact]
    public void DuplicatePortFiles_CollapseToOneCandidate_SoTheLivenessLookupRunsOncePerPort()
    {
        using var root = Fixtures.NewWorkDir();
        var now = DateTime.UtcNow;
        Workspace(root, "ws-a", "55001", now);
        Workspace(root, "ws-b", "55001", now.AddMinutes(-5));
        var probed = new List<int>();

        var found = NewDiscovery().Discover(new[] { root.Path }, port => { probed.Add(port); return 4242; });

        Assert.Equal(55001, Assert.Single(found).Port);
        Assert.Equal(new[] { 55001 }, probed);
    }

    [Fact]
    public void AGarbageOrNonPositivePortFile_IsSkippedWithoutReachingTheLivenessLookup()
    {
        using var root = Fixtures.NewWorkDir();
        Workspace(root, "ws-garbage", "not-a-port", DateTime.UtcNow);
        Workspace(root, "ws-zero", "0", DateTime.UtcNow);

        var found = NewDiscovery().Discover(
            new[] { root.Path },
            _ => throw new InvalidOperationException("no parsable port ever existed, so nothing may be probed."));

        Assert.Empty(found);
    }

    [Fact]
    public void AMissingRoot_IsSkippedQuietly()
    {
        using var root = Fixtures.NewWorkDir();
        string absent = Path.Combine(root.Path, "never-created");

        Assert.Empty(NewDiscovery().Discover(new[] { absent }, _ => 4242));
    }
}
