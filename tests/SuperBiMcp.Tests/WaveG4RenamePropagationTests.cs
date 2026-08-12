using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave G4 propagating renames: the DAX reference rewriter's fault-sensitive matrix (substring
/// collisions, string literals, comments, case variants, escapes, host-table bare brackets), the M
/// query-name rewriter, the model-wide walkers over an in-memory TOM model (every expression site
/// category + the TOM wiring that must follow automatically), rename-set validation, plan parsing,
/// and the report-side propagation on BOTH the legacy Layout path and the PBIR path. No live
/// engine anywhere.
/// </summary>
public sealed class WaveG4RenamePropagationTests
{
    // ---------------------------------------------------------------- fixtures

    /// <summary>A model rich enough to exercise every rename form: two tables sharing a column
    /// name, a measure whose name is also a column name elsewhere, and a bracket-hostile column.</summary>
    private static TOM.Model BaseModel()
    {
        var model = new TOM.Model { Name = "M" };

        var sales = new TOM.Table { Name = "Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Columns.Add(new TOM.DataColumn { Name = "Qty", DataType = TOM.DataType.Int64, SourceColumn = "Qty" });
        sales.Columns.Add(new TOM.DataColumn { Name = "A]B", DataType = TOM.DataType.String, SourceColumn = "AB" });
        sales.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM ( Sales[Amount] )" });
        model.Tables.Add(sales);

        var salesLy = new TOM.Table { Name = "Sales LY" };
        salesLy.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        model.Tables.Add(salesLy);

        var costs = new TOM.Table { Name = "Costs" };
        costs.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        model.Tables.Add(costs);

        return model;
    }

    private static RenameSet Set(TOM.Model model, params RenameSpec[] specs) => RenameSet.Build(model, specs);

    private static RenameSet RenameSalesTable(string newName = "Revenue")
        => Set(BaseModel(), new RenameSpec("table", null, "Sales", newName));

    // ================================================================ DaxRenamer: the matrix

    [Fact]
    public void QuotedTableRename_SubstringNameUntouched()
    {
        var set = RenameSalesTable();
        Assert.Equal("SUM ( 'Revenue'[Amount] ) + SUM ( 'Sales LY'[Amount] )",
            DaxRenamer.Rewrite("SUM ( 'Sales'[Amount] ) + SUM ( 'Sales LY'[Amount] )", set));
    }

    [Fact]
    public void BareTableRename_WordBoundary_NoSubstringHit()
    {
        var set = RenameSalesTable();
        // SalesX / _Sales are different identifiers - the bare-token matcher must not touch them
        Assert.Equal("Revenue[Amount] + SalesX[Amount] + _Sales[Amount]",
            DaxRenamer.Rewrite("Sales[Amount] + SalesX[Amount] + _Sales[Amount]", set));
    }

    [Fact]
    public void NewNameWithSpace_GetsQuotedInBareForm()
    {
        var set = RenameSalesTable("Sales Fact");
        Assert.Equal("SUM('Sales Fact'[Amount])", DaxRenamer.Rewrite("SUM(Sales[Amount])", set));
    }

    [Fact]
    public void StringLiterals_NeverRewritten()
    {
        var set = RenameSalesTable();
        string dax = "IF ( Sales[Amount] > 0, \"see Sales[Amount] here\", \"a \"\"'Sales'\"\" b\" )";
        Assert.Equal("IF ( Revenue[Amount] > 0, \"see Sales[Amount] here\", \"a \"\"'Sales'\"\" b\" )",
            DaxRenamer.Rewrite(dax, set));
    }

    [Fact]
    public void Comments_NeverRewritten()
    {
        var set = RenameSalesTable();
        string dax = "// Sales[Amount]\n-- 'Sales'[Amount]\n/* [Total Sales] on Sales */\nSUM ( 'Sales'[Amount] )";
        Assert.Equal("// Sales[Amount]\n-- 'Sales'[Amount]\n/* [Total Sales] on Sales */\nSUM ( 'Revenue'[Amount] )",
            DaxRenamer.Rewrite(dax, set));
    }

    [Fact]
    public void CaseVariants_Match_DaxNamesAreCaseInsensitive()
    {
        var set = Set(BaseModel(),
            new RenameSpec("table", null, "Sales", "Revenue"),
            new RenameSpec("column", "Sales", "Amount", "Amount NZD"));
        Assert.Equal("SUM ( 'Revenue'[Amount NZD] )", DaxRenamer.Rewrite("SUM ( 'SALES'[AMOUNT] )", set));
    }

    [Fact]
    public void ColumnRename_ScopedToItsTable()
    {
        var set = Set(BaseModel(), new RenameSpec("column", "Sales", "Amount", "Amount NZD"));
        // the same column name on Costs and on 'Sales LY' must NOT be rewritten
        Assert.Equal("Sales[Amount NZD] + Costs[Amount] + 'Sales LY'[Amount]",
            DaxRenamer.Rewrite("Sales[Amount] + Costs[Amount] + 'Sales LY'[Amount]", set));
    }

    [Fact]
    public void MeasureRename_BareAndHomeTableQualified_OtherTableUntouched()
    {
        var set = Set(BaseModel(), new RenameSpec("measure", "Sales", "Total Sales", "Revenue Total"));
        Assert.Equal("[Revenue Total] + Sales[Revenue Total] + 'Sales'[Revenue Total] + [Total Sales LY] + Costs[Total Sales]",
            DaxRenamer.Rewrite("[Total Sales] + Sales[Total Sales] + 'Sales'[Total Sales] + [Total Sales LY] + Costs[Total Sales]", set));
    }

    [Fact]
    public void BareBracket_Column_NeedsHostTable_AndMeasureOwnsBareBrackets()
    {
        var model = BaseModel();
        var set = RenameSet.Build(model, new[] { new RenameSpec("column", "Sales", "Amount", "Amount NZD") });
        // with the host table the bare column reference rewrites; without it, it stays
        Assert.Equal("[Amount NZD] * 2", DaxRenamer.Rewrite("[Amount] * 2", set, hostTable: "Sales"));
        Assert.Equal("[Amount] * 2", DaxRenamer.Rewrite("[Amount] * 2", set, hostTable: null));

        // a model measure named exactly like the column claims the bare-bracket form
        var model2 = BaseModel();
        model2.Tables["Costs"].Measures.Add(new TOM.Measure { Name = "Amount", Expression = "1" });
        var set2 = RenameSet.Build(model2, new[] { new RenameSpec("column", "Sales", "Amount", "Amount NZD") });
        Assert.Equal("[Amount] * 2", DaxRenamer.Rewrite("[Amount] * 2", set2, hostTable: "Sales"));
        // the qualified form still rewrites - it is unambiguous
        Assert.Equal("Sales[Amount NZD]", DaxRenamer.Rewrite("Sales[Amount]", set2));
    }

    [Fact]
    public void StandaloneQuotedTable_Rewritten_StandaloneBareWordLeft()
    {
        var set = RenameSalesTable();
        // quoted standalone is unambiguous; a bare word could be a VAR - deliberately untouched
        Assert.Equal("CALCULATE ( [X], ALL ( 'Revenue' ) )", DaxRenamer.Rewrite("CALCULATE ( [X], ALL ( 'Sales' ) )", set));
        Assert.Equal("VAR Sales = 1 RETURN Sales", DaxRenamer.Rewrite("VAR Sales = 1 RETURN Sales", set));
    }

    [Fact]
    public void BracketEscape_ReadAndWritten()
    {
        var set = Set(BaseModel(), new RenameSpec("column", "Sales", "A]B", "C]D"));
        Assert.Equal("Sales[C]]D] + 1", DaxRenamer.Rewrite("Sales[A]]B] + 1", set));
    }

    [Fact]
    public void QuoteEscape_InTableNames()
    {
        var model = BaseModel();
        model.Tables.Add(new TOM.Table { Name = "O'Brien" });
        var set = RenameSet.Build(model, new[] { new RenameSpec("table", null, "O'Brien", "OBrien") });
        Assert.Equal("COUNTROWS ( 'OBrien' )", DaxRenamer.Rewrite("COUNTROWS ( 'O''Brien' )", set));
    }

    [Fact]
    public void TableAndColumnRenamedTogether_OnePass()
    {
        var set = Set(BaseModel(),
            new RenameSpec("table", null, "Sales", "Sales Fact"),
            new RenameSpec("column", "Sales", "Amount", "Amount NZD"));
        Assert.Equal("SUMX ( 'Sales Fact', 'Sales Fact'[Amount NZD] )",
            DaxRenamer.Rewrite("SUMX ( 'Sales', 'Sales'[Amount] )", set));
    }

    [Fact]
    public void UnterminatedTokens_LeftVerbatim_NoCrash()
    {
        var set = RenameSalesTable();
        Assert.Equal("SUM ( 'Sales", DaxRenamer.Rewrite("SUM ( 'Sales", set));
        Assert.Equal("SUM ( Sales[Amount", DaxRenamer.Rewrite("SUM ( Sales[Amount", set));
        Assert.Equal("\"unterminated Sales[Amount]", DaxRenamer.Rewrite("\"unterminated Sales[Amount]", set));
    }

    // ================================================================ MRenamer

    [Fact]
    public void M_HashQuotedRef_Rewritten_StringLiteralUntouched()
    {
        string m = "let Source = #\"Old Name\", T = Table.SelectRows(Source, each [n] <> \"Old Name\") in T";
        Assert.Equal("let Source = #\"New Name\", T = Table.SelectRows(Source, each [n] <> \"Old Name\") in T",
            MRenamer.RewriteTableRefs(m, "Old Name", "New Name"));
    }

    [Fact]
    public void M_BareIdentifier_WordBoundary_CaseSensitive()
    {
        string m = "let J = Table.NestedJoin(Sales, {\"K\"}, SalesX, {\"K\"}, \"S\"), s2 = sales in J";
        // M is case-sensitive: only the exact 'Sales' token rewrites; SalesX and sales stay
        Assert.Equal("let J = Table.NestedJoin(#\"Sales Fact\", {\"K\"}, SalesX, {\"K\"}, \"S\"), s2 = sales in J",
            MRenamer.RewriteTableRefs(m, "Sales", "Sales Fact"));
    }

    [Fact]
    public void M_DottedFunctionNames_NeverSplit()
    {
        // Table.SelectRows must never be half-matched when renaming a table named 'Table'
        string m = "let T = Table.SelectRows(#\"Table\", each true) in T";
        Assert.Equal("let T = Table.SelectRows(Renamed, each true) in T",
            MRenamer.RewriteTableRefs(m, "Table", "Renamed"));
    }

    [Fact]
    public void M_Comments_Untouched()
    {
        string m = "// Sales here\n/* #\"Sales\" */\nlet S = #\"Sales\" in S";
        // a bare-valid new name is emitted bare even for a #"..." reference - both forms are valid M
        Assert.Equal("// Sales here\n/* #\"Sales\" */\nlet S = Rev in S",
            MRenamer.RewriteTableRefs(m, "Sales", "Rev"));
    }

    // ================================================================ RenameSet validation

    [Fact]
    public void Build_MissingObjects_Throw()
    {
        var model = BaseModel();
        Assert.Throws<InvalidOperationException>(() =>
            RenameSet.Build(model, new[] { new RenameSpec("table", null, "Nope", "X") }));
        Assert.Throws<InvalidOperationException>(() =>
            RenameSet.Build(model, new[] { new RenameSpec("column", "Sales", "Nope", "X") }));
        Assert.Throws<InvalidOperationException>(() =>
            RenameSet.Build(model, new[] { new RenameSpec("measure", "Sales", "Nope", "X") }));
        Assert.Throws<InvalidOperationException>(() =>
            RenameSet.Build(model, new[] { new RenameSpec("widget", null, "Sales", "X") }));
    }

    [Fact]
    public void Build_Collisions_Throw_ChainsRefused()
    {
        var model = BaseModel();
        // straight collision with an existing table
        Assert.Throws<InvalidOperationException>(() =>
            RenameSet.Build(model, new[] { new RenameSpec("table", null, "Sales", "Costs") }));
        // measure names are model-wide: renaming to a name held on ANOTHER table collides
        model.Tables["Costs"].Measures.Add(new TOM.Measure { Name = "Margin", Expression = "1" });
        Assert.Throws<InvalidOperationException>(() =>
            RenameSet.Build(model, new[] { new RenameSpec("measure", "Sales", "Total Sales", "Margin") }));
        // chained renames in one batch (A->B while B->C) are refused outright
        Assert.Throws<InvalidOperationException>(() =>
            RenameSet.Build(model, new[]
            {
                new RenameSpec("table", null, "Costs", "Expenses"),
                new RenameSpec("table", null, "Sales", "Costs"),
            }));
    }

    [Fact]
    public void Build_CaseOnlyRename_Allowed()
    {
        var set = Set(BaseModel(), new RenameSpec("table", null, "Sales", "SALES"));
        Assert.True(set.TryTable("Sales", out var nt));
        Assert.Equal("SALES", nt);
    }

    // ================================================================ model-wide walkers

    /// <summary>Every DAX site category populated, then Sales / Sales[Amount] / [Total Sales] renamed.</summary>
    private static TOM.Model RichModel()
    {
        var model = BaseModel();
        var sales = model.Tables["Sales"];
        var total = sales.Measures["Total Sales"];
        total.FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "IF ( [Total Sales] > 0, \"#,0\", \"0\" )" };
        total.DetailRowsDefinition = new TOM.DetailRowsDefinition { Expression = "SELECTCOLUMNS ( 'Sales', \"Amt\", 'Sales'[Amount] )" };
        total.KPI = new TOM.KPI { TargetExpression = "[Total Sales] * 1.1", StatusExpression = "IF ( [Total Sales] > 0, 1, -1 )" };
        sales.Measures.Add(new TOM.Measure { Name = "Margin", Expression = "[Total Sales] * 0.3" });
        sales.Columns.Add(new TOM.CalculatedColumn { Name = "Band", Expression = "IF ( [Amount] > 100, \"High\", \"Low\" )" });

        var top = new TOM.Table { Name = "Top Sales" };
        top.Partitions.Add(new TOM.Partition
        {
            Name = "Top Sales",
            Source = new TOM.CalculatedPartitionSource { Expression = "FILTER ( 'Sales', 'Sales'[Amount] > 10 )" },
        });
        model.Tables.Add(top);

        var ti = new TOM.Table { Name = "TI" };
        ti.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
        ti.CalculationGroup = new TOM.CalculationGroup();
        ti.CalculationGroup.CalculationItems.Add(new TOM.CalculationItem
        {
            Name = "SalesOnly",
            Expression = "CALCULATE ( SELECTEDMEASURE (), 'Sales'[Amount] > 0 )",
            FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "IF ( [Total Sales] > 0, \"0\", \"0\" )" },
        });
        model.Tables.Add(ti);

        var role = new TOM.ModelRole { Name = "Regional" };
        role.TablePermissions.Add(new TOM.TablePermission { Table = sales, FilterExpression = "[Amount] > 0 && 'Sales'[Qty] > 0" });
        model.Roles.Add(role);
        return model;
    }

    private static readonly RenameSpec[] TripleRename =
    {
        new("table", null, "Sales", "Sales Fact"),
        new("column", "Sales", "Amount", "Amount NZD"),
        new("measure", "Sales", "Total Sales", "Revenue"),
    };

    [Fact]
    public void RewriteModelDax_HitsEverySiteCategory()
    {
        var model = RichModel();
        var set = RenameSet.Build(model, TripleRename);
        var result = ModelRenamer.RewriteModelDax(model, set);

        var sales = model.Tables["Sales"];
        Assert.Equal("SUM ( 'Sales Fact'[Amount NZD] )", sales.Measures["Total Sales"].Expression);
        Assert.Equal("[Revenue] * 0.3", sales.Measures["Margin"].Expression);
        Assert.Equal("IF ( [Revenue] > 0, \"#,0\", \"0\" )", sales.Measures["Total Sales"].FormatStringDefinition.Expression);
        Assert.Equal("SELECTCOLUMNS ( 'Sales Fact', \"Amt\", 'Sales Fact'[Amount NZD] )",
            sales.Measures["Total Sales"].DetailRowsDefinition.Expression);
        Assert.Equal("[Revenue] * 1.1", sales.Measures["Total Sales"].KPI.TargetExpression);
        // the calculated column's bare [Amount] rewrites through its host table
        Assert.Equal("IF ( [Amount NZD] > 100, \"High\", \"Low\" )",
            ((TOM.CalculatedColumn)sales.Columns["Band"]).Expression);
        Assert.Equal("FILTER ( 'Sales Fact', 'Sales Fact'[Amount NZD] > 10 )",
            ((TOM.CalculatedPartitionSource)model.Tables["Top Sales"].Partitions[0].Source).Expression);
        Assert.Equal("CALCULATE ( SELECTEDMEASURE (), 'Sales Fact'[Amount NZD] > 0 )",
            model.Tables["TI"].CalculationGroup.CalculationItems["SalesOnly"].Expression);
        Assert.Equal("[Amount NZD] > 0 && 'Sales Fact'[Qty] > 0",
            model.Roles["Regional"].TablePermissions[0].FilterExpression);

        foreach (var category in new[] { "measures", "calculatedColumns", "calculatedTables", "calculationItems",
                     "formatStringExpressions", "detailRowsExpressions", "kpiExpressions", "rlsFilterExpressions" })
            Assert.True(result.Categories.GetValueOrDefault(category) >= 1, $"category '{category}' had no hits");
        Assert.Equal(result.Categories.Values.Sum(), result.Total);
    }

    [Fact]
    public void ApplyTomRenames_WiringFollows_SortBy_Hierarchy_Relationship_Translation()
    {
        var model = BaseModel();
        var sales = model.Tables["Sales"];
        sales.Columns.Add(new TOM.DataColumn { Name = "AmountSort", DataType = TOM.DataType.Int64, SourceColumn = "AmountSort" });
        sales.Columns["AmountSort"].SortByColumn = sales.Columns["Amount"];
        var hy = new TOM.Hierarchy { Name = "H" };
        hy.Levels.Add(new TOM.Level { Name = "Amount", Ordinal = 0, Column = sales.Columns["Amount"] });
        sales.Hierarchies.Add(hy);
        model.Relationships.Add(new TOM.SingleColumnRelationship
        {
            Name = "R",
            FromColumn = sales.Columns["Amount"],
            ToColumn = model.Tables["Costs"].Columns["Amount"],
            FromCardinality = TOM.RelationshipEndCardinality.Many,
            ToCardinality = TOM.RelationshipEndCardinality.One,
        });
        var culture = new TOM.Culture { Name = "en-US" };
        culture.ObjectTranslations.Add(new TOM.ObjectTranslation
        {
            Object = sales.Columns["Amount"], Property = TOM.TranslatedProperty.Caption, Value = "Amount (t)",
        });
        model.Cultures.Add(culture);

        var set = RenameSet.Build(model, new[]
        {
            new RenameSpec("table", null, "Sales", "Sales Fact"),
            new RenameSpec("column", "Sales", "Amount", "Amount NZD"),
        });
        var wiring = JsonSerializer.SerializeToNode(ModelRenamer.WiringSummary(model, set)) as JsonObject;
        Assert.True((int)wiring!["sortByColumns"]! >= 1);
        Assert.True((int)wiring["hierarchyLevels"]! >= 1);
        Assert.True((int)wiring["relationshipEnds"]! >= 1);
        Assert.True((int)wiring["translations"]! >= 1);

        var applied = ModelRenamer.ApplyTomRenames(model, set);
        Assert.Equal(2, applied.Count);

        // TOM object references carry over: the wiring points at the renamed objects automatically
        var renamed = model.Tables["Sales Fact"];
        Assert.Equal("Amount NZD", renamed.Columns["AmountSort"].SortByColumn.Name);
        Assert.Equal("Amount NZD", renamed.Hierarchies["H"].Levels[0].Column.Name);
        var rel = model.Relationships.OfType<TOM.SingleColumnRelationship>().Single();
        Assert.Equal("Sales Fact", rel.FromTable.Name);
        Assert.Equal("Amount NZD", rel.FromColumn.Name);
        Assert.Equal("Amount NZD", ((TOM.Column)culture.ObjectTranslations[0].Object).Name);
    }

    [Fact]
    public void RewriteModelM_Partitions_SharedExpressions_AndPartitionName()
    {
        var model = new TOM.Model();
        var sales = new TOM.Table { Name = "Sales" };
        sales.Partitions.Add(new TOM.Partition
        {
            Name = "Sales",
            Source = new TOM.MPartitionSource { Expression = "let Source = Csv.Document(File.Contents(\"x.csv\")) in Source" },
        });
        model.Tables.Add(sales);
        var orders = new TOM.Table { Name = "Orders" };
        orders.Partitions.Add(new TOM.Partition
        {
            Name = "Orders",
            Source = new TOM.MPartitionSource { Expression = "let S = #\"Sales\", J = Table.NestedJoin(S, {\"K\"}, Sales, {\"K\"}, \"S\") in J" },
        });
        model.Tables.Add(orders);
        model.Expressions.Add(new TOM.NamedExpression
        {
            Name = "Shared", Kind = TOM.ExpressionKind.M, Expression = "let x = #\"Sales\" in x",
        });

        var set = RenameSet.Build(model, new[] { new RenameSpec("table", null, "Sales", "Sales Fact") });
        ModelRenamer.ApplyTomRenames(model, set);
        var (sites, partitionsRenamed) = ModelRenamer.RewriteModelM(model, set);

        Assert.Equal(2, sites);   // the Orders partition and the shared expression (Sales' own M has no self-ref)
        Assert.Equal("let S = #\"Sales Fact\", J = Table.NestedJoin(S, {\"K\"}, #\"Sales Fact\", {\"K\"}, \"S\") in J",
            ((TOM.MPartitionSource)orders.Partitions[0].Source).Expression);
        Assert.Equal("let x = #\"Sales Fact\" in x", model.Expressions["Shared"].Expression);
        // the renamed table's partition (the query name) follows the table name
        Assert.Single(partitionsRenamed);
        Assert.NotNull(model.Tables["Sales Fact"].Partitions.Find("Sales Fact"));
    }

    // ================================================================ plan parsing + validation

    [Fact]
    public void Plan_Parse_AcceptsAuditEnvelope_BareArray_AndRejectsMalformedRows()
    {
        var (specs, rejected) = RenamePlan.Parse(
            "{\"renamePlan\":{\"renames\":[{\"objectType\":\"table\",\"oldName\":\"DIM_X\",\"newName\":\"X\"}," +
            "{\"objectType\":\"column\",\"table\":\"T\",\"oldName\":\"a_b\",\"newName\":\"A B\"}," +
            "{\"objectType\":\"measure\",\"oldName\":\"only-old\"}]}}");
        Assert.Equal(2, specs.Count);
        Assert.Single(rejected);
        Assert.Equal("table", specs[0].ObjectType);
        Assert.Null(specs[0].Table);

        var (bare, none) = RenamePlan.Parse("[{\"objectType\":\"table\",\"oldName\":\"A\",\"newName\":\"B\"}]");
        Assert.Single(bare);
        Assert.Empty(none);

        Assert.Throws<ArgumentException>(() => RenamePlan.Parse("{\"nothing\":true}"));
        Assert.Throws<ArgumentException>(() => RenamePlan.Parse("   "));
    }

    [Fact]
    public void Plan_Validate_SkipsBadRowsWithReasons_KeepsSurvivors()
    {
        var model = BaseModel();
        var specs = new List<RenameSpec>
        {
            new("table", null, "Sales", "Revenue"),               // fine
            new("table", null, "Missing", "X"),                    // no such table
            new("column", "Costs", "Amount", "Spend"),             // fine
            new("column", "Sales", "Amount", "Qty"),               // collides with Sales[Qty]
            new("measure", "Sales", "Total Sales", "Total Sales"), // no-op
        };
        var (valid, skipped) = RenamePlan.Validate(model, specs);
        Assert.Equal(2, valid.Count);
        Assert.Equal(3, skipped.Count);
        var reasons = skipped.Select(s => (string)((JsonObject)JsonSerializer.SerializeToNode(s)!)["reason"]!).ToList();
        Assert.Contains(reasons, r => r.Contains("not found"));
        Assert.Contains(reasons, r => r.Contains("already has a column"));
        Assert.Contains(reasons, r => r.Contains("no-op"));
    }

    // ================================================================ report maps

    [Fact]
    public void BuildReportMaps_FieldAndEntity_TableRenameComposedIntoFieldTargets()
    {
        var model = RichModel();
        var set = RenameSet.Build(model, TripleRename);
        var (fieldMap, entityMap) = ModelRenamer.BuildReportMaps(model, set);

        Assert.Equal("Sales Fact", entityMap["Sales"]);
        Assert.Equal("Sales Fact[Amount NZD]", fieldMap["Sales[Amount]"]);
        Assert.Equal("Sales Fact[Revenue]", fieldMap["Sales[Total Sales]"]);
    }

    // ================================================================ legacy report propagation

    private static JsonObject Section(string displayName, int ordinal) => new()
    {
        ["name"] = "ReportSection" + ordinal + new string('c', 18),
        ["displayName"] = displayName, ["ordinal"] = ordinal,
        ["visualContainers"] = new JsonArray(),
        ["config"] = "{}", ["filters"] = "[]", ["width"] = 1280, ["height"] = 720,
    };

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

    [Fact]
    public void LegacyPropagation_TableAndColumnRename_RewritesQuery_Projections_Filters_UnnamedFieldsFollowEntity()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v1 = (string)Obj(svc.AddVisual(sid, "Main", "clusteredColumnChart", 0, 0, 0, 400, 300,
            new[]
            {
                new FieldBinding("Category", "Customer", "Name", "column"),
                new FieldBinding("Y", "Sales", "Amount", "measure"),
            }, null))["visualName"]!;
        svc.AddVisualFilter(sid, "Main", v1, "Sales", "Amount", "column", "gt", "0", "int");
        // Qty is NOT in the field map - it must follow the table rename via the entity map
        svc.AddReportFilter(sid, "Sales", "Qty", "categorical", "column", null, new[] { "1" }, "int");

        var session = store.GetReport(sid);
        var fieldMap = new Dictionary<string, string> { ["Sales[Amount]"] = "Sales Fact[Amount NZD]" };
        var entityMap = new Dictionary<string, string> { ["Sales"] = "Sales Fact" };
        var r = Obj(svc.PropagateRenamesLegacy(session, fieldMap, entityMap));

        Assert.True((bool)r["ok"]!);
        Assert.True(session.Dirty);

        var uses = svc.CollectFieldUses(sid);
        Assert.Contains(uses, u => u is { Table: "Sales Fact", Field: "Amount NZD", Context: "projection" });
        Assert.Contains(uses, u => u is { Table: "Sales Fact", Field: "Amount NZD", Context: "filter" });
        Assert.Contains(uses, u => u is { Table: "Sales Fact", Field: "Qty", Context: "filter" });
        Assert.Contains(uses, u => u is { Table: "Customer", Field: "Name" });   // untouched
        Assert.DoesNotContain(uses, u => u.Table == "Sales");

        // the raw config: From entity repointed, Select Name and projection queryRef in step
        string cfg = "";
        var root = session.Layout.Root;
        foreach (var vc in ((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!).OfType<JsonObject>())
            cfg = (string)vc["config"]!;
        Assert.Contains("Sales Fact.Amount NZD", cfg);
        Assert.Contains("\"Entity\":\"Sales Fact\"", cfg.Replace(" ", "").Replace("SalesFact", "Sales Fact"));
        Assert.DoesNotContain("\"Sales\"", cfg);
    }

    [Fact]
    public void LegacyPropagation_MeasureRename_CfBindingFollows()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        string v1 = (string)Obj(svc.AddVisual(sid, "Main", "clusteredColumnChart", 0, 0, 0, 400, 300,
            new[] { new FieldBinding("Y", "Sales", "Total Sales", "measure") }, null))["visualName"]!;
        // patch a conditional-formatting colour measure into objects
        var root = store.GetReport(sid).Layout.Root;
        foreach (var vc in ((JsonArray)((JsonObject)((JsonArray)root["sections"]!)[0]!)["visualContainers"]!).OfType<JsonObject>())
        {
            var co = (JsonObject)JsonNode.Parse((string)vc["config"]!)!;
            ((JsonObject)co["singleVisual"]!)["objects"] = new JsonObject
            {
                ["dataPoint"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                {
                    ["fill"] = new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonObject
                    {
                        ["expr"] = new JsonObject
                        {
                            ["Measure"] = new JsonObject
                            {
                                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = "Sales" } },
                                ["Property"] = "Total Sales",
                            },
                        },
                    } } },
                } } },
            };
            vc["config"] = co.ToJsonString();
        }

        var session = store.GetReport(sid);
        var r = Obj(svc.PropagateRenamesLegacy(session,
            new Dictionary<string, string> { ["Sales[Total Sales]"] = "Sales[Revenue]" },
            new Dictionary<string, string>()));

        Assert.True((bool)r["ok"]!);
        var uses = svc.CollectFieldUses(sid);
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Revenue", Context: "projection" });
        Assert.Contains(uses, u => u is { Table: "Sales", Field: "Revenue", Context: "conditional-formatting" });
        Assert.DoesNotContain(uses, u => u.Field == "Total Sales");
    }

    // ================================================================ PBIR propagation

    private static PbirService.PbirModel PbirModelFixture()
    {
        var model = new PbirService.PbirModel { SourcePath = "in-memory", FromPbix = false, ReportRootInZip = "" };
        void Put(string rel, string json) =>
            model.Add(rel, new PbirService.PbirEntry { RelPath = rel, Json = (JsonObject)JsonNode.Parse(json)! });

        Put("definition.pbir", "{\"version\":\"1.0\",\"datasetReference\":{\"byPath\":{\"path\":\"../Model\"}}}");
        Put("report.json", "{}");
        Put("definition/pages/pages.json", "{\"pageOrder\":[\"p1\"],\"activePageName\":\"p1\"}");
        Put("definition/pages/p1/page.json",
            "{\"name\":\"p1\",\"displayName\":\"Overview\",\"width\":1280,\"height\":720," +
            "\"filterConfig\":{\"filters\":[{\"name\":\"f1\",\"expression\":{\"Column\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Sales\"}},\"Property\":\"Qty\"}}}]}}");
        Put("definition/pages/p1/visuals/v1/visual.json",
            "{\"name\":\"v1\",\"position\":{\"x\":0,\"y\":0,\"z\":0,\"width\":400,\"height\":300}," +
            "\"visual\":{\"visualType\":\"columnChart\",\"query\":{\"queryState\":{" +
            "\"Category\":{\"projections\":[{\"field\":{\"Column\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Customer\"}},\"Property\":\"Name\"}},\"queryRef\":\"Customer.Name\"}]}," +
            "\"Y\":{\"projections\":[{\"field\":{\"Measure\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Sales\"}},\"Property\":\"Total Sales\"}},\"queryRef\":\"Sales.Total Sales\",\"active\":true}]}}}," +
            "\"objects\":{\"dataPoint\":[{\"properties\":{\"fill\":{\"solid\":{\"color\":{\"expr\":{\"Measure\":{\"Expression\":{\"SourceRef\":{\"Entity\":\"Sales\"}},\"Property\":\"Total Sales\"}}}}}}}]}}}");
        return model;
    }

    [Fact]
    public void PbirPropagation_RewritesEntities_Properties_QueryRefs_AndMarksDirty()
    {
        var model = PbirModelFixture();
        var r = Obj(ReportService.PropagateRenamesPbir(model,
            new Dictionary<string, string> { ["Sales[Total Sales]"] = "Revenue[Total Rev]" },
            new Dictionary<string, string> { ["Sales"] = "Revenue" }));

        Assert.True((bool)r["ok"]!);
        Assert.True((int)r["filesChanged"]! >= 2);   // the visual and the page filter

        var visual = model.Entries["definition/pages/p1/visuals/v1/visual.json"];
        Assert.True(visual.Dirty);
        string vtext = visual.Json!.ToJsonString();
        Assert.Contains("\"Revenue.Total Rev\"", vtext);
        Assert.Contains("\"Total Rev\"", vtext);
        Assert.DoesNotContain("\"Sales\"", vtext);
        Assert.Contains("Customer.Name", vtext);   // untouched binding

        var page = model.Entries["definition/pages/p1/page.json"];
        Assert.True(page.Dirty);
        // Qty is not in the field map - it follows the entity rename
        Assert.Contains("\"Revenue\"", page.Json!.ToJsonString());
        Assert.DoesNotContain("\"Sales\"", page.Json!.ToJsonString());

        // the collector reads the rewritten tree cleanly - both layers agree
        var uses = CrossLayerAnalyzer.CollectPbirUses(model);
        Assert.Contains(uses, u => u is { Table: "Revenue", Field: "Total Rev", IsMeasure: true });
        Assert.Contains(uses, u => u is { Table: "Revenue", Field: "Qty", Context: "filter" });
        Assert.DoesNotContain(uses, u => u.Table == "Sales");

        // untouched files stay clean
        Assert.False(model.Entries["definition/pages/pages.json"].Dirty);
        Assert.False(model.Entries["definition.pbir"].Dirty);
    }

    [Fact]
    public void PbirPropagation_NoMatches_NothingDirtied()
    {
        var model = PbirModelFixture();
        var r = Obj(ReportService.PropagateRenamesPbir(model,
            new Dictionary<string, string> { ["Nope[Field]"] = "Nope2[Field]" },
            new Dictionary<string, string>()));
        Assert.Equal(0, (int)r["filesChanged"]!);
        Assert.All(model.Order, rel => Assert.False(model.Entries[rel].Dirty));
    }

    [Fact]
    public void RewriteQueryRef_Exact_Aggregate_EntityPrefix()
    {
        var dotMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["Sales.Amount"] = "Rev.Amount NZD" };
        var entityMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["Sales"] = "Rev" };

        Assert.Equal("Rev.Amount NZD", ReportService.RewriteQueryRef("Sales.Amount", dotMap, entityMap));
        Assert.Equal("Sum(Rev.Amount NZD)", ReportService.RewriteQueryRef("Sum(Sales.Amount)", dotMap, entityMap));
        Assert.Equal("Rev.Qty", ReportService.RewriteQueryRef("Sales.Qty", dotMap, entityMap));
        Assert.Equal("Costs.Amount", ReportService.RewriteQueryRef("Costs.Amount", dotMap, entityMap));
    }

    // ================================================================ report rename targets

    [Fact]
    public void LegacyTarget_SnapshotAndRestore_PutsTheLayoutBack()
    {
        var (svc, sid, store) = NewReport(Section("Main", 0));
        svc.AddVisual(sid, "Main", "clusteredColumnChart", 0, 0, 0, 400, 300,
            new[] { new FieldBinding("Y", "Sales", "Amount", "measure") }, null);
        var session = store.GetReport(sid);
        session.Dirty = false;
        string before = session.Layout.Root.ToJsonString();

        var target = new LegacyReportRenameTarget(svc, session);
        target.Snapshot();
        target.Rewrite(new Dictionary<string, string> { ["Sales[Amount]"] = "Sales[Amt]" },
            new Dictionary<string, string>());
        Assert.NotEqual(before, session.Layout.Root.ToJsonString());
        Assert.True(session.Dirty);

        target.Restore();
        Assert.Equal(before, session.Layout.Root.ToJsonString());
        Assert.False(session.Dirty);
    }

    [Fact]
    public void PbirSessionTarget_SnapshotAndRestore_PutsEntriesAndDirtyFlagsBack()
    {
        var model = PbirModelFixture();
        string before = model.Entries["definition/pages/p1/visuals/v1/visual.json"].Json!.ToJsonString();

        var target = new PbirSessionRenameTarget(model);
        target.Snapshot();
        target.Rewrite(new Dictionary<string, string> { ["Sales[Total Sales]"] = "Rev[Total]"},
            new Dictionary<string, string> { ["Sales"] = "Rev" });
        Assert.True(model.Entries["definition/pages/p1/visuals/v1/visual.json"].Dirty);

        target.Restore();
        Assert.Equal(before, model.Entries["definition/pages/p1/visuals/v1/visual.json"].Json!.ToJsonString());
        Assert.All(model.Order, rel => Assert.False(model.Entries[rel].Dirty));
    }
}
