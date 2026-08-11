using System.Text.Json.Nodes;
using SuperBiMcp;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Staging isolation for the materialiser's shared temp root. Every <see cref="Materialize.MaterialiseOne"/>
/// invocation must get its own work dir under %TEMP%\daxops-materialise (concurrent bakes of the SAME
/// solution must never share staging), and only the saver-success path may remove it - and then exactly that
/// dir, nothing wider. A saverless or failed run keeps its PBIP as the hand-finish / diagnosis artefact.
/// Everything here is offline: a synthetic Solution in a throwaway dir, no Power BI Desktop, no network.
/// </summary>
[Collection("solutions-env")]   // SUPERBI_PBIX_SAVER is process-wide; serialise with the other env-var tests
public sealed class MaterialiseStagingScopeTests
{
    // the smallest spec Headless.GenerateProject scaffolds: one table, one column, no data-folder token
    private const string SpecJson =
        @"{ ""tables"": [ { ""name"": ""Facts"", ""columns"": [ { ""name"": ""Id"", ""dataType"": ""int64"" } ] } ] }";

    private static SolutionLibrary.SolutionInfo NewSolution(TempDir home, string id)
    {
        string spec = home.File("model.spec.json");
        File.WriteAllText(spec, SpecJson);
        return new SolutionLibrary.SolutionInfo
        {
            Id = id,
            Name = id,
            Dir = home.Path,
            ModelSpecPath = spec,
            StarterPath = home.File("starter.pbix"),
        };
    }

    [Fact]
    public void TwoRunsOfTheSameSolution_StageIntoDistinctDirs_AndBothPbipsSurvive()
    {
        Environment.SetEnvironmentVariable("SUPERBI_PBIX_SAVER", null);
        using var home = Fixtures.NewWorkDir();
        var sol = NewSolution(home, "scope-two-runs");

        Materialize.Outcome first = Materialize.MaterialiseOne(sol, dataFolderOverride: null);
        Materialize.Outcome second = Materialize.MaterialiseOne(sol, dataFolderOverride: null);
        try
        {
            Assert.False(first.done, first.note);       // no saver -> stops honestly at the PBIP
            Assert.NotNull(first.pbip);
            Assert.NotNull(second.pbip);
            Assert.NotEqual(first.pbip, second.pbip);
            Assert.True(File.Exists(first.pbip!), "the first run's PBIP must survive the second run");
            Assert.True(File.Exists(second.pbip!));

            // both stage under %TEMP%\daxops-materialise, in a dir carrying the safe id + a per-run suffix
            string work = WorkDirOf(first.pbip!);
            Assert.Equal("daxops-materialise", Path.GetFileName(Path.GetDirectoryName(work)!));
            Assert.StartsWith("scope_two_runs-", Path.GetFileName(work), StringComparison.Ordinal);
        }
        finally
        {
            WipeWorkDirOf(first.pbip);
            WipeWorkDirOf(second.pbip);
        }
    }

    [Fact]
    public async Task ConcurrentRunsOfTheSameSolution_EachScaffoldTheirOwnPbip()
    {
        Environment.SetEnvironmentVariable("SUPERBI_PBIX_SAVER", null);
        using var home = Fixtures.NewWorkDir();
        var sol = NewSolution(home, "scope-concurrent");

        Materialize.Outcome[] runs = await Task.WhenAll(
            Task.Run(() => Materialize.MaterialiseOne(sol, dataFolderOverride: null)),
            Task.Run(() => Materialize.MaterialiseOne(sol, dataFolderOverride: null)));
        try
        {
            Assert.NotEqual(runs[0].pbip, runs[1].pbip);
            Assert.All(runs, o => Assert.True(o.pbip != null && File.Exists(o.pbip), o.note));
        }
        finally
        {
            foreach (var o in runs) WipeWorkDirOf(o.pbip);
        }
    }

    [SkippableFact]
    public void SaverSuccess_WipesExactlyItsOwnStaging_LeavingSiblingsIntact()
    {
        Skip.If(!OperatingSystem.IsWindows(), "the saver template runs through cmd.exe.");

        using var home = Fixtures.NewWorkDir();
        var sol = NewSolution(home, "scope-wipe");
        string root = Path.Combine(Path.GetTempPath(), "daxops-materialise");

        // a sibling invocation's staging (same solution, saverless) must survive the bake's cleanup
        Environment.SetEnvironmentVariable("SUPERBI_PBIX_SAVER", null);
        Materialize.Outcome sibling = Materialize.MaterialiseOne(sol, dataFolderOverride: null);
        try
        {
            string[] before = DirsFor(root, "scope_wipe-*");

            Environment.SetEnvironmentVariable("SUPERBI_PBIX_SAVER", "copy /y \"{pbip}\" \"{out}\"");
            Materialize.Outcome baked = Materialize.MaterialiseOne(sol, dataFolderOverride: null);

            Assert.True(baked.done, baked.note);
            Assert.True(File.Exists(baked.starter!));
            Assert.Null(baked.pbip);                            // its staging is gone: no dangling path returned
            Assert.Equal(before, DirsFor(root, "scope_wipe-*")); // its own dir removed, nothing else touched
            Assert.True(File.Exists(sibling.pbip!), "a sibling invocation's staging must survive");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SUPERBI_PBIX_SAVER", null);
            WipeWorkDirOf(sibling.pbip);
        }
    }

    [SkippableFact]
    public void SaverFailure_KeepsItsStagingPbip_ForDiagnosis()
    {
        Skip.If(!OperatingSystem.IsWindows(), "the saver template runs through cmd.exe.");

        using var home = Fixtures.NewWorkDir();
        var sol = NewSolution(home, "scope-keep");

        Environment.SetEnvironmentVariable("SUPERBI_PBIX_SAVER", "exit 1");
        Materialize.Outcome outcome;
        try { outcome = Materialize.MaterialiseOne(sol, dataFolderOverride: null); }
        finally { Environment.SetEnvironmentVariable("SUPERBI_PBIX_SAVER", null); }
        try
        {
            Assert.False(outcome.done);
            Assert.NotNull(outcome.pbip);
            Assert.True(File.Exists(outcome.pbip!), "a failed bake keeps its PBIP for diagnosis");
        }
        finally
        {
            WipeWorkDirOf(outcome.pbip);
        }
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The per-invocation work dir a scaffolded PBIP lives in (work\project\Model.pbip).</summary>
    private static string WorkDirOf(string pbip)
        => Path.GetDirectoryName(Path.GetDirectoryName(pbip))!;

    /// <summary>Test cleanup of exactly the work dir a run under test created (located via its PBIP).</summary>
    private static void WipeWorkDirOf(string? pbip)
    {
        if (pbip == null) return;
        string work = WorkDirOf(pbip);
        try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
        catch { /* best effort - a leaked temp dir is harmless */ }
    }

    private static string[] DirsFor(string root, string pattern)
        => Directory.Exists(root)
            ? Directory.GetDirectories(root, pattern).OrderBy(d => d, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
}

/// <summary>
/// <see cref="FilesConnector.DiscoverAsync"/> stages into %TEMP%\daxops-discover: every invocation must get
/// its own probe dir (concurrent discovers must never cross-pollute each other's schemas) and must discard
/// exactly that dir on the way out. Driven with the committed fixtures - no network, no Desktop.
/// </summary>
public sealed class FilesConnectorDiscoverScopeTests
{
    private static ConnectorRequest Req(params string[] files)
        => new() { Params = new JsonObject { ["files"] = new JsonArray(files.Select(f => (JsonNode)f).ToArray()) } };

    [Fact]
    public async Task ConcurrentDiscovers_SeeOnlyTheirOwnTables_AndLeaveNoProbeResidue()
    {
        string root = Path.Combine(Path.GetTempPath(), "daxops-discover");
        string[] before = Probes(root);

        var connector = new FilesConnector();
        SchemaDiscovery[] schemas = await Task.WhenAll(
            Task.Run(() => connector.DiscoverAsync(Req(Fixtures.Path("sales.csv")), CancellationToken.None)),
            Task.Run(() => connector.DiscoverAsync(Req(Fixtures.Path("multi.xlsx")), CancellationToken.None)));

        // each discovery sees exactly its own staged tables - shared staging would cross-pollute the schemas
        Assert.Equal(new[] { "sales" },
            schemas[0].Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { "Money", "People" },
            schemas[1].Tables.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());

        // both probe dirs were discarded on the way out
        Assert.Equal(before, Probes(root));
    }

    private static string[] Probes(string root)
        => Directory.Exists(root)
            ? Directory.GetDirectories(root).OrderBy(d => d, StringComparer.Ordinal).ToArray()
            : Array.Empty<string>();
}
