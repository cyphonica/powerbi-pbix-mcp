using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive coverage of the PBIR (enhanced report) read/write path. Every PBIR tree is SYNTHESISED
/// in-test (as a folder AND as a zipped .pbix-like archive) - no real .pbix/PBIP on disk is touched. Proves:
///   - detect_report_format classifies legacy vs pbir vs pbip (and distinguishes them - fault sensitivity),
///   - read returns the pages + visuals from both a folder and a .pbix,
///   - round-trip open+save preserves unchanged files byte-for-byte + GUID names, re-emitting only changed files,
///   - create_pbir_page adds the GUID folder + updates pages.json order,
///   - add_pbir_visual writes a visual.json with the right visualType + projections,
///   - set_pbir_visual_format merges via the shared encoder (incl. a nested-object value written verbatim),
///   - add_pbir_filter writes the right condition at report/page/visual scope,
///   - convert_legacy_to_pbir explodes a 2-page legacy Layout into the tree,
///   - a malformed save is caught before disk is touched.
/// </summary>
public sealed class PbirServiceTests : IDisposable
{
    private readonly string _scratch;

    public PbirServiceTests()
    {
        // scratch root: SUPERBI_TEST_SCRATCH override (e.g. to keep scratch off the system drive), temp fallback.
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        _scratch = Path.Combine(root, "pbir-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_scratch)) Directory.Delete(_scratch, recursive: true); } catch { }
    }

    private static PbirService NewSvc(out SessionStore store)
    {
        store = new SessionStore();
        return new PbirService(store, NullLogger<PbirService>.Instance);
    }

    // ---------------------------------------------------------------- synthetic PBIR tree (rel -> json text)

    private const string PageA = "11111111-1111-1111-1111-111111111111";
    private const string PageB = "22222222-2222-2222-2222-222222222222";
    private const string Vis1 = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";

    /// <summary>The files of a minimal but valid PBIR report: pointer, report.json, pages index, two pages,
    /// one visual on page A. Returned as rel-path -> UTF-8 JSON text.</summary>
    private static Dictionary<string, string> SynthTree()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        files["definition.pbir"] = """
        { "version": "1.0", "datasetReference": { "byPath": { "path": "../Model" } } }
        """;

        files["report.json"] = """
        { "$schema": "report/1.0.0", "themeCollection": { "baseTheme": { "name": "CY24SU10" } } }
        """;

        files["definition/pages/pages.json"] =
            $$"""
            { "pageOrder": ["{{PageA}}", "{{PageB}}"], "activePageName": "{{PageA}}" }
            """;

        files[$"definition/pages/{PageA}/page.json"] =
            $$"""
            { "name": "{{PageA}}", "displayName": "Overview", "displayOption": "FitToPage", "width": 1280, "height": 720 }
            """;

        files[$"definition/pages/{PageB}/page.json"] =
            $$"""
            { "name": "{{PageB}}", "displayName": "Detail", "displayOption": "FitToPage", "width": 1280, "height": 720 }
            """;

        files[$"definition/pages/{PageA}/visuals/{Vis1}/visual.json"] =
            $$"""
            {
              "name": "{{Vis1}}",
              "position": { "x": 10, "y": 10, "z": 0, "width": 400, "height": 300, "tabOrder": 0 },
              "visual": { "visualType": "card", "drillFilterOtherVisuals": true }
            }
            """;

        return files;
    }

    private string WriteSynthFolder()
    {
        string root = Path.Combine(_scratch, "report-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in SynthTree())
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
        return root;
    }

    /// <summary>A synthetic PBIR .pbix: the definition tree under Report/, plus a DataModel and the OPC
    /// scaffolding ([Content_Types].xml) and a SecurityBindings part we expect Save to strip.</summary>
    private string WriteSynthPbix(out byte[] dataModelBytes)
    {
        string pbix = Path.Combine(_scratch, "report-" + Guid.NewGuid().ToString("N") + ".pbix");
        dataModelBytes = Encoding.ASCII.GetBytes("FAKE-DATAMODEL-DO-NOT-TOUCH");
        var dm = dataModelBytes;
        using (var fs = new FileStream(pbix, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            void WriteText(string entry, string text)
            {
                var e = zip.CreateEntry(entry, CompressionLevel.Optimal);
                using var s = e.Open();
                byte[] b = new UTF8Encoding(false).GetBytes(text);
                s.Write(b, 0, b.Length);
            }
            foreach (var (rel, text) in SynthTree()) WriteText("Report/" + rel, text);

            // DataModel (must be carried through untouched), content types, and a security signature to strip.
            var dme = zip.CreateEntry("DataModel", CompressionLevel.NoCompression);
            using (var ds = dme.Open()) ds.Write(dm, 0, dm.Length);

            WriteText("[Content_Types].xml",
                "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"json\" ContentType=\"application/json\" />" +
                "<Override PartName=\"/SecurityBindings\" ContentType=\"application/x-ms-securitybindings\" />" +
                "</Types>");

            var sb = zip.CreateEntry("SecurityBindings", CompressionLevel.Optimal);
            using (var ss = sb.Open()) { byte[] x = { 9, 9, 9 }; ss.Write(x, 0, x.Length); }
        }
        return pbix;
    }

    private static JsonObject Obj(object result) =>
        JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject ?? new JsonObject();

    // ================================================================= detect_report_format

    [Fact]
    public void Detect_Folder_IsPbip()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        var r = Obj(svc.DetectReportFormat(folder));
        Assert.Equal("pbip", (string?)r["format"]);
        Assert.Equal("folder", (string?)r["source"]);
    }

    [Fact]
    public void Detect_Pbix_WithDefinition_IsPbir()
    {
        var svc = NewSvc(out _);
        string pbix = WriteSynthPbix(out _);
        var r = Obj(svc.DetectReportFormat(pbix));
        Assert.Equal("pbir", (string?)r["format"]);
        Assert.Equal("pbix", (string?)r["source"]);
    }

    [Fact]
    public void Detect_LegacyPbix_IsLegacy_NotPbir()
    {
        // FAULT SENSITIVITY: a legacy Report/Layout .pbix must classify as legacy, never pbir.
        var svc = NewSvc(out _);
        string pbix = Path.Combine(_scratch, "legacy.pbix");
        using (var fs = new FileStream(pbix, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("Report/Layout", CompressionLevel.Optimal);
            using var s = e.Open();
            // legacy Layout is UTF-16-LE
            byte[] b = new UnicodeEncoding(false, false).GetBytes("{\"sections\":[]}");
            s.Write(b, 0, b.Length);
        }
        var r = Obj(svc.DetectReportFormat(pbix));
        Assert.Equal("legacy", (string?)r["format"]);
        Assert.NotEqual("pbir", (string?)r["format"]);
    }

    [Fact]
    public void Detect_MissingPath_Throws()
    {
        var svc = NewSvc(out _);
        Assert.ThrowsAny<Exception>(() => svc.DetectReportFormat(Path.Combine(_scratch, "nope.pbix")));
    }

    // ================================================================= read (folder + pbix)

    [Fact]
    public void Read_Folder_ReturnsPagesAndVisuals()
    {
        var svc = NewSvc(out _);
        var r = Obj(svc.ReadPbir(WriteSynthFolder()));
        var pages = r["pages"] as JsonArray;
        Assert.NotNull(pages);
        Assert.Equal(2, pages!.Count);

        var overview = pages.OfType<JsonObject>().First(p => (string?)p["displayName"] == "Overview");
        Assert.Equal(PageA, (string?)overview["name"]);
        Assert.Equal(1, (int?)overview["visuals"]);

        var detail = pages.OfType<JsonObject>().First(p => (string?)p["displayName"] == "Detail");
        Assert.Equal(0, (int?)detail["visuals"]);
    }

    [Fact]
    public void Read_Pbix_ReturnsPagesAndVisuals_AndDatasetReference()
    {
        var svc = NewSvc(out _);
        string pbix = WriteSynthPbix(out _);
        var r = Obj(svc.ReadPbir(pbix));
        Assert.Equal("pbir (pbix)", (string?)r["format"]);
        Assert.Equal(PageA, (string?)r["activePageName"]);
        Assert.NotNull(r["datasetReference"]);
        Assert.Equal(2, (r["pages"] as JsonArray)!.Count);
    }

    [Fact]
    public void GetPage_And_GetVisual_ReturnTheRightFiles()
    {
        var svc = NewSvc(out var store);
        string sid = (string)Obj(svc.ReadPbir(WriteSynthFolder()))["pbirSessionId"]!;

        var page = Obj(svc.GetPage(sid, "Overview"));
        Assert.Equal(PageA, (string?)page["name"]);
        var visuals = page["visuals"] as JsonArray;
        Assert.Single(visuals!);
        Assert.Equal(Vis1, (string?)(visuals![0] as JsonObject)!["name"]);

        var vis = Obj(svc.GetVisual(sid, PageA, Vis1));
        Assert.Equal("card", (string?)vis["visualJson"]!["visual"]!["visualType"]);
    }

    // ================================================================= round-trip (save only changed files)

    [Fact]
    public void RoundTrip_Folder_PreservesUnchangedFiles_AndGuidNames()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        // capture the original bytes of an UNCHANGED file (page B) before any mutation
        string pageBPath = Path.Combine(folder, "definition", "pages", PageB, "page.json");
        byte[] before = File.ReadAllBytes(pageBPath);

        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;
        // mutate page A only (add a visual)
        svc.AddVisual(sid, PageA, "clusteredColumnChart",
            new[] { new FieldBinding("Category", "Sales", "Region", "column") }, 0, 0, 400, 300);

        var save = Obj(svc.Save(sid));
        var changed = (save["changedFiles"] as JsonArray)!.Select(n => (string?)n).ToList();

        // ONLY the new visual.json (+ nothing on page B) was re-emitted; page B is byte-for-byte unchanged.
        Assert.All(changed, c => Assert.DoesNotContain($"pages/{PageB}/", c!));
        Assert.Equal(before, File.ReadAllBytes(pageBPath));

        // re-open and confirm the GUID page names survived
        var reread = Obj(svc.ReadPbir(folder));
        var pages = (reread["pages"] as JsonArray)!.OfType<JsonObject>().Select(p => (string?)p["name"]).ToList();
        Assert.Contains(PageA, pages);
        Assert.Contains(PageB, pages);
    }

    [Fact]
    public void RoundTrip_Pbix_StripsSecurityBindings_PreservesDataModel_AndGuidNames()
    {
        var svc = NewSvc(out _);
        string pbix = WriteSynthPbix(out var dm);
        string sid = (string)Obj(svc.ReadPbir(pbix))["pbirSessionId"]!;

        svc.CreatePage(sid, "New Page");          // mutate -> forces a repack
        var save = Obj(svc.Save(sid));
        Assert.True((bool?)save["securityBindingsStripped"]);

        using var zip = ZipFile.OpenRead(pbix);
        // SecurityBindings dropped
        Assert.Null(zip.GetEntry("SecurityBindings"));
        // DataModel carried through byte-for-byte
        var dme = zip.GetEntry("DataModel");
        Assert.NotNull(dme);
        using (var s = dme!.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); Assert.Equal(dm, ms.ToArray()); }
        // original GUID page survives + the new page landed
        Assert.NotNull(zip.GetEntry($"Report/definition/pages/{PageA}/page.json"));
    }

    // ================================================================= create_pbir_page

    [Fact]
    public void CreatePage_AddsGuidFolder_AndUpdatesPagesOrder()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        var r = Obj(svc.CreatePage(sid, "Trends", 1600, 900));
        string newName = (string?)r["pageName"]!;
        Assert.True(Guid.TryParse(newName, out _), "new page name must be a GUID");
        Assert.Equal(3, (int?)r["pageOrderCount"]);

        svc.Save(sid);
        // the GUID folder + page.json landed on disk
        Assert.True(File.Exists(Path.Combine(folder, "definition", "pages", newName, "page.json")));
        // pages.json pageOrder now ends with the new GUID
        var idx = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "definition", "pages", "pages.json"))) as JsonObject;
        var order = (idx!["pageOrder"] as JsonArray)!.Select(n => (string?)n).ToList();
        Assert.Equal(new[] { PageA, PageB, newName }, order);
    }

    // ================================================================= add_pbir_visual

    [Fact]
    public void AddVisual_WritesVisualJson_WithVisualType_AndProjections()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        var r = Obj(svc.AddVisual(sid, "Overview", "clusteredBarChart",
            new[]
            {
                new FieldBinding("Category", "Sales", "Region", "column"),
                new FieldBinding("Y", "Sales", "Amount", "measure"),
            }, 0, 0, 500, 350, "Sales by Region"));
        string vname = (string?)r["visualName"]!;
        Assert.True(Guid.TryParse(vname, out _));

        var vis = Obj(svc.GetVisual(sid, PageA, vname));
        var visual = vis["visualJson"]!["visual"] as JsonObject;
        Assert.Equal("clusteredBarChart", (string?)visual!["visualType"]);

        // projections carried into the query (the shared builder's output), under both projections and queryState
        var query = visual["query"] as JsonObject;
        Assert.NotNull(query);
        var projections = query!["projections"] as JsonObject;
        Assert.True(projections!.ContainsKey("Category"));
        Assert.True(projections.ContainsKey("Y"));
        var qs = query["queryState"] as JsonObject;
        Assert.True(qs!.ContainsKey("Category"));

        // a title was requested -> the chrome bucket carries it
        Assert.NotNull(visual["visualContainerObjects"]);
    }

    // ================================================================= set_pbir_visual_format

    [Fact]
    public void SetVisualFormat_Scalar_MergesViaSharedEncoder()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        svc.SetVisualFormat(sid, PageA, Vis1, "legend", "show", JsonValue.Create(false), "objects");

        var vis = Obj(svc.GetVisual(sid, PageA, Vis1));
        var objects = vis["visualJson"]!["visual"]!["objects"] as JsonObject;
        // the shared encoder wraps a bool literal as { expr:{ Literal:{ Value:"false" } } }
        var showVal = objects!["legend"]![0]!["properties"]!["show"]!["expr"]!["Literal"]!["Value"];
        Assert.Equal("false", (string?)showVal);
    }

    [Fact]
    public void SetVisualFormat_NestedObject_WrittenVerbatim_WaveM()
    {
        // WAVE M behaviour: a structured PBI JSON value (a measure-bound expr) is written VERBATIM, not stringified.
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        var measureBound = JsonNode.Parse("""{ "measure": "Sales[Color Hex]" }""");
        svc.SetVisualFormat(sid, PageA, Vis1, "dataPoint", "fill", measureBound, "objects");

        var vis = Obj(svc.GetVisual(sid, PageA, Vis1));
        var fill = vis["visualJson"]!["visual"]!["objects"]!["dataPoint"]![0]!["properties"]!["fill"] as JsonObject;
        // EncodeValue lifts { measure:"T[M]" } into a wrapped expr.Measure node (the "drive any property by a measure")
        var prop = fill!["expr"]!["Measure"]!["Property"];
        Assert.Equal("Color Hex", (string?)prop);
    }

    // ================================================================= add_pbir_filter

    [Fact]
    public void AddFilter_Visual_WritesComparisonCondition()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        svc.AddFilter(sid, "visual", PageA, Vis1, "Sales", "Amount", "comparison", "measure", "gt",
            new[] { "0" }, "double");

        var vis = Obj(svc.GetVisual(sid, PageA, Vis1));
        var filters = vis["visualJson"]!["filterConfig"]!["filters"] as JsonArray;
        Assert.Single(filters!);
        var cond = filters![0]!["filter"]!["Where"]![0]!["Condition"] as JsonObject;
        // gt -> ComparisonKind 1; the literal is a typed double "0D"
        Assert.Equal(1, (int?)cond!["Comparison"]!["ComparisonKind"]);
        Assert.Equal("0D", (string?)cond["Comparison"]!["Right"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddFilter_Page_Categorical_WritesInValues()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        svc.AddFilter(sid, "page", "Overview", null, "Sales", "Region", "categorical", "column", null,
            new[] { "North", "South" }, "string");

        var page = Obj(svc.GetPage(sid, PageA));
        var filters = page["page"]!["filterConfig"]!["filters"] as JsonArray;
        Assert.Single(filters!);
        var inNode = filters![0]!["filter"]!["Where"]![0]!["Condition"]!["In"] as JsonObject;
        Assert.NotNull(inNode);
        // two single-quoted string values
        var values = inNode!["Values"] as JsonArray;
        Assert.Equal(2, values!.Count);
        Assert.Equal("'North'", (string?)values[0]![0]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddFilter_Report_Scope_LandsInReportJson()
    {
        var svc = NewSvc(out var store);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        svc.AddFilter(sid, "report", null, null, "Sales", "Year", "categorical", "column", null,
            new[] { "2024" }, "int");
        svc.Save(sid);

        var reportJson = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "report.json"))) as JsonObject;
        var filters = reportJson!["filterConfig"]!["filters"] as JsonArray;
        Assert.Single(filters!);
    }

    // ================================================================= bookmarks

    [Fact]
    public void AddBookmark_WritesBookmarkFile_AndIndexEntry()
    {
        var svc = NewSvc(out _);
        string folder = WriteSynthFolder();
        string sid = (string)Obj(svc.ReadPbir(folder))["pbirSessionId"]!;

        var r = Obj(svc.SetBookmark(sid, "Hide Card", PageA, new[] { Vis1 }, existingName: null));
        string bmName = (string?)r["name"]!;
        Assert.True(Guid.TryParse(bmName, out _));

        svc.Save(sid);
        string bmPath = Path.Combine(folder, "definition", "bookmarks", bmName + ".bookmark.json");
        Assert.True(File.Exists(bmPath));
        var bm = JsonNode.Parse(File.ReadAllText(bmPath)) as JsonObject;
        var mode = bm!["explorationState"]!["sections"]![PageA]!["visualContainers"]![Vis1]!["singleVisual"]!["display"]!["mode"];
        Assert.Equal("hidden", (string?)mode);

        // index entry exists
        var idx = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "definition", "bookmarks", "bookmarks.json"))) as JsonObject;
        Assert.Single((idx!["items"] as JsonArray)!);
    }

    // ================================================================= convert_legacy_to_pbir

    [Fact]
    public void ConvertLegacyToPbir_Explodes_TwoPageLayout_IntoTree()
    {
        var svc = NewSvc(out _);
        // a legacy Report/Layout with 2 sections, the first carrying one visualContainer (stringified config).
        var v1Config = new JsonObject
        {
            ["name"] = "legacyvis1",
            ["layouts"] = new JsonArray { new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                { ["x"] = 5, ["y"] = 6, ["z"] = 0, ["width"] = 200, ["height"] = 150, ["tabOrder"] = 0 } } },
            ["singleVisual"] = new JsonObject
            {
                ["visualType"] = "lineChart",
                ["projections"] = new JsonObject { ["Category"] = new JsonArray { new JsonObject { ["queryRef"] = "Sales.Region" } } },
                ["prototypeQuery"] = new JsonObject { ["Version"] = 2 },
                ["drillFilterOtherVisuals"] = true,
            },
        };
        var layout = new JsonObject
        {
            ["sections"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "ReportSectionAAA", ["displayName"] = "Page One", ["ordinal"] = 0,
                    ["width"] = 1280, ["height"] = 720, ["displayOption"] = 1,
                    ["filters"] = "[]",
                    ["visualContainers"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["x"] = 5, ["y"] = 6, ["z"] = 0, ["width"] = 200, ["height"] = 150,
                            ["config"] = v1Config.ToJsonString(),
                            ["filters"] = "[]",
                        },
                    },
                },
                new JsonObject
                {
                    ["name"] = "ReportSectionBBB", ["displayName"] = "Page Two", ["ordinal"] = 1,
                    ["width"] = 1280, ["height"] = 720, ["displayOption"] = 0,
                    ["filters"] = "[]",
                    ["visualContainers"] = new JsonArray(),
                },
            },
        };
        string layoutPath = Path.Combine(_scratch, "Layout.json");
        File.WriteAllText(layoutPath, layout.ToJsonString(), new UTF8Encoding(false));
        string target = Path.Combine(_scratch, "exploded.Report");

        var r = Obj(svc.ConvertLegacyToPbir(layoutPath, target));
        Assert.True((bool?)r["bestEffort"]);
        Assert.Equal(2, (int?)r["pages"]);

        // the tree exists: pointer + report.json + pages index + 2 page.json + 1 visual.json
        Assert.True(File.Exists(Path.Combine(target, "definition.pbir")));
        Assert.True(File.Exists(Path.Combine(target, "report.json")));
        var pagesIdx = JsonNode.Parse(File.ReadAllText(Path.Combine(target, "definition", "pages", "pages.json"))) as JsonObject;
        var order = (pagesIdx!["pageOrder"] as JsonArray)!;
        Assert.Equal(2, order.Count);

        // re-read the exploded tree through the engine and verify the visual landed with its type + projection
        string sid = (string?)r["pbirSessionId"]!;
        var pages = Obj(svc.ListPages(sid))["pages"] as JsonArray;
        var pageOne = pages!.OfType<JsonObject>().First(p => (string?)p["displayName"] == "Page One");
        Assert.Equal(1, (int?)pageOne["visuals"]);
        string pOneName = (string?)pageOne["name"]!;
        // GUID names were generated (not the legacy ReportSection name)
        Assert.True(Guid.TryParse(pOneName, out _));

        var pageDetail = Obj(svc.GetPage(sid, pOneName));
        string vName = (string?)((pageDetail["visuals"] as JsonArray)![0] as JsonObject)!["name"]!;
        var vis = Obj(svc.GetVisual(sid, pOneName, vName));
        Assert.Equal("lineChart", (string?)vis["visualJson"]!["visual"]!["visualType"]);
        Assert.True((vis["visualJson"]!["visual"]!["query"]!["projections"] as JsonObject)!.ContainsKey("Category"));
    }

    // ================================================================= fault sensitivity: malformed save

    [Fact]
    public void Save_NoPbirPointer_IsRejected()
    {
        // a tree with no *.pbir pointer cannot be saved (ValidateModel guards it).
        var svc = NewSvc(out _);
        string broken = Path.Combine(_scratch, "nopointer-" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in SynthTree())
        {
            if (rel.EndsWith("definition.pbir", StringComparison.OrdinalIgnoreCase)) continue;   // omit the pointer
            string full = Path.Combine(broken, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text, new UTF8Encoding(false));
        }
        string sid = (string)Obj(svc.ReadPbir(broken))["pbirSessionId"]!;
        svc.CreatePage(sid, "Y");   // a mutation to save
        var ex = Record.Exception(() => svc.Save(sid));
        Assert.NotNull(ex);
        Assert.Contains("pbir", ex!.Message, StringComparison.OrdinalIgnoreCase);
    }
}
