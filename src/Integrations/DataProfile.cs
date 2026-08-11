using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The data-profile / data-quality report - a per-column health pass over the freshly ingested tables. It runs
/// over the same staged CSVs in the work dir that the visible <see cref="VerificationReport"/> and the
/// <see cref="MetricsSnapshot"/> run over, and emits a structured profile the cloud picker and the desktop app
/// surface so a customer can SEE what the data actually looks like before they trust a dashboard built on it.
///
/// Like verification and metrics it is a first-class field on the ingest/build result AND is persisted next to
/// the project as <c>profile.json</c>, so all three reports ride into the same zipped deliverable.
///
/// Honesty rule (identical to the verification / metrics moats): every number is MEASURED from the loaded rows -
/// nothing is invented. A statistic that cannot be derived (a mean over a non-numeric column) is simply omitted
/// for that column, never faked. A flag is raised ONLY when the measured shape of the column triggers it.
///
/// Per column it reports:
///   - rowCount        the data rows scanned for this table (the whole file up to the scan cap)
///   - nullCount / pct null-or-blank cells and their share of the rows
///   - distinctCount   distinct non-blank values seen
///   - type            the inferred spec data-type (int64 | double | date | boolean | string), via ColumnTypeInference
///   - min/max/mean    for a numeric column only (omitted otherwise - never faked over text)
///   - topValues       the most common non-blank values with their counts (bounded to TopN)
///   - flags           the quality flags below, each raised only when its measured condition holds
///
/// Quality flags (the data-quality signal):
///   - fully-empty     the column is blank in every scanned row (carries no data at all)
///   - high-null       a large share (>= HighNullPct) of cells are null/blank, but not all
///   - constant        every non-blank cell holds the SAME single value (no variation - likely a mis-map)
///   - all-unique-id   every non-blank cell is distinct AND the name/type looks like an identifier (a key column)
///   - mixed-type      the non-blank cells do not share one inferable type (e.g. numbers mixed with words)
/// </summary>
public static class DataProfile
{
    public const string FileName = "profile.json";

    // how many data rows to scan per CSV for the per-column statistics. Bounded so a huge ingest cannot let the
    // profile dominate the job; the per-table row count itself is read from the whole file (the headline "rows").
    private const int ScanRows = 50000;

    // how many top values to report per column.
    private const int TopN = 5;

    // a column is "high-null" when at least this share of its scanned cells are null/blank (but not all of them -
    // an all-blank column is the stronger "fully-empty" flag instead).
    private const double HighNullPct = 0.5;

    // the all-unique-id flag needs enough rows that "every value distinct" is meaningful (2 rows is not an id).
    private const int MinRowsForIdFlag = 3;

    // name tokens that, together with all-distinct values, mark a column as an identifier/key.
    private static readonly string[] IdNameHints = { "id", "key", "code", "guid", "uuid", "sku", "ref", "number", "no" };

    private const string FlagFullyEmpty = "fully-empty";
    private const string FlagHighNull = "high-null";
    private const string FlagConstant = "constant";
    private const string FlagAllUniqueId = "all-unique-id";
    private const string FlagMixedType = "mixed-type";

    /// <summary>
    /// Build the data-profile over the staged CSVs in <paramref name="dataFolder"/>. Returns a JSON object in the
    /// contract shape:
    ///   { generatedAt, tables: [ { name, rows, columns: [ { name, type, nullCount, nullPct, distinctCount,
    ///     min?, max?, mean?, topValues: [ { value, count } ], flags: [..] } ] } ] }.
    /// Never throws on a bad table - a per-table read failure becomes a table entry carrying a readError, not an
    /// exception.
    /// </summary>
    /// <param name="tables">Logical-table -&gt; CSV-path map from the ingest (paths inside the work dir).</param>
    /// <param name="modelSpec">The model.spec.json the build used. Accepted to mirror
    /// <see cref="VerificationReport.Build"/> / <see cref="MetricsSnapshot.Build"/>; the profile is derived from
    /// the raw loaded data, so the spec is not required and may be null.</param>
    /// <param name="dataFolder">The work dir holding the staged CSVs. Accepted to mirror the sibling builders'
    /// signatures; the table map already holds full paths, so it is unused for resolution here.</param>
    public static JsonObject Build(
        IReadOnlyDictionary<string, string> tables,
        string? modelSpec,
        string? dataFolder)
    {
        var loaded = ReadTables(tables);

        var tableArr = new JsonArray();
        foreach (var t in loaded)
            tableArr.Add(ProfileTable(t));

        return new JsonObject
        {
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["tables"] = tableArr,
        };
    }

    /// <summary>Build the profile and persist it as <c>profile.json</c> in <paramref name="projectDir"/>,
    /// returning the in-memory object too. A write failure never fails the build - the object is still returned
    /// (the persisted copy is a convenience for the desktop app / cloud picker, not the source of truth).</summary>
    public static JsonObject BuildAndSave(
        IReadOnlyDictionary<string, string> tables,
        string? modelSpec,
        string? dataFolder,
        string projectDir)
    {
        var profile = Build(tables, modelSpec, dataFolder);
        try
        {
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, FileName), profile.ToJsonString(Cli.Pretty));
        }
        catch { /* the persisted copy is best-effort; the returned object is authoritative */ }
        return profile;
    }

    // ---- per-table / per-column profiling ------------------------------------------------------

    private static JsonObject ProfileTable(LoadedTable t)
    {
        if (t.ReadError != null)
            return new JsonObject
            {
                ["name"] = t.Name,
                ["rows"] = 0,
                ["readError"] = t.ReadError,
                ["columns"] = new JsonArray(),
            };

        var cols = new JsonArray();
        for (int c = 0; c < t.Columns.Count; c++)
            cols.Add(ProfileColumn(t, c));

        return new JsonObject
        {
            ["name"] = t.Name,
            ["rows"] = t.RowCount,
            ["columns"] = cols,
        };
    }

    /// <summary>Profile one column from its scanned cells: the counts, the inferred type, the numeric stats (only
    /// when numeric), the top-N values, and the quality flags. Every field is derived from the cells - nothing is
    /// invented; a stat that does not apply is omitted rather than faked.</summary>
    internal static JsonObject ProfileColumn(LoadedTable t, int columnIndex)
    {
        int rows = 0, nulls = 0;
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);   // distinct non-blank value -> count

        // numeric accumulators (used only if the column infers as numeric)
        bool anyNumeric = false, anyNonNumeric = false;
        double sum = 0, min = double.MaxValue, max = double.MinValue; long numericN = 0;

        var nonBlank = new List<string?>();   // for the type inference (matches ColumnTypeInference's contract)

        foreach (var v in t.Column(columnIndex))
        {
            rows++;
            if (string.IsNullOrWhiteSpace(v)) { nulls++; continue; }
            string val = v.Trim();
            nonBlank.Add(val);
            counts[val] = counts.TryGetValue(val, out var n) ? n + 1 : 1;

            if (TryNumber(val, out double d))
            {
                anyNumeric = true; numericN++;
                sum += d;
                if (d < min) min = d;
                if (d > max) max = d;
            }
            else anyNonNumeric = true;
        }

        string type = ColumnTypeInference.Infer(nonBlank);
        int distinct = counts.Count;
        int nonNull = rows - nulls;

        // mixed-type: the non-blank cells do not share one inferable type. Detected as the inference falling back
        // to "string" while the cells are genuinely heterogeneous - some parse as numbers and some do not (e.g.
        // "10", "20", "n/a"). A clean text column (no cell numeric) is NOT mixed; a clean numeric column is not.
        bool mixedType = type == "string" && anyNumeric && anyNonNumeric;

        var col = new JsonObject
        {
            ["name"] = t.Columns[columnIndex],
            ["type"] = type,
            ["nullCount"] = nulls,
            ["nullPct"] = rows > 0 ? Round((double)nulls / rows) : 0,
            ["distinctCount"] = distinct,
        };

        // numeric stats ONLY for a column that inferred to a numeric type and actually carried parseable numbers.
        // (A "mixed-type" column with a few stray numbers infers as string, so it gets no min/max/mean - honest.)
        bool numericType = type is "int64" or "double";
        if (numericType && anyNumeric)
        {
            col["min"] = JsonValue.Create(Round(min));
            col["max"] = JsonValue.Create(Round(max));
            col["mean"] = JsonValue.Create(Round(sum / numericN));
        }

        col["topValues"] = TopValues(counts);
        col["flags"] = Flags(t.Columns[columnIndex], type, rows, nonNull, nulls, distinct, mixedType);
        return col;
    }

    /// <summary>The TopN most-common non-blank values with their counts, highest first. Ties break on the value so
    /// the output is deterministic (the tests pin exact top values).</summary>
    private static JsonArray TopValues(Dictionary<string, int> counts)
    {
        var top = new JsonArray();
        foreach (var kv in counts
                     .OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                     .Take(TopN))
            top.Add(new JsonObject { ["value"] = kv.Key, ["count"] = kv.Value });
        return top;
    }

    /// <summary>Raise the quality flags whose measured condition holds. The decision is pure so the tests pin each
    /// flag (a high-null column flags; a constant column flags; an id column flags; a mixed-type column flags; a
    /// clean column does not). <paramref name="mixedType"/> is computed by the caller (it needs the per-cell
    /// numeric/non-numeric split) and threaded in so the rule stays a single pure decision.</summary>
    internal static JsonArray Flags(
        string columnName, string inferredType, int rows, int nonNull, int nulls, int distinct, bool mixedType)
    {
        var flags = new JsonArray();
        if (rows == 0) return flags;   // an empty table is the verification report's job, not a column flag

        // fully-empty: blank in every scanned row (the strongest null signal - supersedes high-null).
        if (nonNull == 0)
        {
            flags.Add(FlagFullyEmpty);
            return flags;   // nothing else is meaningful for a column with no data
        }

        // high-null: a large share of cells are blank (but at least one is not - else it is fully-empty above).
        if ((double)nulls / rows >= HighNullPct)
            flags.Add(FlagHighNull);

        // constant: every non-blank cell holds the same single value (no variation at all).
        if (distinct == 1 && nonNull > 1)
            flags.Add(FlagConstant);

        // all-unique-id: every non-blank cell is distinct AND the name/type looks like an identifier. Needs a few
        // rows for "all distinct" to mean anything, and the constant case (distinct==1) is mutually exclusive.
        if (nonNull >= MinRowsForIdFlag && distinct == nonNull && LooksLikeId(columnName, inferredType))
            flags.Add(FlagAllUniqueId);

        // mixed-type: the non-blank cells do not share one inferable type (some numbers, some words).
        if (mixedType)
            flags.Add(FlagMixedType);

        return flags;
    }

    /// <summary>True when a column with all-distinct values is named/typed like an identifier (a key): an id-ish
    /// name token, or a numeric/string type that is plausibly a key. A plain numeric measure column (e.g. Amount)
    /// is NOT an id even if its values happen to be distinct, so the name hint is required.</summary>
    internal static bool LooksLikeId(string columnName, string inferredType)
    {
        var tokens = WordTokens(columnName);
        foreach (var h in IdNameHints)
            if (tokens.Contains(h)) return true;
        return false;
    }

    /// <summary>Split a column name into lower-cased word tokens on camelCase boundaries and on _ / - / space, so
    /// "OrderId" / "order_id" / "Customer Id" all yield an "id" token. Mirrors MetricsSnapshot's tokeniser.</summary>
    private static HashSet<string> WordTokens(string column)
    {
        string spaced = System.Text.RegularExpressions.Regex.Replace(column, @"(?<=[a-z0-9])(?=[A-Z])", " ");
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tok in System.Text.RegularExpressions.Regex.Split(spaced, @"[^A-Za-z0-9]+"))
            if (tok.Length > 0) set.Add(tok.ToLowerInvariant());
        return set;
    }

    // ---- shared helpers (mirrors VerificationReport / MetricsSnapshot) --------------------------

    private static List<LoadedTable> ReadTables(IReadOnlyDictionary<string, string> tables)
    {
        var list = new List<LoadedTable>();
        foreach (var kv in tables)
            list.Add(LoadedTable.Read(kv.Key, kv.Value));
        return list;
    }

    private static bool TryNumber(string? v, out double d)
    {
        d = 0;
        if (string.IsNullOrWhiteSpace(v)) return false;
        string s = v.Trim();
        if (s.Length > 0 && (s[0] == '$' || s[0] == '£' || s[0] == '€' || s[0] == '¥')) s = s[1..].Trim();
        return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d);
    }

    /// <summary>Round to a sane display precision: whole numbers stay whole; otherwise 4 dp to avoid a long binary
    /// float tail in the JSON. Matches MetricsSnapshot's rounding.</summary>
    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 4, MidpointRounding.AwayFromZero) : 0;

    // ---- a CSV loaded just far enough to profile it --------------------------------------------

    /// <summary>One ingested table read into memory up to the scan cap: the header, the total data-row count
    /// (whole file - the headline "rows"), and a bounded sample of rows the per-column statistics run over.
    /// Internal so the fault-sensitive tests can drive <see cref="ProfileColumn"/> directly over a real CSV.</summary>
    internal sealed class LoadedTable
    {
        public string Name { get; private init; } = "";
        public List<string> Columns { get; } = new();
        public int RowCount { get; private set; }          // total data rows in the file (not the sample size)
        public string? ReadError { get; private set; }

        private readonly List<string[]> _sample = new();   // up to ScanRows rows for the per-column statistics

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

        // RFC-4180 line splitter (quoted fields with embedded commas/quotes), matching CsvSink / the sibling reports.
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
