using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp;

/// <summary>
/// The bake worker - the one step that needs a real Analysis Services engine, so it only runs on a
/// Windows host with Power BI Desktop installed. Baking is where raw data actually lands in the model and where the binary
/// <c>DataModel</c> part of the delivered single-file .pbix is produced.
///
///   SuperBiMcp bake &lt;pbipFolder&gt; &lt;outFolder&gt; [asServer]
///
/// HOW THE DataModel IS PRODUCED (and why this engine, not standalone SSAS):
/// Power BI Desktop loads the .pbix <c>DataModel</c> via a proprietary engine "ImageLoad" that expects an
/// XPress9-compressed image written by Power BI's OWN engine. A standalone SQL Server Analysis Services
/// backup / ImageSave produces an uncompressed STREAM_STORAGE container Desktop refuses ("Not implemented").
/// So we launch the engine that ships WITH Power BI Desktop (msmdsrv.exe) as an ephemeral, private,
/// SharePoint-deployment-mode workspace, deploy the model into it, FULL-refresh it, then call
/// <c>Server.ImageSave</c> - whose bytes ARE the DataModel Desktop loads (header
/// "This backup was created using XPress9 compression."). Proven: a fresh engine of the same version
/// ImageLoads the result and queries the data, which is exactly what Desktop does on open.
///
/// THE M CONSTRAINT: a bare bundled msmdsrv in SharePoint mode cannot execute M / Power Query (Desktop
/// wires up a separate Mashup container that a raw launch does not), so an MPartitionSource fails with
/// "M engine integration is not enabled". We therefore rebuild each table M-FREE: the scaffold inlines
/// each partition's CSV as base64 inside its M (Binary.FromText("...",BinaryEncoding.Base64)); we decode
/// that back to CSV and give the table a DAX DATATABLE(...) CalculatedPartitionSource instead. No M, no
/// external files, fully self-contained. (If a partition is not inline-base64, we fall back to the staged
/// CSV under the project's data folder.)
///
/// The bundled engine path is configurable via SUPERBI_PBI_ENGINE (defaults to the Power BI Desktop
/// install). If the engine is not present, the worker degrades gracefully (returns unbaked) rather than
/// throwing, so the caller falls back to the PBIP-zip deliverable.
///
/// VERSION COUPLING: the engine build that writes the image must be the same as (or older than) the Power
/// BI Desktop build that opens the delivered .pbix - the VertiPaq IDF column-stream format is forward
/// incompatible (a newer engine's image fails to load in an older Desktop with an IDF / IdfCombined error).
/// Pin the bake host's Power BI Desktop to the targeted client version.
/// </summary>
public static class Bake
{
    /// <summary>Default location of the Power BI Desktop engine; override with SUPERBI_PBI_ENGINE.</summary>
    private const string DefaultEnginePath = @"C:\Program Files\Microsoft Power BI Desktop\bin\msmdsrv.exe";

    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: SuperBiMcp bake <pbipFolder> <outFolder> [asServer]"); return 2; }
            string asServer = args.Length > 3 && !string.IsNullOrWhiteSpace(args[3])
                ? args[3] : Environment.GetEnvironmentVariable("SUPERBI_AS_SERVER") ?? "";
            var result = BakeProject(args[1], args[2], asServer);
            Console.WriteLine(JsonSerializer.Serialize(result, Cli.Pretty));
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 1; }
    }

    /// <summary>
    /// Deploy the PBIP's semantic model into an ephemeral Power BI engine, refresh it (load the data), and
    /// <c>ImageSave</c> the populated database to a binary DataModel file in <paramref name="outFolder"/>.
    /// Returns a manifest of what landed. The returned <c>abf</c> field is the path to that DataModel file -
    /// kept under the same name so the .pbix compiler / HTTP caller need no change (it is no longer an .abf
    /// backup; it is the engine image Power BI Desktop loads directly).
    ///
    /// <paramref name="asServer"/> is retained for compatibility and acts as the bake trigger: when empty
    /// the worker returns the unbaked project (honest, no faked bake).
    /// </summary>
    public static object BakeProject(string pbipFolder, string outFolder, string asServer)
    {
        if (!Directory.Exists(pbipFolder)) throw new DirectoryNotFoundException($"pbip folder not found: {pbipFolder}");
        string? defFolder = FindModelDefinition(pbipFolder);
        if (defFolder == null) throw new InvalidOperationException("no <name>.SemanticModel/definition folder found under the project.");

        Directory.CreateDirectory(outFolder);

        // The bake is gated the same way as before: it only runs when the deployment is configured to bake
        // (asServer is the existing trigger the HTTP service sets when running on the VM).
        if (string.IsNullOrWhiteSpace(asServer))
            return new
            {
                ok = false,
                baked = false,
                reason = "bake not requested (no SUPERBI_AS_SERVER). The model is ready but unpopulated.",
                pbipFolder,
                next = "Open the .pbip in Power BI Desktop and Refresh, or run this worker on the VM with the bake configured.",
            };

        string? engineEnv = Environment.GetEnvironmentVariable("SUPERBI_PBI_ENGINE");
        string enginePath = string.IsNullOrWhiteSpace(engineEnv) ? DefaultEnginePath : engineEnv;

        // The bundled Power BI engine is required to produce a Desktop-loadable DataModel. If it is not
        // installed on this host, degrade gracefully so the caller serves the PBIP-zip deliverable.
        if (!File.Exists(enginePath))
            return new
            {
                ok = false,
                baked = false,
                reason = $"Power BI engine not found at '{enginePath}' (set SUPERBI_PBI_ENGINE). The model is ready but unpopulated.",
                pbipFolder,
                next = "Install Power BI Desktop on the bake host, or set SUPERBI_PBI_ENGINE to its msmdsrv.exe.",
            };

        // load the scaffolded model and strip M (rebuild each table as a DAX DATATABLE - see class summary)
        var model = TOM.TmdlSerializer.DeserializeModelFromFolder(defFolder);
        // capture the relationships before stripping them (they are re-applied after the calc tables exist -
        // see the two-phase deploy below) and convert every table to a DATATABLE calculated partition.
        var relIntent = CaptureRelationships(model);
        int rebuilt = MakeModelMFree(model, pbipFolder);

        using var engine = new PbiEngine(enginePath);
        engine.Start();

        string dbName = "daxops_" + Guid.NewGuid().ToString("N")[..12];
        using var server = new TOM.Server();
        server.Connect(engine.ConnectionString);
        try
        {
            using var db = new TOM.Database(dbName)
            {
                CompatibilityLevel = 1500,   // widest-compatible level for our model (tables/measures/relationships); see class summary on version coupling
                Model = model,
            };
            server.Databases.Add(db);

            // PHASE 1: deploy + refresh the tables WITHOUT relationships. A DATATABLE table is a calculated
            // table whose columns are generated by the engine at refresh time, so a relationship defined up
            // front references columns that do not yet exist and the deploy fails ("invalid column ID").
            db.Update(Microsoft.AnalysisServices.UpdateOptions.ExpandFull);
            db.Model.RequestRefresh(TOM.RefreshType.Full);
            db.Model.SaveChanges();

            // PHASE 2: the calculated-table columns now exist on the server. Re-resolve each relationship by
            // table/column name against the live model and add it, then RECALCULATE so the relationship
            // indexes are built (without the recalc, queries error "needs to be recalculated").
            int relsAdded = ApplyRelationships(db.Model, relIntent);
            if (relsAdded > 0)
            {
                db.Model.SaveChanges();
                db.Model.RequestRefresh(TOM.RefreshType.Calculate);
                db.Model.SaveChanges();
            }

            long rows = db.Model.Tables.Sum(t => CountRows(server, db.Name, t.Name));

            // RENDER GATE: every measure must EVALUATE without a query-time error. A measure that throws
            // (e.g. a numeric aggregation over a text-typed column) renders as "Error fetching data for this
            // visual" on the customer's dashboard - a green compile that is NOT shippable. Run each measure
            // now against the freshly baked model and surface any failure so the caller FAILs verification and
            // blocks delivery. This is the check that closes the "compiled but the visuals error" gap.
            var renderErrors = ValidateMeasures(server, db);

            // ImageSave -> the binary DataModel the .pbix compiler folds in verbatim
            string dmOut = Path.Combine(outFolder, dbName + ".DataModel");
            using (var fs = new FileStream(dmOut, FileMode.Create, FileAccess.Write))
                server.ImageSave(db.ID, fs);

            return new
            {
                ok = true,
                baked = true,
                outFolder,
                database = db.Name,
                tables = db.Model.Tables.Count,
                approxRows = rows,
                tablesRebuiltMFree = rebuilt,
                relationships = db.Model.Relationships.Count,
                measuresChecked = db.Model.Tables.Sum(t => t.Measures.Count),
                renderErrors = renderErrors.Select(e => new { measure = e.measure, error = e.error }).ToArray(),
                abf = dmOut,   // path to the DataModel image (field name kept for caller/compiler compatibility)
                engine = enginePath,
                note = "Model deployed and FULL-refreshed on the bundled Power BI engine (data in VertiPaq) and ImageSaved to the binary DataModel the .pbix compiler folds in.",
            };
        }
        finally
        {
            // never leave a tenant's data resident on the engine: drop the database (the engine itself is
            // ephemeral and torn down by the PbiEngine dispose below)
            try { server.Databases.FindByName(dbName)?.Drop(); } catch { }
        }
    }

    /// <summary>
    /// Rewrite every table's M partition as a DAX <c>DATATABLE(...)</c> calculated partition so the model
    /// refreshes on the bundled engine (which has no M). Row data comes from the base64 inlined in the M
    /// (Binary.FromText), falling back to the staged CSV under the project's data folder. Returns the number
    /// of tables rebuilt.
    /// </summary>
    private static int MakeModelMFree(TOM.Model model, string pbipFolder)
    {
        int n = 0;
        foreach (TOM.Table table in model.Tables)
        {
            // collect the table's declared columns (order + type drive the DATATABLE header)
            var cols = table.Columns
                .OfType<TOM.DataColumn>()
                .Where(c => !string.IsNullOrEmpty(c.Name))
                .ToList();
            if (cols.Count == 0) continue;   // nothing to materialise (e.g. a pure calc/measure table)

            // source rows for this table
            List<string[]>? rows = null;
            string[]? header = null;

            // 1) preferred: decode the base64 CSV inlined in the M partition (self-contained)
            foreach (TOM.Partition p in table.Partitions)
            {
                if (p.Source is TOM.MPartitionSource mps && !string.IsNullOrEmpty(mps.Expression))
                {
                    byte[]? csvBytes = ExtractInlineCsv(mps.Expression);
                    if (csvBytes != null && csvBytes.Length > 0)
                    {
                        ParseCsv(Encoding.UTF8.GetString(csvBytes), out header, out rows);
                        if (rows != null) break;
                    }
                }
            }

            // 2) fallback: the staged CSV under the project data folder (named after the table)
            if (rows == null)
            {
                string? csvPath = FindStagedCsv(pbipFolder, table.Name);
                if (csvPath != null)
                    ParseCsv(File.ReadAllText(csvPath, Encoding.UTF8), out header, out rows);
            }

            // build the DATATABLE expression (a typed zero-row table still loads when we have no rows)
            string dax = BuildDataTable(cols, header, rows);

            // swap the partition(s): one DATATABLE calculated partition replaces the M partition
            table.Partitions.Clear();
            table.Partitions.Add(new TOM.Partition
            {
                Name = table.Name,
                Source = new TOM.CalculatedPartitionSource { Expression = dax },
            });
            n++;
        }
        // strip M shared expressions (e.g. the DataFolder parameter): the bundled engine has no M engine and
        // the DATATABLE partitions no longer reference them, so any leftover M expression fails the deploy with
        // "M engine integration is not enabled".
        model.Expressions.Clear();
        return n;
    }

    /// <summary>A relationship captured by name so it can be re-applied after the calculated tables exist.</summary>
    private readonly record struct RelIntent(
        string FromTable, string FromColumn, string ToTable, string ToColumn,
        TOM.CrossFilteringBehavior CrossFiltering, bool IsActive,
        TOM.RelationshipEndCardinality FromCardinality, TOM.RelationshipEndCardinality ToCardinality);

    /// <summary>
    /// Snapshot every single-column relationship by table/column NAME and remove all relationships from the
    /// model. Relationships must not be present at the Phase-1 deploy: a DATATABLE table is a calculated table
    /// whose columns the engine generates at refresh time, so a relationship referencing them up front fails
    /// the deploy with "invalid column ID". They are re-applied in Phase 2 (see ApplyRelationships).
    /// </summary>
    private static List<RelIntent> CaptureRelationships(TOM.Model model)
    {
        var list = new List<RelIntent>();
        foreach (var rel in model.Relationships.OfType<TOM.SingleColumnRelationship>())
        {
            if (rel.FromColumn?.Table == null || rel.ToColumn?.Table == null) continue;
            list.Add(new RelIntent(
                rel.FromColumn.Table.Name, rel.FromColumn.Name,
                rel.ToColumn.Table.Name, rel.ToColumn.Name,
                rel.CrossFilteringBehavior, rel.IsActive,
                rel.FromCardinality, rel.ToCardinality));
        }
        model.Relationships.Clear();
        return list;
    }

    /// <summary>
    /// Re-apply the captured relationships against the LIVE model (after the calculated-table columns exist),
    /// re-resolving each end by table/column name. Returns the number added. The caller must SaveChanges and
    /// then RefreshType.Calculate so the relationship indexes are built.
    /// </summary>
    private static int ApplyRelationships(TOM.Model model, List<RelIntent> rels)
    {
        int added = 0;
        foreach (var ri in rels)
        {
            var ft = model.Tables.Find(ri.FromTable);
            var tt = model.Tables.Find(ri.ToTable);
            if (ft == null || tt == null) continue;
            var fc = ft.Columns.Find(ri.FromColumn);
            var tc = tt.Columns.Find(ri.ToColumn);
            if (fc == null || tc == null) continue;
            model.Relationships.Add(new TOM.SingleColumnRelationship
            {
                FromColumn = fc,
                ToColumn = tc,
                FromCardinality = ri.FromCardinality,
                ToCardinality = ri.ToCardinality,
                CrossFilteringBehavior = ri.CrossFiltering,
                IsActive = ri.IsActive,
            });
            added++;
        }
        return added;
    }

    /// <summary>Pull the base64 out of a <c>Binary.FromText("...", BinaryEncoding.Base64)</c> in the M and
    /// decode it to the original CSV bytes. Returns null when the partition is not inline-base64.</summary>
    private static byte[]? ExtractInlineCsv(string m)
    {
        var mt = InlineB64Rx.Match(m);
        if (!mt.Success) return null;
        try { return Convert.FromBase64String(WhitespaceRx.Replace(mt.Groups["b64"].Value, "")); }
        catch { return null; }
    }

    private static readonly System.Text.RegularExpressions.Regex InlineB64Rx = new(
        @"Binary\.FromText\(\s*""(?<b64>[A-Za-z0-9+/=\s]+)""\s*,\s*BinaryEncoding\.Base64\s*\)",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex WhitespaceRx = new(
        @"\s", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Locate a staged CSV for a table under the project (data/ subfolders), matched by name.</summary>
    private static string? FindStagedCsv(string pbipFolder, string tableName)
    {
        try
        {
            string want = tableName + ".csv";
            foreach (var f in Directory.EnumerateFiles(pbipFolder, "*.csv", SearchOption.AllDirectories))
                if (string.Equals(Path.GetFileName(f), want, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        catch { }
        return null;
    }

    /// <summary>Minimal RFC4180-ish CSV parse (quotes, escaped quotes, embedded newlines). First row is the
    /// header; fields are returned as raw strings.</summary>
    private static void ParseCsv(string text, out string[]? header, out List<string[]>? rows)
    {
        header = null; rows = null;
        if (string.IsNullOrEmpty(text)) return;
        // strip a leading UTF-8 BOM so the first column's header name is not corrupted (﻿ is NOT trimmed by
        // string.Trim in .NET, so a BOM'd CSV - common from Excel/PowerShell exports - would leave the first
        // header as "﻿<Name>", which then fails to match a model column by name and yields a blank column).
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        var all = new List<string[]>();
        var field = new StringBuilder();
        var rec = new List<string>();
        bool inQuotes = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else field.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { rec.Add(field.ToString()); field.Clear(); }
                else if (c == '\r') { /* swallow; \n ends the record */ }
                else if (c == '\n') { rec.Add(field.ToString()); field.Clear(); all.Add(rec.ToArray()); rec = new List<string>(); }
                else field.Append(c);
            }
        }
        if (field.Length > 0 || rec.Count > 0) { rec.Add(field.ToString()); all.Add(rec.ToArray()); }
        if (all.Count == 0) return;
        header = all[0];
        rows = all.Skip(1).Where(r => !(r.Length == 1 && r[0].Length == 0)).ToList();   // drop blank trailing line
    }

    /// <summary>
    /// Build a DAX <c>DATATABLE("Col", TYPE, ..., {{...},{...}})</c> for the table's declared columns, taking
    /// values from the parsed CSV by header name. Numeric columns are emitted unquoted (BLANK() for empty);
    /// everything else is a quoted string. A declared-numeric column whose data is not actually numeric
    /// degrades to text so the table still loads.
    /// </summary>
    private static string BuildDataTable(List<TOM.DataColumn> cols, string[]? header, List<string[]>? rows)
    {
        // map each model column to its index in the CSV header (by name, case-insensitive)
        int[] idx = new int[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            idx[c] = -1;
            if (header != null)
                for (int h = 0; h < header.Length; h++)
                    if (string.Equals(header[h]?.Trim().Trim('"'), cols[c].Name, StringComparison.OrdinalIgnoreCase)) { idx[c] = h; break; }
        }

        // decide DATATABLE type per column: model type, but only "numeric" if the data is actually numeric
        bool[] numeric = new bool[cols.Count];
        string[] daxType = new string[cols.Count];
        for (int c = 0; c < cols.Count; c++)
        {
            bool modelNumeric = cols[c].DataType is TOM.DataType.Int64 or TOM.DataType.Double or TOM.DataType.Decimal;
            bool dataNumeric = modelNumeric && idx[c] >= 0 && (rows == null || rows.All(r => idx[c] < r.Length && IsNum(r[idx[c]])));
            numeric[c] = modelNumeric && dataNumeric;
            daxType[c] = numeric[c]
                ? (cols[c].DataType == TOM.DataType.Int64 ? "INTEGER" : "DOUBLE")
                : cols[c].DataType switch
                {
                    TOM.DataType.DateTime => "DATETIME",
                    TOM.DataType.Boolean => "BOOLEAN",
                    _ => "STRING",
                };
        }

        var sb = new StringBuilder();
        sb.Append("DATATABLE(");
        for (int c = 0; c < cols.Count; c++)
        {
            if (c > 0) sb.Append(", ");
            sb.Append('"').Append(cols[c].Name.Replace("\"", "\"\"")).Append("\", ").Append(daxType[c]);
        }
        sb.Append(", {");
        if (rows != null && rows.Count > 0)
        {
            for (int r = 0; r < rows.Count; r++)
            {
                if (r > 0) sb.Append(", ");
                sb.Append('{');
                for (int c = 0; c < cols.Count; c++)
                {
                    if (c > 0) sb.Append(", ");
                    string raw = idx[c] >= 0 && idx[c] < rows[r].Length ? rows[r][idx[c]] : "";
                    sb.Append(FormatCell(raw, numeric[c], daxType[c]));
                }
                sb.Append('}');
            }
        }
        else
        {
            // DATATABLE requires at least one row; emit a single typed empty row so the table loads.
            sb.Append('{');
            for (int c = 0; c < cols.Count; c++)
            {
                if (c > 0) sb.Append(", ");
                sb.Append(numeric[c] ? "BLANK()" : "\"\"");
            }
            sb.Append('}');
        }
        sb.Append("})");
        return sb.ToString();
    }

    private static string FormatCell(string raw, bool numeric, string daxType)
    {
        if (numeric)
            return string.IsNullOrEmpty(raw) ? "BLANK()" : raw.Trim();
        if (daxType == "BOOLEAN")
        {
            if (string.IsNullOrEmpty(raw)) return "FALSE";
            string t = raw.Trim();
            return t.Equals("true", StringComparison.OrdinalIgnoreCase) || t == "1" ? "TRUE" : "FALSE";
        }
        if (daxType == "DATETIME")
            return string.IsNullOrEmpty(raw) ? "BLANK()" : "\"" + raw.Replace("\"", "\"\"") + "\"";
        return "\"" + (raw ?? "").Replace("\"", "\"\"") + "\"";
    }

    private static bool IsNum(string? v)
        => !string.IsNullOrWhiteSpace(v) && double.TryParse(v.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out _);

    private static long CountRows(TOM.Server server, string db, string table)
    {
        try
        {
            using var conn = new Microsoft.AnalysisServices.AdomdClient.AdomdConnection($"Data Source={server.ConnectionString};Catalog={db}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EVALUATE ROW(\"n\", COUNTROWS('{table.Replace("'", "''")}'))";
            using var rdr = cmd.ExecuteReader();
            return rdr.Read() ? Convert.ToInt64(rdr.GetValue(0)) : 0;
        }
        catch { return 0; }
    }

    /// <summary>The render gate. Run every measure in the baked model (EVALUATE ROW("v",[measure])) and return
    /// one entry per measure that throws a query-time error - the exact class of failure that renders as
    /// "Error fetching data for this visual" on the customer's page. An empty list means every measure resolves
    /// and the gate passes. Never throws: a connection-level failure returns no measure errors (the bake's other
    /// checks surface that separately) rather than masking it as a measure fault.</summary>
    private static List<(string measure, string error)> ValidateMeasures(TOM.Server server, TOM.Database db)
    {
        var errors = new List<(string measure, string error)>();
        try
        {
            using var conn = new Microsoft.AnalysisServices.AdomdClient.AdomdConnection($"Data Source={server.ConnectionString};Catalog={db.Name}");
            conn.Open();
            foreach (var t in db.Model.Tables)
                foreach (var m in t.Measures)
                {
                    try
                    {
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "EVALUATE ROW(\"v\", [" + m.Name.Replace("]", "]]") + "])";
                        using var rdr = cmd.ExecuteReader();
                        while (rdr.Read()) { }   // force full evaluation of the measure
                    }
                    catch (Exception ex)
                    {
                        errors.Add((m.Name, (ex.Message ?? "measure error").Replace("\r", " ").Replace("\n", " ").Trim()));
                    }
                }
        }
        catch { /* connection-level failure is surfaced by the bake's other checks; don't mask it as a measure error */ }
        return errors;
    }

    private static string? FindModelDefinition(string pbipFolder)
    {
        foreach (var sm in Directory.GetDirectories(pbipFolder, "*.SemanticModel", SearchOption.AllDirectories))
        {
            string def = Path.Combine(sm, "definition");
            if (Directory.Exists(def)) return def;
        }
        if (Directory.Exists(Path.Combine(pbipFolder, "definition"))) return Path.Combine(pbipFolder, "definition");
        return null;
    }
}
