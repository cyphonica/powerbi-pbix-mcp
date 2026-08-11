using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL Wave M structured/data-bound visual builders + the universal-encoder fix over an
/// in-memory Report/Layout (the same JsonObject root Open() produces). Fault-sensitive: each test asserts
/// the exact JSON shape the engine writes, so a regression breaks a specific test. The encoder tests are
/// the load-bearing ones - they FAIL if a nested object is stringified into a broken literal.
/// </summary>
public sealed class WaveMVisualBuildersTests
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

    // the live singleVisual the engine mutated (re-parsed from the stringified container config)
    private static JsonObject SingleVisual(SessionStore store, string sid)
    {
        var root = store.GetReport(sid).Layout.Root;
        var container = (JsonObject)((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!)[0]!;
        var co = JsonNode.Parse((string)container["config"]!) as JsonObject;
        return (JsonObject)co!["singleVisual"]!;
    }

    private static JsonObject FirstProps(SessionStore store, string sid, string bucket, string card)
    {
        var sv = SingleVisual(store, sid);
        var arr = (JsonArray)sv[bucket]![card]!;
        return (JsonObject)arr[0]!["properties"]!;
    }

    // ====================================================================== THE CRITICAL ENCODER FIX

    [Fact]
    public void SetVisualFormat_WritesNestedObjectVerbatim_NotStringified()
    {
        var (svc, sid, store) = NewReport();
        // a property whose value is a NESTED object lacking a top-level expr/solid key - the old encoder
        // stringified this into a broken literal. The fix must deep-clone it in verbatim.
        string formatJson = """
        {
          "objects": {
            "plotArea": { "image": { "image": { "url": { "expr": { "ResourcePackageItem": { "ItemName": "logo.png" } } }, "scaling": "Fit" } } }
          }
        }
        """;
        svc.SetVisualFormat(sid, Page, "v1", formatJson);

        var image = FirstProps(store, sid, "objects", "plotArea")["image"];
        // FAULT-SENSITIVE: the value must STILL be a JSON object, not a stringified literal.
        Assert.IsType<JsonObject>(image);
        Assert.Equal("logo.png", (string?)image!["image"]!["url"]!["expr"]!["ResourcePackageItem"]!["ItemName"]);
        // it must NOT have been mangled into a Literal string (the bug signature)
        Assert.Null(image["expr"]?["Literal"]);
    }

    [Fact]
    public void SetVisualFormat_WritesArrayValueVerbatim()
    {
        var (svc, sid, store) = NewReport();
        string formatJson = """
        { "objects": { "general": { "paragraphs": [ { "textRuns": [ { "value": "hi" } ] } ] } } }
        """;
        svc.SetVisualFormat(sid, Page, "v1", formatJson);

        var paragraphs = FirstProps(store, sid, "objects", "general")["paragraphs"];
        Assert.IsType<JsonArray>(paragraphs);
        Assert.Equal("hi", (string?)((JsonArray)paragraphs!)[0]!["textRuns"]![0]!["value"]);
    }

    [Fact]
    public void SetVisualFormat_ScalarsStillEncodeThroughTheLiteralEncoder()
    {
        var (svc, sid, store) = NewReport();
        svc.SetVisualFormat(sid, Page, "v1", """{ "vcObjects": { "title": { "show": true, "text": "Hi", "fontSize": 14 } } }""");

        var title = FirstProps(store, sid, "vcObjects", "title");
        // scalars are STILL wrapped as expr-literals (the encoder path is unchanged for true scalars)
        Assert.Equal("true", (string?)title["show"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Hi'", (string?)title["text"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("14D", (string?)title["fontSize"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetVisualFormat_MeasureBoundShorthand_EmitsMeasureNode()
    {
        var (svc, sid, store) = NewReport();
        // drive a property by a measure via the { measure:"Table[Measure]" } shorthand
        svc.SetVisualFormat(sid, Page, "v1", """{ "vcObjects": { "title": { "titleText": { "measure": "Sales[Title Text]" } } } }""");

        var node = FirstProps(store, sid, "vcObjects", "title")["titleText"];
        Assert.Equal("Sales", (string?)node!["expr"]!["Measure"]!["Expression"]!["SourceRef"]!["Entity"]);
        Assert.Equal("Title Text", (string?)node["expr"]!["Measure"]!["Property"]);
    }

    [Fact]
    public void SetVisualFormat_ColumnBoundShorthand_WithSeparateTable_EmitsColumnNode()
    {
        var (svc, sid, store) = NewReport();
        svc.SetVisualFormat(sid, Page, "v1", """{ "objects": { "dataPoint": { "fill": { "column": "Colour", "table": "Dim" } } } }""");

        var node = FirstProps(store, sid, "objects", "dataPoint")["fill"];
        Assert.Equal("Dim", (string?)node!["expr"]!["Column"]!["Expression"]!["SourceRef"]!["Entity"]);
        Assert.Equal("Colour", (string?)node["expr"]!["Column"]!["Property"]);
    }

    [Fact]
    public void Selector_WritesNestedObjectVerbatim_PerXAxisCategory()
    {
        var (svc, sid, store) = NewReport();
        // a dataViewWildcard selector = per-X-axis-category formatting; the property value is a structured fill.
        string selector = """{ "data": [ { "dataViewWildcard": { "matchingOption": 0 } } ] }""";
        string props = """{ "fill": { "solid": { "color": { "expr": { "Literal": { "Value": "'#FF0000'" } } } } } }""";
        svc.SetVisualFormatSelector(sid, Page, "v1", "objects", "dataPoint", props, selector);

        var arr = (JsonArray)SingleVisual(store, sid)["objects"]!["dataPoint"]!;
        var entry = (JsonObject)arr[0]!;
        // the selector is the per-category wildcard
        Assert.NotNull(entry["selector"]!["data"]![0]!["dataViewWildcard"]);
        // the fill landed verbatim (solid.color.expr), NOT stringified
        var fill = entry["properties"]!["fill"];
        Assert.IsType<JsonObject>(fill);
        Assert.Equal("'#FF0000'", (string?)fill!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
    }

    // ====================================================================== validator warnings

    [Fact]
    public void Validator_Warns_WhenScalarGivenToObjectProperty()
    {
        var catalog = new PropertyCatalog();
        // find a real visual type + an object-typed property from the bundled schema, then feed it a scalar.
        var (vt, card, prop) = FindObjectProp(catalog);
        if (vt is null) return;   // schema has no object-typed property (unlikely) - nothing to assert

        string formatJson = $$"""{ "objects": { "{{card}}": { "{{prop}}": "oops-a-scalar" } } }""";
        var r = catalog.Validate(vt!, formatJson);
        Assert.Contains(r.Warnings, w => w.Contains(prop!) && w.Contains("structured object"));
    }

    [Fact]
    public void Validator_Warns_WhenObjectGivenToScalarBoolProperty()
    {
        var catalog = new PropertyCatalog();
        // "show" is a bool on essentially every card; a nested object where a bool is expected is malformed.
        var vt = catalog.VisualTypes.FirstOrDefault(t =>
            catalog.Get(t)?.Cards.Values.Any(c => c.Properties.TryGetValue("show", out var pi) && pi.Type == "bool") == true);
        if (vt is null) return;
        string card = catalog.Get(vt)!.Cards.Values.First(c => c.Properties.TryGetValue("show", out var pi) && pi.Type == "bool").Name;

        string formatJson = $$"""{ "objects": { "{{card}}": { "show": { "nested": true } } } }""";
        var r = catalog.Validate(vt, formatJson);
        Assert.Contains(r.Warnings, w => w.Contains("show") && w.Contains("nested object"));
    }

    [Fact]
    public void Validator_DoesNotWarn_OnWrappedExprOrMeasureShorthand()
    {
        var catalog = new PropertyCatalog();
        var vt = catalog.VisualTypes.FirstOrDefault(t =>
            catalog.Get(t)?.Cards.Values.Any(c => c.Properties.TryGetValue("show", out var pi) && pi.Type == "bool") == true);
        if (vt is null) return;
        string card = catalog.Get(vt)!.Cards.Values.First(c => c.Properties.TryGetValue("show", out var pi) && pi.Type == "bool").Name;

        // an already-wrapped expr value for a bool prop is taken on trust (no warning about the value type)
        string formatJson = $$"""{ "objects": { "{{card}}": { "show": { "expr": { "Literal": { "Value": "true" } } } } } }""";
        var r = catalog.Validate(vt, formatJson);
        Assert.DoesNotContain(r.Warnings, w => w.Contains("show") && w.Contains("nested object"));
    }

    private static (string? vt, string? card, string? prop) FindObjectProp(PropertyCatalog catalog)
    {
        foreach (var vt in catalog.VisualTypes)
            foreach (var c in catalog.Get(vt)!.Cards.Values)
                foreach (var p in c.Properties.Values)
                    if (p.Type == "object") return (vt, c.Name, p.Name);
        return (null, null, null);
    }

    // ====================================================================== dedicated builders

    [Fact]
    public void SetPlotAreaImage_FromUrl_WritesPlotAreaImageObject()
    {
        var (svc, sid, store) = NewReport();
        svc.SetPlotAreaImage(sid, Page, "v1", "https://example.com/bg.png", "Fill", 25);

        var props = FirstProps(store, sid, "objects", "plotArea");
        var img = (JsonObject)props["image"]!["image"]!;
        Assert.Equal("'https://example.com/bg.png'", (string?)img["url"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Fill'", (string?)img["scaling"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("25D", (string?)props["transparency"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetImageSource_OnNonImageVisual_Throws()
    {
        var (svc, sid, _) = NewReport("clusteredColumnChart");
        Assert.Throws<InvalidOperationException>(() => svc.SetImageSource(sid, Page, "v1", "https://x/y.png"));
    }

    [Fact]
    public void SetImageSource_OnImageVisual_RewritesSourceFile()
    {
        var (svc, sid, store) = NewReport("image");
        // seed an existing image card with a scaling, then re-source it.
        svc.SetVisualFormat(sid, Page, "v1",
            """{ "objects": { "image": { "sourceFile": { "image": { "scaling": { "expr": { "Literal": { "Value": "'Fit'" } } } } } } } }""");
        svc.SetImageSource(sid, Page, "v1", "https://example.com/new.png");

        var img = (JsonObject)FirstProps(store, sid, "objects", "image")["sourceFile"]!["image"]!;
        Assert.Equal("'https://example.com/new.png'", (string?)img["url"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Fit'", (string?)img["scaling"]!["expr"]!["Literal"]!["Value"]);   // preserved
    }

    [Fact]
    public void SetTextboxContent_WritesMultipleStyledRuns()
    {
        var (svc, sid, store) = NewReport("textbox");
        var runs = JsonNode.Parse("""
        [
          { "text": "Bold ", "bold": true, "color": "#16365C", "fontSize": 18 },
          { "text": "link", "url": "https://x", "italic": true }
        ]
        """) as JsonArray;
        svc.SetTextboxContent(sid, Page, "v1", runs!, bulleted: true);

        var para = (JsonObject)((JsonArray)FirstProps(store, sid, "objects", "general")["paragraphs"]!)[0]!;
        Assert.Equal("Bullet", (string?)para["listType"]);
        var tr = (JsonArray)para["textRuns"]!;
        Assert.Equal(2, tr.Count);
        Assert.Equal("bold", (string?)tr[0]!["textStyle"]!["fontWeight"]);
        Assert.Equal("18pt", (string?)tr[0]!["textStyle"]!["fontSize"]);
        Assert.Equal("https://x", (string?)tr[1]!["url"]);
        Assert.Equal("italic", (string?)tr[1]!["textStyle"]!["fontStyle"]);
    }

    [Fact]
    public void SetGradientColor_WritesMeasureDrivenGradientOnDataPointFill()
    {
        var (svc, sid, store) = NewReport("treemap");
        svc.SetGradientColor(sid, Page, "v1", "dataPoint", "Fact", "Sales", "#FFFFFF", "#FF0000", "#FFCC00",
            min: 0, center: 50, max: 100);

        var fill = FirstProps(store, sid, "objects", "dataPoint")["fillColor"]!;
        var fr = fill["solid"]!["color"]!["expr"]!["FillRule"]!;
        Assert.Equal("Sales", (string?)fr["Input"]!["Measure"]!["Property"]);
        var g3 = fr["FillRule"]!["linearGradient3"]!;
        Assert.Equal("'#FFFFFF'", (string?)g3["min"]!["color"]!["Literal"]!["Value"]);
        Assert.Equal("'#FFCC00'", (string?)g3["mid"]!["color"]!["Literal"]!["Value"]);
        Assert.Equal("'#FF0000'", (string?)g3["max"]!["color"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetMapConditionalFormatting_Rules_WritesRuleFillRule()
    {
        var (svc, sid, store) = NewReport("filledMap");
        var rules = new List<(double, double, string)> { (0, 100, "#00FF00"), (100, 200, "#FF0000") };
        svc.SetMapConditionalFormatting(sid, Page, "v1", "Fact", "Sales", rules, null, "fill");

        var fr = FirstProps(store, sid, "objects", "dataPoint")["fillColor"]!["solid"]!["color"]!["expr"]!["FillRule"]!;
        var ruleArr = (JsonArray)fr["FillRule"]!["ruleDefinition"]!["rules"]!;
        Assert.Equal(2, ruleArr.Count);
        Assert.Equal("'#00FF00'", (string?)ruleArr[0]!["Color"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetMapConditionalFormatting_Gradient_WritesGradientFillRule()
    {
        var (svc, sid, store) = NewReport("filledMap");
        svc.SetMapConditionalFormatting(sid, Page, "v1", "Fact", "Sales", null,
            ("#FFFFFF", "#0000FF", null, null, null, null), "fill");

        var fr = FirstProps(store, sid, "objects", "dataPoint")["fillColor"]!["solid"]!["color"]!["expr"]!["FillRule"]!;
        Assert.NotNull(fr["FillRule"]!["linearGradient2"]);
    }

    [Fact]
    public void SetMapConditionalFormatting_NoRulesNoGradient_Throws()
    {
        var (svc, sid, _) = NewReport("filledMap");
        Assert.Throws<ArgumentException>(() =>
            svc.SetMapConditionalFormatting(sid, Page, "v1", "Fact", "Sales", null, null, "fill"));
    }

    [Fact]
    public void SetAzureMapLayerSource_ReferenceUrl_WritesReferenceLayer()
    {
        var (svc, sid, store) = NewReport("azureMap");
        svc.SetAzureMapLayerSource(sid, Page, "v1", "reference", "https://example.com/regions.geojson");

        var props = FirstProps(store, sid, "objects", "referenceLayer");
        Assert.Equal("'https://example.com/regions.geojson'", (string?)props["url"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Url'", (string?)props["dataLocation"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetAzureMapLayerSource_TileTemplate_WritesTileLayer()
    {
        var (svc, sid, store) = NewReport("azureMap");
        svc.SetAzureMapLayerSource(sid, Page, "v1", "tile", "https://t/{z}/{x}/{y}.png");

        var props = FirstProps(store, sid, "objects", "tileLayer");
        Assert.Equal("'https://t/{z}/{x}/{y}.png'", (string?)props["tileUrl"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetAzureMapLayerSource_InlineGeojson_WritesInlineData()
    {
        var (svc, sid, store) = NewReport("azureMap");
        svc.SetAzureMapLayerSource(sid, Page, "v1", "reference", """{"type":"FeatureCollection"}""");
        var props = FirstProps(store, sid, "objects", "referenceLayer");
        Assert.Equal("'Inline'", (string?)props["dataLocation"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetShapeMapCustomMap_InlineJson_WritesCustomMap()
    {
        var (svc, sid, store) = NewReport("filledMap");   // shapeMap-style; in-memory only checks structure
        svc.SetShapeMapCustomMap(sid, Page, "v1", """{"type":"Topology"}""");
        var props = FirstProps(store, sid, "objects", "shape");
        Assert.Equal("'Custom'", (string?)props["mapType"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddErrorBars_ByField_WritesBoundMeasures()
    {
        var (svc, sid, store) = NewReport("lineChart");
        svc.AddErrorBars(sid, Page, "v1", "byField", "Fact", "Upper", "Lower", null, "Relative", false, "Both");

        var props = FirstProps(store, sid, "objects", "errorBars");
        Assert.Equal("'ByField'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("Upper", (string?)props["upperBound"]!["expr"]!["Measure"]!["Property"]);
        Assert.Equal("Lower", (string?)props["lowerBound"]!["expr"]!["Measure"]!["Property"]);
        Assert.Equal("'Relative'", (string?)props["relationship"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Both'", (string?)props["displayType"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddErrorBars_ByPercentage_WritesPercent()
    {
        var (svc, sid, store) = NewReport("lineChart");
        svc.AddErrorBars(sid, Page, "v1", "byPercentage", null, null, null, 10, "Absolute", true, null);

        var props = FirstProps(store, sid, "objects", "errorBars");
        Assert.Equal("'ByPercentage'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("10D", (string?)props["percentageValue"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddErrorBars_ByPercentage_WithoutPercent_Throws()
    {
        var (svc, sid, _) = NewReport("lineChart");
        Assert.Throws<ArgumentException>(() =>
            svc.AddErrorBars(sid, Page, "v1", "byPercentage", null, null, null, null, "Absolute", null, null));
    }

    [Fact]
    public void AddAnomalyDetection_WritesAnomaliesObject()
    {
        var (svc, sid, store) = NewReport("lineChart");
        svc.AddAnomalyDetection(sid, Page, "v1", 70, new[] { "Dim.Region", "Dim.Channel" });

        var props = FirstProps(store, sid, "objects", "anomalyDetection");
        Assert.Equal("true", (string?)props["show"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("70D", (string?)props["sensitivity"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal(2, ((JsonArray)props["explainBy"]!).Count);
    }

    [Fact]
    public void SetForecast_WritesFullForecastObject()
    {
        var (svc, sid, store) = NewReport("lineChart");
        svc.SetForecast(sid, Page, "v1", 12, "Month", 2, 0.95, 12);

        var props = FirstProps(store, sid, "objects", "forecast");
        Assert.Equal("12D", (string?)props["forecastLength"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Month'", (string?)props["forecastUnits"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("2D", (string?)props["ignoreLast"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("0.95D", (string?)props["confidenceBand"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetPlayAxis_BindsPlayProjectionAndSelect()
    {
        var (svc, sid, store) = NewReport("scatterChart");
        svc.SetPlayAxis(sid, Page, "v1", "Calendar.Year");

        var sv = SingleVisual(store, sid);
        var play = (JsonArray)sv["projections"]!["Play"]!;
        Assert.Equal("Calendar.Year", (string?)play[0]!["queryRef"]);
        var sel = (JsonArray)sv["prototypeQuery"]!["Select"]!;
        Assert.Contains(sel.OfType<JsonObject>(), s => (string?)s["Name"] == "Calendar.Year"
            && (string?)s["Column"]!["Property"] == "Year");
    }

    [Fact]
    public void SetPlayAxis_BadField_Throws()
    {
        var (svc, sid, _) = NewReport("scatterChart");
        Assert.Throws<ArgumentException>(() => svc.SetPlayAxis(sid, Page, "v1", "NoDotHere"));
    }

    [Fact]
    public void SetCardImage_FromUrl_WritesImageSourceFile()
    {
        var (svc, sid, store) = NewReport("cardVisual");
        svc.SetCardImage(sid, Page, "v1", "https://example.com/hero.png", "Fill");

        var props = FirstProps(store, sid, "objects", "image");
        Assert.Equal("true", (string?)props["show"]!["expr"]!["Literal"]!["Value"]);
        var img = (JsonObject)props["sourceFile"]!["image"]!;
        Assert.Equal("'https://example.com/hero.png'", (string?)img["url"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Fill'", (string?)img["scaling"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetSlicerConditionalFormatting_Fill_WritesRuleFillOnItemsCard()
    {
        var (svc, sid, store) = NewReport("slicer");
        var rules = new List<(double, double, string)> { (0, 50, "#EEEEEE"), (50, 100, "#1B8A4B") };
        svc.SetSlicerConditionalFormatting(sid, Page, "v1", "fill", "Fact", "Score", rules);

        var fr = FirstProps(store, sid, "objects", "items")["fill"]!["solid"]!["color"]!["expr"]!["FillRule"]!;
        Assert.Equal("Score", (string?)fr["Input"]!["Measure"]!["Property"]);
        Assert.Equal(2, ((JsonArray)fr["FillRule"]!["ruleDefinition"]!["rules"]!).Count);
    }

    [Fact]
    public void SetSlicerConditionalFormatting_UnknownTarget_Throws()
    {
        var (svc, sid, _) = NewReport("slicer");
        var rules = new List<(double, double, string)> { (0, 1, "#FFFFFF") };
        Assert.Throws<ArgumentException>(() =>
            svc.SetSlicerConditionalFormatting(sid, Page, "v1", "bogus", "Fact", "Score", rules));
    }
}
