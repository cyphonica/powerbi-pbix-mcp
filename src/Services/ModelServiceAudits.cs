using System.Text;
using System.Text.RegularExpressions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>
/// Wave G2 read-only model audits: star-schema classification (audit_star_schema), the naming audit
/// with its PLAN-ONLY rename output (audit_naming), and the data dictionary export with description
/// coverage (export_data_dictionary). Each public method resolves the live session and delegates to a
/// static *Core over the TOM object tree, so the classification logic unit-tests against an in-memory
/// <c>new TOM.Model()</c> with no engine.
/// </summary>
public sealed partial class ModelService
{
    // ================================================================ audit_star_schema

    public object AuditStarSchema(string sessionId)
        => AuditStarSchemaCore(_sessions.GetModel(sessionId).Model);

    /// <summary>Classify every table (fact / dimension / date / bridge / disconnected) from the
    /// relationship topology + column types, then flag the schema smells: snowflaking, bidirectional
    /// filters, many-to-many, fact-to-fact relationships, a missing/unmarked date table and descriptive
    /// text columns on fact tables. Returns a scored report with recommendations.</summary>
    internal static object AuditStarSchemaCore(TOM.Model model)
    {
        var tables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();
        var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>()
            .Where(r => r.FromTable != null && r.ToTable != null
                        && !IsAutoDateName(r.FromTable.Name) && !IsAutoDateName(r.ToTable.Name))
            .ToList();

        // relationship-key columns per table (columns that participate in any relationship end)
        var relColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rels)
        {
            if (r.FromColumn != null) relColumns.Add($"{r.FromTable.Name}[{r.FromColumn.Name}]");
            if (r.ToColumn != null) relColumns.Add($"{r.ToTable.Name}[{r.ToColumn.Name}]");
        }

        // topology counts: manyOut = this table is the MANY side pointing at a ONE side (it references a
        // dimension); oneIn = this table is the ONE side (something references it as a dimension).
        int ManyOut(TOM.Table t) => rels.Count(r =>
            r.FromTable == t && r.FromCardinality == TOM.RelationshipEndCardinality.Many
            && r.ToCardinality == TOM.RelationshipEndCardinality.One);
        int OneIn(TOM.Table t) => rels.Count(r =>
            r.ToTable == t && r.ToCardinality == TOM.RelationshipEndCardinality.One);
        int AnyRel(TOM.Table t) => rels.Count(r => r.FromTable == t || r.ToTable == t);

        static bool LooksLikeDateName(string n) =>
            n.Contains("date", StringComparison.OrdinalIgnoreCase) || n.Contains("calendar", StringComparison.OrdinalIgnoreCase);
        static bool HasDateColumn(TOM.Table t) =>
            t.Columns.Any(c => c.Type != TOM.ColumnType.RowNumber && c.DataType == TOM.DataType.DateTime);
        static bool IsMarkedDateTable(TOM.Table t) =>
            string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase);

        var classification = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var reasons = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            int manyOut = ManyOut(t), oneIn = OneIn(t);
            int visibleColumns = t.Columns.Count(c => c.Type != TOM.ColumnType.RowNumber);
            var why = new List<string>();
            string cls;
            if (AnyRel(t) == 0)
            {
                cls = "disconnected";
                why.Add("participates in no relationship (parameter / what-if / annotation table).");
            }
            else if (IsMarkedDateTable(t) || (HasDateColumn(t) && LooksLikeDateName(t.Name) && oneIn > 0))
            {
                cls = "date";
                why.Add(IsMarkedDateTable(t) ? "marked as the date table (DataCategory=Time)."
                                             : "date-named table with a DateTime column, referenced as a dimension.");
            }
            else if (t.Measures.Count == 0 && manyOut >= 2 && visibleColumns <= 4)
            {
                cls = "bridge";
                why.Add($"no measures, {visibleColumns} column(s), on the many side of {manyOut} relationships (a key-resolver between dimensions/facts).");
            }
            else if ((manyOut >= 1 && (oneIn == 0 || t.Measures.Count > 0))
                     || (t.Measures.Count > 0 && oneIn == 0))
            {
                // the second arm catches a measure-bearing table whose only links are many-to-many
                // (no clean many->one edge) - still a fact for smell detection.
                cls = "fact";
                why.Add($"on the many side of {manyOut} relationship(s){(t.Measures.Count > 0 ? $", carries {t.Measures.Count} measure(s)" : "")}.");
            }
            else if (oneIn >= 1)
            {
                cls = "dimension";
                why.Add($"referenced as the one side by {oneIn} relationship(s).");
                if (manyOut >= 1) why.Add($"also references {manyOut} further table(s) - a snowflake link.");
            }
            else
            {
                cls = "dimension";
                why.Add("related but neither a clear fact nor a referenced dimension - defaulted to dimension; review.");
            }
            classification[t.Name] = cls;
            reasons[t.Name] = why;
        }

        // ---- schema smells ----
        var issues = new List<object>();
        var issueKinds = new List<string>();
        void Issue(string severity, string kind, string detail, string recommendation)
        {
            issueKinds.Add(kind);
            issues.Add(new { severity, kind, detail, recommendation });
        }

        foreach (var r in rels)
        {
            string from = r.FromTable.Name, to = r.ToTable.Name;
            string label = $"{from}[{r.FromColumn?.Name}] -> {to}[{r.ToColumn?.Name}]";

            if (r.CrossFilteringBehavior == TOM.CrossFilteringBehavior.BothDirections)
                Issue("warn", "bidirectional-filter", $"{label} cross-filters BOTH directions.",
                    "prefer single-direction filtering; replace with a measure-level CROSSFILTER or a bridge if the both-ways path is really needed (update_relationship).");

            if (r.FromCardinality == TOM.RelationshipEndCardinality.Many
                && r.ToCardinality == TOM.RelationshipEndCardinality.Many)
                Issue("warn", "many-to-many", $"{label} is many-to-many.",
                    "introduce a bridge table of distinct keys so both sides relate many-to-one (conform_dimension can build it).");

            if (classification.TryGetValue(from, out var fc) && classification.TryGetValue(to, out var tc))
            {
                if (fc == "fact" && tc == "fact")
                    Issue("warn", "fact-to-fact", $"{label} relates two FACT tables directly.",
                        "route both facts through shared conformed dimensions instead of relating them to each other.");
                if (fc == "dimension" && tc == "dimension")
                    Issue("info", "snowflake", $"{label} chains one dimension through another (snowflaking).",
                        "flatten the outer dimension's attributes into the inner one (merge_queries) so every dimension joins the fact directly.");
            }
        }

        bool anyMarkedDate = tables.Any(IsMarkedDateTable);
        bool anyFactWithDates = tables.Any(t => classification[t.Name] == "fact" && HasDateColumn(t));
        if (!anyMarkedDate)
        {
            var dateLike = tables.FirstOrDefault(t => classification[t.Name] == "date"
                                                      || (LooksLikeDateName(t.Name) && HasDateColumn(t)));
            if (dateLike != null)
                Issue("warn", "unmarked-date-table",
                    $"'{dateLike.Name}' looks like the date table but is not marked (DataCategory != Time).",
                    $"mark_as_date_table on '{dateLike.Name}' so time intelligence binds correctly.");
            else if (anyFactWithDates)
                Issue("warn", "missing-date-table",
                    "no date table exists, but fact tables carry DateTime columns (auto date/time will bloat the model).",
                    "create_date_table + mark_as_date_table, then relate the facts to it.");
        }

        foreach (var t in tables.Where(t => classification[t.Name] == "fact"))
        {
            var textCols = t.Columns
                .Where(c => c.Type != TOM.ColumnType.RowNumber && c.DataType == TOM.DataType.String
                            && !c.IsHidden && !relColumns.Contains($"{t.Name}[{c.Name}]"))
                .Select(c => c.Name).ToList();
            if (textCols.Count > 0)
                Issue("info", "text-on-fact",
                    $"fact '{t.Name}' carries {textCols.Count} visible text column(s): {string.Join(", ", textCols.Take(8))}{(textCols.Count > 8 ? ", ..." : "")}.",
                    "move descriptive text to a dimension (or hide the columns) - text on a wide fact bloats the dictionary and invites bad slicers.");
        }

        // ---- score: start at 100, deduct per smell (weight by kind), clamp at 0 ----
        int score = 100;
        foreach (var kind in issueKinds)
        {
            score -= kind switch
            {
                "many-to-many" => 12,
                "fact-to-fact" => 12,
                "bidirectional-filter" => 8,
                "missing-date-table" => 10,
                "unmarked-date-table" => 6,
                "snowflake" => 5,
                "text-on-fact" => 3,
                _ => 3,
            };
        }
        score = Math.Max(0, score);

        var counts = new
        {
            facts = classification.Values.Count(v => v == "fact"),
            dimensions = classification.Values.Count(v => v == "dimension"),
            dateTables = classification.Values.Count(v => v == "date"),
            bridges = classification.Values.Count(v => v == "bridge"),
            disconnected = classification.Values.Count(v => v == "disconnected"),
        };
        return new
        {
            ok = true,
            score,
            verdict = score >= 90 ? "clean star schema" : score >= 70 ? "minor deviations"
                : score >= 50 ? "needs review" : "significant redesign recommended",
            counts,
            tables = tables.Select(t => new
            {
                table = t.Name,
                classification = classification[t.Name],
                reasons = reasons[t.Name],
            }).ToList(),
            issues,
            issueCount = issues.Count,
        };
    }

    // ================================================================ audit_naming (PLAN ONLY)

    public object AuditNaming(string sessionId)
        => AuditNamingCore(_sessions.GetModel(sessionId).Model);

    // technical prefixes stripped from table names (the report layer wants human names).
    private static readonly string[] TechnicalPrefixes = { "dim_", "fact_", "dim ", "fact ", "tbl_", "vw_", "v_" };
    private static readonly Regex GluedPrefixRx = new("^(Dim|Fact)(?=[A-Z])", RegexOptions.Compiled);
    private static readonly Regex CamelBoundaryRx = new("(?<=[a-z0-9])(?=[A-Z])", RegexOptions.Compiled);

    /// <summary>Audit table / column / measure names (technical DIM_/FACT_ prefixes, snake_case,
    /// camelCase, leading/trailing/doubled spaces) and return an applyable RENAME PLAN
    /// {renames:[{objectType, table, oldName, newName, reason}]}. PLAN ONLY - nothing is renamed here;
    /// the propagating apply (rewriting DAX/M references) lands in a later wave.</summary>
    internal static object AuditNamingCore(TOM.Model model)
    {
        var renames = new List<object>();
        var skipped = new List<object>();
        var findings = new List<object>();

        var tables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();

        // ---- tables ----
        var finalTableNames = new HashSet<string>(tables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            var (proposed, why) = ProposeName(t.Name, stripTablePrefix: true);
            if (proposed == t.Name) continue;
            findings.Add(new { objectType = "table", table = t.Name, name = t.Name, issues = why });
            if (finalTableNames.Contains(proposed))
            {
                skipped.Add(new { objectType = "table", table = t.Name, oldName = t.Name, proposed,
                    reason = $"collision - a table named '{proposed}' already exists (or is already proposed)." });
                continue;
            }
            finalTableNames.Remove(t.Name);
            finalTableNames.Add(proposed);
            renames.Add(new { objectType = "table", table = t.Name, oldName = t.Name, newName = proposed,
                reason = string.Join("; ", why) });
        }

        // ---- columns (per table) + measures (model-wide name space) ----
        var finalMeasureNames = new HashSet<string>(
            tables.SelectMany(t => t.Measures).Select(mm => mm.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables)
        {
            var finalColumnNames = new HashSet<string>(
                t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber).Select(c => c.Name),
                StringComparer.OrdinalIgnoreCase);
            foreach (var c in t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber))
            {
                var (proposed, why) = ProposeName(c.Name, stripTablePrefix: false);
                if (proposed == c.Name) continue;
                findings.Add(new { objectType = "column", table = t.Name, name = c.Name, issues = why });
                if (finalColumnNames.Contains(proposed))
                {
                    skipped.Add(new { objectType = "column", table = t.Name, oldName = c.Name, proposed,
                        reason = $"collision - '{t.Name}' already has a column named '{proposed}'." });
                    continue;
                }
                finalColumnNames.Remove(c.Name);
                finalColumnNames.Add(proposed);
                renames.Add(new { objectType = "column", table = t.Name, oldName = c.Name, newName = proposed,
                    reason = string.Join("; ", why) });
            }

            foreach (var mm in t.Measures)
            {
                var (proposed, why) = ProposeName(mm.Name, stripTablePrefix: false);
                if (proposed == mm.Name) continue;
                findings.Add(new { objectType = "measure", table = t.Name, name = mm.Name, issues = why });
                // measures are referenced as [Name] model-wide, so the whole model is one name space.
                if (finalMeasureNames.Contains(proposed))
                {
                    skipped.Add(new { objectType = "measure", table = t.Name, oldName = mm.Name, proposed,
                        reason = $"collision - a measure named '{proposed}' already exists (measure names are model-wide)." });
                    continue;
                }
                finalMeasureNames.Remove(mm.Name);
                finalMeasureNames.Add(proposed);
                renames.Add(new { objectType = "measure", table = t.Name, oldName = mm.Name, newName = proposed,
                    reason = string.Join("; ", why) });
            }
        }

        return new
        {
            ok = true,
            planOnly = true,
            findingCount = findings.Count,
            findings,
            renamePlan = new { renames },
            renameCount = renames.Count,
            skipped,
            note = "PLAN ONLY - nothing was renamed. Apply selectively with rename_table / rename_column / "
                 + "set_measure_properties for now; note those do NOT rewrite DAX/M references (the "
                 + "propagating apply lands in a later wave).",
        };
    }

    /// <summary>Propose the human-readable form of one object name and the reasons it changes:
    /// trim + collapse whitespace, strip technical table prefixes (DIM_/FACT_/TBL_/VW_ and glued
    /// DimCustomer/FactSales), split snake_case underscores and camelCase boundaries into Title Case
    /// words (short all-caps acronyms are preserved). Returns the name unchanged when it is clean.</summary>
    internal static (string proposed, List<string> reasons) ProposeName(string raw, bool stripTablePrefix)
    {
        var reasons = new List<string>();
        string s = raw;

        string trimmed = s.Trim();
        if (trimmed != s) { reasons.Add("leading/trailing whitespace"); s = trimmed; }
        string collapsed = Regex.Replace(s, @"  +", " ");
        if (collapsed != s) { reasons.Add("doubled spaces"); s = collapsed; }

        if (stripTablePrefix)
        {
            foreach (var prefix in TechnicalPrefixes)
                if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && s.Length > prefix.Length)
                {
                    reasons.Add($"technical prefix '{s[..prefix.Length]}'");
                    s = s[prefix.Length..];
                    break;
                }
            var glued = GluedPrefixRx.Match(s);
            if (glued.Success)
            {
                reasons.Add($"technical prefix '{glued.Value}'");
                s = s[glued.Length..];
            }
        }

        if (s.Contains('_'))
        {
            reasons.Add("underscore separators (snake_case)");
            var words = s.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(TitleWord);
            s = string.Join(" ", words);
        }
        else if (!s.Contains(' ') && CamelBoundaryRx.IsMatch(s))
        {
            reasons.Add("camelCase/PascalCase without spaces");
            var words = CamelBoundaryRx.Split(s).Select(TitleWord);
            s = string.Join(" ", words);
        }

        if (s.Length > 0 && char.IsLower(s[0]))
        {
            reasons.Add("lowercase initial");
            s = char.ToUpperInvariant(s[0]) + s[1..];
        }

        return (s, s == raw ? new List<string>() : reasons);
    }

    // common short acronyms restored to caps when a snake_case word arrives lowercase (order_id -> Order ID).
    private static readonly HashSet<string> KnownAcronyms = new(StringComparer.OrdinalIgnoreCase)
        { "id", "sku", "gst", "url", "api", "kpi", "sql", "iso", "ytd", "mtd", "qtd" };

    /// <summary>Title-case one word, preserving short all-caps acronyms (ID, SKU, GST) and restoring
    /// known acronyms that arrive lowercase.</summary>
    private static string TitleWord(string w)
    {
        if (w.Length == 0) return w;
        if (w.Length <= 3 && w.All(char.IsUpper)) return w;
        if (KnownAcronyms.Contains(w)) return w.ToUpperInvariant();
        return char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();
    }

    // ================================================================ export_data_dictionary

    public object ExportDataDictionary(string sessionId, string format = "md")
        => ExportDataDictionaryCore(_sessions.GetModel(sessionId).Model, format);

    /// <summary>Render the model's data dictionary (tables / columns / measures / relationships with
    /// descriptions, types and format strings) as Markdown or HTML, plus a description COVERAGE score
    /// (the fraction of visible objects carrying a description). Rendered from the TOM tree only - no
    /// new model access paths, no queries.</summary>
    internal static object ExportDataDictionaryCore(TOM.Model model, string format)
    {
        string fmt = (format ?? "md").Trim().ToLowerInvariant();
        if (fmt is not ("md" or "markdown" or "html"))
            throw new ArgumentException($"unknown format '{format}' (use md|html).");
        bool html = fmt == "html";

        var tables = model.Tables.Where(t => !IsAutoDateName(t.Name)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

        // ---- coverage: visible tables + visible columns + all measures, described vs total ----
        int tTotal = 0, tDesc = 0, cTotal = 0, cDesc = 0, mTotal = 0, mDesc = 0;
        foreach (var t in tables)
        {
            if (!t.IsHidden) { tTotal++; if (!string.IsNullOrWhiteSpace(t.Description)) tDesc++; }
            foreach (var c in t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber && !c.IsHidden))
            { cTotal++; if (!string.IsNullOrWhiteSpace(c.Description)) cDesc++; }
            foreach (var mm in t.Measures)
            { mTotal++; if (!string.IsNullOrWhiteSpace(mm.Description)) mDesc++; }
        }
        int objects = tTotal + cTotal + mTotal, described = tDesc + cDesc + mDesc;
        double percent = objects == 0 ? 100 : Math.Round(100.0 * described / objects, 1);

        string content = html ? RenderDictionaryHtml(model, tables) : RenderDictionaryMd(model, tables);
        return new
        {
            ok = true,
            format = html ? "html" : "md",
            coverage = new
            {
                objects, described, percent,
                tables = $"{tDesc}/{tTotal}", columns = $"{cDesc}/{cTotal}", measures = $"{mDesc}/{mTotal}",
            },
            content,
        };
    }

    private static string MdCell(string? s) =>
        string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");

    private static string RenderDictionaryMd(TOM.Model model, List<TOM.Table> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Data dictionary - {model.Name}");
        sb.AppendLine();
        foreach (var t in tables)
        {
            sb.AppendLine($"## {t.Name}{(t.IsHidden ? " (hidden)" : "")}");
            if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine($"\n{t.Description}");
            sb.AppendLine();
            var cols = t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber).ToList();
            if (cols.Count > 0)
            {
                sb.AppendLine("| Column | Type | Format | Hidden | Description |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var c in cols.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"| {MdCell(c.Name)} | {c.DataType} | {MdCell(c.FormatString)} | {(c.IsHidden ? "yes" : "")} | {MdCell(c.Description)} |");
                sb.AppendLine();
            }
            if (t.Measures.Count > 0)
            {
                sb.AppendLine("| Measure | Format | Folder | Description | DAX |");
                sb.AppendLine("|---|---|---|---|---|");
                foreach (var mm in t.Measures.OrderBy(mm => mm.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"| {MdCell(mm.Name)} | {MdCell(mm.FormatString)} | {MdCell(mm.DisplayFolder)} | {MdCell(mm.Description)} | `{MdCell(mm.Expression)}` |");
                sb.AppendLine();
            }
        }
        var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>()
            .Where(r => r.FromTable != null && r.ToTable != null
                        && !IsAutoDateName(r.FromTable.Name) && !IsAutoDateName(r.ToTable.Name)).ToList();
        if (rels.Count > 0)
        {
            sb.AppendLine("## Relationships");
            sb.AppendLine();
            sb.AppendLine("| From | To | Active | Cross filter |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var r in rels)
                sb.AppendLine($"| {r.FromTable.Name}[{r.FromColumn?.Name}] | {r.ToTable.Name}[{r.ToColumn?.Name}] " +
                              $"| {(r.IsActive ? "yes" : "no")} | {r.CrossFilteringBehavior} |");
        }
        return sb.ToString();
    }

    private static string H(string? s) => System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string RenderDictionaryHtml(TOM.Model model, List<TOM.Table> tables)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Data dictionary - {H(model.Name)}</title>");
        sb.AppendLine("<style>body{font-family:Segoe UI,sans-serif;margin:24px}table{border-collapse:collapse;margin:8px 0 20px}" +
                      "th,td{border:1px solid #ccc;padding:4px 8px;text-align:left;font-size:13px}th{background:#f2f2f2}</style>");
        sb.AppendLine($"</head><body><h1>Data dictionary - {H(model.Name)}</h1>");
        foreach (var t in tables)
        {
            sb.AppendLine($"<h2>{H(t.Name)}{(t.IsHidden ? " (hidden)" : "")}</h2>");
            if (!string.IsNullOrWhiteSpace(t.Description)) sb.AppendLine($"<p>{H(t.Description)}</p>");
            var cols = t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber).ToList();
            if (cols.Count > 0)
            {
                sb.AppendLine("<table><tr><th>Column</th><th>Type</th><th>Format</th><th>Hidden</th><th>Description</th></tr>");
                foreach (var c in cols.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"<tr><td>{H(c.Name)}</td><td>{c.DataType}</td><td>{H(c.FormatString)}</td>" +
                                  $"<td>{(c.IsHidden ? "yes" : "")}</td><td>{H(c.Description)}</td></tr>");
                sb.AppendLine("</table>");
            }
            if (t.Measures.Count > 0)
            {
                sb.AppendLine("<table><tr><th>Measure</th><th>Format</th><th>Folder</th><th>Description</th><th>DAX</th></tr>");
                foreach (var mm in t.Measures.OrderBy(mm => mm.Name, StringComparer.OrdinalIgnoreCase))
                    sb.AppendLine($"<tr><td>{H(mm.Name)}</td><td>{H(mm.FormatString)}</td><td>{H(mm.DisplayFolder)}</td>" +
                                  $"<td>{H(mm.Description)}</td><td><code>{H(mm.Expression)}</code></td></tr>");
                sb.AppendLine("</table>");
            }
        }
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }
}
