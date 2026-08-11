using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Drives the REAL report-building engine (ReportService clone/bookmark/action-button/conditional-format
/// methods) over an in-memory Report/Layout - the same JsonObject root Open() produces. Proves the
/// end-to-end reproduction contract:
///   - clone_page makes a new page, same visual count, ALL-new ids, and global id uniqueness holds,
///   - clone_visual produces a unique id,
///   - add_bookmark hides EXACTLY the named visuals (and list_bookmarks reads them back),
///   - update/delete_bookmark behave,
///   - add_action_button's visualLink carries type='Bookmark' and the right bookmark name,
///   - set_conditional_formatting writes the discrete rule structure on the right measure column.
/// No live .pbix is needed - every transform is pure over the in-memory layout.
/// </summary>
public sealed class ReportBuilderTests
{
    private const string Page = "Page1";

    private static JsonObject Lit(string raw) =>
        new() { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = raw } } };

    // a visualContainer whose "config" is a STRINGIFIED blob holding { name, layouts, singleVisual } -
    // exactly the shape the engine writes and reads.
    private static JsonObject Container(string id, string visualType, double x, double y)
    {
        var config = new JsonObject
        {
            ["name"] = id,
            ["layouts"] = new JsonArray
            {
                new JsonObject { ["id"] = 0, ["position"] = new JsonObject
                    { ["x"] = x, ["y"] = y, ["z"] = 0, ["width"] = 100, ["height"] = 80, ["tabOrder"] = 0 } },
            },
            ["singleVisual"] = new JsonObject { ["visualType"] = visualType },
        };
        return new JsonObject
        {
            ["x"] = x, ["y"] = y, ["z"] = 0, ["width"] = 100, ["height"] = 80,
            ["config"] = config.ToJsonString(),
            ["filters"] = "[]",
        };
    }

    private static (ReportService svc, string sid, SessionStore store) NewReport(params (string id, string type)[] visuals)
    {
        var store = new SessionStore();
        var svc = new ReportService(store, NullLogger<ReportService>.Instance);

        var containers = new JsonArray();
        double y = 20;
        foreach (var (id, type) in visuals) { containers.Add(Container(id, type, 16, y)); y += 100; }

        var section = new JsonObject
        {
            ["name"] = "ReportSection" + new string('a', 32),
            ["displayName"] = Page, ["ordinal"] = 0,
            ["visualContainers"] = containers,
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

    // ---- inspection helpers (read the live in-memory layout the engine mutated) ----

    private static JsonObject Root(SessionStore store, string sid) => store.GetReport(sid).Layout.Root;
    private static JsonArray SectionsOf(SessionStore store, string sid) => (JsonArray)Root(store, sid)["sections"]!;

    private static JsonObject SectionByDisplay(SessionStore store, string sid, string display) =>
        SectionsOf(store, sid).OfType<JsonObject>().First(s => (string?)s["displayName"] == display);

    // every visual id used anywhere in the report (parsed out of each container's stringified config)
    private static List<string> AllIds(SessionStore store, string sid)
    {
        var ids = new List<string>();
        foreach (var s in SectionsOf(store, sid).OfType<JsonObject>())
            foreach (var vc in (JsonArray)s["visualContainers"]!)
            {
                var co = JsonNode.Parse((string)vc!["config"]!) as JsonObject;
                if ((string?)co!["name"] is string n) ids.Add(n);
            }
        return ids;
    }

    private static List<string> IdsOnPage(JsonObject section) =>
        ((JsonArray)section["visualContainers"]!)
            .Select(vc => (string?)(JsonNode.Parse((string)vc!["config"]!) as JsonObject)!["name"])
            .Where(n => n != null).Select(n => n!).ToList();

    private static string Pck(object result, string prop)
    {
        var node = JsonNode.Parse(JsonSerializer.Serialize(result)) as JsonObject;
        return (string?)node![prop] ?? "";
    }

    // ====================================================================== clone_page

    [Fact]
    public void ClonePage_NewPage_SameVisualCount_AllNewIds_GlobalUniquenessHolds()
    {
        var (svc, sid, store) = NewReport(("a1", "tableEx"), ("b2", "clusteredBarChart"), ("c3", "slicer"));
        var before = AllIds(store, sid);

        var result = svc.ClonePage(sid, Page, "Performance Copy");
        string newPageName = Pck(result, "pageName");

        // a brand-new section landed
        Assert.Equal(2, SectionsOf(store, sid).Count);
        Assert.StartsWith("ReportSection", newPageName);

        var newSection = SectionByDisplay(store, sid, "Performance Copy");
        var srcSection = SectionByDisplay(store, sid, Page);

        // same visual count
        var srcIds = IdsOnPage(srcSection);
        var newIds = IdsOnPage(newSection);
        Assert.Equal(srcIds.Count, newIds.Count);
        Assert.Equal(3, newIds.Count);

        // ALL visual ids on the clone are new (none shared with the source)
        Assert.Empty(newIds.Intersect(srcIds));
        Assert.DoesNotContain("a1", newIds);
        Assert.DoesNotContain("b2", newIds);
        Assert.DoesNotContain("c3", newIds);

        // GLOBAL id uniqueness holds across the whole report (no duplicate ids anywhere)
        var all = AllIds(store, sid);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Equal(before.Count + 3, all.Count);

        // the section name itself is fresh (32 hex after the prefix)
        Assert.NotEqual((string?)srcSection["name"], newPageName);
    }

    [Fact]
    public void ClonePage_PreservesVisualTypes_AndOrdersAfterSource()
    {
        var (svc, sid, store) = NewReport(("a1", "tableEx"), ("b2", "lineChart"));
        var result = svc.ClonePage(sid, Page, "Copy");
        var newSection = SectionByDisplay(store, sid, "Copy");

        var types = ((JsonArray)newSection["visualContainers"]!)
            .Select(vc => (string?)(JsonNode.Parse((string)vc!["config"]!) as JsonObject)!["singleVisual"]!["visualType"])
            .ToList();
        Assert.Equal(new[] { "tableEx", "lineChart" }, types);

        // new ordinal is at the end (greater than the source's 0). Read via GetValue<int> -
        // the engine stores ordinal as an Int32, so a strict (double?) cast would throw.
        Assert.True((newSection["ordinal"]!.GetValue<int>()) >= 1);
    }

    // ====================================================================== clone_visual

    [Fact]
    public void CloneVisual_ProducesUniqueId_OnSamePage()
    {
        var (svc, sid, store) = NewReport(("a1", "tableEx"), ("b2", "card"));
        var result = svc.CloneVisual(sid, Page, "a1", null);
        string newId = Pck(result, "visualName");

        var ids = IdsOnPage(SectionByDisplay(store, sid, Page));
        Assert.Equal(3, ids.Count);                 // original 2 + clone
        Assert.Contains(newId, ids);
        Assert.NotEqual("a1", newId);
        Assert.Equal(ids.Count, ids.Distinct().Count());   // uniqueness
    }

    [Fact]
    public void CloneVisual_OntoTargetPage_KeepsGlobalUniqueness()
    {
        var (svc, sid, store) = NewReport(("a1", "tableEx"));
        string target = Pck(svc.ClonePage(sid, Page, "Other"), "pageName");

        var result = svc.CloneVisual(sid, Page, "a1", "Other");
        string newId = Pck(result, "visualName");

        // the clone landed on the target page, with an id seen nowhere else
        var targetIds = IdsOnPage(SectionByDisplay(store, sid, "Other"));
        Assert.Contains(newId, targetIds);
        var all = AllIds(store, sid);
        Assert.Equal(all.Count, all.Distinct().Count());
        Assert.Single(all, i => i == newId);
    }

    // ====================================================================== bookmarks

    [Fact]
    public void AddBookmark_HidesExactlyTheNamedVisuals_AndListsThemBack()
    {
        var (svc, sid, store) = NewReport(("a1", "tableEx"), ("b2", "clusteredBarChart"), ("c3", "card"));

        var added = svc.AddBookmark(sid, Page, "By Brand", new[] { "b2", "c3" });
        string bmName = Pck(added, "name");
        Assert.StartsWith("Bookmark", bmName);

        // list_bookmarks reads back EXACTLY the hidden set
        var listed = svc.ListBookmarks(sid);
        var node = JsonNode.Parse(JsonSerializer.Serialize(listed)) as JsonObject;
        var bms = (JsonArray)node!["bookmarks"]!;
        Assert.Single(bms);
        var bm0 = (JsonObject)bms[0]!;
        Assert.Equal("By Brand", (string?)bm0["displayName"]);
        var hidden = ((JsonArray)bm0["hiddenVisuals"]!).Select(x => (string?)x).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "b2", "c3" }, hidden);

        // and the explorationState records the SHOWN visual too (a1 -> show), mirroring the PBIX shape
        var (_, bookmarks) = ReadBookmarks(store, sid);
        var es = (JsonObject)((JsonObject)bookmarks[0]!)["explorationState"]!;
        Assert.Equal("1.3", (string?)es["version"]);
        string secName = (string)es["activeSection"]!;
        var vcs = (JsonObject)es["sections"]![secName]!["visualContainers"]!;
        Assert.Equal("show", (string?)vcs["a1"]!["singleVisual"]!["display"]!["mode"]);
        Assert.Equal("hidden", (string?)vcs["b2"]!["singleVisual"]!["display"]!["mode"]);
        Assert.Equal("hidden", (string?)vcs["c3"]!["singleVisual"]!["display"]!["mode"]);
    }

    private static (JsonObject cfg, JsonArray bookmarks) ReadBookmarks(SessionStore store, string sid)
    {
        var cfg = JsonNode.Parse((string?)Root(store, sid)["config"] ?? "{}") as JsonObject;
        return (cfg!, (JsonArray)cfg!["bookmarks"]!);
    }

    [Fact]
    public void UpdateBookmark_FlipsTheHiddenSet()
    {
        var (svc, sid, _) = NewReport(("a1", "tableEx"), ("b2", "card"), ("c3", "slicer"));
        string bm = Pck(svc.AddBookmark(sid, Page, "V", new[] { "a1" }), "name");

        var updated = svc.UpdateBookmark(sid, bm, new[] { "b2", "c3" });
        var hidden = ((JsonArray)(JsonNode.Parse(JsonSerializer.Serialize(updated)) as JsonObject)!["hiddenVisuals"]!)
            .Select(x => (string?)x).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "b2", "c3" }, hidden);   // a1 now shown, b2/c3 hidden
    }

    [Fact]
    public void DeleteBookmark_RemovesIt()
    {
        var (svc, sid, _) = NewReport(("a1", "tableEx"));
        string bm = Pck(svc.AddBookmark(sid, Page, "V", new[] { "a1" }), "name");
        svc.DeleteBookmark(sid, bm);

        var node = JsonNode.Parse(JsonSerializer.Serialize(svc.ListBookmarks(sid))) as JsonObject;
        Assert.Empty((JsonArray)node!["bookmarks"]!);
    }

    // ====================================================================== action button bound to a bookmark

    [Fact]
    public void AddActionButton_VisualLinkCarriesTheRightBookmark()
    {
        var (svc, sid, store) = NewReport(("a1", "tableEx"));
        string bm = Pck(svc.AddBookmark(sid, Page, "By Brand", new[] { "a1" }), "name");

        var btn = svc.AddActionButton(sid, Page, "By Brand", bm, 24, 124, 150, 40);
        string btnId = Pck(btn, "visualName");

        // find the new actionButton container and read its visualLink
        var section = SectionByDisplay(store, sid, Page);
        var co = ((JsonArray)section["visualContainers"]!)
            .Select(vc => JsonNode.Parse((string)vc!["config"]!) as JsonObject)
            .First(c => (string?)c!["name"] == btnId)!;

        var sv = (JsonObject)co["singleVisual"]!;
        Assert.Equal("actionButton", (string?)sv["visualType"]);

        var props = (JsonObject)((JsonArray)sv["vcObjects"]!["visualLink"]!)[0]!["properties"]!;
        Assert.Equal("true", (string?)props["show"]!["expr"]!["Literal"]!["Value"]);
        Assert.Equal("'Bookmark'", (string?)props["type"]!["expr"]!["Literal"]!["Value"]);
        // the bookmark literal is single-quoted around the exact bookmark name
        Assert.Equal($"'{bm}'", (string?)props["bookmark"]!["expr"]!["Literal"]!["Value"]);

        // the label text is carried (default-selector text entry)
        var textArr = (JsonArray)sv["vcObjects"]!["text"]!;
        var textProps = textArr.OfType<JsonObject>().First(e => e["selector"] != null)["properties"] as JsonObject;
        Assert.Equal("'By Brand'", (string?)textProps!["text"]!["expr"]!["Literal"]!["Value"]);
    }

    // ====================================================================== discrete conditional formatting

    [Fact]
    public void SetConditionalFormatting_WritesDiscreteRuleStructure_OnTheRightMeasure()
    {
        var (svc, sid, store) = NewReport(("m1", "pivotTable"));
        var rules = new List<(double, double, string)>
        {
            (0d, 1_000_000d, "#C6EFCE"),       // positive band
            (-1_000_000d, 0d, "#e68f96"),      // negative band
        };
        svc.SetConditionalFormattingRules(sid, Page, "m1", "Fact", "Sales Growth %", rules, "values", "backColor");

        var section = SectionByDisplay(store, sid, Page);
        var co = ((JsonArray)section["visualContainers"]!)
            .Select(vc => JsonNode.Parse((string)vc!["config"]!) as JsonObject)
            .First(c => (string?)c!["name"] == "m1")!;
        var values = (JsonArray)co["singleVisual"]!["objects"]!["values"]!;
        var entry = (JsonObject)values[0]!;

        // the column is targeted by a selector whose metadata is the measure's queryRef
        Assert.Equal("Fact.Sales Growth %", (string?)entry["selector"]!["metadata"]);

        // the property is backColor -> solid.color.expr.FillRule
        var fillRule = (JsonObject)entry["properties"]!["backColor"]!["solid"]!["color"]!["expr"]!["FillRule"]!;

        // the FillRule Input is the driving measure
        Assert.Equal("Sales Growth %", (string?)fillRule["Input"]!["Measure"]!["Property"]);
        Assert.Equal("Fact", (string?)fillRule["Input"]!["Measure"]!["Expression"]!["SourceRef"]!["Entity"]);

        // it is a DISCRETE ruleDefinition (not a gradient), with one rule per band
        var ruleArr = (JsonArray)fillRule["FillRule"]!["ruleDefinition"]!["rules"]!;
        Assert.Equal(2, ruleArr.Count);

        // first band: colour + a (>= 0 AND < 1,000,000) condition
        var r0 = (JsonObject)ruleArr[0]!;
        Assert.Equal("'#C6EFCE'", (string?)r0["Color"]!["Literal"]!["Value"]);
        var and = (JsonObject)r0["Condition"]!["And"]!;
        Assert.Equal(2, and["Left"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>());   // GTE min
        Assert.Equal("0D", (string?)and["Left"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);
        Assert.Equal(3, and["Right"]!["Comparison"]!["ComparisonKind"]!.GetValue<int>());  // LT max
        Assert.Equal("1000000D", (string?)and["Right"]!["Comparison"]!["Right"]!["Literal"]!["Value"]);

        // second band colour
        var r1 = (JsonObject)ruleArr[1]!;
        Assert.Equal("'#e68f96'", (string?)r1["Color"]!["Literal"]!["Value"]);
    }
}
