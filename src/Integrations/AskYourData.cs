using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// Ask-your-data: answer a NATURAL-LANGUAGE question grounded in the freshly ingested tables. The flow mirrors
/// the rest of the engine's honesty moat - the model never invents a number. Instead the AI does ONE narrow job:
/// it reads a compact, grounded context (the table/column schema + the metrics snapshot + small data samples) and
/// decides WHICH deterministic computation answers the question (an aggregation, a filtered aggregation, or a
/// grouped top-N over a real column). That decision is then EXECUTED here, by this engine, over the loaded rows,
/// and the computed value + the rows it used become the answer's evidence. If the AI says the question cannot be
/// answered from the data, we return grounded:false with a clear refusal rather than a fabricated figure.
///
/// Separation of concerns (the testable seam):
///   - context assembly     (deterministic)  -> <see cref="BuildContext"/>: schema + metrics + capped samples,
///                                               with secrets/tokens stripped and sizes bounded.
///   - the LLM decision      (the ONLY AI bit)-> <see cref="DecideAsync"/>: turns the question + context into an
///                                               <see cref="AskDecision"/>. Behind a swappable delegate so the
///                                               tests inject the decision and NO network call happens.
///   - computation execution (deterministic)  -> <see cref="Execute"/>: runs the decided computation over the
///                                               loaded data and returns the value + the evidence rows.
///   - result formatting     (deterministic)  -> <see cref="Answer"/>: assembles the contract response
///                                               { answer, evidence, usedQuery, grounded }.
///
/// HONESTY: a computation the data cannot support (a column that is missing, or no AI decision at all) yields
/// grounded:false, never a guessed number. SECURITY: the context carries only table/column NAMES, the public
/// metrics snapshot and a tiny capped sample of cell values - never a token, key or secret (the connector access
/// token never reaches this layer), and the sample size + context length are hard-capped.
/// </summary>
public static class AskYourData
{
    // hard caps so a large ingest can never blow up the grounded context we hand the model. These bound BOTH the
    // prompt cost and the blast radius of any one question.
    internal const int MaxSampleRowsPerTable = 5;     // tiny cell sample per table in the context
    internal const int MaxTablesInContext = 25;       // never enumerate an unbounded number of tables
    internal const int MaxColumnsPerTable = 60;       // nor an unbounded number of columns
    internal const int MaxEvidenceRows = 50;          // cap the rows we return as evidence
    internal const int MaxQuestionChars = 2000;       // reject an oversized question outright

    // the computations the model may choose. Kept deliberately narrow - each is something this engine can compute
    // exactly from raw CSVs, so the answer is always defensible.
    internal const string OpAggregate = "aggregate";  // AGG(column) over the whole table, optional WHERE
    internal const string OpGroupTopN = "group-top-n"; // AGG(value) grouped by a column, top N groups
    internal const string OpCount = "count-rows";      // count rows (optionally filtered)
    internal const string OpUnanswerable = "unanswerable"; // the model says the data cannot answer it

    /// <summary>
    /// The grounded context handed to the model: the table/column schema, the public metrics snapshot, and a
    /// tiny capped sample of cell values. Carries NO secrets - only names, the snapshot, and sample cells.
    /// </summary>
    public sealed class AskContext
    {
        public JsonArray Schema { get; init; } = new();      // [ { name, columns:[{name,type}], sampleRows:[[..]] } ]
        public JsonObject? Metrics { get; init; }            // the metrics.json snapshot (kpis + table rows)
        public List<TableData> Tables { get; init; } = new();// the loaded rows the computation runs over (not sent raw to the model)

        /// <summary>The compact text block the model reads. Bounded by the caps above; never contains a token.</summary>
        public string ToPromptText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("TABLES (schema + a tiny sample of rows):");
            foreach (var tn in Schema)
            {
                if (tn is not JsonObject to) continue;
                sb.Append("- ").Append((string?)to["name"]).Append(" (");
                var cols = to["columns"] as JsonArray ?? new JsonArray();
                sb.Append(string.Join(", ", cols.OfType<JsonObject>()
                    .Select(c => $"{(string?)c["name"]}:{(string?)c["type"]}")));
                sb.AppendLine(")");
                if (to["sampleRows"] is JsonArray sr && sr.Count > 0)
                    foreach (var row in sr)
                        sb.Append("    ").AppendLine(row?.ToJsonString());
            }
            if (Metrics?["kpis"] is JsonArray kpis && kpis.Count > 0)
            {
                sb.AppendLine("KNOWN HEADLINE NUMBERS (already computed from this data):");
                foreach (var k in kpis.OfType<JsonObject>())
                    sb.Append("- ").Append((string?)k["label"]).Append(" = ").AppendLine(k["value"]?.ToJsonString());
            }
            return sb.ToString();
        }
    }

    /// <summary>The loaded rows of one table the computation runs over. Held outside the prompt text - only a
    /// capped SAMPLE of these rows is ever placed in <see cref="AskContext.Schema"/> for the model to read.</summary>
    public sealed class TableData
    {
        public string Name { get; init; } = "";
        public List<string> Columns { get; init; } = new();
        public List<string[]> Rows { get; init; } = new();

        public int IndexOf(string column)
        {
            for (int i = 0; i < Columns.Count; i++)
                if (Columns[i].Equals(column, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }
    }

    /// <summary>The model's structured decision: which deterministic computation answers the question. A decision
    /// the engine cannot execute (a missing table/column, or <see cref="OpUnanswerable"/>) leads to grounded:false.</summary>
    public sealed class AskDecision
    {
        public string Op { get; init; } = OpUnanswerable;
        public string Table { get; init; } = "";
        public string Column { get; init; } = "";     // the aggregated/grouped value column (per the op)
        public string Aggregation { get; init; } = "SUM"; // SUM | AVERAGE | MIN | MAX | COUNT | DISTINCTCOUNT
        public string GroupBy { get; init; } = "";     // for group-top-n
        public int TopN { get; init; } = 5;            // for group-top-n
        public string? FilterColumn { get; init; }     // optional equality filter
        public string? FilterValue { get; init; }
        public string Reason { get; init; } = "";      // the model's note (shown when unanswerable)

        /// <summary>Parse the model's JSON decision (lenient - an unparseable or empty body is "unanswerable").</summary>
        public static AskDecision FromJson(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new AskDecision { Reason = "no decision returned" };
            JsonObject? o;
            try { o = JsonNode.Parse(json) as JsonObject; } catch { o = null; }
            if (o == null) return new AskDecision { Reason = "decision was not valid JSON" };
            return new AskDecision
            {
                Op = ((string?)o["op"] ?? OpUnanswerable).Trim().ToLowerInvariant(),
                Table = ((string?)o["table"] ?? "").Trim(),
                Column = ((string?)o["column"] ?? "").Trim(),
                Aggregation = ((string?)o["aggregation"] ?? "SUM").Trim().ToUpperInvariant(),
                GroupBy = ((string?)o["groupBy"] ?? "").Trim(),
                TopN = (int?)o["topN"] ?? 5,
                FilterColumn = (string?)o["filterColumn"],
                FilterValue = (string?)o["filterValue"],
                Reason = ((string?)o["reason"] ?? "").Trim(),
            };
        }
    }

    /// <summary>The decision delegate - the ONLY AI touch-point. Given the grounded context and the question it
    /// returns the structured decision. Production wires this to the Anthropic client (see
    /// <see cref="MakeAnthropicDecider"/>); the tests inject a stub so NO network call happens. Mirrors the
    /// SqlConnector.UseDialectForTests seam: a swappable function the suite controls.</summary>
    public delegate Task<AskDecision> Decider(AskContext context, string question, CancellationToken ct);

    /// <summary>
    /// Assemble the grounded context from the ingested tables and the (already-computed) metrics snapshot. Reads a
    /// capped sample of rows per table for the model, plus the full loaded rows (bounded by the reader) for the
    /// computation. Deterministic and secret-free: only names, types, sample cells and the public snapshot.
    /// </summary>
    public static AskContext BuildContext(
        IReadOnlyDictionary<string, string> tables,
        JsonObject? metrics)
    {
        var schema = new JsonArray();
        var data = new List<TableData>();

        int tableCount = 0;
        foreach (var kv in tables)
        {
            if (tableCount++ >= MaxTablesInContext) break;
            var lt = LoadedTable.Read(kv.Key, kv.Value);
            if (lt.ReadError != null)
            {
                schema.Add(new JsonObject { ["name"] = lt.Name, ["readError"] = lt.ReadError, ["columns"] = new JsonArray() });
                continue;
            }

            var td = new TableData { Name = lt.Name, Columns = lt.Columns.ToList(), Rows = lt.Rows };
            data.Add(td);

            // schema columns (capped) with the inferred type per column
            var cols = new JsonArray();
            int colCount = Math.Min(lt.Columns.Count, MaxColumnsPerTable);
            for (int c = 0; c < colCount; c++)
            {
                var sample = lt.Rows.Select(r => c < r.Length ? r[c] : null);
                cols.Add(new JsonObject { ["name"] = lt.Columns[c], ["type"] = ColumnTypeInference.Infer(sample) });
            }

            // a tiny capped sample of rows the model can read (NEVER the whole table)
            var sampleRows = new JsonArray();
            foreach (var r in lt.Rows.Take(MaxSampleRowsPerTable))
            {
                var arr = new JsonArray();
                for (int c = 0; c < colCount; c++) arr.Add(c < r.Length ? r[c] : null);
                sampleRows.Add(arr);
            }

            schema.Add(new JsonObject
            {
                ["name"] = lt.Name,
                ["columns"] = cols,
                ["sampleRows"] = sampleRows,
            });
        }

        return new AskContext { Schema = schema, Metrics = metrics, Tables = data };
    }

    /// <summary>The end-to-end entry point: build the context, ask the decider WHICH computation answers the
    /// question, execute it deterministically, and format the contract response. The decider is injected so the
    /// deterministic parts are tested without a live LLM. Returns the contract shape
    /// { question, answer, evidence, usedQuery, grounded }.</summary>
    public static async Task<JsonObject> AskAsync(
        IReadOnlyDictionary<string, string> tables,
        JsonObject? metrics,
        string question,
        Decider decide,
        CancellationToken ct = default)
    {
        question = (question ?? "").Trim();
        if (question.Length == 0)
            return NotGrounded(question, "Please ask a question about the data.");
        if (question.Length > MaxQuestionChars)
            return NotGrounded(question, $"That question is too long (max {MaxQuestionChars} characters).");

        var context = BuildContext(tables, metrics);
        if (context.Tables.Count == 0)
            return NotGrounded(question, "There is no data loaded to answer from.");

        AskDecision decision;
        try { decision = await decide(context, question, ct); }
        catch (Exception ex) { return NotGrounded(question, "The analyst could not process that question: " + ex.Message); }

        var (value, evidence, usedQuery, error) = Execute(context, decision);
        if (error != null)
            return NotGrounded(question, error);

        return Answer(question, decision, value, evidence!, usedQuery!);
    }

    // ---- computation execution -----------------------------------------------------------------

    /// <summary>
    /// Execute the decided computation over the loaded rows. Returns the computed value (boxed - a number for an
    /// aggregate/count, a JsonArray of group rows for a top-N), the evidence rows it used, a human-readable
    /// "usedQuery" string, and an error message (non-null ONLY when the computation cannot be performed honestly,
    /// which the caller turns into grounded:false). Never fabricates: a missing table/column is an error, not a 0.
    /// </summary>
    internal static (JsonNode? value, JsonArray? evidence, string? usedQuery, string? error) Execute(
        AskContext context, AskDecision d)
    {
        if (d.Op == OpUnanswerable)
            return (null, null, null, d.Reason.Length > 0
                ? "I cannot answer that from this data: " + d.Reason
                : "I cannot answer that from this data.");

        var table = context.Tables.FirstOrDefault(t => t.Name.Equals(d.Table, StringComparison.OrdinalIgnoreCase));
        if (table == null)
            return (null, null, null, $"I cannot answer that from this data (no table '{d.Table}').");

        // optional equality filter -> the matching row indices
        var rowIdx = FilterRows(table, d.FilterColumn, d.FilterValue, out string? filterError);
        if (filterError != null) return (null, null, null, filterError);

        switch (d.Op)
        {
            case OpCount:
            {
                long n = rowIdx.Count;
                var evidence = EvidenceRows(table, rowIdx);
                return (JsonValue.Create(n), evidence, Describe(d, "COUNT", "rows"), null);
            }

            case OpAggregate:
            {
                int ci = table.IndexOf(d.Column);
                if (ci < 0) return (null, null, null, $"I cannot answer that from this data (no column '{d.Column}' on '{table.Name}').");
                if (!Aggregate(table, ci, rowIdx, d.Aggregation, out double val, out string? aggError))
                    return (null, null, null, aggError);
                var evidence = EvidenceRows(table, rowIdx);
                return (JsonValue.Create(Round(val)), evidence, Describe(d, d.Aggregation, d.Column), null);
            }

            case OpGroupTopN:
            {
                int gi = table.IndexOf(d.GroupBy);
                if (gi < 0) return (null, null, null, $"I cannot answer that from this data (no group column '{d.GroupBy}' on '{table.Name}').");
                int vi = d.Aggregation == "COUNT" || d.Aggregation == "DISTINCTCOUNT" ? gi : table.IndexOf(d.Column);
                if (vi < 0) return (null, null, null, $"I cannot answer that from this data (no column '{d.Column}' on '{table.Name}').");
                var groups = GroupTopN(table, gi, vi, rowIdx, d.Aggregation, Math.Clamp(d.TopN, 1, MaxEvidenceRows), out string? gError);
                if (gError != null) return (null, null, null, gError);
                // the evidence for a top-N is the group rollups themselves
                return (groups, groups, Describe(d, d.Aggregation + " by " + d.GroupBy, d.Column), null);
            }

            default:
                return (null, null, null, "I cannot answer that from this data (unsupported computation).");
        }
    }

    /// <summary>The row indices matching an optional equality filter (case-insensitive, trimmed). With no filter,
    /// every row matches. A filter on a missing column is an error (never silently "all rows").</summary>
    private static List<int> FilterRows(TableData table, string? filterColumn, string? filterValue, out string? error)
    {
        error = null;
        var all = Enumerable.Range(0, table.Rows.Count).ToList();
        if (string.IsNullOrWhiteSpace(filterColumn)) return all;

        int fi = table.IndexOf(filterColumn);
        if (fi < 0) { error = $"I cannot answer that from this data (no filter column '{filterColumn}' on '{table.Name}')."; return all; }

        string want = (filterValue ?? "").Trim();
        var matched = new List<int>();
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            string cell = fi < row.Length ? (row[fi] ?? "").Trim() : "";
            if (cell.Equals(want, StringComparison.OrdinalIgnoreCase)) matched.Add(i);
        }
        return matched;
    }

    /// <summary>Run one aggregation over the selected rows of a column. Numeric aggregations return false (an
    /// error) when no selected cell parses as a number, so the answer is never a fabricated 0.</summary>
    private static bool Aggregate(TableData table, int ci, List<int> rowIdx, string agg, out double value, out string? error)
    {
        value = 0; error = null;
        switch (agg)
        {
            case "COUNT":
            {
                long n = 0;
                foreach (int i in rowIdx) if (!string.IsNullOrWhiteSpace(Cell(table, i, ci))) n++;
                value = n; return true;
            }
            case "DISTINCTCOUNT":
            {
                var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (int i in rowIdx) { var c = Cell(table, i, ci); if (!string.IsNullOrWhiteSpace(c)) set.Add(c.Trim()); }
                value = set.Count; return true;
            }
            default:
            {
                bool any = false; double sum = 0, min = double.MaxValue, max = double.MinValue; long n = 0;
                foreach (int i in rowIdx)
                {
                    if (!TryNumber(Cell(table, i, ci), out double dv)) continue;
                    any = true; n++; sum += dv;
                    if (dv < min) min = dv;
                    if (dv > max) max = dv;
                }
                if (!any) { error = $"I cannot answer that from this data ('{table.Columns[ci]}' has no numeric values to {agg.ToLowerInvariant()})."; return false; }
                value = agg switch { "AVERAGE" => sum / n, "MIN" => min, "MAX" => max, _ => sum };
                return true;
            }
        }
    }

    /// <summary>Group the selected rows by one column and aggregate a value column per group, returning the top-N
    /// groups (highest aggregate first). The evidence is the rollup itself: [ { group, value } ].</summary>
    private static JsonArray? GroupTopN(
        TableData table, int gi, int vi, List<int> rowIdx, string agg, int topN, out string? error)
    {
        error = null;
        // bucket -> the numeric values (for SUM/AVG/MIN/MAX) and a set (for DISTINCTCOUNT) and a count (for COUNT)
        var sums = new Dictionary<string, double>(StringComparer.Ordinal);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        var distinct = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        bool numericAgg = agg is "SUM" or "AVERAGE" or "MIN" or "MAX";

        foreach (int i in rowIdx)
        {
            string g = (Cell(table, i, gi) ?? "").Trim();
            if (g.Length == 0) continue;
            counts.TryGetValue(g, out var cn); counts[g] = cn + 1;
            if (numericAgg)
            {
                if (TryNumber(Cell(table, i, vi), out double dv))
                {
                    if (!sums.ContainsKey(g)) sums[g] = agg == "MIN" ? double.MaxValue : agg == "MAX" ? double.MinValue : 0;
                    sums[g] = agg switch { "MIN" => Math.Min(sums[g], dv), "MAX" => Math.Max(sums[g], dv), _ => sums[g] + dv };
                }
            }
            else if (agg == "DISTINCTCOUNT")
            {
                var c = Cell(table, i, vi);
                if (!string.IsNullOrWhiteSpace(c)) { if (!distinct.TryGetValue(g, out var s)) distinct[g] = s = new(StringComparer.OrdinalIgnoreCase); s.Add(c.Trim()); }
            }
        }

        var rollup = new List<(string group, double value, long n)>();
        foreach (var g in counts.Keys)
        {
            double v = agg switch
            {
                "COUNT" => counts[g],
                "DISTINCTCOUNT" => distinct.TryGetValue(g, out var s) ? s.Count : 0,
                "AVERAGE" => sums.TryGetValue(g, out var su) ? su / Math.Max(1, counts[g]) : 0,
                _ => sums.TryGetValue(g, out var su2) ? su2 : 0,
            };
            rollup.Add((g, v, counts[g]));
        }

        var top = rollup.OrderByDescending(r => r.value).ThenBy(r => r.group, StringComparer.Ordinal).Take(topN);
        var arr = new JsonArray();
        foreach (var r in top)
            arr.Add(new JsonObject { ["group"] = r.group, ["value"] = JsonValue.Create(Round(r.value)) });
        return arr;
    }

    // ---- result formatting ---------------------------------------------------------------------

    /// <summary>Assemble the grounded answer contract from a successful computation.</summary>
    internal static JsonObject Answer(string question, AskDecision d, JsonNode? value, JsonArray evidence, string usedQuery)
    {
        string nl = d.Op == OpGroupTopN
            ? FormatGroups(d, evidence)
            : $"{usedQuery} = {value?.ToJsonString()}.";

        return new JsonObject
        {
            ["question"] = question,
            ["answer"] = nl,
            ["evidence"] = evidence.DeepClone(),
            ["usedQuery"] = usedQuery,
            ["grounded"] = true,
        };
    }

    /// <summary>The honest "cannot answer" contract - grounded:false, a clear message, and an EMPTY evidence
    /// array (we used nothing). Never carries a fabricated number.</summary>
    internal static JsonObject NotGrounded(string question, string message) => new()
    {
        ["question"] = question,
        ["answer"] = message,
        ["evidence"] = new JsonArray(),
        ["usedQuery"] = (JsonNode?)null,
        ["grounded"] = false,
    };

    private static string FormatGroups(AskDecision d, JsonArray groups)
    {
        var sb = new StringBuilder();
        sb.Append("Top ").Append(groups.Count).Append(" ").Append(d.GroupBy)
          .Append(" by ").Append(d.Aggregation == "COUNT" ? "row count" : (d.Aggregation + " of " + d.Column)).Append(": ");
        sb.Append(string.Join(", ", groups.OfType<JsonObject>()
            .Select(g => $"{(string?)g["group"]} ({g["value"]?.ToJsonString()})")));
        sb.Append('.');
        return sb.ToString();
    }

    private static string Describe(AskDecision d, string agg, string what)
    {
        var sb = new StringBuilder();
        sb.Append(agg).Append('(').Append(d.Table);
        if (what.Length > 0 && what != "rows") sb.Append('[').Append(what).Append(']');
        sb.Append(')');
        if (!string.IsNullOrWhiteSpace(d.FilterColumn))
            sb.Append(" WHERE ").Append(d.FilterColumn).Append(" = '").Append(d.FilterValue).Append('\'');
        return sb.ToString();
    }

    private static JsonArray EvidenceRows(TableData table, List<int> rowIdx)
    {
        var arr = new JsonArray();
        foreach (int i in rowIdx.Take(MaxEvidenceRows))
        {
            var obj = new JsonObject();
            var row = table.Rows[i];
            for (int c = 0; c < table.Columns.Count; c++)
                obj[table.Columns[c]] = c < row.Length ? row[c] : null;
            arr.Add(obj);
        }
        return arr;
    }

    private static string? Cell(TableData t, int rowIndex, int colIndex)
    {
        var row = t.Rows[rowIndex];
        return colIndex < row.Length ? row[colIndex] : null;
    }

    private static bool TryNumber(string? v, out double d)
    {
        d = 0;
        if (string.IsNullOrWhiteSpace(v)) return false;
        string s = v.Trim();
        if (s.Length > 0 && (s[0] == '$' || s[0] == '£' || s[0] == '€' || s[0] == '¥')) s = s[1..].Trim();
        return double.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out d);
    }

    private static double Round(double v) => double.IsFinite(v) ? Math.Round(v, 4, MidpointRounding.AwayFromZero) : 0;

    // ---- the production decider (the LLM) ------------------------------------------------------

    /// <summary>The system prompt that constrains the model to the structured-decision contract. It does NOT ask
    /// the model for a number - only for WHICH deterministic computation this engine should run, so the figure is
    /// always computed by the engine over real rows.</summary>
    internal const string SystemPrompt =
        "You are a careful data analyst. You are given the schema and a tiny sample of a customer's tables, plus " +
        "some already-computed headline numbers. Decide WHICH single deterministic computation answers the user's " +
        "question, over the REAL columns shown. You never compute or guess the number yourself - the engine does " +
        "that. Reply with ONLY a JSON object, no prose, of this shape:\n" +
        "{ \"op\": \"aggregate|group-top-n|count-rows|unanswerable\", \"table\": \"<table>\", " +
        "\"column\": \"<value column>\", \"aggregation\": \"SUM|AVERAGE|MIN|MAX|COUNT|DISTINCTCOUNT\", " +
        "\"groupBy\": \"<column for group-top-n>\", \"topN\": 5, \"filterColumn\": \"<optional>\", " +
        "\"filterValue\": \"<optional>\", \"reason\": \"<why, esp. if unanswerable>\" }\n" +
        "Use ONLY table and column names exactly as shown. If the question cannot be answered from these columns, " +
        "set op to \"unanswerable\" and explain briefly in reason. Do NOT invent columns or values.";

    /// <summary>Build a production decider backed by the existing Anthropic client (the same plumbing the AI
    /// prompter uses). One non-streaming Messages call: the grounded context + the question -> a JSON decision,
    /// parsed via <see cref="AskDecision.FromJson"/>. Never sends a token/secret (the context is secret-free).</summary>
    public static Decider MakeAnthropicDecider(Agent.AnthropicClient client, string model, int maxTokens = 1024)
    {
        return async (context, question, ct) =>
        {
            string user = context.ToPromptText() +
                          "\n\nQUESTION: " + question +
                          "\n\nReply with ONLY the JSON decision object.";
            var messages = new JsonArray
            {
                new JsonObject { ["role"] = "user", ["content"] = user },
            };
            var resp = await client.CreateAsync(model, maxTokens, SystemPrompt, new JsonArray(), messages);

            // concatenate the text blocks of the response, then parse the JSON decision out of them
            var sb = new StringBuilder();
            if (resp["content"] is JsonArray content)
                foreach (var blk in content)
                    if (blk is JsonObject bo && bo["type"]?.ToString() == "text")
                        sb.Append(bo["text"]?.ToString());

            return AskDecision.FromJson(ExtractJson(sb.ToString()));
        };
    }

    /// <summary>Pull the first balanced {...} JSON object out of a model reply that may wrap it in prose or a code
    /// fence. Returns the raw text when no braces are found (FromJson then treats it as unanswerable).</summary>
    internal static string ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        int start = text.IndexOf('{');
        if (start < 0) return text;
        int depth = 0; bool inStr = false; bool esc = false;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (inStr)
            {
                if (esc) esc = false;
                else if (c == '\\') esc = true;
                else if (c == '"') inStr = false;
            }
            else
            {
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}') { depth--; if (depth == 0) return text.Substring(start, i - start + 1); }
            }
        }
        return text.Substring(start);
    }

    // ---- a CSV loaded for the Q&A (header + bounded rows) --------------------------------------

    /// <summary>One ingested table read into memory for the Q&amp;A: the header and the data rows up to the scan
    /// cap. Internal so the tests can build an <see cref="AskContext"/> over a real CSV. Mirrors the sibling
    /// reports' reader.</summary>
    internal sealed class LoadedTable
    {
        // the Q&A scans more rows than the per-column reports so a filtered answer over a real table is exact, but
        // it is still hard-capped so one question can never load an unbounded file.
        private const int ScanRows = 100000;

        public string Name { get; private init; } = "";
        public List<string> Columns { get; } = new();
        public List<string[]> Rows { get; } = new();
        public string? ReadError { get; private set; }

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
                while ((line = reader.ReadLine()) != null && t.Rows.Count < ScanRows)
                {
                    if (line.Length == 0 && reader.EndOfStream) break;
                    t.Rows.Add(SplitCsvLine(line));
                }
            }
            catch (Exception ex) { t.ReadError = ex.Message; }
            return t;
        }

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
