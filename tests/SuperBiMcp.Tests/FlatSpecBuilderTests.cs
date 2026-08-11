using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Exercises the no-Solution auto-shape spec synthesiser: one flat table per discovered CSV, the canonical
/// DataFolder parameter carrying the {{DATA_FOLDER}} token, the Csv.Document |> PromoteHeaders |>
/// TransformColumnTypes M shape, spec-token -> M-type mapping, M-string escaping of a quote in a column name,
/// and the structural guarantee that NO measures are emitted (so a synthesised flat table can never collide a
/// measure name with a column name - the AI prompter adds measures later).
/// </summary>
public sealed class FlatSpecBuilderTests
{
    private static SchemaDiscovery SampleSchema()
    {
        var schema = new SchemaDiscovery();
        var sales = new TableSchema("Sales");
        sales.Columns.Add(new ColumnSchema("OrderId", "int64"));
        sales.Columns.Add(new ColumnSchema("Amount", "double"));
        sales.Columns.Add(new ColumnSchema("OrderDate", "date"));
        sales.Columns.Add(new ColumnSchema("Active", "boolean"));
        sales.Columns.Add(new ColumnSchema("Customer", "string"));
        schema.Tables.Add(sales);

        var region = new TableSchema("Region");
        region.Columns.Add(new ColumnSchema("RegionKey", "int64"));
        region.Columns.Add(new ColumnSchema("Region Name", "string"));
        schema.Tables.Add(region);
        return schema;
    }

    private static JsonObject Build(SchemaDiscovery schema)
        => (JsonObject)JsonNode.Parse(FlatSpecBuilder.Build(schema))!;

    [Fact]
    public void Build_ProducesParseableJson_WithDataFolderParameter()
    {
        var spec = Build(SampleSchema());
        var exprs = spec["expressions"] as JsonArray;
        Assert.NotNull(exprs);
        var dataFolder = exprs!.Single() as JsonObject;
        Assert.Equal("DataFolder", (string?)dataFolder!["name"]);
        // the literal token the bake/materialise step substitutes
        Assert.Contains("{{DATA_FOLDER}}", (string?)dataFolder["expression"]);
    }

    [Fact]
    public void Build_EmitsOneTablePerCsv()
    {
        var spec = Build(SampleSchema());
        var tables = spec["tables"] as JsonArray;
        Assert.Equal(2, tables!.Count);
        Assert.Equal(new[] { "Sales", "Region" },
            tables.Select(t => (string?)t!["name"]).ToArray());
    }

    [Fact]
    public void Build_TableM_HasCanonicalCsvDocumentShape()
    {
        var spec = Build(SampleSchema());
        var sales = (spec["tables"] as JsonArray)!.First(t => (string?)t!["name"] == "Sales")!;
        string m = sales["m"]!.GetValue<string>();

        Assert.Contains("Csv.Document(File.Contents(DataFolder & \"/Sales.csv\")", m);
        Assert.Contains("Encoding=65001", m);
        Assert.Contains("QuoteStyle=QuoteStyle.Csv", m);
        Assert.Contains("Table.PromoteHeaders(Source,[PromoteAllScalars=true])", m);
        Assert.Contains("Table.TransformColumnTypes", m);
        Assert.EndsWith(" in Typed", m);
    }

    [Fact]
    public void Build_MapsSpecTokensToMTypes()
    {
        var spec = Build(SampleSchema());
        var sales = (spec["tables"] as JsonArray)!.First(t => (string?)t!["name"] == "Sales")!;
        string m = sales["m"]!.GetValue<string>();

        Assert.Contains("{\"OrderId\",Int64.Type}", m);
        Assert.Contains("{\"Amount\",type number}", m);
        Assert.Contains("{\"OrderDate\",type date}", m);
        Assert.Contains("{\"Active\",type logical}", m);
        Assert.Contains("{\"Customer\",type text}", m);
    }

    [Fact]
    public void Build_ColumnsArray_CarriesNameAndDataType()
    {
        var spec = Build(SampleSchema());
        var region = (spec["tables"] as JsonArray)!.First(t => (string?)t!["name"] == "Region")!;
        var cols = region["columns"] as JsonArray;
        Assert.Equal(2, cols!.Count);
        Assert.Equal("RegionKey", (string?)cols[0]!["name"]);
        Assert.Equal("int64", (string?)cols[0]!["dataType"]);
        Assert.Equal("Region Name", (string?)cols[1]!["name"]); // a space in a column name survives
    }

    [Fact]
    public void Build_EmitsNoMeasures_SoNoMeasureVsColumnCollisionIsPossible()
    {
        var spec = Build(SampleSchema());
        foreach (var t in (spec["tables"] as JsonArray)!)
        {
            var to = (JsonObject)t!;
            // the auto-shape path emits columns only; measures are the AI prompter's job. A flat table with
            // zero measures cannot have a measure whose name shadows one of its columns.
            Assert.False(to.ContainsKey("measures"));
        }
    }

    [Fact]
    public void Build_ColumnNameWithQuote_IsEscapedInM()
    {
        var schema = new SchemaDiscovery();
        var t = new TableSchema("Quirky");
        t.Columns.Add(new ColumnSchema("He said \"hi\"", "string"));
        schema.Tables.Add(t);

        var spec = Build(schema);
        string m = (spec["tables"] as JsonArray)![0]!["m"]!.GetValue<string>();
        // the embedded quote is doubled for the M double-quoted string literal
        Assert.Contains("{\"He said \"\"hi\"\"\",type text}", m);
    }

    [Fact]
    public void Build_TableWithNoColumns_FallsBackToPromOnly()
    {
        var schema = new SchemaDiscovery();
        schema.Tables.Add(new TableSchema("Bare")); // no columns
        var spec = Build(schema);
        string m = (spec["tables"] as JsonArray)![0]!["m"]!.GetValue<string>();
        Assert.DoesNotContain("TransformColumnTypes", m);
        Assert.EndsWith(" in Prom", m);
    }

    [Fact]
    public void Build_GeneratedSpec_ScaffoldsThroughHeadless()
    {
        // the synthesised spec must be consumable by the real scaffolder (the contract it targets).
        using var work = Fixtures.NewWorkDir();
        string json = FlatSpecBuilder.Build(SampleSchema());
        object result = SuperBiMcp.Headless.GenerateProject(json, work.File("proj"), "Model");

        var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(result))!;
        Assert.True((bool?)node["ok"]);
        Assert.Equal(2, (int?)node["tables"]);
        Assert.Equal(0, (int?)node["measures"]); // none, as built
        Assert.True(File.Exists(work.File(System.IO.Path.Combine("proj", "Model.pbip"))));
    }
}
