using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// Enriches a flat discovered schema (one table per uploaded CSV, columns + inferred types) into a REAL model
/// spec - the same shape the hand-authored Solution model.spec.json files use - so an uploaded dataset yields a
/// populated multi-page dashboard instead of a sparse flat one. It adds what <see cref="FlatSpecBuilder"/> does
/// not: many-to-one relationships, synthesised measures on the fact table(s), and a usable date column for the
/// Trend page. The output drops straight into Headless.GenerateProject + ReportLayoutBuilder + Bake unchanged.
///
/// Heuristics (deliberately conservative - prefer correctness over coverage):
///  - RELATIONSHIPS: a key-looking column (ends id/key/_id) in table A that matches a column in table B by name
///    (exact, or A's "&lt;B&gt;Key"/"&lt;B&gt;Id" -&gt; B's "Key"/"Id"/"&lt;B&gt;Key") AND whose values in B are
///    (near-)unique over the sample is a FK -&gt; PK; emit {A.col -&gt; B.col} (B = the "one"/dimension side).
///  - FACT vs DIMENSION: the fact table(s) are the "many" side (they hold FK columns / the most numeric columns
///    / most rows); dimensions are the "one" side referenced by an FK. Measures live on the fact table(s).
///  - MEASURES: per numeric non-key column on a fact table, SUM (additive) or AVERAGE (rate/price/%/ratio/avg by
///    name) named "Total &lt;Col&gt;" / "Avg &lt;Col&gt;", plus a "&lt;Fact&gt; Count" row-count measure. Never sums a
///    key/id column.
///  - DATE: the model's date/datetime column drives the Trend page (ReportLayoutBuilder.DetectDateColumn picks
///    it up); no separate Calendar table is synthesised (low value, higher risk for an arbitrary upload).
///
/// Degrades gracefully: a single-table upload yields measures + by-dimension (its text columns) + trend (its
/// date column) with no relationships; multi-table yields relationships + a fact/dimension split.
/// </summary>
public static class AutoModeller
{
    /// <summary>
    /// Build an enriched model-spec JSON string (with the literal <c>{{DATA_FOLDER}}</c> token, like the Solution
    /// specs) from a discovered schema. <paramref name="tableCsvPaths"/> (logical table name -&gt; staged CSV path)
    /// is used to sample values for relationship uniqueness; when a path is missing that table simply contributes
    /// no relationship target (conservative). Never throws on a data issue - it falls back to a flat-but-measured
    /// model.
    /// </summary>
    public static string Build(SchemaDiscovery schema, IReadOnlyDictionary<string, string>? tableCsvPaths = null)
    {
        var tables = schema.Tables;

        // sample each column's values once (header + up to N rows) for uniqueness / relationship checks
        var samples = SampleColumns(tables, tableCsvPaths);

        var relationships = DetectRelationships(tables, samples);
        var dimTables = relationships.Select(r => r.ToTable).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // fact tables = those that are NOT a relationship target (the "one" side). When there are no
        // relationships (single table or none detected), every table is treated as a fact for measures.
        var factTables = tables.Where(t => !dimTables.Contains(t.Name)).ToList();
        if (factTables.Count == 0) factTables = tables.ToList();

        // ---- assemble the spec (same shape as FlatSpecBuilder + measures + relationships) ----
        var spec = new JsonObject
        {
            ["_comment"] = "Auto-modelled spec from an uploaded schema: flat tables enriched with detected " +
                           "relationships + synthesised measures. {{DATA_FOLDER}} is substituted at bake time.",
            ["expressions"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "DataFolder",
                    ["expression"] = "\"{{DATA_FOLDER}}\" meta [IsParameterQuery=true, Type=\"Text\", IsParameterQueryRequired=true]",
                },
            },
        };

        var factSet = factTables.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tablesArr = new JsonArray();
        foreach (var t in tables)
        {
            string file = t.Name + ".csv";
            var columns = new JsonArray();
            foreach (var c in t.Columns)
                columns.Add(new JsonObject { ["name"] = c.Name, ["dataType"] = c.DataType });

            var to = new JsonObject
            {
                ["name"] = t.Name,
                ["m"] = BuildM(file, t.Columns),
                ["columns"] = columns,
            };

            // measures on the fact table(s)
            if (factSet.Contains(t.Name))
            {
                var measures = SynthesiseMeasures(t);
                if (measures.Count > 0) to["measures"] = measures;
            }
            tablesArr.Add(to);
        }
        spec["tables"] = tablesArr;

        if (relationships.Count > 0)
        {
            var relArr = new JsonArray();
            foreach (var r in relationships)
                relArr.Add(new JsonObject
                {
                    ["fromTable"] = r.FromTable,
                    ["fromColumn"] = r.FromColumn,
                    ["toTable"] = r.ToTable,
                    ["toColumn"] = r.ToColumn,
                });
            spec["relationships"] = relArr;
        }

        return spec.ToJsonString(Cli.Pretty);
    }

    // -------------------------------------------------------------------- relationships

    private sealed record Rel(string FromTable, string FromColumn, string ToTable, string ToColumn);

    /// <summary>Detect many-to-one FK -&gt; PK relationships. For each key-looking column in a candidate fact
    /// table, find a target table whose matching column is (near-)unique over the sample - that target is the
    /// dimension/"one" side. Conservative: requires a name match and target uniqueness; never links a table to
    /// itself; at most one relationship per (fromTable, fromColumn).</summary>
    private static List<Rel> DetectRelationships(IReadOnlyList<TableSchema> tables, Dictionary<string, Dictionary<string, List<string>>> samples)
    {
        var rels = new List<Rel>();
        if (tables.Count < 2) return rels;

        foreach (var from in tables)
        {
            foreach (var col in from.Columns)
            {
                if (!IsKeyName(col.Name)) continue;

                (TableSchema table, ColumnSchema column)? best = null;
                foreach (var to in tables)
                {
                    if (ReferenceEquals(to, from) || string.Equals(to.Name, from.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    var target = MatchTargetColumn(from.Name, col.Name, to);
                    if (target == null) continue;
                    // the target must be (near-)unique in its table for it to be a PK / "one" side
                    if (!IsNearUnique(samples, to.Name, target.Name)) continue;
                    best = (to, target);
                    // a same-named exact target is the strongest signal; stop on it
                    if (string.Equals(target.Name, col.Name, StringComparison.OrdinalIgnoreCase)) break;
                }
                if (best != null && !rels.Any(r => r.FromTable == from.Name && string.Equals(r.FromColumn, col.Name, StringComparison.OrdinalIgnoreCase)))
                    rels.Add(new Rel(from.Name, col.Name, best.Value.table.Name, best.Value.column.Name));
            }
        }
        return rels;
    }

    /// <summary>Find the column in <paramref name="to"/> that the from-table key column references: an exact name
    /// match; or the from column being "&lt;To&gt;Key"/"&lt;To&gt;Id" matching the to-table's own key column; or a
    /// shared "Id"/"Key" column. Returns null when no confident match exists.</summary>
    private static ColumnSchema? MatchTargetColumn(string fromTable, string fromCol, TableSchema to)
    {
        // 1) exact same-named key column in the target (e.g. Results.RegionKey -> Region.RegionKey)
        var exact = to.Columns.FirstOrDefault(c => string.Equals(c.Name, fromCol, StringComparison.OrdinalIgnoreCase) && IsKeyName(c.Name));
        if (exact != null) return exact;

        // 2) the from column names the target table: "<To>Key"/"<To>Id"/"<To> Key" -> the target's key column
        string n = fromCol.Trim();
        string toName = to.Name.Trim();
        bool namesTarget =
            n.StartsWith(toName, StringComparison.OrdinalIgnoreCase) && IsKeyName(n) ||
            n.StartsWith(Singular(toName), StringComparison.OrdinalIgnoreCase) && IsKeyName(n);
        if (namesTarget)
        {
            var key = to.Columns.FirstOrDefault(c => IsKeyName(c.Name));
            if (key != null) return key;
        }
        return null;
    }

    // -------------------------------------------------------------------- measures

    /// <summary>Synthesise measures for a fact table: SUM (or AVERAGE for rate/price/%/ratio/avg-named) of each
    /// numeric non-key column, plus a row-count measure, plus obvious DERIVED ratio measures (e.g. Average Price =
    /// DIVIDE(Total &lt;money&gt;, Total &lt;qty&gt;)). Never sums a key/id column.</summary>
    private static JsonArray SynthesiseMeasures(TableSchema fact)
    {
        var measures = new JsonArray();
        string t = fact.Name;

        // remember the base measure name we minted for each numeric column, so derived ratios can reference it
        // by NAME (not re-derive the aggregation) - keeps the DAX as DIVIDE([Total Sales],[Total Units]).
        var baseMeasureForColumn = new Dictionary<ColumnSchema, string>();

        foreach (var c in fact.Columns)
        {
            if (!IsNumeric(c.DataType)) continue;
            if (IsKeyName(c.Name)) continue;   // never aggregate keys/ids

            string colRef = $"{t}[{c.Name}]";
            if (IsAverageName(c.Name))
            {
                string name = $"Avg {c.Name}";
                measures.Add(Measure(name, $"AVERAGE({colRef})", FormatFor(c.Name, average: true), "Averages"));
                baseMeasureForColumn[c] = name;
            }
            else
            {
                string name = $"Total {c.Name}";
                measures.Add(Measure(name, $"SUM({colRef})", FormatFor(c.Name, average: false), "Totals"));
                baseMeasureForColumn[c] = name;
            }
        }

        // a row-count measure is always useful (and the only measure when a table is all-text/all-key)
        measures.Add(Measure($"{Singular(t)} Count", $"COUNTROWS('{t.Replace("'", "''")}')", "#,0", "Counts"));

        // DERIVED ratios over the SUM-base measures, when the obvious source columns exist on this fact.
        // These are the measures recipes/prompts most often want but that no single-column synthesis produces -
        // most importantly an average/unit-price measure so an "average price" KPI binds instead of breaking.
        var existing = new HashSet<string>(
            measures.OfType<JsonObject>().Select(m => (string?)m["name"] ?? ""), StringComparer.OrdinalIgnoreCase);
        foreach (var d in DerivedMeasures(fact, baseMeasureForColumn))
            if (existing.Add((string?)d["name"] ?? "")) measures.Add(d);

        return measures;
    }

    /// <summary>Build safe, obvious DERIVED ratio measures over the SUM-base measures already minted for a fact:
    ///  - Average Price = DIVIDE([Total &lt;value&gt;], [Total &lt;quantity&gt;]) when the fact has BOTH a monetary
    ///    amount column (sales/revenue/amount/value/$, summed) AND a quantity/units column (units/qty, summed).
    ///  - Margin % = DIVIDE([Total &lt;revenue&gt;] - [Total &lt;cost&gt;], [Total &lt;revenue&gt;]) when BOTH a revenue
    ///    column and a cost column exist (both summed).
    /// DIVIDE() is blank-safe (no divide-by-zero), so these never error. Only emitted when the inputs were
    /// actually synthesised as SUM measures (so the referenced measure names are guaranteed to exist).</summary>
    private static IEnumerable<JsonObject> DerivedMeasures(
        TableSchema fact, IReadOnlyDictionary<ColumnSchema, string> baseMeasureForColumn)
    {
        // only SUM-base measures are valid denominators/numerators for an additive ratio.
        bool IsSummed(ColumnSchema c) => baseMeasureForColumn.ContainsKey(c) && !IsAverageName(c.Name);
        string? Base(ColumnSchema? c) => c != null && baseMeasureForColumn.TryGetValue(c, out var n) ? n : null;

        // pick the best column matching a name predicate among summed numeric columns (first match wins).
        ColumnSchema? Pick(Func<string, bool> match) =>
            fact.Columns.FirstOrDefault(c => IsSummed(c) && match(c.Name.Trim().ToLowerInvariant()));

        // ---- Average Price = DIVIDE(value, quantity) ----
        var qtyCol = Pick(n => n == "units" || n == "qty" || n == "quantity" || n.EndsWith(" units")
                            || n.EndsWith("units") || n.EndsWith(" qty") || n.EndsWith("quantity")
                            || n.Contains("volume"));
        // a monetary amount column, but NOT a price/rate column (those are already per-unit), and not the qty col.
        var valueCol = Pick(n => (n.Contains("sales") || n.Contains("revenue") || n.Contains("amount")
                                  || n.Contains("value") || n.Contains("$") || n.Contains("turnover") || n.Contains("spend"))
                                 && !n.Contains("price") && !n.Contains("per "));
        if (qtyCol != null && valueCol != null && !ReferenceEquals(qtyCol, valueCol))
        {
            string vBase = Base(valueCol)!, qBase = Base(qtyCol)!;
            yield return Measure("Average Price", $"DIVIDE([{vBase}],[{qBase}])", "\\$#,0.00", "Ratios");
        }

        // ---- Margin % = DIVIDE(revenue - cost, revenue) ----
        var revCol = Pick(n => n.Contains("revenue") || n.Contains("sales") || n.Contains("turnover"));
        var costCol = Pick(n => (n.Contains("cost") || n.Contains("cogs")) && !n.Contains("customer"));
        if (revCol != null && costCol != null && !ReferenceEquals(revCol, costCol))
        {
            string rBase = Base(revCol)!, cBase = Base(costCol)!;
            yield return Measure("Margin %", $"DIVIDE([{rBase}]-[{cBase}],[{rBase}])", "0.0%", "Ratios");
        }
    }

    private static JsonObject Measure(string name, string dax, string format, string folder) => new()
    {
        ["name"] = name,
        ["dax"] = dax,
        ["format"] = format,
        ["displayFolder"] = folder,
    };

    /// <summary>A currency-ish format for amount/revenue/sales/cost/price-named columns, a percent for %/rate
    /// columns, else a plain number. Averages of money keep two decimals.</summary>
    private static string FormatFor(string col, bool average)
    {
        string n = col.Trim().ToLowerInvariant();
        bool money = n.Contains("revenue") || n.Contains("sales") || n.Contains("amount") || n.Contains("cost")
                  || n.Contains("price") || n.Contains("value") || n.Contains("spend") || n.Contains("$")
                  || n.Contains("margin") || n.Contains("profit");
        bool pct = n.Contains("%") || n.Contains("percent") || n.Contains("rate") || n.Contains("ratio") || n.Contains("share");
        if (pct) return "0.0%";
        if (money) return average ? "\\$#,0.00" : "\\$#,0";
        return average ? "#,0.0" : "#,0";
    }

    /// <summary>True for columns that should be AVERAGEd not SUMmed (rates/prices/percentages/ratios/averages),
    /// which are not additive across rows.</summary>
    private static bool IsAverageName(string col)
    {
        string n = col.Trim().ToLowerInvariant();
        return n.Contains("rate") || n.Contains("ratio") || n.Contains("%") || n.Contains("percent")
            || n.StartsWith("avg") || n.Contains("average") || n.Contains("price") || n.Contains("score")
            || n.Contains("margin %") || n.EndsWith(" pct");
    }

    // -------------------------------------------------------------------- shared helpers

    /// <summary>Key-looking column name (mirrors the LooksLikeKey idea used elsewhere).</summary>
    private static bool IsKeyName(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return n == "id" || n == "key" || n.EndsWith("id") || n.EndsWith("key") || n.EndsWith("_id") || n.EndsWith(" id");
    }

    private static bool IsNumeric(string? dt) => (dt ?? "").ToLowerInvariant() switch
    {
        "int64" or "int" or "integer" or "whole" or "double" or "real" or "float" or "number"
            or "decimal" or "currency" or "fixed" => true,
        _ => false,
    };

    /// <summary>A naive singular of a table name for measure naming ("Sales" -&gt; "Sale", "Regions" -&gt; "Region").
    /// Best-effort and cosmetic only.</summary>
    private static string Singular(string name)
    {
        string n = name.Trim();
        if (n.Length > 3 && n.EndsWith("ies", StringComparison.OrdinalIgnoreCase)) return n[..^3] + "y";
        if (n.Length > 1 && n.EndsWith("s", StringComparison.OrdinalIgnoreCase) && !n.EndsWith("ss", StringComparison.OrdinalIgnoreCase)) return n[..^1];
        return n;
    }

    /// <summary>Sample each table's columns from its staged CSV (header + up to 2000 data rows) into
    /// table -&gt; column -&gt; values, for uniqueness checks. Tables without a readable CSV get no sample (and so
    /// never serve as a relationship target).</summary>
    private static Dictionary<string, Dictionary<string, List<string>>> SampleColumns(
        IReadOnlyList<TableSchema> tables, IReadOnlyDictionary<string, string>? tableCsvPaths)
    {
        const int MaxRows = 2000;
        var result = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase);
        if (tableCsvPaths == null) return result;

        foreach (var t in tables)
        {
            if (!tableCsvPaths.TryGetValue(t.Name, out var path) || !File.Exists(path)) continue;
            try
            {
                using var reader = new StreamReader(path);
                string? header = reader.ReadLine();
                if (header == null) continue;
                var heads = ParseCsvLine(header);
                var cols = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var h in heads) if (!cols.ContainsKey(h)) cols[h] = new List<string>();

                int rows = 0; string? line;
                while (rows < MaxRows && (line = reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    var vals = ParseCsvLine(line);
                    for (int i = 0; i < heads.Length && i < vals.Length; i++)
                        if (cols.TryGetValue(heads[i], out var list)) list.Add(vals[i]);
                    rows++;
                }
                result[t.Name] = cols;
            }
            catch { /* an unreadable CSV simply contributes no sample */ }
        }
        return result;
    }

    /// <summary>True when a column's sampled non-empty values are (near-)unique - the signal that it is a primary
    /// key / "one" side. Near-unique allows a few dup rows (>= 95% distinct) to tolerate sampling noise; an empty
    /// sample is not considered unique (conservative).</summary>
    private static bool IsNearUnique(Dictionary<string, Dictionary<string, List<string>>> samples, string table, string column)
    {
        if (!samples.TryGetValue(table, out var cols) || !cols.TryGetValue(column, out var values)) return false;
        var nonEmpty = values.Where(v => !string.IsNullOrWhiteSpace(v)).ToList();
        if (nonEmpty.Count == 0) return false;
        int distinct = nonEmpty.Distinct(StringComparer.OrdinalIgnoreCase).Count();
        return distinct >= (int)Math.Ceiling(nonEmpty.Count * 0.95);
    }

    /// <summary>Minimal RFC4180-ish CSV line split (handles quoted fields + escaped quotes; no embedded
    /// newlines, which the sampler reads line-by-line anyway).</summary>
    private static string[] ParseCsvLine(string line)
    {
        var fields = new List<string>();
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
                if (c == '"') inQ = true;
                else if (c == ',') { fields.Add(sb.ToString().Trim()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString().Trim());
        return fields.ToArray();
    }

    // -------------------------------------------------------------------- M (identical shape to FlatSpecBuilder)

    private static string BuildM(string csvFile, IReadOnlyList<ColumnSchema> columns)
    {
        var sb = new StringBuilder();
        sb.Append("let Source = Csv.Document(File.Contents(DataFolder & \"/").Append(csvFile)
          .Append("\"),[Delimiter=\",\",Encoding=65001,QuoteStyle=QuoteStyle.Csv]), ");
        sb.Append("Prom = Table.PromoteHeaders(Source,[PromoteAllScalars=true])");
        if (columns.Count > 0)
        {
            sb.Append(", Typed = Table.TransformColumnTypes(Prom,{");
            for (int i = 0; i < columns.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"").Append(EscapeM(columns[i].Name)).Append("\",").Append(MType(columns[i].DataType)).Append('}');
            }
            sb.Append("}) in Typed");
        }
        else sb.Append(" in Prom");
        return sb.ToString();
    }

    private static string MType(string? dataType) => (dataType ?? "string").ToLowerInvariant() switch
    {
        "int64" or "int" or "integer" or "whole" => "Int64.Type",
        "double" or "real" or "float" or "number" => "type number",
        "decimal" or "currency" or "fixed" => "Currency.Type",
        "date" => "type date",
        "datetime" or "time" => "type datetime",
        "boolean" or "bool" or "logical" => "type logical",
        _ => "type text",
    };

    private static string EscapeM(string name) => name.Replace("\"", "\"\"");
}
