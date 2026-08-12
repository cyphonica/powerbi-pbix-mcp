using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL Wave N report-feature gap-closers (bookmark DATA-state capture, page rename/reorder/
/// resize/preset, advanced filter types, field-value/web-url CF, data-bar fix, cross-report drillthrough,
/// tooltip bindings, button action types, mobile show/hide, page tab order + show-no-data, org custom
/// visual) over an in-memory Report/Layout - the same JsonObject root Open() produces. Fault-sensitive:
/// each test asserts the exact JSON shape the engine writes, so a regression breaks a specific test.
/// </summary>
public sealed class ReportWaveNTests
{
    private const string Page = "Page1";

    // a visualContainer whose "config" is a STRINGIFIED { name, layouts, singleVisual } blob, plus an
    // optional prototypeQuery/projections so the data-state capture has something to snapshot.
    private static JsonObject Container(string id, string visualType, double x = 16, double y = 20,
        double z = 0, double width = 100, double height = 80,
        JsonObject? prototypeQuery = null, JsonObject? projections = null, JsonObject? objects = null,
        string filters = "[]")
    {
        var sv = new JsonObject { ["visualType"] = visualType };
        if (prototypeQuery != null) sv["prototypeQuery"] = prototypeQuery;
        if (projections != null) sv["projections"] = projections;
        if (objects != null) sv["objects"] = objects;

        var config = new JsonObject
        {
            ["name"] = id,
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                    { ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = width, ["height"] = height, ["tabOrder"] = (int)z } },
            },
            ["singleVisual"] = sv,
        };
        return new JsonObject
        {
            ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = width, ["height"] = height,
            ["config"] = config.ToJsonString(),
            ["filters"] = filters,
        };
    }

    private record V(string Id, string Type, string Filters = "[]");

    // convenience overload: no page filters, just visuals (or none).
    private static (ReportService svc, string sid, SessionStore store) NewReport(params V[] visuals)
        => NewReportCore("[]", visuals);

    private static (ReportService svc, string sid, SessionStore store) NewReport(
        string pageFilters, params V[] visuals)
        => NewReportCore(pageFilters, visuals);

    private static (ReportService svc, string sid, SessionStore store) NewReportCore(
        string pageFilters, V[] visuals)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var containers = new JsonArray();
        foreach (var v in visuals) containers.Add(Container(v.Id, v.Type, filters: v.Filters));

        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 32),
            ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = containers,
            ["config"] = "{}", ["filters"] = pageFilters, ["width"] = 1280, ["height"] = 720, ["displayOption"] = 1,
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
    private static JsonObject Section(SessionStore store, string sid, string? display = null) =>
        SectionsOf(store, sid).OfType<JsonObject>().First(s => (string?)s["displayName"] == (display ?? Page));
    private static JsonArray Containers(SessionStore store, string sid) =>
        (JsonArray)Section(store, sid)["visualContainers"]!;
    private static JsonObject? ConfigOf(SessionStore store, string sid, string id) =>
        Containers(store, sid).OfType<JsonObject>()
            .Select(vc => JsonNode.Parse((string)vc["config"]!) as JsonObject)
            .FirstOrDefault(co => (string?)co!["name"] == id);
    private static JsonObject ContainerOf(SessionStore store, string sid, string id) =>
        Containers(store, sid).OfType<JsonObject>()
            .First(vc => (string?)(JsonNode.Parse((string)vc["config"]!) as JsonObject)!["name"] == id);
    private static JsonObject SingleVisual(SessionStore store, string sid, string id) =>
        (JsonObject)ConfigOf(store, sid, id)!["singleVisual"]!;
    private static JsonObject ReportConfig(SessionStore store, string sid) =>
        JsonNode.Parse((string)Root(store, sid)["config"]!) as JsonObject ?? new JsonObject();
    private static JsonObject SectionConfig(SessionStore store, string sid, string? display = null) =>
        JsonNode.Parse((string)Section(store, sid, display)["config"]!) as JsonObject ?? new JsonObject();

    private static string Pck(object result, string prop) =>
        (string?)(JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject)![prop] ?? "";

    private static double D(JsonNode? n)
    {
        try { return n!.GetValue<double>(); }
        catch { return n!.GetValue<int>(); }
    }

    // a categorical filter blob (the same shape the filter builders write), used as a "live slicer value".
    private static string CategoricalFilters(string table, string field, params string[] values)
    {
        var rows = new JsonArray();
        foreach (var v in values) rows.Add(new JsonArray { new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{v}'" } } });
        var f = new JsonObject
        {
            ["name"] = "f" + field,
            ["expression"] = new JsonObject { ["Column"] = new JsonObject {
                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } }, ["Property"] = field } },
            ["type"] = "Categorical",
        };
        return new JsonArray { f }.ToJsonString();
    }

    // ====================================================================== 1. bookmark DATA-state capture

    [Fact]
    public void AddBookmark_CaptureData_SnapshotsPageFilters_Sort_Drill_Highlight()
    {
        // a visual carrying a live SORT (OrderBy), a DRILL position (projections + expansionStates), a
        // cross-HIGHLIGHT, and its own filter; plus a page-level slicer filter.
        var pq = new JsonObject
        {
            ["Version"] = 2,
            ["OrderBy"] = new JsonArray { new JsonObject { ["Direction"] = 2,
                ["Expression"] = new JsonObject { ["Measure"] = new JsonObject { ["Property"] = "Sales" } } } },
        };
        var projections = new JsonObject { ["Rows"] = new JsonArray { new JsonObject { ["queryRef"] = "Dim.Brand" } } };

        var (svc, sid, store) = NewReport(
            pageFilters: CategoricalFilters("Dim", "Region", "North"),
            new V("v1", "pivotTable", Filters: CategoricalFilters("Dim", "Channel", "Web")));

        // give v1 a sort, drill state and a highlight by mutating its config directly (simulating a live state).
        var container = ContainerOf(store, sid, "v1");
        var co = JsonNode.Parse((string)container["config"]!) as JsonObject;
        var sv = (JsonObject)co!["singleVisual"]!;
        sv["prototypeQuery"] = pq;
        sv["projections"] = projections;
        sv["expansionStates"] = new JsonArray { new JsonObject { ["roles"] = new JsonArray { "Rows" } } };
        co["highlight"] = new JsonObject { ["selection"] = new JsonArray { new JsonObject { ["dataMap"] = new JsonObject() } } };
        container["config"] = co.ToJsonString();

        var result = svc.AddBookmark(sid, Page, "Filtered View", Array.Empty<string>(), captureData: true);
        string bmName = Pck(result, "name");

        var bookmarks = (JsonArray)ReportConfig(store, sid)["bookmarks"]!;
        var bm = bookmarks.OfType<JsonObject>().First(b => (string?)b["name"] == bmName);
        var secState = (JsonObject)bm["explorationState"]!["sections"]!.AsObject().First().Value!;

        // PAGE filter/slicer values captured into the SectionState.filterConfig
        var pageFilters = (JsonArray)secState["filterConfig"]!["filters"]!;
        Assert.Equal("Region", (string?)pageFilters[0]!["expression"]!["Column"]!["Property"]);

        var vState = (JsonObject)secState["visualContainers"]!["v1"]!;
        // SORT captured
        Assert.Equal(2, vState["singleVisual"]!["orderBy"]![0]!["Direction"]!.GetValue<int>());
        // DRILL captured (activeProjections + expansionStates)
        Assert.Equal("Dim.Brand", (string?)vState["singleVisual"]!["activeProjections"]!["Rows"]![0]!["queryRef"]);
        Assert.NotNull(vState["singleVisual"]!["expansionStates"]);
        // cross-HIGHLIGHT captured
        Assert.NotNull(vState["highlight"]!["selection"]);
        // VISUAL filter/slicer values captured
        Assert.Equal("Channel", (string?)vState["filterConfig"]!["filters"]![0]!["expression"]!["Column"]!["Property"]);
        // display.mode still present (show, since not hidden)
        Assert.Equal("show", (string?)vState["singleVisual"]!["display"]!["mode"]);
    }

    [Fact]
    public void AddBookmark_WithoutCaptureData_RecordsDisplayOnly_NoDataState()
    {
        var (svc, sid, store) = NewReport(
            pageFilters: CategoricalFilters("Dim", "Region", "North"),
            new V("v1", "pivotTable", Filters: CategoricalFilters("Dim", "Channel", "Web")));

        string bmName = Pck(svc.AddBookmark(sid, Page, "Display View", Array.Empty<string>(), captureData: false), "name");

        var bookmarks = (JsonArray)ReportConfig(store, sid)["bookmarks"]!;
        var bm = bookmarks.OfType<JsonObject>().First(b => (string?)b["name"] == bmName);
        var secState = (JsonObject)bm["explorationState"]!["sections"]!.AsObject().First().Value!;

        // FAULT-SENSITIVE: display-only bookmark must NOT capture the data state.
        Assert.Null(secState["filterConfig"]);
        var vState = (JsonObject)secState["visualContainers"]!["v1"]!;
        Assert.Null(vState["filterConfig"]);
        Assert.Null(vState["singleVisual"]!["orderBy"]);
        // but display.mode is still recorded
        Assert.Equal("show", (string?)vState["singleVisual"]!["display"]!["mode"]);
    }

    [Fact]
    public void SetBookmarkDataState_ReSnapshots_AndKeepsHiddenSet()
    {
        var (svc, sid, store) = NewReport(
            pageFilters: "[]",
            new V("v1", "pivotTable"), new V("v2", "card"));
        // a display-only bookmark hiding v2
        string bmName = Pck(svc.AddBookmark(sid, Page, "View", new[] { "v2" }, captureData: false), "name");

        // now a page filter appears (a viewer picked a slicer), then we re-snapshot data state.
        Section(store, sid)["filters"] = CategoricalFilters("Dim", "Year", "2024");
        svc.SetBookmarkDataState(sid, bmName, null);

        var bookmarks = (JsonArray)ReportConfig(store, sid)["bookmarks"]!;
        var bm = bookmarks.OfType<JsonObject>().First(b => (string?)b["name"] == bmName);
        var secState = (JsonObject)bm["explorationState"]!["sections"]!.AsObject().First().Value!;

        // the freshly-captured page filter is present
        Assert.Equal("Year", (string?)secState["filterConfig"]!["filters"]![0]!["expression"]!["Column"]!["Property"]);
        // and the hidden set is preserved (v2 still hidden, v1 shown)
        Assert.Equal("hidden", (string?)secState["visualContainers"]!["v2"]!["singleVisual"]!["display"]!["mode"]);
        Assert.Equal("show", (string?)secState["visualContainers"]!["v1"]!["singleVisual"]!["display"]!["mode"]);
    }

    // ====================================================================== 2. pages

    [Fact]
    public void RenamePage_SetsDisplayName_KeepsInternalName()
    {
        var (svc, sid, store) = NewReport();
        string internalName = (string)Section(store, sid)["name"]!;
        svc.RenamePage(sid, Page, "Executive Summary");

        var s = SectionsOf(store, sid).OfType<JsonObject>().First(x => (string?)x["name"] == internalName);
        Assert.Equal("Executive Summary", (string?)s["displayName"]);
        Assert.Equal(internalName, (string?)s["name"]);   // internal name unchanged
    }

    [Fact]
    public void ReorderPages_AssignsOrdinalsInGivenOrder()
    {
        var (svc, sid, store) = NewReport();
        // add two more pages
        string p2 = Pck(svc.AddPage(sid, "Two", 1280, 720), "pageName");
        string p3 = Pck(svc.AddPage(sid, "Three", 1280, 720), "pageName");
        // reorder: Three, then Page1 (Two not listed -> appended)
        svc.ReorderPages(sid, new[] { "Three", Page });

        int OrdOf(string display) => (int)D(SectionsOf(store, sid).OfType<JsonObject>()
            .First(s => (string?)s["displayName"] == display)["ordinal"]);
        Assert.Equal(0, OrdOf("Three"));
        Assert.Equal(1, OrdOf(Page));
        Assert.Equal(2, OrdOf("Two"));   // unlisted page appended last
    }

    [Fact]
    public void ResizePage_SetsSectionSizeAndPageSizeObject()
    {
        var (svc, sid, store) = NewReport();
        svc.ResizePage(sid, Page, 1600, 900);

        var s = Section(store, sid);
        Assert.Equal(1600d, D(s["width"]));
        Assert.Equal(900d, D(s["height"]));
        var props = (JsonObject)((JsonArray)SectionConfig(store, sid)["objects"]!["pageSize"]!)[0]!["properties"]!;
        Assert.Equal("1600D", (string?)props["width"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Custom'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
    }

    [Theory]
    [InlineData("16:9", 1280, 720, "16:9")]
    [InlineData("4:3", 1024, 768, "4:3")]
    [InlineData("letter", 816, 1056, "Letter")]
    [InlineData("tooltip", 320, 240, "Tooltip")]
    [InlineData("mobile", 320, 568, "Custom")]
    public void SetCanvasPreset_AppliesKnownPresets(string preset, double w, double h, string type)
    {
        var (svc, sid, store) = NewReport();
        svc.SetCanvasPreset(sid, Page, preset, null, null);
        var s = Section(store, sid);
        Assert.Equal(w, D(s["width"]));
        Assert.Equal(h, D(s["height"]));
        var props = (JsonObject)((JsonArray)SectionConfig(store, sid)["objects"]!["pageSize"]!)[0]!["properties"]!;
        Assert.Equal($"'{type}'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetCanvasPreset_Custom_RequiresDimensions()
    {
        var (svc, sid, _) = NewReport();
        Assert.Throws<ArgumentException>(() => svc.SetCanvasPreset(sid, Page, "custom", null, null));
    }

    [Fact]
    public void SetCanvasPreset_Unknown_Throws()
    {
        var (svc, sid, _) = NewReport();
        Assert.Throws<ArgumentException>(() => svc.SetCanvasPreset(sid, Page, "a2", null, null));
    }

    // ====================================================================== 3. advanced filter types

    private static JsonObject FirstPageFilter(SessionStore store, string sid) =>
        (JsonObject)(JsonNode.Parse((string)Section(store, sid)["filters"]!) as JsonArray)![0]!;

    [Fact]
    public void AddTopNFilter_WritesTopNode_WithRankingMeasureAndCount()
    {
        var (svc, sid, store) = NewReport();
        svc.AddTopNFilter(sid, "page", Page, null, "Dim", "Brand", 10, "Fact", "Sales", "top");

        var f = FirstPageFilter(store, sid);
        Assert.Equal("TopN", (string?)f["type"]);
        var top = (JsonObject)f["filter"]!["Where"]![0]!["Condition"]!["Top"]!;
        Assert.Equal(10, top["Count"]!.GetValue<int>());
        Assert.Equal(2, top["OrderBy"]![0]!["Direction"]!.GetValue<int>());   // top = desc(2)
        Assert.Equal("Sales", (string?)top["OrderBy"]![0]!["Expression"]!["Measure"]!["Property"]);
        Assert.Equal("Brand", (string?)top["Expressions"]![0]!["Column"]!["Property"]);
    }

    [Fact]
    public void AddTopNFilter_Bottom_UsesAscendingDirection()
    {
        var (svc, sid, store) = NewReport();
        svc.AddTopNFilter(sid, "page", Page, null, "Dim", "Brand", 5, "Fact", "Sales", "bottom");
        var top = (JsonObject)FirstPageFilter(store, sid)["filter"]!["Where"]![0]!["Condition"]!["Top"]!;
        Assert.Equal(1, top["OrderBy"]![0]!["Direction"]!.GetValue<int>());   // bottom = asc(1)
    }

    [Fact]
    public void AddRelativeDateFilter_WritesRelativeDateNode_WithUnitAndOpCodes()
    {
        var (svc, sid, store) = NewReport();
        svc.AddRelativeDateFilter(sid, "page", Page, null, "Dim_Date", "Date", "Last", 3, "Months", includeCurrent: true, calendar: false);

        var f = FirstPageFilter(store, sid);
        Assert.Equal("RelativeDate", (string?)f["type"]);
        var rel = (JsonObject)f["filter"]!["Where"]![0]!["Condition"]!["RelativeDate"]!;
        Assert.Equal(0, rel["TimeRange"]!.GetValue<int>());   // Last = 0
        Assert.Equal(2, rel["TimeUnit"]!.GetValue<int>());    // Months = 2
        Assert.Equal(3, rel["Amount"]!.GetValue<int>());
        Assert.True(rel["IncludeCurrent"]!.GetValue<bool>());
        Assert.False(rel["Calendar"]!.GetValue<bool>());
    }

    [Fact]
    public void AddRelativeTimeFilter_WritesRelativeTimeNode_WithHourUnit()
    {
        var (svc, sid, store) = NewReport();
        svc.AddRelativeTimeFilter(sid, "page", Page, null, "Dim_Date", "Timestamp", "Last", 6, "Hours", includeCurrent: false);

        var f = FirstPageFilter(store, sid);
        Assert.Equal("RelativeTime", (string?)f["type"]);
        var rel = (JsonObject)f["filter"]!["Where"]![0]!["Condition"]!["RelativeTime"]!;
        Assert.Equal(4, rel["TimeUnit"]!.GetValue<int>());    // Hours = 4
        Assert.Null(rel["Calendar"]);                          // time filter has no Calendar key
    }

    [Fact]
    public void AddIncludeExcludeFilter_Exclude_WrapsInWithNot()
    {
        var (svc, sid, store) = NewReport();
        svc.AddIncludeExcludeFilter(sid, "page", Page, null, "Dim", "Brand", new[] { "X", "Y" }, "string", exclude: true);

        var f = FirstPageFilter(store, sid);
        Assert.Equal("Exclude", (string?)f["type"]);
        var where = (JsonObject)f["filter"]!["Where"]![0]!["Condition"]!;
        Assert.NotNull(where["Not"]);                                  // exclude = Not In
        Assert.NotNull(where["Not"]!["Expression"]!["In"]);
        var values = (JsonArray)where["Not"]!["Expression"]!["In"]!["Values"]!;
        Assert.Equal(2, values.Count);
        Assert.Equal("'X'", (string?)values[0]![0]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddIncludeExcludeFilter_Include_PlainIn()
    {
        var (svc, sid, store) = NewReport();
        svc.AddIncludeExcludeFilter(sid, "page", Page, null, "Dim", "Brand", new[] { "X" }, "string", exclude: false);
        var where = (JsonObject)FirstPageFilter(store, sid)["filter"]!["Where"]![0]!["Condition"]!;
        Assert.NotNull(where["In"]);
        Assert.Null(where["Not"]);
        Assert.Equal("Include", (string?)FirstPageFilter(store, sid)["type"]);
    }

    [Fact]
    public void AddAdvancedFilter_TwoConditions_JoinedByOr()
    {
        var (svc, sid, store) = NewReport();
        svc.AddAdvancedFilter(sid, "page", Page, null, "Fact", "Sales", "measure", "or",
            new[] { ("gt", (string?)"100"), ("lt", (string?)"-100") }, "decimal");

        var where = (JsonObject)FirstPageFilter(store, sid)["filter"]!["Where"]![0]!["Condition"]!;
        Assert.NotNull(where["Or"]);                                   // combine = or
        Assert.Equal(1, where["Or"]!["Left"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>());  // gt = 1
        Assert.Equal("100M", (string?)where["Or"]!["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);   // decimal -> M
        Assert.Equal("Advanced", (string?)FirstPageFilter(store, sid)["type"]);
    }

    [Fact]
    public void AddAdvancedFilter_Contains_WritesContainsNode()
    {
        var (svc, sid, store) = NewReport();
        svc.AddAdvancedFilter(sid, "page", Page, null, "Dim", "Name", "column", "and",
            new[] { ("contains", (string?)"abc") }, "string");
        var where = (JsonObject)FirstPageFilter(store, sid)["filter"]!["Where"]![0]!["Condition"]!;
        Assert.NotNull(where["Contains"]);
        Assert.Equal("'abc'", (string?)where["Contains"]!["Right"]!["Literal"]!["Value"]);
    }

    // ====================================================================== 4. field-value CF + data-bar fix + icons

    [Fact]
    public void SetFieldValueCf_Background_EmitsMeasureBoundExpr_NotFillRule()
    {
        var (svc, sid, store) = NewReport(visuals: new V("m1", "pivotTable"));
        svc.SetFieldValueCf(sid, Page, "m1", "Dim.Brand", "background", "Fact[Colour]", null);

        var entry = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!;
        Assert.Equal("Dim.Brand", (string?)entry["selector"]!["metadata"]);
        var color = (JsonObject)entry["properties"]!["backColor"]!["solid"]!["color"]!;
        // FAULT-SENSITIVE: the colour is a Measure-bound expr (NOT a FillRule / Literal).
        Assert.Equal("Colour", (string?)color["expr"]!["Measure"]!["Property"]);
        Assert.Equal("Fact", (string?)color["expr"]!["Measure"]!["Expression"]!["SourceRef"]!["Entity"]);
        Assert.Null(color["expr"]!["FillRule"]);
    }

    [Fact]
    public void SetFieldValueCf_Font_TargetsFontColor()
    {
        var (svc, sid, store) = NewReport(visuals: new V("m1", "pivotTable"));
        svc.SetFieldValueCf(sid, Page, "m1", "Dim.Brand", "font", "Fact[Colour]", null);
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!["properties"]!;
        Assert.NotNull(props["fontColor"]);
        Assert.Null(props["backColor"]);
    }

    [Fact]
    public void SetWebUrlCf_WritesWebURLMeasureBoundExpr()
    {
        var (svc, sid, store) = NewReport(visuals: new V("m1", "tableEx"));
        svc.SetWebUrlCf(sid, Page, "m1", "Dim.Brand", "Fact[Link]", null);
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!["properties"]!;
        Assert.Equal("Link", (string?)props["webURL"]!["expr"]!["Measure"]!["Property"]);
    }

    [Fact]
    public void SetDataBars_FixedMinMaxBug_MinNotEqualMaxByDefault()
    {
        var (svc, sid, store) = NewReport(visuals: new V("m1", "tableEx"));
        svc.SetDataBars(sid, Page, "m1", "Fact", "Sales", "#1B8A4B", null);

        var dataBars = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!["properties"]!["dataBars"]!;
        // FAULT-SENSITIVE: the old bug wrote the SAME measure expr to both min and max (min==max -> no bars).
        // Now both default to an Auto rule (distinct, non-measure), so bars render.
        Assert.NotNull(dataBars["minValue"]!["Auto"]);
        Assert.NotNull(dataBars["maxValue"]!["Auto"]);
        Assert.Null(dataBars["minValue"]!["Measure"]);
        Assert.Null(dataBars["maxValue"]!["Measure"]);
    }

    [Fact]
    public void SetDataBars_ExposesReverseDirection_HideText_AxisColor_ExplicitMinMax()
    {
        var (svc, sid, store) = NewReport(visuals: new V("m1", "tableEx"));
        svc.SetDataBars(sid, Page, "m1", "Fact", "Sales", "#1B8A4B", "#E81123",
            reverseDirection: true, hideText: true, axisColor: "#888888", minValue: 0, maxValue: 1000);

        var dataBars = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!["properties"]!["dataBars"]!;
        Assert.Equal("true", (string?)dataBars["reverseDirection"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("true", (string?)dataBars["hideText"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'#888888'", (string?)dataBars["axisColor"]!["solid"]!["color"]!["expr"]!["Literal"]!["Value"]);
        // explicit min != explicit max
        Assert.Equal("0D", (string?)dataBars["minValue"]!["Literal"]!["Value"]);
        Assert.Equal("1000D", (string?)dataBars["maxValue"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetIconRules_WithGlyphsAndLayout_UsesGlyphIdsAndLayout()
    {
        var (svc, sid, store) = NewReport(visuals: new V("m1", "pivotTable"));
        var rules = new List<(double, double, string)> { (-1e9, 0, "#FF0000"), (0, 1e9, "#00FF00") };
        svc.SetIconRules(sid, Page, "m1", "Fact", "Growth", rules,
            glyphs: new[] { "ArrowDown", "ArrowUp" }, iconSet: "ThreeArrowsColored", layout: "right");

        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, "m1")["objects"]!["values"]!)[0]!["properties"]!;
        var ruleArr = (JsonArray)props["icon"]!["solid"]!["color"]!["expr"]!["FillRule"]!["FillRule"]!["ruleDefinition"]!["rules"]!;
        // FAULT-SENSITIVE: when glyphs are given the IconId is the GLYPH name, not the colour.
        Assert.Equal("'ArrowDown'", (string?)ruleArr[0]!["IconId"]!["Literal"]!["Value"]);
        Assert.Equal("'ArrowUp'", (string?)ruleArr[1]!["IconId"]!["Literal"]!["Value"]);
        Assert.Equal("'ThreeArrowsColored'", (string?)props["iconSet"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'IconAndData'", (string?)props["iconLayout"]!["expr"]!["Literal"]!["Value"]);   // right
    }

    // ====================================================================== 5. cross-report drillthrough

    [Fact]
    public void SetCrossReportDrillthrough_Enable_WritesReferenceScopeAndReportSetting()
    {
        var (svc, sid, store) = NewReport();
        svc.SetCrossReportDrillthrough(sid, Page, enable: true);

        var binding = (JsonObject)SectionConfig(store, sid)["pageBinding"]!;
        Assert.Equal("Drillthrough", (string?)binding["type"]);
        Assert.Equal("CrossReport", (string?)binding["referenceScope"]);
        // report-level setting flipped on
        var setting = (JsonObject)ReportConfig(store, sid)["settings"]!["useCrossReportDrillthrough"]!;
        Assert.Equal("true", (string?)setting["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void SetCrossReportDrillthrough_Disable_DropsReferenceScope()
    {
        var (svc, sid, store) = NewReport();
        svc.SetCrossReportDrillthrough(sid, Page, enable: false);
        var binding = (JsonObject)SectionConfig(store, sid)["pageBinding"]!;
        Assert.Equal("Drillthrough", (string?)binding["type"]);
        Assert.Null(binding["referenceScope"]);
    }

    // ====================================================================== 6. tooltip field binding

    [Fact]
    public void SetTooltipFieldBinding_WritesPageBindingParameters()
    {
        var (svc, sid, store) = NewReport();
        svc.SetTooltipFieldBinding(sid, Page, new[] { ("Fact", "Sales"), ("Dim", "Brand") });

        var binding = (JsonObject)SectionConfig(store, sid)["pageBinding"]!;
        Assert.Equal("Tooltip", (string?)binding["type"]);
        var pars = (JsonArray)binding["parameters"]!;
        Assert.Equal(2, pars.Count);
        Assert.Equal("Sales", (string?)pars[0]!["Expression"]!["Column"]!["Property"]);
        Assert.Equal("Brand", (string?)pars[1]!["Expression"]!["Column"]!["Property"]);
    }

    [Fact]
    public void AddTooltipFields_AddsTooltipsProjectionAndSelect()
    {
        var (svc, sid, store) = NewReport(visuals: new V("v1", "clusteredColumnChart"));
        svc.AddTooltipFields(sid, Page, "v1", new[] { new FieldBinding("Tooltips", "Fact", "Margin", "measure") });

        var sv = SingleVisual(store, sid, "v1");
        var tooltips = (JsonArray)sv["projections"]!["Tooltips"]!;
        Assert.Equal("Fact.Margin", (string?)tooltips[0]!["queryRef"]);
        // the Select projection was added so the field participates in the query
        var select = (JsonArray)sv["prototypeQuery"]!["Select"]!;
        Assert.Contains(select.OfType<JsonObject>(), s => (string?)s["Name"] == "Fact.Margin" && s["Measure"] != null);
    }

    // ====================================================================== 7. button action types

    [Fact]
    public void AddButton_WebUrl_WritesWebUrlLink()
    {
        var (svc, sid, store) = NewReport();
        string id = Pck(svc.AddActionButtonEx(sid, Page, "Visit", "webUrl", null, "https://example.com", 0, 0, 100, 40), "visualName");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("'WebUrl'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'https://example.com'", (string?)props["webUrl"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddButton_Drillthrough_ResolvesDestinationPage()
    {
        var (svc, sid, store) = NewReport();
        string secName = (string)Section(store, sid)["name"]!;
        string id = Pck(svc.AddActionButtonEx(sid, Page, "Details", "drillthrough", null, Page, 0, 0, 100, 40), "visualName");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("'Drillthrough'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal($"'{secName}'", (string?)props["navigationSection"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddButton_Back_NeedsNoTarget_AndGetsBackShape()
    {
        var (svc, sid, store) = NewReport();
        string id = Pck(svc.AddActionButtonEx(sid, Page, "", "back", null, null, 0, 0, 48, 48), "visualName");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("'Back'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        // Back button uses the 'back' chevron shape
        var shape = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["objects"]!["icon"]!)[0]!["properties"]!;
        Assert.Equal("'back'", (string?)shape["shapeType"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddButton_Qna_NoTargetKeys()
    {
        var (svc, sid, store) = NewReport();
        string id = Pck(svc.AddActionButtonEx(sid, Page, "Ask", "qna", null, null, 0, 0, 100, 40), "visualName");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("'Qna'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Null(props["webUrl"]);
        Assert.Null(props["bookmark"]);
    }

    [Fact]
    public void AddButton_WebUrl_MissingUrl_Throws()
    {
        var (svc, sid, _) = NewReport();
        Assert.Throws<ArgumentException>(() => svc.AddActionButtonEx(sid, Page, "x", "webUrl", null, null, 0, 0, 100, 40));
    }

    [Fact]
    public void AddActionButton_LegacyBookmarkOverload_StillWritesBookmarkLink()
    {
        var (svc, sid, store) = NewReport();
        string id = Pck(svc.AddActionButton(sid, Page, "Go", "MyBookmark", 0, 0, 100, 40), "visualName");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("'Bookmark'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'MyBookmark'", (string?)props["bookmark"]!["expr"]!["Literal"]!["Value"]);
    }

    [Fact]
    public void AddButton_ClearAllSlicers_WritesClearAllSlicersLink_NoTargetKeys()
    {
        var (svc, sid, store) = NewReport();
        string id = Pck(svc.AddActionButtonEx(sid, Page, "Clear filters", "clearAllSlicers", null, null, 0, 0, 120, 40), "visualName");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("'ClearAllSlicers'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        // a clear-all-slicers button acts on the page's slicers - no destination keys
        Assert.Null(props["bookmark"]);
        Assert.Null(props["webUrl"]);
        Assert.Null(props["navigationSection"]);
    }

    [Fact]
    public void AddButton_ApplyAllSlicers_WritesApplyAllSlicersLink()
    {
        var (svc, sid, store) = NewReport();
        string id = Pck(svc.AddActionButtonEx(sid, Page, "Apply", "applyAllSlicers", null, null, 0, 0, 120, 40), "visualName");
        var props = (JsonObject)((JsonArray)SingleVisual(store, sid, id)["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("'ApplyAllSlicers'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        Assert.Null(props["navigationSection"]);
    }

    // ====================================================================== 8. mobile visibility

    [Fact]
    public void SetMobileVisibility_Hide_SeedsMobileEntryAndSetsIsHidden()
    {
        var (svc, sid, store) = NewReport(visuals: new V("v1", "card"));
        svc.SetMobileVisibility(sid, Page, "v1", visible: false);

        var layouts = (JsonArray)ConfigOf(store, sid, "v1")!["layouts"]!;
        var mobile = layouts.OfType<JsonObject>().First(l => (int?)D(l["id"]) == 1);
        Assert.True(mobile["isHidden"]!.GetValue<bool>());
        // the desktop layout (id 0) is untouched
        Assert.NotNull(layouts.OfType<JsonObject>().First(l => (int?)D(l["id"]) == 0));
    }

    [Fact]
    public void SetMobilePosition_WithMobileFormat_StampsOverridesOnMobileEntry()
    {
        var (svc, sid, store) = NewReport(visuals: new V("v1", "clusteredColumnChart"));
        string fmt = new JsonObject { ["objects"] = new JsonObject { ["legend"] = new JsonObject { ["show"] = false } } }.ToJsonString();
        svc.SetMobilePosition(sid, Page, "v1", 8, 8, 300, 200, fmt);

        var layouts = (JsonArray)ConfigOf(store, sid, "v1")!["layouts"]!;
        var mobile = layouts.OfType<JsonObject>().First(l => (int?)D(l["id"]) == 1);
        var legend = (JsonObject)((JsonArray)mobile["objects"]!["objects"]!["legend"]!)[0]!["properties"]!;
        Assert.Equal("false", (string?)legend["show"]!["expr"]!["Literal"]!["Value"]);
    }

    // ====================================================================== 9. tab order + show-no-data

    [Fact]
    public void SetPageTabOrder_OrdersListed_AndHidesWithMinusOne()
    {
        var (svc, sid, store) = NewReport(new V("a", "card"), new V("b", "card"), new V("c", "card"));
        svc.SetPageTabOrder(sid, Page, new[] { "b", "a" }, new[] { "c" });

        int Tab(string id) => (int)D(ConfigOf(store, sid, id)!["layouts"]![0]!["position"]!["tabOrder"]);
        Assert.Equal(0, Tab("b"));   // first listed
        Assert.Equal(1, Tab("a"));   // second listed
        Assert.Equal(-1, Tab("c"));  // hidden from tab order
    }

    [Fact]
    public void SetPageTabOrder_NoLists_Throws()
    {
        var (svc, sid, _) = NewReport(visuals: new V("a", "card"));
        Assert.Throws<ArgumentException>(() => svc.SetPageTabOrder(sid, Page, Array.Empty<string>(), Array.Empty<string>()));
    }

    [Fact]
    public void SetShowItemsNoData_SetsShowAllOnProjectedField()
    {
        var projections = new JsonObject { ["Category"] = new JsonArray { new JsonObject { ["queryRef"] = "Dim_Date.Month" } } };
        var (svc, sid, store) = NewReport();
        // attach a projection to v1
        var container = NewVisualWithProjection(store, sid, "v1", "clusteredColumnChart", projections);

        svc.SetShowItemsNoData(sid, Page, "v1", "Dim_Date", "Month", true);

        var proj = (JsonObject)SingleVisual(store, sid, "v1")["projections"]!;
        var field = (JsonObject)((JsonArray)proj["Category"]!)[0]!;
        Assert.True(field["showAll"]!.GetValue<bool>());
    }

    [Fact]
    public void SetShowItemsNoData_UnprojectedField_Throws()
    {
        var (svc, sid, store) = NewReport();
        NewVisualWithProjection(store, sid, "v1", "clusteredColumnChart",
            new JsonObject { ["Category"] = new JsonArray { new JsonObject { ["queryRef"] = "Dim.Brand" } } });
        Assert.Throws<InvalidOperationException>(() => svc.SetShowItemsNoData(sid, Page, "v1", "Dim_Date", "Month", true));
    }

    // helper: append a visual with a projections block to the page
    private static JsonObject NewVisualWithProjection(SessionStore store, string sid, string id, string type, JsonObject projections)
    {
        var c = Container(id, type, projections: projections);
        Containers(store, sid).Add(c);
        return c;
    }

    // ====================================================================== 10. org custom visual

    [Fact]
    public void RegisterOrgCustomVisual_AddsGuidToOrganizationCustomVisuals_NotPublic()
    {
        var (svc, sid, store) = NewReport();
        svc.RegisterOrgCustomVisual(sid, "MyOrgVisual1234ABCD", null, null);

        var cfg = ReportConfig(store, sid);
        var org = (JsonArray)cfg["organizationCustomVisuals"]!;
        Assert.Contains(org.OfType<JsonValue>(), v => (string?)v == "MyOrgVisual1234ABCD");
        // FAULT-SENSITIVE: it must NOT land in publicCustomVisuals.
        Assert.True(cfg["publicCustomVisuals"] is null ||
                    !((JsonArray)cfg["publicCustomVisuals"]!).OfType<JsonValue>().Any(v => (string?)v == "MyOrgVisual1234ABCD"));
    }

    [Fact]
    public void RegisterCustomVisual_StillUsesPublicCustomVisuals()
    {
        var (svc, sid, store) = NewReport();
        svc.RegisterCustomVisual(sid, "PublicVisualXYZ", null, null);
        var pub = (JsonArray)ReportConfig(store, sid)["publicCustomVisuals"]!;
        Assert.Contains(pub.OfType<JsonValue>(), v => (string?)v == "PublicVisualXYZ");
    }
}
