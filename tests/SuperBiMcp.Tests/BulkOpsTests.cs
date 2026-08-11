using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for the bulk fan-out helpers (<see cref="BulkOps"/>): glob resolution, per-file
/// output derivation, batch manifest detection/synthesis and result aggregation. No live engine and no
/// Power BI Desktop - the refresh loop itself is deliberately untested here (it needs a real Desktop
/// with a real model, which no CI box has).
/// </summary>
public sealed class BulkOpsTests
{
    // scratch root: SUPERBI_TEST_SCRATCH override (e.g. to keep scratch off the system drive), temp fallback.
    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "bulkops-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Touch(string path) => File.WriteAllText(path, "x");

    // ---------------- glob resolution ----------------

    [Fact]
    public void ResolveInputs_MixesLiteralsAndGlobs_DedupesCaseInsensitively_AndSorts()
    {
        string scratch = NewScratch();
        try
        {
            Touch(Path.Combine(scratch, "beta.pbix"));
            Touch(Path.Combine(scratch, "alpha.pbix"));
            Touch(Path.Combine(scratch, "notes.txt"));

            string missingLiteral = Path.Combine(scratch, "zeta.pbix"); // literal, does not exist
            var got = BulkOps.ResolveInputs(new[]
            {
                Path.Combine(scratch, "*.pbix"),
                Path.Combine(scratch, "ALPHA.PBIX"),   // duplicate of the glob hit, different case
                missingLiteral,                        // passes through - becomes a per-file failure later
            });

            var expected = new[]
            {
                Path.Combine(scratch, "alpha.pbix"),
                Path.Combine(scratch, "beta.pbix"),
                missingLiteral,
            }.Select(p => p.ToLowerInvariant());
            Assert.Equal(expected, got.Select(p => p.ToLowerInvariant()));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void ResolveInputs_GlobOverMissingDirectory_ContributesNothing()
    {
        string scratch = NewScratch();
        try
        {
            string missingDir = Path.Combine(scratch, "no-such-dir");
            Assert.Empty(BulkOps.ResolveInputs(new[] { Path.Combine(missingDir, "*.pbix") }));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void ResolveInputs_BareDirectory_ExpandsToItsPbixFilesOnly()
    {
        string scratch = NewScratch();
        try
        {
            Touch(Path.Combine(scratch, "a.pbix"));
            Touch(Path.Combine(scratch, "b.pbix"));
            Touch(Path.Combine(scratch, "c.txt"));
            string sub = Path.Combine(scratch, "sub");
            Directory.CreateDirectory(sub);
            Touch(Path.Combine(sub, "nested.pbix")); // non-recursive: must not appear

            var got = BulkOps.ResolveInputs(new[] { scratch });
            Assert.Equal(
                new[] { Path.Combine(scratch, "a.pbix"), Path.Combine(scratch, "b.pbix") }.Select(p => p.ToLowerInvariant()),
                got.Select(p => p.ToLowerInvariant()));
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    // ---------------- output derivation ----------------

    [Fact]
    public void DeriveOutput_OutputDir_KeepsTheFileNameInsideIt()
    {
        Assert.Equal(
            Path.Combine("D:\\out", "Client.pbix"),
            BulkOps.DeriveOutput("D:\\reports\\Client.pbix", "D:\\out", null, inPlace: false));
    }

    [Fact]
    public void DeriveOutput_Suffix_LandsBesideTheSource()
    {
        Assert.Equal(
            Path.Combine("D:\\reports", "Client-built.pbix"),
            BulkOps.DeriveOutput("D:\\reports\\Client.pbix", null, "-built", inPlace: false));
    }

    [Fact]
    public void DeriveOutput_OutputDirAndSuffix_Combine()
    {
        Assert.Equal(
            Path.Combine("D:\\out", "Client-built.pbix"),
            BulkOps.DeriveOutput("D:\\reports\\Client.pbix", "D:\\out", "-built", inPlace: false));
    }

    [Fact]
    public void DeriveOutput_NeitherRuleAndNoInPlace_IsAHardError()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BulkOps.DeriveOutput("D:\\reports\\Client.pbix", null, null, inPlace: false));
        Assert.Contains("outputDir", ex.Message);
        Assert.Contains("inPlace", ex.Message);
    }

    [Fact]
    public void DeriveOutput_ExplicitInPlace_ReturnsTheSource()
    {
        Assert.Equal(
            "D:\\reports\\Client.pbix",
            BulkOps.DeriveOutput("D:\\reports\\Client.pbix", null, null, inPlace: true));
    }

    // ---------------- batch manifest detection + per-file synthesis ----------------

    [Fact]
    public void IsBatchManifest_SourcesOrGlob_YesSingleSource_No()
    {
        Assert.True(BulkOps.IsBatchManifest(new JsonObject { ["sources"] = new JsonArray("a.pbix") }));
        Assert.True(BulkOps.IsBatchManifest(new JsonObject { ["glob"] = "D:\\r\\*.pbix" }));
        Assert.False(BulkOps.IsBatchManifest(new JsonObject { ["source"] = "a.pbix" }));
    }

    [Fact]
    public void SynthesizeFileManifest_CarriesRecipeFieldsVerbatim_AndIsolated()
    {
        var batch = new JsonObject
        {
            ["glob"] = "D:\\r\\*.pbix",
            ["outputDir"] = "D:\\out",
            ["recipe"] = "executive",
            ["config"] = new JsonObject { ["y"] = 2 },
            ["recipes"] = new JsonArray(
                new JsonObject { ["recipe"] = "grid", ["config"] = new JsonObject { ["x"] = 1 } }),
            ["verify"] = true,
        };

        var m = BulkOps.SynthesizeFileManifest(batch, "D:\\r\\a.pbix", "D:\\out\\a.pbix");

        Assert.Equal("D:\\r\\a.pbix", (string?)m["source"]);
        Assert.Equal("D:\\out\\a.pbix", (string?)m["output"]);
        Assert.Equal(batch["recipe"]!.ToJsonString(), m["recipe"]!.ToJsonString());
        Assert.Equal(batch["config"]!.ToJsonString(), m["config"]!.ToJsonString());
        Assert.Equal(batch["recipes"]!.ToJsonString(), m["recipes"]!.ToJsonString());
        Assert.Equal(batch["verify"]!.ToJsonString(), m["verify"]!.ToJsonString());
        Assert.Null(m["glob"]);      // fan-out fields never leak into the single-file manifest
        Assert.Null(m["outputDir"]);

        // deep-cloned: a per-file mutation cannot bleed into the shared batch manifest
        ((JsonObject)m["config"]!)["y"] = 99;
        Assert.Equal(2, (int?)batch["config"]!["y"]);
    }

    [Fact]
    public void TryParseBuildArgs_SynthesizesABatchManifest()
    {
        bool ok = BulkOps.TryParseBuildArgs(
            new[] { "build", "--glob", "D:\\r\\*.pbix", "--outputDir", "D:\\out", "--suffix", "-x", "--recipe", "grid" },
            out var manifest, out string error);

        Assert.True(ok, error);
        Assert.True(BulkOps.IsBatchManifest(manifest));
        Assert.Equal("D:\\r\\*.pbix", (string?)manifest["sources"]![0]);
        Assert.Equal("D:\\out", (string?)manifest["outputDir"]);
        Assert.Equal("-x", (string?)manifest["suffix"]);
        Assert.Equal("grid", (string?)manifest["recipe"]);
    }

    [Fact]
    public void TryParseBuildArgs_RejectsBadUsage()
    {
        Assert.False(BulkOps.TryParseBuildArgs(new[] { "build", "--nope" }, out _, out string e1));
        Assert.Contains("--nope", e1);

        Assert.False(BulkOps.TryParseBuildArgs(new[] { "build", "--suffix", "-x" }, out _, out string e2));
        Assert.Contains("--glob", e2);

        Assert.False(BulkOps.TryParseBuildArgs(new[] { "build", "--glob" }, out _, out string e3));
        Assert.Contains("--glob", e3);
    }

    // ---------------- result aggregation + exit codes ----------------

    [Fact]
    public void SummarizeBuild_AggregatesPerFileResults()
    {
        var results = new[]
        {
            new BulkOps.BuildFileResult("a.pbix", true, "out\\a.pbix", 2, null),
            new BulkOps.BuildFileResult("b.pbix", false, null, null, "PBIR format not yet supported"),
            new BulkOps.BuildFileResult("c.pbix", true, "out\\c.pbix", 1, null),
        };

        var s = BulkOps.SummarizeBuild(results);
        Assert.False((bool?)s["ok"]);
        Assert.Equal(3, (int?)s["total"]);
        Assert.Equal(2, (int?)s["succeeded"]);
        Assert.Equal(1, (int?)s["failed"]);

        var rows = (JsonArray)s["results"]!;
        Assert.Equal(3, rows.Count);
        Assert.Equal("out\\a.pbix", (string?)rows[0]!["output"]);
        Assert.Equal(2, (int?)rows[0]!["sections"]);
        Assert.Null(rows[0]!["error"]);
        Assert.False((bool?)rows[1]!["ok"]);
        Assert.Equal("PBIR format not yet supported", (string?)rows[1]!["error"]);
        Assert.Null(rows[1]!["output"]);

        var allOk = new[] { new BulkOps.BuildFileResult("a.pbix", true, "a.pbix", 1, null) };
        Assert.True((bool?)BulkOps.SummarizeBuild(allOk)["ok"]);
    }

    // ---------------- batch execution (offline - synthesised .pbix zips, per suite convention) ----------------

    private static void WriteMinimalPbix(string path)
    {
        using var fs = new FileStream(path, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string name, byte[] bytes)
        {
            using var s = zip.CreateEntry(name).Open();
            s.Write(bytes, 0, bytes.Length);
        }
        Add("[Content_Types].xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>"));
        Add("Report/Layout", new UnicodeEncoding(false, false).GetBytes("{\"sections\":[]}"));
    }

    [Fact]
    public void RunBatchBuild_OneBadFileFailsAlone_GoodFileStillBuilds()
    {
        string scratch = NewScratch();
        try
        {
            string good = Path.Combine(scratch, "good.pbix");
            WriteMinimalPbix(good);
            string bad = Path.Combine(scratch, "bad.pbix");
            using (var fs = new FileStream(bad, FileMode.Create))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                zip.CreateEntry("[Content_Types].xml"); // no Report/Layout -> Open rejects it (the PBIR case)

            int exit = BulkOps.RunBatchBuild(new JsonObject
            {
                ["glob"] = Path.Combine(scratch, "*.pbix"),
                ["suffix"] = "-built",
                ["recipe"] = "grid",
                ["config"] = new JsonObject { ["title"] = "Fan-out" },
            });

            // the bad file is a per-file failure (exit 1), never a batch abort: the good one still built
            Assert.Equal(1, exit);
            Assert.True(File.Exists(Path.Combine(scratch, "good-built.pbix")));
            Assert.True(File.Exists(good)); // suffix mode never touches the source
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    [Fact]
    public void BuildExitCode_AnyFailureIsOne_NeverTheFailureCount()
    {
        var ok = new BulkOps.BuildFileResult("a.pbix", true, "a.pbix", 1, null);
        var bad = new BulkOps.BuildFileResult("b.pbix", false, null, null, "boom");

        Assert.Equal(0, BulkOps.BuildExitCode(new[] { ok, ok }));
        Assert.Equal(1, BulkOps.BuildExitCode(new[] { ok, bad }));
        // three failures must still be 1 - a count of 2 or 3 would collide with usage/licence exits
        Assert.Equal(1, BulkOps.BuildExitCode(new[] { bad, bad, bad }));
    }
}
