using System;
using System.Linq;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave O report-side helpers over an in-memory Report/Layout: dynamic-title binding, measure-driven
/// button-state CF, global wildcard theme defaults, palette generation into the theme, the HTML-content
/// custom visual (registration + add + bind), and the report-template apply/save round-trip. Fault-sensitive:
/// each test pins the exact JSON the engine writes, so a regression breaks a specific test.
/// </summary>
public sealed class ReportWaveOTests
{
    private const string Page = "Page1";

    private static (ReportService svc, string sid, SessionStore store) NewReport(string visualType = "clusteredColumnChart")
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var config = new JsonObject
        {
            ["name"] = "v1",
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                    { ["x"] = 10, ["y"] = 20, ["z"] = 0, ["width"] = 100, ["height"] = 80, ["tabOrder"] = 0 } },
            },
            ["singleVisual"] = new JsonObject
            {
                ["visualType"] = visualType,
                ["prototypeQuery"] = new JsonObject { ["Select"] = new JsonArray() },
            },
        };
        var container = new JsonObject
        {
            ["x"] = 10, ["y"] = 20, ["z"] = 0, ["width"] = 100, ["height"] = 80,
            ["config"] = config.ToJsonString(), ["filters"] = "[]",
        };
        var section = new JsonObject
        {
            ["name"] = Page, ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = new JsonArray { container },
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
        };
        var root = new JsonObject { ["sections"] = new JsonArray { section }, ["config"] = "{}" };

        var session = new ReportSession
        {
            Id = store.NewId("report"),
            PbixPath = "in-memory.pbix",
            Layout = new ReportLayout { Root = root, LayoutPartName = "Report/Layout" },
        };
        store.AddReport(session);
        return (svc, session.Id, store);
    }

    private static JsonObject SingleVisual(SessionStore store, string sid, int index = 0)
    {
        var root = store.GetReport(sid).Layout.Root;
        var container = (JsonObject)((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!)[index]!;
        var co = JsonNode.Parse((string)container["config"]!) as JsonObject;
        return (JsonObject)co!["singleVisual"]!;
    }

    private static JsonObject RootConfig(SessionStore store, string sid)
        => JsonNode.Parse((string)store.GetReport(sid).Layout.Root["config"]!) as JsonObject ?? new JsonObject();

    private static JsonObject Theme(SessionStore store, string sid)
        => (JsonObject)RootConfig(store, sid)["themeCollection"]!["customTheme"]!;

    // ====================================================================== bind_dynamic_title
    [Fact]
    public void BindDynamicTitle_BindsMeasureToTitleText_AndForcesShow()
    {
        var (svc, sid, store) = NewReport();
        svc.BindDynamicTitle(sid, Page, "v1", "Sales[Page Title]", null);

        var arr = (JsonArray)SingleVisual(store, sid)["vcObjects"]!["title"]!;
        var props = (JsonObject)arr[0]!["properties"]!;
        // FAULT-SENSITIVE: the title text is a Measure-bound expr (not a literal), and show is true.
        Assert.Equal("Sales", (string?)props["text"]!["expr"]!["Measure"]!["Expression"]!["SourceRef"]!["Entity"]);
        Assert.Equal("Page Title", (string?)props["text"]!["expr"]!["Measure"]!["Property"]);
        Assert.Equal("true", (string?)props["show"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void BindDynamicTitle_SeparateTableArgument_Works()
    {
        var (svc, sid, store) = NewReport();
        svc.BindDynamicTitle(sid, Page, "v1", "Page Title", "Sales");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid)["vcObjects"]!["title"]!)[0]!["properties"]!;
        Assert.Equal("Sales", (string?)props["text"]!["expr"]!["Measure"]!["Expression"]!["SourceRef"]!["Entity"]);
    }

    [Fact]
    public void BindDynamicTitle_NoTable_Throws()
    {
        var (svc, sid, _) = NewReport();
        Assert.Throws<ArgumentException>(() => svc.BindDynamicTitle(sid, Page, "v1", "Page Title", null));
    }

    // ====================================================================== set_button_state_cf
    [Fact]
    public void SetButtonStateCf_WritesMeasureBoundColour_WithStateSelector()
    {
        var (svc, sid, store) = NewReport("actionButton");
        svc.SetButtonStateCf(sid, Page, "v1", "Nav[Active Colour]", "selected", "fill", null);

        var arr = (JsonArray)SingleVisual(store, sid)["vcObjects"]!["fill"]!;
        var entry = (JsonObject)arr[0]!;
        // the per-state selector
        Assert.Equal("selected", (string?)entry["selector"]!["id"]);
        // the fill colour is a solid wrapper around a Measure-bound expr
        var colour = entry["properties"]!["fillColor"]!["solid"]!["color"]!;
        Assert.Equal("Nav", (string?)colour["expr"]!["Measure"]!["Expression"]!["SourceRef"]!["Entity"]);
        Assert.Equal("Active Colour", (string?)colour["expr"]!["Measure"]!["Property"]);
    }

    [Fact]
    public void SetButtonStateCf_TextTarget_UsesFontColorCard()
    {
        var (svc, sid, store) = NewReport("actionButton");
        svc.SetButtonStateCf(sid, Page, "v1", "Nav[Txt]", "hover", "text", null);
        var entry = (JsonObject)((JsonArray)SingleVisual(store, sid)["vcObjects"]!["text"]!)[0]!;
        Assert.Equal("hover", (string?)entry["selector"]!["id"]);
        Assert.NotNull(entry["properties"]!["fontColor"]!["solid"]!["color"]!["expr"]!["Measure"]);
    }

    [Fact]
    public void SetButtonStateCf_BadStateOrTarget_Throws()
    {
        var (svc, sid, _) = NewReport("actionButton");
        Assert.Throws<ArgumentException>(() => svc.SetButtonStateCf(sid, Page, "v1", "Nav[C]", "glowing", "fill", null));
        Assert.Throws<ArgumentException>(() => svc.SetButtonStateCf(sid, Page, "v1", "Nav[C]", "hover", "border", null));
    }

    // ====================================================================== set_global_wildcard_defaults
    [Fact]
    public void SetGlobalWildcardDefaults_WritesStarStarCards_NormalisedToArrays()
    {
        var (svc, sid, store) = NewReport();
        // a card given as an object and a card given as an array - both must end up as arrays.
        string props = """{ "title": { "fontColor": "#16365C" }, "border": [ { "show": true, "radius": 8 } ] }""";
        svc.SetGlobalWildcardDefaults(sid, props);

        var def = (JsonObject)Theme(store, sid)["visualStyles"]!["*"]!["*"]!;
        var title = (JsonArray)def["title"]!;
        Assert.Equal("#16365C", (string?)title[0]!["fontColor"]);
        var border = (JsonArray)def["border"]!;
        Assert.Equal(8, (int?)border[0]!["radius"]);
    }

    [Fact]
    public void SetGlobalWildcardDefaults_MergesIntoExistingDefaults()
    {
        var (svc, sid, store) = NewReport();
        svc.SetGlobalWildcardDefaults(sid, """{ "title": { "fontColor": "#111111" } }""");
        svc.SetGlobalWildcardDefaults(sid, """{ "border": { "show": false } }""");

        var def = (JsonObject)Theme(store, sid)["visualStyles"]!["*"]!["*"]!;
        Assert.NotNull(def["title"]);   // first call preserved
        Assert.NotNull(def["border"]);  // second call merged in
    }

    // ====================================================================== generate_palette
    [Fact]
    public void GeneratePalette_Harmonic_WritesNDataColoursAndStops()
    {
        var (svc, sid, store) = NewReport();
        svc.GeneratePaletteIntoTheme(sid, "#2E86AB", null, null, 6, "harmonic");

        var theme = Theme(store, sid);
        Assert.Equal(6, ((JsonArray)theme["dataColors"]!).Count);
        Assert.Equal("#2E86AB", (string?)((JsonArray)theme["dataColors"]!)[0]);   // base colour first
        Assert.NotNull(theme["minimum"]); Assert.NotNull(theme["center"]); Assert.NotNull(theme["maximum"]);
        Assert.Equal("#2E7D32", (string?)theme["good"]);
    }

    [Fact]
    public void ComputePalette_Gradient_InterpolatesEndpoints()
    {
        var p = ReportService.ComputePalette(null, "#000000", "#FFFFFF", 3, "gradient");
        Assert.Equal(3, p.Length);
        Assert.Equal("#000000", p[0]);
        Assert.Equal("#FFFFFF", p[2]);
        Assert.Equal("#808080", p[1]);   // exact midpoint
    }

    [Fact]
    public void ComputePalette_Monochrome_RampsLightnessOfBase()
    {
        var p = ReportService.ComputePalette("#2E86AB", null, null, 5, "monochrome");
        Assert.Equal(5, p.Length);
        Assert.All(p, c => Assert.Matches("^#[0-9A-F]{6}$", c));
    }

    [Fact]
    public void ComputePalette_GradientMissingEndpoint_Throws()
    {
        Assert.Throws<ArgumentException>(() => ReportService.ComputePalette(null, "#000000", null, 3, "gradient"));
    }

    // ====================================================================== add_html_content_block
    [Fact]
    public void AddHtmlContentBlock_RegistersVisual_AddsAndBindsMeasure()
    {
        var (svc, sid, store) = NewReport();
        svc.AddHtmlContentBlock(sid, Page, "KPI HTML", "Sales[Html]", 0, 0, 300, 200, null);

        // the custom visual type is registered in layout.config.publicCustomVisuals
        var pub = (JsonArray)RootConfig(store, sid)["publicCustomVisuals"]!;
        Assert.Contains(pub.OfType<JsonValue>(), v => (string?)v == "htmlContentLite");

        // a new visual was added (index 1) bound to the measure on the Values role
        var sv = SingleVisual(store, sid, index: 1);
        Assert.Equal("htmlContentLite", (string?)sv["visualType"]);
        var values = (JsonArray)sv["projections"]!["Values"]!;
        Assert.Equal("Sales.Html", (string?)values[0]!["queryRef"]);
        // the Select carries the measure binding
        var sel = (JsonArray)sv["prototypeQuery"]!["Select"]!;
        Assert.Contains(sel.OfType<JsonObject>(), s => (string?)s["Name"] == "Sales.Html" && s["Measure"] != null);
    }

    [Fact]
    public void AddHtmlContentBlock_CustomGuid_IsUsed()
    {
        var (svc, sid, store) = NewReport();
        svc.AddHtmlContentBlock(sid, Page, "HTML", "Sales[Html]", 0, 0, 100, 100, "myOrg.htmlViewer");
        Assert.Equal("myOrg.htmlViewer", (string?)SingleVisual(store, sid, index: 1)["visualType"]);
    }

    // ====================================================================== template apply / save round-trip
    [Fact]
    public void ApplyReportTemplate_AppliesThemeWallpaperCanvasNav()
    {
        var (svc, sid, store) = NewReport();
        string template = """
        {
          "theme": { "name": "Brand", "dataColors": ["#16365C", "#2E86AB"] },
          "wallpaper": { "color": "#F4F6FA", "transparency": 0 },
          "canvas": { "preset": "custom", "width": 1600, "height": 900 },
          "nav": { "hideVisualContainerHeader": true }
        }
        """;
        svc.ApplyReportTemplate(sid, template, Page);

        // theme applied
        Assert.Equal("Brand", (string?)Theme(store, sid)["name"]);
        // canvas resized on the section
        var section = (JsonObject)((JsonArray)store.GetReport(sid).Layout.Root["sections"]!)[0]!;
        Assert.Equal(1600d, (double?)section["width"]);
        Assert.Equal(900d, (double?)section["height"]);
        // wallpaper (outspace) written on the section config
        var secCfg = JsonNode.Parse((string)section["config"]!) as JsonObject;
        Assert.NotNull(secCfg!["objects"]!["outspace"]);
        // nav settings on the report config
        Assert.NotNull(RootConfig(store, sid)["settings"]!["hideVisualContainerHeader"]);
    }

    [Fact]
    public void SaveReportTemplate_CapturesCurrentLook_AndRoundTrips()
    {
        var (svc, sid, store) = NewReport();
        // set up a look: theme + wallpaper + canvas.
        svc.GeneratePaletteIntoTheme(sid, "#16365C", null, null, 4, "harmonic");
        svc.SetPageWallpaper(sid, Page, "#101820", 10);
        svc.SetCanvasPreset(sid, Page, "custom", 1600, 900);

        var saved = (dynamic)svc.SaveReportTemplate(sid, "My Template", Page);
        string templateJson = saved.templateJson;
        var t = JsonNode.Parse(templateJson) as JsonObject;
        Assert.NotNull(t!["theme"]);
        Assert.Equal("#101820", (string?)t["wallpaper"]!["color"]);
        Assert.Equal(1600d, (double?)t["canvas"]!["width"]);

        // round-trip: applying the saved template to a fresh report reproduces the canvas size.
        var (svc2, sid2, store2) = NewReport();
        svc2.ApplyReportTemplate(sid2, templateJson, Page);
        var section2 = (JsonObject)((JsonArray)store2.GetReport(sid2).Layout.Root["sections"]!)[0]!;
        Assert.Equal(1600d, (double?)section2["width"]);
        Assert.Equal("My Template", (string?)t["name"]);
    }
}
