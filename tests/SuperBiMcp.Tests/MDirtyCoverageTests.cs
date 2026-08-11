using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// M-refresh gate COVERAGE regressions: every M-mutating path must mark the session's
/// <see cref="MDirtyTracker"/>, and nothing short of a whole-model Full refresh may clear it.
///
/// Each fact reproduces a confirmed adversarial-review finding against ModelService:
///  - add_m_parameter mutated an existing shared M parameter without marking, so a param-repoint
///    followed by save_open_pbix sailed past the gate as a bare Ctrl+S (silent-revert class).
///  - add_table_from_m (and the generator wrappers funnelling through it) injected a brand-new,
///    never-refreshed M partition unmarked while its own response said refreshRequired: true.
///  - create_csv_table CLEARED the model-wide tracker after a refresh scoped to only its own table,
///    disarming the gate for every OTHER table's pending M edit.
///  - set_incremental_refresh created RangeStart/RangeEnd shared M expressions unmarked (sweep catch).
///
/// The tests drive the internal *Core seams (mutation + tracker, no SaveChanges) against an
/// in-memory <c>new TOM.Model()</c>, the same offline pattern as <see cref="ModelParityToolsTests"/>,
/// and prove gate behaviour through the real <see cref="MRefreshGate"/>.
/// </summary>
public sealed class MDirtyCoverageTests
{
    private static TOM.Model NewModel()
    {
        var model = new TOM.Model { Name = "Model" };
        var sales = new TOM.Table { Name = "Fact_Sales" };
        sales.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        sales.Partitions.Add(new TOM.Partition
        {
            Name = "Fact_Sales",
            Source = new TOM.MPartitionSource { Expression = "let Source = #table({},{}) in Source" },
        });
        model.Tables.Add(sales);
        return model;
    }

    // ---------------- add_m_parameter ----------------

    [Fact]
    public void AddMParameter_UpdatingAnExistingParameter_MarksTheTracker()
    {
        // The finding's scenario: a param-driven template pbix whose partitions read a source-path
        // parameter is repointed via add_m_parameter with an EXISTING name. Before the fix the tracker
        // stayed clean and save_open_pbix dispatched a bare Ctrl+S over unrefreshed M.
        var model = NewModel();
        model.Expressions.Add(new TOM.NamedExpression
        {
            Name = "SourceFolder",
            Kind = TOM.ExpressionKind.M,
            Expression = "\"D:\\olddata\" meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]",
        });
        var dirty = new MDirtyTracker();

        ModelService.AddMParameterCore(model, dirty, "SourceFolder", "Text", "D:\\newdata", null);

        Assert.True(dirty.IsDirty);
        Assert.Contains("add_m_parameter SourceFolder", dirty.Reasons);
        Assert.Contains("newdata", model.Expressions.Find("SourceFolder")!.Expression);

        // and the REAL gate now refuses a save whose forced refresh fails, instead of waving it through
        Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => throw new Exception("source offline"), null));
    }

    [Fact]
    public void AddMParameter_CreatingANewParameter_MarksTheTracker_AndTheGateRunsTheRefresh()
    {
        var model = NewModel();
        var dirty = new MDirtyTracker();

        ModelService.AddMParameterCore(model, dirty, "RowCap", "Number", "1000", null);

        Assert.True(dirty.IsDirty);
        int refreshes = 0;
        Assert.Equal(MRefreshOutcome.Ran, MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => refreshes++, null));
        Assert.Equal(1, refreshes);      // pre-fix this was NotNeeded / 0: the coverage hole itself
        Assert.False(dirty.IsDirty);
    }

    // ---------------- add_table_from_m + generator wrappers ----------------

    [Fact]
    public void AddTableFromM_MarksTheTracker_SoABareSaveIsImpossibleWhileUnrefreshed()
    {
        // The finding's scenario: add_table_from_m creates a REST-sourced table, the agent immediately
        // calls save_open_pbix. Pre-fix: tracker clean, gate NotNeeded, bare Ctrl+S writes a .pbix with
        // a TOM-injected M partition Desktop's mashup document never knew about.
        var model = NewModel();
        var dirty = new MDirtyTracker();

        var table = ModelService.AddTableFromMCore(model, dirty, "ApiRows",
            "let Source = Json.Document(Web.Contents(\"https://api.example/rows\")) in Source");

        Assert.True(model.Tables.Contains("ApiRows"));
        Assert.IsType<TOM.MPartitionSource>(table.Partitions[0].Source);
        Assert.True(dirty.IsDirty);
        Assert.Contains("add_table_from_m ApiRows", dirty.Reasons);
        Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => throw new Exception("x"), null));
    }

    [Fact]
    public void AddTableFromM_ADuplicateTableIsRejected_WithoutAFalseMark()
    {
        var model = NewModel();
        var dirty = new MDirtyTracker();

        Assert.Throws<InvalidOperationException>(() =>
            ModelService.AddTableFromMCore(model, dirty, "Fact_Sales", "let Source = 1 in Source"));

        Assert.False(dirty.IsDirty);     // a refused mutation must not arm the gate
    }

    // ---------------- create_csv_table ----------------

    [Fact]
    public void CreateCsvTable_MarksTheTracker_AndOtherTablesPendingMEditsSurvive()
    {
        // The finding's scenario: set_partition_m on Fact_Sales (marked), then create_csv_table adds a
        // lookup table. Pre-fix the wrapper called MDirty.Clear after a refresh scoped to only the NEW
        // table, wiping Fact_Sales' pending reason model-wide; save_open_pbix then dispatched a bare
        // Ctrl+S and the unrefreshed Fact_Sales M reached the .pbix. The mutation seam must mark and
        // must never clear; the wrapper no longer contains any Clear at all.
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string csv = Path.Combine(root, "superbi-mdirty-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(csv, "Store,Region,Units\nAlbany,North,12\nTakapuna,North,7\n");
        try
        {
            var model = NewModel();
            var dirty = new MDirtyTracker();
            dirty.Mark("set_partition_m Fact_Sales/Fact_Sales");   // another table's pending M edit

            var (table, types) = ModelService.CreateCsvTableCore(model, dirty, "Lookup_Stores", csv, null, 50);

            Assert.True(model.Tables.Contains("Lookup_Stores"));
            Assert.Equal(new[] { "String", "String", "Int64" }, types);
            Assert.Equal(3, table.Columns.Count(c => c.Type != TOM.ColumnType.RowNumber));

            Assert.True(dirty.IsDirty);
            Assert.Contains("set_partition_m Fact_Sales/Fact_Sales", dirty.Reasons);   // SURVIVES
            Assert.Contains("create_csv_table Lookup_Stores", dirty.Reasons);
            Assert.Throws<MRefreshRequiredException>(() =>
                MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => throw new Exception("x"), null));
        }
        finally
        {
            try { File.Delete(csv); } catch { /* best effort */ }
        }
    }

    // ---------------- set_incremental_refresh (sweep catch) ----------------

    [Fact]
    public void SetIncrementalRefresh_CreatingRangeParams_MarksTheTracker()
    {
        var model = NewModel();
        var dirty = new MDirtyTracker();

        bool created = ModelService.SetIncrementalRefreshCore(model, dirty, "Fact_Sales", "Year", 5, "Month", 2, null);

        Assert.True(created);
        Assert.NotNull(model.Expressions.Find("RangeStart"));
        Assert.NotNull(model.Expressions.Find("RangeEnd"));
        Assert.True(dirty.IsDirty);
        Assert.Contains("set_incremental_refresh Fact_Sales (created RangeStart/RangeEnd M parameters)", dirty.Reasons);
    }

    [Fact]
    public void SetIncrementalRefresh_WhenRangeParamsAlreadyExist_LeavesTheTrackerAlone()
    {
        // The policy attach alone writes no M text: no false mark, no forced whole-model refresh at save.
        var model = NewModel();
        ModelService.SetIncrementalRefreshCore(model, "Fact_Sales", "Year", 3, "Day", 10, null);
        var dirty = new MDirtyTracker();

        bool created = ModelService.SetIncrementalRefreshCore(model, dirty, "Fact_Sales", "Year", 5, "Month", 2, null);

        Assert.False(created);
        Assert.False(dirty.IsDirty);
    }
}
