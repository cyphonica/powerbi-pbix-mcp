using System.Text.Json;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Wave G4 offline (target=pbip) generators: the engine-free cores over an in-memory TOM model
/// (date table with authored columns / sort-bys / hierarchy, calc-group table creation, hierarchy
/// replace semantics, collision checks), and the full TMDL round trip - deserialize, generate,
/// re-serialise with the original folder kept as the backup and descriptions (/// doc comments)
/// preserved. No engine anywhere.
/// </summary>
public sealed class WaveG4OfflineGeneratorTests
{
    // ---------------------------------------------------------------- fixtures

    private static TOM.Model SeedModel()
    {
        var model = new TOM.Model();
        var fact = new TOM.Table { Name = "Fact_Sales", Description = "The sales fact." };
        fact.Columns.Add(new TOM.DataColumn { Name = "DateKey", DataType = TOM.DataType.Int64, SourceColumn = "DateKey" });
        fact.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        fact.Columns.Add(new TOM.DataColumn { Name = "Segment", DataType = TOM.DataType.String, SourceColumn = "Segment" });
        fact.Partitions.Add(new TOM.Partition
        {
            Name = "Fact_Sales",
            Source = new TOM.MPartitionSource { Expression = "let Source = Table.FromRows({}, {\"DateKey\",\"Amount\",\"Segment\"}) in Source" },
        });
        fact.Measures.Add(new TOM.Measure
        {
            Name = "Total Sales", Expression = "SUM(Fact_Sales[Amount])", Description = "The headline total.",
        });
        model.Tables.Add(fact);
        return model;
    }

    private static JsonObject Obj(object result) => (JsonObject)JsonSerializer.SerializeToNode(result)!;

    // ================================================================ offline cores (pure TOM)

    [Fact]
    public void CreateDateTable_Offline_AuthorsColumns_SortBys_Hidden_Hierarchy()
    {
        var model = SeedModel();
        var r = Obj(OfflineTmdlGenerators.CreateDateTableCore(model, "Calendar", "Fact_Sales[DateKey]", hierarchy: true));
        Assert.Equal("Calendar", (string)r["created"]!);

        var t = model.Tables["Calendar"];
        Assert.Equal(8, t.Columns.Count);
        // the shared DAX text drives the partition - the same generator the live path uses
        var dax = ((TOM.CalculatedPartitionSource)t.Partitions[0].Source).Expression;
        Assert.Contains("CALENDAR(MIN(Fact_Sales[DateKey]), MAX(Fact_Sales[DateKey]))", dax);
        Assert.Equal(DaxGenerators.DateTableDax("Fact_Sales[DateKey]"), dax);

        Assert.Equal("MonthNo", t.Columns["Month"].SortByColumn.Name);
        Assert.Equal("QuarterNo", t.Columns["Quarter"].SortByColumn.Name);
        Assert.Equal("YearMonthNo", t.Columns["MonthYear"].SortByColumn.Name);
        Assert.True(t.Columns["MonthNo"].IsHidden);
        Assert.True(t.Columns["QuarterNo"].IsHidden);
        Assert.True(t.Columns["YearMonthNo"].IsHidden);
        Assert.Equal("dd mmm yyyy", t.Columns["Date"].FormatString);
        // authored the way a Desktop refresh would infer them
        Assert.All(t.Columns.Cast<TOM.Column>(), c => Assert.IsType<TOM.CalculatedTableColumn>(c));
        Assert.Equal(new[] { "Year", "Quarter", "Month" },
            t.Hierarchies["Calendar Hierarchy"].Levels.OrderBy(l => l.Ordinal).Select(l => l.Name).ToArray());

        // collision-checked append
        Assert.Throws<InvalidOperationException>(() =>
            OfflineTmdlGenerators.CreateDateTableCore(model, "Calendar", null, hierarchy: false));
    }

    [Fact]
    public void AddCalculationGroup_Offline_CreatesMissingTableWhole_AndRefusesADuplicateGroup()
    {
        var model = SeedModel();
        var r = Obj(OfflineTmdlGenerators.AddCalculationGroupCore(model, "Time Intelligence", precedence: 10));
        Assert.True((bool)r["tableCreated"]!);

        var t = model.Tables["Time Intelligence"];
        Assert.NotNull(t.CalculationGroup);
        Assert.Equal(10, t.CalculationGroup.Precedence);
        Assert.IsType<TOM.CalculationGroupSource>(t.Partitions[0].Source);
        Assert.Equal("Name", t.Columns[0].Name);
        Assert.True(model.DiscourageImplicitMeasures);   // calc groups require it

        // the group already exists - the live core's collision check fires
        Assert.Throws<InvalidOperationException>(() =>
            OfflineTmdlGenerators.AddCalculationGroupCore(model, "Time Intelligence", null));

        // an EXISTING table is converted, not recreated
        var r2 = Obj(OfflineTmdlGenerators.AddCalculationGroupCore(model, "Fact_Sales", null));
        Assert.False((bool)r2["tableCreated"]!);
        Assert.NotNull(model.Tables["Fact_Sales"].CalculationGroup);
    }

    [Fact]
    public void AddHierarchy_Offline_ReplacesExisting_AndRequiresRealColumns()
    {
        var model = SeedModel();
        Obj(OfflineTmdlGenerators.AddHierarchyCore(model, "Fact_Sales", "Drill", new[] { "Segment", "Amount" }));
        Assert.Equal(2, model.Tables["Fact_Sales"].Hierarchies["Drill"].Levels.Count);

        // same-name hierarchy is REPLACED (the live tool's semantics)
        Obj(OfflineTmdlGenerators.AddHierarchyCore(model, "Fact_Sales", "Drill", new[] { "Segment" }));
        Assert.Single(model.Tables["Fact_Sales"].Hierarchies["Drill"].Levels);

        Assert.Throws<InvalidOperationException>(() =>
            OfflineTmdlGenerators.AddHierarchyCore(model, "Fact_Sales", "Bad", new[] { "Nope" }));
        Assert.Throws<InvalidOperationException>(() =>
            OfflineTmdlGenerators.AddHierarchyCore(model, "Missing", "Bad", new[] { "Segment" }));
    }

    // ================================================================ the TMDL round trip

    [Fact]
    public void Run_TmdlRoundTrip_AppendsMeasure_KeepsDescriptions_LeavesBackup()
    {
        string root = Path.Combine(Path.GetTempPath(), "sbi_g4_tmdl_" + Guid.NewGuid().ToString("N"));
        string def = Path.Combine(root, "Model.SemanticModel", "definition");
        try
        {
            Directory.CreateDirectory(def);
            TOM.TmdlSerializer.SerializeModelToFolder(SeedModel(), def);

            var r = Obj(OfflineTmdlGenerators.AddMeasure(root, "Fact_Sales", "Margin %",
                "DIVIDE([Profit], [Total Sales])", "0.0%", "Ratios", "Margin over sales."));

            Assert.True((bool)r["ok"]!);
            Assert.Equal("offline_tmdl", (string)r["route"]!);
            Assert.Equal("pbip", (string)r["target"]!);
            string backup = (string)r["backup"]!;
            Assert.True(Directory.Exists(backup), "the original definition folder must survive as the backup");

            // reload the re-serialised model: the new measure landed AND the descriptions round-tripped
            var reloaded = TOM.TmdlSerializer.DeserializeModelFromFolder(def);
            var fact = reloaded.Tables["Fact_Sales"];
            Assert.Equal("DIVIDE([Profit], [Total Sales])", fact.Measures["Margin %"].Expression);
            Assert.Equal("0.0%", fact.Measures["Margin %"].FormatString);
            Assert.Equal("Margin over sales.", fact.Measures["Margin %"].Description);
            Assert.Equal("The headline total.", fact.Measures["Total Sales"].Description);
            Assert.Equal("The sales fact.", fact.Description);

            // the backup folder still holds the PRE-edit model
            var backedUp = TOM.TmdlSerializer.DeserializeModelFromFolder(backup);
            Assert.Null(backedUp.Tables["Fact_Sales"].Measures.Find("Margin %"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Run_CollisionThrowsBeforeAnythingTouchesDisk()
    {
        string root = Path.Combine(Path.GetTempPath(), "sbi_g4_tmdl_" + Guid.NewGuid().ToString("N"));
        string def = Path.Combine(root, "definition");
        try
        {
            Directory.CreateDirectory(def);
            TOM.TmdlSerializer.SerializeModelToFolder(SeedModel(), def);
            var before = Directory.GetFiles(def, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, File.GetLastWriteTimeUtc);

            // duplicate measure name - the shared AddMeasureCore collision check fires
            Assert.Throws<InvalidOperationException>(() =>
                OfflineTmdlGenerators.AddMeasure(root, "Fact_Sales", "Total Sales", "1", null, null, null));

            // nothing was written, moved or backed up
            var after = Directory.GetFiles(def, "*", SearchOption.AllDirectories)
                .ToDictionary(f => f, File.GetLastWriteTimeUtc);
            Assert.Equal(before, after);
            Assert.Empty(Directory.GetDirectories(root).Where(d => d.Contains(".bak-")));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Run_TimeIntelligence_Offline_WritesTheFullSet()
    {
        string root = Path.Combine(Path.GetTempPath(), "sbi_g4_tmdl_" + Guid.NewGuid().ToString("N"));
        string def = Path.Combine(root, "definition");
        try
        {
            Directory.CreateDirectory(def);
            var seed = SeedModel();
            var cal = new TOM.Table { Name = "Calendar" };
            cal.Columns.Add(new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, SourceColumn = "Date" });
            cal.Partitions.Add(new TOM.Partition
            {
                Name = "Calendar",
                Source = new TOM.MPartitionSource { Expression = "let Source = Table.FromRows({}, {\"Date\"}) in Source" },
            });
            seed.Tables.Add(cal);
            TOM.TmdlSerializer.SerializeModelToFolder(seed, def);

            var r = Obj(OfflineTmdlGenerators.AddTimeIntelligenceMeasures(root, "Fact_Sales", "Total Sales",
                "Calendar", "Date", fiscalYearEnd: null));
            Assert.True((bool)r["ok"]!);
            Assert.Equal(12, (int)r["detail"]!["count"]!);

            var reloaded = TOM.TmdlSerializer.DeserializeModelFromFolder(def);
            var fact = reloaded.Tables["Fact_Sales"];
            foreach (var name in new[] { "Total Sales YTD", "Total Sales QTD", "Total Sales MTD", "Total Sales PY",
                         "Total Sales YoY %", "Total Sales PYTD" })
                Assert.NotNull(fact.Measures.Find(name));
            // the same DaxGenerators text as the live path
            Assert.Contains("DATESYTD ( 'Calendar'[Date] )", fact.Measures["Total Sales YTD"].Expression);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Run_MissingDefinitionFolder_Throws()
    {
        string root = Path.Combine(Path.GetTempPath(), "sbi_g4_tmdl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                OfflineTmdlGenerators.AddMeasure(root, "T", "M", "1", null, null, null));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
