using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL remaining report-layer engine (ReportService: AddVisualCalculation, AddSparkline,
/// SetDrillthroughFields, SetDrilldown, AddNavigator, the granular theme tools, SetAltText, SetTabOrder,
/// SetPersonalization, RegisterCustomVisual, SetPageWallpaper) over an in-memory Report/Layout - the same
/// JsonObject root Open() produces. Every transform is pure over the layout, so no live .pbix is needed.
/// Each test asserts the produced PBIX JSON lands in the right place and carries the right shape, and the
/// fault-sensitivity tests prove a wrong location / value fails.
/// </summary>
public sealed class ReportWaveHTests
{
    private const string Page = "Page1";

    // ---- fixture builders (the SAME shape the engine writes: config is a STRINGIFIED blob) ----

    private static JsonObject SimpleContainer(string id, string visualType)
        => Container(id, new JsonObject { ["visualType"] = visualType });

    private static JsonObject ContainerWithProjections(string id, string visualType)
        => Container(id, new JsonObject
        {
            ["visualType"] = visualType,
            ["projections"] = new JsonObject { ["Values"] = new JsonArray { new JsonObject { ["queryRef"] = "Fact.Sales" } } },
        });

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
    private static JsonArray ParseArr(string? s) => JsonNode.Parse(s ?? "[]") as JsonArray ?? new JsonArray();
    private static JsonObject VisualConfig(SessionStore store, string sid, string id) =>
        ((JsonArray)Section(store, sid)["visualContainers"]!)
            .Select(vc => ParseObj((string?)vc!["config"]))
            .First(c => (string?)c["name"] == id);
    private static JsonObject SingleVisual(SessionStore store, string sid, string id) =>
        (JsonObject)VisualConfig(store, sid, id)["singleVisual"]!;

    // seed a custom theme (the granular theme tools edit the LIVE custom theme).
    private static void SeedTheme(ReportService svc, string sid) =>
        svc.GenerateTheme(sid, "Test", "#16365C", null, "executive", "Segoe UI", false, 8, true, true, true, null);

    private static JsonObject LiveTheme(SessionStore store, string sid) =>
        (JsonObject)ParseObj((string?)Root(store, sid)["config"])["themeCollection"]!["customTheme"]!;

    // ====================================================================== 1. visual calculations

    [Fact]
    public void AddVisualCalculation_WritesIntoVisualCalculations_AndProjectsAsValuesColumn()
    {
        var (svc, sid, store) = NewReport(ContainerWithProjections("a1", "tableEx"));

        svc.AddVisualCalculation(sid, Page, "a1", "Running Total", "RUNNINGSUM([Sales])");

        var sv = SingleVisual(store, sid, "a1");
        var calcs = (JsonArray)sv["visualCalculations"]!;
        var calc = calcs.OfType<JsonObject>().First(c => (string?)c["name"] == "Running Total");
        Assert.Equal("RUNNINGSUM([Sales])", (string?)calc["expression"]);
        // projected as a Values column referencing the calc by name (nativeVisualCalculation)
        var values = (JsonArray)sv["projections"]!["Values"]!;
        var proj = values.OfType<JsonObject>().First(p => (string?)p["queryRef"] == "Running Total");
        Assert.True((bool)proj["nativeVisualCalculation"]!);
    }

    [Fact]
    public void AddVisualCalculation_SameName_UpdatesInPlace_NoDuplicate()
    {
        var (svc, sid, store) = NewReport(ContainerWithProjections("a1", "tableEx"));

        svc.AddVisualCalculation(sid, Page, "a1", "VC", "1");
        svc.AddVisualCalculation(sid, Page, "a1", "VC", "2");

        var calcs = (JsonArray)SingleVisual(store, sid, "a1")["visualCalculations"]!;
        Assert.Single(calcs.OfType<JsonObject>(), c => (string?)c["name"] == "VC");
        Assert.Equal("2", (string?)calcs.OfType<JsonObject>().First(c => (string?)c["name"] == "VC")["expression"]);
    }

    [Fact]
    public void AddVisualCalculation_MissingExpression_Throws()
    {
        var (svc, sid, _) = NewReport(ContainerWithProjections("a1", "tableEx"));
        Assert.ThrowsAny<Exception>(() => svc.AddVisualCalculation(sid, Page, "a1", "VC", ""));
    }

    // ====================================================================== 2. native sparklines

    [Fact]
    public void AddSparkline_WritesSparklineObject_WithLineColourAndBindings()
    {
        var (svc, sid, store) = NewReport(ContainerWithProjections("a1", "tableEx"));

        svc.AddSparkline(sid, Page, "a1", "Fact", "Sales", "Dim_Date", "Month", "#FF0000");

        var entry = (JsonObject)((JsonArray)SingleVisual(store, sid, "a1")["objects"]!["sparkline"]!)[0]!;
        var props = (JsonObject)entry["properties"]!;
        Assert.Equal("true", (string?)props["show"]!["expr"]!["Literal"]!["Value"]);
        // line colour encoded as a solid colour literal
        Assert.Equal("'#FF0000'", (string?)props["lineColor"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
        // the measure + category bindings reference the queryRefs
        Assert.Equal("'Fact.Sales'", (string?)props["measure"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Dim_Date.Month'", (string?)props["categoryAxis"]!["expr"]!["Literal"]!["Value"]);
        // the value measure was projected so the query carries the trend
        var values = (JsonArray)SingleVisual(store, sid, "a1")["projections"]!["Values"]!;
        Assert.Contains(values.OfType<JsonObject>(), p => (string?)p["queryRef"] == "Fact.Sales");
    }

    [Fact]
    public void AddSparkline_MissingCategory_Throws()
    {
        var (svc, sid, _) = NewReport(ContainerWithProjections("a1", "tableEx"));
        Assert.ThrowsAny<Exception>(() => svc.AddSparkline(sid, Page, "a1", "Fact", "Sales", "Dim_Date", "", null));
    }

    // ====================================================================== 3. drill-through fields

    [Fact]
    public void SetDrillthroughFields_WritesCarriedFieldsAsHowCreated5Filters_AndKeepAllFilters()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetDrillthroughFields(sid, Page,
            new[] { ("Dim_Product", "Category"), ("Dim_Store", "Region") }, keepAllFilters: true);

        var filters = ParseArr((string?)Section(store, sid)["filters"]);
        // both carried fields are drill-through filters (howCreated 5) referencing their entity
        var dt = filters.OfType<JsonObject>().Where(f => (int?)f["howCreated"]?.GetValue<int>() == 5).ToList();
        Assert.Equal(2, dt.Count);
        Assert.Contains(dt, f => (string?)f["expression"]!["Column"]!["Expression"]!["SourceRef"]!["Entity"] == "Dim_Product"
                                 && (string?)f["expression"]!["Column"]!["Property"] == "Category");
        // keep-all-filters lands on the page binding card
        var keep = ParseObj((string?)Section(store, sid)["config"])
            ["objects"]!["pageInformation"]![0]!["properties"]!["keepAllFilters"]!["expr"]!["Literal"]!["Value"];
        Assert.Equal("true", (string?)keep);
    }

    [Fact]
    public void SetDrillthroughFields_ReplacesExistingDrillthroughFilters()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetDrillthroughFields(sid, Page, new[] { ("T", "A") }, null);
        svc.SetDrillthroughFields(sid, Page, new[] { ("T", "B") }, null);

        var filters = ParseArr((string?)Section(store, sid)["filters"]);
        var dt = filters.OfType<JsonObject>().Where(f => (int?)f["howCreated"]?.GetValue<int>() == 5).ToList();
        // the first field set was REPLACED, not appended
        Assert.Single(dt);
        Assert.Equal("B", (string?)dt[0]["expression"]!["Column"]!["Property"]);
    }

    [Fact]
    public void SetDrillthroughFields_NoFields_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.SetDrillthroughFields(sid, Page, System.Array.Empty<(string, string)>(), null));
    }

    // ====================================================================== 4. drill-down

    [Fact]
    public void SetDrilldown_SetsBehaviourOnGeneral_AndSeedsExpansionStates()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "clusteredColumnChart"));

        svc.SetDrilldown(sid, Page, "a1", expandToNextLevel: true, drillOnClick: false);

        var sv = SingleVisual(store, sid, "a1");
        var general = (JsonObject)((JsonArray)sv["objects"]!["general"]!)[0]!["properties"]!;
        Assert.Equal("true", (string?)general["expandToNextLevel"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("false", (string?)general["drillOnClick"]!["expr"]!["Literal"]!["Value"]);
        // expansionStates seeded
        Assert.NotNull(sv["expansionStates"]);
    }

    // ====================================================================== 5. navigators

    [Fact]
    public void AddPageNavigator_AddsPageNavigatorVisual()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        var res = svc.AddNavigator(sid, Page, "pageNavigator", 16, 16, 640, 48);
        string id = (string)res.GetType().GetProperty("visualName")!.GetValue(res)!;

        Assert.Equal("pageNavigator", (string?)SingleVisual(store, sid, id)["visualType"]);
    }

    [Fact]
    public void AddBookmarkNavigator_AddsBookmarkNavigatorVisual()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        var res = svc.AddNavigator(sid, Page, "bookmarkNavigator", 16, 16, 640, 48);
        string id = (string)res.GetType().GetProperty("visualName")!.GetValue(res)!;

        Assert.Equal("bookmarkNavigator", (string?)SingleVisual(store, sid, id)["visualType"]);
    }

    [Fact]
    public void AddNavigator_UnknownType_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.AddNavigator(sid, Page, "wibble", 0, 0, 100, 40));
    }

    // ====================================================================== 6. theme authoring

    [Fact]
    public void SetThemeDataColors_ReplacesPalette()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedTheme(svc, sid);

        svc.SetThemeDataColors(sid, new[] { "#111111", "#222222", "#333333" });

        var dc = (JsonArray)LiveTheme(store, sid)["dataColors"]!;
        Assert.Equal(3, dc.Count);
        Assert.Equal("#111111", (string?)dc[0]);
    }

    [Fact]
    public void SetThemeSentimentColors_SetsGoodNeutralBad()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedTheme(svc, sid);

        svc.SetThemeSentimentColors(sid, "#00FF00", "#FFFF00", "#FF0000");

        var t = LiveTheme(store, sid);
        Assert.Equal("#00FF00", (string?)t["good"]);
        Assert.Equal("#FFFF00", (string?)t["neutral"]);
        Assert.Equal("#FF0000", (string?)t["bad"]);
    }

    [Fact]
    public void SetThemeCfColors_SetsGradientStops()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedTheme(svc, sid);

        svc.SetThemeCfColors(sid, "#0000FF", "#888888", "#FF0000", "#CCCCCC");

        var t = LiveTheme(store, sid);
        Assert.Equal("#0000FF", (string?)t["minimum"]);
        Assert.Equal("#888888", (string?)t["center"]);
        Assert.Equal("#FF0000", (string?)t["maximum"]);
        Assert.Equal("#CCCCCC", (string?)t["null"]);
    }

    [Fact]
    public void SetThemeStructuralColors_SetsNamedStructuralKeys_IgnoresUnknown()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedTheme(svc, sid);

        svc.SetThemeStructuralColors(sid,
            "{\"firstLevelElements\":\"#101010\",\"secondaryBackground\":\"#F5F5F5\",\"tableAccent\":\"#ABCDEF\",\"bogusKey\":\"#000000\"}");

        var t = LiveTheme(store, sid);
        Assert.Equal("#101010", (string?)t["firstLevelElements"]);
        Assert.Equal("#F5F5F5", (string?)t["secondaryBackground"]);
        Assert.Equal("#ABCDEF", (string?)t["tableAccent"]);
        Assert.Null(t["bogusKey"]);   // unknown structural key was ignored, not written
    }

    [Fact]
    public void SetThemeTextClass_MergesIntoTextClass()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedTheme(svc, sid);

        svc.SetThemeTextClass(sid, "title", "Arial Black", 18, "#123456");

        var title = (JsonObject)LiveTheme(store, sid)["textClasses"]!["title"]!;
        Assert.Equal("Arial Black", (string?)title["fontFace"]);
        Assert.Equal(18, title["fontSize"]!.GetValue<double>());
        Assert.Equal("#123456", (string?)title["color"]);
    }

    [Fact]
    public void AddThemeVisualStylePreset_WritesPresetUnderVisualStyles_AsCardArrays()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));
        SeedTheme(svc, sid);

        svc.AddThemeVisualStylePreset(sid, "lineChart", "MyLine",
            "{\"title\":{\"fontSize\":14,\"bold\":true}}");

        var preset = (JsonObject)LiveTheme(store, sid)["visualStyles"]!["lineChart"]!["MyLine"]!;
        // a card body must be an ARRAY of property objects (the theme schema's shape)
        var titleArr = (JsonArray)preset["title"]!;
        Assert.Single(titleArr);
        Assert.Equal(14, ((JsonObject)titleArr[0]!)["fontSize"]!.GetValue<double>());
        Assert.True((bool)((JsonObject)titleArr[0]!)["bold"]!);
    }

    [Fact]
    public void ThemeTool_NoCustomTheme_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        // no SeedTheme -> there is no live custom theme to edit
        Assert.ThrowsAny<Exception>(() => svc.SetThemeDataColors(sid, new[] { "#111111" }));
    }

    // ====================================================================== 7. alt text

    [Fact]
    public void SetAltText_WritesGeneralAltText()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "clusteredColumnChart"));

        svc.SetAltText(sid, Page, "a1", "Sales by region, descending");

        var general = (JsonObject)((JsonArray)SingleVisual(store, sid, "a1")["objects"]!["general"]!)[0]!["properties"]!;
        Assert.Equal("'Sales by region, descending'", (string?)general["altText"]!["expr"]!["Literal"]!["Value"]);
    }

    // ====================================================================== 8. tab order

    [Fact]
    public void SetTabOrder_SetsTabOrderOnEachVisualPosition_InSequence()
    {
        var (svc, sid, store) = NewReport(
            SimpleContainer("a1", "card"), SimpleContainer("b2", "card"), SimpleContainer("c3", "card"));

        svc.SetTabOrder(sid, Page, new[] { "c3", "a1", "b2" });

        int TabOf(string id) => ((JsonObject)VisualConfig(store, sid, id)["layouts"]![0]!["position"]!)["tabOrder"]!.GetValue<int>();
        // c3 first (0), a1 next (1000), b2 last (2000)
        Assert.Equal(0, TabOf("c3"));
        Assert.Equal(1000, TabOf("a1"));
        Assert.Equal(2000, TabOf("b2"));
    }

    [Fact]
    public void SetTabOrder_Empty_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.SetTabOrder(sid, Page, System.Array.Empty<string>()));
    }

    // ====================================================================== 9. personalisation

    [Fact]
    public void SetPersonalization_Report_SetsAllowInlineExploration()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetPersonalization(sid, "report", null, allowInlineExploration: true, null);

        var settings = (JsonObject)ParseObj((string?)Root(store, sid)["config"])["settings"]!;
        Assert.Equal("true", (string?)settings["allowInlineExploration"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetPersonalization_Page_SetsPersonalizeVisualShow()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetPersonalization(sid, "page", Page, null, perVisualPersonalize: true);

        var show = ParseObj((string?)Section(store, sid)["config"])
            ["objects"]!["personalizeVisual"]![0]!["properties"]!["show"]!["expr"]!["Literal"]!["Value"];
        Assert.Equal("true", (string?)show);
    }

    [Fact]
    public void SetPersonalization_UnknownScope_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.SetPersonalization(sid, "galaxy", null, true, null));
    }

    // ====================================================================== 10. custom visual

    [Fact]
    public void RegisterCustomVisual_AddsGuidToPublicCustomVisuals()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.RegisterCustomVisual(sid, "MyChartGuid1234", null, null);

        var pub = (JsonArray)ParseObj((string?)Root(store, sid)["config"])["publicCustomVisuals"]!;
        Assert.Contains(pub.OfType<JsonValue>(), v => (string?)v == "MyChartGuid1234");
    }

    [Fact]
    public void RegisterCustomVisual_SameGuidTwice_NoDuplicate()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.RegisterCustomVisual(sid, "G", null, null);
        svc.RegisterCustomVisual(sid, "G", null, null);

        var pub = (JsonArray)ParseObj((string?)Root(store, sid)["config"])["publicCustomVisuals"]!;
        Assert.Single(pub.OfType<JsonValue>(), v => (string?)v == "G");
    }

    // ====================================================================== 11. wallpaper

    [Fact]
    public void SetPageWallpaper_WritesOutspaceObject_DistinctFromBackground()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        // first set a canvas background, then a wallpaper - they must NOT collide
        svc.SetPageBackground(sid, Page, "#FFFFFF", 0);
        svc.SetPageWallpaper(sid, Page, "#E0E0E0", 10);

        var objects = (JsonObject)ParseObj((string?)Section(store, sid)["config"])["objects"]!;
        // wallpaper is the OUTSPACE object
        var ws = (JsonObject)((JsonArray)objects["outspace"]!)[0]!["properties"]!;
        Assert.Equal("'#E0E0E0'", (string?)ws["color"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("10D", (string?)ws["transparency"]!["expr"]!["Literal"]!["Value"]);
        // the canvas background (set_page_background) is a SEPARATE object, untouched
        Assert.NotNull(objects["background"]);
        Assert.NotSame(objects["background"], objects["outspace"]);
    }
}
