using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>
/// Wave G4 target=pbip mode for the model generators: create_date_table, add_hierarchy,
/// add_measure, add_calculation_group and add_time_intelligence_measures running ENGINE-FREE
/// against a PBIP semantic model's TMDL folder. The SAME generation logic as the live path
/// (the shared DaxGenerators text and the ModelService *Core mutators) is routed through the
/// TMDL serialiser: deserialize the definition folder, mutate the object tree, re-serialise.
/// Collision checks are the live cores' own; /// doc comments (TMDL descriptions) round-trip
/// through the serialiser. The original definition folder survives as a timestamped .bak sibling
/// and the swap is staged, so a failed write never leaves a half-serialised model.
/// </summary>
public static class OfflineTmdlGenerators
{
    // ---------------------------------------------------------------- the five generators

    public static object CreateDateTable(string pbipFolder, string name, string? dateColumnRef, bool hierarchy)
        => Run(pbipFolder, "create_date_table", model => CreateDateTableCore(model, name, dateColumnRef, hierarchy));

    public static object AddHierarchy(string pbipFolder, string table, string name, string[] levels)
        => Run(pbipFolder, "add_hierarchy", model => AddHierarchyCore(model, table, name, levels));

    public static object AddMeasure(string pbipFolder, string table, string name, string dax,
        string? formatString, string? displayFolder, string? description)
        => Run(pbipFolder, "add_measure", model =>
        {
            ModelService.AddMeasureCore(model, table, name, dax, formatString, displayFolder, description);
            return new { added = $"{table}[{name}]" };
        });

    public static object AddCalculationGroup(string pbipFolder, string table, int? precedence)
        => Run(pbipFolder, "add_calculation_group", model => AddCalculationGroupCore(model, table, precedence));

    public static object AddTimeIntelligenceMeasures(string pbipFolder, string table, string baseMeasure,
        string dateTable, string dateColumn, string? fiscalYearEnd)
        => Run(pbipFolder, "add_time_intelligence_measures", model =>
        {
            var added = ModelService.ApplyMeasureSpecsCore(model,
                DaxGenerators.TimeIntelligenceMeasures(table, baseMeasure, dateTable, dateColumn, fiscalYearEnd));
            return new { added, count = added.Count };
        });

    // ---------------------------------------------------------------- offline cores (pure TOM)

    /// <summary>The offline date table. No engine means no refresh to infer the calculated-table
    /// columns, so they are AUTHORED explicitly (the same shape a Desktop refresh infers - names
    /// and types matching the shared ADDCOLUMNS projection) and the sort-bys / hidden flags /
    /// hierarchy are wired directly. Desktop-faithful TMDL, data materialises on first refresh.</summary>
    internal static object CreateDateTableCore(TOM.Model model, string name, string? dateColumnRef, bool hierarchy)
    {
        if (model.Tables.Contains(name))
            throw new InvalidOperationException($"Table '{name}' already exists. Pick another name or delete it first.");

        var table = new TOM.Table { Name = name };
        table.Partitions.Add(new TOM.Partition
        {
            Name = name,
            Source = new TOM.CalculatedPartitionSource { Expression = DaxGenerators.DateTableDax(dateColumnRef) },
        });

        void Col(string cname, TOM.DataType type, bool hidden = false, string? format = null)
        {
            var c = new TOM.CalculatedTableColumn
            {
                Name = cname,
                SourceColumn = $"[{cname}]",
                DataType = type,
                IsNameInferred = true,
                IsDataTypeInferred = true,
                IsHidden = hidden,
            };
            if (format != null) c.FormatString = format;
            table.Columns.Add(c);
        }
        Col("Date", TOM.DataType.DateTime, format: "dd mmm yyyy");
        Col("Year", TOM.DataType.Int64);
        Col("Quarter", TOM.DataType.String);
        Col("QuarterNo", TOM.DataType.Int64, hidden: true);
        Col("Month", TOM.DataType.String);
        Col("MonthNo", TOM.DataType.Int64, hidden: true);
        Col("MonthYear", TOM.DataType.String);
        Col("YearMonthNo", TOM.DataType.Int64, hidden: true);

        table.Columns["Month"].SortByColumn = table.Columns["MonthNo"];
        table.Columns["Quarter"].SortByColumn = table.Columns["QuarterNo"];
        table.Columns["MonthYear"].SortByColumn = table.Columns["YearMonthNo"];

        if (hierarchy)
        {
            var hy = new TOM.Hierarchy { Name = "Calendar Hierarchy" };
            int ord = 0;
            foreach (var lvl in new[] { "Year", "Quarter", "Month" })
                hy.Levels.Add(new TOM.Level { Name = lvl, Ordinal = ord++, Column = table.Columns[lvl] });
            table.Hierarchies.Add(hy);
        }
        model.Tables.Add(table);
        return new
        {
            created = name,
            columns = 8,
            hierarchy,
            note = "Columns authored explicitly (no engine to infer them offline); the data materialises on the "
                 + "first refresh in Desktop. Relate your fact's date column to " + name + "[Date].",
        };
    }

    /// <summary>Offline add_hierarchy - the live tool's semantics (an existing hierarchy of the
    /// same name is replaced; every level column must exist).</summary>
    internal static object AddHierarchyCore(TOM.Model model, string table, string name, string[] levels)
    {
        var t = model.Tables.Find(table)
                ?? throw new InvalidOperationException($"Table '{table}' not found.");
        if (levels.Length == 0) throw new InvalidOperationException("levels is empty - pass an ordered, comma-separated column list.");
        if (t.Hierarchies.Contains(name)) t.Hierarchies.Remove(name);
        var h = new TOM.Hierarchy { Name = name };
        int ord = 0;
        foreach (var lvl in levels)
        {
            var c = t.Columns.Find(lvl)
                    ?? throw new InvalidOperationException($"Column '{table}[{lvl}]' not found.");
            h.Levels.Add(new TOM.Level { Name = lvl, Ordinal = ord++, Column = c });
        }
        t.Hierarchies.Add(h);
        return new { hierarchy = $"{table}.{name}", levels };
    }

    /// <summary>Offline add_calculation_group. A missing table is CREATED whole (the Desktop shape:
    /// a calculationGroup partition source plus the single string Name column), because offline
    /// there is no prior tool call that could have made it. DiscourageImplicitMeasures is switched
    /// on - calculation groups require it.</summary>
    internal static object AddCalculationGroupCore(TOM.Model model, string table, int? precedence)
    {
        var t = model.Tables.Find(table);
        bool created = false;
        if (t == null)
        {
            t = new TOM.Table { Name = table };
            t.Partitions.Add(new TOM.Partition { Name = table, Source = new TOM.CalculationGroupSource() });
            t.Columns.Add(new TOM.DataColumn { Name = "Name", DataType = TOM.DataType.String, SourceColumn = "Name" });
            model.Tables.Add(t);
            created = true;
        }
        ModelService.AddCalculationGroupCore(model, table, precedence);   // throws when a group already exists
        model.DiscourageImplicitMeasures = true;
        return new { calculationGroup = table, precedence, tableCreated = created, discourageImplicitMeasures = true };
    }

    // ---------------------------------------------------------------- the TMDL round trip

    /// <summary>Deserialize the PBIP/TMDL definition folder, run the generator against the object
    /// tree, and re-serialise IN PLACE via a staged swap: new TMDL lands in a temp sibling, the
    /// original folder becomes the timestamped .bak, the temp moves in. A serialisation failure
    /// puts the original back untouched.</summary>
    internal static object Run(string pbipFolder, string generator, Func<TOM.Model, object> generate)
    {
        string defFolder = ModelPersistService.ResolveDefinitionFolder(pbipFolder)
            ?? throw new InvalidOperationException(
                $"no TMDL definition folder found at '{pbipFolder}' (expected model.tmdl / a definition/ folder, or a <name>.SemanticModel).");

        var model = TOM.TmdlSerializer.DeserializeModelFromFolder(defFolder);
        var detail = generate(model);   // collision checks throw HERE, before anything touches disk

        string baseName = defFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string tmp = baseName + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        TOM.TmdlSerializer.SerializeModelToFolder(model, tmp);

        string backup = baseName + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        int n = 1;
        while (Directory.Exists(backup)) backup = baseName + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (n++);
        Directory.Move(defFolder, backup);
        try { Directory.Move(tmp, defFolder); }
        catch
        {
            Directory.Move(backup, defFolder);   // put the original back - nothing changed
            throw;
        }

        int files = Directory.GetFiles(defFolder, "*.tmdl", SearchOption.AllDirectories).Length;
        return new
        {
            ok = true,
            route = "offline_tmdl",
            target = "pbip",
            generator,
            definitionFolder = defFolder,
            tmdlFiles = files,
            backup,
            detail,
            note = "Engine-free TMDL edit: the whole model was deserialised, mutated and re-serialised, so "
                 + "descriptions (/// doc comments) round-trip and unrelated objects re-emit in canonical TMDL "
                 + "style. The previous definition folder was kept as the backup. Open the PBIP in Desktop and "
                 + "refresh to materialise any new calculated table.",
        };
    }
}
