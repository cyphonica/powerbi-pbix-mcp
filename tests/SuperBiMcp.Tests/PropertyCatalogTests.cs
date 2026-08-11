using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using SuperBiMcp.Tools;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive tests for the visual-property REGISTRY (PropertyCatalog) and its tool wrappers.
/// Drives the REAL catalogue built from the bundled reportThemeSchema-*.json embedded resource - if the
/// embed or the $ref/allOf resolver breaks, these fail. Proves:
///   - the registry loads from the bundled schema and exposes &gt;= 40 visual types,
///   - commonCards (title/background/border) resolve onto an arbitrary visual,
///   - a known visual (lineChart) exposes its expected cards (legend, valueAxis, categoryAxis, dataPoint),
///   - list_visual_properties returns a property carrying a type,
///   - validate flags an unknown card/property and passes a known one,
///   - set_visual_format still applies AND surfaces a non-fatal warning for an unknown property,
///   - the strict flag rejects on unknown without applying.
/// </summary>
public sealed class PropertyCatalogTests
{
    private static readonly PropertyCatalog Catalog = new();   // built once, schema parsed lazily on first use

    // ---------------------------------------------------------------- registry loads from the bundled schema

    [Fact]
    public void Registry_LoadsFromBundledSchema_WithAtLeast40VisualTypes()
    {
        // proves the EmbeddedResource is present and the parse/resolve produced a non-empty registry.
        Assert.True(Catalog.VisualTypes.Count >= 40,
            $"expected >= 40 visual types from the bundled schema, got {Catalog.VisualTypes.Count}");
        Assert.NotEqual("unknown", Catalog.SchemaVersion);   // version parsed from the resource name
        Assert.Equal("2.155", Catalog.SchemaVersion);
    }

    [Fact]
    public void ListVisualTypes_ReportsDataAndContainerKinds()
    {
        var node = ToNode(Catalog.ListVisualTypes());
        Assert.True((int)node!["count"]! >= 40);
        Assert.True((int)node["dataVisuals"]! > 0);
        Assert.True((int)node["containers"]! > 0);
        var keys = (node["visualTypes"] as JsonArray)!.Select(v => (string?)v!["visualType"]).ToList();
        Assert.Contains("lineChart", keys);
        Assert.Contains("tableEx", keys);
        Assert.Contains("slicer", keys);
    }

    // ---------------------------------------------------------------- commonCards resolve onto any visual

    [Theory]
    [InlineData("slicer")]
    [InlineData("pieChart")]
    [InlineData("tableEx")]
    public void CommonCards_ResolveOntoArbitraryVisual(string visualType)
    {
        var v = Catalog.Get(visualType);
        Assert.NotNull(v);
        foreach (var card in new[] { "title", "background", "border" })
        {
            Assert.True(v!.Cards.ContainsKey(card), $"{visualType} should inherit commonCard '{card}'");
            Assert.True(v.Cards[card].Common, $"{visualType}.{card} should be flagged as a common card");
        }
        // title carries a 'show' bool and 'text' text property
        var title = v!.Cards["title"];
        Assert.True(title.Properties.ContainsKey("show"));
        Assert.Equal("bool", title.Properties["show"].Type);
    }

    // ---------------------------------------------------------------- a known visual's expected cards

    [Fact]
    public void LineChart_ExposesExpectedCards()
    {
        var v = Catalog.Get("lineChart");
        Assert.NotNull(v);
        foreach (var card in new[] { "legend", "valueAxis", "categoryAxis", "dataPoint" })
            Assert.True(v!.Cards.ContainsKey(card), $"lineChart should expose card '{card}'");

        // legend.position is an enum carrying values (e.g. Top/Bottom/Right)
        var pos = v!.Cards["legend"].Properties["position"];
        Assert.Equal("enum", pos.Type);
        Assert.NotNull(pos.Enum);
        Assert.Contains("Bottom", pos.Enum!);

        // a fontSize-typed property resolves to a number with the schema's 6..45 range
        var fs = v.Cards["legend"].Properties["fontSize"];
        Assert.Equal("number", fs.Type);
        Assert.Equal(6, fs.Min);
        Assert.Equal(45, fs.Max);

        // labelColor resolves via $ref fill -> colour
        Assert.Equal("color", v.Cards["legend"].Properties["labelColor"].Type);
    }

    // ---------------------------------------------------------------- list_visual_properties returns a typed property

    [Fact]
    public void ListVisualProperties_ReturnsPropertyWithAType()
    {
        var node = ToNode(Catalog.ListVisualProperties("lineChart"));
        Assert.True((int)node!["cardCount"]! > 0);
        Assert.True((int)node["propertyCount"]! > 0);

        var cards = (node["cards"] as JsonArray)!;
        var anyProp = cards
            .SelectMany(c => (c!["properties"] as JsonArray)!)
            .First();
        Assert.False(string.IsNullOrWhiteSpace((string?)anyProp!["name"]));
        Assert.False(string.IsNullOrWhiteSpace((string?)anyProp["type"]));
    }

    [Fact]
    public void GetVisualSchema_ScopesToASingleCard()
    {
        var node = ToNode(Catalog.ListVisualProperties("lineChart", "legend"));
        Assert.Equal(1, (int)node!["cardCount"]!);
        Assert.Equal("legend", (string?)(node["cards"] as JsonArray)![0]!["card"]);
    }

    // ---------------------------------------------------------------- validation

    [Fact]
    public void Validate_FlagsUnknownCardAndProperty()
    {
        string fmt = """
        { "objects": {
            "legend": { "show": true, "notARealProp": 1 },
            "notARealCard": { "x": 1 }
        } }
        """;
        var r = Catalog.Validate("lineChart", fmt);
        Assert.False(r.UnknownVisualType);
        Assert.False(r.Valid);
        Assert.Contains(r.Warnings, w => w.Contains("notARealCard"));
        Assert.Contains(r.Warnings, w => w.Contains("legend.notARealProp"));
    }

    [Fact]
    public void Validate_PassesAKnownCardAndProperty()
    {
        string fmt = """{ "objects": { "legend": { "show": true, "position": "Bottom" } } }""";
        var r = Catalog.Validate("lineChart", fmt);
        Assert.False(r.UnknownVisualType);
        Assert.True(r.Valid, "known card+property should produce no warnings: " + string.Join("; ", r.Warnings));
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Validate_FlagsEnumAndRangeMismatches()
    {
        string fmt = """{ "objects": { "legend": { "position": "Sideways", "fontSize": 999 } } }""";
        var r = Catalog.Validate("lineChart", fmt);
        Assert.Contains(r.Warnings, w => w.Contains("position") && w.Contains("Sideways"));
        Assert.Contains(r.Warnings, w => w.Contains("fontSize") && w.Contains("maximum"));
    }

    [Fact]
    public void Validate_UnknownVisualType_IsNotAnError()
    {
        var r = Catalog.Validate("someCustomVisualGuid12345", """{ "objects": { "whatever": { "x": 1 } } }""");
        Assert.True(r.UnknownVisualType);
        Assert.Empty(r.Warnings);   // advisory: not validatable, never fails
    }

    // ---------------------------------------------------------------- set_visual_format still works + warns

    [Fact]
    public void SetVisualFormat_Applies_AndSurfacesWarningForUnknownProperty()
    {
        var (report, sid) = NewLineChartReport();

        // a known title + an UNKNOWN property on a known card
        string fmt = """
        { "vcObjects": { "title": { "show": true, "text": "Hi", "totallyBogusProp": 5 } } }
        """;
        string json = ReportTools.SetVisualFormat(report, Catalog, sid, Page, Visual, fmt);
        var res = JsonNode.Parse(json) as JsonObject;

        // the format STILL applied (non-fatal) ...
        Assert.True((bool?)res!["validated"] == true);
        Assert.True((int)res["warningCount"]! >= 1);
        Assert.Contains((res["warnings"] as JsonArray)!, w => ((string?)w)!.Contains("totallyBogusProp"));

        // ... and the real engine wrote the title (set->get round-trip)
        var got = JsonNode.Parse(JsonSerializer.Serialize(report.GetVisualFormat(sid, Page, Visual))) as JsonObject;
        var title = got!["vcObjects"]!["title"] as JsonObject;
        Assert.Equal("Hi", (string?)title!["text"]);
    }

    [Fact]
    public void SetVisualFormat_Strict_RejectsUnknownWithoutApplying()
    {
        var (report, sid) = NewLineChartReport();
        string fmt = """{ "vcObjects": { "title": { "bogusProp": 1 } } }""";
        string json = ReportTools.SetVisualFormat(report, Catalog, sid, Page, Visual, fmt, strict: true);
        var res = JsonNode.Parse(json) as JsonObject;

        Assert.True((bool?)res!["ok"] == false);
        Assert.True((bool?)res["applied"] == false);

        // nothing was written: the title card has no bogusProp
        var got = JsonNode.Parse(JsonSerializer.Serialize(report.GetVisualFormat(sid, Page, Visual))) as JsonObject;
        var titleVc = (got!["vcObjects"] as JsonObject)?["title"] as JsonObject;
        Assert.True(titleVc is null || !titleVc.ContainsKey("bogusProp"));
    }

    [Fact]
    public void SetVisualFormat_KnownFormat_NoWarnings()
    {
        var (report, sid) = NewLineChartReport();
        string fmt = """{ "objects": { "legend": { "show": false, "position": "Bottom" } } }""";
        string json = ReportTools.SetVisualFormat(report, Catalog, sid, Page, Visual, fmt);
        var res = JsonNode.Parse(json) as JsonObject;
        Assert.True((bool?)res!["validated"] == true);
        Assert.Equal(0, (int)res["warningCount"]!);
    }

    // ---------------------------------------------------------------- in-memory report harness

    private const string Page = "Page1";
    private const string Visual = "v1";

    private static JsonNode? ToNode(object o) => JsonNode.Parse(JsonSerializer.Serialize(o));

    private static (ReportService svc, string sid) NewLineChartReport()
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var singleVisual = new JsonObject { ["visualType"] = "lineChart", ["objects"] = new JsonObject() };
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
}
