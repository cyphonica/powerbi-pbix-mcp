using Microsoft.Extensions.Logging;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>
/// Engine-backed offline .pbix operations: evaluate DAX, read table rows, read the model and edit a
/// measure on a CLOSED .pbix by briefly opening it in the LOCAL Power BI Desktop (its embedded msmdsrv
/// hosts the model), driving it, and closing Desktop again. Nothing here re-implements VertiPaq, XPress9
/// or the model compiler - the local Desktop engine does the load, exactly as <see cref="BulkOps"/>'s
/// refresh loop does for a bulk refresh. A Desktop window appears for the duration of each call.
///
/// Reuse, not duplication:
///   - the open -> connect -> await -> (save) -> close loop is <see cref="DesktopSession"/> +
///     <see cref="DesktopInterop"/>, the same proven path <c>BulkOps.RefreshOne</c> drives;
///   - the DAX exec + row serialisation is <see cref="ModelService.ExecuteDaxRows"/> /
///     <see cref="ModelService.SerialiseDaxReader"/>, so run_dax and these tools return one result shape;
///   - the model -> summary mapping mirrors <c>get_model_summary</c>'s shaping (with measure DAX added).
///
/// The pure halves - query construction (<see cref="BuildTopNQuery"/> / <see cref="QuoteTableName"/>),
/// the model mapping (<see cref="MapModel"/>) and argument validation - are unit-tested offline. The live
/// open-Desktop path is deliberately untested here (it needs a real Desktop, which no CI box has); its one
/// manual end-to-end check lives under examples/.
/// </summary>
public sealed class OfflinePbixService
{
    /// <summary>Hard ceiling on the rows eval_dax_offline will store, so a careless query cannot try to
    /// stream a whole fact table back through the reader (it still runs; the response is capped + flagged).</summary>
    internal const int EvalMaxRows = 100_000;

    /// <summary>Hard ceiling on read_table_offline's topN, for the same reason.</summary>
    internal const int MaxTopN = 1_000_000;

    /// <summary>Longest a single open-Desktop call may wait for the model to attach.</summary>
    internal const int MaxTimeoutSec = 1_800;
    internal const int DefaultTimeoutSec = 180;

    private readonly ILogger<OfflinePbixService>? _log;

    public OfflinePbixService(ILogger<OfflinePbixService>? log = null) => _log = log;

    // ---------------------------------------------------------------- tools (open a Desktop, briefly)

    /// <summary>Evaluate DAX against a closed .pbix: open it in the local Desktop, ExecuteReader the query
    /// (EVALUATE is prepended to a bare table expression, exactly like run_dax), serialise the rows to
    /// run_dax's result shape, then close Desktop.</summary>
    public object EvalDaxOffline(string pbixPath, string dax, int timeoutSec = DefaultTimeoutSec)
    {
        string query = ModelService.NormaliseDaxQuery(dax);   // reuse run_dax's EVALUATE-prefix + empty guard
        var (result, _, _) = RunOnOpenPbix(pbixPath, timeoutSec, save: false, saveRetries: 0, (sess, _) =>
        {
            var (cols, rows, truncated, _) = ModelService.ExecuteDaxRows(
                LocalConnString(sess.Port), query, EvalMaxRows, countBeyondMax: false);
            return (object)new { ok = true, pbixPath, columns = cols, rowCount = rows.Count, truncated, rows };
        });
        return result;
    }

    /// <summary>Read up to topN rows of one table from a closed .pbix via EVALUATE TOPN(topN, 'table').</summary>
    public object ReadTableOffline(string pbixPath, string table, int topN = 1000)
    {
        ValidateTableName(table);
        int cap = ValidateTopN(topN);
        string query = BuildTopNQuery(table, cap);
        var (result, _, _) = RunOnOpenPbix(pbixPath, DefaultTimeoutSec, save: false, saveRetries: 0, (sess, _) =>
        {
            var (cols, rows, _, _) = ModelService.ExecuteDaxRows(
                LocalConnString(sess.Port), query, cap, countBeyondMax: false);
            // TOPN caps the rows engine-side, so ExecuteDaxRows never trips its own truncation flag: a
            // full page (rowCount == cap) means the table almost certainly holds more than were returned.
            bool truncated = rows.Count >= cap;
            return (object)new
            {
                ok = true, pbixPath, table, topN = cap,
                columns = cols, rowCount = rows.Count, truncated, rows,
                note = truncated ? $"Row cap hit: only the first {cap} row(s) were returned; the table holds more." : null,
            };
        });
        return result;
    }

    /// <summary>Read a closed .pbix's model via TOM (tables, columns with types, measures with DAX,
    /// relationships, named M expressions), then close Desktop. Mirrors get_model_summary's shaping.</summary>
    public object GetModelOffline(string pbixPath, int timeoutSec = DefaultTimeoutSec)
    {
        var (result, _, _) = RunOnOpenPbix(pbixPath, timeoutSec, save: false, saveRetries: 0,
            (_, db) => (object)new { ok = true, pbixPath, model = MapModel(db.Model) });
        return result;
    }

    /// <summary>Add or edit a measure on a closed .pbix: open it, TOM add/update the measure, SaveChanges
    /// to the live engine, drive Desktop's own File > Save (scripted Ctrl+S) so it lands in the .pbix, then
    /// close Desktop. Reports the before/after expression, format string and display folder.</summary>
    public object EditMeasureOffline(string pbixPath, string table, string name,
        string? expression = null, string? formatString = null, string? displayFolder = null,
        int timeoutSec = DefaultTimeoutSec, int saveRetries = 3)
    {
        ValidateTableName(table);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("A measure name is required.");
        if (saveRetries <= 0) saveRetries = 3;

        var (edit, saveDispatched, saveError) = RunOnOpenPbix(pbixPath, timeoutSec, save: true, saveRetries, (_, db) =>
        {
            var model = db.Model;
            var t = Table(model, table);
            var me = t.Measures.Find(name);

            object? before = me is null ? null
                : new { expression = me.Expression, formatString = me.FormatString, displayFolder = me.DisplayFolder };
            bool added;
            if (me is null)
            {
                if (string.IsNullOrWhiteSpace(expression))
                    throw new InvalidOperationException(
                        $"Measure '{table}[{name}]' does not exist - pass expression to create it.");
                me = new TOM.Measure { Name = name, Expression = expression };
                if (!string.IsNullOrWhiteSpace(formatString)) me.FormatString = formatString;
                if (!string.IsNullOrWhiteSpace(displayFolder)) me.DisplayFolder = displayFolder;
                t.Measures.Add(me);
                added = true;
            }
            else
            {
                if (expression != null) me.Expression = expression;
                if (formatString != null) me.FormatString = formatString;
                if (displayFolder != null) me.DisplayFolder = displayFolder;
                added = false;
            }
            model.SaveChanges();   // commit into the live Desktop engine (the Ctrl+S below writes the .pbix)
            var after = new { expression = me.Expression, formatString = me.FormatString, displayFolder = me.DisplayFolder };
            return new MeasureEdit($"{table}[{name}]", added, before, after);
        });

        return new
        {
            ok = true,
            pbixPath,
            measure = edit.Measure,
            action = edit.Added ? "added" : "updated",
            before = edit.Before,
            after = edit.After,
            committedToLiveModel = true,
            persistedToDisk = saveDispatched,
            saveDispatched,
            error = saveError,
            note = saveDispatched
                ? "Ctrl+S was dispatched to the Desktop hosting this .pbix and its LastWriteTime advanced - "
                  + "the edit was written to disk (a dispatch confirmation, not a content diff)."
                : "The measure is committed to the live engine but the scripted .pbix save was not confirmed.",
        };
    }

    private sealed record MeasureEdit(string Measure, bool Added, object? Before, object After);

    // ---------------------------------------------------------------- open/close loop (reused by all four)

    /// <summary>
    /// Open <paramref name="pbixPath"/> in the local Power BI Desktop, wait for its embedded engine, run
    /// <paramref name="body"/> against the freshly attached model, then (when <paramref name="save"/>)
    /// release the engine and drive Desktop's own Ctrl+S so the edit lands in the .pbix - and ALWAYS close
    /// Desktop. This is BulkOps.RefreshOne's proven launch/connect/await/save/teardown, factored so the four
    /// tools share one open/close. Not unit-tested: it needs a live Desktop, which no CI box has.
    /// </summary>
    private (T result, bool saveDispatched, string? saveError) RunOnOpenPbix<T>(
        string pbixPath, int timeoutSec, bool save, int saveRetries, Func<DesktopSession, TOM.Database, T> body)
    {
        ValidatePbixPath(pbixPath);
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Engine-backed offline .pbix ops drive Power BI Desktop and only run on Windows.");
        int timeout = ClampTimeout(timeoutSec);
        string exe = DesktopInterop.ResolvePbixExe(null);
        if (!File.Exists(exe))
            throw new InvalidOperationException($"Power BI Desktop not found: '{exe}' (set DAXOPS_PBIDESKTOP_EXE).");

        DesktopSession? d = null;
        TOM.Server? srv = null;
        bool saveDispatched = false;
        string? saveError = null;
        try
        {
            Log("OPEN  " + pbixPath);
            d = DesktopSession.Launch(pbixPath, exe, timeout, Log);
            srv = d.Connect();
            var db = d.AwaitModel(srv, d.DeadlineUtc);

            // short grace for table metadata to populate (same as RefreshOne) - the engine is up before the model is
            for (int g = 0; g < 15 && db.Model.Tables.Count == 0; g++)
            {
                Thread.Sleep(1000);
                try { srv.Refresh(); } catch { }
                db = srv.Databases[0];
            }

            var result = body(d, db);

            if (save)
            {
                // release the engine BEFORE the save - Desktop will not write the .pbix while TOM holds it
                d.DisconnectEngine();
                srv = null;
                saveDispatched = d.SaveDispatch(saveRetries);
                if (!saveDispatched)
                    saveError = "Scripted File > Save did not confirm - the .pbix LastWriteTime did not advance.";
            }
            return (result, saveDispatched, saveError);
        }
        finally
        {
            try { srv?.Disconnect(); } catch { }
            d?.CloseDesktop();
        }
    }

    private void Log(string m)
    {
        if (_log is { } l) l.LogInformation("{Message}", m);
        else Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {m}");
    }

    // ---------------------------------------------------------------- pure helpers (unit-tested offline)

    /// <summary>ADOMD connection string for a local Desktop engine on <paramref name="port"/> - the same
    /// form <see cref="ModelSession.AdomdConnectionString"/> builds for a launched session.</summary>
    internal static string LocalConnString(int port) => $"Data Source=localhost:{port}";

    /// <summary>EVALUATE TOPN(n, 'table') - read_table_offline's query. topN must already be validated.</summary>
    internal static string BuildTopNQuery(string table, int topN) =>
        $"EVALUATE TOPN({topN}, {QuoteTableName(table)})";

    /// <summary>Quote a table name as a DAX table reference: single-quoted, with any embedded single quote
    /// doubled (DAX's escape inside a quoted identifier). Accepts a bare or an already-quoted name, so
    /// Sales and 'Sales' normalise identically; Bob's -> 'Bob''s'.</summary>
    internal static string QuoteTableName(string table)
    {
        string t = (table ?? "").Trim();
        if (t.Length == 0) throw new InvalidOperationException("A table name is required.");
        if (t.Length >= 2 && t[0] == '\'' && t[^1] == '\'') t = t[1..^1].Replace("''", "'");
        return "'" + t.Replace("'", "''") + "'";
    }

    /// <summary>The model -> summary mapping (tables, columns with types, measures WITH DAX, relationships,
    /// named M expressions). Mirrors get_model_summary's shaping and adds the measure expression that the
    /// live summary omits, so a closed-file read matches the connected one. Pure over a TOM.Model, so it is
    /// unit-testable against a model built in memory - no engine.</summary>
    internal static object MapModel(TOM.Model m)
    {
        var tables = m.Tables.Select(t => new
        {
            name = t.Name,
            isHidden = t.IsHidden,
            columns = t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber)
                               .Select(c => new { name = c.Name, dataType = c.DataType.ToString(), type = c.Type.ToString() }),
            measures = t.Measures.Select(me => new
            {
                name = me.Name, expression = me.Expression, format = me.FormatString, folder = me.DisplayFolder,
            }),
            partitions = t.Partitions.Select(pp => new { name = pp.Name, source = pp.SourceType.ToString() }),
        });
        var rels = m.Relationships.OfType<TOM.SingleColumnRelationship>().Select(r => new
        {
            name = r.Name,
            from = $"{r.FromTable.Name}[{r.FromColumn.Name}]",
            to = $"{r.ToTable.Name}[{r.ToColumn.Name}]",
            fromCardinality = r.FromCardinality.ToString(),
            toCardinality = r.ToCardinality.ToString(),
            crossFilter = r.CrossFilteringBehavior.ToString(),
            active = r.IsActive,
        });
        var exprs = m.Expressions.Select(e => new { name = e.Name, kind = e.Kind.ToString() });
        return new
        {
            name = m.Name,
            tableCount = m.Tables.Count,
            tables,
            relationships = rels,
            expressions = exprs,
        };
    }

    internal static void ValidatePbixPath(string pbixPath)
    {
        if (string.IsNullOrWhiteSpace(pbixPath)) throw new InvalidOperationException("A .pbix path is required.");
        if (!pbixPath.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Not a .pbix file: '{pbixPath}'.");
        if (!File.Exists(pbixPath)) throw new InvalidOperationException($"File not found: '{pbixPath}'.");
    }

    internal static void ValidateTableName(string table)
    {
        if (string.IsNullOrWhiteSpace(table)) throw new InvalidOperationException("A table name is required.");
    }

    /// <summary>topN must be positive; it is capped at <see cref="MaxTopN"/>.</summary>
    internal static int ValidateTopN(int topN)
    {
        if (topN <= 0) throw new InvalidOperationException($"topN must be a positive number of rows (got {topN}).");
        return Math.Min(topN, MaxTopN);
    }

    /// <summary>A non-positive timeout falls back to the default; anything above the ceiling is clamped.</summary>
    internal static int ClampTimeout(int timeoutSec) =>
        timeoutSec <= 0 ? DefaultTimeoutSec : Math.Min(timeoutSec, MaxTimeoutSec);

    private static TOM.Table Table(TOM.Model m, string name) =>
        m.Tables.Find(name) ?? throw new InvalidOperationException($"Table '{name}' not found.");
}
