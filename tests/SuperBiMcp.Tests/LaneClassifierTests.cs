using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for <see cref="LaneClassifier"/>. Both of the classifier's inputs are injected, so
/// these prove the gate with no engine binary installed and no Power BI Desktop.
///
/// The rule under test: heavy is a RUNTIME property, not a route property. A route that could bake is still
/// cheap on a deployment that has no engine to bake with, and keying the lane on the route name instead would
/// serialise an entire scaffold-only deployment to one job at a time.
/// </summary>
public sealed class LaneClassifierTests
{
    private const string Engine = @"C:\engine\msmdsrv.exe";

    private static readonly Func<string, bool> Installed = _ => true;
    private static readonly Func<string, bool> NotInstalled = _ => false;

    [Fact]
    public void BothConditionsMet_IsHeavy()
    {
        // The only combination that reaches msmdsrv.exe.
        Assert.Equal(Lane.Heavy, LaneClassifier.Classify(routeCanBake: true, "server", Engine, Installed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void NoAsServer_IsCheap(string? asServer)
    {
        // Without SUPERBI_AS_SERVER the bake never happens, whatever the route is.
        Assert.Equal(Lane.Cheap, LaneClassifier.Classify(routeCanBake: true, asServer, Engine, Installed));
    }

    [Fact]
    public void NoEngineBinary_IsCheap()
    {
        // A scaffold-only deployment is never heavy: the route degrades to a PBIP zip that costs nothing.
        Assert.Equal(Lane.Cheap, LaneClassifier.Classify(routeCanBake: true, "server", Engine, NotInstalled));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ARouteThatCannotBake_IsAlwaysCheap(bool asServerSet, bool engineInstalled)
    {
        Lane lane = LaneClassifier.Classify(
            routeCanBake: false,
            asServerSet ? "server" : null,
            Engine,
            engineInstalled ? Installed : NotInstalled);

        Assert.Equal(Lane.Cheap, lane);
    }

    [Fact]
    public void EveryCombinationShortOfBothConditions_IsCheap()
    {
        // The truth table in full: heavy has exactly one row.
        var heavy = new List<(bool route, string? asServer, bool engine)>();
        foreach (bool route in new[] { true, false })
            foreach (string? asServer in new[] { "server", null })
                foreach (bool engine in new[] { true, false })
                {
                    Lane lane = LaneClassifier.Classify(route, asServer, Engine, engine ? Installed : NotInstalled);
                    if (lane == Lane.Heavy) heavy.Add((route, asServer, engine));
                }

        Assert.Equal(new[] { (true, (string?)"server", true) }, heavy);
    }

    [Fact]
    public void TheEnginePathUnderTestIsTheOneProbed()
    {
        // The existence probe must be asked about the engine path it was handed, not about some other file.
        var asked = new List<string>();
        LaneClassifier.Classify(routeCanBake: true, "server", Engine, p => { asked.Add(p); return true; });

        Assert.Equal(new[] { Engine }, asked);
    }

    [Fact]
    public void TheEngineProbeIsNotRunWhenTheEarlierConditionsAlreadyDecided()
    {
        // Short-circuit order matches Bake.BakeProject's own gate: no filesystem hit on a route that cannot bake.
        var asked = new List<string>();
        Func<string, bool> probe = p => { asked.Add(p); return true; };

        Assert.Equal(Lane.Cheap, LaneClassifier.Classify(routeCanBake: false, "server", Engine, probe));
        Assert.Equal(Lane.Cheap, LaneClassifier.Classify(routeCanBake: true, null, Engine, probe));
        Assert.Empty(asked);
    }

    [Fact]
    public void ANullProbeIsRejectedRatherThanSilentlyTreatedAsMissing()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LaneClassifier.Classify(routeCanBake: true, "server", Engine, null!));
    }

    [Fact]
    public void TheProductionOverload_IsCheapWithoutAsServer()
    {
        // The two-arg overload resolves the installed engine path itself; with no asServer it cannot be heavy,
        // which is the one assertion that holds on a box with no engine installed.
        Assert.Equal(Lane.Cheap, LaneClassifier.Classify(routeCanBake: true, asServer: null));
        Assert.Equal(Lane.Cheap, LaneClassifier.Classify(routeCanBake: false, asServer: "server"));
    }
}
