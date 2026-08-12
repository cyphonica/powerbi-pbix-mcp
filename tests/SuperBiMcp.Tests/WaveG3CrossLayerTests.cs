using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave G3 cross-layer tests: the legacy and PBIR field-use collectors (projections, tooltips,
/// filters, conditional-formatting and chrome bindings all land as uses), the pure DIRECT /
/// INDIRECT / UNUSED classifier over an in-memory TOM model, impact_analysis blast radius,
/// scan_broken_refs suggestions, and the fix_broken_visuals rewriter (Select + From + projections
/// + filters, alias repointing on a table move). No live engine anywhere.
/// </summary>
public sealed class WaveG3CrossLayerTests
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

    private static string AddChart(ReportService svc, string sid, string page, params FieldBinding[] binds)
        => (string)Obj(svc.AddVisual(sid, page, "clusteredColumnChart", 0, 0, 0, 400, 300, binds, null))["visualName"]!;

    /// <summary>Patch a visual's parsed config in place (the test seam for CF / chrome bindings).</summary>
    private static void PatchConfig(SessionStore store, string sid, string page, string visualName, Action<JsonObject> edit)
    {
        var root = store.GetReport(sid).Layout.Root;
        foreach (var section in ((JsonArray)root["sections"]!).OfType<JsonObject>())
        {
            if ((string?)section["displayName"] != page) continue;
            foreach (var vc in ((JsonArray)section["visualContainers"]!).OfType<JsonObject>())
            {
                var co = (JsonObject)JsonNode.Parse((string)vc["config"]!)!;
                if ((string?)co["name"] != visualName) continue;
                edit(co);
                vc["config"] = co.ToJsonString();
                return;
            }
        }
        throw new InvalidOperationException("visual not found for patching");
    }

    private static JsonObject MeasureExpr(string table, string measure) => new JsonObject
    {
        ["Measure"] = new JsonObject
        {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } },
            ["Property"] = measure,
        },
    };

    /// <summary>The star model both classification suites run over.</summary>
    private static TOM.Model StarModel()
    {
        var model = new TOM.Model { Name = "M" };
        var customer = new TOM.Table { Name = "Customer" };
        customer.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey" });
        customer.Columns.Add(new TOM.DataColumn { Name = "Customer Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
        customer.Columns.Add(new TOM.DataColumn { Name = "NameSort", DataType = TOM.DataType.Int64, SourceColumn = "NameSort" });
        customer.Columns.Add(new TOM.DataColumn { Name = "City", DataType = TOM.DataType.String, SourceColumn = "City" });
        customer.Columns["Customer Name"].SortByColumn = customer.Columns["NameSort"];
        model.Tables.Add(customer);

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Qty", DataType = TOM.DataType.Int64, SourceColumn = "Qty" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM ( Sales[Amount] )" });
        sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "[Total Sales] * 0.3" });
        sales.Measures.Add(new TOM.Measure { Name = "Orphan Measure", Expression = "1" });
        model.Tables.Add(sales);

        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = "SalesToCustomer",
            FromColumn = sales.Columns["CustomerKey"],
            ToColumn = customer.Columns["CustomerKey"],
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
        });
        return model;
    }

    private static ReportFieldUse Use(string table, string field, bool measure = false,
        string context = "projection", string page = "P", string visual = "v1")
        => new(table, field, measure, context, page, visual);

    // ================================================================ legacy collector

    [Fact]
    public void LegacyCollector_Projections_Tooltips_Filters_CF_And_Chrome()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v1 = AddChart(svc, sid, "Main",
            new FieldBinding("Category", "Customer", "Customer Name", "column"),
            new FieldBinding("Y", "Sales", "Total Sales", "measure"),
            new FieldBinding("Tooltips", "Sales", "Qty", "column"));
        svc.AddVisualFilter(sid, "Main", v1, "Sales", "Amount", "column", "gt", "0", "int");
        svc.AddReportFilter(sid, "Customer", "City", "categorical", "column", null, new[] { "Akl" }, "string");

        // a conditional-formatting colour measure + a dynamic-title measure, patched in raw
        PatchConfig(store, sid, "Main", v1, co =>
        {
            var sv = (JsonObject)co["singleVisual"]!;
            sv["objects"] = new JsonObject
            {
                ["dataPoint"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                {
                    ["fill"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonObject
                        { ["expr"] = MeasureExpr("Sales", "Colour M") } } },
                } } },
            };
            sv["vcObjects"] = new JsonObject
            {
                ["title"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                    { ["text"] = new JsonObject { ["expr"] = MeasureExpr("Sales", "Title M") } } } },
            };
        });

        var uses = svc.CollectFieldUses(sid);

        Assert.Contains(uses, u => u is { Table: "Customer", Field: "Customer Name", Context: "projection", IsMeasure: false });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Total Sales", Context: "projection", IsMeasure: true });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Qty", Context: "tooltip" });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Amount", Context: "filter", Visual: not "(page)" });
        Assert.Contains(uses, u => u is { Table: "Customer", Field: "City", Context: "filter", Visual: "(report)" });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Colour M", Context: "conditional-formatting", IsMeasure: true });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Title M", Context: "chrome-binding", IsMeasure: true });
        // every use is attributed to the right visual
        Assert.All(uses.Where(u => u.Context is "projection" or "tooltip" or "conditional-formatting" or "chrome-binding"),
            u => Assert.Equal(v1, u.Visual));
    }

    [Fact]
    public void LegacyCollector_EmptyReport_NoUses()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0));
        Assert.Empty(svc.CollectFieldUses(sid));
    }

    // ================================================================ PBIR collector

    [Fact]
    public void PbirCollector_QueryState_CF_And_PageFilters()
    {
        using var work = Fixtures.NewWorkDir();
        string root = work.Path;
        void Put(string rel, string json)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, json, new UTF8Encoding(false));
        }
        Put("definition.pbir", "{\"version\":\"1.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../Model\"}}}");
        Put("report.json", "{}");
        Put("definition/pages/pages.json", "{\"pageOrder\":[\"p1\"],\"activePageName\":\"p1\"}");
        Put("definition/pages/p1/page.json",
            "{\"name\":\"p1\",\"displayName\":\"Overview\",\"width\":1280,\"height\":720," +
            "\"filterConfig\":{\"filters\":[{\"name\":\"f1\",\"expression\":{\"Column\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Customer\"}},\"Property\":\"City\"}}}]}}");
        Put("definition/pages/p1/visuals/v1/visual.json",
            "{\"name\":\"v1\",\"position\":{\"x\":0,\"y\":0,\"z\":0,\"width\":400,\"height\":300}," +
            "\"visual\":{\"visualType\":\"columnChart\",\"query\":{\"queryState\":{" +
            "\"Category\":{\"projections\":[{\"field\":{\"Column\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Customer\"}},\"Property\":\"Customer Name\"}},\"queryRef\":\"Customer.Customer Name\"}]}," +
            "\"Y\":{\"projections\":[{\"field\":{\"Measure\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Sales\"}},\"Property\":\"Total Sales\"}},\"queryRef\":\"Sales.Total Sales\",\"active\":true}]}}}," +
            "\"objects\":{\"dataPoint\":[{\"properties\":{\"fill\":{\"solid\":{\"color\":{\"expr\":{\"Measure\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Sales\"}},\"Property\":\"Colour M\"}}}}}}}]}}}");

        var store = new SessionStore();
        var pbir = new PbirService(store, NullLogger<PbirService>.Instance);
        string sid = (string)Obj(pbir.ReadPbir(root))["pbirSessionId"]!;

        var uses = CrossLayerAnalyzer.CollectPbirUses(pbir.SessionModel(sid));

        Assert.Contains(uses, u => u is { Table: "Customer", Field: "Customer Name", Context: "projection", Page: "Overview", Visual: "v1" });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Total Sales", IsMeasure: true, Context: "projection" });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Colour M", Context: "conditional-formatting" });
        Assert.Contains(uses, u => u is { Table: "Customer", Field: "City", Context: "filter", Visual: "(page)" });
    }

    // ================================================================ classification

    [Fact]
    public void Classify_Direct_Indirect_Unused_Tiers()
    {
        var inv = CrossLayerAnalyzer.BuildInventory(StarModel());
        var uses = new List<ReportFieldUse>
        {
            Use("Customer", "Customer Name"),
            Use("Sales", "Total Sales", measure: true),
        };

        var r = Obj(CrossLayerAnalyzer.Classify(inv, uses, "test"));
        var byObject = ((JsonArray)r["fields"]!).OfType<JsonObject>()
            .ToDictionary(f => (string)f["object"]!, f => f);

        Assert.Equal("DIRECT", (string)byObject["Customer[Customer Name]"]["tier"]!);
        Assert.Equal("DIRECT", (string)byObject["Sales[Total Sales]"]["tier"]!);
        Assert.Contains("P/v1", ((JsonArray)byObject["Sales[Total Sales]"]["visuals"]!).Select(v => (string)v!));

        // DAX lineage: Total Sales -> Sales[Amount]
        Assert.Equal("INDIRECT", (string)byObject["Sales[Amount]"]["tier"]!);
        Assert.Contains(((JsonArray)byObject["Sales[Amount]"]["reasons"]!).Select(x => (string)x!),
            s => s.Contains("DAX lineage"));
        // relationship path between the two in-play tables
        Assert.Equal("INDIRECT", (string)byObject["Sales[CustomerKey]"]["tier"]!);
        Assert.Equal("INDIRECT", (string)byObject["Customer[CustomerKey]"]["tier"]!);
        Assert.Contains(((JsonArray)byObject["Sales[CustomerKey]"]["reasons"]!).Select(x => (string)x!),
            s => s.Contains("relationship path"));
        // sort-by of a direct column
        Assert.Equal("INDIRECT", (string)byObject["Customer[NameSort]"]["tier"]!);
        Assert.Contains(((JsonArray)byObject["Customer[NameSort]"]["reasons"]!).Select(x => (string)x!),
            s => s.Contains("sort-by"));
        // untouched by report AND model -> safe to remove
        Assert.Equal("UNUSED", (string)byObject["Customer[City]"]["tier"]!);
        Assert.Equal("UNUSED", (string)byObject["Sales[Qty]"]["tier"]!);
        Assert.Equal("UNUSED", (string)byObject["Sales[Margin]"]["tier"]!);
        Assert.Equal("UNUSED", (string)byObject["Sales[Orphan Measure]"]["tier"]!);

        var safe = ((JsonArray)r["safeToRemove"]!).Select(v => (string)v!).ToList();
        Assert.Contains("Customer[City]", safe);
        Assert.Contains("Sales[Orphan Measure]", safe);
        Assert.DoesNotContain("Sales[Amount]", safe);
        Assert.Equal(2, (int)r["summary"]!["direct"]!);
    }

    [Fact]
    public void Classify_UnresolvedBinding_Reported_NotClassified()
    {
        var inv = CrossLayerAnalyzer.BuildInventory(StarModel());
        var uses = new List<ReportFieldUse> { Use("Sales", "Ghost Field") };

        var r = Obj(CrossLayerAnalyzer.Classify(inv, uses, "test"));

        Assert.Equal(0, (int)r["summary"]!["direct"]!);
        var unresolved = ((JsonArray)r["unresolvedBindings"]!).OfType<JsonObject>().ToList();
        Assert.Contains(unresolved, u => (string)u["field"]! == "Sales[Ghost Field]");
    }

    [Fact]
    public void Classify_CfOnlyBinding_IsDirect()
    {
        // the binding naive scanners miss: a measure used ONLY as a conditional-formatting colour
        var inv = CrossLayerAnalyzer.BuildInventory(StarModel());
        var uses = new List<ReportFieldUse> { Use("Sales", "Orphan Measure", measure: true, context: "conditional-formatting") };

        var r = Obj(CrossLayerAnalyzer.Classify(inv, uses, "test"));
        var f = ((JsonArray)r["fields"]!).OfType<JsonObject>().First(x => (string)x["object"]! == "Sales[Orphan Measure]");

        Assert.Equal("DIRECT", (string)f["tier"]!);
        Assert.Contains("conditional-formatting", ((JsonArray)f["reasons"]!).Select(x => (string)x!));
    }

    // ================================================================ impact analysis

    [Fact]
    public void Impact_TransitiveDependants_And_ReportVisuals()
    {
        var inv = CrossLayerAnalyzer.BuildInventory(StarModel());
        var uses = new List<ReportFieldUse>
        {
            Use("Sales", "Total Sales", measure: true, visual: "chart1"),
            Use("Sales", "Margin", measure: true, visual: "card9", page: "P2"),
        };

        var r = Obj(CrossLayerAnalyzer.Impact(inv, uses, "Sales[Amount]"));

        var deps = ((JsonArray)r["modelDependants"]!).OfType<JsonObject>().Select(d => (string)d["object"]!).ToList();
        Assert.Contains("Sales[Total Sales]", deps);            // direct DAX reference
        Assert.Contains("Sales[Margin]", deps);                 // transitive via Total Sales
        Assert.DoesNotContain("Sales[Orphan Measure]", deps);

        var visuals = ((JsonArray)r["reportVisuals"]!).OfType<JsonObject>().ToList();
        Assert.Contains(visuals, v => (string)v["visual"]! == "chart1" && (string)v["via"]! == "Sales[Total Sales]");
        Assert.Contains(visuals, v => (string)v["visual"]! == "card9" && (string)v["via"]! == "Sales[Margin]");
    }

    [Fact]
    public void Impact_BareMeasureName_Resolves_UnknownThrows()
    {
        var inv = CrossLayerAnalyzer.BuildInventory(StarModel());
        var r = Obj(CrossLayerAnalyzer.Impact(inv, new List<ReportFieldUse>(), "Total Sales"));
        Assert.Equal("Sales[Total Sales]", (string)r["object"]!);
        Assert.Contains("Sales[Margin]",
            ((JsonArray)r["modelDependants"]!).OfType<JsonObject>().Select(d => (string)d["object"]!));

        Assert.Throws<InvalidOperationException>(() => CrossLayerAnalyzer.Impact(inv, new List<ReportFieldUse>(), "No Such Thing"));
    }

    // ================================================================ broken refs

    [Fact]
    public void BrokenRefs_Typo_And_MovedTable_GetSuggestions()
    {
        var inv = CrossLayerAnalyzer.BuildInventory(StarModel());
        var uses = new List<ReportFieldUse>
        {
            Use("Sales", "Amout"),                                       // typo: Amount
            Use("Old Sales", "Total Sales", measure: true),              // table renamed
            Use("Sales", "Total Sales", measure: true),                  // healthy - never flagged
        };

        var r = Obj(CrossLayerAnalyzer.FindBrokenRefs(inv, uses, "test"));

        Assert.Equal(2, (int)r["brokenCount"]!);
        var broken = ((JsonArray)r["broken"]!).OfType<JsonObject>().ToDictionary(b => (string)b["field"]!, b => b);

        var typo = broken["Sales[Amout]"];
        Assert.True((bool)typo["tableExists"]!);
        Assert.Contains("Sales[Amount]", ((JsonArray)typo["suggestions"]!).Select(s => (string)s!));

        var moved = broken["Old Sales[Total Sales]"];
        Assert.False((bool)moved["tableExists"]!);
        Assert.Contains("Sales[Total Sales]", ((JsonArray)moved["suggestions"]!).Select(s => (string)s!));
    }

    [Fact]
    public void BrokenRefs_CleanReport_ReturnsZero()
    {
        var inv = CrossLayerAnalyzer.BuildInventory(StarModel());
        var r = Obj(CrossLayerAnalyzer.FindBrokenRefs(inv,
            new List<ReportFieldUse> { Use("Sales", "Amount") }, "test"));
        Assert.Equal(0, (int)r["brokenCount"]!);
    }

    [Fact]
    public void EditDistance_Basics()
    {
        Assert.Equal(0, CrossLayerAnalyzer.EditDistance("amount", "amount"));
        Assert.Equal(1, CrossLayerAnalyzer.EditDistance("amout", "amount"));
        Assert.Equal(6, CrossLayerAnalyzer.EditDistance("", "amount"));
    }

    // ================================================================ fix_broken_visuals

    [Fact]
    public void FixBrokenVisuals_RewritesSelect_Projections_Filters_AndAliases()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v1 = AddChart(svc, sid, "Main",
            new FieldBinding("Category", "Customer", "Name", "column"),
            new FieldBinding("Y", "Sales", "Amout", "measure"));
        svc.AddVisualFilter(sid, "Main", v1, "Sales", "Amout", "column", "gt", "0", "int");

        var r = Obj(svc.FixBrokenVisuals(sid,
            "{\"Sales[Amout]\":\"Sales[Amount]\",\"Customer[Name]\":\"Client[Full Name]\",\"Never[Seen]\":\"X[Y]\"}"));

        Assert.True((bool)r["ok"]!);
        Assert.Equal(new[] { "Never[Seen]" },
            ((JsonArray)r["unmatchedRepairs"]!).Select(u => (string)u!).ToArray());
        Assert.True(store.GetReport(sid).Dirty);

        // read the rewritten visual back through the collector - both readers agree on the result
        var uses = svc.CollectFieldUses(sid);
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Amount", Context: "projection" });
        Assert.Contains(uses, u => u is { Table: "Client", Field: "Full Name", Context: "projection" });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Amount", Context: "filter" });
        Assert.DoesNotContain(uses, u => u.Field == "Amout" || u.Field == "Name");

        // the raw config: queryRefs renamed, the moved table's From entity repointed
        string cfg = "";
        var root = store.GetReport(sid).Layout.Root;
        foreach (var vc in ((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!).OfType<JsonObject>())
            cfg = (string)vc["config"]!;
        Assert.Contains("Sales.Amount", cfg);
        Assert.Contains("Client.Full Name", cfg);
        Assert.Contains("\"Entity\":\"Client\"", cfg.Replace(" ", ""));
        Assert.DoesNotContain("Amout", cfg);
    }

    [Fact]
    public void FixBrokenVisuals_CfBinding_And_ReportFilters_AlsoRepaired()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v1 = AddChart(svc, sid, "Main", new FieldBinding("Y", "Sales", "Amount", "measure"));
        svc.AddReportFilter(sid, "Sales", "Old Region", "categorical", "column", null, new[] { "NZ" }, "string");
        PatchConfig(store, sid, "Main", v1, co =>
        {
            ((JsonObject)co["singleVisual"]!)["objects"] = new JsonObject
            {
                ["dataPoint"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                {
                    ["fill"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonObject
                        { ["expr"] = MeasureExpr("Sales", "Old Colour") } } },
                } } },
            };
        });

        var r = Obj(svc.FixBrokenVisuals(sid,
            "{\"Sales[Old Colour]\":\"Sales[New Colour]\",\"Sales[Old Region]\":\"Sales[Region]\"}"));

        Assert.Empty((JsonArray)r["unmatchedRepairs"]!);
        var uses = svc.CollectFieldUses(sid);
        Assert.Contains(uses, u => u is { Field: "New Colour", Context: "conditional-formatting" });
        Assert.Contains(uses, u => u is { Field: "Region", Context: "filter", Visual: "(report)" });
        Assert.DoesNotContain(uses, u => u.Field is "Old Colour" or "Old Region");
    }

    [Fact]
    public void FixBrokenVisuals_MalformedMap_Throws()
    {
        var (svc, sid, _) = NewReport(Section("Main", 0));
        Assert.Throws<ArgumentException>(() => svc.FixBrokenVisuals(sid, "[]"));
        Assert.Throws<ArgumentException>(() => svc.FixBrokenVisuals(sid, "{}"));
        Assert.Throws<ArgumentException>(() => svc.FixBrokenVisuals(sid, "{\"no-brackets\":\"X[Y]\"}"));
    }
}
