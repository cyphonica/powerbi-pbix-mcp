using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Integrations;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The POST-BUILD BIND SAFETY NET (ReportService.PruneUnresolvedVisuals) and the AutoModeller DERIVED-measure
/// synthesis - the two halves of the "no broken visual ever ships, and the measure it needs exists" fix.
///
/// Contract proven here:
///   - a visual bound to a measure/column that the model does NOT have is DROPPED;
///   - a visual bound only to REAL fields SURVIVES (no-op on a clean report);
///   - a measure resolves GLOBALLY (a real measure attributed to the "wrong" table is NOT dropped);
///   - an empty model set is a strict no-op (never prunes blindly);
///   - decorative visuals with no field bindings (textbox) are never touched;
///   - AutoModeller mints "Average Price" = DIVIDE(value, qty) when a fact has both, and nothing extra otherwise.
/// Every transform is pure over the in-memory layout / discovered schema - no live .pbix or AS engine needed.
/// </summary>
public sealed class BindSafetyNetTests
{
    // ---- report harness (a single page, build visuals via the real engine so the prototypeQuery is authentic) ----
    private static (ReportService svc, string sid, SessionStore store, string page) NewReport()
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);
        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 32),
            ["displayName"] = "Page1", ["ordinal"] = 0,
            ["visualContainers"] = new JsonArray(),
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
        return (svc, session.Id, store, "Page1");
    }

    private static int VisualCount(SessionStore store, string sid)
    {
        var sec = (JsonObject)((JsonArray)store.GetReport(sid).Layout.Root["sections"]!)[0]!;
        return ((JsonArray)sec["visualContainers"]!).Count;
    }

    private static IReadOnlySet<string> Set(params string[] xs) => new HashSet<string>(xs, System.StringComparer.OrdinalIgnoreCase);

    private static FieldBinding Measure(string table, string name) => new("Values", table, name, "measure");
    private static FieldBinding Column(string table, string name) => new("Category", table, name, "column");

    [Fact]
    public void DropsVisualBoundToMissingMeasure_KeepsRealOne()
    {
        var (svc, sid, store, page) = NewReport();
        // a card on a REAL measure, and a card on a measure the model does NOT have (the AVG PRICE failure case)
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("Sales", "Total Sales") }, "Sales");
        svc.AddVisual(sid, page, "card", 16, 160, 0, 200, 120, new[] { Measure("Sales", "Average Price") }, "Avg Price");
        Assert.Equal(2, VisualCount(store, sid));

        var tables = Set("Sales");
        var cols = Set();
        var measures = Set("Total Sales");          // NOTE: no "Average Price" in the model
        var res = svc.PruneUnresolvedVisuals(sid, tables, cols, measures);

        Assert.Equal(1, (int)res.GetType().GetProperty("dropped")!.GetValue(res)!);
        Assert.Equal(1, VisualCount(store, sid));   // only the broken card was removed
    }

    [Fact]
    public void KeepsAllWhenEveryRefResolves()
    {
        var (svc, sid, store, page) = NewReport();
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("Sales", "Total Sales") }, "Sales");
        svc.AddVisual(sid, page, "clusteredColumnChart", 16, 160, 0, 400, 300,
            new[] { Column("Products", "Brand"), Measure("Sales", "Total Sales") }, "By Brand");

        var res = svc.PruneUnresolvedVisuals(sid,
            Set("Sales", "Products"),
            Set(ReportService.FieldKey("Products", "Brand")),
            Set("Total Sales"));

        Assert.Equal(0, (int)res.GetType().GetProperty("dropped")!.GetValue(res)!);
        Assert.Equal(2, VisualCount(store, sid));
    }

    [Fact]
    public void MeasureResolvesGlobally_EvenWhenAttributedToWrongTable()
    {
        var (svc, sid, store, page) = NewReport();
        // the agent cited the right measure NAME but the wrong owning table - measures are model-global, keep it.
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("Products", "Total Sales") }, "Sales");

        var res = svc.PruneUnresolvedVisuals(sid, Set("Sales", "Products"), Set(), Set("Total Sales"));
        Assert.Equal(0, (int)res.GetType().GetProperty("dropped")!.GetValue(res)!);
        Assert.Equal(1, VisualCount(store, sid));
    }

    [Fact]
    public void EmptyModelSet_IsStrictNoOp()
    {
        var (svc, sid, store, page) = NewReport();
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("Whatever", "Anything") }, "X");

        var res = svc.PruneUnresolvedVisuals(sid, Set(), Set(), Set());
        Assert.Equal(0, (int)res.GetType().GetProperty("dropped")!.GetValue(res)!);
        Assert.Equal(1, VisualCount(store, sid));   // nothing pruned blindly
    }

    [Fact]
    public void UnknownTableRef_IsLeftAlone()
    {
        var (svc, sid, store, page) = NewReport();
        // references a table NOT in the model snapshot -> cannot be proven broken -> kept.
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("OtherTable", "Mystery") }, "X");

        var res = svc.PruneUnresolvedVisuals(sid, Set("Sales"), Set(), Set("Total Sales"));
        Assert.Equal(0, (int)res.GetType().GetProperty("dropped")!.GetValue(res)!);
        Assert.Equal(1, VisualCount(store, sid));
    }

    [Fact]
    public void TextboxWithNoBindings_IsNeverTouched()
    {
        var (svc, sid, store, page) = NewReport();
        svc.AddTextbox(sid, page, "Executive Summary", 16, 8, 600, 40, 24, true, "#16365C", "left");

        var res = svc.PruneUnresolvedVisuals(sid, Set("Sales"), Set(), Set("Total Sales"));
        Assert.Equal(0, (int)res.GetType().GetProperty("dropped")!.GetValue(res)!);
        Assert.Equal(1, VisualCount(store, sid));
    }

    // ---- empty-page cleanup ----

    private static int PageCount(SessionStore store, string sid) =>
        ((JsonArray)store.GetReport(sid).Layout.Root["sections"]!).Count;

    private static List<string> PageNames(SessionStore store, string sid)
    {
        var names = new List<string>();
        foreach (var s in (JsonArray)store.GetReport(sid).Layout.Root["sections"]!)
            if ((string?)(s as JsonObject)?["displayName"] is { } d) names.Add(d);
        return names;
    }

    private static string AddedPage(object addPageResult) =>
        (string)addPageResult.GetType().GetProperty("pageName")!.GetValue(addPageResult)!;

    [Fact]
    public void DropsPageWithNoDataVisual_KeepsContentPage()
    {
        var (svc, sid, store, page) = NewReport();   // page = "Page1"
        // Page1 gets a real card; a second page is created empty; a third has only a textbox (decorative).
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("Sales", "Total Sales") }, "Sales");
        svc.AddPage(sid, "Sales Trend", 1280, 720);                       // empty placeholder
        string decoName = AddedPage(svc.AddPage(sid, "Notes", 1280, 720));
        svc.AddTextbox(sid, decoName, "Just a caption", 16, 8, 400, 40, 18, false, "#000000", "left");
        Assert.Equal(3, PageCount(store, sid));

        var res = svc.PruneEmptyPages(sid);

        Assert.Equal(2, (int)res.GetType().GetProperty("pagesDropped")!.GetValue(res)!);
        Assert.Equal(1, PageCount(store, sid));
        Assert.Equal(new[] { "Page1" }, PageNames(store, sid).ToArray());
    }

    [Fact]
    public void KeepsEveryPage_WhenAllHaveDataVisuals()
    {
        var (svc, sid, store, page) = NewReport();
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("Sales", "Total Sales") }, "Sales");
        string p2 = AddedPage(svc.AddPage(sid, "By Brand", 1280, 720));
        svc.AddVisual(sid, p2, "clusteredColumnChart", 16, 16, 0, 400, 300,
            new[] { Column("Products", "Brand"), Measure("Sales", "Total Sales") }, "By Brand");

        var res = svc.PruneEmptyPages(sid);

        Assert.Equal(0, (int)res.GetType().GetProperty("pagesDropped")!.GetValue(res)!);
        Assert.Equal(2, PageCount(store, sid));   // no-op on a clean multi-page report
    }

    [Fact]
    public void NeverRemovesLastPage_EvenIfEmpty()
    {
        var (svc, sid, store, page) = NewReport();   // single page, no visuals at all
        var res = svc.PruneEmptyPages(sid);

        Assert.Equal(0, (int)res.GetType().GetProperty("pagesDropped")!.GetValue(res)!);
        Assert.Equal(1, PageCount(store, sid));   // the only page is always kept
    }

    [Fact]
    public void AllEmpty_KeepsExactlyOnePage()
    {
        var (svc, sid, store, page) = NewReport();
        svc.AddPage(sid, "Empty A", 1280, 720);
        svc.AddPage(sid, "Empty B", 1280, 720);
        Assert.Equal(3, PageCount(store, sid));

        var res = svc.PruneEmptyPages(sid);

        Assert.Equal(2, (int)res.GetType().GetProperty("pagesDropped")!.GetValue(res)!);
        Assert.Equal(1, PageCount(store, sid));   // drops all-but-one even when none have content
    }

    [Fact]
    public void BindPruneThenEmptyPage_CleansUpPageEmptiedByBindNet()
    {
        var (svc, sid, store, page) = NewReport();   // page = "Page1"
        // Page1 keeps a real card. A second page's ONLY visual is bound to a missing measure -> bind net empties
        // it -> empty-page cleanup then removes the now-blank page. Proves the intended pass ordering.
        svc.AddVisual(sid, page, "card", 16, 16, 0, 200, 120, new[] { Measure("Sales", "Total Sales") }, "Sales");
        string p2 = AddedPage(svc.AddPage(sid, "Bad Page", 1280, 720));
        svc.AddVisual(sid, p2, "card", 16, 16, 0, 200, 120, new[] { Measure("Sales", "Nonexistent") }, "Oops");

        svc.PruneUnresolvedVisuals(sid, Set("Sales"), Set(), Set("Total Sales"));
        var res = svc.PruneEmptyPages(sid);

        Assert.Equal(1, (int)res.GetType().GetProperty("pagesDropped")!.GetValue(res)!);
        Assert.Equal(new[] { "Page1" }, PageNames(store, sid).ToArray());
    }

    // ---- AutoModeller derived measures ----

    private static JsonObject AutoModel(params (string col, string type)[] cols)
    {
        var schema = new SchemaDiscovery();
        var t = new TableSchema("Sales");
        foreach (var (c, ty) in cols) t.Columns.Add(new ColumnSchema(c, ty));
        schema.Tables.Add(t);
        return (JsonObject)JsonNode.Parse(AutoModeller.Build(schema))!;
    }

    private static List<string> MeasureNames(JsonObject spec)
    {
        var names = new List<string>();
        foreach (var tn in (JsonArray)spec["tables"]!)
            if (tn is JsonObject to && to["measures"] is JsonArray ms)
                foreach (var m in ms)
                    if ((string?)(m as JsonObject)?["name"] is { Length: > 0 } n) names.Add(n);
        return names;
    }

    [Fact]
    public void AutoModeller_MintsAveragePrice_WhenFactHasMoneyAndUnits()
    {
        var spec = AutoModel(("Sales", "double"), ("Units", "int64"), ("OrderId", "int64"));
        var names = MeasureNames(spec);
        Assert.Contains("Total Sales", names);
        Assert.Contains("Total Units", names);
        Assert.Contains("Average Price", names);   // DIVIDE([Total Sales],[Total Units])
        Assert.DoesNotContain("Total OrderId", names); // key column never aggregated
    }

    [Fact]
    public void AutoModeller_NoAveragePrice_WhenNoQuantityColumn()
    {
        var spec = AutoModel(("Sales", "double"), ("Discount", "double"));
        Assert.DoesNotContain("Average Price", MeasureNames(spec));
    }

    [Fact]
    public void AutoModeller_MintsMarginPct_WhenRevenueAndCostExist()
    {
        var spec = AutoModel(("Revenue", "double"), ("Cost", "double"));
        Assert.Contains("Margin %", MeasureNames(spec));
    }
}
