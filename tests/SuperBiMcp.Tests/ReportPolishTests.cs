using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL report-polish engine (ReportService set_report_settings, set_page_display,
/// set_visual_format_selector, set_bookmark_options, set_visual_display_mode, add_report_measure) over an
/// in-memory Report/Layout - the same JsonObject root Open() produces. Every transform is pure over the
/// layout, so no live .pbix is needed. Each test asserts the produced PBIX JSON lands in the right place
/// and carries the right shape:
///   - set_report_settings -> expr-literal toggles in layout.config.settings (merged, not clobbered),
///   - set_page_display -> section.displayOption int + section.config.objects.section visibility,
///   - set_visual_format_selector -> a { properties, selector } entry on the object array with the right selector,
///   - set_bookmark_options -> options suppress* + targetVisualNames on the bookmark,
///   - set_visual_display_mode -> singleVisual.display.mode (or removed for normal),
///   - add_report_measure -> a measure in layout.config.modelExtensions entity for the table.
/// Fault-sensitivity: each shape is asserted in its exact location so breaking a rule fails a test.
/// </summary>
public sealed class ReportPolishTests
{
    private const string Page = "Page1";

    // ---- fixture builders: the SAME shape the engine writes (config is a STRINGIFIED blob) ----

    private static JsonObject SimpleContainer(string id, string visualType)
        => Container(id, new JsonObject { ["visualType"] = visualType });

    private static JsonObject Container(string id, JsonObject singleVisual)
    {
        var config = new JsonObject
        {
            ["name"] = id,
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                    { ["x"] = 16, ["y"] = 16, ["z"] = 0, ["width"] = 200, ["height"] = 200, ["tabOrder"] = 0 } },
            },
            ["singleVisual"] = singleVisual,
        };
        return new JsonObject
        {
            ["x"] = 16, ["y"] = 16, ["z"] = 0, ["width"] = 200, ["height"] = 200,
            ["config"] = config.ToJsonString(),
            ["filters"] = "[]",
        };
    }

    private static (ReportService svc, string sid, SessionStore store) NewReport(params JsonObject[] containers)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var arr = new JsonArray();
        foreach (var c in containers) arr.Add(c);

        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 32),
            ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = arr,
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
            ["displayOption"] = 1,
        };
        var root = new JsonObject { ["sections"] = new JsonArray { section }, ["config"] = "{}", ["filters"] = "[]" };

        var session = new ReportSession
        {
            Id = store.NewId("report"),
            PbixPath = "in-memory.pbix",
            Layout = new ReportLayout { Root = root, LayoutPartName = "Report/Layout" },
        };
        store.AddReport(session);
        return (svc, session.Id, store);
    }

    // ---- inspection helpers ----
    private static JsonObject Root(SessionStore store, string sid) => store.GetReport(sid).Layout.Root;
    private static JsonObject Section(SessionStore store, string sid) =>
        ((JsonArray)Root(store, sid)["sections"]!).OfType<JsonObject>().First();

    private static JsonObject ParseObj(string? s) => JsonNode.Parse(s ?? "{}") as JsonObject ?? new JsonObject();

    private static JsonObject VisualConfig(SessionStore store, string sid, string id) =>
        ((JsonArray)Section(store, sid)["visualContainers"]!)
            .Select(vc => ParseObj((string?)vc!["config"]))
            .First(c => (string?)c["name"] == id);

    // a bookmark added straight into layout.config.bookmarks for the options tests.
    private static void SeedBookmark(SessionStore store, string sid, string name)
    {
        var root = Root(store, sid);
        var cfg = ParseObj((string?)root["config"]);
        var bms = (cfg["bookmarks"] as JsonArray) ?? new JsonArray();
        cfg["bookmarks"] = bms;
        bms.Add(new JsonObject
        {
            ["name"] = name,
            ["displayName"] = name,
            ["explorationState"] = new JsonObject { ["version"] = "1.3", ["activeSection"] = (string?)Section(store, sid)["name"] },
        });
        root["config"] = cfg.ToJsonString();
    }

    // ====================================================================== set_report_settings

    [Fact]
    public void SetReportSettings_WritesExprLiteralToggles_IntoLayoutConfigSettings()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetReportSettings(sid, "{\"defaultFilterActionIsDataFilter\":true,\"useEnhancedTooltips\":false,\"exportDataMode\":\"None\",\"pagesPosition\":\"Bottom\"}");

        var settings = (JsonObject)ParseObj((string?)Root(store, sid)["config"])["settings"]!;
        // booleans are bare expr-literal "true"/"false"
        Assert.Equal("true", (string?)settings["defaultFilterActionIsDataFilter"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("false", (string?)settings["useEnhancedTooltips"]!["expr"]!["Literal"]!["Value"]);
        // enums are single-quoted expr-literals
        Assert.Equal("'None'", (string?)settings["exportDataMode"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Bottom'", (string?)settings["pagesPosition"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetReportSettings_Merges_DoesNotClobberExistingToggles()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetReportSettings(sid, "{\"allowChangeFilterTypes\":true}");
        svc.SetReportSettings(sid, "{\"useScaledTooltips\":true}");

        var settings = (JsonObject)ParseObj((string?)Root(store, sid)["config"])["settings"]!;
        // the first toggle survived the second call (merge, not clobber)
        Assert.Equal("true", (string?)settings["allowChangeFilterTypes"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("true", (string?)settings["useScaledTooltips"]!["expr"]!["Literal"]!["Value"]);
    }

    // ====================================================================== set_page_display

    [Fact]
    public void SetPageDisplay_SetsDisplayOptionInt_AndVisibilityOnSectionConfig()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetPageDisplay(sid, Page, "FitToWidth", "HiddenInViewMode");

        // displayOption is the int 1 (FitToWidth) on the section itself
        Assert.Equal(1, Section(store, sid)["displayOption"]!.GetValue<int>());

        // visibility 1 (HiddenInViewMode) is an expr-literal on the section card in the page config
        var vis = ParseObj((string?)Section(store, sid)["config"])
            ["objects"]!["section"]![0]!["properties"]!["visibility"]!["expr"]!["Literal"]!["Value"];
        Assert.Equal("1D", (string?)vis);
    }

    [Fact]
    public void SetPageDisplay_ActualSize_MapsToTwo()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        svc.SetPageDisplay(sid, Page, "ActualSize", null);
        Assert.Equal(2, Section(store, sid)["displayOption"]!.GetValue<int>());
    }

    [Fact]
    public void SetPageDisplay_UnknownDisplayOption_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.SetPageDisplay(sid, Page, "Squish", null));
    }

    // ============================================================ set_visual_format_selector

    [Fact]
    public void SetVisualFormatSelector_Data_WritesEntryWithDataSelector_AndEncodedColour()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "clusteredColumnChart"));

        svc.SetVisualFormatSelector(sid, Page, "a1", "objects", "dataPoint",
            "{\"fill\":\"#FF0000\"}",
            "{\"data\":[{\"scopeId\":\"Anchor\"}]}");

        var arr = (JsonArray)VisualConfig(store, sid, "a1")["singleVisual"]!["objects"]!["dataPoint"]!;
        Assert.Single(arr);
        var entry = (JsonObject)arr[0]!;
        // the selector is a DATA selector carrying the scopeId
        Assert.NotNull(entry["selector"]!["data"]);
        Assert.Equal("'Anchor'", (string?)entry["selector"]!["data"]![0]!["scopeId"]!["expr"]!["Literal"]!["Value"]);
        // the colour property was encoded as a solid colour literal (per-series colour)
        Assert.Equal("'#FF0000'", (string?)entry["properties"]!["fill"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetVisualFormatSelector_Metadata_TargetsAColumnQueryRef()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "tableEx"));

        svc.SetVisualFormatSelector(sid, Page, "a1", "objects", "values",
            "{\"fontColor\":\"#00FF00\"}",
            "{\"metadata\":\"Fact.Sales\"}");

        var entry = (JsonObject)((JsonArray)VisualConfig(store, sid, "a1")["singleVisual"]!["objects"]!["values"]!)[0]!;
        Assert.Equal("Fact.Sales", (string?)entry["selector"]!["metadata"]);
    }

    [Fact]
    public void SetVisualFormatSelector_SameSelectorTwice_UpdatesInPlace_NoDuplicate_AndKeepsDefaultCard()
    {
        // seed a DEFAULT (selector-less) dataPoint card to prove selector entries do not clobber it.
        var sv = new JsonObject
        {
            ["visualType"] = "clusteredColumnChart",
            ["objects"] = new JsonObject
            {
                ["dataPoint"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject() } },
            },
        };
        var (svc, sid, store) = NewReport(Container("a1", sv));

        svc.SetVisualFormatSelector(sid, Page, "a1", "objects", "dataPoint",
            "{\"fill\":\"#111111\"}", "{\"data\":[{\"scopeId\":\"X\"}]}");
        svc.SetVisualFormatSelector(sid, Page, "a1", "objects", "dataPoint",
            "{\"fill\":\"#222222\"}", "{\"data\":[{\"scopeId\":\"X\"}]}");

        var arr = (JsonArray)VisualConfig(store, sid, "a1")["singleVisual"]!["objects"]!["dataPoint"]!;
        // default card (index 0, no selector) + ONE selector entry = 2 (the repeat updated in place)
        Assert.Equal(2, arr.Count);
        Assert.Null(((JsonObject)arr[0]!)["selector"]);   // default card untouched
        var selEntry = arr.OfType<JsonObject>().First(e => e["selector"] != null);
        Assert.Equal("'#222222'", (string?)selEntry["properties"]!["fill"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetVisualFormatSelector_Total_WritesTotalScope()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "pivotTable"));

        svc.SetVisualFormatSelector(sid, Page, "a1", "objects", "subTotals",
            "{\"backColor\":\"#EEEEEE\"}", "{\"total\":true}");

        var entry = (JsonObject)((JsonArray)VisualConfig(store, sid, "a1")["singleVisual"]!["objects"]!["subTotals"]!)[0]!;
        // a total selector targets a total/subtotal scope
        Assert.NotNull(entry["selector"]!["data"]);
        Assert.NotNull(entry["selector"]!["data"]![0]!["scopeId"]);
    }

    // ====================================================================== set_bookmark_options

    [Fact]
    public void SetBookmarkOptions_SetsSuppressToggles_AndTargetVisuals()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedBookmark(store, sid, "BM1");

        svc.SetBookmarkOptions(sid, "BM1", suppressData: true, suppressDisplay: false,
            suppressActiveSection: true, targetVisuals: new[] { "a1", "b2" });

        var cfg = ParseObj((string?)Root(store, sid)["config"]);
        var bm = ((JsonArray)cfg["bookmarks"]!).OfType<JsonObject>().First(b => (string?)b["name"] == "BM1");
        var opt = (JsonObject)bm["options"]!;
        Assert.True((bool)opt["suppressData"]!);
        Assert.False((bool)opt["suppressDisplay"]!);
        Assert.True((bool)opt["suppressActiveSection"]!);
        Assert.True((bool)opt["applyOnlyToTargetVisuals"]!);
        var tvn = (JsonArray)opt["targetVisualNames"]!;
        Assert.Equal(2, tvn.Count);
        Assert.Equal("a1", (string?)tvn[0]);
    }

    [Fact]
    public void SetBookmarkOptions_EmptyTargets_ClearsScope()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedBookmark(store, sid, "BM1");

        svc.SetBookmarkOptions(sid, "BM1", null, null, null, new[] { "a1" });
        svc.SetBookmarkOptions(sid, "BM1", null, null, null, System.Array.Empty<string>());

        var cfg = ParseObj((string?)Root(store, sid)["config"]);
        var opt = (JsonObject)((JsonArray)cfg["bookmarks"]!).OfType<JsonObject>().First()["options"]!;
        Assert.Null(opt["targetVisualNames"]);
        Assert.False((bool)opt["applyOnlyToTargetVisuals"]!);
    }

    [Fact]
    public void SetBookmarkOptions_UnknownBookmark_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.SetBookmarkOptions(sid, "Nope", true, null, null, null));
    }

    // ====================================================================== set_visual_display_mode

    [Fact]
    public void SetVisualDisplayMode_Spotlight_SetsDisplayMode()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetVisualDisplayMode(sid, Page, "a1", "spotlight");

        Assert.Equal("spotlight", (string?)VisualConfig(store, sid, "a1")["singleVisual"]!["display"]!["mode"]);
    }

    [Fact]
    public void SetVisualDisplayMode_Normal_RemovesTheOverride()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetVisualDisplayMode(sid, Page, "a1", "maximize");
        Assert.Equal("maximize", (string?)VisualConfig(store, sid, "a1")["singleVisual"]!["display"]!["mode"]);

        svc.SetVisualDisplayMode(sid, Page, "a1", "normal");
        Assert.Null(VisualConfig(store, sid, "a1")["singleVisual"]!["display"]);
    }

    [Fact]
    public void SetVisualDisplayMode_UnknownMode_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.SetVisualDisplayMode(sid, Page, "a1", "wobble"));
    }

    // ====================================================================== add_report_measure

    [Fact]
    public void AddReportMeasure_WritesMeasureIntoModelExtensions_ForTheTable()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.AddReportMeasure(sid, "Fact", "Margin %", "DIVIDE([Profit],[Sales])", "0.0%", "KPIs");

        var ext = (JsonObject)((JsonArray)ParseObj((string?)Root(store, sid)["config"])["modelExtensions"]!)[0]!;
        var entity = ((JsonArray)ext["entities"]!).OfType<JsonObject>().First(e => (string?)e["name"] == "Fact");
        Assert.Equal("Fact", (string?)entity["extends"]);
        var m = ((JsonArray)entity["measures"]!).OfType<JsonObject>().First(x => (string?)x["name"] == "Margin %");
        Assert.Equal("DIVIDE([Profit],[Sales])", (string?)m["expression"]);
        Assert.Equal("0.0%", (string?)m["formatString"]);
        Assert.Equal("KPIs", (string?)m["displayFolder"]);
    }

    [Fact]
    public void AddReportMeasure_SameName_UpdatesInPlace_NoDuplicate()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.AddReportMeasure(sid, "Fact", "M1", "1", null, null);
        svc.AddReportMeasure(sid, "Fact", "M1", "2", null, null);

        var ext = (JsonObject)((JsonArray)ParseObj((string?)Root(store, sid)["config"])["modelExtensions"]!)[0]!;
        var measures = (JsonArray)((JsonArray)ext["entities"]!).OfType<JsonObject>().First()["measures"]!;
        Assert.Single(measures);
        Assert.Equal("2", (string?)((JsonObject)measures[0]!)["expression"]);
    }

    [Fact]
    public void AddReportMeasure_MissingExpression_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.AddReportMeasure(sid, "Fact", "M1", "", null, null));
    }
}
