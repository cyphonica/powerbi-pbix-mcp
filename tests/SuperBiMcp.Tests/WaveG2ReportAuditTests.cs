using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave G2 report-side audits: the shared wireframe geometry checker (pure + legacy collector + PBIR
/// collector), theme compliance / colour inventory / recolour, filter + settings + slicer read-back
/// symmetry (whatever the write tools produce must read back), document_report, the pbix_doctor
/// container scan and the DataMashup credential audit (presence only - never the value). All in-memory
/// or against synthetic zips in a temp dir - no live engine, no Desktop.
/// </summary>
public sealed class WaveG2ReportAuditTests
{
    // ---- fixture builders (mirrors TidySlicerLayoutV2Tests) -------------------------------------

    private static JsonObject Lit(string value) =>
        new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = value } } };

    private static JsonObject Container(string id, string visualType, double x, double y, double w, double h,
        string? title = null, double z = 0, JsonObject? objects = null, JsonObject? vcObjects = null,
        JsonObject? projections = null, bool hidden = false)
    {
        var sv = new JsonObject { ["visualType"] = visualType, ["drillFilterOtherVisuals"] = true };
        var vco = vcObjects ?? new JsonObject();
        if (title != null)
            vco["title"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                { ["text"] = Lit($"'{title}'"), ["show"] = Lit("true") } } };
        if (vco.Count > 0) sv["vcObjects"] = vco;
        if (objects != null) sv["objects"] = objects;
        if (projections != null) sv["projections"] = projections;
        if (hidden) sv["display"] = new JsonObject { ["mode"] = "hidden" };

        var config = new JsonObject
        {
            ["name"] = id,
            ["layouts"] = new JsonArray { new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                { ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = w, ["height"] = h, ["tabOrder"] = 0 } } },
            ["singleVisual"] = sv,
        };
        return new JsonObject
        {
            ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = w, ["height"] = h,
            ["config"] = config.ToJsonString(),
            ["filters"] = "[]",
        };
    }

    private static JsonObject Section(string displayName, int ordinal, params JsonObject[] containers)
    {
        var arr = new JsonArray();
        foreach (var c in containers) arr.Add(c);
        return new JsonObject
        {
            ["name"] = "ReportSection" + ordinal + new string('c', 18),
            ["displayName"] = displayName, ["ordinal"] = ordinal,
            ["visualContainers"] = arr,
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
        };
    }

    private static (ReportService svc, string sid, SessionStore store) NewReport(params JsonObject[] sections)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);
        var secArr = new JsonArray();
        foreach (var s in sections) secArr.Add(s);
        var root = new JsonObject { ["sections"] = secArr, ["config"] = "{}", ["filters"] = "[]" };
        var session = new ReportSession
        {
            Id = store.NewId("report"),
            PbixPath = "in-memory.pbix",
            Layout = new ReportLayout { Root = root, LayoutPartName = "Report/Layout" },
        };
        store.AddReport(session);
        return (svc, session.Id, store);
    }

    private static JsonObject Obj(object result) => (JsonObject)JsonSerializer.SerializeToNode(result)!;

    private static IEnumerable<JsonObject> Violations(JsonObject r) =>
        ((JsonArray)r["violations"]!).OfType<JsonObject>();

    // ================================================================= wireframe: pure checker

    [Fact]
    public void Wireframe_Detects_Overlap_OffCanvas_Tiny_And_ZOrder()
    {
        var page = new WirePage("P", 1280, 720, new List<WireVisual>
        {
            new("slicer1", "slicer", 0, 40, 200, 60, 1, false),
            new("chart1", "barChart", 100, 50, 400, 300, 5, false),      // overlaps the slicer, higher z
            new("chart2", "lineChart", 1200, 600, 300, 200, 0, false),   // hangs off right + bottom
            new("debris", "card", 10, 700, 2, 2, 0, false),              // tiny
        });

        var r = Obj(WireframeAuditor.Audit(new[] { page }));

        Assert.True((bool)r["ok"]!);
        var kinds = Violations(r).Select(v => (string)v["kind"]!).ToList();
        Assert.Contains("overlap", kinds);
        Assert.Contains("off-canvas", kinds);
        Assert.Contains("tiny-visual", kinds);
        Assert.Contains("z-order", kinds);
        Assert.Equal("review", (string)r["verdict"]!);

        // every violation names an existing fixer tool
        foreach (var v in Violations(r))
            Assert.False(string.IsNullOrWhiteSpace((string?)v["suggestedFix"]));
        var z = Violations(r).First(v => (string)v["kind"]! == "z-order");
        Assert.Contains("set_visual_z_order", (string)z["suggestedFix"]!);
    }

    [Fact]
    public void Wireframe_CleanPage_Passes_WithStats()
    {
        var page = new WirePage("P", 1280, 720, new List<WireVisual>
        {
            new("a", "card", 16, 16, 200, 120, 0, false),
            new("b", "card", 232, 16, 200, 120, 0, false),
            new("c", "barChart", 16, 152, 416, 300, 0, false),
        });

        var r = Obj(WireframeAuditor.Audit(new[] { page }));

        Assert.Equal(0, (int)r["violationCount"]!);
        Assert.Equal("pass", (string)r["verdict"]!);
        var stats = (JsonObject)((JsonArray)r["pages"]!)[0]!;
        Assert.Equal(16, (double)stats["margins"]!["left"]!, 1);
        Assert.Equal(16, (double)stats["margins"]!["top"]!, 1);
        Assert.Equal(16, (double)stats["gaps"]!["median"]!, 1);          // the a-b gap
    }

    [Fact]
    public void Wireframe_DecorativeOverlap_And_HiddenVisuals_AreIgnored()
    {
        var page = new WirePage("P", 1280, 720, new List<WireVisual>
        {
            new("panel", "shape", 0, 0, 1280, 720, 0, false),            // full-page background panel
            new("chart", "barChart", 100, 100, 400, 300, 1, false),
            new("ghost", "card", 100, 100, 400, 300, 2, true),           // hidden - never linted
        });

        var r = Obj(WireframeAuditor.Audit(new[] { page }));

        Assert.Equal(0, (int)r["violationCount"]!);
    }

    // ================================================================= wireframe: legacy collector

    [Fact]
    public void ValidateWireframe_Legacy_CollectsGeometry_AndFlagsOverlap()
    {
        var (svc, sid, _) = NewReport(Section("Overview", 0,
            Container("v1", "barChart", 100, 100, 400, 300, title: "Sales"),
            Container("v2", "lineChart", 300, 200, 400, 300, title: "Trend"),
            Container("v3", "card", -20, 40, 200, 100)));

        var r = Obj(svc.ValidateWireframe(sid));

        Assert.Equal(1, (int)r["pagesScanned"]!);
        var kinds = Violations(r).Select(v => (string)v["kind"]!).ToList();
        Assert.Contains("overlap", kinds);
        Assert.Contains("off-canvas", kinds);
        // labels use the visible title when present
        Assert.Contains(Violations(r), v => ((string)v["visual"]!).Contains("Sales"));
    }

    [Fact]
    public void ValidateWireframe_Legacy_PageScope_And_UnknownPage_Throws()
    {
        var (svc, sid, _) = NewReport(
            Section("P1", 0, Container("a", "card", 0, 0, 100, 100)),
            Section("P2", 1, Container("b", "card", -50, 0, 100, 100)));

        var all = Obj(svc.ValidateWireframe(sid));
        Assert.Equal(2, (int)all["pagesScanned"]!);

        var one = Obj(svc.ValidateWireframe(sid, "P1"));
        Assert.Equal(1, (int)one["pagesScanned"]!);
        Assert.Equal(0, (int)one["violationCount"]!);                    // the off-canvas visual is on P2

        Assert.Throws<InvalidOperationException>(() => svc.ValidateWireframe(sid, "NoSuchPage"));
    }

    // ================================================================= wireframe: PBIR collector

    [Fact]
    public void ValidateWireframe_Pbir_RunsSameChecker_OverTheFolderReader()
    {
        using var work = Fixtures.NewWorkDir();
        string root = work.Path;
        void Put(string rel, string json)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json, new UTF8Encoding(false));
        }
        Put("definition.pbir", "{\"version\":\"1.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../Model\"}}}");
        Put("report.json", "{}");
        Put("definition/pages/pages.json", "{\"pageOrder\":[\"p1\"],\"activePageName\":\"p1\"}");
        Put("definition/pages/p1/page.json",
            "{\"name\":\"p1\",\"displayName\":\"Overview\",\"displayOption\":\"FitToPage\",\"width\":1280,\"height\":720}");
        Put("definition/pages/p1/visuals/v1/visual.json",
            "{\"name\":\"v1\",\"position\":{\"x\":100,\"y\":100,\"z\":0,\"width\":400,\"height\":300},\"visual\":{\"visualType\":\"barChart\"}}");
        Put("definition/pages/p1/visuals/v2/visual.json",
            "{\"name\":\"v2\",\"position\":{\"x\":300,\"y\":200,\"z\":1,\"width\":400,\"height\":300},\"visual\":{\"visualType\":\"lineChart\"}}");
        Put("definition/pages/p1/visuals/v3/visual.json",
            "{\"name\":\"v3\",\"isHidden\":true,\"position\":{\"x\":0,\"y\":0,\"z\":2,\"width\":50,\"height\":50},\"visual\":{\"visualType\":\"card\"}}");

        var store = new SessionStore();
        var pbir = new PbirService(store, NullLogger<PbirService>.Instance);
        var opened = Obj(pbir.ReadPbir(root));
        string sid = (string)opened["pbirSessionId"]!;

        var r = Obj(pbir.ValidateWireframe(sid));

        Assert.Equal(1, (int)r["pagesScanned"]!);
        Assert.Contains(Violations(r), v => (string)v["kind"]! == "overlap");
        // the hidden visual is excluded from the lint and counted as hidden in the stats
        var stats = (JsonObject)((JsonArray)r["pages"]!)[0]!;
        Assert.Equal(2, (int)stats["visuals"]!);
        Assert.Equal(1, (int)stats["hidden"]!);
        Assert.Equal("Overview", (string)stats["page"]!);
    }

    // ================================================================= theme compliance

    private static JsonObject TestTheme() => new JsonObject
    {
        ["name"] = "TestTheme",
        ["dataColors"] = new JsonArray { "#111111", "#222222" },
        ["background"] = "#FFFFFF",
        ["foreground"] = "#000000",
        ["textClasses"] = new JsonObject
        {
            ["title"] = new JsonObject { ["fontFace"] = "Segoe UI Semibold", ["fontSize"] = 14, ["color"] = "#000000" },
        },
        ["visualStyles"] = new JsonObject
        {
            ["*"] = new JsonObject { ["*"] = new JsonObject
            {
                ["background"] = new JsonArray { new JsonObject { ["show"] = true } },
                ["border"] = new JsonArray { new JsonObject { ["show"] = true } },
            } },
        },
    };

    private static (ReportService svc, string sid, SessionStore store) ThemedReport(params JsonObject[] containers)
    {
        var (svc, sid, store) = NewReport(Section("Main", 0, containers));
        var root = store.GetReport(sid).Layout.Root;
        var cfg = new JsonObject { ["themeCollection"] = new JsonObject
            { ["customTheme"] = TestTheme(), ["customThemeAdded"] = true } };
        root["config"] = cfg.ToJsonString();
        return (svc, sid, store);
    }

    private static JsonObject FillObjects(string colourLiteral) => new JsonObject
    {
        ["dataPoint"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
        {
            ["fill"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = Lit($"'{colourLiteral}'") } },
        } } },
    };

    [Fact]
    public void ThemeCompliance_Flags_OffPalette_FrozenPalette_Font_And_CardOverrides()
    {
        var offPalette = Container("v1", "barChart", 0, 0, 300, 200, objects: FillObjects("#ABCDEF"));
        var frozen = Container("v2", "columnChart", 320, 0, 300, 200, objects: FillObjects("#111111"));
        var fontOverride = Container("v3", "card", 640, 0, 300, 200, vcObjects: new JsonObject
        {
            ["title"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                { ["text"] = Lit("'T'"), ["fontFamily"] = Lit("'Arial'"), ["fontSize"] = Lit("20D") } } },
            ["background"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                { ["color"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = Lit("'#EEEEEE'") } } } } },
        });
        var (svc, sid, _) = ThemedReport(offPalette, frozen, fontOverride);

        var r = Obj(svc.AuditThemeCompliance(sid));

        Assert.True((bool)r["hasCustomTheme"]!);
        Assert.Equal("TestTheme", (string)r["themeName"]!);
        var kinds = Violations(r).Select(v => (string)v["kind"]!).ToList();
        Assert.Contains("off-palette-colour", kinds);
        Assert.Contains("hard-coded-theme-colour", kinds);               // #111111 duplicates the palette
        Assert.Contains("font-override", kinds);                         // Arial vs Segoe UI Semibold
        Assert.Contains("font-size-override", kinds);                    // 20 vs 14
        Assert.Contains("card-style-override", kinds);                   // per-visual background override
        Assert.Contains(Violations(r), v => ((string?)v["detail"] ?? "").Contains("#ABCDEF"));
        Assert.Equal("review", (string)r["verdict"]!);
    }

    [Fact]
    public void ThemeCompliance_NoTheme_ListsHardCodedColours_AsInventory()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0,
            Container("v1", "barChart", 0, 0, 300, 200, objects: FillObjects("#ABCDEF"))));

        var r = Obj(svc.AuditThemeCompliance(sid));

        Assert.False((bool)r["hasCustomTheme"]!);
        Assert.Contains(Violations(r), v => (string)v["kind"]! == "hard-coded-colour");
    }

    // ================================================================= colour inventory + recolour

    [Fact]
    public void ExtractReportColors_Inventories_Visuals_And_Theme_WithLocations()
    {
        var (svc, sid, _) = ThemedReport(
            Container("v1", "barChart", 0, 0, 300, 200, objects: FillObjects("#ABCDEF")));

        var r = Obj(svc.ExtractReportColors(sid));

        var colors = ((JsonArray)r["colors"]!).OfType<JsonObject>().ToList();
        var abc = colors.First(c => (string)c["color"]! == "#ABCDEF");
        Assert.Contains(((JsonArray)abc["locations"]!).OfType<JsonNode>(),
            l => ((string)l!).Contains("Main/") && ((string)l!).Contains("objects.dataPoint"));
        var pal = colors.First(c => (string)c["color"]! == "#111111");
        Assert.Contains(((JsonArray)pal["locations"]!).OfType<JsonNode>(),
            l => ((string)l!).StartsWith("theme.dataColors"));
        Assert.True((int)r["distinctColors"]! >= 4);
    }

    [Fact]
    public void RecolorReport_Replaces_QuotedVisualLiterals_And_PlainThemeSlots_ThenIsIdempotent()
    {
        var (svc, sid, store) = ThemedReport(
            Container("v1", "barChart", 0, 0, 300, 200, objects: FillObjects("#ABCDEF")));

        var r = Obj(svc.RecolorReport(sid, "{\"#ABCDEF\":\"#123456\",\"#111111\":\"#999999\"}"));

        Assert.True((int)r["replaced"]! >= 2);
        Assert.True(store.GetReport(sid).Dirty);

        // the visual literal keeps its quoted spelling; the theme slot stays plain
        var root = store.GetReport(sid).Layout.Root;
        var vc = (JsonObject)((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!)[0]!;
        Assert.Contains("'#123456'", (string)vc["config"]!);
        Assert.DoesNotContain("#ABCDEF", (string)vc["config"]!);
        var theme = Obj(svc.ReadTheme(sid));
        Assert.Contains("#999999", (string)theme["themeJson"]!);
        Assert.DoesNotContain("#111111", (string)theme["themeJson"]!);

        // second run finds nothing left to replace
        var again = Obj(svc.RecolorReport(sid, "{\"#ABCDEF\":\"#123456\"}"));
        Assert.Equal(0, (int)again["replaced"]!);
    }

    [Fact]
    public void RecolorReport_RejectsMalformedMap()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0));
        Assert.Throws<ArgumentException>(() => svc.RecolorReport(sid, "{\"notacolour\":\"#123456\"}"));
        Assert.Throws<ArgumentException>(() => svc.RecolorReport(sid, "{}"));
    }

    [Fact]
    public void ColourLiteral_Normalisation_AcceptsQuotedAndAlpha_RejectsJunk()
    {
        Assert.Equal("#AABBCC", ThemeAuditor.AsColourLiteral("#aabbcc"));
        Assert.Equal("#AABBCC", ThemeAuditor.AsColourLiteral("'#AABBCC'"));
        Assert.Equal("#AABBCCDD", ThemeAuditor.AsColourLiteral("#AABBCCDD"));
        Assert.Null(ThemeAuditor.AsColourLiteral("#ABC"));               // shorthand is not a PBI literal
        Assert.Null(ThemeAuditor.AsColourLiteral("red"));
        Assert.Null(ThemeAuditor.AsColourLiteral("#GGGGGG"));
        Assert.Null(ThemeAuditor.AsColourLiteral(null));
    }

    // ================================================================= filter read-back symmetry

    [Fact]
    public void GetReportFilters_ReadsBack_WhatTheWriteToolsWrote()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0,
            Container("v1", "barChart", 0, 0, 300, 200)));

        svc.AddReportFilter(sid, "Products", "Category", "categorical", "column", null, new[] { "A", "B" }, "string");
        svc.AddPageFilter(sid, "Main", "Sales", "Amount", "comparison", "measure", "gt", new[] { "100" }, "int");
        svc.AddVisualFilter(sid, "Main", "v1", "Sales", "Qty", "column", "isnotblank", null, "int");
        svc.AddTopNFilter(sid, "report", null, null, "Products", "Category", 5, "Sales", "Total Sales", "top");
        svc.AddRelativeDateFilter(sid, "page", "Main", null, "Dates", "Date", "Last", 3, "Months", true, false);

        var r = Obj(svc.GetReportFilters(sid));
        var filters = ((JsonArray)r["filters"]!).OfType<JsonObject>().ToList();
        Assert.Equal(5, (int)r["filterCount"]!);

        var cat = filters.First(f => (string?)f["field"] == "Category" && (string?)f["type"] == "Categorical");
        Assert.Equal("report", (string)cat["scope"]!);
        Assert.Equal("Products", (string)cat["table"]!);
        Assert.Equal("in", (string)cat["condition"]!["kind"]!);
        Assert.Equal(new[] { "A", "B" },
            ((JsonArray)cat["condition"]!["values"]!).Select(v => (string)v!).ToArray());

        var cmp = filters.First(f => (string?)f["field"] == "Amount");
        Assert.Equal("page", (string)cmp["scope"]!);
        Assert.Equal("Main", (string)cmp["page"]!);
        Assert.Equal("measure", (string)cmp["fieldKind"]!);
        Assert.Equal("gt", (string)cmp["condition"]!["op"]!);
        Assert.Equal("100", (string)cmp["condition"]!["value"]!);

        var blank = filters.First(f => (string?)f["field"] == "Qty");
        Assert.Equal("visual", (string)blank["scope"]!);
        Assert.Equal("v1", (string)blank["visual"]!);
        Assert.Equal("not", (string)blank["condition"]!["kind"]!);       // isnotblank = Not(eq null)
        Assert.Equal("isblank", (string)blank["condition"]!["inner"]!["op"]!);

        var top = filters.First(f => (string?)f["type"] == "TopN");
        Assert.Equal(5, (int)top["condition"]!["n"]!);
        Assert.Equal("top", (string)top["condition"]!["direction"]!);
        Assert.Equal("Total Sales", (string)top["condition"]!["byField"]!);

        var rel = filters.First(f => (string?)f["type"] == "RelativeDate");
        Assert.Equal("Last", (string)rel["condition"]!["mode"]!);
        Assert.Equal(3, (int)rel["condition"]!["count"]!);
        Assert.Equal("Months", (string)rel["condition"]!["unit"]!);
        Assert.True((bool)rel["condition"]!["includeCurrent"]!);
    }

    [Fact]
    public void GetReportFilters_EmptyReport_ReturnsZero()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0));
        var r = Obj(svc.GetReportFilters(sid));
        Assert.Equal(0, (int)r["filterCount"]!);
    }

    [Fact]
    public void UnwrapLiteral_HandlesQuoted_Numeric_And_Null()
    {
        Assert.Equal("Widget", ReportService.UnwrapLiteral("'Widget'"));
        Assert.Equal("O'Brien", ReportService.UnwrapLiteral("'O''Brien'"));
        Assert.Equal("10", ReportService.UnwrapLiteral("10L"));
        Assert.Equal("1.5", ReportService.UnwrapLiteral("1.5D"));
        Assert.Equal("null", ReportService.UnwrapLiteral("null"));
        Assert.Equal("true", ReportService.UnwrapLiteral("true"));
    }

    // ================================================================= settings + slicer read-back

    [Fact]
    public void GetReportSettings_ReadsBack_EncodedToggles()
    {
        var (svc, sid, _) = ThemedReport();
        svc.SetReportSettings(sid, "{\"hideVisualContainerHeader\":true,\"exportDataMode\":\"None\"}");

        var r = Obj(svc.GetReportSettings(sid));

        Assert.Equal(2, (int)r["settingCount"]!);
        Assert.Equal("true", (string)r["settings"]!["hideVisualContainerHeader"]!);
        Assert.Equal("None", (string)r["settings"]!["exportDataMode"]!);
        Assert.True((bool)r["hasCustomTheme"]!);
        Assert.Equal("TestTheme", (string)r["themeName"]!);
    }

    [Fact]
    public void GetSlicerDefaults_ReadsBack_SetSlicerSelection()
    {
        var projections = new JsonObject
        {
            ["Values"] = new JsonArray { new JsonObject { ["queryRef"] = "Products.Category" } },
        };
        var (svc, sid, _) = NewReport(Section("Main", 0,
            Container("s1", "slicer", 0, 0, 200, 60, title: "Category", projections: projections),
            Container("v1", "barChart", 0, 100, 400, 300)));

        svc.SetSlicerSelection(sid, "Main", "s1", singleSelect: true, defaultValue: "Trance");

        var r = Obj(svc.GetSlicerDefaults(sid));

        Assert.Equal(1, (int)r["slicerCount"]!);                          // the chart is not a slicer
        var s = (JsonObject)((JsonArray)r["slicers"]!)[0]!;
        Assert.Equal("s1", (string)s["name"]!);
        Assert.Equal("Products[Category]", (string)s["boundField"]!);
        Assert.True((bool)s["strictSingleSelect"]!);
        Assert.Equal(new[] { "Trance" }, ((JsonArray)s["defaultValues"]!).Select(v => (string)v!).ToArray());
    }

    // ================================================================= document_report

    [Fact]
    public void DocumentReport_RendersPagesVisualsAndBindings_AndWritesFile()
    {
        using var work = Fixtures.NewWorkDir();
        var projections = new JsonObject
        {
            ["Values"] = new JsonArray { new JsonObject { ["queryRef"] = "Sales.Total Sales" } },
        };
        var (svc, sid, _) = NewReport(Section("Overview", 0,
            Container("card1", "card", 16, 16, 200, 120, title: "KPI", projections: projections)));
        string outPath = work.File("report-doc.md");

        var r = Obj(svc.DocumentReport(sid, outPath));

        Assert.Equal(1, (int)r["pages"]!);
        Assert.Equal(1, (int)r["visuals"]!);
        string md = (string)r["markdown"]!;
        Assert.Contains("## Page: Overview", md);
        Assert.Contains("card", md);
        Assert.Contains("Values: Sales.Total Sales", md);
        Assert.Contains("KPI", md);
        Assert.True(File.Exists(outPath));
        Assert.Equal(md, File.ReadAllText(outPath));
    }

    // ================================================================= pbix_doctor

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var s = e.Open();
        s.Write(bytes, 0, bytes.Length);
    }

    private static string BuildSyntheticPbix(string dir, bool healthy)
    {
        string path = Path.Combine(dir, healthy ? "healthy.pbix" : "broken.pbix");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        if (healthy)
        {
            AddEntry(zip, "[Content_Types].xml",
                new UTF8Encoding(false).GetBytes("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />"));
            AddEntry(zip, "Version", new UnicodeEncoding(false, false).GetBytes("1.28"));
            AddEntry(zip, "DataModel", new byte[] { 1, 2, 3, 4, 5 });
            AddEntry(zip, "Report/Layout",
                new UnicodeEncoding(false, false).GetBytes("{\"sections\":[]}"));
            AddEntry(zip, "DataMashup",
                ReportService.BuildMashupContainerForTest("section Section1; shared Q = 1;", withBindings: true));
            AddEntry(zip, "SecurityBindings", new byte[] { 9, 9 });
            AddEntry(zip, "JunkPart", Array.Empty<byte>());              // zero-byte AND unexpected
        }
        else
        {
            // no [Content_Types].xml, no model part, no report part
            AddEntry(zip, "SomethingElse", new byte[] { 1 });
        }
        return path;
    }

    [Fact]
    public void PbixDoctor_Healthy_ReportsWarnings_ForStaleBindings_And_ZeroByteParts()
    {
        using var work = Fixtures.NewWorkDir();
        string path = BuildSyntheticPbix(work.Path, healthy: true);

        var r = Obj(PbixDoctor.Run(path));

        Assert.True((bool)r["ok"]!);
        Assert.Equal("legacy", (string)r["reportFormat"]!);
        Assert.Equal("1.28", (string)r["version"]!);
        Assert.Equal(0, (int)r["failCount"]!);
        Assert.True((int)r["warnCount"]! >= 3);                          // bindings + SecurityBindings + zero-byte
        Assert.Equal("review", (string)r["verdict"]!);

        var checks = ((JsonArray)r["checks"]!).OfType<JsonObject>()
            .ToDictionary(c => (string)c["id"]!, c => (string)c["status"]!);
        Assert.Equal("pass", checks["content-types"]);
        Assert.Equal("pass", checks["data-model"]);
        Assert.Equal("pass", checks["layout-parses"]);
        Assert.Equal("pass", checks["datamashup"]);
        Assert.Equal("warn", checks["permission-bindings"]);
        Assert.Equal("warn", checks["security-bindings"]);
        Assert.Equal("warn", checks["zero-byte-parts"]);
        Assert.Equal("pass", checks["truncated-parts"]);
        Assert.Equal("pass", checks["duplicate-parts"]);
        Assert.Equal("info", checks["unexpected-parts"]);                // JunkPart
    }

    [Fact]
    public void PbixDoctor_Broken_Fails_OnMissingCoreParts()
    {
        using var work = Fixtures.NewWorkDir();
        string path = BuildSyntheticPbix(work.Path, healthy: false);

        var r = Obj(PbixDoctor.Run(path));

        Assert.True((int)r["failCount"]! >= 3);                          // content types + model + report
        Assert.Equal("fail", (string)r["verdict"]!);
    }

    [Fact]
    public void PbixDoctor_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => PbixDoctor.Run(@"D:\does\not\exist.pbix"));
    }

    // ================================================================= DataMashup credential audit

    private const string CredentialM =
        "section Section1;\n" +
        "shared CleanQuery = let Source = 1 in Source;\n" +
        "shared DbQuery = let Source = Sql.Database(\"srv\", \"db\", " +
        "[ConnectionString=\"Server=srv;User Id=sa;Password=Hunter2SecretValue;\"]) in Source;\n" +
        "shared ApiQuery = let Source = Web.Contents(\"https://x\", [Headers=[Authorization=\"Bearer abc\"]]) in Source;\n";

    [Fact]
    public void DetectCredentialIndicators_Finds_Password_And_Authorization_ByQueryAndLine()
    {
        var found = ReportService.DetectCredentialIndicators(CredentialM)
            .Select(o => Obj(o)).ToList();

        Assert.Contains(found, f => (string)f["indicator"]! == "password" && (string)f["query"]! == "DbQuery");
        Assert.Contains(found, f => (string)f["indicator"]! == "authorization" && (string)f["query"]! == "ApiQuery");
        Assert.All(found, f => Assert.True((int)f["line"]! >= 1));
        Assert.DoesNotContain(found, f => (string)f["query"]! == "CleanQuery");
    }

    [Fact]
    public void DetectCredentialIndicators_CleanM_FindsNothing()
    {
        Assert.Empty(ReportService.DetectCredentialIndicators(
            "section Section1;\nshared Q = let Source = Csv.Document(File.Contents(\"c:\\\\a.csv\")) in Source;\n"));
    }

    [Fact]
    public void AuditDataMashupCredentials_ReportsPresence_ButNeverTheSecretValue()
    {
        using var work = Fixtures.NewWorkDir();
        string path = Path.Combine(work.Path, "creds.pbix");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddEntry(zip, "DataMashup", ReportService.BuildMashupContainerForTest(CredentialM, withBindings: true));
        }
        var svc = new ReportService(new SessionStore(), NullLogger<ReportService>.Instance);

        var result = svc.AuditDataMashupCredentials(path);
        var r = Obj(result);

        Assert.True((bool)r["hasDataMashup"]!);
        Assert.True((bool)r["permissionBindingsPresent"]!);
        Assert.True((int)r["indicatorCount"]! >= 2);
        // the hard guarantee: the secret VALUE never appears anywhere in the serialised result
        Assert.DoesNotContain("Hunter2SecretValue", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void AuditDataMashupCredentials_NoMashup_IsCleanResult()
    {
        using var work = Fixtures.NewWorkDir();
        string path = Path.Combine(work.Path, "nomashup.pbix");
        using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            AddEntry(zip, "DataModel", new byte[] { 1 });
        }
        var svc = new ReportService(new SessionStore(), NullLogger<ReportService>.Instance);

        var r = Obj(svc.AuditDataMashupCredentials(path));

        Assert.False((bool)r["hasDataMashup"]!);
        Assert.Equal(0, (int)r["indicatorCount"]!);
    }
}
