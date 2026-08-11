using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL universal visual-formatting engine (ReportService.Encode/Decode + get/set_visual_format
/// + the typed wrappers) over an in-memory Report/Layout. No live .pbix is needed - a ReportSession is built
/// straight from a JsonObject root and added to the SessionStore, exactly the shape Open() would produce.
/// Proves:
///   - Encode/Decode round-trips for Bool / Number / Text / Colour / Enum (decode(encode(x)) == x),
///   - set_visual_format adds a title + background WITHOUT clobbering a pre-existing unrelated object,
///   - the typed wrappers set title/position/visibility correctly (position updates BOTH locations,
///     visibility toggles display.mode),
///   - get_visual_format returns the decoded shape that matches what was set (set -> get round-trip).
/// </summary>
public sealed class VisualFormatTests
{
    // ---- a minimal in-memory report: one section, one chart visual, reused across tests ----

    private const string Page = "Page1";
    private const string Visual = "v1";

    private static JsonObject Lit(string raw) =>
        new() { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = raw } } };

    private static (ReportService svc, string sid) NewReport(JsonObject singleVisual)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var config = new JsonObject
        {
            ["name"] = Visual,
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                    { ["x"] = 10, ["y"] = 20, ["z"] = 0, ["width"] = 100, ["height"] = 80, ["tabOrder"] = 0 } },
            },
            ["singleVisual"] = singleVisual,
        };
        var container = new JsonObject
        {
            ["x"] = 10, ["y"] = 20, ["z"] = 0, ["width"] = 100, ["height"] = 80,
            ["config"] = config.ToJsonString(),
            ["filters"] = "[]",
        };
        var section = new JsonObject
        {
            ["name"] = Page, ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = new JsonArray { container },
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
        };
        var root = new JsonObject { ["sections"] = new JsonArray { section } };

        var session = new ReportSession
        {
            Id = store.NewId("report"),
            PbixPath = "in-memory.pbix",
            Layout = new ReportLayout { Root = root, LayoutPartName = "Report/Layout" },
        };
        store.AddReport(session);
        return (svc, session.Id);
    }

    // a chart that already carries an UNRELATED legend object - set_visual_format must not clobber it.
    private static JsonObject ChartWithLegend()
    {
        return new JsonObject
        {
            ["visualType"] = "clusteredColumnChart",
            ["objects"] = new JsonObject
            {
                ["legend"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true") } } },
            },
        };
    }

    private static JsonObject GetVc(object getResult, string obj)
    {
        // GetVisualFormat returns an anonymous object; round-trip it through JSON to read the decoded maps.
        string json = JsonSerializer.Serialize(getResult);
        var node = JsonNode.Parse(json) as JsonObject;
        return (node!["vcObjects"] as JsonObject)![obj] as JsonObject ?? new JsonObject();
    }

    private static JsonObject GetObjects(object getResult, string obj)
    {
        string json = JsonSerializer.Serialize(getResult);
        var node = JsonNode.Parse(json) as JsonObject;
        return (node!["objects"] as JsonObject)![obj] as JsonObject ?? new JsonObject();
    }

    // ====================================================================== encode / decode

    // kind is passed as a string so the public test method has no internal-enum parameter (accessibility).
    [Theory]
    [InlineData("Bool", "true")]
    [InlineData("Bool", "false")]
    [InlineData("Number", "100")]
    [InlineData("Number", "12.5")]
    [InlineData("Text", "Category Sales & Brand Share by Period")]
    [InlineData("Text", "O'Brien's")]    // embedded quote -> doubled and recovered
    [InlineData("Color", "#FFFFFF")]
    [InlineData("Enum", "Bookmark")]
    public void EncodeDecode_RoundTrips(string kindName, string value)
    {
        var kind = Enum.Parse<ReportService.FmtKind>(kindName);
        var encoded = ReportService.Encode(value, kind);
        string? decoded = ReportService.Decode(encoded);
        Assert.Equal(value, decoded);
    }

    [Fact]
    public void Encode_UsesTheCorrectWrappers()
    {
        // bool / number: bare value (number carries a D suffix); text / enum: single-quoted; colour: solid wrapper.
        Assert.Equal("true", (string?)ReportService.Encode("true", ReportService.FmtKind.Bool)["expr"]!["Literal"]!["Value"]);
        Assert.Equal("100D", (string?)ReportService.Encode("100", ReportService.FmtKind.Number)["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Top'", (string?)ReportService.Encode("Top", ReportService.FmtKind.Enum)["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'hi'", (string?)ReportService.Encode("hi", ReportService.FmtKind.Text)["expr"]!["Literal"]!["Value"]);
        // colour wraps in solid.color.expr.Literal.Value
        var col = ReportService.Encode("#16365C", ReportService.FmtKind.Color);
        Assert.Equal("'#16365C'", (string?)col["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
    }

    // ====================================================================== set_visual_format

    [Fact]
    public void SetVisualFormat_AddsTitleAndBackground_WithoutClobberingExistingLegend()
    {
        var (svc, sid) = NewReport(ChartWithLegend());
        string formatJson = """
        {
          "vcObjects": {
            "title": { "show": true, "text": "My Title" },
            "background": { "show": true, "color": "#FFFFFF", "transparency": 100 }
          }
        }
        """;
        svc.SetVisualFormat(sid, Page, Visual, formatJson);

        var got = svc.GetVisualFormat(sid, Page, Visual);

        // the new title + background landed, decoded
        var title = GetVc(got, "title");
        Assert.Equal("true", (string?)title["show"]);
        Assert.Equal("My Title", (string?)title["text"]);
        var bg = GetVc(got, "background");
        Assert.Equal("#FFFFFF", (string?)bg["color"]);
        Assert.Equal("100", (string?)bg["transparency"]);

        // the pre-existing, unrelated legend object SURVIVED untouched
        var legend = GetObjects(got, "legend");
        Assert.Equal("true", (string?)legend["show"]);
    }

    [Fact]
    public void SetVisualFormat_MergesIntoAnExistingObject_PreservingOtherProperties()
    {
        var (svc, sid) = NewReport(ChartWithLegend());
        // first set a title text, then set only show=false -> text must survive.
        svc.SetVisualFormat(sid, Page, Visual, """{ "vcObjects": { "title": { "text": "Keep" } } }""");
        svc.SetVisualFormat(sid, Page, Visual, """{ "vcObjects": { "title": { "show": false } } }""");

        var title = GetVc(svc.GetVisualFormat(sid, Page, Visual), "title");
        Assert.Equal("Keep", (string?)title["text"]);   // preserved
        Assert.Equal("false", (string?)title["show"]);  // added
    }

    // ====================================================================== typed wrappers

    [Fact]
    public void SetVisualTitle_SetsCustomTextAndShow()
    {
        var (svc, sid) = NewReport(ChartWithLegend());
        svc.SetVisualTitle(sid, Page, Visual, show: true, text: "Category Sales & Brand Share by Period",
            fontColor: null, alignment: null, fontSize: null);

        var title = GetVc(svc.GetVisualFormat(sid, Page, Visual), "title");
        Assert.Equal("true", (string?)title["show"]);
        Assert.Equal("Category Sales & Brand Share by Period", (string?)title["text"]);
    }

    [Fact]
    public void SetVisualPosition_UpdatesBothContainerAndLayoutPosition()
    {
        var (svc, sid) = NewReport(ChartWithLegend());
        svc.SetVisualPosition(sid, Page, Visual, x: 200, y: 300, width: 400, height: 250);

        // read the raw container to assert BOTH the container fields and layouts[0].position changed
        var store = (SessionStore?)typeof(ReportService)
            .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(svc);
        var root = store!.GetReport(sid).Layout.Root;
        var container = (JsonObject)((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!)[0]!;

        Assert.Equal(200, (double?)container["x"]);
        Assert.Equal(300, (double?)container["y"]);
        Assert.Equal(400, (double?)container["width"]);
        Assert.Equal(250, (double?)container["height"]);

        var co = JsonNode.Parse((string)container["config"]!) as JsonObject;
        var pos = (JsonObject)((JsonArray)co!["layouts"]!)[0]!["position"]!;
        Assert.Equal(200, (double?)pos["x"]);
        Assert.Equal(300, (double?)pos["y"]);
        Assert.Equal(400, (double?)pos["width"]);
        Assert.Equal(250, (double?)pos["height"]);
    }

    [Fact]
    public void SetVisualDisplay_TogglesDisplayMode()
    {
        var (svc, sid) = NewReport(ChartWithLegend());

        string DisplayMode()
        {
            var store = (SessionStore?)typeof(ReportService)
                .GetField("_sessions", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(svc);
            var root = store!.GetReport(sid).Layout.Root;
            var container = (JsonObject)((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!)[0]!;
            var co = JsonNode.Parse((string)container["config"]!) as JsonObject;
            return (string?)co!["singleVisual"]?["display"]?["mode"] ?? "<none>";
        }

        Assert.Equal("<none>", DisplayMode());
        svc.SetVisualDisplay(sid, Page, Visual, visible: false);
        Assert.Equal("hidden", DisplayMode());
        svc.SetVisualDisplay(sid, Page, Visual, visible: true);
        Assert.Equal("<none>", DisplayMode());   // hidden flag removed
    }

    [Fact]
    public void GetVisualFormat_ReturnsDecodedShape_MatchingWhatWasSet()
    {
        var (svc, sid) = NewReport(ChartWithLegend());
        svc.SetVisualTitle(sid, Page, Visual, show: false, text: null, fontColor: "#16365C", alignment: "center", fontSize: 14);
        svc.SetVisualBackground(sid, Page, Visual, show: true, color: "#FAFAFA", transparency: 50);
        svc.SetLegendShow(sid, Page, Visual, show: false, position: "Bottom");

        var got = svc.GetVisualFormat(sid, Page, Visual);

        var title = GetVc(got, "title");
        Assert.Equal("false", (string?)title["show"]);
        Assert.Equal("#16365C", (string?)title["fontColor"]);
        Assert.Equal("center", (string?)title["alignment"]);
        Assert.Equal("14", (string?)title["fontSize"]);

        var bg = GetVc(got, "background");
        Assert.Equal("true", (string?)bg["show"]);
        Assert.Equal("#FAFAFA", (string?)bg["color"]);
        Assert.Equal("50", (string?)bg["transparency"]);

        var legend = GetObjects(got, "legend");
        Assert.Equal("false", (string?)legend["show"]);
        Assert.Equal("Bottom", (string?)legend["position"]);

        // type + position are reported too
        string json = JsonSerializer.Serialize(got);
        var node = JsonNode.Parse(json) as JsonObject;
        Assert.Equal("clusteredColumnChart", (string?)node!["type"]);
        Assert.Equal(10, (double?)node["position"]!["x"]);
    }
}
