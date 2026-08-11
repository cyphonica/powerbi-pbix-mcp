using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the match_slicer_layout engine (ReportService.MatchSlicerLayout): LEARN the slicer row structure
/// (rows, per-row y/height/left-start/gap) from hand-fixed reference pages with median-based inference that
/// shrugs off reference noise (a slicer nudged to x=-3, top-row y drifting a few px), then APPLY it to the
/// target pages. Runs in-memory over the same JsonObject root Open() produces, so it needs no live .pbix.
/// </summary>
public sealed class MatchSlicerLayoutTests
{
    // ---- fixture builders ----------------------------------------------------------------------

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
            ["name"] = "ReportSection" + ordinal + new string('a', 18),
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

    // ---- inspection helpers --------------------------------------------------------------------

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

    private static bool Overlap((double x, double y, double w, double h) a, (double x, double y, double w, double h) b)
    {
        double ox = Math.Min(a.x + a.w, b.x + b.w) - Math.Max(a.x, b.x);
        double oy = Math.Min(a.y + a.h, b.y + b.h) - Math.Max(a.y, b.y);
        return ox > 0.5 && oy > 0.5;
    }

    private static JsonObject Obj(object result) => (JsonObject)JsonSerializer.SerializeToNode(result)!;

    /// <summary>Two noisy hand-fixed reference pages (jittered top row around y~45, one slicer off-canvas at
    /// x=-3.3, second row around y~110) + one messy target page.</summary>
    private static (ReportService svc, string sid, SessionStore store) DairyLikeReport()
    {
        var ref1 = Section("Ref A", 0,
            Slicer("r1a", x: -3.3,  y: 47.5, w: 180, h: 60,   title: "State"),
            Slicer("r1b", x: 178,   y: 48.4, w: 280, h: 60,   title: "Category"),
            Slicer("r1c", x: 460,   y: 44.5, w: 190, h: 60,   title: "Brand"),
            Slicer("r1d", x: 652,   y: 46.4, w: 250, h: 62,   title: "Segment"),
            Slicer("r1e", x: 0,     y: 112,  w: 250, h: 58.7, title: "Chart Period"),
            Slicer("r1f", x: 252,   y: 110,  w: 250, h: 58.7, title: "Promo Group"));
        var ref2 = Section("Ref B", 1,
            Slicer("r2a", x: 0,     y: 44.9, w: 128, h: 60,   title: "State"),
            Slicer("r2b", x: 128.3, y: 44.9, w: 288, h: 60,   title: "Category"),
            Slicer("r2c", x: 418,   y: 44.9, w: 160, h: 60,   title: "Brand"),
            Slicer("r2d", x: 580,   y: 44.9, w: 496, h: 60,   title: "Segment"),
            Slicer("r2e", x: 2,     y: 108,  w: 250, h: 58.7, title: "Chart Period"));
        var target = Section("Messy", 2,
            Slicer("t1", x: 300,  y: 12,  w: 170, h: 74, title: "State"),
            Slicer("t2", x: 40,   y: 3,   w: 280, h: 70, title: "Category"),      // overlaps t1
            Slicer("t3", x: 900,  y: 0,   w: 160, h: 66, title: "Brand"),
            Slicer("t4", x: 500,  y: 95,  w: 250, h: 58, title: "Chart Period"),
            Container("card", "card", x: 0, y: 200, w: 360, h: 30, title: "Data Range"));
        return NewReport(ref1, ref2, target);
    }

    // ================================================================= learning

    [Fact]
    public void MatchSlicerLayout_LearnsRowStructure_FromNoisyReferences()
    {
        var (svc, sid, _) = DairyLikeReport();

        var r = Obj(svc.MatchSlicerLayout(sid, "0,1"));

        Assert.True((bool)r["ok"]!);
        var rows = (JsonArray)r["learned"]!["rows"]!;
        Assert.Equal(2, rows.Count);                                       // top row + second row inferred

        double y1 = (double)rows[0]!["y"]!;
        Assert.InRange(y1, 44.0, 48.5);                                    // median of the jittered tops, not an outlier
        Assert.True((double)rows[0]!["leftStart"]! >= 0,
            "the x=-3.3 off-canvas reference slicer must not teach an off-canvas left start");
        Assert.InRange((double)rows[0]!["height"]!, 58.0, 62.5);           // ~60px learned height
        Assert.True((double)rows[1]!["y"]! > y1 + 55, "second row learned below the first");
    }

    [Fact]
    public void MatchSlicerLayout_ReferencePages_AreNeverModified()
    {
        var (svc, sid, store) = DairyLikeReport();
        var before = Box(store, sid, "Ref A", "r1a");

        svc.MatchSlicerLayout(sid, "0,1");

        Assert.Equal(before, Box(store, sid, "Ref A", "r1a"));             // even the noisy one stays put
    }

    // ================================================================= applying

    [Fact]
    public void MatchSlicerLayout_TargetRows_FollowLearnedStructure()
    {
        var (svc, sid, store) = DairyLikeReport();

        var r = Obj(svc.MatchSlicerLayout(sid, "0,1"));
        var rows = (JsonArray)r["learned"]!["rows"]!;
        double y1 = (double)rows[0]!["y"]!, h1 = (double)rows[0]!["height"]!;
        double y2 = (double)rows[1]!["y"]!, h2 = (double)rows[1]!["height"]!;

        foreach (var id in new[] { "t1", "t2", "t3" })                     // messy top-band slicers -> learned row 1
        {
            var b = Box(store, sid, "Messy", id);
            Assert.Equal(y1, b.y, 2);
            Assert.Equal(h1, b.h, 2);
        }
        var t4 = Box(store, sid, "Messy", "t4");                           // lower slicer -> learned row 2
        Assert.Equal(y2, t4.y, 2);
        Assert.Equal(h2, t4.h, 2);
    }

    [Fact]
    public void MatchSlicerLayout_Target_NoOverlaps_AllOnCanvas_OrderPreserved()
    {
        var (svc, sid, store) = DairyLikeReport();

        svc.MatchSlicerLayout(sid, "0,1");

        var ids = new[] { "t1", "t2", "t3", "t4" };
        var boxes = ids.ToDictionary(i => i, i => Box(store, sid, "Messy", i));
        for (int i = 0; i < ids.Length; i++)
            for (int j = i + 1; j < ids.Length; j++)
                Assert.False(Overlap(boxes[ids[i]], boxes[ids[j]]), $"{ids[i]} overlaps {ids[j]}");
        foreach (var b in boxes.Values)
        {
            Assert.True(b.x >= -0.01, "on-canvas left");
            Assert.True(b.x + b.w <= 1280.01, "on-canvas right");
        }
        // left-to-right order within the row is the slicers' existing order: t2 (x40) before t1 (x300) before t3 (x900)
        Assert.True(boxes["t2"].x < boxes["t1"].x);
        Assert.True(boxes["t1"].x < boxes["t3"].x);
    }

    [Fact]
    public void MatchSlicerLayout_NonSlicerVisuals_Untouched_WidthsPreserved()
    {
        var (svc, sid, store) = DairyLikeReport();
        var cardBefore = Box(store, sid, "Messy", "card");
        double w1 = Box(store, sid, "Messy", "t1").w;

        svc.MatchSlicerLayout(sid, "0,1");

        Assert.Equal(cardBefore, Box(store, sid, "Messy", "card"));        // non-slicers never move
        Assert.Equal(w1, Box(store, sid, "Messy", "t1").w, 2);             // width preserved (row fits the canvas)
    }

    [Fact]
    public void MatchSlicerLayout_RowWiderThanCanvas_ShrinksWidthsToFit()
    {
        var wide = Section("Wide", 2,
            Slicer("w1", x: 0,   y: 10, w: 700, h: 60, title: "A"),
            Slicer("w2", x: 400, y: 10, w: 700, h: 60, title: "B"));       // 1400px of slicer on a 1280px canvas
        var (svc, sid, store) = NewReport(
            Section("Ref A", 0,
                Slicer("ra", x: 0,   y: 45, w: 200, h: 60, title: "A"),
                Slicer("rb", x: 202, y: 45, w: 200, h: 60, title: "B")),
            wide);

        svc.MatchSlicerLayout(sid, "0");

        var b1 = Box(store, sid, "Wide", "w1"); var b2 = Box(store, sid, "Wide", "w2");
        Assert.False(Overlap(b1, b2));
        Assert.True(b2.x + b2.w <= 1280.01, "shrunk row must fit the canvas");
        Assert.True(b1.w < 700 && b2.w < 700, "widths shrink proportionally only in the cannot-fit case");
    }

    [Fact]
    public void MatchSlicerLayout_MoreTargetRowsThanLearned_ExtrapolatesByPitch()
    {
        var (svc, sid, store) = NewReport(
            Section("Ref A", 0,
                Slicer("ra", x: 0, y: 45,  w: 200, h: 60, title: "A"),
                Slicer("rb", x: 0, y: 110, w: 200, h: 58, title: "B")),    // learned pitch = 65
            Section("Deep", 1,
                Slicer("d1", x: 0, y: 0,   w: 200, h: 60, title: "A"),
                Slicer("d2", x: 0, y: 70,  w: 200, h: 60, title: "B"),
                Slicer("d3", x: 0, y: 140, w: 200, h: 60, title: "C")));   // 3 rows vs 2 learned

        var r = Obj(svc.MatchSlicerLayout(sid, "0"));

        var b1 = Box(store, sid, "Deep", "d1"); var b2 = Box(store, sid, "Deep", "d2"); var b3 = Box(store, sid, "Deep", "d3");
        Assert.True(b1.y < b2.y && b2.y < b3.y, "rows stay stacked top-to-bottom");
        Assert.False(Overlap(b1, b2)); Assert.False(Overlap(b2, b3));
        double pitch = (double)r["learned"]!["rowPitch"]!;
        Assert.Equal(b2.y + pitch, b3.y, 1);                               // third row = second + learned pitch
    }

    // ================================================================= reporting + targeting

    [Fact]
    public void MatchSlicerLayout_ReportsLearnedStructure_AndPerPageBeforeAfter()
    {
        var (svc, sid, _) = DairyLikeReport();

        var r = Obj(svc.MatchSlicerLayout(sid, "0,1"));

        Assert.NotNull(r["learned"]!["rowPitch"]);
        Assert.Equal(2, ((JsonArray)r["learned"]!["referencePages"]!).Count);
        var pages = (JsonArray)r["pages"]!;
        Assert.Single(pages);                                               // targets default = all OTHER slicer-bearing pages
        Assert.Equal("Messy", (string?)pages[0]!["page"]);
        foreach (var m in ((JsonArray)pages[0]!["moves"]!).OfType<JsonObject>())
        {
            Assert.NotNull(m["before"]!["x"]); Assert.NotNull(m["after"]!["x"]);
            Assert.NotNull(m["row"]);
        }
    }

    [Fact]
    public void MatchSlicerLayout_ExplicitTargets_ResolveByNameOrOrdinal()
    {
        var (svc, sid, _) = DairyLikeReport();

        var byName = Obj(svc.MatchSlicerLayout(sid, "Ref A,Ref B", "Messy"));
        Assert.Equal(1, (int)byName["pagesMatched"]!);

        var (svc2, sid2, _) = DairyLikeReport();
        var byOrdinal = Obj(svc2.MatchSlicerLayout(sid2, "0,1", "2"));
        Assert.Equal(1, (int)byOrdinal["pagesMatched"]!);
    }
}
