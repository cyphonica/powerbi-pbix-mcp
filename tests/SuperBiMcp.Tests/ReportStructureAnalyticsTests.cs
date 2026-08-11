using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL structure-and-analytics engine methods (grouping, z-order, page type & tooltip,
/// analytics reference lines, and the conditional-format variants) over an in-memory Report/Layout -
/// the same JsonObject root Open() produces. Fault-sensitive: each test asserts the exact JSON shape the
/// engine writes, so a regression in a builder breaks a specific test. No live .pbix is needed.
/// </summary>
public sealed class ReportStructureAnalyticsTests
{
    private const string Page = "Page1";

    // a visualContainer whose "config" is a STRINGIFIED blob holding { name, layouts, singleVisual } -
    // exactly the shape the engine writes and reads. x/y/z and bounds are explicit so z-order and grouping
    // can be checked.
    private static JsonObject Container(string id, string visualType, double x, double y, double z = 0,
        double width = 100, double height = 80)
    {
        var config = new JsonObject
        {
            ["name"] = id,
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                    { ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = width, ["height"] = height, ["tabOrder"] = (int)z } },
            },
            ["singleVisual"] = new JsonObject { ["visualType"] = visualType },
        };
        return new JsonObject
        {
            ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = width, ["height"] = height,
            ["config"] = config.ToJsonString(),
            ["filters"] = "[]",
        };
    }

    private record V(string Id, string Type, double X = 16, double Y = 20, double Z = 0, double W = 100, double H = 80);

    private static (ReportService svc, string sid, SessionStore store) NewReport(params V[] visuals)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var containers = new JsonArray();
        foreach (var v in visuals) containers.Add(Container(v.Id, v.Type, v.X, v.Y, v.Z, v.W, v.H));

        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 32),
            ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = containers,
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720, ["displayOption"] = 1,
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

    // ---- inspection helpers (read the live in-memory layout the engine mutated) ----

    private static JsonObject Root(SessionStore store, string sid) => store.GetReport(sid).Layout.Root;
    private static JsonArray SectionsOf(SessionStore store, string sid) => (JsonArray)Root(store, sid)["sections"]!;
    private static JsonObject Section(SessionStore store, string sid) =>
        SectionsOf(store, sid).OfType<JsonObject>().First(s => (string?)s["displayName"] == Page);
    private static JsonArray Containers(SessionStore store, string sid) =>
        (JsonArray)Section(store, sid)["visualContainers"]!;

    // the parsed config of the container whose config.name == id
    private static JsonObject? ConfigOf(SessionStore store, string sid, string id) =>
        Containers(store, sid).OfType<JsonObject>()
            .Select(vc => JsonNode.Parse((string)vc["config"]!) as JsonObject)
            .FirstOrDefault(co => (string?)co!["name"] == id);

    // the live container (not the parsed config) whose config.name == id
    private static JsonObject ContainerOf(SessionStore store, string sid, string id) =>
        Containers(store, sid).OfType<JsonObject>()
            .First(vc => (string?)(JsonNode.Parse((string)vc["config"]!) as JsonObject)!["name"] == id);

    private static string Pck(object result, string prop) =>
        (string?)(JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject)![prop] ?? "";

    // a number stored by the engine may be an int or a double JSON node; read either tolerantly
    private static double D(JsonNode? n)
    {
        try { return n!.GetValue<double>(); }
        catch { return n!.GetValue<int>(); }
    }

    private static JsonObject SingleVisual(SessionStore store, string sid, string id) =>
        (JsonObject)ConfigOf(store, sid, id)!["singleVisual"]!;

    // ====================================================================== group / ungroup

    [Fact]
    public void GroupVisuals_StampsParentGroupName_OnExactlyNamedVisuals_AndCreatesContainer()
    {
        var (svc, sid, store) = NewReport(
            new V("a1", "tableEx", 10, 10, 1, 100, 80),
            new V("b2", "card", 200, 50, 2, 100, 80),
            new V("c3", "slicer", 400, 400, 3, 100, 80));   // NOT grouped

        var result = svc.GroupVisuals(sid, Page, new[] { "a1", "b2" }, "My Group");
        string grp = Pck(result, "groupName");

        // EXACTLY the named visuals carry parentGroupName = the group id
        Assert.Equal(grp, (string?)ConfigOf(store, sid, "a1")!["parentGroupName"]);
        Assert.Equal(grp, (string?)ConfigOf(store, sid, "b2")!["parentGroupName"]);
        Assert.Null(ConfigOf(store, sid, "c3")!["parentGroupName"]);   // untouched

        // a group CONTAINER exists with that name and NO singleVisual
        var grpCo = ConfigOf(store, sid, grp);
        Assert.NotNull(grpCo);
        Assert.Null(grpCo!["singleVisual"]);
        Assert.Equal("My Group", (string?)grpCo["singleVisualGroup"]!["displayName"]);

        // the group rectangle wraps the two children (x10..x300, y10..y130)
        var pos = (JsonObject)grpCo["layouts"]![0]!["position"]!;
        Assert.Equal(10d, pos["x"]!.GetValue<double>());
        Assert.Equal(10d, pos["y"]!.GetValue<double>());
        Assert.Equal(290d, pos["width"]!.GetValue<double>());    // 300 - 10
        Assert.Equal(120d, pos["height"]!.GetValue<double>());   // 130 - 10
    }

    [Fact]
    public void GroupVisuals_RequiresTwoVisuals()
    {
        var (svc, sid, _) = NewReport(new V("a1", "tableEx"));
        Assert.Throws<ArgumentException>(() => svc.GroupVisuals(sid, Page, new[] { "a1" }, null));
    }

    [Fact]
    public void GroupVisuals_UnknownVisual_Throws()
    {
        var (svc, sid, _) = NewReport(new V("a1", "tableEx"), new V("b2", "card"));
        Assert.Throws<InvalidOperationException>(() => svc.GroupVisuals(sid, Page, new[] { "a1", "nope" }, null));
    }

    [Fact]
    public void UngroupVisuals_ReversesTheGrouping()
    {
        var (svc, sid, store) = NewReport(new V("a1", "tableEx"), new V("b2", "card"));
        string grp = Pck(svc.GroupVisuals(sid, Page, new[] { "a1", "b2" }, null), "groupName");

        // sanity: grouped first
        Assert.Equal(grp, (string?)ConfigOf(store, sid, "a1")!["parentGroupName"]);
        int before = Containers(store, sid).Count;   // 2 children + group container = 3

        svc.UngroupVisuals(sid, Page, grp);

        // children freed, group container removed
        Assert.Null(ConfigOf(store, sid, "a1")!["parentGroupName"]);
        Assert.Null(ConfigOf(store, sid, "b2")!["parentGroupName"]);
        Assert.Null(ConfigOf(store, sid, grp));               // group container gone
        Assert.Equal(before - 1, Containers(store, sid).Count);
    }

    // ====================================================================== z-order

    [Fact]
    public void SetVisualZOrder_SetsContainerAndConfigZ()
    {
        var (svc, sid, store) = NewReport(new V("a1", "tableEx", Z: 5));
        svc.SetVisualZOrder(sid, Page, "a1", 42);

        Assert.Equal(42d, ContainerOf(store, sid, "a1")["z"]!.GetValue<double>());
        var pos = (JsonObject)ConfigOf(store, sid, "a1")!["layouts"]![0]!["position"]!;
        Assert.Equal(42d, pos["z"]!.GetValue<double>());
        Assert.Equal(42, pos["tabOrder"]!.GetValue<int>());
    }

    [Fact]
    public void BringToFront_PutsZAboveTheCurrentMax()
    {
        var (svc, sid, store) = NewReport(
            new V("a1", "tableEx", Z: 1),
            new V("b2", "card", Z: 7),
            new V("c3", "slicer", Z: 3));
        svc.BringToFront(sid, Page, "a1");

        // current max was 7 -> a1 goes to 8 (strictly above every other)
        double a1z = ContainerOf(store, sid, "a1")["z"]!.GetValue<double>();
        Assert.Equal(8d, a1z);
        Assert.True(a1z > ContainerOf(store, sid, "b2")["z"]!.GetValue<double>());
        Assert.True(a1z > ContainerOf(store, sid, "c3")["z"]!.GetValue<double>());
    }

    [Fact]
    public void SendToBack_PutsZBelowTheCurrentMin()
    {
        var (svc, sid, store) = NewReport(
            new V("a1", "tableEx", Z: 2),
            new V("b2", "card", Z: 7),
            new V("c3", "slicer", Z: 5));
        svc.SendToBack(sid, Page, "b2");

        // current min was 2 -> b2 goes to 1 (strictly below every other)
        double b2z = ContainerOf(store, sid, "b2")["z"]!.GetValue<double>();
        Assert.Equal(1d, b2z);
        Assert.True(b2z < ContainerOf(store, sid, "a1")["z"]!.GetValue<double>());
        Assert.True(b2z < ContainerOf(store, sid, "c3")["z"]!.GetValue<double>());
    }

    // ====================================================================== page type & tooltip

    [Fact]
    public void SetPageType_Tooltip_MarksTypeAndShrinksCanvas()
    {
        var (svc, sid, store) = NewReport(new V("a1", "tableEx"));
        svc.SetPageType(sid, Page, "tooltip");

        var section = Section(store, sid);
        // small tooltip canvas
        Assert.Equal(320d, D(section["width"]));
        Assert.Equal(240d, D(section["height"]));
        Assert.Equal(2d, D(section["displayOption"]));

        // page flagged Tooltip in config.objects.pageInformation
        var cfg = JsonNode.Parse((string)section["config"]!) as JsonObject;
        var props = (JsonObject)((JsonArray)cfg!["objects"]!["pageInformation"]!)[0]!["properties"]!;
        Assert.Equal("'Tooltip'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetPageType_Drillthrough_FlagsTypeWithoutShrinking()
    {
        var (svc, sid, store) = NewReport(new V("a1", "tableEx"));
        svc.SetPageType(sid, Page, "drillthrough");

        var section = Section(store, sid);
        Assert.Equal(1280d, D(section["width"]));   // canvas NOT shrunk
        var cfg = JsonNode.Parse((string)section["config"]!) as JsonObject;
        var props = (JsonObject)((JsonArray)cfg!["objects"]!["pageInformation"]!)[0]!["properties"]!;
        Assert.Equal("'Drillthrough'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetPageType_Unknown_Throws()
    {
        var (svc, sid, _) = NewReport(new V("a1", "tableEx"));
        Assert.Throws<ArgumentException>(() => svc.SetPageType(sid, Page, "bogus"));
    }

    [Fact]
    public void SetVisualTooltipPage_PointsVisualAtTheTooltipPage()
    {
        var (svc, sid, store) = NewReport(new V("a1", "clusteredColumnChart"));
        // the tooltip page section name is the section's "name" (resolves by displayName too)
        string secName = (string)Section(store, sid)["name"]!;
        svc.SetVisualTooltipPage(sid, Page, "a1", Page);

        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, "a1")["vcObjects"]!["visualTooltip"]!)[0]!["properties"]!;
        Assert.Equal("true", (string?)props["show"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'ReportPage'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal($"'{secName}'", (string?)props["section"]!["expr"]!["Literal"]!["Value"]);
    }

    // ====================================================================== analytics lines

    [Fact]
    public void AddAnalyticsLine_Constant_WritesValueOnReferenceLine()
    {
        var (svc, sid, store) = NewReport(new V("a1", "lineChart"));
        svc.AddAnalyticsLine(sid, Page, "a1", "constant", 100, null, null, "Target", "#E81123");

        var arr = (JsonArray)SingleVisual(store, sid, "a1")["objects"]!["y1AxisReferenceLine"]!;
        var props = (JsonObject)arr[0]!["properties"]!;
        Assert.Equal("100D", (string?)props["value"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'#E81123'", (string?)props["lineColor"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Target'", (string?)props["text"]!["expr"]!["Literal"]!["Value"]);
        Assert.NotNull(arr[0]!["selector"]!["id"]);
    }

    [Fact]
    public void AddAnalyticsLine_Average_WritesAggregateTypeAndMeasure()
    {
        var (svc, sid, store) = NewReport(new V("a1", "clusteredColumnChart"));
        svc.AddAnalyticsLine(sid, Page, "a1", "average", null, "Fact", "Sales", null, null);

        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, "a1")["objects"]!["y1AxisReferenceLine"]!)[0]!["properties"]!;
        Assert.Equal("'Average'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("Sales", (string?)props["measure"]!["expr"]!["Measure"]!["Property"]);
        Assert.Equal("Fact", (string?)props["measure"]!["expr"]!["Measure"]!["Expression"]!["SourceRef"]!["Entity"]);
    }

    [Fact]
    public void AddAnalyticsLine_Trend_WritesTrendObject()
    {
        var (svc, sid, store) = NewReport(new V("a1", "lineChart"));
        svc.AddAnalyticsLine(sid, Page, "a1", "trend", null, null, null, null, null);

        var objects = (JsonObject)SingleVisual(store, sid, "a1")["objects"]!;
        Assert.NotNull(objects["trend"]);
        Assert.Null(objects["y1AxisReferenceLine"]);   // a trend line does NOT use the reference-line object
    }

    [Fact]
    public void AddAnalyticsLine_Forecast_WritesForecastObject()
    {
        var (svc, sid, store) = NewReport(new V("a1", "lineChart"));
        svc.AddAnalyticsLine(sid, Page, "a1", "forecast", null, null, null, null, null);
        Assert.NotNull(SingleVisual(store, sid, "a1")["objects"]!["forecast"]);
    }

    [Fact]
    public void AddAnalyticsLine_Constant_WithoutValue_Throws()
    {
        var (svc, sid, _) = NewReport(new V("a1", "lineChart"));
        Assert.Throws<ArgumentException>(() => svc.AddAnalyticsLine(sid, Page, "a1", "constant", null, null, null, null, null));
    }

    [Fact]
    public void AddAnalyticsLine_UnknownKind_Throws()
    {
        var (svc, sid, _) = NewReport(new V("a1", "lineChart"));
        Assert.Throws<ArgumentException>(() => svc.AddAnalyticsLine(sid, Page, "a1", "wibble", null, null, null, null, null));
    }

    // ====================================================================== CF variants

    private static List<(double, double, string)> Bands() => new()
    {
        (0d, 1_000_000d, "#C6EFCE"),
        (-1_000_000d, 0d, "#e68f96"),
    };

    [Fact]
    public void SetFontColorRules_WritesDiscreteRulesOnFontColor_NotBackColor()
    {
        var (svc, sid, store) = NewReport(new V("m1", "pivotTable"));
        svc.SetFontColorRules(sid, Page, "m1", "Fact", "Sales Growth %", Bands());

        var entry = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!;
        Assert.Equal("Fact.Sales Growth %", (string?)entry["selector"]!["metadata"]);

        // the rules land on fontColor (NOT backColor)
        var props = (JsonObject)entry["properties"]!;
        Assert.NotNull(props["fontColor"]);
        Assert.Null(props["backColor"]);

        var fillRule = (JsonObject)props["fontColor"]!["solid"]!["color"]!["expr"]!["FillRule"]!;
        Assert.Equal("Sales Growth %", (string?)fillRule["Input"]!["Measure"]!["Property"]);
        var ruleArr = (JsonArray)fillRule["FillRule"]!["ruleDefinition"]!["rules"]!;
        Assert.Equal(2, ruleArr.Count);
        Assert.Equal("'#C6EFCE'", (string?)ruleArr[0]!["Color"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetDataBars_WritesDataBarsOnTheMeasureColumn()
    {
        var (svc, sid, store) = NewReport(new V("m1", "tableEx"));
        svc.SetDataBars(sid, Page, "m1", "Fact", "Sales", "#1B8A4B", "#E81123");

        var entry = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!;
        Assert.Equal("Fact.Sales", (string?)entry["selector"]!["metadata"]);

        var dataBars = (JsonObject)entry["properties"]!["dataBars"]!;
        Assert.Equal("'#1B8A4B'", (string?)dataBars["positiveColor"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'#E81123'", (string?)dataBars["negativeColor"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
        // the bar scale defaults to AUTO min/max (Wave N fix - the old code wrote the SAME measure expr to both
        // min and max, so min==max and the bars never rendered). Now min/max are distinct Auto rules.
        Assert.NotNull(dataBars["minValue"]!["Auto"]);
        Assert.NotNull(dataBars["maxValue"]!["Auto"]);
    }

    [Fact]
    public void SetDataBars_DefaultsNegativeColorWhenOmitted()
    {
        var (svc, sid, store) = NewReport(new V("m1", "tableEx"));
        svc.SetDataBars(sid, Page, "m1", "Fact", "Sales", "#1B8A4B", null);

        var dataBars = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!["properties"]!["dataBars"]!;
        Assert.Equal("'#FF0000'", (string?)dataBars["negativeColor"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetIconRules_WritesIconRuleDefinitionOnTheMeasureColumn()
    {
        var (svc, sid, store) = NewReport(new V("m1", "pivotTable"));
        svc.SetIconRules(sid, Page, "m1", "Fact", "Sales Growth %", Bands());

        var entry = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!;
        Assert.Equal("Fact.Sales Growth %", (string?)entry["selector"]!["metadata"]);

        var fillRule = (JsonObject)entry["properties"]!["icon"]!["solid"]!["color"]!["expr"]!["FillRule"]!;
        Assert.Equal("Sales Growth %", (string?)fillRule["Input"]!["Measure"]!["Property"]);
        var ruleArr = (JsonArray)fillRule["FillRule"]!["ruleDefinition"]!["rules"]!;
        Assert.Equal(2, ruleArr.Count);

        // first band: a (>= 0 AND < 1,000,000) condition + an icon keyed by the band colour
        var and = (JsonObject)ruleArr[0]!["Condition"]!["And"]!;
        Assert.Equal(2, and["Left"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>());
        Assert.Equal("0D", (string?)and["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
        Assert.Equal("'#C6EFCE'", (string?)ruleArr[0]!["IconId"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetIconRules_Empty_Throws()
    {
        var (svc, sid, _) = NewReport(new V("m1", "pivotTable"));
        Assert.Throws<ArgumentException>(() => svc.SetIconRules(sid, Page, "m1", "Fact", "Sales", new List<(double, double, string)>()));
    }
}
