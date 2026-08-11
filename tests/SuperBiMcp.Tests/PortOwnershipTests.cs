using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for <see cref="DesktopInterop.AssertPortOwnedByLaunchedPid"/> and its retry wrapper.
/// Both lookups are injected, so the ownership walk is proven with no live msmdsrv and no Power BI Desktop -
/// the live half (<c>PbiEngine.Start</c>, the toolhelp and iphlpapi calls) needs a real engine and stays
/// untested here.
///
/// What the assert buys: a pipeline can never bind to a Desktop it did not launch. An unproven owner is a
/// failure, not a best effort, because the alternative is driving somebody else's document.
///
/// The assert no-ops off Windows, so the facts that prove a throw are Windows-only by construction.
/// </summary>
public sealed class PortOwnershipTests
{
    private const int Port = 55001;
    private const int LaunchedPid = 100;

    private static void SkipOffWindows() =>
        Skip.If(!OperatingSystem.IsWindows(), "the ownership assert has nothing to assert off Windows.");

    /// <summary>A parent lookup over an explicit child -> parent chain; 0 (no parent) for anything else.</summary>
    private static Func<int, int> Chain(params int[] pidsChildFirst) => pid =>
    {
        int i = Array.IndexOf(pidsChildFirst, pid);
        return i >= 0 && i + 1 < pidsChildFirst.Length ? pidsChildFirst[i + 1] : 0;
    };

    // ---------------- the walk ----------------

    [SkippableFact]
    public void AnEngineWhoseParentIsTheLaunchedPid_IsOwned()
    {
        SkipOffWindows();
        DesktopInterop.AssertPortOwnedByLaunchedPid(Port, LaunchedPid,
            msmdsrvPidOnPort: _ => 101,
            parentOf: pid => pid == 101 ? LaunchedPid : 0);
    }

    [SkippableFact]
    public void AnEngineThatIsTheLaunchedPid_IsOwned()
    {
        // PbiEngine launches msmdsrv directly, so there is no parent hop to walk.
        SkipOffWindows();
        DesktopInterop.AssertPortOwnedByLaunchedPid(Port, LaunchedPid,
            msmdsrvPidOnPort: _ => LaunchedPid,
            parentOf: _ => throw new InvalidOperationException("the parent walk must not run when the engine IS the launched pid."));
    }

    [SkippableFact]
    public void AnEngineOwnedByAStranger_Throws_NamingBothPids()
    {
        SkipOffWindows();
        var ex = Assert.Throws<PortOwnershipException>(() =>
            DesktopInterop.AssertPortOwnedByLaunchedPid(Port, launchedPid: 200,
                msmdsrvPidOnPort: _ => 101,
                parentOf: pid => pid == 101 ? LaunchedPid : 0));

        // The operator has to be able to tell which two processes disagreed.
        Assert.Contains("101", ex.Message);
        Assert.Contains("200", ex.Message);
    }

    [SkippableFact]
    public void APortWithNoLiveOwner_Throws()
    {
        SkipOffWindows();
        var ex = Assert.Throws<PortOwnershipException>(() =>
            DesktopInterop.AssertPortOwnedByLaunchedPid(Port, LaunchedPid,
                msmdsrvPidOnPort: _ => 0,
                parentOf: _ => LaunchedPid));

        Assert.Contains("no live msmdsrv owner", ex.Message);
        Assert.Contains(Port.ToString(), ex.Message);
    }

    [SkippableFact]
    public void TheLaunchedPidFourHopsUp_IsStillOwned()
    {
        // The boundary the walk allows.
        SkipOffWindows();
        DesktopInterop.AssertPortOwnedByLaunchedPid(Port, LaunchedPid,
            msmdsrvPidOnPort: _ => 104,
            parentOf: Chain(104, 103, 102, 101, LaunchedPid));
    }

    [SkippableFact]
    public void TheLaunchedPidFiveHopsUp_IsNotOwned()
    {
        // The walk stops at 4 hops: an ancestor that distant is not evidence of ownership, it is a coincidence.
        SkipOffWindows();
        Assert.Throws<PortOwnershipException>(() =>
            DesktopInterop.AssertPortOwnedByLaunchedPid(Port, LaunchedPid,
                msmdsrvPidOnPort: _ => 105,
                parentOf: Chain(105, 104, 103, 102, 101, LaunchedPid)));
    }

    [SkippableFact]
    public void AParentChainThatDeadEnds_Throws_RatherThanLoopingOnZero()
    {
        SkipOffWindows();
        Assert.Throws<PortOwnershipException>(() =>
            DesktopInterop.AssertPortOwnedByLaunchedPid(Port, LaunchedPid,
                msmdsrvPidOnPort: _ => 101,
                parentOf: _ => 0));
    }

    // ---------------- the retry wrapper ----------------

    [SkippableFact]
    public void TheRetryWrapper_SucceedsOnTheThirdAttempt_AfterExactlyTwoSleeps()
    {
        // The port file can parse microseconds before the socket enters the LISTENING table, so a first miss
        // is expected rather than a failure.
        SkipOffWindows();
        int lookups = 0;
        var sleeps = new List<int>();

        DesktopInterop.AssertPortOwnedByLaunchedPidWithRetry(Port, LaunchedPid,
            attempts: 20, delayMs: 250,
            msmdsrvPidOnPort: _ => ++lookups < 3 ? 0 : 101,   // not listening yet on the first two looks
            parentOf: pid => pid == 101 ? LaunchedPid : 0,
            sleep: ms => sleeps.Add(ms));

        Assert.Equal(3, lookups);
        Assert.Equal(new[] { 250, 250 }, sleeps);
    }

    [SkippableFact]
    public void TheRetryWrapper_SleepsNotAtAllWhenTheFirstLookSucceeds()
    {
        SkipOffWindows();
        var sleeps = new List<int>();

        DesktopInterop.AssertPortOwnedByLaunchedPidWithRetry(Port, LaunchedPid,
            attempts: 20, delayMs: 250,
            msmdsrvPidOnPort: _ => 101,
            parentOf: pid => pid == 101 ? LaunchedPid : 0,
            sleep: ms => sleeps.Add(ms));

        Assert.Empty(sleeps);
    }

    [SkippableFact]
    public void TheRetryWrapper_GivesUpAfterTheBudgetAndRethrows()
    {
        // A retry budget is not a licence to hang: the last attempt's exception is the caller's answer.
        SkipOffWindows();
        int lookups = 0;
        var sleeps = new List<int>();

        var ex = Assert.Throws<PortOwnershipException>(() =>
            DesktopInterop.AssertPortOwnedByLaunchedPidWithRetry(Port, LaunchedPid,
                attempts: 4, delayMs: 10,
                msmdsrvPidOnPort: _ => { lookups++; return 0; },
                parentOf: _ => 0,
                sleep: ms => sleeps.Add(ms)));

        Assert.Equal(4, lookups);
        Assert.Equal(3, sleeps.Count);   // one sleep between attempts, none after the last
        Assert.Contains("no live msmdsrv owner", ex.Message);
    }

    [SkippableFact]
    public void TheRetryWrapper_DoesNotRetryAStrangerIntoAnOwner()
    {
        // A stranger on the port is a permanent answer, but the wrapper cannot tell it from a slow start, so
        // it spends the budget and still refuses. What matters is that it never returns.
        SkipOffWindows();
        Assert.Throws<PortOwnershipException>(() =>
            DesktopInterop.AssertPortOwnedByLaunchedPidWithRetry(Port, LaunchedPid,
                attempts: 3, delayMs: 1,
                msmdsrvPidOnPort: _ => 999,
                parentOf: _ => 888,
                sleep: _ => { }));
    }

    // ---------------- the exe path ----------------

    [Fact]
    public void ResolvePbixExe_PrefersTheFlag_ThenTheEnvVar_ThenTheStockInstall()
    {
        const string Var = "DAXOPS_PBIDESKTOP_EXE";
        string? saved = Environment.GetEnvironmentVariable(Var);
        try
        {
            Environment.SetEnvironmentVariable(Var, null);
            Assert.Equal(DesktopInterop.DefaultPbixExe, DesktopInterop.ResolvePbixExe(null));
            Assert.Equal(@"C:\flag\PBIDesktop.exe", DesktopInterop.ResolvePbixExe(@"C:\flag\PBIDesktop.exe"));

            Environment.SetEnvironmentVariable(Var, @"C:\env\PBIDesktop.exe");
            Assert.Equal(@"C:\env\PBIDesktop.exe", DesktopInterop.ResolvePbixExe(null));
            Assert.Equal(@"C:\flag\PBIDesktop.exe", DesktopInterop.ResolvePbixExe(@"C:\flag\PBIDesktop.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(Var, saved);
        }
    }
}
