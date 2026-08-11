using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL interactions-and-filters parity engine (ReportService page/report filters, remove,
/// edit-interactions, filter pane, lock/hide, slicer sync) over an in-memory Report/Layout - the same
/// JsonObject root Open() produces. Every transform is pure over the layout, so no live .pbix is needed.
/// Each test asserts the produced PBIX JSON lands in the right place and carries the right shape:
///   - add_page_filter -> a filter object in section.filters,
///   - add_report_filter -> a filter object in the top-level layout.filters,
///   - categorical filters -> a Categorical In(...) filter; comparison -> an Advanced filter,
///   - remove_filter -> the matching filter is gone at the right scope,
///   - set_visual_interactions -> a { source, target, type } override in the page config relationships,
///   - set_filter_pane -> outspacePane visible/expanded literals on the right config bucket,
///   - lock_filter / hide_filter -> isLockedInViewMode / isHiddenInViewMode booleans on the filter,
///   - sync_slicer -> a syncGroup { groupName, fieldChanges, filterChanges } on the slicer's singleVisual.
/// </summary>
public sealed class ReportInteractionsFilterTests
{
    private const string Page = "Page1";

    // ---- fixture builders: the SAME shape the engine writes (config is a STRINGIFIED blob) ----

    // a slicer visualContainer with a real singleVisual.prototypeQuery so sync_slicer can read the field.
    private static JsonObject SlicerContainer(string id, string table, string field)
    {
        var singleVisual = new JsonObject
        {
            ["visualType"] = "slicer",
            ["prototypeQuery"] = new JsonObject
            {
                ["Version"] = 2,
                ["From"] = new JsonArray { new JsonObject { ["Name"] = "t", ["Entity"] = table, ["Type"] = 0 } },
                ["Select"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Column"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = "t" } },
                            ["Property"] = field,
                        },
                        ["Name"] = $"{table}.{field}",
                    },
                },
            },
        };
        return Container(id, singleVisual);
    }

    private static JsonObject SimpleContainer(string id, string visualType)
        => Container(id, new JsonObject { ["visualType"] = visualType });

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

    private static JsonArray ParseArr(string? s) => JsonNode.Parse(s ?? "[]") as JsonArray ?? new JsonArray();
    private static JsonObject ParseObj(string? s) => JsonNode.Parse(s ?? "{}") as JsonObject ?? new JsonObject();

    // the visual's parsed config (round-trip read of the stringified blob)
    private static JsonObject VisualConfig(SessionStore store, string sid, string id) =>
        ((JsonArray)Section(store, sid)["visualContainers"]!)
            .Select(vc => ParseObj((string?)vc!["config"]))
            .First(c => (string?)c["name"] == id);

    // ====================================================================== add_page_filter

    [Fact]
    public void AddPageFilter_Categorical_LandsInSectionFilters_AsCategoricalInFilter()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "tableEx"));

        svc.AddPageFilter(sid, Page, "Dim_Product", "Brand", "categorical", "column", null,
            new[] { "Anchor", "Mainland" }, "string");

        var filters = ParseArr((string?)Section(store, sid)["filters"]);
        Assert.Single(filters);
        var f = (JsonObject)filters[0]!;

        // stored at PAGE scope, as a Categorical filter on the right column
        Assert.Equal("Categorical", (string?)f["type"]);
        Assert.Equal("Dim_Product", (string?)f["expression"]!["Column"]!["Expression"]!["SourceRef"]!["Entity"]);
        Assert.Equal("Brand", (string?)f["expression"]!["Column"]!["Property"]);

        // the In(...) condition carries BOTH values as one-element literal rows
        var inObj = (JsonObject)f["filter"]!["Where"]![0]!["Condition"]!["In"]!;
        var values = (JsonArray)inObj["Values"]!;
        Assert.Equal(2, values.Count);
        Assert.Equal("'Anchor'", (string?)values[0]![0]!["Literal"]!["Value"]);
        Assert.Equal("'Mainland'", (string?)values[1]![0]!["Literal"]!["Value"]);
        // the In expression targets the same column via the From alias
        Assert.Equal("Brand", (string?)inObj["Expressions"]![0]!["Column"]!["Property"]);
    }

    [Fact]
    public void AddPageFilter_Comparison_WritesAdvancedFilter_OnMeasure()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "tableEx"));

        svc.AddPageFilter(sid, Page, "Fact", "Sales", "comparison", "measure", "gt",
            new[] { "100" }, "decimal");

        var f = (JsonObject)ParseArr((string?)Section(store, sid)["filters"])[0]!;
        Assert.Equal("Advanced", (string?)f["type"]);
        // a measure filter references Measure (not Column) in the expression
        Assert.NotNull(f["expression"]!["Measure"]);
        var cmp = (JsonObject)f["filter"]!["Where"]![0]!["Condition"]!["Comparison"]!;
        Assert.Equal(1, cmp["ComparisonKind"]!.GetValue<int>());   // gt
        Assert.Equal("100M", (string?)cmp["Right"]!["Literal"]!["Value"]);   // decimal -> M (hardened encoder)
    }

    // ====================================================================== add_report_filter

    [Fact]
    public void AddReportFilter_LandsInTopLevelLayoutFilters_NotInAnyPage()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "tableEx"));

        svc.AddReportFilter(sid, "Dim_Geo", "Region", "categorical", "column", null,
            new[] { "North" }, "string");

        // report scope: the top-level layout.filters got it
        var reportFilters = ParseArr((string?)Root(store, sid)["filters"]);
        Assert.Single(reportFilters);
        Assert.Equal("Region", (string?)((JsonObject)reportFilters[0]!)["expression"]!["Column"]!["Property"]);

        // and the page's own filters stayed empty (proves scope isolation)
        Assert.Empty(ParseArr((string?)Section(store, sid)["filters"]));
    }

    // ====================================================================== remove_filter

    [Fact]
    public void RemoveFilter_Page_RemovesOnlyTheMatchingFilter()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "tableEx"));
        svc.AddPageFilter(sid, Page, "Dim_Product", "Brand", "categorical", "column", null, new[] { "Anchor" }, "string");
        svc.AddPageFilter(sid, Page, "Dim_Product", "Segment", "categorical", "column", null, new[] { "Cheese" }, "string");
        Assert.Equal(2, ParseArr((string?)Section(store, sid)["filters"]).Count);

        var result = svc.RemoveFilter(sid, "page", Page, null, "Dim_Product", "Brand");
        Assert.Equal(1, Pick(result, "removed").GetInt32());

        var remaining = ParseArr((string?)Section(store, sid)["filters"]);
        Assert.Single(remaining);
        Assert.Equal("Segment", (string?)((JsonObject)remaining[0]!)["expression"]!["Column"]!["Property"]);
    }

    [Fact]
    public void RemoveFilter_Report_RemovesFromTopLevel()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "tableEx"));
        svc.AddReportFilter(sid, "Dim_Geo", "Region", "categorical", "column", null, new[] { "North" }, "string");

        svc.RemoveFilter(sid, "report", null, null, "Dim_Geo", "Region");
        Assert.Empty(ParseArr((string?)Root(store, sid)["filters"]));
    }

    // ====================================================================== set_visual_interactions

    [Fact]
    public void SetVisualInteractions_WritesRelationshipOverride_WithCorrectSourceTargetType()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("src", "slicer"), SimpleContainer("tgt", "clusteredBarChart"));

        svc.SetVisualInteractions(sid, Page, "src", "tgt", "none");

        var cfg = ParseObj((string?)Section(store, sid)["config"]);
        var rels = (JsonArray)cfg["relationships"]!;
        Assert.Single(rels);
        var r = (JsonObject)rels[0]!;
        Assert.Equal("src", (string?)r["source"]);
        Assert.Equal("tgt", (string?)r["target"]);
        Assert.Equal(3, r["type"]!.GetValue<int>());   // none = 3
    }

    [Fact]
    public void SetVisualInteractions_UpdatesExistingOverride_InPlace_NoDuplicate()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("src", "slicer"), SimpleContainer("tgt", "card"));

        svc.SetVisualInteractions(sid, Page, "src", "tgt", "filter");     // 1
        svc.SetVisualInteractions(sid, Page, "src", "tgt", "highlight");  // -> updates to 2, no new entry

        var rels = (JsonArray)ParseObj((string?)Section(store, sid)["config"])["relationships"]!;
        Assert.Single(rels);
        Assert.Equal(2, ((JsonObject)rels[0]!)["type"]!.GetValue<int>());
    }

    [Fact]
    public void SetVisualInteractions_UnknownVisual_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("src", "slicer"));
        Assert.ThrowsAny<Exception>(() => svc.SetVisualInteractions(sid, Page, "src", "missing", "filter"));
    }

    // ====================================================================== set_filter_pane

    [Fact]
    public void SetFilterPane_Page_WritesOutspacePaneLiterals_OnSectionConfig()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetFilterPane(sid, Page, visible: false, expanded: true);

        var props = (JsonObject)ParseObj((string?)Section(store, sid)["config"])
            ["objects"]!["outspacePane"]![0]!["properties"]!;
        Assert.Equal("false", (string?)props["visible"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("true", (string?)props["expanded"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetFilterPane_ReportWide_WritesOnLayoutConfig_NotOnPage()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "card"));

        svc.SetFilterPane(sid, null, visible: false, expanded: null);

        // report-level config carries it
        var reportProps = (JsonObject)ParseObj((string?)Root(store, sid)["config"])
            ["objects"]!["outspacePane"]![0]!["properties"]!;
        Assert.Equal("false", (string?)reportProps["visible"]!["expr"]!["Literal"]!["Value"]);
        Assert.Null(reportProps["expanded"]);   // only visible was set

        // the page config stayed empty
        Assert.Null(ParseObj((string?)Section(store, sid)["config"])["objects"]);
    }

    // ====================================================================== lock_filter / hide_filter

    [Fact]
    public void LockAndHideFilter_SetFlagsOnTheMatchingPageFilter()
    {
        var (svc, sid, store) = NewReport(SimpleContainer("a1", "tableEx"));
        svc.AddPageFilter(sid, Page, "Dim_Product", "Brand", "categorical", "column", null, new[] { "Anchor" }, "string");

        svc.SetFilterFlags(sid, "page", Page, null, "Dim_Product", "Brand", locked: true, hidden: null);
        svc.SetFilterFlags(sid, "page", Page, null, "Dim_Product", "Brand", locked: null, hidden: true);

        var f = (JsonObject)ParseArr((string?)Section(store, sid)["filters"])[0]!;
        Assert.True((bool)f["isLockedInViewMode"]!);
        Assert.True((bool)f["isHiddenInViewMode"]!);
    }

    [Fact]
    public void LockFilter_NoMatchingFilter_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("a1", "tableEx"));
        Assert.ThrowsAny<Exception>(() =>
            svc.SetFilterFlags(sid, "page", Page, null, "Dim_Product", "Brand", locked: true, hidden: null));
    }

    // ====================================================================== sync_slicer

    [Fact]
    public void SyncSlicer_WritesSyncGroupOnSingleVisual_DefaultGroupIsField()
    {
        var (svc, sid, store) = NewReport(SlicerContainer("s1", "Dim_Calendar", "Year"));

        svc.SyncSlicer(sid, Page, "s1", null, fieldChanges: true, filterChanges: true);

        var sg = (JsonObject)VisualConfig(store, sid, "s1")["singleVisual"]!["syncGroup"]!;
        Assert.Equal("Year", (string?)sg["groupName"]);   // defaulted to the bound field
        Assert.True((bool)sg["fieldChanges"]!);
        Assert.True((bool)sg["filterChanges"]!);
    }

    [Fact]
    public void SyncSlicer_ExplicitGroupName_IsUsed()
    {
        var (svc, sid, store) = NewReport(SlicerContainer("s1", "Dim_Calendar", "Year"));

        svc.SyncSlicer(sid, Page, "s1", "DateSync", fieldChanges: true, filterChanges: false);

        var sg = (JsonObject)VisualConfig(store, sid, "s1")["singleVisual"]!["syncGroup"]!;
        Assert.Equal("DateSync", (string?)sg["groupName"]);
        Assert.False((bool)sg["filterChanges"]!);
    }

    [Fact]
    public void SyncSlicer_NonSlicer_Throws()
    {
        var (svc, sid, _) = NewReport(SimpleContainer("c1", "card"));
        Assert.ThrowsAny<Exception>(() => svc.SyncSlicer(sid, Page, "c1", null, true, true));
    }

    // ---- tiny JSON helper to read engine result objects ----
    private static JsonElement Pick(object result, string prop)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(result));
        return doc.RootElement.GetProperty(prop).Clone();
    }
}
