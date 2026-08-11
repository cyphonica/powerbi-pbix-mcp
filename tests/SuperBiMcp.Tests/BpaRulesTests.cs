using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Fault-sensitive unit tests for the Wave U Best Practice Analyzer (BpaRules): the rule catalogue, the
/// runner with its category/severity/ruleId/scope filters, every autofix (it must mutate exactly the right
/// TOM property, and dryRun must NOT mutate), and the Tabular Editor ruleset importer. Each test builds an
/// in-memory <c>new TOM.Model()</c> - no live Analysis Services server. A deliberately-bad model triggers a
/// representative rule from EVERY category; a clean model triggers none of those. Fault-sensitivity:
/// removing a rule's check or autofix breaks its dedicated test.
/// </summary>
public sealed class BpaRulesTests
{
    // ---------------------------------------------------------------- model builders

    /// <summary>A clean star schema that should pass the representative rule from every category.</summary>
    private static TOM.Model CleanModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US", DiscourageImplicitMeasures = true };

        // --- Date dimension (marked) ---
        var date = new TOM.Table { Name = "Calendar", Description = "Date dimension." };
        var dcol = new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, SourceColumn = "Date",
            DataCategory = "Time", IsKey = true, FormatString = "yyyy-mm-dd" };
        date.Columns.Add(dcol);
        date.Partitions.Add(new TOM.Partition { Name = "Calendar", Source = new TOM.MPartitionSource { Expression = "let S=1 in S" } });
        model.Tables.Add(date);

        // --- Customer dimension ---
        var customer = new TOM.Table { Name = "Customer", Description = "Customer dimension." };
        customer.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64,
            SourceColumn = "CustomerKey", IsKey = true, IsHidden = true });
        customer.Columns.Add(new TOM.DataColumn { Name = "Customer Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
        customer.Partitions.Add(new TOM.Partition { Name = "Customer", Source = new TOM.MPartitionSource { Expression = "let S=1 in S" } });
        model.Tables.Add(customer);

        // --- Sales fact ---
        var sales = new TOM.Table { Name = "Sales", Description = "Sales fact." };
        sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64,
            SourceColumn = "CustomerKey", IsHidden = true, SummarizeBy = TOM.AggregateFunction.None,
            IsAvailableInMDX = false });
        sales.Columns.Add(new TOM.DataColumn { Name = "DateKey", DataType = TOM.DataType.DateTime,
            SourceColumn = "DateKey", IsHidden = true, SummarizeBy = TOM.AggregateFunction.None, IsAvailableInMDX = false });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Decimal, SourceColumn = "Amount",
            IsHidden = true, SummarizeBy = TOM.AggregateFunction.None, IsAvailableInMDX = false, FormatString = "#,0.00" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales",
            Expression = "SUM ( Sales[Amount] )", FormatString = "#,0.00",
            Description = "Sum of sales amount.", DisplayFolder = "Core" });
        sales.Measures.Add(new TOM.Measure { Name = "Margin Pct",
            Expression = "DIVIDE ( [Total Sales] * 0.3, [Total Sales] )", FormatString = "0.0%",
            Description = "Margin percentage.", DisplayFolder = "Core" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let S=1 in S" } });
        model.Tables.Add(sales);

        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = "Sales to Customer",
            FromColumn = sales.Columns.Find("CustomerKey"),
            ToColumn = customer.Columns.Find("CustomerKey"),
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
            CrossFilteringBehavior = TOM.CrossFilteringBehavior.OneDirection,
        });
        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = "Sales to Calendar",
            FromColumn = sales.Columns.Find("DateKey"),
            ToColumn = date.Columns.Find("Date"),
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
            CrossFilteringBehavior = TOM.CrossFilteringBehavior.OneDirection,
        });
        return model;
    }

    /// <summary>A model that deliberately violates at least one rule in every category.</summary>
    private static TOM.Model BadModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US", DiscourageImplicitMeasures = false };

        // an auto-date table (AUTO_DATE_TIME_ON, Performance)
        model.Tables.Add(new TOM.Table { Name = "LocalDateTable_abc" });

        var customer = new TOM.Table { Name = "Customer" };   // no description (Metadata)
        customer.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.String,
            SourceColumn = "CustomerKey" });   // NOT marked IsKey (MARK_PRIMARY_KEYS), string FK (datatype rules)
        customer.Columns.Add(new TOM.DataColumn { Name = "Country", DataType = TOM.DataType.String, SourceColumn = "Country" });   // geo (no DataCategory)
        customer.Columns.Add(new TOM.DataColumn { Name = "Customer ", DataType = TOM.DataType.String, SourceColumn = "Trail" });   // trailing space (Naming)
        customer.Partitions.Add(new TOM.Partition { Name = "Customer", Source = new TOM.MPartitionSource { Expression = "let S=1 in S" } });
        model.Tables.Add(customer);

        var sales = new TOM.Table { Name = "Sales" };
        // visible numeric FK on the many side (HIDE_FOREIGN_KEYS), Double currency col (AVOID_FLOATING_POINT)
        sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.String,
            SourceColumn = "CustomerKey", IsHidden = false });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double,
            SourceColumn = "Amount", IsHidden = false, SummarizeBy = TOM.AggregateFunction.Sum });   // float currency + implicit measure + no format
        // a hidden non-key, non-sort-by column with IsAvailableInMDX still true (ISAVAILABLEINMDX rule)
        sales.Columns.Add(new TOM.DataColumn { Name = "Internal Note", DataType = TOM.DataType.String,
            SourceColumn = "Note", IsHidden = true, IsAvailableInMDX = true });
        // measure: '/' division (DAX), no format string (Formatting), no description/folder (Metadata/Maintenance)
        sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "[Amount Sum] / [Count]" });
        // measure with EVALUATEANDLOG (ErrorPrevention) + INTERSECT (DAX)
        sales.Measures.Add(new TOM.Measure { Name = "Debug Total",
            Expression = "EVALUATEANDLOG ( SUMX ( INTERSECT ( VALUES ( Sales[CustomerKey] ), VALUES ( Customer[CustomerKey] ) ), 1 ), \"dbg\" )" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let S=1 in S" } });
        model.Tables.Add(sales);

        // FK relationship on the many side -> the Sales[CustomerKey] is a FK (visible => HIDE_FOREIGN_KEYS)
        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = "rel1",
            FromColumn = sales.Columns.Find("CustomerKey"),
            ToColumn = customer.Columns.Find("CustomerKey"),
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
            CrossFilteringBehavior = TOM.CrossFilteringBehavior.BothDirections,   // BIDIRECTIONAL (Layout)
        });
        return model;
    }

    private static bool Fires(TOM.Model m, string ruleId) =>
        BpaRules.Run(m, ruleIds: new[] { ruleId }).Any();

    // ================================================================ catalogue shape

    [Fact]
    public void Catalogue_has_about_ninety_rules_across_all_eight_categories()
    {
        var cats = BpaRules.All.Select(r => r.Category).Distinct().ToList();
        Assert.True(BpaRules.All.Count >= 85, $"expected ~90 rules, got {BpaRules.All.Count}");
        foreach (var c in new[] { BpaRules.CatPerformance, BpaRules.CatDax, BpaRules.CatError,
            BpaRules.CatMaintenance, BpaRules.CatNaming, BpaRules.CatFormatting, BpaRules.CatMetadata, BpaRules.CatLayout })
            Assert.Contains(c, cats);
    }

    [Fact]
    public void Catalogue_rule_ids_are_unique_and_upper_snake()
    {
        var ids = BpaRules.All.Select(r => r.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var id in ids)
            Assert.Matches("^[A-Z0-9_]+$", id);
    }

    [Fact]
    public void Catalogue_has_both_fixable_and_flag_only_rules()
    {
        Assert.True(BpaRules.All.Count(r => r.Fixable) >= 12, "expected a healthy set of autofixes");
        Assert.True(BpaRules.All.Any(r => !r.Fixable), "expected flag-only rules too");
    }

    // ================================================================ a representative rule per category fires on bad, not on clean

    [Theory]
    [InlineData("AUTO_DATE_TIME_ON")]                          // Performance
    [InlineData("DAX_USE_DIVIDE")]                             // DAXExpressions
    [InlineData("EVALUATEANDLOG_IN_PRODUCTION")]              // ErrorPrevention
    [InlineData("MISSING_DESCRIPTION")]                       // Metadata
    [InlineData("NO_TRAILING_SPACES_IN_NAMES")]              // NamingConventions
    [InlineData("NO_FORMAT_STRING_ON_MEASURES")]             // Formatting
    [InlineData("OBJECTS_WITH_NO_DISPLAY_FOLDER")]           // Maintenance
    [InlineData("BIDIRECTIONAL_RELATIONSHIP")]               // RelationshipsLayout
    public void Representative_rule_fires_on_bad_model(string ruleId)
    {
        Assert.True(Fires(BadModel(), ruleId), $"{ruleId} should fire on the bad model");
    }

    [Theory]
    [InlineData("AUTO_DATE_TIME_ON")]
    [InlineData("DAX_USE_DIVIDE")]
    [InlineData("EVALUATEANDLOG_IN_PRODUCTION")]
    [InlineData("MISSING_DESCRIPTION")]
    [InlineData("NO_TRAILING_SPACES_IN_NAMES")]
    [InlineData("NO_FORMAT_STRING_ON_MEASURES")]
    [InlineData("OBJECTS_WITH_NO_DISPLAY_FOLDER")]
    [InlineData("BIDIRECTIONAL_RELATIONSHIP")]
    public void Representative_rule_does_not_fire_on_clean_model(string ruleId)
    {
        Assert.False(Fires(CleanModel(), ruleId), $"{ruleId} should NOT fire on the clean model");
    }

    // ================================================================ autofixes - exact property mutation

    [Fact]
    public void Fix_HIDE_FOREIGN_KEYS_sets_IsHidden_true()
    {
        var m = BadModel();
        var fk = m.Tables.Find("Sales")!.Columns.Find("CustomerKey")!;
        Assert.False(fk.IsHidden);
        var (rule, outcomes) = BpaRules.Fix(m, "HIDE_FOREIGN_KEYS", null, dryRun: false);
        Assert.True(fk.IsHidden);
        Assert.NotEmpty(outcomes);
        Assert.Equal("HIDE_FOREIGN_KEYS", rule.Id);
    }

    [Fact]
    public void Fix_HIDE_FOREIGN_KEYS_dryRun_does_NOT_mutate()
    {
        var m = BadModel();
        var fk = m.Tables.Find("Sales")!.Columns.Find("CustomerKey")!;
        Assert.False(fk.IsHidden);
        var (_, outcomes) = BpaRules.Fix(m, "HIDE_FOREIGN_KEYS", null, dryRun: true);
        Assert.False(fk.IsHidden);                        // dryRun must NOT mutate
        Assert.NotEmpty(outcomes);                        // but it must report what WOULD change
        Assert.Contains("WOULD", outcomes[0].Change);
    }

    [Fact]
    public void Fix_ISAVAILABLEINMDX_sets_false_on_safe_hidden_column()
    {
        var m = BadModel();
        var col = m.Tables.Find("Sales")!.Columns.Find("Internal Note")!;
        Assert.True(col.IsAvailableInMDX);
        BpaRules.Fix(m, "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS", null, dryRun: false);
        Assert.False(col.IsAvailableInMDX);
    }

    [Fact]
    public void ISAVAILABLEINMDX_rule_guards_sort_by_targets()
    {
        // a hidden column that is a SortByColumn target must NOT be flagged (flipping breaks the sort).
        var m = CleanModel();
        var sales = m.Tables.Find("Sales")!;
        var sortDisplay = new TOM.DataColumn { Name = "Month Name", DataType = TOM.DataType.String, SourceColumn = "MN" };
        var sortBy = new TOM.DataColumn { Name = "Month No", DataType = TOM.DataType.Int64, SourceColumn = "MNo",
            IsHidden = true, IsAvailableInMDX = true };
        sales.Columns.Add(sortDisplay);
        sales.Columns.Add(sortBy);
        sortDisplay.SortByColumn = sortBy;
        var hits = BpaRules.Run(m, ruleIds: new[] { "ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS" });
        Assert.DoesNotContain(hits, f => f.ObjectName == "Sales[Month No]");
    }

    [Fact]
    public void Fix_EVALUATEANDLOG_strips_the_wrapper()
    {
        var m = BadModel();
        var me = m.Tables.Find("Sales")!.Measures.Find("Debug Total")!;
        Assert.Contains("EVALUATEANDLOG", me.Expression, StringComparison.OrdinalIgnoreCase);
        BpaRules.Fix(m, "EVALUATEANDLOG_IN_PRODUCTION", null, dryRun: false);
        Assert.DoesNotContain("EVALUATEANDLOG", me.Expression, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("SUMX", me.Expression, StringComparison.OrdinalIgnoreCase);   // inner expression preserved
    }

    [Fact]
    public void StripEvaluateAndLog_unwraps_nested_and_keeps_first_arg()
    {
        var stripped = BpaRules.StripEvaluateAndLog("EVALUATEANDLOG ( SUM ( T[X] ), \"label\", 5 )");
        Assert.DoesNotContain("EVALUATEANDLOG", stripped, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("label", stripped);
        Assert.Contains("SUM", stripped);
    }

    [Fact]
    public void Fix_MARK_PRIMARY_KEYS_sets_IsKey_true()
    {
        var m = BadModel();
        var key = m.Tables.Find("Customer")!.Columns.Find("CustomerKey")!;
        Assert.False(key.IsKey);
        BpaRules.Fix(m, "MARK_PRIMARY_KEYS", null, dryRun: false);
        Assert.True(key.IsKey);
    }

    [Fact]
    public void Fix_BIDIRECTIONAL_RELATIONSHIP_sets_OneDirection()
    {
        var m = BadModel();
        var rel = (TOM.SingleColumnRelationship)m.Relationships.Find("rel1")!;
        Assert.Equal(TOM.CrossFilteringBehavior.BothDirections, rel.CrossFilteringBehavior);
        BpaRules.Fix(m, "BIDIRECTIONAL_RELATIONSHIP", null, dryRun: false);
        Assert.Equal(TOM.CrossFilteringBehavior.OneDirection, rel.CrossFilteringBehavior);
    }

    [Fact]
    public void Fix_NO_FORMAT_STRING_ON_MEASURES_sets_a_format_string()
    {
        var m = BadModel();
        var me = m.Tables.Find("Sales")!.Measures.Find("Margin")!;
        Assert.True(string.IsNullOrWhiteSpace(me.FormatString));
        BpaRules.Fix(m, "NO_FORMAT_STRING_ON_MEASURES", null, dryRun: false);
        Assert.False(string.IsNullOrWhiteSpace(me.FormatString));
    }

    [Fact]
    public void Fix_NO_TRAILING_SPACES_trims_the_name()
    {
        var m = BadModel();
        var col = m.Tables.Find("Customer")!.Columns.Find("Customer ")!;
        BpaRules.Fix(m, "NO_TRAILING_SPACES_IN_NAMES", null, dryRun: false);
        Assert.Null(m.Tables.Find("Customer")!.Columns.Find("Customer "));   // old name gone
        Assert.NotNull(m.Tables.Find("Customer")!.Columns.Find("Customer")); // trimmed name present
    }

    [Fact]
    public void Fix_SET_DATA_CATEGORY_FOR_GEO_sets_country()
    {
        var m = BadModel();
        var col = m.Tables.Find("Customer")!.Columns.Find("Country")!;
        Assert.True(string.IsNullOrEmpty(col.DataCategory));
        BpaRules.Fix(m, "SET_DATA_CATEGORY_FOR_GEO", null, dryRun: false);
        Assert.Equal("Country", col.DataCategory);
    }

    [Fact]
    public void Fix_DISCOURAGE_IMPLICIT_MEASURES_sets_model_flag()
    {
        var m = BadModel();
        Assert.False(m.DiscourageImplicitMeasures);
        BpaRules.Fix(m, "DISCOURAGE_IMPLICIT_MEASURES", null, dryRun: false);
        Assert.True(m.DiscourageImplicitMeasures);
    }

    [Fact]
    public void Fix_targets_only_the_named_object()
    {
        var m = BadModel();
        var sales = m.Tables.Find("Sales")!;
        var fk = sales.Columns.Find("CustomerKey")!;
        // also make a second visible FK candidate so "fix all" would hit more than one.
        var (_, outcomes) = BpaRules.Fix(m, "HIDE_FOREIGN_KEYS", "Sales[CustomerKey]", dryRun: false);
        Assert.Single(outcomes);
        Assert.Equal("Sales[CustomerKey]", outcomes[0].ObjectName);
        Assert.True(fk.IsHidden);
    }

    // ================================================================ flag-only rules refuse a fix

    [Fact]
    public void Fix_refuses_a_flag_only_rule()
    {
        var m = BadModel();
        var ex = Assert.Throws<InvalidOperationException>(() => BpaRules.Fix(m, "DAX_USE_DIVIDE", null, dryRun: false));
        Assert.Contains("no safe automatic fix", ex.Message);
    }

    [Fact]
    public void DAX_USE_DIVIDE_is_flag_only()
    {
        Assert.False(BpaRules.ById("DAX_USE_DIVIDE")!.Fixable);
        Assert.False(BpaRules.ById("AVOID_FLOATING_POINT_DATA_TYPES")!.Fixable);
        Assert.False(BpaRules.ById("UNUSED_MEASURES")!.Fixable);
    }

    [Fact]
    public void Fix_throws_for_unknown_rule()
    {
        var m = BadModel();
        Assert.Throws<InvalidOperationException>(() => BpaRules.Fix(m, "NO_SUCH_RULE", null, dryRun: true));
    }

    // ================================================================ DAX detectors (precision)

    [Fact]
    public void DAX_USE_DIVIDE_fires_on_slash_not_on_DIVIDE()
    {
        var m = CleanModel();
        var sales = m.Tables.Find("Sales")!;
        sales.Measures.Add(new TOM.Measure { Name = "Bad Ratio", Expression = "[Total Sales] / [Total Sales]",
            FormatString = "0.0", Description = "x", DisplayFolder = "f" });
        var hits = BpaRules.Run(m, ruleIds: new[] { "DAX_USE_DIVIDE" });
        Assert.Contains(hits, f => f.ObjectName == "Sales[Bad Ratio]");
        Assert.DoesNotContain(hits, f => f.ObjectName == "Sales[Margin Pct]");   // uses DIVIDE -> clean
    }

    [Fact]
    public void DAX_USE_DIVIDE_ignores_slash_inside_a_string_literal()
    {
        var m = CleanModel();
        var sales = m.Tables.Find("Sales")!;
        sales.Measures.Add(new TOM.Measure { Name = "Labelled", Expression = "\"a/b\" & FORMAT ( [Total Sales], \"0\" )",
            FormatString = "0", Description = "x", DisplayFolder = "f" });
        var hits = BpaRules.Run(m, ruleIds: new[] { "DAX_USE_DIVIDE" });
        Assert.DoesNotContain(hits, f => f.ObjectName == "Sales[Labelled]");
    }

    [Fact]
    public void SUMMARIZE_WITHOUT_ADDCOLUMNS_fires_when_columns_added_inside()
    {
        var m = CleanModel();
        var sales = m.Tables.Find("Sales")!;
        sales.Measures.Add(new TOM.Measure { Name = "Bad Summ",
            Expression = "SUMX ( SUMMARIZE ( Sales, Customer[Customer Name], \"Tot\", [Total Sales] ), [Tot] )",
            FormatString = "0", Description = "x", DisplayFolder = "f" });
        Assert.True(Fires(m, "SUMMARIZE_WITHOUT_ADDCOLUMNS"));
    }

    // ================================================================ unused-object rules

    [Fact]
    public void UNUSED_COLUMNS_fires_and_autofix_hides()
    {
        var m = CleanModel();
        var sales = m.Tables.Find("Sales")!;
        var orphan = new TOM.DataColumn { Name = "Orphan Col", DataType = TOM.DataType.String, SourceColumn = "Orphan" };
        sales.Columns.Add(orphan);
        Assert.True(Fires(m, "UNUSED_COLUMNS"));
        BpaRules.Fix(m, "UNUSED_COLUMNS", "Sales[Orphan Col]", dryRun: false);
        Assert.True(orphan.IsHidden);
    }

    // ================================================================ runner filters

    [Fact]
    public void Run_category_filter_returns_only_that_category()
    {
        var hits = BpaRules.Run(BadModel(), categories: new[] { BpaRules.CatFormatting });
        Assert.NotEmpty(hits);
        Assert.All(hits, f => Assert.Equal(BpaRules.CatFormatting, f.Category));
    }

    [Fact]
    public void Run_severity_filter_returns_only_that_severity()
    {
        var hits = BpaRules.Run(BadModel(), severities: new[] { BpaRules.Error });
        Assert.NotEmpty(hits);
        Assert.All(hits, f => Assert.Equal(BpaRules.Error, f.Severity));
    }

    [Fact]
    public void Run_ruleId_filter_returns_only_that_rule()
    {
        var hits = BpaRules.Run(BadModel(), ruleIds: new[] { "HIDE_FOREIGN_KEYS" });
        Assert.NotEmpty(hits);
        Assert.All(hits, f => Assert.Equal("HIDE_FOREIGN_KEYS", f.RuleId));
    }

    [Fact]
    public void Run_scope_filter_returns_only_that_scope()
    {
        var hits = BpaRules.Run(BadModel(), scope: "Relationship");
        Assert.NotEmpty(hits);
        Assert.All(hits, f => Assert.Equal("Relationship", f.ObjectType));
    }

    [Fact]
    public void Clean_model_has_far_fewer_findings_than_bad_model()
    {
        var clean = BpaRules.Run(CleanModel());
        var bad = BpaRules.Run(BadModel());
        Assert.True(bad.Count > clean.Count, $"bad={bad.Count} should exceed clean={clean.Count}");
    }

    // ================================================================ Tabular Editor ruleset import

    [Fact]
    public void Import_maps_a_known_id_and_flags_an_unknown_expression_rule()
    {
        // one rule whose ID is a built-in (mapped) + one with a TE dynamic expression we cannot evaluate (manual).
        var json = new JsonArray
        {
            new JsonObject { ["ID"] = "HIDE_FOREIGN_KEYS", ["Name"] = "Hide foreign keys", ["Severity"] = 2 },
            new JsonObject
            {
                ["ID"] = "CUSTOM_TE_RULE_XYZ",
                ["Name"] = "Some TE-only rule",
                ["Severity"] = 3,
                ["Description"] = "Custom org rule.",
                ["Expression"] = "Measures.Where(m => m.Expression.Contains(\"FOO\")).Any()",
            },
        }.ToJsonString();

        var r = BpaRules.Import(json);
        Assert.Equal(2, r.total);
        Assert.Equal(1, r.mappedToBuiltIn);
        Assert.Equal(1, r.registeredAsManual);
        Assert.Contains(r.mapped, o => o.ToString()!.Contains("HIDE_FOREIGN_KEYS"));
        Assert.Contains(r.manual, o => o.ToString()!.Contains("CUSTOM_TE_RULE_XYZ"));
    }

    [Fact]
    public void Import_accepts_the_Rules_wrapper_object_shape()
    {
        var json = new JsonObject
        {
            ["Rules"] = new JsonArray { new JsonObject { ["ID"] = "MARK_PRIMARY_KEYS", ["Severity"] = 1 } },
        }.ToJsonString();
        var r = BpaRules.Import(json);
        Assert.Equal(1, r.mappedToBuiltIn);
    }

    [Fact]
    public void Import_rejects_non_json()
    {
        Assert.Throws<InvalidOperationException>(() => BpaRules.Import("not json {{{"));
    }

    [Fact]
    public void Import_never_evaluates_te_expression()
    {
        // the manual rule must be flagged as not-evaluated; the engine must not run the expression.
        var json = new JsonArray
        {
            new JsonObject { ["ID"] = "ORG_RULE_1", ["Severity"] = 2, ["Expression"] = "1/0 throw" },
        }.ToJsonString();
        var r = BpaRules.Import(json);
        Assert.Single(r.manual);
        Assert.Contains(r.manual, o => o.ToString()!.Contains("manual"));
    }

    // ================================================================ catalogue listing

    [Fact]
    public void Catalogue_listing_filters_by_category()
    {
        var obj = BpaRules.Catalogue(BpaRules.CatPerformance);
        var json = System.Text.Json.JsonSerializer.Serialize(obj);
        Assert.Contains("Performance", json);
        Assert.DoesNotContain("\"category\":\"Formatting\"", json);
    }

    // ================================================================ #REF! / broken named-range rules

    /// <summary>A fresh scratch dir (SUPERBI_TEST_SCRATCH override, temp fallback); caller deletes in finally.</summary>
    private static string NewScratchDir()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "bpa-refcheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Synthesizes a minimal workbook zip: just xl/workbook.xml with the given defined names.
    /// ReadDefinedNames only opens that entry, so a full valid workbook is not needed.
    /// </summary>
    private static string WriteWorkbook(string dir, string fileName, params (string Name, string RefersTo)[] definedNames)
    {
        string path = Path.Combine(dir, fileName);
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using var w = new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open());
        w.Write("<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
              + "<sheets><sheet name=\"Sheet1\" sheetId=\"1\"/></sheets><definedNames>"
              + string.Join("", definedNames.Select(d => $"<definedName name=\"{d.Name}\">{d.RefersTo}</definedName>"))
              + "</definedNames></workbook>");
        return path;
    }

    private static string DefinedNameM(string workbookPath) =>
        $"let Source = Excel.Workbook(File.Contents(\"{workbookPath}\"), null, true), " +
        $"X = Source{{[Item=\"FY27Data\",Kind=\"DefinedName\"]}}[Data] in X";

    [Fact]
    public void M_CONTAINS_REF_ERROR_fires_on_partition_M_not_on_clean_model()
    {
        var m = CleanModel();
        var src = (TOM.MPartitionSource)m.Tables.Find("Sales")!.Partitions.Find("Sales")!.Source;
        src.Expression = "let Source = Excel.CurrentWorkbook(){[Name=\"Sheet1!#REF!\"]}[Content] in Source";
        var hits = BpaRules.Run(m, ruleIds: new[] { "M_CONTAINS_REF_ERROR" });
        Assert.Contains(hits, f => f.ObjectType == "Partition" && f.ObjectName == "Sales.Sales");

        Assert.False(Fires(CleanModel(), "M_CONTAINS_REF_ERROR"));
    }

    [Fact]
    public void M_CONTAINS_REF_ERROR_fires_on_shared_expression()
    {
        var m = CleanModel();
        m.Expressions.Add(new TOM.NamedExpression { Name = "SourceRange", Kind = TOM.ExpressionKind.M,
            Expression = "let P = Excel.CurrentWorkbook(){[Name=\"#REF!\"]}[Content] in P" });
        var hits = BpaRules.Run(m, ruleIds: new[] { "M_CONTAINS_REF_ERROR" });
        Assert.Contains(hits, f => f.ObjectType == "Expression" && f.ObjectName == "SourceRange");
    }

    [Fact]
    public void BROKEN_SOURCE_NAMED_RANGE_fires_and_names_the_broken_defined_names()
    {
        string dir = NewScratchDir();
        try
        {
            string brokenWb = WriteWorkbook(dir, "broken.xlsx",
                ("FY27Data", "Sheet1!#REF!"), ("PriceTable", "Sheet1!$A$1:$B$9"));
            var m = CleanModel();
            var src = (TOM.MPartitionSource)m.Tables.Find("Sales")!.Partitions.Find("Sales")!.Source;
            src.Expression = DefinedNameM(brokenWb);

            var hits = BpaRules.Run(m, ruleIds: new[] { "BROKEN_SOURCE_NAMED_RANGE" });
            Assert.NotEmpty(hits);
            Assert.Contains(hits, f => f.ObjectName.StartsWith("Sales.Sales") && f.ObjectName.Contains("FY27Data"));
            Assert.DoesNotContain(hits, f => f.ObjectName.Contains("PriceTable"));   // healthy name not reported
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void BROKEN_SOURCE_NAMED_RANGE_stays_quiet_on_clean_workbook_and_clean_model()
    {
        string dir = NewScratchDir();
        try
        {
            string cleanWb = WriteWorkbook(dir, "clean.xlsx",
                ("FY27Data", "Sheet1!$A$1:$B$9"), ("PriceTable", "Sheet1!$D$1:$E$9"));
            var m = CleanModel();
            var src = (TOM.MPartitionSource)m.Tables.Find("Sales")!.Partitions.Find("Sales")!.Source;
            src.Expression = DefinedNameM(cleanWb);
            Assert.False(Fires(m, "BROKEN_SOURCE_NAMED_RANGE"));

            Assert.False(Fires(CleanModel(), "BROKEN_SOURCE_NAMED_RANGE"));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    [Fact]
    public void ReadDefinedNames_returns_pairs_and_is_empty_without_workbook_xml()
    {
        string dir = NewScratchDir();
        try
        {
            string wb = WriteWorkbook(dir, "names.xlsx",
                ("FY27Data", "Sheet1!#REF!"), ("PriceTable", "Sheet1!$A$1:$B$9"));
            var names = ExcelService.ReadDefinedNames(wb);
            Assert.Equal(2, names.Count);   // consecutive definedName siblings must BOTH be read
            Assert.Contains(names, n => n.Name == "FY27Data" && n.RefersTo == "Sheet1!#REF!");
            Assert.Contains(names, n => n.Name == "PriceTable" && n.RefersTo == "Sheet1!$A$1:$B$9");

            // a zip with no xl/workbook.xml -> empty, no throw; same for a missing file.
            string notWb = Path.Combine(dir, "notawb.xlsx");
            using (var fs = new FileStream(notWb, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            using (var w = new StreamWriter(zip.CreateEntry("other.txt").Open()))
                w.Write("x");
            Assert.Empty(ExcelService.ReadDefinedNames(notWb));
            Assert.Empty(ExcelService.ReadDefinedNames(Path.Combine(dir, "missing.xlsx")));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
