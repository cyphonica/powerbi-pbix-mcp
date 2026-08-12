using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Unit tests for the Wave G1 features: the model write-transaction gate (deferred SaveChanges,
/// commit/rollback semantics), RLS execution plumbing (role resolution, connection-string building with
/// its injection guard, DAX query normalisation), the ClearCache XMLA builder, the trace FE/SE
/// arithmetic, shared-expression rename/delete with M-aware reference scanning and rewriting,
/// calculation-item update/delete, measure moving (same-object re-parenting), hierarchy level CRUD,
/// culture/translation CRUD, partition CRUD and the calendar annotation CRUD. Each builds an in-memory
/// <c>new TOM.Model()</c> and drives the *Core helpers / pure seams - no live Analysis Services server
/// is required. Every group includes fault-sensitive assertions (a wrong/guarded input must throw).
/// </summary>
public sealed class ModelWaveG1ToolsTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model", Culture = "en-US" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Region", DataType = TOM.DataType.String, SourceColumn = "Region" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey" });
        sales.Columns.Add(new TOM.DataColumn { Name = "MonthName", DataType = TOM.DataType.String, SourceColumn = "MonthName" });
        sales.Columns.Add(new TOM.DataColumn { Name = "MonthNo", DataType = TOM.DataType.Int64, SourceColumn = "MonthNo" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM('Sales'[Amount])" });
        sales.Partitions.Add(new TOM.Partition { Name = "Sales", Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" } });
        model.Tables.Add(sales);

        var customer = new TOM.Table { Name = "Customer" };
        customer.Columns.Add(new TOM.DataColumn { Name = "CustomerKey", DataType = TOM.DataType.Int64, SourceColumn = "CustomerKey", IsKey = true });
        customer.Columns.Add(new TOM.DataColumn { Name = "Country", DataType = TOM.DataType.String, SourceColumn = "Country" });
        customer.Partitions.Add(new TOM.Partition { Name = "Customer", Source = new TOM.MPartitionSource { Expression = "let S = 1 in S" } });
        model.Tables.Add(customer);

        return model;
    }

    // ================================================================ 1. model write-transaction gate
    [Fact]
    public void GateSave_saves_immediately_when_no_transaction()
    {
        int saves = 0;
        bool saved = ModelTxn.GateSave(null, () => saves++);
        Assert.True(saved);
        Assert.Equal(1, saves);
    }

    [Fact]
    public void GateSave_defers_and_counts_when_transaction_open()
    {
        var state = new ModelTransactionState();
        int saves = 0;
        bool saved = ModelTxn.GateSave(state, () => saves++);
        Assert.False(saved);
        Assert.Equal(0, saves);                 // the engine never saw a save
        Assert.Equal(1, state.DeferredSaves);
        ModelTxn.GateSave(state, () => saves++);
        Assert.Equal(2, state.DeferredSaves);
    }

    [Fact]
    public void Begin_twice_on_the_same_model_throws()
    {
        var model = NewModel();
        ModelTxn.Begin(model);
        try
        {
            Assert.Throws<InvalidOperationException>(() => ModelTxn.Begin(model));
        }
        finally { ModelTxn.RollbackWith(model, () => { }); }
    }

    [Fact]
    public void Save_defers_while_open_then_commit_saves_exactly_once()
    {
        var model = NewModel();
        ModelTxn.Begin(model);
        // ModelTxn.Save on an open transaction never touches the (offline) model's SaveChanges.
        Assert.False(ModelTxn.Save(model));
        Assert.False(ModelTxn.Save(model));

        int saves = 0;
        int deferred = ModelTxn.CommitWith(model, () => saves++);
        Assert.Equal(1, saves);
        Assert.Equal(2, deferred);
        Assert.Null(ModelTxn.For(model));       // closed after commit
    }

    [Fact]
    public void Commit_without_open_transaction_throws()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelTxn.CommitWith(model, () => { }));
    }

    [Fact]
    public void Commit_failure_keeps_the_transaction_open()
    {
        var model = NewModel();
        ModelTxn.Begin(model);
        Assert.Throws<InvalidOperationException>(() =>
            ModelTxn.CommitWith(model, () => throw new InvalidOperationException("engine says no")));
        Assert.NotNull(ModelTxn.For(model));    // still open - caller can fix or roll back
        ModelTxn.RollbackWith(model, () => { });
        Assert.Null(ModelTxn.For(model));
    }

    [Fact]
    public void Rollback_discards_and_closes()
    {
        var model = NewModel();
        ModelTxn.Begin(model);
        ModelTxn.Save(model);
        bool undone = false;
        int discarded = ModelTxn.RollbackWith(model, () => undone = true);
        Assert.True(undone);
        Assert.Equal(1, discarded);
        Assert.Null(ModelTxn.For(model));
    }

    // ================================================================ 2. RLS execution plumbing
    [Fact]
    public void BuildRoleConnectionString_appends_roles_and_effective_user()
    {
        string s = ModelService.BuildRoleConnectionString(
            "Data Source=localhost:12345", new[] { "Region NZ", "Region AU" }, "user@contoso.com");
        Assert.Equal("Data Source=localhost:12345;Roles=Region NZ,Region AU;EffectiveUserName=user@contoso.com", s);
    }

    [Fact]
    public void BuildRoleConnectionString_omits_effective_user_when_absent()
    {
        string s = ModelService.BuildRoleConnectionString("Data Source=localhost:1", new[] { "R1" }, null);
        Assert.Equal("Data Source=localhost:1;Roles=R1", s);
        Assert.DoesNotContain("EffectiveUserName", s);
    }

    [Theory]
    [InlineData("bad;role")]
    [InlineData("bad=role")]
    [InlineData("bad,role")]
    [InlineData("  ")]
    public void BuildRoleConnectionString_rejects_injection_characters(string role)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.BuildRoleConnectionString("Data Source=x", new[] { role }, null));
    }

    [Fact]
    public void BuildRoleConnectionString_rejects_empty_roles_and_bad_effective_user()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.BuildRoleConnectionString("Data Source=x", Array.Empty<string>(), null));
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.BuildRoleConnectionString("Data Source=x", new[] { "R" }, "u;Password=p"));
    }

    [Fact]
    public void ResolveRoleNames_canonicalises_case_and_dedupes()
    {
        var model = NewModel();
        model.Roles.Add(new TOM.ModelRole { Name = "Region NZ" });
        model.Roles.Add(new TOM.ModelRole { Name = "Region AU" });
        var resolved = ModelService.ResolveRoleNames(model, new[] { "region nz", "REGION NZ", "Region AU" });
        Assert.Equal(new List<string> { "Region NZ", "Region AU" }, resolved);
    }

    [Fact]
    public void ResolveRoleNames_rejects_unknown_role_listing_known_ones()
    {
        var model = NewModel();
        model.Roles.Add(new TOM.ModelRole { Name = "Region NZ" });
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelService.ResolveRoleNames(model, new[] { "Nope" }));
        Assert.Contains("Region NZ", ex.Message);
        Assert.Throws<InvalidOperationException>(() => ModelService.ResolveRoleNames(model, Array.Empty<string>()));
    }

    [Fact]
    public void NormaliseDaxQuery_prefixes_evaluate_and_passes_define_through()
    {
        Assert.Equal("EVALUATE VALUES('Sales'[Region])", ModelService.NormaliseDaxQuery("VALUES('Sales'[Region])"));
        Assert.Equal("EVALUATE ROW(\"v\", 1)", ModelService.NormaliseDaxQuery("  EVALUATE ROW(\"v\", 1) ".Trim()));
        Assert.StartsWith("DEFINE", ModelService.NormaliseDaxQuery("DEFINE MEASURE Sales[X] = 1 EVALUATE ROW(\"v\", [X])"));
        Assert.Throws<InvalidOperationException>(() => ModelService.NormaliseDaxQuery("   "));
    }

    // ================================================================ 3. ClearCache XMLA builder
    [Fact]
    public void BuildClearCacheXmla_embeds_the_escaped_database_id()
    {
        string xmla = ModelService.BuildClearCacheXmla("db<1>&");
        Assert.StartsWith("<ClearCache xmlns=\"http://schemas.microsoft.com/analysisservices/2003/engine\">", xmla);
        Assert.Contains("<DatabaseID>db&lt;1&gt;&amp;</DatabaseID>", xmla);
        Assert.Throws<InvalidOperationException>(() => ModelService.BuildClearCacheXmla(" "));
    }

    // ================================================================ 4. trace FE/SE arithmetic
    private static DaxTraceEvent Ev(string cls, string? sub, long? durationMs) =>
        new(cls, sub, durationMs, null, null, null, null);

    [Fact]
    public void ComputeSummary_splits_fe_se_and_ignores_internal_scans()
    {
        var events = new List<DaxTraceEvent>
        {
            Ev("QueryEnd", "0", 100),
            Ev("VertiPaqSEQueryEnd", "0", 30),      // real scan - counted
            Ev("VertiPaqSEQueryEnd", "10", 25),     // internal scan - nested inside the real one
            Ev("VertiPaqSEQueryCacheMatch", "0", 0),
        };
        var s = DaxTrace.ComputeSummary(events);
        Assert.Equal(100, s.QueryMs);
        Assert.Equal(30, s.StorageEngineMs);
        Assert.Equal(70, s.FormulaEngineMs);
        Assert.Equal(1, s.StorageEngineQueries);
        Assert.Equal(1, s.CacheMatches);
        Assert.Equal(30.0, s.StorageEnginePct);
    }

    [Fact]
    public void ComputeSummary_floors_fe_at_zero_when_parallel_se_exceeds_wall_clock()
    {
        var events = new List<DaxTraceEvent>
        {
            Ev("QueryEnd", "0", 50),
            Ev("VertiPaqSEQueryEnd", "0", 40),
            Ev("VertiPaqSEQueryEnd", "0", 40),
        };
        var s = DaxTrace.ComputeSummary(events);
        Assert.Equal(80, s.StorageEngineMs);
        Assert.Equal(0, s.FormulaEngineMs);      // never negative
        Assert.Equal(100.0, s.StorageEnginePct); // capped at 100
    }

    [Fact]
    public void ComputeSummary_with_no_queries_reports_null_pct()
    {
        var s = DaxTrace.ComputeSummary(new List<DaxTraceEvent>());
        Assert.Equal(0, s.QueryMs);
        Assert.Null(s.StorageEnginePct);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("VertiPaqScan", true)]
    [InlineData(null, true)]     // missing subclass counts as a scan (deliberate: never under-report SE)
    [InlineData("10", false)]
    [InlineData("1", false)]
    public void IsScanSubclass_classifies(string? subclass, bool expected)
        => Assert.Equal(expected, DaxTrace.IsScanSubclass(subclass));

    // ================================================================ 5. M reference scanning / rewriting
    [Fact]
    public void MaskMLiteralsAndComments_masks_strings_and_comments_keeps_quoted_identifiers()
    {
        string code = "let a = \"text #\"\"Sales\"\"\" // trailing Sales\n, b = #\"Sales\" /* block Sales */ in b";
        string masked = ModelService.MaskMLiteralsAndComments(code);
        Assert.Equal(code.Length, masked.Length);        // positions must stay valid
        Assert.Contains("#\"Sales\"", masked);           // the real quoted identifier survives
        Assert.DoesNotContain("text", masked);           // string content is gone
        Assert.DoesNotContain("trailing", masked);       // line comment gone
        Assert.DoesNotContain("block", masked);          // block comment gone
    }

    [Fact]
    public void ScanMForName_finds_quoted_and_bare_references()
    {
        var quoted = ModelService.ScanMForName("let x = Table.RowCount(#\"My Query\") in x", "My Query");
        Assert.True(quoted.HasQuotedRef);
        Assert.False(quoted.HasBareRef);
        Assert.False(quoted.DeclaresLocally);

        var bare = ModelService.ScanMForName("let x = Params + 1 in x", "Params");
        Assert.True(bare.HasBareRef);
        Assert.False(bare.DeclaresLocally);
    }

    [Fact]
    public void ScanMForName_detects_local_declarations()
    {
        var scan = ModelService.ScanMForName("let Source = 1 in Source", "Source");
        Assert.True(scan.DeclaresLocally);      // Source = ... shadows a shared 'Source'
        Assert.True(scan.HasBareRef);           // in Source
    }

    [Fact]
    public void ScanMForName_ignores_strings_comments_and_partial_tokens()
    {
        var scan = ModelService.ScanMForName("let x = \"Params\" /* Params */ + MyParams + Params2 in x", "Params");
        Assert.False(scan.HasQuotedRef);
        Assert.False(scan.HasBareRef);          // MyParams / Params2 are different identifiers
        Assert.False(scan.DeclaresLocally);
    }

    [Fact]
    public void ScanMForName_does_not_false_hit_inside_longer_escaped_identifier()
    {
        // #"Sales"" X" is ONE identifier named: Sales" X - not a reference to Sales.
        var scan = ModelService.ScanMForName("let a = #\"Sales\"\" X\" in a", "Sales");
        Assert.False(scan.HasQuotedRef);
        Assert.False(scan.HasBareRef);
    }

    [Fact]
    public void RewriteMReferences_rewrites_both_forms_choosing_quoting_by_validity()
    {
        string code = "let a = Rates, b = #\"Rates\" in b";
        string rewritten = ModelService.RewriteMReferences(code, "Rates", "FX Rates");
        // 'FX Rates' is not a valid bare identifier, so BOTH forms become quoted.
        Assert.Equal("let a = #\"FX Rates\", b = #\"FX Rates\" in b", rewritten);

        string bareTarget = ModelService.RewriteMReferences("let a = Rates in a", "Rates", "Rates2");
        Assert.Equal("let a = Rates2 in a", bareTarget);
    }

    [Fact]
    public void RewriteMReferences_leaves_string_content_untouched()
    {
        string rewritten = ModelService.RewriteMReferences("let a = \"Rates\" & Rates in a", "Rates", "Rates2");
        Assert.Equal("let a = \"Rates\" & Rates2 in a", rewritten);
    }

    [Fact]
    public void RenameSharedExpressionCore_rewrites_referencers_and_renames()
    {
        var model = NewModel();
        model.Expressions.Add(new TOM.NamedExpression { Name = "Param1", Kind = TOM.ExpressionKind.M, Expression = "1" });
        model.Expressions.Add(new TOM.NamedExpression { Name = "Staging", Kind = TOM.ExpressionKind.M, Expression = "let x = Param1 + 1 in x" });
        var mps = (TOM.MPartitionSource)model.Tables.Find("Customer")!.Partitions[0].Source;
        mps.Expression = "let S = #\"Param1\" in S";

        var rewrittenIn = ModelService.RenameSharedExpressionCore(model, "Param1", "Param2");

        Assert.Null(model.Expressions.Find("Param1"));
        Assert.NotNull(model.Expressions.Find("Param2"));
        Assert.Equal("let x = Param2 + 1 in x", model.Expressions.Find("Staging")!.Expression);
        Assert.Equal("let S = #\"Param2\" in S", mps.Expression);
        Assert.Contains("partition Customer/Customer", rewrittenIn);
        Assert.Contains("shared expression Staging", rewrittenIn);
    }

    [Fact]
    public void RenameSharedExpressionCore_refuses_when_a_document_shadows_the_name()
    {
        var model = NewModel();
        // the Sales partition declares a LOCAL Source and uses it - renaming a shared 'Source' there is ambiguous.
        model.Expressions.Add(new TOM.NamedExpression { Name = "Source", Kind = TOM.ExpressionKind.M, Expression = "1" });
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelService.RenameSharedExpressionCore(model, "Source", "GlobalSource"));
        Assert.Contains("partition Sales/Sales", ex.Message);
    }

    [Fact]
    public void RenameSharedExpressionCore_rejects_collision_and_unknown()
    {
        var model = NewModel();
        model.Expressions.Add(new TOM.NamedExpression { Name = "A", Kind = TOM.ExpressionKind.M, Expression = "1" });
        model.Expressions.Add(new TOM.NamedExpression { Name = "B", Kind = TOM.ExpressionKind.M, Expression = "2" });
        Assert.Throws<InvalidOperationException>(() => ModelService.RenameSharedExpressionCore(model, "A", "B"));
        Assert.Throws<InvalidOperationException>(() => ModelService.RenameSharedExpressionCore(model, "Nope", "X"));
    }

    [Fact]
    public void DeleteSharedExpressionCore_refuses_while_referenced_and_deletes_when_clean()
    {
        var model = NewModel();
        model.Expressions.Add(new TOM.NamedExpression { Name = "Used", Kind = TOM.ExpressionKind.M, Expression = "1" });
        model.Expressions.Add(new TOM.NamedExpression { Name = "Unused", Kind = TOM.ExpressionKind.M, Expression = "2" });
        var mps = (TOM.MPartitionSource)model.Tables.Find("Customer")!.Partitions[0].Source;
        mps.Expression = "let S = Used in S";

        var ex = Assert.Throws<InvalidOperationException>(() => ModelService.DeleteSharedExpressionCore(model, "Used"));
        Assert.Contains("partition Customer/Customer", ex.Message);

        ModelService.DeleteSharedExpressionCore(model, "Unused");
        Assert.Null(model.Expressions.Find("Unused"));
    }

    [Fact]
    public void DeleteSharedExpressionCore_ignores_locally_shadowed_uses()
    {
        var model = NewModel();
        // Sales partition is 'let Source = ... in Source' - a LOCAL Source, not a reference to the shared one.
        model.Expressions.Add(new TOM.NamedExpression { Name = "Source", Kind = TOM.ExpressionKind.M, Expression = "1" });
        ModelService.DeleteSharedExpressionCore(model, "Source");
        Assert.Null(model.Expressions.Find("Source"));
    }

    [Fact]
    public void IsValidBareMIdentifier_classifies()
    {
        Assert.True(ModelService.IsValidBareMIdentifier("Param1"));
        Assert.True(ModelService.IsValidBareMIdentifier("_x.y"));
        Assert.False(ModelService.IsValidBareMIdentifier("My Query"));   // space needs #""
        Assert.False(ModelService.IsValidBareMIdentifier("let"));        // keyword
        Assert.False(ModelService.IsValidBareMIdentifier("1abc"));       // digit start
        Assert.False(ModelService.IsValidBareMIdentifier(""));
    }

    // ================================================================ 6. calculation item update/delete
    private static TOM.Table AddCalcGroup(TOM.Model model, string name = "Time")
    {
        var t = new TOM.Table { Name = name };
        t.Columns.Add(new TOM.DataColumn { Name = "Item", DataType = TOM.DataType.String, SourceColumn = "Item" });
        model.Tables.Add(t);
        ModelService.AddCalculationGroupCore(model, name, null);
        ModelService.AddCalculationItemCore(model, name, "Current", "SELECTEDMEASURE()", 0, null);
        ModelService.AddCalculationItemCore(model, name, "YTD", "CALCULATE(SELECTEDMEASURE(), DATESYTD('Sales'[MonthNo]))", 1, null);
        return t;
    }

    [Fact]
    public void UpdateCalculationItemCore_updates_expression_ordinal_format_and_name()
    {
        var model = NewModel();
        AddCalcGroup(model);
        var item = ModelService.UpdateCalculationItemCore(model, "Time", "YTD",
            "SELECTEDMEASURE() * 2", 5, "\"0.0%\"", "YTD v2");
        Assert.Equal("SELECTEDMEASURE() * 2", item.Expression);
        Assert.Equal(5, item.Ordinal);
        Assert.Equal("\"0.0%\"", item.FormatStringDefinition!.Expression);
        Assert.Equal("YTD v2", item.Name);
        Assert.Null(model.Tables.Find("Time")!.CalculationGroup!.CalculationItems.Find("YTD"));
    }

    [Fact]
    public void UpdateCalculationItemCore_empty_format_clears_and_null_leaves_unchanged()
    {
        var model = NewModel();
        AddCalcGroup(model);
        ModelService.UpdateCalculationItemCore(model, "Time", "YTD", null, null, "\"#,0\"", null);
        var item = ModelService.UpdateCalculationItemCore(model, "Time", "YTD", null, null, null, null);
        Assert.NotNull(item.FormatStringDefinition);     // null = unchanged
        item = ModelService.UpdateCalculationItemCore(model, "Time", "YTD", null, null, "", null);
        Assert.Null(item.FormatStringDefinition);        // empty = cleared
    }

    [Fact]
    public void UpdateCalculationItemCore_rejects_rename_collision_and_bad_targets()
    {
        var model = NewModel();
        AddCalcGroup(model);
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.UpdateCalculationItemCore(model, "Time", "YTD", null, null, null, "Current"));
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.UpdateCalculationItemCore(model, "Sales", "YTD", "1", null, null, null));   // not a calc group
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.UpdateCalculationItemCore(model, "Time", "Nope", "1", null, null, null));
    }

    [Fact]
    public void DeleteCalculationItemCore_removes_and_rejects_unknown()
    {
        var model = NewModel();
        AddCalcGroup(model);
        ModelService.DeleteCalculationItemCore(model, "Time", "YTD");
        Assert.Null(model.Tables.Find("Time")!.CalculationGroup!.CalculationItems.Find("YTD"));
        Assert.Throws<InvalidOperationException>(() => ModelService.DeleteCalculationItemCore(model, "Time", "YTD"));
    }

    // ================================================================ 7. move measure
    [Fact]
    public void MoveMeasureCore_reparents_preserving_kpi_format_folder_and_translations()
    {
        var model = NewModel();
        var me = model.Tables.Find("Sales")!.Measures.Find("Total Sales")!;
        me.FormatString = "#,0";
        me.DisplayFolder = "Key Measures";
        me.LineageTag = "tag-1";
        me.KPI = new TOM.KPI { TargetExpression = "100", StatusExpression = "1", StatusGraphic = "Three Circles Colored" };
        ModelService.AddCultureCore(model, "fr-FR");
        ModelService.SetTranslationCore(model, "fr-FR", "measure", "Total Sales", "Caption", "Ventes totales", "Sales");

        var (moved, fromTable) = ModelService.MoveMeasureCore(model, "Total Sales", "Customer");

        Assert.Equal("Sales", fromTable);
        Assert.Null(model.Tables.Find("Sales")!.Measures.Find("Total Sales"));
        var onTarget = model.Tables.Find("Customer")!.Measures.Find("Total Sales")!;
        Assert.Same(moved, onTarget);                    // the returned clone IS the one on the target
        Assert.Equal("#,0", onTarget.FormatString);
        Assert.Equal("Key Measures", onTarget.DisplayFolder);
        Assert.Equal("tag-1", onTarget.LineageTag);
        Assert.Equal("100", onTarget.KPI!.TargetExpression);
        // the translation holds an object reference, so it survives the move intact.
        var tr = model.Cultures.Find("fr-FR")!.ObjectTranslations.Single();
        Assert.Same(onTarget, tr.Object);
        Assert.Equal("Ventes totales", tr.Value);
    }

    [Fact]
    public void MoveMeasureCore_rejects_unknown_same_table_and_collision()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() => ModelService.MoveMeasureCore(model, "Nope", "Customer"));
        Assert.Throws<InvalidOperationException>(() => ModelService.MoveMeasureCore(model, "Total Sales", "Sales"));
        model.Tables.Find("Customer")!.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "1" });
        Assert.Throws<InvalidOperationException>(() => ModelService.MoveMeasureCore(model, "Total Sales", "Customer"));
    }

    // ================================================================ 8. hierarchy levels
    private static TOM.Hierarchy AddGeoHierarchy(TOM.Model model)
    {
        var t = model.Tables.Find("Sales")!;
        var h = new TOM.Hierarchy { Name = "Geo" };
        h.Levels.Add(new TOM.Level { Name = "Region", Ordinal = 0, Column = t.Columns.Find("Region") });
        t.Hierarchies.Add(h);
        return h;
    }

    [Fact]
    public void AddHierarchyLevelCore_appends_and_inserts_with_dense_ordinals()
    {
        var model = NewModel();
        AddGeoHierarchy(model);
        var appended = ModelService.AddHierarchyLevelCore(model, "Sales", "Geo", "MonthName", null, null);
        Assert.Equal(1, appended.Ordinal);               // appended at the bottom

        var inserted = ModelService.AddHierarchyLevelCore(model, "Sales", "Geo", "MonthNo", 0, "Month Number");
        Assert.Equal(0, inserted.Ordinal);               // inserted at the top
        var h = model.Tables.Find("Sales")!.Hierarchies.Find("Geo")!;
        var ordered = h.Levels.OrderBy(l => l.Ordinal).Select(l => l.Name).ToList();
        Assert.Equal(new List<string> { "Month Number", "Region", "MonthName" }, ordered);
        Assert.Equal(new List<int> { 0, 1, 2 }, h.Levels.OrderBy(l => l.Ordinal).Select(l => l.Ordinal).ToList());
    }

    [Fact]
    public void AddHierarchyLevelCore_rejects_duplicate_level_and_duplicate_column()
    {
        var model = NewModel();
        AddGeoHierarchy(model);
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddHierarchyLevelCore(model, "Sales", "Geo", "MonthName", null, "Region"));   // name taken
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddHierarchyLevelCore(model, "Sales", "Geo", "Region", null, "Region 2"));    // column already a level
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddHierarchyLevelCore(model, "Sales", "Nope", "MonthName", null, null));      // unknown hierarchy
    }

    [Fact]
    public void RemoveHierarchyLevelCore_removes_renumbers_and_guards_the_last_level()
    {
        var model = NewModel();
        AddGeoHierarchy(model);
        ModelService.AddHierarchyLevelCore(model, "Sales", "Geo", "MonthName", null, null);
        ModelService.RemoveHierarchyLevelCore(model, "Sales", "Geo", "Region");
        var h = model.Tables.Find("Sales")!.Hierarchies.Find("Geo")!;
        Assert.Single(h.Levels);
        Assert.Equal(0, h.Levels.Find("MonthName")!.Ordinal);    // renumbered densely
        // the last level cannot be removed - the hierarchy would be invalid.
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.RemoveHierarchyLevelCore(model, "Sales", "Geo", "MonthName"));
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.RemoveHierarchyLevelCore(model, "Sales", "Geo", "Nope"));
    }

    // ================================================================ 9. cultures / translations
    [Fact]
    public void ListTranslationsCore_flattens_and_filters_by_culture()
    {
        var model = NewModel();
        ModelService.AddCultureCore(model, "fr-FR");
        ModelService.AddCultureCore(model, "mi-NZ");
        ModelService.SetTranslationCore(model, "fr-FR", "measure", "Total Sales", "Caption", "Ventes totales", "Sales");
        ModelService.SetTranslationCore(model, "mi-NZ", "table", "Sales", "Caption", "Hoko", null);

        var all = ModelService.ListTranslationsCore(model, null);
        Assert.Equal(2, all.Count);

        var fr = ModelService.ListTranslationsCore(model, "fr-FR");
        var row = Assert.Single(fr);
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(row))!.AsObject();
        Assert.Equal("fr-FR", (string?)json["culture"]);
        Assert.Equal("Total Sales", (string?)json["objectName"]);
        Assert.Equal("Sales", (string?)json["table"]);
        Assert.Equal("Caption", (string?)json["property"]);
        Assert.Equal("Ventes totales", (string?)json["value"]);

        Assert.Throws<InvalidOperationException>(() => ModelService.ListTranslationsCore(model, "de-DE"));
    }

    [Fact]
    public void DeleteTranslationCore_removes_the_one_translation_and_rejects_missing()
    {
        var model = NewModel();
        ModelService.AddCultureCore(model, "fr-FR");
        ModelService.SetTranslationCore(model, "fr-FR", "measure", "Total Sales", "Caption", "Ventes", "Sales");
        ModelService.DeleteTranslationCore(model, "fr-FR", "measure", "Total Sales", "Caption", "Sales");
        Assert.Empty(model.Cultures.Find("fr-FR")!.ObjectTranslations);
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.DeleteTranslationCore(model, "fr-FR", "measure", "Total Sales", "Caption", "Sales"));
    }

    [Fact]
    public void DeleteCultureCore_removes_and_rejects_unknown()
    {
        var model = NewModel();
        ModelService.AddCultureCore(model, "fr-FR");
        ModelService.DeleteCultureCore(model, "fr-FR");
        Assert.Null(model.Cultures.Find("fr-FR"));
        var ex = Assert.Throws<InvalidOperationException>(() => ModelService.DeleteCultureCore(model, "fr-FR"));
        Assert.Contains("not found", ex.Message);
    }

    // ================================================================ 10. partition CRUD
    [Fact]
    public void AddPartitionCore_adds_m_partition_with_mode_and_marks_dirty()
    {
        var model = NewModel();
        var tracker = new MDirtyTracker();
        var p = ModelService.AddPartitionCore(model, tracker, "Sales", "Sales 2025",
            "let S = Csv.Document(File.Contents(\"x.csv\")) in S", "Import");
        Assert.Same(p, model.Tables.Find("Sales")!.Partitions.Find("Sales 2025"));
        Assert.IsType<TOM.MPartitionSource>(p.Source);
        Assert.Equal(TOM.ModeType.Import, p.Mode);
        Assert.True(tracker.IsDirty);
        Assert.Contains(tracker.Reasons, r => r.Contains("add_partition Sales/Sales 2025"));
    }

    [Fact]
    public void AddPartitionCore_rejects_duplicate_missing_m_and_bad_mode()
    {
        var model = NewModel();
        var tracker = new MDirtyTracker();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddPartitionCore(model, tracker, "Sales", "Sales", "let S = 1 in S", null));   // name taken
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddPartitionCore(model, tracker, "Sales", "P2", "  ", null));                  // no M
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddPartitionCore(model, tracker, "Sales", "P2", "let S = 1 in S", "Turbo"));   // bad mode
        Assert.False(model.Tables.Find("Sales")!.Partitions.Contains("P2"));
    }

    [Fact]
    public void DeletePartitionCore_deletes_and_refuses_the_last_partition()
    {
        var model = NewModel();
        var tracker = new MDirtyTracker();
        // Sales has exactly one partition - deleting it must be refused.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelService.DeletePartitionCore(model, tracker, "Sales", "Sales"));
        Assert.Contains("only partition", ex.Message);

        ModelService.AddPartitionCore(model, tracker, "Sales", "Sales 2025", "let S = 1 in S", null);
        tracker.Clear("test reset");
        ModelService.DeletePartitionCore(model, tracker, "Sales", "Sales 2025");
        Assert.Null(model.Tables.Find("Sales")!.Partitions.Find("Sales 2025"));
        Assert.True(tracker.IsDirty);

        Assert.Throws<InvalidOperationException>(() =>
            ModelService.DeletePartitionCore(model, tracker, "Sales", "Nope"));
    }

    // ================================================================ 11. calendar annotation CRUD
    [Fact]
    public void CalendarCrud_lists_updates_and_deletes_the_annotation()
    {
        var model = NewModel();
        var t = model.Tables.Find("Sales")!;
        t.Columns.Add(new TOM.DataColumn { Name = "FiscalPeriod", DataType = TOM.DataType.String, SourceColumn = "FiscalPeriod" });
        ModelService.AddCalendarBasedTimeIntelligenceCore(model, "Sales", "MonthName", new[] { "FiscalPeriod" });

        var listed = ModelService.ListCalendarsCore(model);
        var row = Assert.Single(listed);
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(row))!.AsObject();
        Assert.Equal("Sales", (string?)json["table"]);
        Assert.Equal("MonthName", (string?)json["primaryColumn"]);

        string updated = ModelService.UpdateCalendarCore(model, "Sales", "Region", new[] { "MonthName", "MonthNo" });
        var doc = JsonNode.Parse(updated)!.AsObject();
        Assert.Equal("Region", (string?)doc["columnName"]);
        var cols = doc["calendarColumnGroups"]![0]!["columnNames"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new List<string?> { "MonthName", "MonthNo" }, cols);

        ModelService.DeleteCalendarCore(model, "Sales");
        Assert.Null(t.Annotations.Find("PBI_Calendar"));
        Assert.Empty(ModelService.ListCalendarsCore(model));
    }

    [Fact]
    public void UpdateCalendarCore_rejects_unknown_column_and_missing_annotation()
    {
        var model = NewModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.UpdateCalendarCore(model, "Sales", "Region", null));   // no annotation yet
        model.Tables.Find("Sales")!.Columns.Add(new TOM.DataColumn { Name = "FiscalPeriod", DataType = TOM.DataType.String, SourceColumn = "FiscalPeriod" });
        ModelService.AddCalendarBasedTimeIntelligenceCore(model, "Sales", "MonthName", new[] { "FiscalPeriod" });
        Assert.Throws<InvalidOperationException>(() =>
            ModelService.UpdateCalendarCore(model, "Sales", "NoSuchColumn", null));
        Assert.Throws<InvalidOperationException>(() => ModelService.DeleteCalendarCore(model, "Customer"));
    }
}
