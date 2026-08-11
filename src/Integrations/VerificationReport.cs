using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The visible verification report - the "did the build actually load real, refreshable data?" moat. It runs
/// over the freshly ingested tables (the staged CSVs in the work dir) plus the built model spec (its tables,
/// measures and relationships) and emits a structured, customer-surfaced report with a status traffic light.
///
/// The report is a first-class field on the ingest/build result AND is written next to the project as
/// <c>verification.json</c>, so the cloud picker and the desktop app both surface the same object.
///
/// Honesty rule (the whole point of the moat): a check is emitted ONLY when it can be computed from what the
/// engine actually has in front of it. If a calendar table is not present, the continuity check is omitted -
/// it is NEVER invented, and a number is NEVER fabricated. The overall status is the WORST of the checks that
/// ran (fail beats warn beats pass); with no checks at all the status is "warn" (nothing could be proven).
///
/// Checks (each emitted only when computable):
///   - rows-loaded per table:        FAIL if a table loaded 0 data rows.
///   - no all-null/all-blank column: WARN if any column is blank in every loaded row.
///   - calendar continuity:          WARN on gaps in a date/calendar column when a calendar table exists.
///   - refresh-safety:               WARN/FAIL if a measure or query hardcodes an absolute / "today" date
///                                   that would break (or silently freeze) on the next scheduled refresh.
///   - primary measure resolves:     WARN if the model's first numeric measure cannot resolve to a value
///                                   from the loaded data (its referenced column is empty/missing).
///   - relationship key coverage:    WARN on orphan foreign keys when a relationship's two key columns are
///                                   both present in the loaded CSVs.
/// </summary>
public static class VerificationReport
{
    public const string FileName = "verification.json";

    private const string Pass = "pass";
    private const string Warn = "warn";
    private const string Fail = "fail";

    // how many data rows to scan per CSV for the null-column / key-coverage / continuity checks. Bounded so a
    // huge ingest cannot make verification dominate the job; the row-count check itself reads the whole file.
    private const int ScanRows = 5000;

    /// <summary>
    /// Build the verification report over the staged CSVs in <paramref name="dataFolder"/> and the built model
    /// <paramref name="modelSpec"/> (the same spec string handed to the scaffolder). Returns a JSON object in
    /// the contract shape: { status, generatedAt, summary, tables[], checks[] }. Never throws on a bad table -
    /// a per-table read failure becomes a check, not an exception.
    /// </summary>
    /// <param name="tables">Logical-table -&gt; CSV-path map from the ingest (paths inside the work dir).</param>
    /// <param name="modelSpec">The model.spec.json the build used (carries measures, M and relationships); may
    /// be null when only the raw tables are available.</param>
    /// <param name="dataFolder">The work dir holding the staged CSVs (used to resolve relative table paths and
    /// to read a calendar CSV referenced by the spec). May be null when the table map already holds full paths.</param>
    public static JsonObject Build(
        IReadOnlyDictionary<string, string> tables,
        string? modelSpec,
        string? dataFolder)
    {
        var loaded = ReadTables(tables);
        JsonObject? spec = TryParse(modelSpec);

        var checks = new JsonArray();
        foreach (var c in RowsLoaded(loaded)) checks.Add(c);
        foreach (var c in NoAllNullColumn(loaded)) checks.Add(c);
        foreach (var c in CalendarContinuity(loaded, spec, dataFolder)) checks.Add(c);
        foreach (var c in RefreshSafety(spec)) checks.Add(c);
        foreach (var c in PrimaryMeasureResolves(loaded, spec)) checks.Add(c);
        foreach (var c in RelationshipKeyCoverage(loaded, spec)) checks.Add(c);

        string status = WorstStatus(checks);

        var tableSummary = new JsonArray();
        foreach (var t in loaded)
            tableSummary.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["rows"] = t.RowCount,
                ["columns"] = t.Columns.Count,
            });

        return new JsonObject
        {
            ["status"] = status,
            ["generatedAt"] = DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            ["summary"] = Summarise(status, loaded, checks),
            ["tables"] = tableSummary,
            ["checks"] = checks,
        };
    }

    /// <summary>Build the report and persist it as <c>verification.json</c> in <paramref name="projectDir"/>,
    /// returning the in-memory object too. A write failure never fails the build - the object is still returned
    /// (the persisted copy is a convenience for the desktop app, not the source of truth).</summary>
    public static JsonObject BuildAndSave(
        IReadOnlyDictionary<string, string> tables,
        string? modelSpec,
        string? dataFolder,
        string projectDir)
    {
        var report = Build(tables, modelSpec, dataFolder);
        try
        {
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, FileName), report.ToJsonString(Cli.Pretty));
        }
        catch { /* the persisted copy is best-effort; the returned object is authoritative */ }
        return report;
    }

    // ---- checks --------------------------------------------------------------------------------

    /// <summary>rows-loaded per table: FAIL if a table loaded 0 data rows (the build "succeeded" but the
    /// dashboard would be empty). One check per table so the customer sees exactly which one is empty.</summary>
    private static IEnumerable<JsonObject> RowsLoaded(IReadOnlyList<LoadedTable> tables)
    {
        foreach (var t in tables)
        {
            if (t.ReadError != null)
            {
                yield return Check($"rows-loaded:{t.Name}", $"Rows loaded - {t.Name}", Fail,
                    $"Could not read the {t.Name} table: {t.ReadError}");
                continue;
            }
            yield return t.RowCount > 0
                ? Check($"rows-loaded:{t.Name}", $"Rows loaded - {t.Name}", Pass, $"{t.RowCount:N0} rows loaded.")
                : Check($"rows-loaded:{t.Name}", $"Rows loaded - {t.Name}", Fail, "0 rows loaded - this table is empty.");
        }
    }

    /// <summary>no all-null/all-blank column: WARN per column that is blank in every loaded row (the column
    /// carries no signal and likely points at a mapping mistake). Skipped for an empty table (the rows-loaded
    /// FAIL already covers it).</summary>
    private static IEnumerable<JsonObject> NoAllNullColumn(IReadOnlyList<LoadedTable> tables)
    {
        foreach (var t in tables)
        {
            if (t.ReadError != null || t.RowCount == 0) continue;
            for (int c = 0; c < t.Columns.Count; c++)
            {
                if (t.NonBlankCount[c] == 0)
                    yield return Check($"all-null:{t.Name}.{t.Columns[c]}", $"Column has data - {t.Name}[{t.Columns[c]}]", Warn,
                        $"The {t.Columns[c]} column is blank in every loaded row.");
            }
        }
    }

    /// <summary>calendar continuity: when the model has a Calendar table (or a table named calendar/date) with
    /// a single date column, WARN if its loaded dates have gaps at the inferred grain (daily/weekly/monthly).
    /// Omitted entirely when no calendar table is present (NEVER invented).</summary>
    private static IEnumerable<JsonObject> CalendarContinuity(
        IReadOnlyList<LoadedTable> tables, JsonObject? spec, string? dataFolder)
    {
        var cal = tables.FirstOrDefault(t =>
            t.ReadError == null &&
            (t.Name.Equals("Calendar", StringComparison.OrdinalIgnoreCase) ||
             t.Name.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
             t.Name.Equals("calendar", StringComparison.OrdinalIgnoreCase)));
        if (cal == null) yield break;

        int dateCol = DateColumnIndex(cal, spec);
        if (dateCol < 0) yield break; // no resolvable date column - cannot prove continuity, so omit

        var dates = new List<DateTime>();
        foreach (var v in cal.Column(dateCol))
            if (TryDate(v, out var d)) dates.Add(d.Date);
        dates = dates.Distinct().OrderBy(d => d).ToList();
        if (dates.Count < 3) yield break; // too few points to judge a grain / a gap

        var (grain, gaps) = FindGaps(dates);
        yield return gaps.Count == 0
            ? Check("calendar-continuity", "Calendar continuity", Pass,
                $"{dates.Count:N0} {grain} periods from {dates[0]:yyyy-MM-dd} to {dates[^1]:yyyy-MM-dd}, no gaps.")
            : Check("calendar-continuity", "Calendar continuity", Warn,
                $"{gaps.Count} gap(s) in the {grain} calendar (e.g. after {gaps[0]:yyyy-MM-dd}).");
    }

    /// <summary>refresh-safety: scan every measure DAX and every table's M for a hardcoded absolute date or a
    /// literal "today". A hardcoded date in a time-intelligence measure silently freezes on the next refresh,
    /// so it FAILs; a hardcoded date elsewhere (e.g. an M filter) WARNs. TODAY()/NOW() are fine (they move with
    /// the refresh) and are not flagged.</summary>
    private static IEnumerable<JsonObject> RefreshSafety(JsonObject? spec)
    {
        if (spec == null) yield break;

        var hits = new List<string>();
        bool inMeasure = false;

        if (spec["tables"] is JsonArray tbls)
            foreach (var tn in tbls)
            {
                if (tn is not JsonObject to) continue;
                string table = (string?)to["name"] ?? "?";

                if (to["measures"] is JsonArray measures)
                    foreach (var mn in measures)
                    {
                        if (mn is not JsonObject mo) continue;
                        string name = (string?)mo["name"] ?? "?";
                        string dax = (string?)mo["dax"] ?? "";
                        if (HasHardcodedDate(dax)) { hits.Add($"measure [{name}]"); inMeasure = true; }
                    }

                string m = (string?)to["m"] ?? "";
                if (HasHardcodedDate(m)) hits.Add($"{table} query");
            }

        if (hits.Count == 0)
        {
            yield return Check("refresh-safety", "Refresh safety", Pass,
                "No measure or query hardcodes an absolute date - the dashboard will move with each scheduled refresh.");
            yield break;
        }

        // a frozen measure is worse than a frozen query filter: a measure drives the numbers on the page.
        string status = inMeasure ? Fail : Warn;
        yield return Check("refresh-safety", "Refresh safety", status,
            $"Hardcoded date(s) found in {string.Join(", ", hits.Take(5))}" +
            (hits.Count > 5 ? $" and {hits.Count - 5} more" : "") +
            " - these will not move on the next refresh. Use TODAY()/NOW() or a calendar relationship instead.");
    }

    /// <summary>primary numeric measure resolves: take the model's first numeric measure, find the column it
    /// sums/averages, and WARN if that column is empty in the loaded data (so the headline number would be
    /// blank). Omitted when there is no measure or the column reference cannot be resolved.</summary>
    private static IEnumerable<JsonObject> PrimaryMeasureResolves(
        IReadOnlyList<LoadedTable> tables, JsonObject? spec)
    {
        if (spec?["tables"] is not JsonArray tbls) yield break;

        foreach (var tn in tbls)
        {
            if (tn is not JsonObject to || to["measures"] is not JsonArray measures) continue;
            foreach (var mn in measures)
            {
                if (mn is not JsonObject mo) continue;
                string name = (string?)mo["name"] ?? "?";
                string dax = (string?)mo["dax"] ?? "";
                // resolve only the simple, unambiguous aggregations - SUM/AVERAGE/MIN/MAX/COUNT(Table[Col]).
                if (!TryResolveAggColumn(dax, out string tbl, out string col)) continue;

                var lt = tables.FirstOrDefault(t => t.Name.Equals(tbl, StringComparison.OrdinalIgnoreCase));
                if (lt == null || lt.ReadError != null) yield break; // can't prove it - omit rather than invent
                int ci = lt.IndexOf(col);
                if (ci < 0) yield break;

                yield return lt.NonBlankCount[ci] > 0
                    ? Check("primary-measure", "Primary measure resolves", Pass,
                        $"[{name}] resolves against {tbl}[{col}] ({lt.NonBlankCount[ci]:N0} non-blank values).")
                    : Check("primary-measure", "Primary measure resolves", Warn,
                        $"[{name}] aggregates {tbl}[{col}], which is blank in every loaded row - the headline value would be blank.");
                yield break; // first resolvable numeric measure only
            }
        }
    }

    /// <summary>relationship key coverage: for each relationship whose BOTH key columns are present in the
    /// loaded CSVs, WARN if a from-side key has no match on the to-side (an orphan that drops rows from a
    /// related visual). Omitted for a relationship whose columns are not both loaded (cannot be judged).</summary>
    private static IEnumerable<JsonObject> RelationshipKeyCoverage(
        IReadOnlyList<LoadedTable> tables, JsonObject? spec)
    {
        if (spec?["relationships"] is not JsonArray rels) yield break;

        foreach (var rn in rels)
        {
            if (rn is not JsonObject ro) continue;
            string fromT = (string?)ro["fromTable"] ?? "", fromC = (string?)ro["fromColumn"] ?? "";
            string toT = (string?)ro["toTable"] ?? "", toC = (string?)ro["toColumn"] ?? "";
            if (fromT.Length == 0 || toT.Length == 0) continue;

            var from = tables.FirstOrDefault(t => t.Name.Equals(fromT, StringComparison.OrdinalIgnoreCase));
            var to = tables.FirstOrDefault(t => t.Name.Equals(toT, StringComparison.OrdinalIgnoreCase));
            if (from == null || to == null || from.ReadError != null || to.ReadError != null) continue;
            int fi = from.IndexOf(fromC), ti = to.IndexOf(toC);
            if (fi < 0 || ti < 0) continue;

            var toKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var v in to.Column(ti)) if (!string.IsNullOrWhiteSpace(v)) toKeys.Add(v.Trim());

            int orphans = 0;
            foreach (var v in from.Column(fi))
                if (!string.IsNullOrWhiteSpace(v) && !toKeys.Contains(v.Trim())) orphans++;

            string id = $"rel-coverage:{fromT}.{fromC}->{toT}.{toC}";
            yield return orphans == 0
                ? Check(id, $"Relationship coverage - {fromT}[{fromC}] -> {toT}[{toC}]", Pass,
                    "Every foreign key matches a row on the related table.")
                : Check(id, $"Relationship coverage - {fromT}[{fromC}] -> {toT}[{toC}]", Warn,
                    $"{orphans:N0} {fromT}[{fromC}] value(s) have no match in {toT}[{toC}] - those rows drop from related visuals.");
        }
    }

    // ---- refresh-safety helpers ----------------------------------------------------------------

    /// <summary>True when the expression embeds an absolute calendar date - a DATE(y,m,d) literal, an ISO
    /// yyyy-MM-dd date literal, or the bare word "today" (a comment/marker that something was pinned). TODAY()
    /// and NOW() are refresh-safe and deliberately not matched here.</summary>
    internal static bool HasHardcodedDate(string expr)
    {
        if (string.IsNullOrWhiteSpace(expr)) return false;

        // DATE( 2024 , 1 , 1 ) literal
        if (System.Text.RegularExpressions.Regex.IsMatch(expr,
                @"\bDATE\s*\(\s*\d{4}\s*,\s*\d{1,2}\s*,\s*\d{1,2}\s*\)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;

        // a "yyyy-MM-dd" or "yyyy/MM/dd" quoted/embedded date literal
        if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"\b\d{4}[-/]\d{2}[-/]\d{2}\b"))
            return true;

        // a literal "today" word that is NOT the TODAY() function (e.g. "today" pinned in a comment / string)
        if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"\btoday\b(?!\s*\()",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    /// <summary>Pull the single Table[Column] reference out of a simple one-aggregation measure
    /// (SUM/AVERAGE/MIN/MAX/COUNT), used by the primary-measure check. Returns false for anything with no, or
    /// more than one, column reference (a composite measure is not a thing we can prove from raw data).</summary>
    internal static bool TryResolveAggColumn(string dax, out string table, out string column)
    {
        table = ""; column = "";
        if (string.IsNullOrWhiteSpace(dax)) return false;
        if (!System.Text.RegularExpressions.Regex.IsMatch(dax, @"\b(SUM|AVERAGE|MIN|MAX|COUNT|COUNTA)\s*\(",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return false;

        var refs = System.Text.RegularExpressions.Regex.Matches(dax, @"([A-Za-z_][\w ]*)\[([^\]]+)\]");
        if (refs.Count != 1) return false; // exactly one column reference keeps it unambiguous
        table = refs[0].Groups[1].Value.Trim();
        column = refs[0].Groups[2].Value.Trim();
        return table.Length > 0 && column.Length > 0;
    }

    // ---- calendar helpers ----------------------------------------------------------------------

    /// <summary>Resolve the index of the calendar's date column: prefer a spec column typed "date", else the
    /// first loaded column whose values all parse as dates. Returns -1 when none can be found.</summary>
    private static int DateColumnIndex(LoadedTable cal, JsonObject? spec)
    {
        // 1. a spec column typed "date" on this table
        if (spec?["tables"] is JsonArray tbls)
            foreach (var tn in tbls)
            {
                if (tn is not JsonObject to) continue;
                if (!string.Equals((string?)to["name"], cal.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (to["columns"] is JsonArray cols)
                    foreach (var cn in cols)
                        if (cn is JsonObject co &&
                            string.Equals((string?)co["dataType"], "date", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = cal.IndexOf((string?)co["name"] ?? "");
                            if (idx >= 0) return idx;
                        }
            }

        // 2. the first loaded column that is entirely dates
        for (int c = 0; c < cal.Columns.Count; c++)
        {
            bool any = false, allDate = true;
            foreach (var v in cal.Column(c))
            {
                if (string.IsNullOrWhiteSpace(v)) continue;
                any = true;
                if (!TryDate(v, out _)) { allDate = false; break; }
            }
            if (any && allDate) return c;
        }
        return -1;
    }

    /// <summary>Infer the grain from the most common gap between consecutive sorted dates (1d / 7d / month),
    /// then list the dates after which the next expected period is missing.</summary>
    private static (string grain, List<DateTime> gaps) FindGaps(List<DateTime> dates)
    {
        // most common day-delta between neighbours decides the grain
        var deltas = new Dictionary<int, int>();
        for (int i = 1; i < dates.Count; i++)
        {
            int d = (int)(dates[i] - dates[i - 1]).TotalDays;
            if (d <= 0) continue;
            deltas[d] = deltas.TryGetValue(d, out var n) ? n + 1 : 1;
        }
        if (deltas.Count == 0) return ("daily", new List<DateTime>());
        int step = deltas.OrderByDescending(kv => kv.Value).First().Key;

        var gaps = new List<DateTime>();
        string grain;
        if (step is >= 28 and <= 31)
        {
            grain = "monthly";
            for (int i = 1; i < dates.Count; i++)
                if (MonthsBetween(dates[i - 1], dates[i]) > 1) gaps.Add(dates[i - 1]);
        }
        else
        {
            grain = step == 7 ? "weekly" : step == 1 ? "daily" : $"{step}-day";
            for (int i = 1; i < dates.Count; i++)
            {
                int gap = (int)(dates[i] - dates[i - 1]).TotalDays;
                if (gap > step) gaps.Add(dates[i - 1]);
            }
        }
        return (grain, gaps);
    }

    private static int MonthsBetween(DateTime a, DateTime b) => (b.Year - a.Year) * 12 + (b.Month - a.Month);

    private static bool TryDate(string? v, out DateTime d)
    {
        d = default;
        if (string.IsNullOrWhiteSpace(v)) return false;
        return DateTime.TryParse(v.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out d);
    }

    // ---- table reading -------------------------------------------------------------------------

    private static List<LoadedTable> ReadTables(IReadOnlyDictionary<string, string> tables)
    {
        var list = new List<LoadedTable>();
        foreach (var kv in tables)
            list.Add(LoadedTable.Read(kv.Key, kv.Value));
        return list;
    }

    // ---- status / summary ----------------------------------------------------------------------

    /// <summary>The overall status is the WORST check that ran (fail > warn > pass). With no checks at all
    /// nothing could be proven, so the status is "warn" - never a silent "pass".</summary>
    internal static string WorstStatus(JsonArray checks)
    {
        bool anyFail = false, anyWarn = false, anyPass = false;
        foreach (var c in checks)
        {
            switch ((string?)c?["status"])
            {
                case Fail: anyFail = true; break;
                case Warn: anyWarn = true; break;
                case Pass: anyPass = true; break;
            }
        }
        if (anyFail) return Fail;
        if (anyWarn) return Warn;
        return anyPass ? Pass : Warn;
    }

    private static string Summarise(string status, IReadOnlyList<LoadedTable> tables, JsonArray checks)
    {
        int tableCount = tables.Count;
        long rows = tables.Where(t => t.ReadError == null).Sum(t => (long)t.RowCount);
        int failed = checks.Count(c => (string?)c?["status"] == Fail);
        int warned = checks.Count(c => (string?)c?["status"] == Warn);

        return status switch
        {
            Fail => $"Verification failed: {failed} blocking issue(s) across {tableCount} table(s). Review before publishing.",
            // a warn with no warnings is the no-checks-could-be-computed case (WorstStatus warns on an empty
            // checks array): say so honestly instead of the self-contradictory "passed with 0 warning(s)".
            Warn when warned == 0 => $"Verification could not be fully proven: no checks were computable over {tableCount} table(s).",
            Warn => $"Verification passed with {warned} warning(s): {rows:N0} rows across {tableCount} table(s) loaded.",
            _ => $"Verification passed: {rows:N0} rows across {tableCount} table(s) loaded and checked.",
        };
    }

    private static JsonObject Check(string id, string label, string status, string detail) => new()
    {
        ["id"] = id,
        ["label"] = label,
        ["status"] = status,
        ["detail"] = detail,
    };

    private static JsonObject? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch { return null; }
    }

    // ---- a CSV loaded just far enough to run the checks ----------------------------------------

    /// <summary>One ingested table read into memory up to the scan cap: the header, the total data-row count
    /// (whole file), a bounded sample of rows for the column-level checks, and a per-column non-blank tally.</summary>
    private sealed class LoadedTable
    {
        public string Name { get; private init; } = "";
        public List<string> Columns { get; } = new();
        public int RowCount { get; private set; }          // total data rows in the file (not the sample size)
        public int[] NonBlankCount { get; private set; } = Array.Empty<int>();
        public string? ReadError { get; private set; }

        private readonly List<string[]> _sample = new();   // up to ScanRows rows for column-level checks

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
                t.NonBlankCount = new int[t.Columns.Count];

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    // a trailing blank line is not a data row
                    if (line.Length == 0 && reader.EndOfStream) break;
                    t.RowCount++;
                    var fields = SplitCsvLine(line);
                    if (t._sample.Count < ScanRows) t._sample.Add(fields);
                    for (int i = 0; i < t.Columns.Count; i++)
                        if (i < fields.Length && !string.IsNullOrWhiteSpace(fields[i])) t.NonBlankCount[i]++;
                }
            }
            catch (Exception ex) { t.ReadError = ex.Message; }
            return t;
        }

        // RFC-4180 line splitter (quoted fields with embedded commas/quotes), matching CloudFileStaging's.
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
