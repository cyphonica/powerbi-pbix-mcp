using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for the PbiEngine hostability pre-flight and workdir resolution. No msmdsrv, no
/// Power BI Desktop and no network: <see cref="PbiEngine.AssertHostable"/> walks an in-memory
/// <c>new TOM.Model()</c>, and the workdir tests exercise only the ctor + Dispose (Start is never called).
/// </summary>
public sealed class PbiEngineTests
{
    private static TOM.Table TableWith(string name, TOM.PartitionSource source)
    {
        var table = new TOM.Table { Name = name };
        table.Columns.Add(new TOM.DataColumn { Name = "Id", DataType = TOM.DataType.Int64, SourceColumn = "Id" });
        table.Partitions.Add(new TOM.Partition { Name = name, Source = source });
        return table;
    }

    private static TOM.PartitionSource MSource() =>
        new TOM.MPartitionSource { Expression = "let Source = Table.FromRows({}, {\"Id\"}) in Source" };

    private static TOM.PartitionSource DataTableSource() =>
        new TOM.CalculatedPartitionSource { Expression = "DATATABLE(\"Id\", INTEGER, {{1}})" };

    // ---------------------------------------------------------------- AssertHostable: rejects M

    [Fact]
    public void AssertHostable_throws_for_an_m_import_partition_naming_the_constraint_and_the_desktop_path()
    {
        var model = new TOM.Model();
        model.Tables.Add(TableWith("Fact_Sales", MSource()));

        var ex = Assert.Throws<InvalidOperationException>(() => PbiEngine.AssertHostable(model));

        // the message must name the real constraint (the cryptic runtime errors it pre-empts) and direct
        // the caller to the Desktop-hosted path
        Assert.Contains("Power Query (M)", ex.Message);
        Assert.Contains("MEngineHelper is not loaded", ex.Message);
        Assert.Contains("M engine integration is not enabled", ex.Message);
        Assert.Contains("DesktopSession", ex.Message);
        Assert.Contains("'Fact_Sales'[Fact_Sales]", ex.Message);
    }

    [Fact]
    public void AssertHostable_throws_for_entity_and_policy_range_partitions()
    {
        var entityModel = new TOM.Model();
        entityModel.Tables.Add(TableWith("Orders", new TOM.EntityPartitionSource { EntityName = "Orders" }));
        var entityEx = Assert.Throws<InvalidOperationException>(() => PbiEngine.AssertHostable(entityModel));
        Assert.Contains("(Entity)", entityEx.Message);

        var policyModel = new TOM.Model();
        policyModel.Tables.Add(TableWith("Sales", new TOM.PolicyRangePartitionSource
        {
            Start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            End = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Granularity = TOM.RefreshGranularityType.Month,
        }));
        var policyEx = Assert.Throws<InvalidOperationException>(() => PbiEngine.AssertHostable(policyModel));
        Assert.Contains("(PolicyRange)", policyEx.Message);
    }

    [Fact]
    public void AssertHostable_names_only_the_m_backed_partitions_in_a_mixed_model()
    {
        var model = new TOM.Model();
        model.Tables.Add(TableWith("Dim_Native", DataTableSource()));
        model.Tables.Add(TableWith("Fact_Import", MSource()));

        var ex = Assert.Throws<InvalidOperationException>(() => PbiEngine.AssertHostable(model));
        Assert.Contains("'Fact_Import'[Fact_Import] (M)", ex.Message);
        Assert.DoesNotContain("Dim_Native", ex.Message);
    }

    // ---------------------------------------------------------------- AssertHostable: passes M-free

    [Fact]
    public void AssertHostable_passes_an_engine_native_datatable_model()
    {
        var model = new TOM.Model();
        model.Tables.Add(TableWith("Dim_A", DataTableSource()));
        model.Tables.Add(TableWith("Dim_B", DataTableSource()));

        PbiEngine.AssertHostable(model);   // no throw
    }

    [Fact]
    public void AssertHostable_passes_an_empty_model_and_a_partitionless_table()
    {
        PbiEngine.AssertHostable(new TOM.Model());   // no throw

        var model = new TOM.Model();
        model.Tables.Add(new TOM.Table { Name = "MeasuresOnly" });
        PbiEngine.AssertHostable(model);   // no throw
    }

    // ---------------------------------------------------------------- workdir resolution

    [Fact]
    public void Workdir_defaults_to_the_system_temp_and_is_removed_on_dispose()
    {
        string workDir;
        using (var engine = new PbiEngine("unused.exe"))
        {
            workDir = engine.WorkDir;
            Assert.StartsWith(Path.GetTempPath(), workDir);
            Assert.Contains("daxops_pbiengine_", Path.GetFileName(workDir));
            Assert.True(Directory.Exists(workDir));
        }
        Assert.False(Directory.Exists(workDir));
    }

    [Fact]
    public void Workdir_honours_a_caller_supplied_root_and_dispose_never_touches_the_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "sbi_engineroot_" + Guid.NewGuid().ToString("N"));
        try
        {
            string workDir;
            using (var engine = new PbiEngine("unused.exe", root))
            {
                workDir = engine.WorkDir;
                Assert.Equal(root, Path.GetDirectoryName(workDir));
                Assert.True(Directory.Exists(workDir));
            }
            Assert.False(Directory.Exists(workDir));
            Assert.True(Directory.Exists(root));   // only the GUID workdir is deleted, never the root
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Workdir_falls_back_to_the_system_temp_for_a_blank_root()
    {
        using var engine = new PbiEngine("unused.exe", "   ");
        Assert.StartsWith(Path.GetTempPath(), engine.WorkDir);
    }
}
