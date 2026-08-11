using System.IO.Compression;
using System.Text;
using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit + integration tests for the model-persist gap closer (ModelPersistService). No live Analysis
/// Services engine and no Power BI Desktop are needed: the object-tree edits run against an in-memory
/// <c>new TOM.Model()</c>, the TMDL round-trip uses the official (engine-free) serializer, and the pbix
/// backup + atomic DataModel repack run against a hand-built zip. The engine-hosted (ImageLoad/ImageSave) and
/// Desktop (scripted Ctrl+S) routes are exercised out-of-band on a copy; they cannot run in headless CI.
/// </summary>
public sealed class ModelPersistTests
{
    private static ModelPersistService.ModelEdit Edit(string op, Action<ModelPersistService.ModelEdit> set)
    {
        var e = new ModelPersistService.ModelEdit { Op = op };
        set(e);
        return e;
    }

    private static TOM.Model TwoTableModel()
    {
        var model = new TOM.Model();

        var fact = new TOM.Table { Name = "Fact_Sales" };
        fact.Columns.Add(new TOM.DataColumn { Name = "DateKey", DataType = TOM.DataType.Int64, SourceColumn = "DateKey" });
        fact.Columns.Add(new TOM.DataColumn { Name = "Amount", DataType = TOM.DataType.Double, SourceColumn = "Amount" });
        fact.Partitions.Add(new TOM.Partition
        {
            Name = "Fact_Sales",
            Source = new TOM.MPartitionSource { Expression = "let Source = Table.FromRows({}, {\"DateKey\",\"Amount\"}) in Source" },
        });
        fact.Measures.Add(new TOM.Measure { Name = "Total Sales", Expression = "SUM(Fact_Sales[Amount])" });
        model.Tables.Add(fact);

        var dim = new TOM.Table { Name = "Dim_Date" };
        dim.Columns.Add(new TOM.DataColumn { Name = "DateKey", DataType = TOM.DataType.Int64, SourceColumn = "DateKey" });
        dim.Columns.Add(new TOM.DataColumn { Name = "Date", DataType = TOM.DataType.DateTime, SourceColumn = "Date" });
        dim.Partitions.Add(new TOM.Partition
        {
            Name = "Dim_Date",
            Source = new TOM.MPartitionSource { Expression = "let Source = Table.FromRows({}, {\"DateKey\",\"Date\"}) in Source" },
        });
        model.Tables.Add(dim);

        return model;
    }

    // ---------------------------------------------------------------- ParseEdits

    [Fact]
    public void ParseEdits_reads_a_json_array()
    {
        var edits = ModelPersistService.ParseEdits(
            "[{\"op\":\"add_measure\",\"table\":\"Fact_Sales\",\"name\":\"M\",\"dax\":\"1\"}," +
            "{\"op\":\"add_relationship\",\"fromTable\":\"Fact_Sales\",\"fromColumn\":\"DateKey\",\"toTable\":\"Dim_Date\",\"toColumn\":\"DateKey\"}]");
        Assert.Equal(2, edits.Count);
        Assert.Equal("add_measure", edits[0].Op);
        Assert.Equal("Fact_Sales", edits[1].FromTable);
    }

    [Fact]
    public void ParseEdits_reads_a_single_object()
    {
        var edits = ModelPersistService.ParseEdits("{\"op\":\"add_measure\",\"table\":\"T\",\"name\":\"M\",\"dax\":\"1\"}");
        Assert.Single(edits);
        Assert.Equal("M", edits[0].Name);
    }

    [Fact]
    public void ParseEdits_rejects_empty_and_op_less_edits()
    {
        Assert.Throws<InvalidOperationException>(() => ModelPersistService.ParseEdits(""));
        Assert.Throws<InvalidOperationException>(() => ModelPersistService.ParseEdits("[{\"table\":\"T\"}]"));
    }

    // ---------------------------------------------------------------- ApplyEdits (object tree)

    [Fact]
    public void ApplyEdits_add_measure_relationship_and_calc_column()
    {
        var model = TwoTableModel();
        var res = ModelPersistService.ApplyEdits(model, new[]
        {
            Edit("add_measure", e => { e.Table = "Fact_Sales"; e.Name = "Margin %"; e.Dax = "DIVIDE(1,2)"; e.FormatString = "0.0%"; }),
            Edit("add_relationship", e => { e.FromTable = "Fact_Sales"; e.FromColumn = "DateKey"; e.ToTable = "Dim_Date"; e.ToColumn = "DateKey"; }),
            Edit("add_calculated_column", e => { e.Table = "Dim_Date"; e.Name = "IsWeekend"; e.Dax = "WEEKDAY([Date])>5"; }),
        });

        var fact = model.Tables.Find("Fact_Sales")!;
        Assert.True(fact.Measures.Contains("Margin %"));
        Assert.Equal("0.0%", fact.Measures.Find("Margin %")!.FormatString);
        Assert.Single(model.Relationships);
        Assert.True(model.Tables.Find("Dim_Date")!.Columns.Contains("IsWeekend"));
        Assert.IsType<TOM.CalculatedColumn>(model.Tables.Find("Dim_Date")!.Columns.Find("IsWeekend"));
        Assert.True(res.NeedsRecalc);   // a relationship + calc column were added
        Assert.Equal(3, res.Applied.Count);
    }

    [Fact]
    public void ApplyEdits_measure_only_does_not_need_recalc()
    {
        var model = TwoTableModel();
        var res = ModelPersistService.ApplyEdits(model, new[]
        {
            Edit("add_measure", e => { e.Table = "Fact_Sales"; e.Name = "M2"; e.Dax = "1"; }),
        });
        Assert.False(res.NeedsRecalc);
    }

    [Fact]
    public void ApplyEdits_update_and_delete_measure()
    {
        var model = TwoTableModel();
        ModelPersistService.ApplyEdits(model, new[]
        {
            Edit("update_measure", e => { e.Table = "Fact_Sales"; e.Name = "Total Sales"; e.Dax = "SUM(Fact_Sales[Amount]) * 2"; e.FormatString = "#,0"; }),
        });
        Assert.Equal("SUM(Fact_Sales[Amount]) * 2", model.Tables.Find("Fact_Sales")!.Measures.Find("Total Sales")!.Expression);

        ModelPersistService.ApplyEdits(model, new[] { Edit("delete_measure", e => { e.Table = "Fact_Sales"; e.Name = "Total Sales"; }) });
        Assert.False(model.Tables.Find("Fact_Sales")!.Measures.Contains("Total Sales"));
    }

    [Fact]
    public void ApplyEdits_delete_relationship_by_column_pair()
    {
        var model = TwoTableModel();
        ModelPersistService.ApplyEdits(model, new[]
        {
            Edit("add_relationship", e => { e.FromTable = "Fact_Sales"; e.FromColumn = "DateKey"; e.ToTable = "Dim_Date"; e.ToColumn = "DateKey"; }),
        });
        Assert.Single(model.Relationships);
        ModelPersistService.ApplyEdits(model, new[]
        {
            Edit("delete_relationship", e => { e.FromTable = "Fact_Sales"; e.FromColumn = "DateKey"; e.ToTable = "Dim_Date"; e.ToColumn = "DateKey"; }),
        });
        Assert.Empty(model.Relationships);
    }

    // ---------------------------------------------------------------- failure handling

    [Fact]
    public void ApplyEdits_rejects_unknown_table_duplicate_measure_and_unknown_op()
    {
        var model = TwoTableModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelPersistService.ApplyEdits(model, new[] { Edit("add_measure", e => { e.Table = "Nope"; e.Name = "X"; e.Dax = "1"; }) }));
        Assert.Throws<InvalidOperationException>(() =>
            ModelPersistService.ApplyEdits(model, new[] { Edit("add_measure", e => { e.Table = "Fact_Sales"; e.Name = "Total Sales"; e.Dax = "1"; }) }));
        Assert.Throws<InvalidOperationException>(() =>
            ModelPersistService.ApplyEdits(model, new[] { Edit("frobnicate", e => { e.Table = "Fact_Sales"; }) }));
    }

    [Fact]
    public void ApplyEdits_requires_mandatory_fields()
    {
        var model = TwoTableModel();
        Assert.Throws<InvalidOperationException>(() =>
            ModelPersistService.ApplyEdits(model, new[] { Edit("add_measure", e => { e.Table = "Fact_Sales"; e.Name = "X"; /* no dax */ }) }));
        Assert.Throws<InvalidOperationException>(() =>
            ModelPersistService.ApplyEdits(model, new[] { Edit("add_relationship", e => { e.FromTable = "Fact_Sales"; /* missing the rest */ }) }));
    }

    // ---------------------------------------------------------------- TMDL round-trip (export -> edit -> re-import)

    [Fact]
    public void EditTmdlFolder_round_trips_measure_relationship_and_calc_column_through_tmdl()
    {
        string root = Path.Combine(Path.GetTempPath(), "sbi_tmdl_" + Guid.NewGuid().ToString("N"));
        string inFolder = Path.Combine(root, "in");
        string outFolder = Path.Combine(root, "out");
        try
        {
            Directory.CreateDirectory(inFolder);
            TOM.TmdlSerializer.SerializeModelToFolder(TwoTableModel(), inFolder);

            var svc = new ModelPersistService(new SessionStore());
            var result = svc.EditTmdlFolder(inFolder, outFolder, new[]
            {
                Edit("add_measure", e => { e.Table = "Fact_Sales"; e.Name = "Persist Test"; e.Dax = "COUNTROWS(Fact_Sales)"; e.FormatString = "#,0"; }),
                Edit("add_relationship", e => { e.FromTable = "Fact_Sales"; e.FromColumn = "DateKey"; e.ToTable = "Dim_Date"; e.ToColumn = "DateKey"; }),
                Edit("add_calculated_column", e => { e.Table = "Dim_Date"; e.Name = "IsWeekend"; e.Dax = "WEEKDAY([Date])>5"; }),
            });

            // capability statement: structure route is honest about NOT preserving data
            var bag = result.GetType().GetProperties().ToDictionary(p => p.Name, p => p.GetValue(result));
            Assert.Equal(false, bag["dataPreserved"]);
            Assert.Equal("offline_tmdl", bag["route"]);
            Assert.Contains("STRUCTURE", (string)bag["capability"]!, StringComparison.OrdinalIgnoreCase);

            // re-import the edited TMDL (engine-free) and confirm the edits round-tripped
            var reloaded = TOM.TmdlSerializer.DeserializeModelFromFolder(
                ModelPersistService.ResolveDefinitionFolder(outFolder)!);
            Assert.True(reloaded.Tables.Find("Fact_Sales")!.Measures.Contains("Persist Test"));
            Assert.Single(reloaded.Relationships);
            var rel = reloaded.Relationships.OfType<TOM.SingleColumnRelationship>().Single();
            Assert.Equal("Fact_Sales", rel.FromTable.Name);
            Assert.Equal("Dim_Date", rel.ToTable.Name);
            Assert.IsType<TOM.CalculatedColumn>(reloaded.Tables.Find("Dim_Date")!.Columns.Find("IsWeekend"));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    // ---------------------------------------------------------------- backup + atomic DataModel repack

    private static string BuildFakePbix(string dir, byte[] dataModel)
    {
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "fake.pbix");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        void Put(string name, byte[] bytes) { using var s = zip.CreateEntry(name).Open(); s.Write(bytes, 0, bytes.Length); }
        Put("Version", Encoding.Unicode.GetBytes("1.28"));
        Put("DataModel", dataModel);
        Put("Report/Layout", Encoding.Unicode.GetBytes("{\"sections\":[]}"));
        Put("SecurityBindings", new byte[] { 1, 2, 3 });
        return path;
    }

    [Fact]
    public void RepackDataModel_swaps_datamodel_preserves_parts_and_drops_signature()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sbi_repack_" + Guid.NewGuid().ToString("N"));
        try
        {
            string pbix = BuildFakePbix(dir, Encoding.UTF8.GetBytes("OLD-DATAMODEL"));
            byte[] fresh = Encoding.UTF8.GetBytes("NEW-DATAMODEL-BYTES");

            ModelPersistService.RepackDataModel(pbix, fresh);

            using var zip = ZipFile.OpenRead(pbix);
            Assert.Equal("NEW-DATAMODEL-BYTES", ReadEntry(zip, "DataModel"));
            Assert.Equal("1.28", new UnicodeEncoding(false, false).GetString(ReadEntryBytes(zip, "Version")));
            Assert.NotNull(zip.GetEntry("Report/Layout"));
            Assert.Null(zip.GetEntry("SecurityBindings"));   // signature dropped
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void Backup_makes_an_additive_copy_without_touching_the_original()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sbi_backup_" + Guid.NewGuid().ToString("N"));
        try
        {
            string pbix = BuildFakePbix(dir, Encoding.UTF8.GetBytes("DM"));
            long before = new FileInfo(pbix).Length;
            string bak = ModelPersistService.Backup(pbix);
            Assert.True(File.Exists(bak));
            Assert.StartsWith(pbix + ".bak-", bak);
            Assert.Equal(before, new FileInfo(pbix).Length);   // original untouched
            Assert.Equal(before, new FileInfo(bak).Length);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    private static string ReadEntry(ZipArchive zip, string name) => Encoding.UTF8.GetString(ReadEntryBytes(zip, name));

    private static byte[] ReadEntryBytes(ZipArchive zip, string name)
    {
        using var s = zip.GetEntry(name)!.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
