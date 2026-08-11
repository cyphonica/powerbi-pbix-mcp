using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The metrics / KPI snapshot - the dashboard's headline numbers, computed from the freshly built data on every
/// ingest/build. It runs over the same staged CSVs in the work dir plus the built model spec that the visible
/// <see cref="VerificationReport"/> runs over, and emits a structured snapshot the cloud uses to (a) compose the
/// build-summary email and (b) evaluate alerts against the live numbers.
///
/// Like the verification report it is a first-class field on the ingest/build result (alongside
/// <c>verification</c>) AND is persisted next to the project as <c>metrics.json</c>, so the cloud picker and the
/// desktop app surface the same object.
///
/// Honesty rule (identical to the verification moat): a KPI is emitted ONLY when it can be computed from what the
/// engine actually has in front of it. A measure whose column is missing/empty, or a DAX the build path cannot
/// evaluate, yields NO KPI - it is omitted, never faked to zero. The snapshot therefore never claims a number it
/// could not derive from the loaded rows.
///
/// How KPIs are derived (honest, defensible aggregates only):
///   1. The model's measures: each measure whose DAX is a single simple aggregation over one column -
///      SUM / AVERAGE / MIN / MAX / COUNT / COUNTA / DISTINCTCOUNT(Table[Column]) - is evaluated directly over the
///      loaded column. Composite / time-intelligence measures (CALCULATE, DIVIDE, ratios) are not evaluated here:
///      the scaffold build path does not host a DAX engine, so rather than guess we omit them.
///   2. When the model carries no resolvable measure (the flat auto-shaped path), the obvious aggregates over the
///      fact columns are computed instead: the SUM of a revenue/amount/total/sales/price column, and the DISTINCT
///      COUNT of an id/key column. These are clearly-named, defensible headline numbers, never invented values.
/// Each KPI carries a display format token (number | currency | percent) derived from the measure's Power BI
/// format string when present, else inferred from the column's name.
/// </summary>
public static class MetricsSnapshot
{
    public const string FileName = "metrics.json";

    private const string FmtNumber = "number";
    private const string FmtCurrency = "currency";
    private const string FmtPercent = "percent";

    // how many data rows to scan per CSV when evaluating a KPI. Bounded so a huge ingest cannot let the snapshot
    // dominate the job; the per-table row count itself is read from the whole file (it is the headline "rows").
    private const int ScanRows = 50000;

    // column-name heuristics for the no-measure fallback. Kept deliberately narrow so the fallback only fires on
    // an obvious fact/id column - a column we cannot confidently classify yields no KPI rather than a guess.
    private static readonly string[] AmountHints = { "amount", "revenue", "sales", "total", "price", "cost", "value", "spend", "gross", "net" };
    private static readonly string[] IdHints = { "id", "key", "code", "number", "no", "ref", "sku" };

    /// <summary>
    /// Build the metrics snapshot over the staged CSVs in <paramref name="dataFolder"/> and the built model
    /// <paramref name="modelSpec"/>. Returns a JSON object in the contract shape:
    ///   { generatedAt, kpis: [ { id, label, value, format } ], tables: [ { name, rows } ] }.
    /// Never throws on a bad table - a per-table read failure simply contributes no KPI and a 0-row table entry.
    /// </summary>
    /// <param name="tables">Logical-table -&gt; CSV-path map from the ingest (paths inside the work dir).</param>
    /// <param name="modelSpec">The model.spec.json the build used (carries measures); may be null when only the
    /// raw tables are available, in which case the column-name fallback supplies the KPIs.</param>
    /// <param name="dataFolder">The work dir holding the staged CSVs. Unused for path resolution here (the table
    /// map already holds full paths) but accepted to mirror <see cref="VerificationReport.Build"/>'s signature.</param>
    public static JsonObject Build(
        IReadOnlyDictionary<string, string> tables,
        string? modelSpec,
        string? dataFolder)
    {
        var loaded = ReadTables(tables);
        JsonObject? spec = TryParse(modelSpec);

        var kpis = new JsonArray();
        var emittedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. evaluate the model's simple-aggregation measures over the loaded data.
        foreach (var kpi in MeasureKpis(loaded, spec))
            if (emittedIds.Add((string)kpi["id"]!)) kpis.Add(kpi);

        // 2. fallback ONLY when no measure produced a KPI - the obvious fact/id aggregates over the columns.
        if (kpis.Count == 0)
            foreach (var kpi in FallbackKpis(loaded))
                if (emittedIds.Add((string)kpi["id"]!)) kpis.Add(kpi);

        var tableSummary = new JsonArray();
        foreach (var t in loaded)
            tableSummary.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["rows"] = t.RowCount,
            });

        return new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["kpis"] = kpis,
            ["tables"] = tableSummary,
        };
    }

    /// <summary>Build the snapshot and persist it as <c>metrics.json</c> in <paramref name="projectDir"/>,
    /// returning the in-memory object too. A write failure never fails the build - the object is still returned
    /// (the persisted copy is a convenience for the desktop app / cloud picker, not the source of truth).</summary>
    public static JsonObject BuildAndSave(
        IReadOnlyDictionary<string, string> tables,
        string? modelSpec,
        string? dataFolder,
        string projectDir)
    {
        var snapshot = Build(tables, modelSpec, dataFolder);
        try
        {
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, FileName), snapshot.ToJsonString(Cli.Pretty));
        }
        catch { /* the persisted copy is best-effort; the returned object is authoritative */ }
        return snapshot;
    }

    // ---- measure-driven KPIs -------------------------------------------------------------------

    /// <summary>For each measure whose DAX is a single simple aggregation over one Table[Column], evaluate it over
    /// the loaded column and yield a KPI. A measure whose column is missing, unreadable, or (for a numeric
    /// aggregation) has no parseable value is omitted - never faked.</summary>
    private static IEnumerable<JsonObject> MeasureKpis(IReadOnlyList<LoadedTable> tables, JsonObject? spec)
    {
        if (spec?["tables"] is not JsonArray tbls) yield break;

        foreach (var tn in tbls)
        {
            if (tn is not JsonObject to || to["measures"] is not JsonArray measures) continue;
            foreach (var mn in measures)
            {
                if (mn is not JsonObject mo) continue;
                string name = (string?)mo["name"] ?? "";
                string dax = (string?)mo["dax"] ?? "";
                string? formatString = (string?)mo["format"];
                if (name.Length == 0) continue;

                if (!TryResolveAggregation(dax, out string agg, out string tbl, out string col)) continue;

                var lt = tables.FirstOrDefault(t => t.Name.Equals(tbl, StringComparison.OrdinalIgnoreCase));
                if (lt == null || lt.ReadError != null) continue; // cannot prove it - omit rather than invent
                int ci = lt.IndexOf(col);
                if (ci < 0) continue;

                if (!TryEvaluate(agg, lt, ci, out double value)) continue; // no parseable value - omit

                yield return Kpi(
                    id: "measure:" + name,
                    label: name,
                    value: value,
                    format: FormatFor(agg, formatString, col));
            }
        }
    }

    // ---- fallback column-name KPIs -------------------------------------------------------------

    /// <summary>When the model offered no resolvable measure, derive the obvious headline numbers straight from
    /// the columns: the SUM of the first amount/revenue-like numeric column, and the DISTINCT COUNT of the first
    /// id/key-like column. A column we cannot confidently classify yields nothing (no guessed KPI).</summary>
    private static IEnumerable<JsonObject> FallbackKpis(IReadOnlyList<LoadedTable> tables)
    {
        bool emittedAmount = false, emittedCount = false;

        foreach (var t in tables)
        {
            if (t.ReadError != null || t.RowCount == 0) continue;

            // SUM of an amount/revenue-like column that actually parses as a number.
            if (!emittedAmount)
                for (int c = 0; c < t.Columns.Count; c++)
                {
                    if (!LooksLike(t.Columns[c], AmountHints)) continue;
                    if (!IsNumericColumn(t, c)) continue;
                    if (!TryEvaluate("SUM", t, c, out double sum)) continue;
                    emittedAmount = true;
                    yield return Kpi(
                        id: $"total:{t.Name}.{t.Columns[c]}",
                        label: $"Total {Humanise(t.Columns[c])}",
                        value: sum,
                        format: FormatForColumn(t.Columns[c]));
                    break;
                }

            // DISTINCT COUNT of an id/key-like column.
            if (!emittedCount)
                for (int c = 0; c < t.Columns.Count; c++)
                {
                    if (!LooksLike(t.Columns[c], IdHints)) continue;
                    if (!TryEvaluate("DISTINCTCOUNT", t, c, out double distinct)) continue;
                    emittedCount = true;
                    yield return Kpi(
                        id: $"distinct:{t.Name}.{t.Columns[c]}",
                        label: $"Distinct {Humanise(t.Columns[c])}",
                        value: distinct,
                        format: FmtNumber);
                    break;
                }

            if (emittedAmount && emittedCount) yield break;
        }
    }

    // ---- aggregation parsing + evaluation ------------------------------------------------------

    /// <summary>Pull the aggregation function and its single Table[Column] reference out of a simple one-aggregation
    /// measure (SUM/AVERAGE/MIN/MAX/COUNT/COUNTA/DISTINCTCOUNT). Returns false for anything with no, or more than
    /// one, column reference, or whose top-level call is not a bare aggregation (a CALCULATE/DIVIDE/ratio measure
    /// is not evaluable from raw data here).</summary>
    internal static bool TryResolveAggregation(string dax, out string agg, out string table, out string column)
    {
        agg = ""; table = ""; column = "";
        if (string.IsNullOrWhiteSpace(dax)) return false;

        // the whole expression must be exactly AGG( Table[Column] ) - nothing wrapping it, one column ref inside.
        var m = Regex.Match(dax.Trim(),
            @"^(SUM|AVERAGE|MIN|MAX|COUNT|COUNTA|DISTINCTCOUNT)\s*\(\s*([A-Za-z_][\w ]*)\[([^\]]+)\]\s*\)$",
            RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        // a second column reference anywhere means it was not a single bare aggregation - reject.
        if (Regex.Matches(dax, @"[A-Za-z_][\w ]*\[[^\]]+\]").Count != 1) return false;

        agg = m.Groups[1].Value.ToUpperInvariant();
        table = m.Groups[2].Value.Trim();
        column = m.Groups[3].Value.Trim();
        return table.Length > 0 && column.Length > 0;
    }

    /// <summary>Evaluate one aggregation over a loaded column. Numeric aggregations (SUM/AVERAGE/MIN/MAX) return
    /// false when no value in the column parses as a number (so the KPI is omitted, never reported as 0). Counting
    /// aggregations (COUNT/COUNTA/DISTINCTCOUNT) count the non-blank / distinct non-blank cells.</summary>
    internal static bool TryEvaluate(string agg, LoadedTable table, int columnIndex, out double value)
    {
        value = 0;
        switch (agg)
        {
            case "COUNT":
            case "COUNTA":
            {
                long n = 0;
                foreach (var v in table.Column(columnIndex))
                    if (!string.IsNullOrWhiteSpace(v)) n++;
                value = n;
                return true; // 0 non-blank cells is a true, honest count (not a fabricated value)
            }
            case "DISTINCTCOUNT":
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var v in table.Column(columnIndex))
                    if (!string.IsNullOrWhiteSpace(v)) set.Add(v.Trim());
                value = set.Count;
                return true;
            }
            default: // SUM / AVERAGE / MIN / MAX over the parseable numeric values
            {
                bool any = false;
                double sum = 0, min = double.MaxValue, max = double.MinValue; long n = 0;
                foreach (var v in table.Column(columnIndex))
                {
                    if (!TryNumber(v, out double d)) continue;
                    any = true; n++;
                    sum += d;
                    if (d < min) min = d;
                    if (d > max) max = d;
                }
                if (!any) return false; // nothing numeric here - omit the KPI rather than report 0
                value = agg switch
                {
                    "AVERAGE" => sum / n,
                    "MIN" => min,
                    "MAX" => max,
                    _ => sum, // SUM
                };
                return true;
            }
        }
    }

    // ---- format derivation ---------------------------------------------------------------------

    /// <summary>The display format token for a measure-driven KPI: a counting aggregation is always a number; for
    /// a numeric aggregation prefer the measure's Power BI format string (a '%' makes it percent, a currency
    /// symbol makes it currency), falling back to the column-name heuristic.</summary>
    private static string FormatFor(string agg, string? measureFormat, string column)
    {
        if (agg is "COUNT" or "COUNTA" or "DISTINCTCOUNT") return FmtNumber;

        string? fromFmt = FormatFromString(measureFormat);
        if (fromFmt != null) return fromFmt;
        return FormatForColumn(column);
    }

    /// <summary>Map a Power BI format string to a contract token: a percent sign means percent; a currency symbol
    /// (or a currency word) means currency; otherwise null so the caller can fall back. Null/blank yields null.</summary>
    internal static string? FormatFromString(string? format)
    {
        if (string.IsNullOrWhiteSpace(format)) return null;
        if (format.Contains('%')) return FmtPercent;
        // common currency symbols, plus an escaped "\$" as the solution specs write it.
        if (format.IndexOfAny(new[] { '$', '£', '€', '¥' }) >= 0) return FmtCurrency;
        return null;
    }

    /// <summary>Heuristic format from a column name when there is no measure format string: a money-ish name is
    /// currency, otherwise a plain number. (Percent is never guessed from a name - a raw column is rarely a ratio.)</summary>
    private static string FormatForColumn(string column)
    {
        string c = column.ToLowerInvariant();
        if (c.Contains("revenue") || c.Contains("amount") || c.Contains("price") || c.Contains("sales")
            || c.Contains("cost") || c.Contains("spend") || c.Contains("gross") || c.Contains("net")
            || c.Contains("total") || c.Contains("value"))
            return FmtCurrency;
        return FmtNumber;
    }

    // ---- column heuristics ---------------------------------------------------------------------

    /// <summary>True when a hint appears as a whole word-token of the column name. The name is split on camelCase
    /// boundaries and on _ / - / space, so "OrderId"/"order_id"/"Customer Id" all yield an "id" token (matched),
    /// while "video" yields the single token "video" (NOT matched). Word-token matching keeps the fallback narrow:
    /// only a column that genuinely names a fact/id concept fires a KPI.</summary>
    internal static bool LooksLike(string column, string[] hints)
    {
        var tokens = WordTokens(column);
        foreach (var h in hints)
            if (tokens.Contains(h)) return true;
        return false;
    }

    /// <summary>Split a column name into lower-cased word tokens on camelCase boundaries and on _ / - / space.</summary>
    private static HashSet<string> WordTokens(string column)
    {
        // insert a space at each lower/digit -> Upper boundary so "OrderId" -> "Order Id", then split on non-alnum.
        string spaced = Regex.Replace(column, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tok in Regex.Split(spaced, @"[^A-Za-z0-9]+"))
            if (tok.Length > 0) set.Add(tok.ToLowerInvariant());
        return set;
    }

    /// <summary>True when the sampled non-blank values of a column all parse as numbers (so SUM is meaningful).</summary>
    private static bool IsNumericColumn(LoadedTable t, int columnIndex)
    {
        bool any = false;
        foreach (var v in t.Column(columnIndex))
        {
            if (string.IsNullOrWhiteSpace(v)) continue;
            any = true;
            if (!TryNumber(v, out _)) return false;
        }
        return any;
    }

    private static string Humanise(string column)
    {
        // turn "OrderAmount" / "order_amount" / "order-amount" into "Order Amount" for the KPI label.
        var spaced = Regex.Replace(column.Replace('_', ' ').Replace('-', ' '), @"(?<=[a-z0-9])(?=[A-Z])", " ");
        spaced = Regex.Replace(spaced, @"\s+", " ").Trim();
        return spaced.Length == 0 ? column : spaced;
    }

    private static bool TryNumber(string? v, out double d)
    {
        d = 0;
        if (string.IsNullOrWhiteSpace(v)) return false;
        string s = v.Trim();
        // tolerate a leading currency symbol and thousands separators the way a raw fact column may carry them.
        if (s.Length > 0 && (s[0] == '$' || s[0] == '£' || s[0] == '€' || s[0] == '¥')) s = s[1..].Trim();
        return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d);
    }

    private static JsonObject Kpi(string id, string label, double value, string format) => new()
    {
        ["id"] = id,
        ["label"] = label,
        ["value"] = JsonValue.Create(Round(value)),
        ["format"] = format,
    };

    /// <summary>Round to a sane display precision: whole numbers stay whole; otherwise 4 dp to avoid a long binary
    /// float tail in the JSON (the cloud formats for display from the format token).</summary>
    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 4, MidpointRounding.AwayFromZero) : 0;

    // ---- shared helpers (mirrors VerificationReport) -------------------------------------------

    private static List<LoadedTable> ReadTables(IReadOnlyDictionary<string, string> tables)
    {
        var list = new List<LoadedTable>();
        foreach (var kv in tables)
            list.Add(LoadedTable.Read(kv.Key, kv.Value));
        return list;
    }

    private static JsonObject? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch { return null; }
    }

    // ---- a CSV loaded just far enough to evaluate the KPIs -------------------------------------

    /// <summary>One ingested table read into memory up to the scan cap: the header, the total data-row count
    /// (whole file - the headline "rows"), and a bounded sample of rows the aggregations evaluate over. Internal so
    /// the fault-sensitive tests can drive <see cref="TryEvaluate"/> directly over a real CSV.</summary>
    internal sealed class LoadedTable
    {
        public string Name { get; private init; } = "";
        public List<string> Columns { get; } = new();
        public int RowCount { get; private set; }          // total data rows in the file (not the sample size)
        public string? ReadError { get; private set; }

        private readonly List<string[]> _sample = new();   // up to ScanRows rows for the aggregations

        public int IndexOf(string column)
        {
            for (int i = 0; i < Columns.Count; i++)
                if (Columns[i].Equals(column, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        /// <summary>The sampled values of one column (bounded to the scan cap).</summary>
        public IEnumerable<string?> Column(int index)
        {
            foreach (var row in _sample)
                yield return index < row.Length ? row[index] : null;
        }

        public static LoadedTable Read(string name, string path)
        {
            var t = new LoadedTable { Name = name };
            try
            {
                if (!File.Exists(path)) { t.ReadError = "file not found"; return t; }
                using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                string? header = reader.ReadLine();
                if (string.IsNullOrEmpty(header)) { t.ReadError = "file is empty (no header)"; return t; }

                t.Columns.AddRange(SplitCsvLine(header));

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0 && reader.EndOfStream) break; // a trailing blank line is not a data row
                    t.RowCount++;
                    if (t._sample.Count < ScanRows) t._sample.Add(SplitCsvLine(line));
                }
            }
            catch (Exception ex) { t.ReadError = ex.Message; }
            return t;
        }

        // RFC-4180 line splitter (quoted fields with embedded commas/quotes), matching CsvSink / VerificationReport.
        private static string[] SplitCsvLine(string line)
        {
            var outp = new List<string>();
            var sb = new StringBuilder();
            bool inQ = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQ)
                {
                    if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQ = false; }
                    else sb.Append(c);
                }
                else
                {
                    if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); }
                    else if (c == '"') inQ = true;
                    else sb.Append(c);
                }
            }
            outp.Add(sb.ToString());
            return outp.ToArray();
        }
    }
}
