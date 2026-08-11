using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the tidy_slicer_layout engine (ReportService.TidySlicerLayout) - the conservative slicer geometry
/// cleanup: DE-OVERLAP two slicers that collide, SNAP-ALIGN slicers a few px off a shared edge, and leave an
/// already-tidy page (and any deliberate slicer-over-card layering) untouched. Runs in-memory over the same
/// JsonObject root Open() produces (config is a STRINGIFIED blob), so it needs no live .pbix.
/// </summary>
public sealed class TidySlicerLayoutTests
{
    private const string Page = "Page 1";

    // ---- fixture builders ----------------------------------------------------------------------

    private static JsonObject Lit(string value) =>
        new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = value } } };

    /// <summary>A visual container at an explicit box. Geometry is written into BOTH the container fields and the
    /// layouts[0].position mirror (the shape the engine reads/writes), with an optional title for a readable label.</summary>
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

    private static (ReportService svc, string sid, SessionStore store) NewReport(params JsonObject[] containers)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var arr = new JsonArray();
        foreach (var c in containers) arr.Add(c);
        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 20),
            ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = arr,
            ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
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

    // ---- inspection helpers --------------------------------------------------------------------

    private static (double x, double y, double w, double h) Box(SessionStore store, string sid, string id)
    {
        var section = ((JsonArray)store.GetReport(sid).Layout.Root["sections"]!).OfType<JsonObject>().First();
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
        return ox > 0.5 && oy > 0.5;   // a real (>0.5px in both axes) collision
    }

    private static JsonObject Obj(object result) => (JsonObject)JsonSerializer.SerializeToNode(result)!;

    // ================================================================= de-overlap

    [Fact]
    public void TidySlicerLayout_TwoOverlappingSlicers_NoLongerOverlap()
    {
        // two side-by-side slicers: the left one's right edge (10..190 -> right 190) pokes 40px into the right one
        var (svc, sid, store) = NewReport(
            Slicer("left",  x: 10,  y: 20, w: 180, h: 60, title: "Segment"),
            Slicer("right", x: 150, y: 20, w: 200, h: 60, title: "Category"));

        var before = Overlap(Box(store, sid, "left"), Box(store, sid, "right"));
        Assert.True(before, "fixture must start overlapping");

        var r = Obj(svc.TidySlicerLayout(sid, Page));

        Assert.True((bool)r["ok"]!);
        Assert.True((int)r["moveCount"]! >= 1);
        Assert.False(Overlap(Box(store, sid, "left"), Box(store, sid, "right")),
            "the two slicers must no longer overlap after tidy");
    }

    [Fact]
    public void TidySlicerLayout_DeOverlap_KeepsSizesAndReportsBeforeAfter()
    {
        var (svc, sid, store) = NewReport(
            Slicer("a", x: 10,  y: 20, w: 180, h: 60, title: "Segment"),
            Slicer("b", x: 150, y: 20, w: 200, h: 60, title: "Category"));

        var r = Obj(svc.TidySlicerLayout(sid, Page));
        var moves = (JsonArray)r["moves"]!;
        Assert.NotEmpty(moves);

        foreach (var m in moves.OfType<JsonObject>())
        {
            // size is never changed - width/height identical before and after
            Assert.Equal((double)m["before"]!["w"]!, (double)m["after"]!["w"]!, 3);
            Assert.Equal((double)m["before"]!["h"]!, (double)m["after"]!["h"]!, 3);
            Assert.NotNull(m["reason"]);
        }
    }

    [Fact]
    public void TidySlicerLayout_VerticallyStackedOverlap_DropsLowerSlicerDown()
    {
        // a top-row slicer and one that pokes 6px up into its bottom (full-width horizontal overlap)
        var (svc, sid, store) = NewReport(
            Slicer("top",    x: 40, y: 0,  w: 250, h: 74, title: "Manufacturer"),
            Slicer("bottom", x: 40, y: 68, w: 250, h: 58, title: "Segment"));

        svc.TidySlicerLayout(sid, Page);

        Assert.False(Overlap(Box(store, sid, "top"), Box(store, sid, "bottom")));
    }

    // ================================================================= already-tidy = unchanged

    [Fact]
    public void TidySlicerLayout_AlreadyTidyPage_NoMoves()
    {
        // three non-overlapping, cleanly aligned slicers in a row (shared top edge, shared left grid)
        var (svc, sid, store) = NewReport(
            Slicer("s1", x: 0,   y: 0, w: 190, h: 60, title: "State"),
            Slicer("s2", x: 200, y: 0, w: 190, h: 60, title: "Segment"),
            Slicer("s3", x: 400, y: 0, w: 190, h: 60, title: "Brand"));

        var b1 = Box(store, sid, "s1"); var b2 = Box(store, sid, "s2"); var b3 = Box(store, sid, "s3");

        var r = Obj(svc.TidySlicerLayout(sid, Page));

        Assert.Equal(0, (int)r["moveCount"]!);
        Assert.Equal(b1, Box(store, sid, "s1"));
        Assert.Equal(b2, Box(store, sid, "s2"));
        Assert.Equal(b3, Box(store, sid, "s3"));
    }

    // ================================================================= snap-align

    [Fact]
    public void TidySlicerLayout_LeftEdgeDrift_SnapsToSharedEdge()
    {
        // a vertically-stacked pair whose left edges drift sub-pixel (200.0 vs 200.37) - no overlap, pure drift
        var (svc, sid, store) = NewReport(
            Slicer("upper", x: 200.0,  y: 0,  w: 250, h: 60, title: "Segment"),
            Slicer("lower", x: 200.37, y: 70, w: 250, h: 60, title: "Sub-Category"));

        svc.TidySlicerLayout(sid, Page);

        Assert.Equal(Box(store, sid, "upper").x, Box(store, sid, "lower").x, 3);   // left edges now equal
    }

    // ================================================================= slicer-over-non-slicer is flagged, not moved

    [Fact]
    public void TidySlicerLayout_SlicerOverCard_Flagged_NotMovedByDefault()
    {
        // a State slicer deliberately layered over a wider date-label card - the pervasive header pattern
        var (svc, sid, store) = NewReport(
            Slicer("state", x: 0, y: 0, w: 180, h: 30, title: "State"),
            Container("card", "card", x: 0, y: 0, w: 360, h: 30, title: "Data Range"));

        var beforeSlicer = Box(store, sid, "state");
        var beforeCard   = Box(store, sid, "card");

        var r = Obj(svc.TidySlicerLayout(sid, Page));

        Assert.Equal(0, (int)r["moveCount"]!);                 // nothing moved
        Assert.True((int)r["flaggedCount"]! >= 1);             // but the overlap is surfaced
        Assert.Equal(beforeSlicer, Box(store, sid, "state"));  // slicer untouched
        Assert.Equal(beforeCard,   Box(store, sid, "card"));   // card untouched (non-slicers never move)
    }

    [Fact]
    public void TidySlicerLayout_SlicerOverCard_OptIn_NudgesSlicerClear()
    {
        var (svc, sid, store) = NewReport(
            Slicer("state", x: 40, y: 0, w: 180, h: 30, title: "State"),
            Container("card", "card", x: 0, y: 0, w: 360, h: 30, title: "Data Range"));

        svc.TidySlicerLayout(sid, Page, deOverlapNonSlicers: true);

        Assert.False(Overlap(Box(store, sid, "state"), Box(store, sid, "card")));
        Assert.Equal((0.0, 0.0, 360.0, 30.0), Box(store, sid, "card"));   // the card itself still never moves
    }
}
