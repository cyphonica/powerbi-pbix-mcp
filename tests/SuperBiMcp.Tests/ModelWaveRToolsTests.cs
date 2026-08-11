using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Unit tests for the Wave R model-internals features: sync-correctness annotations (lineage / source
/// lineage / changed-property / removed-children), the IsAvailableInMDX perf lever with its SortByColumn
/// guard, VertiPaq-stat stamping, calc-group selection expressions, data-access options, table privacy,
/// Q&amp;A synonym state/weight + LSDL phrasing, the auto-aggregation scaffold, Direct Lake cache/fallback
/// query text, dependency / unused analyzers, calendar-based time intelligence, the case-sensitive DAX
/// fixer, the compat-level gate map and report-level-measure extraction. Each builds an in-memory
/// <c>new TOM.Model()</c>, calls the *Core helper, and asserts on the resulting object tree (or, for the
/// DMV/INFO tools, on the generated query text). No live Analysis Services server is required. Every group
/// includes a fault-sensitive assertion (a wrong/guarded value must throw or be refused).
/// </summary>
public sealed class ModelWaveRToolsTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey" });
        sales.Columns.Add(new TOM.DataColumn { Name = "MonthName", DataType = TOM.DataType.String, SourceColumn = "MonthName" });
        sales.Columns.Add(new TOM.DataColumn { Name = "MonthNo", DataType = TOM.DataType.Int64, SourceColumn = "MonthNo", IsHidden = true });
        // MonthName sorts by MonthNo - so MonthNo is a SortByColumn target (must keep IsAvailableInMDX=true).
        sales.Columns.Find("MonthName")!.SortByColumn = sales.Columns.Find("MonthNo");
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "[Total Sales] * 0.3" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" } });
        model.Tables.Add(sales);

        var customer = new TOM.Table { Name = "Customer" };
        customer.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey", IsKey = true });
        customer.Columns.Add(new TOM.DataColumn { Name = "Country", DataType = TOM.DataType.String, SourceColumn = "Country" });
        customer.Partitions.Add(new TOM.Partition { Name = "Customer", Source = new TOM.MPartitionSource { Expression = "let S=1 in S" } });
        model.Tables.Add(customer);

        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = "Sales_Customer",
            FromColumn = sales.Columns.Find("CustomerKey"),
            ToColumn = customer.Columns.Find("CustomerKey"),
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
        });
        return model;
    }

    private static TOM.Table AddCalcGroup(TOM.Model model, string name = "Time")
    {
        var t = new TOM.Table { Name = name };
        t.Columns.Add(new TOM.DataColumn { Name = "Item", DataType = TOM.DataType.String, SourceColumn = "Item" });
        model.Tables.Add(t);
        ModelService.AddCalculationGroupCore(model, name, null);
        ModelService.AddCalculationItemCore(model, name, "Current", "SELECTEDMEASURE()", 0, null);
        return t;
    }

    // ================================================================ 1. sync-correctness annotations
    [Fact]
    public void SetLineageTag_sets_tag_on_a_column()
    {
        var model = NewModel();
        var obj = ModelService.SetLineageTagCore(model, "column", "Region", "abc-123", "Sales");
        Assert.Equal("abc-123", ((TOM.Column)obj).LineageTag);
    }

    [Fact]
    public void SetSourceLineageTag_sets_source_name_on_a_table()
    {
        var model = NewModel();
        var obj = ModelService.SetSourceLineageTagCore(model, "table", "Sales", "dbo.FactSales", null);
        Assert.Equal("dbo.FactSales", ((TOM.Table)obj).SourceLineageTag);
    }

    [Fact]
    public void SetLineageTag_rejects_empty_tag()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetLineageTagCore(model, "table", "Sales", "  ", null));
    }

    [Fact]
    public void SetLineageTag_rejects_bad_object_type()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetLineageTagCore(model, "perspective", "X", "t", null));
    }

    [Fact]
    public void DeclareChangedProperty_records_the_property_once()
    {
        var model = NewModel();
        ModelService.DeclareChangedPropertyCore(model, "column", "Region", "IsHidden", "Sales");
        ModelService.DeclareChangedPropertyCore(model, "column", "Region", "IsHidden", "Sales");   // idempotent
        var col = model.Tables.Find("Sales")!.Columns.Find("Region")!;
        Assert.Single(col.ChangedProperties, cp => cp.Property == "IsHidden");
    }

    [Fact]
    public void MarkRemovedChildren_writes_and_merges_the_annotation()
    {
        var model = NewModel();
        ModelService.MarkRemovedChildrenCore(model, "Sales", new[] { "OldCol1" });
        var all = ModelService.MarkRemovedChildrenCore(model, "Sales", new[] { "OldCol1", "OldCol2" });   // merge, no dup
        Assert.Equal(new[] { "OldCol1", "OldCol2" }, all);
        var ann = model.Tables.Find("Sales")!.Annotations.Find("PBI_RemovedChildren")!;
        var arr = JsonNode.Parse(ann.Value!)!.AsArray();
        Assert.Equal(2, arr.Count);
    }

    [Fact]
    public void MarkRemovedChildren_rejects_empty_list()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.MarkRemovedChildrenCore(model, "Sales", Array.Empty<string>()));
    }

    // ================================================================ 2. IsAvailableInMDX + SortByColumn guard
    [Fact]
    public void SetIsAvailableInMdx_sets_a_single_column()
    {
        var model = NewModel();
        var changed = ModelService.SetIsAvailableInMdxCore(model, "Sales", "Region", false, null, out var guarded);
        Assert.Contains("Sales[Region]", changed);
        Assert.False(model.Tables.Find("Sales")!.Columns.Find("Region")!.IsAvailableInMDX);
        Assert.Empty(guarded);
    }

    [Fact]
    public void SetIsAvailableInMdx_guards_sortbycolumn_targets()
    {
        var model = NewModel();
        // MonthNo is the SortByColumn target of MonthName - it must NOT be flipped to false.
        ModelService.SetIsAvailableInMdxCore(model, "Sales", "MonthNo", false, null, out var guarded);
        Assert.Contains("Sales[MonthNo]", guarded);
        Assert.True(model.Tables.Find("Sales")!.Columns.Find("MonthNo")!.IsAvailableInMDX);   // still true
    }

    [Fact]
    public void SetIsAvailableInMdx_bulk_hiddenAndKeys_skips_sort_targets()
    {
        var model = NewModel();
        ModelService.SetIsAvailableInMdxCore(model, null, null, false, "hiddenAndKeys", out var guarded);
        // CustomerKey on Customer is a key -> flipped; MonthNo is hidden BUT a sort target -> guarded.
        Assert.False(model.Tables.Find("Customer")!.Columns.Find("CustomerKey")!.IsAvailableInMDX);
        Assert.True(model.Tables.Find("Sales")!.Columns.Find("MonthNo")!.IsAvailableInMDX);
        Assert.Contains("Sales[MonthNo]", guarded);
        // a non-hidden, non-key column is untouched.
        Assert.True(model.Tables.Find("Sales")!.Columns.Find("Region")!.IsAvailableInMDX);
    }

    [Fact]
    public void SetIsAvailableInMdx_requires_a_scope()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetIsAvailableInMdxCore(model, null, null, false, null, out _));
    }

    // ================================================================ 3. VertiPaq stat stamping
    [Fact]
    public void StampVertipaqStats_writes_annotations_onto_matching_columns()
    {
        var model = NewModel();
        var rows = new List<(string, string, long, long, long)>
        {
            ("Sales", "Amount", 1000, 200, 50),
            ("Sales", "DoesNotExist", 9, 9, 9),   // skipped
        };
        int stamped = ModelService.StampVertipaqStatsCore(model, rows);
        Assert.Equal(1, stamped);
        var amount = model.Tables.Find("Sales")!.Columns.Find("Amount")!;
        Assert.Equal("1000", amount.Annotations.Find("Vertipaq_TotalSize")!.Value);
        Assert.Equal("50", amount.Annotations.Find("Vertipaq_Cardinality")!.Value);
    }

    // ================================================================ 4. calc-group selection expressions
    [Fact]
    public void SetCalcGroupSelectionExpressions_sets_both_with_format_strings()
    {
        var model = NewModel();
        AddCalcGroup(model);
        var cg = ModelService.SetCalcGroupSelectionExpressionsCore(model, "Time",
            "SELECTEDMEASURE()", "\"(multiple)\"", "\"#,0\"", "\"@\"");
        Assert.Equal("SELECTEDMEASURE()", cg.NoSelectionExpression.Expression);
        Assert.Equal("\"(multiple)\"", cg.MultipleOrEmptySelectionExpression.Expression);
        Assert.Equal("\"#,0\"", cg.NoSelectionExpression.FormatStringDefinition!.Expression);
    }

    [Fact]
    public void SetCalcGroupSelectionExpressions_throws_when_not_a_calc_group()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetCalcGroupSelectionExpressionsCore(model, "Sales", "1", null, null, null));
    }

    [Fact]
    public void SetSelectionExpressionBehavior_writes_annotation_and_rejects_bad_value()
    {
        var model = NewModel();
        var v = ModelService.SetSelectionExpressionBehaviorCore(model, "visual");
        Assert.Equal("VisualObjects", v);
        Assert.Equal("VisualObjects", model.Annotations.Find("PBI_SelectionExpressionBehavior")!.Value);
        Assert.Throws<InvalidOperationException>(() => ModelService.SetSelectionExpressionBehaviorCore(model, "popup"));
    }

    // ================================================================ 5. data-access options
    [Fact]
    public void SetDataAccessOptions_sets_subset_and_leaves_rest_unchanged()
    {
        var model = NewModel();
        var dao = ModelService.SetDataAccessOptionsCore(model, fastCombine: true, legacyRedirects: null, returnErrorValuesAsNull: true);
        Assert.True(dao.FastCombine);
        Assert.True(dao.ReturnErrorValuesAsNull);
        // second call touches only one flag; the others survive
        var captured = dao.ReturnErrorValuesAsNull;
        ModelService.SetDataAccessOptionsCore(model, fastCombine: false, null, null);
        Assert.False(model.DataAccessOptions!.FastCombine);
        Assert.Equal(captured, model.DataAccessOptions!.ReturnErrorValuesAsNull);   // unchanged
    }

    // ================================================================ 6. table privacy
    [Fact]
    public void SetTablePrivate_toggles_isprivate()
    {
        var model = NewModel();
        Assert.True(ModelService.SetTablePrivateCore(model, "Customer", true).IsPrivate);
        Assert.False(ModelService.SetTablePrivateCore(model, "Customer", false).IsPrivate);
    }

    [Fact]
    public void SetTablePrivate_throws_for_unknown_table()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.SetTablePrivateCore(model, "Nope", true));
    }

    // ================================================================ 7. Q&A linguistic depth
    [Fact]
    public void SetSynonymState_writes_state_and_weight()
    {
        var model = NewModel();
        ModelService.SetSynonymStateCore(model, "measure", "Total Sales", new[] { "revenue" }, "Authored", 0.9, "en-US", "Sales");
        var lm = model.Cultures.Find("en-US")!.LinguisticMetadata!;
        var doc = JsonNode.Parse(lm.Content!)!.AsObject();
        var ent = doc["Entities"]!["Sales_Total Sales"]!.AsObject();
        var term = ent["Terms"]!.AsArray().Single()!.AsObject();
        var body = term["revenue"]!.AsObject();
        Assert.Equal("Authored", (string?)body["State"]);
        Assert.Equal(0.9, (double)body["Weight"]!);
    }

    [Fact]
    public void SetSynonymState_deleted_state_suppresses_a_term()
    {
        var model = NewModel();
        ModelService.SetSynonymStateCore(model, "column", "Region", new[] { "area" }, "Deleted", null, null, "Sales");
        var lm = model.Cultures.Find("en-US")!.LinguisticMetadata!;
        var doc = JsonNode.Parse(lm.Content!)!.AsObject();
        var body = doc["Entities"]!["Sales_Region"]!["Terms"]!.AsArray().Single()!["area"]!.AsObject();
        Assert.Equal("Deleted", (string?)body["State"]);
    }

    [Fact]
    public void SetSynonymState_rejects_bad_state_and_bad_weight()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetSynonymStateCore(model, "measure", "Total Sales", new[] { "x" }, "Maybe", null, null, "Sales"));
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetSynonymStateCore(model, "measure", "Total Sales", new[] { "x" }, "Authored", 2.0, null, "Sales"));
    }

    [Fact]
    public void SetQnaPhrasing_writes_lsdl_phrasing_under_relationships()
    {
        var model = NewModel();
        var (culture, key) = ModelService.SetQnaPhrasingCore(model, "Adjective", "happy_customers",
            "{\"Subject\":\"Customer\",\"Adjectives\":[\"happy\"]}", "en-US");
        Assert.Equal("en-US", culture);
        Assert.Equal("happy_customers", key);
        var lm = model.Cultures.Find("en-US")!.LinguisticMetadata!;
        var doc = JsonNode.Parse(lm.Content!)!.AsObject();
        Assert.NotNull(doc["Relationships"]!["happy_customers"]!["Phrasing"]!["Adjective"]);
    }

    [Fact]
    public void SetQnaPhrasing_rejects_bad_type_and_bad_json()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetQnaPhrasingCore(model, "Sentence", "x", "{}", null));
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.SetQnaPhrasingCore(model, "Verb", "x", "[1,2,3]", null));   // not an object
    }

    // ================================================================ 8. auto-aggregation scaffold
    [Fact]
    public void AddAutoAggregations_builds_hidden_table_measures_and_routes()
    {
        var model = NewModel();
        var (aggTable, aggMeasures, routed) = ModelService.AddAutoAggregationsCore(model, "Sales",
            new[] { "Sales[Region]" },
            new List<(string, string)> { ("Total Sales", "Total Sales") });

        Assert.Equal("Sales_Agg", aggTable);
        var agg = model.Tables.Find("Sales_Agg")!;
        Assert.True(agg.IsHidden);
        Assert.IsType<TOM.CalculatedPartitionSource>(agg.Partitions[0].Source);
        Assert.Contains("Sales_Agg[Total Sales _Agg]", aggMeasures);
        Assert.Contains("Sales[Total Sales]", routed);
        // the base measure was rewritten to route through the agg table.
        Assert.Contains("Sales_Agg", model.Tables.Find("Sales")!.Measures.Find("Total Sales")!.Expression);
    }

    [Fact]
    public void AddAutoAggregations_rejects_empty_groupby_and_duplicate_table()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddAutoAggregationsCore(model, "Sales", Array.Empty<string>(),
                new List<(string, string)> { ("Total Sales", "Total Sales") }));

        ModelService.AddAutoAggregationsCore(model, "Sales", new[] { "Sales[Region]" },
            new List<(string, string)> { ("Total Sales", "Total Sales") });
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddAutoAggregationsCore(model, "Sales", new[] { "Sales[Region]" },
                new List<(string, string)> { ("Total Sales", "Total Sales") }));   // {Table}_Agg already exists
    }

    // ================================================================ 9. Direct Lake cache / fallback query text
    [Fact]
    public void WarmDirectLakeCacheQuery_selects_named_columns()
    {
        var model = NewModel();
        var q = ModelService.WarmDirectLakeCacheQuery(model, "Sales", new[] { "Region", "Amount" });
        Assert.StartsWith("EVALUATE TOPN ( 1, SELECTCOLUMNS ( 'Sales'", q);
        Assert.Contains("\"Region\", 'Sales'[Region]", q);
        Assert.Contains("\"Amount\", 'Sales'[Amount]", q);
    }

    [Fact]
    public void WarmDirectLakeCacheQuery_defaults_to_all_columns_and_validates_names()
    {
        var model = NewModel();
        var q = ModelService.WarmDirectLakeCacheQuery(model, "Sales", null);
        Assert.Contains("'Sales'[Region]", q);
        Assert.Contains("'Sales'[MonthNo]", q);
        Assert.Throws<InvalidOperationException>(() => ModelService.WarmDirectLakeCacheQuery(model, "Sales", new[] { "Nope" }));
    }

    [Fact]
    public void DirectLakeFallbackQuery_targets_the_storage_dmv()
    {
        Assert.Contains("$SYSTEM.TMSCHEMA_DELTA_TABLE_METADATA_STORAGES", ModelService.DirectLakeFallbackQuery());
    }

    // ================================================================ 10. dependency / unused analyzers
    [Fact]
    public void MeasureDependants_finds_measures_referencing_a_measure()
    {
        var model = NewModel();
        var dep = ModelService.MeasureDependants(model, "Total Sales");
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(dep))!.AsObject();
        // Margin references [Total Sales]; Total Sales does not reference itself.
        Assert.Equal(1, (int)json["count"]!);
        Assert.Contains("Sales[Margin]", json["directDependants"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void MeasureDependants_finds_column_consumers()
    {
        var model = NewModel();
        var dep = ModelService.MeasureDependants(model, "Sales[Amount]");
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(dep))!.AsObject();
        // Total Sales = SUM('Sales'[Amount]) references the column.
        Assert.Contains("Sales[Total Sales]", json["directDependants"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void FindUnusedCore_flags_truly_unused_and_spares_referenced()
    {
        var model = NewModel();
        // Region is referenced by no relationship/sort/DAX -> unused. CustomerKey IS in a relationship -> used.
        // MonthNo is a sort target -> used. Amount is in Total Sales DAX -> used.
        var (cols, measures) = ModelService.FindUnusedCore(model);
        Assert.Contains("Sales[Region]", cols);
        Assert.DoesNotContain("Sales[CustomerKey]", cols);
        Assert.DoesNotContain("Sales[MonthNo]", cols);
        Assert.DoesNotContain("Sales[Amount]", cols);
        // Margin is referenced by no other measure -> unused; Total Sales is referenced by Margin -> used.
        Assert.Contains("Sales[Margin]", measures);
        Assert.DoesNotContain("Sales[Total Sales]", measures);
    }

    [Fact]
    public void AnalyzeDependencies_query_text_is_calcdependency()
    {
        Assert.Equal("EVALUATE INFO.CALCDEPENDENCY()", DaxGenerators.InfoViewQuery("CALCDEPENDENCY"));
    }

    // ================================================================ 11. calendar-based time intelligence
    [Fact]
    public void AddCalendarBasedTimeIntelligence_stamps_calendar_annotation()
    {
        var model = NewModel();
        // give Sales some period columns to reference.
        model.Tables.Find("Sales")!.Columns.Add(new TOM.DataColumn { Name = "FiscalPeriod", DataType = TOM.DataType.String, SourceColumn = "FiscalPeriod" });
        var primary = ModelService.AddCalendarBasedTimeIntelligenceCore(model, "Sales", "MonthName", new[] { "FiscalPeriod" });
        Assert.Equal("MonthName", primary);
        var ann = model.Tables.Find("Sales")!.Annotations.Find("PBI_Calendar")!;
        var doc = JsonNode.Parse(ann.Value!)!.AsObject();
        Assert.Equal("MonthName", (string?)doc["columnName"]);
        Assert.Contains("FiscalPeriod", doc["calendarColumnGroups"]![0]!["columnNames"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void AddCalendarBasedTimeIntelligence_rejects_missing_associated_and_unknown_column()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddCalendarBasedTimeIntelligenceCore(model, "Sales", "MonthName", Array.Empty<string>()));
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddCalendarBasedTimeIntelligenceCore(model, "Sales", "MonthName", new[] { "NoSuchColumn" }));
    }

    // ================================================================ 12. case-sensitive DAX fixer
    [Fact]
    public void RewriteRefsToCanonicalCase_fixes_table_and_column_casing()
    {
        var tableCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Sales"] = "Sales" };
        var colCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Amount"] = "Amount" };
        var measureCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Total Sales"] = "Total Sales" };

        var fixedQuoted = ModelService.RewriteRefsToCanonicalCase("SUM ( 'sales'[amount] )", tableCanon, colCanon, measureCanon);
        Assert.Equal("SUM ( 'Sales'[Amount] )", fixedQuoted);

        var fixedBare = ModelService.RewriteRefsToCanonicalCase("SUM ( SALES[AMOUNT] )", tableCanon, colCanon, measureCanon);
        Assert.Equal("SUM ( Sales[Amount] )", fixedBare);

        var fixedMeasure = ModelService.RewriteRefsToCanonicalCase("[total sales] * 2", tableCanon, colCanon, measureCanon);
        Assert.Equal("[Total Sales] * 2", fixedMeasure);
    }

    [Fact]
    public void FixCaseSensitiveDaxCore_rewrites_only_changed_measures()
    {
        var model = NewModel();
        model.Tables.Find("Sales")!.Measures.Find("Total Sales")!.Expression = "SUM('sales'[amount])";
        var changed = ModelService.FixCaseSensitiveDaxCore(model);
        Assert.Contains("Sales[Total Sales]", changed);
        Assert.Equal("SUM('Sales'[Amount])", model.Tables.Find("Sales")!.Measures.Find("Total Sales")!.Expression);
        // a second pass changes nothing (already canonical).
        Assert.Empty(ModelService.FixCaseSensitiveDaxCore(model));
    }

    // ================================================================ 13. report-level-measure extraction
    [Fact]
    public void ParseReportLevelMeasures_reads_modelextensions()
    {
        var root = BuildLayoutWithReportMeasure("Sales", "Report Total", "SUM('Sales'[Amount])", "#,0", "Folder");
        var specs = ModelService.ParseReportLevelMeasures(root);
        var spec = Assert.Single(specs);
        Assert.Equal("Sales", spec.table);
        Assert.Equal("Report Total", spec.name);
        Assert.Equal("SUM('Sales'[Amount])", spec.expression);
        Assert.Equal("#,0", spec.format);
        Assert.Equal("Folder", spec.folder);
    }

    [Fact]
    public void ParseReportLevelMeasures_returns_empty_when_no_extensions()
    {
        var root = new JsonObject { ["config"] = "{}" };
        Assert.Empty(ModelService.ParseReportLevelMeasures(root));
    }

    [Fact]
    public void PromoteReportMeasuresCore_adds_to_the_host_table_and_skips_existing()
    {
        var model = NewModel();
        var specs = new List<(string, string, string, string?, string?)>
        {
            ("Sales", "Report Total", "SUM('Sales'[Amount])", "#,0", null),
            ("Sales", "Total Sales", "1", null, null),     // already exists -> skipped
            ("NoTable", "X", "1", null, null),             // table missing -> skipped
        };
        var promoted = ModelService.PromoteReportMeasuresCore(model, specs);
        Assert.Equal(new[] { "Sales[Report Total]" }, promoted);
        var me = model.Tables.Find("Sales")!.Measures.Find("Report Total")!;
        Assert.Equal("SUM('Sales'[Amount])", me.Expression);
        Assert.Equal("#,0", me.FormatString);
    }

    [Fact]
    public void ReadReportLevelMeasures_round_trips_a_pbix_layout()
    {
        var root = BuildLayoutWithReportMeasure("Sales", "Report Total", "SUM('Sales'[Amount])", null, null);
        string pbix = WriteTempPbix(root);
        try
        {
            var specs = ModelService.ReadReportLevelMeasures(pbix);
            var spec = Assert.Single(specs);
            Assert.Equal("Sales", spec.table);
            Assert.Equal("Report Total", spec.name);
        }
        finally { File.Delete(pbix); }
    }

    // ================================================================ compat-level gate map
    [Theory]
    [InlineData("lineagetag", 1540)]
    [InlineData("sourcelineagetag", 1550)]
    [InlineData("selectionexpressions", 1605)]
    [InlineData("dataaccessoptions", 1400)]
    [InlineData("directlakebehavior", 1604)]
    [InlineData("daxudfs", 1702)]
    public void CompatLevelFor_maps_features_to_levels(string feature, int expected)
        => Assert.Equal(expected, ModelService.CompatLevelFor(feature));

    [Fact]
    public void CompatLevelFor_rejects_unknown_feature()
        => Assert.Throws<InvalidOperationException>(() => ModelService.CompatLevelFor("teleport"));

    // ---- helpers ----------------------------------------------------------------------------------
    private static JsonObject BuildLayoutWithReportMeasure(string table, string name, string expr, string? format, string? folder)
    {
        var measure = new JsonObject { ["name"] = name, ["expression"] = expr };
        if (format != null) measure["formatString"] = format;
        if (folder != null) measure["displayFolder"] = folder;
        var cfg = new JsonObject
        {
            ["modelExtensions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "extension",
                    ["entities"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["name"] = table,
                            ["extends"] = table,
                            ["measures"] = new JsonArray { measure },
                        },
                    },
                },
            },
        };
        // config is a JSON STRING in the legacy Report/Layout.
        return new JsonObject { ["config"] = cfg.ToJsonString() };
    }

    private static string WriteTempPbix(JsonObject layoutRoot)
    {
        string path = Path.Combine(Path.GetTempPath(), $"waveR-{Guid.NewGuid():N}.pbix");
        using var fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("Report/Layout");
        using var es = entry.Open();
        // Report/Layout is UTF-16-LE with a BOM in real pbix files.
        byte[] body = new UnicodeEncoding(false, false).GetBytes(layoutRoot.ToJsonString());
        es.Write(new byte[] { 0xFF, 0xFE }, 0, 2);
        es.Write(body, 0, body.Length);
        return path;
    }
}
