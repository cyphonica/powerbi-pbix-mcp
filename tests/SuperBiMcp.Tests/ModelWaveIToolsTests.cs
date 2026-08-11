using System;
using System.Linq;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Unit tests for the remaining Tabular-model (TOM) features: Q&amp;A synonyms, table-level detail rows,
/// calc-group precedence + item ordinal, partition mode + data coverage, annotations + extended
/// properties, TMDL import (dry-run diff) and model-health query generation. Each builds an in-memory
/// <c>new TOM.Model()</c>, calls the *Core mutation helper, and asserts on the resulting object tree -
/// no live Analysis Services server is needed. SaveChanges() is deliberately not exercised.
/// </summary>
public sealed class ModelWaveIToolsTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "OrderDate", DataType = TOM.DataType.DateTime, SourceColumn = "OrderDate" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" } });
        model.Tables.Add(sales);

        var calendar = new TOM.Table { Name = "Calendar" };
        calendar.Columns.Add(new TOM.DataColumn { Name = "Selector", DataType = TOM.DataType.String, SourceColumn = "Selector" });
        calendar.Partitions.Add(new TOM.Partition { Name = "Calendar", Source = new TOM.CalculatedPartitionSource { Expression = "{ \"Base\" }" } });
        model.Tables.Add(calendar);

        return model;
    }

    // ------------------------------------------------------------------ Q&A synonyms
    [Fact]
    public void SetSynonyms_writes_terms_into_linguistic_metadata_for_a_measure()
    {
        var model = NewModel();
        var (culture, entity, added) = ModelService.SetSynonymsCore(
            model, "measure", "Total Sales", new[] { "revenue", "turnover" }, null, "Sales");

        Assert.Equal("en-US", culture);
        Assert.Equal("Sales.Total Sales", entity);
        Assert.Equal(new[] { "revenue", "turnover" }, added);

        var lm = model.Cultures.Find("en-US")!.LinguisticMetadata!;
        Assert.Equal(TOM.ContentType.Json, lm.ContentType);
        var doc = JsonNode.Parse(lm.Content!)!.AsObject();
        var ent = doc["Entities"]!.AsObject()["Sales_Total Sales"]!.AsObject();
        Assert.Equal("Sales.Total Sales", ent["Definition"]!["Binding"]!["ConceptualEntity"]!.GetValue<string>());
        var terms = ent["Terms"]!.AsArray();
        var termNames = terms.Select(t => t!.AsObject().First().Key).ToArray();
        Assert.Contains("revenue", termNames);
        Assert.Contains("turnover", termNames);
    }

    [Fact]
    public void SetSynonyms_merges_into_existing_metadata_without_duplicating()
    {
        var model = NewModel();
        ModelService.SetSynonymsCore(model, "column", "Region", new[] { "area" }, "en-US", "Sales");
        ModelService.SetSynonymsCore(model, "column", "Region", new[] { "area", "territory" }, "en-US", "Sales");

        var lm = model.Cultures.Find("en-US")!.LinguisticMetadata!;
        var doc = JsonNode.Parse(lm.Content!)!.AsObject();
        var terms = doc["Entities"]!.AsObject()["Sales_Region"]!.AsObject()["Terms"]!.AsArray();
        var termNames = terms.Select(t => t!.AsObject().First().Key).ToArray();
        Assert.Equal(2, termNames.Length);   // "area" not duplicated
        Assert.Contains("territory", termNames);
    }

    [Fact]
    public void SetSynonyms_rejects_empty_synonyms()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetSynonymsCore(model, "measure", "Total Sales", Array.Empty<string>(), null, "Sales"));
    }

    // ------------------------------------------------------------------ table detail rows
    [Fact]
    public void SetTableDetailRows_sets_default_detail_rows_definition()
    {
        var model = NewModel();
        ModelService.SetTableDetailRowsCore(model, "Sales", "SELECTCOLUMNS('Sales', \"Region\", 'Sales'[Region])");

        var t = model.Tables.Find("Sales")!;
        Assert.NotNull(t.DefaultDetailRowsDefinition);
        Assert.Equal("SELECTCOLUMNS('Sales', \"Region\", 'Sales'[Region])", t.DefaultDetailRowsDefinition!.Expression);
    }

    [Fact]
    public void SetTableDetailRows_rejects_empty_expression()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetTableDetailRowsCore(model, "Sales", "  "));
    }

    // ------------------------------------------------------------------ calc group precedence / item ordinal
    [Fact]
    public void SetCalcGroupPrecedence_sets_precedence()
    {
        var model = NewModel();
        ModelService.AddCalculationGroupCore(model, "Calendar", null);
        var cg = ModelService.SetCalcGroupPrecedenceCore(model, "Calendar", 20);

        Assert.Equal(20, cg.Precedence);
        Assert.Same(cg, model.Tables.Find("Calendar")!.CalculationGroup);
    }

    [Fact]
    public void SetCalcGroupPrecedence_throws_when_table_is_not_a_calc_group()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetCalcGroupPrecedenceCore(model, "Sales", 1));
    }

    [Fact]
    public void SetCalcItemOrdinal_sets_item_ordinal()
    {
        var model = NewModel();
        ModelService.AddCalculationGroupCore(model, "Calendar", null);
        ModelService.AddCalculationItemCore(model, "Calendar", "YTD", "SELECTEDMEASURE()", null, null);
        var item = ModelService.SetCalcItemOrdinalCore(model, "Calendar", "YTD", 3);

        Assert.Equal(3, item.Ordinal);
    }

    [Fact]
    public void SetCalcItemOrdinal_throws_for_unknown_item()
    {
        var model = NewModel();
        ModelService.AddCalculationGroupCore(model, "Calendar", null);
        Assert.Throws<InvalidOperationException>(() => ModelService.SetCalcItemOrdinalCore(model, "Calendar", "Nope", 1));
    }

    // ------------------------------------------------------------------ partition mode / data coverage
    [Fact]
    public void SetPartitionMode_sets_mode_on_first_partition()
    {
        var model = NewModel();
        var part = ModelService.SetPartitionModeCore(model, "Sales", "DirectQuery", null);

        Assert.Equal(TOM.ModeType.DirectQuery, part.Mode);
        Assert.Equal("Sales", part.Name);
    }

    [Fact]
    public void SetPartitionMode_rejects_invalid_mode()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetPartitionModeCore(model, "Sales", "Bogus", null));
    }

    [Fact]
    public void SetDataCoverage_sets_coverage_definition()
    {
        var model = NewModel();
        var part = ModelService.SetDataCoverageCore(model, "Sales", "'Sales'[OrderDate] >= DATE(2024,1,1)", null);

        Assert.NotNull(part.DataCoverageDefinition);
        Assert.Equal("'Sales'[OrderDate] >= DATE(2024,1,1)", part.DataCoverageDefinition!.Expression);
    }

    [Fact]
    public void SetDataCoverage_rejects_empty_expression()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetDataCoverageCore(model, "Sales", " ", null));
    }

    // ------------------------------------------------------------------ annotations
    [Fact]
    public void SetAnnotation_sets_and_replaces_on_a_measure()
    {
        var model = NewModel();
        ModelService.SetAnnotationCore(model, "measure", "Total Sales", "Owner", "Finance", "Sales");
        var a = ModelService.SetAnnotationCore(model, "measure", "Total Sales", "Owner", "Sales Ops", "Sales");

        var me = model.Tables.Find("Sales")!.Measures.Find("Total Sales")!;
        Assert.Single(me.Annotations);   // replaced in place, not duplicated
        Assert.Equal("Sales Ops", me.Annotations.Find("Owner")!.Value);
        Assert.Same(me.Annotations.Find("Owner"), a);
    }

    [Fact]
    public void SetAnnotation_sets_on_model_and_table()
    {
        var model = NewModel();
        ModelService.SetAnnotationCore(model, "model", "Model", "Build", "123", null);
        ModelService.SetAnnotationCore(model, "table", "Sales", "Layer", "Fact", null);

        Assert.Equal("123", model.Annotations.Find("Build")!.Value);
        Assert.Equal("Fact", model.Tables.Find("Sales")!.Annotations.Find("Layer")!.Value);
    }

    [Fact]
    public void SetAnnotation_rejects_unknown_object_type()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetAnnotationCore(model, "bogus", "x", "n", "v", null));
    }

    // ------------------------------------------------------------------ extended properties
    [Fact]
    public void SetExtendedProperty_string_on_column()
    {
        var model = NewModel();
        var ep = ModelService.SetExtendedPropertyCore(model, "column", "Region", "Lineage", "src-1", "String", "Sales");

        var c = model.Tables.Find("Sales")!.Columns.Find("Region")!;
        Assert.IsType<TOM.StringExtendedProperty>(ep);
        Assert.Same(ep, c.ExtendedProperties.Find("Lineage"));
        Assert.Equal("src-1", ((TOM.StringExtendedProperty)c.ExtendedProperties.Find("Lineage")!).Value);
    }

    [Fact]
    public void SetExtendedProperty_json_replaces_existing()
    {
        var model = NewModel();
        ModelService.SetExtendedPropertyCore(model, "column", "Amount", "Meta", "{\"a\":1}", "String", "Sales");
        var ep = ModelService.SetExtendedPropertyCore(model, "column", "Amount", "Meta", "{\"a\":2}", "Json", "Sales");

        var c = model.Tables.Find("Sales")!.Columns.Find("Amount")!;
        Assert.Single(c.ExtendedProperties);   // replaced, not duplicated
        var json = Assert.IsType<TOM.JsonExtendedProperty>(c.ExtendedProperties.Find("Meta"));
        Assert.Equal("{\"a\":2}", json.Value);
        Assert.Same(ep, json);
    }

    // ------------------------------------------------------------------ TMDL import (dry-run diff)
    [Fact]
    public void DiffModels_reports_added_and_removed_tables_and_measures()
    {
        var live = NewModel();
        var incoming = NewModel();
        // incoming differs: drop Calendar, add a new table, add a measure to Sales
        incoming.Tables.Remove("Calendar");
        var extra = new TOM.Table { Name = "Budget" };
        incoming.Tables.Add(extra);
        incoming.Tables.Find("Sales")!.Measures.Add(new TOM.Measure { Name = "Avg Sale", Expression = "AVERAGE('Sales'[Amount])" });

        dynamic diff = ModelService.DiffModels(live, incoming);

        Assert.Contains("Budget", (string[])diff.tablesAdded);
        Assert.Contains("Calendar", (string[])diff.tablesRemoved);
        Assert.Contains("Sales[Avg Sale]", (string[])diff.measuresAdded);
    }

    // ------------------------------------------------------------------ model health (query generation)
    [Fact]
    public void ModelHealthQueries_generate_the_storage_and_schema_dmv_text()
    {
        var q = ModelService.ModelHealthQueries();

        Assert.Equal(
            "SELECT DIMENSION_NAME, ATTRIBUTE_NAME, DICTIONARY_SIZE, USED_SIZE, TABLE_ID FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS",
            q.ColumnStorage);
        Assert.Contains("$SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS", q.ColumnSegments);
        Assert.Contains("$SYSTEM.TMSCHEMA_COLUMNS", q.Columns);
        Assert.Contains("$SYSTEM.TMSCHEMA_MEASURES", q.Measures);
        Assert.Contains("$SYSTEM.TMSCHEMA_TABLES", q.Tables);
        Assert.Contains("$SYSTEM.DISCOVER_CALC_DEPENDENCY", q.CalcDependency);
    }
}
