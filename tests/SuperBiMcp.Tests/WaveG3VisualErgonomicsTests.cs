using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave G3 report-ergonomics tests: the curated data-role catalogue (roles, caps, deprecated ->
/// modern mapping, honest coverage), change_visual_type (bindings preserved, roles remapped, caps
/// enforced, formatting pruned against the property registry, position untouched), the bulk batch
/// ops (per-item isolation, never all-or-nothing) and the visual-side field-parameter binder (the
/// active projection + Select swap). All in-memory - no live engine, no Desktop.
/// </summary>
public sealed class WaveG3VisualErgonomicsTests
{
    // ---------------------------------------------------------------- fixtures

    private static JsonObject Section(string displayName, int ordinal)
    {
        return new JsonObject
        {
            ["name"] = "ReportSection" + ordinal + new string('c', 18),
            ["displayName"] = displayName, ["ordinal"] = ordinal,
            ["visualContainers"] = new JsonArray(),
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

    private static JsonObject Obj(object result) => (JsonObject)JsonSerializer.SerializeToNode(result)!;

    private static readonly PropertyCatalog Catalog = new();

    private static string AddVisual(ReportService svc, string sid, string page, string type, params FieldBinding[] binds)
        => (string)Obj(svc.AddVisual(sid, page, type, 10, 20, 0, 400, 300, binds, null))["visualName"]!;

    private static JsonObject VisualConfig(SessionStore store, string sid, string page, string visualName)
    {
        var root = store.GetReport(sid).Layout.Root;
        foreach (var section in ((JsonArray)root["sections"]!).OfType<JsonObject>())
        {
            if ((string?)section["displayName"] != page) continue;
            foreach (var vc in ((JsonArray)section["visualContainers"]!).OfType<JsonObject>())
            {
                var co = (JsonObject)JsonNode.Parse((string)vc["config"]!)!;
                if ((string?)co["name"] == visualName) return co;
            }
        }
        throw new InvalidOperationException("visual not found");
    }

    // ================================================================ data-role catalogue

    [Fact]
    public void ListRoles_CuratedType_ReportsRoles_Kinds_And_Caps()
    {
        var r = Obj(VisualDataRoles.ListRoles("pieChart", formattingRegistryKnows: true));

        Assert.Equal("curated", (string)r["coverage"]!);
        Assert.False((bool)r["deprecated"]!);
        var roles = ((JsonArray)r["roles"]!).OfType<JsonObject>().ToDictionary(x => (string)x["role"]!, x => x);
        Assert.Equal("Grouping", (string)roles["Category"]["accepts"]!);
        Assert.Equal(1, (int)roles["Category"]["maxFields"]!);
        Assert.Equal("Measure", (string)roles["Y"]["accepts"]!);
        Assert.Null(roles["Y"]["maxFields"]);
    }

    [Fact]
    public void ListRoles_DeprecatedType_MapsToModern_And_UnknownIsHonest()
    {
        var card = Obj(VisualDataRoles.ListRoles("matrix", formattingRegistryKnows: true));
        Assert.True((bool)card["deprecated"]!);
        Assert.Equal("pivotTable", (string)card["modernEquivalent"]!);
        Assert.Equal("curated", (string)card["coverage"]!);

        var modern = Obj(VisualDataRoles.ListRoles("tableEx", formattingRegistryKnows: true));
        Assert.Equal("table", (string)modern["legacyAlias"]!);

        var unknown = Obj(VisualDataRoles.ListRoles("decompositionTreeVisual", formattingRegistryKnows: false));
        Assert.Equal("none", (string)unknown["coverage"]!);
        Assert.Null(unknown["roles"]);
        Assert.Contains("hand-curated", (string)unknown["note"]!);
    }

    [Fact]
    public void Modernize_MapsDeprecated_LeavesModernAlone()
    {
        Assert.Equal("cardVisual", VisualDataRoles.Modernize("card"));
        Assert.Equal("tableEx", VisualDataRoles.Modernize("table"));
        Assert.Equal("pivotTable", VisualDataRoles.Modernize("matrix"));
        Assert.Equal("lineChart", VisualDataRoles.Modernize("lineChart"));
    }

    // ================================================================ change_visual_type

    [Fact]
    public void ChangeVisualType_MatrixToTable_RemapsRoles_KeepsPosition()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v = AddVisual(svc, sid, "Main", "pivotTable",
            new FieldBinding("Rows", "Customer", "Customer Name", "column"),
            new FieldBinding("Values", "Sales", "Total Sales", "measure"));

        var r = Obj(svc.ChangeVisualType(sid, "Main", v, "table", Catalog));

        Assert.Equal("pivotTable", (string)r["from"]!);
        Assert.Equal("tableEx", (string)r["to"]!);                 // deprecated target modernised
        Assert.True((bool)r["modernised"]!);
        Assert.Empty((JsonArray)r["droppedBindings"]!);

        var co = VisualConfig(store, sid, "Main", v);
        var sv = (JsonObject)co["singleVisual"]!;
        Assert.Equal("tableEx", (string)sv["visualType"]!);
        // both fields now live in the table's single Values role, order preserved
        var values = ((JsonArray)sv["projections"]!["Values"]!).OfType<JsonObject>()
            .Select(p => (string)p["queryRef"]!).ToArray();
        Assert.Equal(new[] { "Customer.Customer Name", "Sales.Total Sales" }, values);
        // position untouched (x=10, y=20 from the fixture)
        var pos = (JsonObject)((JsonArray)co["layouts"]!)[0]!["position"]!;
        Assert.Equal(10, (double)pos["x"]!);
        Assert.Equal(20, (double)pos["y"]!);
    }

    [Fact]
    public void ChangeVisualType_TableToPie_EnforcesCategoryCap_AndReportsDrop()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0));
        string v = AddVisual(svc, sid, "Main", "tableEx",
            new FieldBinding("Values", "Customer", "Customer Name", "column"),
            new FieldBinding("Values", "Customer", "City", "column"),
            new FieldBinding("Values", "Sales", "Total Sales", "measure"));

        var r = Obj(svc.ChangeVisualType(sid, "Main", v, "pieChart", Catalog));

        var remapped = ((JsonArray)r["remapped"]!).OfType<JsonObject>().ToList();
        Assert.Contains(remapped, m => (string)m["field"]! == "Customer[Customer Name]" && (string)m["to"]! == "Category");
        Assert.Contains(remapped, m => (string)m["field"]! == "Sales[Total Sales]" && (string)m["to"]! == "Y");
        // the second grouping column overflows Category's cap of 1 - dropped and REPORTED
        var dropped = ((JsonArray)r["droppedBindings"]!).Select(d => (string)d!).ToList();
        Assert.Contains(dropped, d => d.Contains("Customer[City]") && d.Contains("capped"));
    }

    [Fact]
    public void ChangeVisualType_PrunesFormatCards_TheNewTypeLacks_KeepsChrome()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v = AddVisual(svc, sid, "Main", "clusteredColumnChart",
            new FieldBinding("Category", "Customer", "Customer Name", "column"),
            new FieldBinding("Y", "Sales", "Total Sales", "measure"));
        // give it a chart-only card plus chrome; the card must go on conversion to a table
        svc.SetVisualFormat(sid, "Main", v, "{\"objects\":{\"categoryAxis\":{\"show\":false}},\"vcObjects\":{\"title\":{\"show\":true,\"text\":\"T\"}}}");

        var r = Obj(svc.ChangeVisualType(sid, "Main", v, "tableEx", Catalog));

        Assert.Contains("categoryAxis", ((JsonArray)r["droppedFormatCards"]!).Select(c => (string)c!));
        var sv = (JsonObject)VisualConfig(store, sid, "Main", v)["singleVisual"]!;
        Assert.Null(sv["objects"]?["categoryAxis"]);
        Assert.NotNull(sv["vcObjects"]?["title"]);                 // chrome always survives
    }

    [Fact]
    public void ChangeVisualType_SameType_Throws_UnknownTargetKeepsBindings()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v = AddVisual(svc, sid, "Main", "clusteredColumnChart",
            new FieldBinding("Category", "Customer", "Customer Name", "column"));

        Assert.Throws<InvalidOperationException>(() => svc.ChangeVisualType(sid, "Main", v, "clusteredColumnChart", Catalog));

        // a type outside the curated set: swap the type, carry projections, say so honestly
        var r = Obj(svc.ChangeVisualType(sid, "Main", v, "decompositionTreeVisual", Catalog));
        Assert.Equal("none", (string)r["roleCoverage"]!);
        var sv = (JsonObject)VisualConfig(store, sid, "Main", v)["singleVisual"]!;
        Assert.Equal("decompositionTreeVisual", (string)sv["visualType"]!);
        Assert.NotNull(sv["projections"]!["Category"]);            // bindings untouched
    }

    // ================================================================ bulk ops

    [Fact]
    public void BulkBindVisuals_PartialFailure_IsPerItem_NeverAllOrNothing()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v1 = AddVisual(svc, sid, "Main", "tableEx", new FieldBinding("Values", "Sales", "Old", "column"));
        string v2 = AddVisual(svc, sid, "Main", "tableEx", new FieldBinding("Values", "Sales", "Old", "column"));

        string items = JsonSerializer.Serialize(new object[]
        {
            new { page = "Main", visual = v1, bindings = new object[] { new { role = "Values", table = "Sales", field = "Amount", kind = "column" } } },
            new { page = "Main", visual = "no-such-visual", bindings = new object[] { new { role = "Values", table = "Sales", field = "Amount", kind = "column" } } },
            new { page = "Main", visual = v2, bindings = new object[] { new { role = "Values", table = "Sales", field = "Qty", kind = "column" } } },
        });
        var r = Obj(svc.BulkBindVisuals(sid, items));

        Assert.False((bool)r["ok"]!);                              // one item failed
        Assert.Equal(3, (int)r["items"]!);
        Assert.Equal(2, (int)r["succeeded"]!);
        Assert.Equal(1, (int)r["failed"]!);
        var rows = ((JsonArray)r["results"]!).OfType<JsonObject>().ToList();
        Assert.Equal(3, rows.Count);                               // one row per target, always
        Assert.False((bool)rows[1]["ok"]!);
        Assert.Contains("no-such-visual", (string)rows[1]["target"]!);
        Assert.False(string.IsNullOrWhiteSpace((string?)rows[1]["error"]));
        // the two good items really applied
        Assert.Contains("Sales.Amount", (string)VisualConfig(store, sid, "Main", v1).ToJsonString());
        Assert.Contains("Sales.Qty", (string)VisualConfig(store, sid, "Main", v2).ToJsonString());
    }

    [Fact]
    public void BulkSetVisualFormat_And_BulkDeleteVisuals_Work_PerItem()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v1 = AddVisual(svc, sid, "Main", "card", new FieldBinding("Values", "Sales", "Total Sales", "measure"));
        string v2 = AddVisual(svc, sid, "Main", "card", new FieldBinding("Values", "Sales", "Margin", "measure"));

        var fmt = Obj(svc.BulkSetVisualFormat(sid, JsonSerializer.Serialize(new object[]
        {
            new { page = "Main", visual = v1, format = new { vcObjects = new { title = new { show = true, text = "KPI" } } } },
            new { page = "Main", visual = "ghost", format = new { vcObjects = new { title = new { show = true } } } },
        })));
        Assert.Equal(1, (int)fmt["succeeded"]!);
        Assert.Equal(1, (int)fmt["failed"]!);
        Assert.Contains("KPI", VisualConfig(store, sid, "Main", v1).ToJsonString());

        var del = Obj(svc.BulkDeleteVisuals(sid, JsonSerializer.Serialize(new object[]
        {
            new { page = "Main", visual = v1 },
            new { page = "Main", visual = v2 },
            new { page = "Main", visual = "ghost" },
        })));
        Assert.Equal(2, (int)del["succeeded"]!);
        Assert.Equal(1, (int)del["failed"]!);
        var section = (JsonObject)((JsonArray)store.GetReport(sid).Layout.Root["sections"]!)[0]!;
        Assert.Empty((JsonArray)section["visualContainers"]!);
    }

    [Fact]
    public void BulkOps_EmptyOrMalformedItems_Throw()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0));
        Assert.Throws<ArgumentException>(() => svc.BulkDeleteVisuals(sid, "[]"));
        Assert.ThrowsAny<Exception>(() => svc.BulkDeleteVisuals(sid, "{not json"));
    }

    // ================================================================ bind_field_parameter

    [Fact]
    public void BindFieldParameter_SwapsRoleProjection_ToActiveParameter()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v = AddVisual(svc, sid, "Main", "clusteredColumnChart",
            new FieldBinding("Category", "Customer", "Customer Name", "column"),
            new FieldBinding("Y", "Sales", "Total Sales", "measure"));

        var r = Obj(svc.BindFieldParameter(sid, "Main", v, "Metric"));

        Assert.Equal("Y", (string)r["role"]!);                     // defaults to the measure well
        Assert.Equal("Metric[Metric]", (string)r["parameter"]!);
        Assert.Contains("Sales.Total Sales", ((JsonArray)r["replaced"]!).Select(x => (string)x!));

        var sv = (JsonObject)VisualConfig(store, sid, "Main", v)["singleVisual"]!;
        // the fifth piece: the role's projection is the parameter column with active=true
        var y = ((JsonArray)sv["projections"]!["Y"]!).OfType<JsonObject>().Single();
        Assert.Equal("Metric.Metric", (string)y["queryRef"]!);
        Assert.True((bool)y["active"]!);
        // Category untouched
        Assert.Equal("Customer.Customer Name",
            (string)((JsonArray)sv["projections"]!["Category"]!).OfType<JsonObject>().Single()["queryRef"]!);
        // the Select swapped: parameter column in, the replaced measure (and its table) out
        var pq = (JsonObject)sv["prototypeQuery"]!;
        var names = ((JsonArray)pq["Select"]!).OfType<JsonObject>().Select(s => (string)s["Name"]!).ToList();
        Assert.Contains("Metric.Metric", names);
        Assert.DoesNotContain("Sales.Total Sales", names);
        var entities = ((JsonArray)pq["From"]!).OfType<JsonObject>().Select(f => (string)f["Entity"]!).ToList();
        Assert.Contains("Metric", entities);
        Assert.DoesNotContain("Sales", entities);                  // pruned - nothing references it now
        Assert.Contains("Customer", entities);
    }

    [Fact]
    public void BindFieldParameter_ExplicitRole_And_SharedFieldSurvives()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        // the same field projected in TWO roles: replacing one role must keep the other's Select
        string v = AddVisual(svc, sid, "Main", "tableEx",
            new FieldBinding("Values", "Sales", "Total Sales", "measure"),
            new FieldBinding("Tooltips", "Sales", "Total Sales", "measure"));

        var r = Obj(svc.BindFieldParameter(sid, "Main", v, "Metric", parameterColumn: "Metric Col", role: "Values"));

        Assert.Equal("Values", (string)r["role"]!);
        var sv = (JsonObject)VisualConfig(store, sid, "Main", v)["singleVisual"]!;
        var names = ((JsonArray)sv["prototypeQuery"]!["Select"]!).OfType<JsonObject>().Select(s => (string)s["Name"]!).ToList();
        Assert.Contains("Metric.Metric Col", names);
        Assert.Contains("Sales.Total Sales", names);               // still projected via Tooltips
    }
}
