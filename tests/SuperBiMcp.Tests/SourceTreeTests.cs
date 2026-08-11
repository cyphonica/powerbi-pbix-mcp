using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// unpack_to_source / pbix_diff coverage. Everything is synthesised in-test: the model is an in-memory
/// <c>new TOM.Model()</c> (no live engine, no SaveChanges), the .pbix files are fake zips (legacy
/// Report/Layout UTF-16 blob or a PBIR definition tree). Proves:
///   - unpacking the same inputs twice yields byte-identical trees (the determinism contract),
///   - the wipe-safety marker: a foreign non-empty folder is refused and NOT deleted; a previous
///     unpack (or an empty folder) is overwritten cleanly,
///   - PBIR JSON is canonicalised (pretty UTF-8 no BOM) and non-JSON entries copy byte-verbatim,
///   - pbix_diff on two unpacked trees pins a changed measure's DAX and an added report page.
/// </summary>
public sealed class SourceTreeTests : IDisposable
{
    private readonly string _scratch;

    public SourceTreeTests()
    {
        // scratch root: SUPERBI_TEST_SCRATCH override (e.g. to keep scratch off the system drive), temp fallback.
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        _scratch = Path.Combine(root, "sourcetree-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true); } catch { }
    }

    // ---------------------------------------------------------------- fixtures (all in-memory / synthetic)

    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model" };
        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Qty", DataType = TOM.DataType.Int64, SourceColumn = "Qty" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Qty", Expression = "SUM('Sales'[Qty])" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let S=1 in S" } });
        model.Tables.Add(sales);
        return model;
    }

    private const string LegacyLayoutJson =
        "{\"id\":0,\"sections\":[{\"name\":\"ReportSection1\",\"displayName\":\"Page 1\"," +
        "\"visualContainers\":[{\"x\":0,\"y\":0,\"config\":\"{}\"}],\"config\":\"{}\"}],\"config\":\"{}\"}";

    /// <summary>A fake legacy .pbix: Report/Layout as UTF-16 LE WITH the FF FE BOM (as Desktop writes it)
    /// plus one StaticResources entry that must copy byte-verbatim.</summary>
    private string WriteLegacyPbix()
    {
        string pbix = Path.Combine(_scratch, "legacy-" + Guid.NewGuid().ToString("N")[..8] + ".pbix");
        using var fs = new FileStream(pbix, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Add(string entry, byte[] bytes)
        {
            var e = zip.CreateEntry(entry);
            using var s = e.Open(); s.Write(bytes, 0, bytes.Length);
        }
        var utf16 = new UnicodeEncoding(false, true);
        Add("Report/Layout", utf16.GetPreamble().Concat(utf16.GetBytes(LegacyLayoutJson)).ToArray());
        Add("Report/StaticResources/RegisteredResources/logo.png", new byte[] { 137, 80, 78, 71, 1, 2, 3 });
        Add("[Content_Types].xml", Encoding.UTF8.GetBytes(
            "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>"));
        return pbix;
    }

    private const string PageA = "11111111-1111-1111-1111-111111111111";

    // deliberately scruffy (single-line) JSON so canonicalisation is observable
    private const string ScruffyPointerJson = "{\"version\":\"1.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../Model\"}}}";

    /// <summary>A fake PBIR .pbix: pointer + report.json + pages index + one page folder with one visual,
    /// plus a non-JSON entry that must copy byte-verbatim.</summary>
    private string WritePbirPbix()
    {
        string pbix = Path.Combine(_scratch, "pbir-" + Guid.NewGuid().ToString("N")[..8] + ".pbix");
        using var fs = new FileStream(pbix, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void AddText(string entry, string text)
        {
            var e = zip.CreateEntry(entry);
            using var s = e.Open();
            byte[] b = new UTF8Encoding(false).GetBytes(text);
            s.Write(b, 0, b.Length);
        }
        AddText("Report/definition.pbir", ScruffyPointerJson);
        AddText("Report/definition/report.json", "{\"$schema\":\"report/1.0.0\",\"themeCollection\":{}}");
        AddText("Report/definition/pages/pages.json",
            $"{{\"pageOrder\":[\"{PageA}\"],\"activePageName\":\"{PageA}\"}}");
        AddText($"Report/definition/pages/{PageA}/page.json",
            $"{{\"name\":\"{PageA}\",\"displayName\":\"Overview\",\"width\":1280,\"height\":720}}");
        AddText($"Report/definition/pages/{PageA}/visuals/aaaa/visual.json",
            "{\"name\":\"aaaa\",\"visual\":{\"visualType\":\"card\"}}");
        var raw = zip.CreateEntry("Report/StaticResources/RegisteredResources/img.png");
        using (var s = raw.Open()) { byte[] b = { 9, 8, 7, 6 }; s.Write(b, 0, b.Length); }
        AddText("[Content_Types].xml",
            "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"></Types>");
        return pbix;
    }

    private static void CopyTree(string src, string dst)
    {
        foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string dest = Path.Combine(dst, Path.GetRelativePath(src, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    private static void AssertTreesIdentical(string dirA, string dirB)
    {
        string[] Rel(string d) => Directory.GetFiles(d, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(d, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var a = Rel(dirA);
        Assert.Equal(a, Rel(dirB));
        foreach (var rel in a)
            Assert.True(
                File.ReadAllBytes(Path.Combine(dirA, rel)).SequenceEqual(File.ReadAllBytes(Path.Combine(dirB, rel))),
                $"content differs between runs: {rel}");
    }

    private static JsonObject AsJson(object result) => JsonSerializer.SerializeToNode(result)!.AsObject();

    // ---------------------------------------------------------------- determinism (legacy)

    [Fact]
    public void Unpack_Legacy_TwoRuns_ProduceByteIdenticalTrees()
    {
        var model = NewModel();
        string pbix = WriteLegacyPbix();
        string out1 = Path.Combine(_scratch, "leg1");
        string out2 = Path.Combine(_scratch, "leg2");

        var r1 = AsJson(SourceTree.Unpack(model, pbix, out1));
        var r2 = AsJson(SourceTree.Unpack(model, pbix, out2));

        Assert.True((bool)r1["ok"]!);
        Assert.Equal("legacy", (string?)r1["format"]);
        Assert.Equal("legacy", (string?)r2["format"]);
        Assert.True((int)r1["modelFiles"]! > 0);
        Assert.True(File.Exists(Path.Combine(out1, "Report", "Layout.json")));
        Assert.True(File.Exists(Path.Combine(out1, "Report", "StaticResources", "RegisteredResources", "logo.png")));
        Assert.True(File.Exists(Path.Combine(out1, ".superbi-source")));

        AssertTreesIdentical(out1, out2);

        // Layout.json is canonical: UTF-8 without BOM, UTF-16 BOM stripped, parses back to the sections
        byte[] lb = File.ReadAllBytes(Path.Combine(out1, "Report", "Layout.json"));
        Assert.False(lb.Length >= 3 && lb[0] == 0xEF && lb[1] == 0xBB && lb[2] == 0xBF, "Layout.json must not carry a BOM");
        var layout = JsonNode.Parse(Encoding.UTF8.GetString(lb))!.AsObject();
        Assert.Equal("Page 1", (string?)layout["sections"]![0]!["displayName"]);

        // the StaticResources entry copied byte-verbatim
        Assert.Equal(new byte[] { 137, 80, 78, 71, 1, 2, 3 },
            File.ReadAllBytes(Path.Combine(out1, "Report", "StaticResources", "RegisteredResources", "logo.png")));
    }

    // ---------------------------------------------------------------- wipe-safety marker

    [Fact]
    public void Unpack_RefusesForeignNonEmptyFolder_AndDeletesNothing()
    {
        var model = NewModel();
        string pbix = WriteLegacyPbix();
        string outDir = Path.Combine(_scratch, "foreign");
        Directory.CreateDirectory(outDir);
        string stray = Path.Combine(outDir, "precious.txt");
        File.WriteAllText(stray, "do not delete");

        var ex = Assert.Throws<InvalidOperationException>(() => SourceTree.Unpack(model, pbix, outDir));
        Assert.Contains("not a previous unpack", ex.Message);
        Assert.True(File.Exists(stray));
        Assert.Equal("do not delete", File.ReadAllText(stray));
    }

    [Fact]
    public void Unpack_AcceptsEmptyFolder_AndOverwritesPreviousUnpack()
    {
        var model = NewModel();
        string pbix = WriteLegacyPbix();
        string outDir = Path.Combine(_scratch, "rerun");
        Directory.CreateDirectory(outDir);   // empty folder: allowed

        var r1 = AsJson(SourceTree.Unpack(model, pbix, outDir));
        Assert.True((bool)r1["ok"]!);
        Assert.True(File.Exists(Path.Combine(outDir, ".superbi-source")));

        // a previous unpack (marker present, folder non-empty): allowed to wipe and redo
        var r2 = AsJson(SourceTree.Unpack(model, pbix, outDir));
        Assert.True((bool)r2["ok"]!);
        Assert.True(File.Exists(Path.Combine(outDir, "Report", "Layout.json")));
    }

    // ---------------------------------------------------------------- PBIR variant

    [Fact]
    public void Unpack_Pbir_CanonicalisesJson_AndIsDeterministic()
    {
        var model = NewModel();
        string pbix = WritePbirPbix();
        string out1 = Path.Combine(_scratch, "pbir1");
        string out2 = Path.Combine(_scratch, "pbir2");

        var r1 = AsJson(SourceTree.Unpack(model, pbix, out1));
        SourceTree.Unpack(model, pbix, out2);

        Assert.Equal("pbir", (string?)r1["format"]);
        AssertTreesIdentical(out1, out2);

        // scruffy single-line input JSON comes out canonical (pretty-printed, property order preserved)
        var canonOpts = new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true };
        string pointer = File.ReadAllText(Path.Combine(out1, "Report", "definition.pbir"));
        Assert.Equal(JsonNode.Parse(ScruffyPointerJson)!.ToJsonString(canonOpts), pointer);
        byte[] pb = File.ReadAllBytes(Path.Combine(out1, "Report", "definition.pbir"));
        Assert.False(pb.Length >= 3 && pb[0] == 0xEF && pb[1] == 0xBB && pb[2] == 0xBF, "PBIR JSON must not carry a BOM");

        // the whole definition tree landed, and the non-JSON entry copied byte-verbatim
        Assert.True(File.Exists(Path.Combine(out1, "Report", "definition", "pages", PageA, "page.json")));
        Assert.Equal(new byte[] { 9, 8, 7, 6 },
            File.ReadAllBytes(Path.Combine(out1, "Report", "StaticResources", "RegisteredResources", "img.png")));
    }

    // ---------------------------------------------------------------- pbix_diff on two unpacked trees

    [Fact]
    public void Diff_TwoTrees_PinsChangedMeasure_AddedPage_MinimalFiles()
    {
        var model = NewModel();
        string pbix = WriteLegacyPbix();
        string dirA = Path.Combine(_scratch, "treeA");
        string dirB = Path.Combine(_scratch, "treeB");
        SourceTree.Unpack(model, pbix, dirA);
        CopyTree(dirA, dirB);

        // change ONE measure's DAX in the copy's tables/*.tmdl
        string salesTmdl = Path.Combine(dirB, "Model", "definition", "tables", "Sales.tmdl");
        string tmdl = File.ReadAllText(salesTmdl);
        Assert.Contains("SUM('Sales'[Amount])", tmdl);
        File.WriteAllText(salesTmdl, tmdl.Replace("SUM('Sales'[Amount])", "SUMX('Sales', 'Sales'[Amount])"), new UTF8Encoding(false));

        // add ONE page to the copy's report side
        string layoutPath = Path.Combine(dirB, "Report", "Layout.json");
        var layout = JsonNode.Parse(File.ReadAllText(layoutPath))!.AsObject();
        ((JsonArray)layout["sections"]!).Add(new JsonObject
        {
            ["name"] = "ReportSection2",
            ["displayName"] = "Added Page",
            ["visualContainers"] = new JsonArray(),
        });
        File.WriteAllText(layoutPath, layout.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        var report = new ReportService(new SessionStore(), NullLogger<ReportService>.Instance);
        var diff = AsJson(SourceTree.Diff(dirA, dirB, report));

        Assert.True((bool)diff["ok"]!);
        Assert.Equal("tree", (string?)diff["a"]!["kind"]);
        Assert.Equal("tree", (string?)diff["b"]!["kind"]);

        Assert.Equal(new[] { "Total Sales" }, ((JsonArray)diff["model"]!["measuresChanged"]!).Select(n => (string?)n).ToArray());
        Assert.Empty((JsonArray)diff["model"]!["measuresAdded"]!);
        Assert.Empty((JsonArray)diff["model"]!["measuresRemoved"]!);
        Assert.Empty((JsonArray)diff["model"]!["tablesAdded"]!);
        Assert.Empty((JsonArray)diff["model"]!["tablesRemoved"]!);

        Assert.Equal(new[] { "Added Page" }, ((JsonArray)diff["report"]!["pagesAdded"]!).Select(n => (string?)n).ToArray());
        Assert.Empty((JsonArray)diff["report"]!["pagesRemoved"]!);
        Assert.Empty((JsonArray)diff["report"]!["visualCountChanges"]!);

        // exactly the two edited files - nothing else drifted
        Assert.Equal(new[] { "Model/definition/tables/Sales.tmdl", "Report/Layout.json" },
            ((JsonArray)diff["files"]!["changed"]!).Select(n => (string?)n).ToArray());
        Assert.Empty((JsonArray)diff["files"]!["added"]!);
        Assert.Empty((JsonArray)diff["files"]!["removed"]!);
    }
}
