using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL flatten primitive (ReportService.FlattenVisualContainers) over representative
/// visualContainers fixtures. Proves the flatten:
///   - strips the decorative CHROME (border, dropShadow, stylePreset, visualHeader, plain opaque background),
///   - PRESERVES deliberate formatting by default - a custom-text/explicit-show title and a transparent
///     (overlay) background - the whole point of the bug fix,
///   - removeTitles=true also strips such titles (the old aggressive behaviour, opt-in),
///   - NEVER removes visualLink / action keys (the bug this tool exists to prevent),
///   - NEVER touches singleVisual.objects (data + conditional formatting),
///   - SKIPS actionButton visuals entirely,
/// and (the round-trip contract) re-stringifies each touched config as a JSON string exactly like every
/// other report edit, so a later save patches only Report/Layout and leaves the binary DataModel untouched.
/// No live .pbix is required - the transform is pure over the visualContainers JsonArray.
/// </summary>
public sealed class ClearVisualStylingTests
{
    // ---- helpers that build the SAME shape the engine writes: a visualContainer whose "config" is a
    // ---- STRINGIFIED JSON blob holding singleVisual { visualType, vcObjects, objects }. ----

    private static JsonObject Lit(string raw) =>
        new() { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = raw } } };

    // a bare chrome key: { properties: {} } - no deliberate show/text/transparency (decorative).
    private static JsonObject BareProps() =>
        new() { ["properties"] = new JsonObject() };

    // a key carrying an explicit show literal (used for action keys, and as a "deliberate" title marker).
    private static JsonObject Props() =>
        new() { ["properties"] = new JsonObject { ["show"] = Lit("true") } };

    // a DELIBERATE title with custom text - must survive the default flatten.
    private static JsonArray CustomTitle(string text) => new()
    {
        new JsonObject { ["properties"] = new JsonObject
        {
            ["show"] = Lit("true"),
            ["text"] = Lit($"'{text}'"),
        } },
    };

    // a title explicitly hidden (show=false) - e.g. on an overlay line chart - must survive too.
    private static JsonArray HiddenTitle() => new()
    {
        new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("false") } },
    };

    // a transparent (overlay) background - carries a transparency property - must survive.
    private static JsonArray OverlayBackground() => new()
    {
        new JsonObject { ["properties"] = new JsonObject
        {
            ["show"] = Lit("true"),
            ["transparency"] = Lit("100D"),
        } },
    };

    // a plain opaque background (no transparency) - decorative chrome, removed.
    private static JsonArray OpaqueBackground() => new()
    {
        new JsonObject { ["properties"] = new JsonObject
        {
            ["color"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = Lit("'#FFFFFF'") } },
        } },
    };

    private static JsonObject Container(string name, JsonObject singleVisual)
    {
        var config = new JsonObject { ["name"] = name, ["singleVisual"] = singleVisual };
        return new JsonObject
        {
            ["x"] = 0, ["y"] = 0, ["z"] = 0, ["width"] = 100, ["height"] = 100,
            ["config"] = config.ToJsonString(),   // nested-as-escaped-string, exactly like the engine
            ["filters"] = "[]",
        };
    }

    // a chart with decorative CHROME (border+dropShadow+stylePreset+visualHeader) + a plain opaque
    // background + a BARE auto-title, AND a visualLink in the SAME vcObjects bucket, plus data +
    // conditional formatting under singleVisual.objects. The default flatten strips chrome + opaque
    // background + bare title, keeps visualLink and the objects bucket.
    private static JsonObject ChartContainer()
    {
        var sv = new JsonObject
        {
            ["visualType"] = "clusteredColumnChart",
            ["vcObjects"] = new JsonObject
            {
                ["background"] = OpaqueBackground(),
                ["border"] = new JsonArray { BareProps() },
                ["dropShadow"] = new JsonArray { BareProps() },
                ["title"] = new JsonArray { BareProps() },     // bare auto-title (no text, no explicit show)
                ["stylePreset"] = new JsonArray { BareProps() },
                ["visualHeader"] = new JsonArray { BareProps() },
                ["visualLink"] = new JsonArray { Props() },    // <-- nav action sharing the bucket
            },
            ["objects"] = new JsonObject
            {
                ["dataPoint"] = new JsonArray { Props() },        // data formatting
                ["values"] = new JsonArray { Props() },           // conditional formatting lives here
            },
        };
        return Container("chart1", sv);
    }

    private static JsonObject ActionButtonContainer()
    {
        var sv = new JsonObject
        {
            ["visualType"] = "actionButton",
            ["vcObjects"] = new JsonObject
            {
                ["background"] = OpaqueBackground(),            // even decorative keys here must be left alone
                ["visualLink"] = new JsonArray { Props() },
            },
        };
        return Container("button1", sv);
    }

    private static JsonObject SingleVisualOf(JsonObject container) =>
        (JsonObject)JsonNode.Parse((string)container["config"]!)!["singleVisual"]!;

    private static JsonObject? VcOf(JsonObject container) =>
        SingleVisualOf(container)["vcObjects"] as JsonObject;

    [Fact]
    public void Flatten_StripsDecorativeChrome_ButKeepsVisualLink_OnAChart()
    {
        var chart = ChartContainer();
        var containers = new JsonArray { chart };

        var (touched, removed, preserved) = ReportService.FlattenVisualContainers(containers);

        Assert.Equal(1, touched);
        Assert.Equal(6, removed);        // background(opaque), border, dropShadow, title(bare), stylePreset, visualHeader
        Assert.Equal(1, preserved);      // visualLink left in place

        var vc = VcOf(chart);
        Assert.NotNull(vc);
        // decorative chrome gone
        Assert.False(vc!.ContainsKey("background"));
        Assert.False(vc.ContainsKey("border"));
        Assert.False(vc.ContainsKey("dropShadow"));
        Assert.False(vc.ContainsKey("title"));
        Assert.False(vc.ContainsKey("stylePreset"));
        Assert.False(vc.ContainsKey("visualHeader"));
        // the action survives - THE contract
        Assert.True(vc.ContainsKey("visualLink"));
    }

    [Fact]
    public void Flatten_DoesNotTouchActionButtons()
    {
        var button = ActionButtonContainer();
        var containers = new JsonArray { button };

        var (touched, removed, _) = ReportService.FlattenVisualContainers(containers);

        Assert.Equal(0, touched);
        Assert.Equal(0, removed);

        var vc = VcOf(button);
        Assert.NotNull(vc);
        Assert.True(vc!.ContainsKey("visualLink"));   // visualLink intact
        Assert.True(vc.ContainsKey("background"));     // actionButton left BYTE-for-byte alone
    }

    [Fact]
    public void Flatten_LeavesDataAndConditionalFormattingUntouched()
    {
        var chart = ChartContainer();
        var containers = new JsonArray { chart };

        ReportService.FlattenVisualContainers(containers);

        var objects = SingleVisualOf(chart)["objects"] as JsonObject;
        Assert.NotNull(objects);
        Assert.True(objects!.ContainsKey("dataPoint"));   // data formatting kept
        Assert.True(objects.ContainsKey("values"));        // conditional formatting kept
    }

    [Fact]
    public void Flatten_DropsTheBucket_WhenOnlyDecorativeKeysRemained()
    {
        // a chart whose vcObjects has ONLY decorative chrome (bare title + opaque bg) -> empty bucket removed.
        var sv = new JsonObject
        {
            ["visualType"] = "lineChart",
            ["vcObjects"] = new JsonObject
            {
                ["background"] = OpaqueBackground(),
                ["title"] = new JsonArray { BareProps() },
            },
        };
        var chart = Container("chart2", sv);
        var containers = new JsonArray { chart };

        var (touched, removed, preserved) = ReportService.FlattenVisualContainers(containers);

        Assert.Equal(1, touched);
        Assert.Equal(2, removed);
        Assert.Equal(0, preserved);
        Assert.Null(VcOf(chart));   // now-empty vcObjects deleted
    }

    [Fact]
    public void Flatten_KeepsTheBucket_WhenActionKeysRemain()
    {
        var chart = ChartContainer();   // has visualLink alongside the decorative keys
        var containers = new JsonArray { chart };

        ReportService.FlattenVisualContainers(containers);

        // bucket survives because visualLink still lives in it
        Assert.NotNull(VcOf(chart));
        Assert.True(VcOf(chart)!.ContainsKey("visualLink"));
    }

    [Fact]
    public void Flatten_TargetsOnlyTheNamedVisual_WhenVisualNameGiven()
    {
        var chartA = ChartContainer();          // name = chart1
        var sv = new JsonObject
        {
            ["visualType"] = "lineChart",
            ["vcObjects"] = new JsonObject { ["background"] = OpaqueBackground() },
        };
        var chartB = Container("other", sv);
        var containers = new JsonArray { chartA, chartB };

        var (touched, _, _) = ReportService.FlattenVisualContainers(containers, visualName: "chart1");

        Assert.Equal(1, touched);
        Assert.False(VcOf(chartA)!.ContainsKey("background"));   // targeted
        Assert.True(VcOf(chartB)!.ContainsKey("background"));    // untouched
    }

    // ---- the BUG FIX: deliberate formatting must survive the default flatten ----

    [Fact]
    public void Flatten_Default_PreservesACustomTitle()
    {
        // a chart with a DELIBERATE custom title alongside decorative chrome.
        var sv = new JsonObject
        {
            ["visualType"] = "clusteredColumnChart",
            ["vcObjects"] = new JsonObject
            {
                ["title"] = CustomTitle("Category Sales & Brand Share by Period"),
                ["border"] = new JsonArray { BareProps() },     // decorative chrome to remove
                ["dropShadow"] = new JsonArray { BareProps() },
            },
        };
        var chart = Container("titled", sv);
        var containers = new JsonArray { chart };

        var (touched, removed, _) = ReportService.FlattenVisualContainers(containers);

        Assert.Equal(1, touched);
        Assert.Equal(2, removed);                                // only border + dropShadow
        var vc = VcOf(chart);
        Assert.NotNull(vc);
        Assert.True(vc!.ContainsKey("title"));                  // custom title PRESERVED
        Assert.False(vc.ContainsKey("border"));
        Assert.False(vc.ContainsKey("dropShadow"));
        // and the title text is intact
        var titleProps = (vc["title"] as JsonArray)![0]!["properties"] as JsonObject;
        Assert.Equal("'Category Sales & Brand Share by Period'",
            (string?)titleProps!["text"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void Flatten_Default_PreservesAnOverlay_HiddenTitlePlusTransparentBackground()
    {
        // the overlay line chart: title show=false + a transparent background. Both must survive.
        var sv = new JsonObject
        {
            ["visualType"] = "lineChart",
            ["vcObjects"] = new JsonObject
            {
                ["title"] = HiddenTitle(),               // show=false (deliberate)
                ["background"] = OverlayBackground(),     // transparency=100 (overlay)
                ["dropShadow"] = new JsonArray { BareProps() },   // decorative -> removed
            },
        };
        var chart = Container("overlay", sv);
        var containers = new JsonArray { chart };

        var (touched, removed, _) = ReportService.FlattenVisualContainers(containers);

        Assert.Equal(1, touched);
        Assert.Equal(1, removed);                       // only dropShadow
        var vc = VcOf(chart);
        Assert.NotNull(vc);
        Assert.True(vc!.ContainsKey("title"));          // hidden title PRESERVED
        Assert.True(vc.ContainsKey("background"));      // transparent overlay PRESERVED
        Assert.False(vc.ContainsKey("dropShadow"));
    }

    [Fact]
    public void Flatten_StillRemoves_PlainBorderAndOpaqueBackground()
    {
        // even with a deliberate title present, a plain decorative border + opaque background still go.
        var sv = new JsonObject
        {
            ["visualType"] = "clusteredColumnChart",
            ["vcObjects"] = new JsonObject
            {
                ["title"] = CustomTitle("Keep Me"),
                ["border"] = new JsonArray { BareProps() },
                ["background"] = OpaqueBackground(),
            },
        };
        var chart = Container("mixed", sv);
        var containers = new JsonArray { chart };

        ReportService.FlattenVisualContainers(containers);

        var vc = VcOf(chart);
        Assert.NotNull(vc);
        Assert.True(vc!.ContainsKey("title"));          // deliberate title kept
        Assert.False(vc.ContainsKey("border"));         // plain border removed
        Assert.False(vc.ContainsKey("background"));     // opaque background removed
    }

    [Fact]
    public void Flatten_RemoveTitlesTrue_AlsoStripsADeliberateTitle()
    {
        var sv = new JsonObject
        {
            ["visualType"] = "clusteredColumnChart",
            ["vcObjects"] = new JsonObject
            {
                ["title"] = CustomTitle("Remove Me"),
                ["border"] = new JsonArray { BareProps() },
            },
        };
        var chart = Container("forced", sv);
        var containers = new JsonArray { chart };

        ReportService.FlattenVisualContainers(containers, visualName: null, removeTitles: true);

        var vc = VcOf(chart);
        // both gone -> bucket dropped
        Assert.Null(vc);
    }

    [Fact]
    public void Flatten_RoundTrips_ConfigStaysAStringifiedJsonBlob()
    {
        // the DataModel-preservation contract at the JSON-transform level: every touched config is written
        // back as a JSON STRING (not an inline object), so a later save patches only Report/Layout and the
        // binary DataModel entry is never re-encoded. (No .pbix fixture exists in the suite; this asserts the
        // exact invariant Save relies on.)
        var chart = ChartContainer();
        var containers = new JsonArray { chart };

        ReportService.FlattenVisualContainers(containers);

        // config remains a JSON string value (NOT an inline object), and it re-parses to a singleVisual
        Assert.IsNotType<JsonObject>(chart["config"]);
        var cfgStr = (string)chart["config"]!;
        var reparsed = JsonNode.Parse(cfgStr) as JsonObject;
        Assert.NotNull(reparsed);
        Assert.NotNull(reparsed!["singleVisual"]);

        // and the whole Report/Layout round-trips as UTF-16-LE without a BOM (the part encoding Save uses)
        var root = new JsonObject { ["sections"] = new JsonArray { new JsonObject { ["visualContainers"] = containers } } };
        string json = root.ToJsonString();
        byte[] bytes = new UnicodeEncoding(false, false).GetBytes(json);
        Assert.False(bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE);   // no BOM
        var decoded = JsonNode.Parse(new UnicodeEncoding(false, false).GetString(bytes)) as JsonObject;
        Assert.NotNull(decoded);
    }
}
