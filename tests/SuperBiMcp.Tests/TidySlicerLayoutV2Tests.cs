using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the tidy_slicer_layout_v2 engine (ReportService.TidySlicerLayoutV2): row detection, top-align to
/// the shared row baseline (the row's MEDIAN y), keep-on-canvas clamp (the reference dashboards carried a
/// slicer at x=-3.3), and even-gap spacing within each row. Positions only - sizes never change - and only
/// slicers ever move. Runs in-memory over the same JsonObject root Open() produces, so it needs no live .pbix.
/// </summary>
public sealed class TidySlicerLayoutV2Tests
{
    // ---- fixture builders (mirrors MatchSlicerLayoutTests) --------------------------------------

    private static JsonObject Lit(string value) =>
        new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = value } } };

    private static JsonObject Container(string id, string visualType, double x, double y, double w, double h, string? title = null)
    {
        var sv = new JsonObject { ["visualType"] = visualType, ["drillFilterOtherVisuals"] = true };
        if (title != null)
            sv["vcObjects"] = new JsonObject
            {
                ["title"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                    { ["text"] = Lit($"'{title}'"), ["show"] = Lit("true") } } },
            };

        var config = new JsonObject
        {
            ["name"] = id,
            ["layouts"] = new JsonArray { new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                { ["x"] = x, ["y"] = y, ["z"] = 0, ["width"] = w, ["height"] = h, ["tabOrder"] = 0 } } },
            ["singleVisual"] = sv,
        };
        return new JsonObject
        {
            ["x"] = x, ["y"] = y, ["z"] = 0, ["width"] = w, ["height"] = h,
            ["config"] = config.ToJsonString(),
            ["filters"] = "[]",
        };
    }

    private static JsonObject Slicer(string id, double x, double y, double w, double h, string? title = null)
        => Container(id, "slicer", x, y, w, h, title);

    private static JsonObject Section(string displayName, int ordinal, params JsonObject[] containers)
    {
        var arr = new JsonArray();
        foreach (var c in containers) arr.Add(c);
        return new JsonObject
        {
            ["name"] = "ReportSection" + ordinal + new string('b', 18),
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

    private static (double x, double y, double w, double h) Box(SessionStore store, string sid, string page, string id)
    {
        var section = ((JsonArray)store.GetReport(sid).Layout.Root["sections"]!).OfType<JsonObject>()
            .First(s => (string?)s["displayName"] == page);
        var vc = ((JsonArray)section["visualContainers"]!).OfType<JsonObject>()
            .First(v => (string?)JsonNode.Parse((string)v["config"]!)!["name"] == id);
        var pos = (JsonObject)JsonNode.Parse((string)vc["config"]!)!["layouts"]![0]!["position"]!;
        return (pos["x"]!.GetValue<double>(), pos["y"]!.GetValue<double>(),
                pos["width"]!.GetValue<double>(), pos["height"]!.GetValue<double>());
    }

    private static JsonObject Obj(object result) => (JsonObject)JsonSerializer.SerializeToNode(result)!;

    /// <summary>A real-report-page-0-like mess: jittered top row (y 44.5-48.4) with one slicer off-canvas at
    /// x=-3.3, a second row near y~110, plus a card that must never move.</summary>
    private static (ReportService svc, string sid, SessionStore store) NoisyPage()
    {
        var page = Section("Overview", 0,
            Slicer("s1", x: -3.3, y: 47.5, w: 180, h: 60,   title: "State"),
            Slicer("s2", x: 190,  y: 48.4, w: 280, h: 60,   title: "Category"),
            Slicer("s3", x: 480,  y: 44.5, w: 190, h: 60,   title: "Brand"),
            Slicer("s4", x: 700,  y: 44.9, w: 250, h: 60,   title: "Segment"),
            Slicer("s5", x: 0,    y: 112,  w: 250, h: 58.7, title: "Chart Period"),
            Slicer("s6", x: 260,  y: 108,  w: 250, h: 58.7, title: "Promo Group"),
            Container("card", "card", x: 0, y: 200, w: 360, h: 30, title: "Data Range"));
        return NewReport(page);
    }

    // ================================================================= row detection + top-align

    [Fact]
    public void V2_TopAligns_EachRow_ToItsMedianBaseline()
    {
        var (svc, sid, store) = NoisyPage();

        var r = Obj(svc.TidySlicerLayoutV2(sid, "Overview"));

        Assert.True((bool)r["ok"]!);
        Assert.Equal(2, (int)r["rowCount"]!);                              // top band + second band, one row each

        // top row: median of {47.5, 48.4, 44.5, 44.9} = 46.2 - every member lands exactly there
        double expected = 46.2;
        foreach (var id in new[] { "s1", "s2", "s3", "s4" })
            Assert.Equal(expected, Box(store, sid, "Overview", id).y, 2);

        // second row: median of {112, 108} = 110
        Assert.Equal(110, Box(store, sid, "Overview", "s5").y, 2);
        Assert.Equal(110, Box(store, sid, "Overview", "s6").y, 2);
    }

    // ================================================================= keep-on-canvas clamp

    [Fact]
    public void V2_ClampsOffCanvasSlicer_BackOntoCanvas()
    {
        var (svc, sid, store) = NoisyPage();

        var r = Obj(svc.TidySlicerLayoutV2(sid, "Overview"));

        var s1 = Box(store, sid, "Overview", "s1");                        // was x=-3.3
        Assert.True(s1.x >= -0.01, "the x=-3.3 slicer must be pulled back on-canvas");
        var moves = (JsonArray)r["moves"]!;
        Assert.Contains(moves.OfType<JsonObject>(), m =>
            (string?)m["slicer"] == "State" && ((string)m["reason"]!).Contains("on-canvas"));
    }

    [Fact]
    public void V2_PullsRowHangingOffRightEdge_BackOn_WithoutResizing()
    {
        var (svc, sid, store) = NewReport(Section("Right", 0,
            Slicer("a", x: 800,  y: 40, w: 200, h: 60, title: "A"),
            Slicer("b", x: 1150, y: 40, w: 200, h: 60, title: "B")));      // right edge at 1350 > 1280

        svc.TidySlicerLayoutV2(sid, "Right");

        var a = Box(store, sid, "Right", "a"); var b = Box(store, sid, "Right", "b");
        Assert.True(b.x + b.w <= 1280.01, "row pulled back inside the canvas");
        Assert.True(a.x >= -0.01);
        Assert.Equal(200, a.w, 2); Assert.Equal(200, b.w, 2);              // sizes NEVER change
    }

    // ================================================================= even-gap spacing

    [Fact]
    public void V2_EvenGapSpacing_UniformGaps_NoOverlaps_OrderPreserved()
    {
        var (svc, sid, store) = NewReport(Section("Gaps", 0,
            Slicer("g1", x: 0,   y: 45, w: 200, h: 60, title: "A"),
            Slicer("g2", x: 195, y: 45, w: 200, h: 60, title: "B"),        // overlaps g1 by 5px
            Slicer("g3", x: 500, y: 45, w: 200, h: 60, title: "C"),        // gap 105
            Slicer("g4", x: 712, y: 45, w: 200, h: 60, title: "D")));      // gap 12

        svc.TidySlicerLayoutV2(sid, "Gaps");

        var b1 = Box(store, sid, "Gaps", "g1"); var b2 = Box(store, sid, "Gaps", "g2");
        var b3 = Box(store, sid, "Gaps", "g3"); var b4 = Box(store, sid, "Gaps", "g4");
        double gap1 = b2.x - (b1.x + b1.w), gap2 = b3.x - (b2.x + b2.w), gap3 = b4.x - (b3.x + b3.w);
        Assert.Equal(gap1, gap2, 1);                                       // one uniform gap across the row
        Assert.Equal(gap2, gap3, 1);
        Assert.True(gap1 >= -0.01, "no overlaps remain");
        Assert.True(b1.x < b2.x && b2.x < b3.x && b3.x < b4.x, "left-to-right order preserved");
        Assert.True(b4.x + b4.w <= 1280.01, "row stays on-canvas");
    }

    [Fact]
    public void V2_EvenGap_IsMedianOfExistingGaps_OutlierIgnored()
    {
        var (svc, sid, store) = NewReport(Section("Med", 0,
            Slicer("m1", x: 0,   y: 45, w: 100, h: 60, title: "A"),
            Slicer("m2", x: 110, y: 45, w: 100, h: 60, title: "B"),        // gap 10
            Slicer("m3", x: 220, y: 45, w: 100, h: 60, title: "C"),        // gap 10
            Slicer("m4", x: 700, y: 45, w: 100, h: 60, title: "D")));      // gap 380 (outlier)

        svc.TidySlicerLayoutV2(sid, "Med");

        var b3 = Box(store, sid, "Med", "m3"); var b4 = Box(store, sid, "Med", "m4");
        Assert.Equal(10, b4.x - (b3.x + b3.w), 1);                         // median gap (10), not the 380 outlier
    }

    // ================================================================= already conforming

    [Fact]
    public void V2_AlreadyConformingPage_IsLeftUnchanged()
    {
        var (svc, sid, store) = NewReport(Section("Clean", 0,
            Slicer("c1", x: 0,   y: 44.9, w: 128, h: 60, title: "State"),
            Slicer("c2", x: 132, y: 44.9, w: 288, h: 60, title: "Category"),
            Slicer("c3", x: 424, y: 44.9, w: 160, h: 60, title: "Brand"),
            Slicer("c4", x: 588, y: 108,  w: 250, h: 58.7, title: "Chart Period")));
        var before = new[] { "c1", "c2", "c3", "c4" }.ToDictionary(i => i, i => Box(store, sid, "Clean", i));

        var r = Obj(svc.TidySlicerLayoutV2(sid, "Clean"));

        Assert.Equal(0, (int)r["moveCount"]!);
        foreach (var id in before.Keys)
            Assert.Equal(before[id], Box(store, sid, "Clean", id));
    }

    // ================================================================= safety rails

    [Fact]
    public void V2_NonSlicerVisuals_NeverMove_And_SizesNeverChange()
    {
        var (svc, sid, store) = NoisyPage();
        var cardBefore = Box(store, sid, "Overview", "card");
        var sizesBefore = new[] { "s1", "s2", "s3", "s4", "s5", "s6" }
            .ToDictionary(i => i, i => { var b = Box(store, sid, "Overview", i); return (b.w, b.h); });

        svc.TidySlicerLayoutV2(sid, "Overview");

        Assert.Equal(cardBefore, Box(store, sid, "Overview", "card"));
        foreach (var id in sizesBefore.Keys)
        {
            var b = Box(store, sid, "Overview", id);
            Assert.Equal(sizesBefore[id].w, b.w, 2);
            Assert.Equal(sizesBefore[id].h, b.h, 2);
        }
    }

    [Fact]
    public void V2_RowWiderThanCanvas_IsFlagged_NotResized()
    {
        var (svc, sid, store) = NewReport(Section("TooWide", 0,
            Slicer("w1", x: 0,   y: 40, w: 700, h: 60, title: "A"),
            Slicer("w2", x: 400, y: 40, w: 700, h: 60, title: "B")));      // 1400px of slicer on a 1280px canvas

        var r = Obj(svc.TidySlicerLayoutV2(sid, "TooWide"));

        Assert.True((int)r["flaggedCount"]! >= 1, "impossible row must be flagged");
        Assert.Equal(700, Box(store, sid, "TooWide", "w1").w, 2);          // never resized
        Assert.Equal(700, Box(store, sid, "TooWide", "w2").w, 2);
    }

    [Fact]
    public void V2_AllPages_WhenNoPageGiven_And_MovesCarryBeforeAfter()
    {
        var (svc, sid, _) = NewReport(
            Section("P1", 0, Slicer("p1a", x: -5, y: 40, w: 200, h: 60, title: "A")),
            Section("P2", 1, Slicer("p2a", x: 10, y: 40, w: 200, h: 60, title: "B"),
                             Slicer("p2b", x: 205, y: 47, w: 200, h: 60, title: "C")));

        var r = Obj(svc.TidySlicerLayoutV2(sid));

        Assert.Equal(2, (int)r["pagesScanned"]!);
        Assert.True((int)r["moveCount"]! >= 2);                            // the clamp on P1 + the align on P2
        foreach (var m in ((JsonArray)r["moves"]!).OfType<JsonObject>())
        {
            Assert.NotNull(m["page"]); Assert.NotNull(m["reason"]);
            Assert.NotNull(m["before"]!["x"]); Assert.NotNull(m["after"]!["x"]);
        }
    }
}
