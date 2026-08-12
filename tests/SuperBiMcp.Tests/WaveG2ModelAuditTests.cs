using System.Text.Json;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave G2 model-side audits, driven over in-memory <c>new TOM.Model()</c> trees (no engine): the
/// star-schema classifier + smell flags + score (audit_star_schema), the naming audit's PLAN-ONLY
/// rename output with collision handling (audit_naming), and the data dictionary renderer with its
/// description-coverage score (export_data_dictionary).
/// </summary>
public sealed class WaveG2ModelAuditTests
{
    private static JsonObject Obj(object result) => (JsonObject)JsonSerializer.SerializeToNode(result)!;

    private static TOM.Table NewTable(TOM.Model model, string name, params (string col, TOM.DataType type)[] cols)
    {
        var t = new TOM.Table { Name = name };
        foreach (var (col, type) in cols)
            t.Columns.Add(new TOM.DataColumn { Name = col, DataType = type, SourceColumn = col });
        model.Tables.Add(t);
        return t;
    }

    private static void Relate(TOM.Model model, TOM.Table from, string fromCol, TOM.Table to, string toCol,
        TOM.RelationshipEndCardinality fromCard = TOM.RelationshipEndCardinality.Many,
        TOM.RelationshipEndCardinality toCard = TOM.RelationshipEndCardinality.One,
        TOM.CrossFilteringBehavior crossFilter = TOM.CrossFilteringBehavior.OneDirection)
    {
        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = Guid.NewGuid().ToString(),
            FromColumn = from.Columns[fromCol],
            ToColumn = to.Columns[toCol],
            FromCardinality = fromCard,
            ToCardinality = toCard,
            CrossFilteringBehavior = crossFilter,
        });
    }

    // ================================================================= audit_star_schema

    /// <summary>A model carrying every smell at once: snowflaked Region behind Customer, a bidirectional
    /// Sales->Customer filter, a many-to-many fact-to-fact Sales-Returns link, an UNMARKED date table,
    /// descriptive text on the fact, a bridge and a disconnected parameter table.</summary>
    private static TOM.Model SmellyModel()
    {
        var model = new TOM.Model { Name = "Smelly" };
        var sales = NewTable(model, "Sales",
            ("Amount", TOM.DataType.Double), ("CustomerKey", TOM.DataType.Int64),
            ("OrderDate", TOM.DataType.DateTime), ("OrderNote", TOM.DataType.String),
            ("ReturnKey", TOM.DataType.Int64));
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        var returns = NewTable(model, "Returns",
            ("ReturnKey", TOM.DataType.Int64), ("Qty", TOM.DataType.Int64));
        returns.Measures.Add(new TOM.Measure { Name = "Total Returns", Expression = "SUM('Returns'[Qty])" });
        var customer = NewTable(model, "Customer",
            ("CustomerKey", TOM.DataType.Int64), ("Country", TOM.DataType.String), ("RegionKey", TOM.DataType.Int64));
        var region = NewTable(model, "Region",
            ("RegionKey", TOM.DataType.Int64), ("RegionName", TOM.DataType.String));
        var dates = NewTable(model, "Dates",
            ("Date", TOM.DataType.DateTime), ("Year", TOM.DataType.Int64));
        NewTable(model, "Params", ("Value", TOM.DataType.Double));
        NewTable(model, "BridgeCR", ("CustomerKey", TOM.DataType.Int64), ("RegionKey", TOM.DataType.Int64));

        Relate(model, sales, "CustomerKey", customer, "CustomerKey",
            crossFilter: TOM.CrossFilteringBehavior.BothDirections);              // bidirectional
        Relate(model, customer, "RegionKey", region, "RegionKey");                // snowflake
        Relate(model, sales, "OrderDate", dates, "Date");                         // date-like, unmarked
        Relate(model, sales, "ReturnKey", returns, "ReturnKey",
            TOM.RelationshipEndCardinality.Many, TOM.RelationshipEndCardinality.Many); // M:M fact-to-fact
        var bridge = model.Tables["BridgeCR"];
        Relate(model, bridge, "CustomerKey", customer, "CustomerKey");
        Relate(model, bridge, "RegionKey", region, "RegionKey");
        return model;
    }

    [Fact]
    public void StarSchema_Classifies_Fact_Dim_Date_Bridge_Disconnected()
    {
        var r = Obj(ModelService.AuditStarSchemaCore(SmellyModel()));

        var cls = ((JsonArray)r["tables"]!).OfType<JsonObject>()
            .ToDictionary(t => (string)t["table"]!, t => (string)t["classification"]!);
        Assert.Equal("fact", cls["Sales"]);
        Assert.Equal("dimension", cls["Customer"]);
        Assert.Equal("dimension", cls["Region"]);
        Assert.Equal("date", cls["Dates"]);
        Assert.Equal("disconnected", cls["Params"]);
        Assert.Equal("bridge", cls["BridgeCR"]);

        var counts = (JsonObject)r["counts"]!;
        Assert.True((int)counts["facts"]! >= 1);
        Assert.Equal(1, (int)counts["dateTables"]!);
        Assert.Equal(1, (int)counts["bridges"]!);
        Assert.Equal(1, (int)counts["disconnected"]!);
    }

    [Fact]
    public void StarSchema_Flags_EverySmell_AndScoresDown()
    {
        var r = Obj(ModelService.AuditStarSchemaCore(SmellyModel()));

        var kinds = ((JsonArray)r["issues"]!).OfType<JsonObject>()
            .Select(i => (string)i["kind"]!).ToList();
        Assert.Contains("bidirectional-filter", kinds);
        Assert.Contains("many-to-many", kinds);
        Assert.Contains("fact-to-fact", kinds);
        Assert.Contains("snowflake", kinds);
        Assert.Contains("unmarked-date-table", kinds);
        Assert.Contains("text-on-fact", kinds);

        Assert.True((int)r["score"]! < 70, "a model with every smell must score well below the clean band");
        // every issue carries a recommendation naming an action
        foreach (var issue in ((JsonArray)r["issues"]!).OfType<JsonObject>())
            Assert.False(string.IsNullOrWhiteSpace((string?)issue["recommendation"]));
    }

    [Fact]
    public void StarSchema_CleanStar_Scores100()
    {
        var model = new TOM.Model { Name = "Clean" };
        var sales = NewTable(model, "Sales",
            ("Amount", TOM.DataType.Double), ("CustomerKey", TOM.DataType.Int64), ("OrderDate", TOM.DataType.DateTime));
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        var customer = NewTable(model, "Customer",
            ("CustomerKey", TOM.DataType.Int64), ("Country", TOM.DataType.String));
        var dates = NewTable(model, "Date", ("Date", TOM.DataType.DateTime), ("Year", TOM.DataType.Int64));
        dates.DataCategory = "Time";                                              // marked date table
        Relate(model, sales, "CustomerKey", customer, "CustomerKey");
        Relate(model, sales, "OrderDate", dates, "Date");

        var r = Obj(ModelService.AuditStarSchemaCore(model));

        Assert.Equal(100, (int)r["score"]!);
        Assert.Equal("clean star schema", (string)r["verdict"]!);
        Assert.Equal(0, (int)r["issueCount"]!);
    }

    [Fact]
    public void StarSchema_MissingDateTable_IsFlagged_WhenFactsCarryDates()
    {
        var model = new TOM.Model { Name = "NoDates" };
        var sales = NewTable(model, "Sales",
            ("Amount", TOM.DataType.Double), ("CustomerKey", TOM.DataType.Int64), ("OrderDate", TOM.DataType.DateTime));
        var customer = NewTable(model, "Customer", ("CustomerKey", TOM.DataType.Int64));
        Relate(model, sales, "CustomerKey", customer, "CustomerKey");

        var r = Obj(ModelService.AuditStarSchemaCore(model));

        Assert.Contains(((JsonArray)r["issues"]!).OfType<JsonObject>(),
            i => (string)i["kind"]! == "missing-date-table");
    }

    // ================================================================= audit_naming

    private static TOM.Model NamingModel()
    {
        var model = new TOM.Model { Name = "Naming" };
        var dimCust = NewTable(model, "DIM_Customer",
            ("customer_name", TOM.DataType.String), ("CustomerID", TOM.DataType.Int64));
        var factSales = NewTable(model, "fact_sales", ("Amount", TOM.DataType.Double));
        factSales.Measures.Add(new TOM.Measure { Name = "total_sales", Expression = "SUM('fact_sales'[Amount])" });
        factSales.Measures.Add(new TOM.Measure { Name = "Margin ", Expression = "1" });     // trailing space
        NewTable(model, "Products", ("SKU", TOM.DataType.String), ("Product Name", TOM.DataType.String));
        return model;
    }

    [Fact]
    public void Naming_ProposesHumanReadableRenames_WithReasons()
    {
        var model = NamingModel();
        var r = Obj(ModelService.AuditNamingCore(model));

        Assert.True((bool)r["planOnly"]!);
        var renames = ((JsonArray)r["renamePlan"]!["renames"]!).OfType<JsonObject>().ToList();

        var table = renames.First(x => (string)x["objectType"]! == "table" && (string)x["oldName"]! == "DIM_Customer");
        Assert.Equal("Customer", (string)table["newName"]!);
        Assert.Contains("prefix", (string)table["reason"]!);

        var table2 = renames.First(x => (string)x["objectType"]! == "table" && (string)x["oldName"]! == "fact_sales");
        Assert.Equal("Sales", (string)table2["newName"]!);

        var col = renames.First(x => (string)x["objectType"]! == "column" && (string)x["oldName"]! == "customer_name");
        Assert.Equal("Customer Name", (string)col["newName"]!);
        Assert.Equal("DIM_Customer", (string)col["table"]!);
        Assert.Contains("snake_case", (string)col["reason"]!);

        var idCol = renames.First(x => (string)x["objectType"]! == "column" && (string)x["oldName"]! == "CustomerID");
        Assert.Equal("Customer ID", (string)idCol["newName"]!);                   // acronym preserved

        var measure = renames.First(x => (string)x["objectType"]! == "measure" && (string)x["oldName"]! == "total_sales");
        Assert.Equal("Total Sales", (string)measure["newName"]!);

        var trailing = renames.First(x => (string)x["objectType"]! == "measure" && (string)x["oldName"]! == "Margin ");
        Assert.Equal("Margin", (string)trailing["newName"]!);
        Assert.Contains("whitespace", (string)trailing["reason"]!);

        // clean names propose nothing
        Assert.DoesNotContain(renames, x => (string)x["oldName"]! == "Products");
        Assert.DoesNotContain(renames, x => (string)x["oldName"]! == "SKU");
        Assert.DoesNotContain(renames, x => (string)x["oldName"]! == "Product Name");
    }

    [Fact]
    public void Naming_IsPlanOnly_TheModelIsNeverMutated()
    {
        var model = NamingModel();
        ModelService.AuditNamingCore(model);

        Assert.NotNull(model.Tables.Find("DIM_Customer"));
        Assert.NotNull(model.Tables.Find("fact_sales"));
        Assert.Null(model.Tables.Find("Customer"));
        Assert.Equal("customer_name", model.Tables["DIM_Customer"].Columns[0].Name);
    }

    [Fact]
    public void Naming_Collisions_AreSkipped_NotProposedTwice()
    {
        var model = new TOM.Model { Name = "Collide" };
        NewTable(model, "DIM_Customer", ("A", TOM.DataType.String));
        NewTable(model, "Customer", ("B", TOM.DataType.String));                  // the target name already exists
        var t = NewTable(model, "T", ("total_sales", TOM.DataType.Double));
        t.Measures.Add(new TOM.Measure { Name = "sales_total", Expression = "1" });
        var t2 = NewTable(model, "T2", ("X", TOM.DataType.Double));
        t2.Measures.Add(new TOM.Measure { Name = "Sales Total", Expression = "1" }); // measure names are model-wide

        var r = Obj(ModelService.AuditNamingCore(model));

        var renames = ((JsonArray)r["renamePlan"]!["renames"]!).OfType<JsonObject>().ToList();
        var skipped = ((JsonArray)r["skipped"]!).OfType<JsonObject>().ToList();

        Assert.DoesNotContain(renames, x => (string)x["oldName"]! == "DIM_Customer");
        Assert.Contains(skipped, x => (string)x["oldName"]! == "DIM_Customer"
                                      && ((string)x["reason"]!).Contains("collision"));
        Assert.Contains(skipped, x => (string)x["oldName"]! == "sales_total");     // collides with 'Sales Total'
    }

    [Fact]
    public void ProposeName_PureCases()
    {
        Assert.Equal("Customer", ModelService.ProposeName("DIM_Customer", stripTablePrefix: true).proposed);
        Assert.Equal("Internet Sales", ModelService.ProposeName("FactInternetSales", stripTablePrefix: true).proposed);
        Assert.Equal("Customer Name", ModelService.ProposeName("customerName", stripTablePrefix: false).proposed);
        Assert.Equal("Order ID", ModelService.ProposeName("order_id", stripTablePrefix: false).proposed);
        Assert.Equal("Products", ModelService.ProposeName(" Products ", stripTablePrefix: true).proposed);
        // a clean name comes back unchanged with no reasons
        var (clean, reasons) = ModelService.ProposeName("Total Sales", stripTablePrefix: false);
        Assert.Equal("Total Sales", clean);
        Assert.Empty(reasons);
        // DimCustomer glued prefix only strips for TABLES
        Assert.Equal("Customer", ModelService.ProposeName("DimCustomer", stripTablePrefix: true).proposed);
        Assert.Equal("Dim Customer", ModelService.ProposeName("DimCustomer", stripTablePrefix: false).proposed);
    }

    // ================================================================= export_data_dictionary

    private static TOM.Model DictionaryModel()
    {
        var model = new TOM.Model { Name = "Dict" };
        var sales = NewTable(model, "Sales",
            ("Amount", TOM.DataType.Double), ("CustomerKey", TOM.DataType.Int64));
        sales.Description = "Fact table of sales lines";
        sales.Columns["Amount"].Description = "Line value in NZD";
        sales.Columns["Amount"].FormatString = "#,0.00";
        sales.Measures.Add(new TOM.Measure
        { Name = "Total Sales", Expression = "SUM('Sales'[Amount])", Description = "Sum of line values" });
        sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1" }); // undescribed
        var customer = NewTable(model, "Customer",
            ("CustomerKey", TOM.DataType.Int64), ("Country", TOM.DataType.String)); // undescribed table + columns
        Relate(model, sales, "CustomerKey", customer, "CustomerKey");
        return model;
    }

    [Fact]
    public void DataDictionary_Markdown_RendersObjects_AndScoresCoverage()
    {
        var r = Obj(ModelService.ExportDataDictionaryCore(DictionaryModel(), "md"));

        Assert.Equal("md", (string)r["format"]!);
        string content = (string)r["content"]!;
        Assert.Contains("# Data dictionary - Dict", content);
        Assert.Contains("## Sales", content);
        Assert.Contains("Line value in NZD", content);
        Assert.Contains("Total Sales", content);
        Assert.Contains("SUM('Sales'[Amount])", content);
        Assert.Contains("## Relationships", content);
        Assert.Contains("Sales[CustomerKey]", content);

        // coverage: objects = 2 tables + 4 columns + 2 measures = 8; described = Sales + Amount + Total Sales = 3
        var cov = (JsonObject)r["coverage"]!;
        Assert.Equal(8, (int)cov["objects"]!);
        Assert.Equal(3, (int)cov["described"]!);
        Assert.Equal(37.5, (double)cov["percent"]!, 1);
        Assert.Equal("1/2", (string)cov["tables"]!);
        Assert.Equal("1/4", (string)cov["columns"]!);
        Assert.Equal("1/2", (string)cov["measures"]!);
    }

    [Fact]
    public void DataDictionary_Html_IsEncoded_AndSelfContained()
    {
        var model = DictionaryModel();
        model.Tables["Sales"].Description = "Uses <b>bold</b> & ampersands";

        var r = Obj(ModelService.ExportDataDictionaryCore(model, "html"));

        Assert.Equal("html", (string)r["format"]!);
        string content = (string)r["content"]!;
        Assert.Contains("<table>", content);
        Assert.Contains("&lt;b&gt;bold&lt;/b&gt; &amp; ampersands", content);     // HTML-encoded
        Assert.Contains("</html>", content);
    }

    [Fact]
    public void DataDictionary_UnknownFormat_Throws()
    {
        Assert.Throws<ArgumentException>(() => ModelService.ExportDataDictionaryCore(DictionaryModel(), "pdf"));
    }

    [Fact]
    public void DataDictionary_EmptyModel_Is100PercentCoverage()
    {
        var r = Obj(ModelService.ExportDataDictionaryCore(new TOM.Model { Name = "Empty" }, "md"));
        Assert.Equal(100, (double)r["coverage"]!["percent"]!, 1);
        Assert.Equal(0, (int)r["coverage"]!["objects"]!);
    }
}
