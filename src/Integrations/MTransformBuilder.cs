using System.Globalization;
using System.Text;

namespace SuperBiMcp.Integrations;

/// <summary>
/// Pure, side-effect-free generators for the discrete Power Query (M) transform layer. Each public
/// method takes a table's current M expression plus the transform arguments and returns a NEW M
/// expression with one extra step appended - the equivalent of clicking a transform in the Power
/// Query editor. The mapping (UI gesture -&gt; M function) follows the documented surface:
///   Merge Queries -&gt; Table.NestedJoin (+ Table.ExpandTableColumn); Append -&gt; Table.Combine;
///   Pivot -&gt; Table.Pivot; Group By -&gt; Table.Group; Split -&gt; Table.SplitColumn (+ Splitter.*);
///   Replace Values -&gt; Table.ReplaceValue; Change Type -&gt; Table.TransformColumnTypes;
///   Filter -&gt; Table.SelectRows; Custom/Index column -&gt; Table.AddColumn/Table.AddIndexColumn;
///   Remove/Rename -&gt; Table.RemoveColumns/Table.RenameColumns; Fill Down/Up -&gt; Table.FillDown/FillUp;
///   Remove Duplicates -&gt; Table.Distinct; First Row as Headers -&gt; Table.PromoteHeaders; Transpose -&gt; Table.Transpose.
///
/// The generators are intentionally kept out of <see cref="SuperBiMcp.Services.ModelService"/> so they can be
/// unit-tested against the produced M text with no live model.
/// </summary>
public static class MTransformBuilder
{
    // ------------------------------------------------------------------ core mechanism

    /// <summary>
    /// Append a step to an M expression. Given the current M (a <c>let s0 = ..., sN = ... in sN</c> block,
    /// or any bare expression), build a step <c>#"&lt;stepName&gt;" = &lt;buildExpr(prevStep)&gt;</c>, insert it
    /// before <c>in</c>, and rewire <c>in</c> to the new step. If the source is not a let-block it is wrapped
    /// as <c>let Source = &lt;expr&gt;, #"&lt;stepName&gt;" = ... in #"&lt;stepName&gt;"</c>.
    /// </summary>
    /// <param name="currentM">the table's existing M expression.</param>
    /// <param name="stepName">the new step's name (used as the <c>#"name"</c> identifier).</param>
    /// <param name="buildExpr">given the previous step's identifier, returns the new step's right-hand-side expression.</param>
    public static string AppendStep(string currentM, string stepName, Func<string, string> buildExpr)
    {
        if (string.IsNullOrWhiteSpace(stepName))
            throw new ArgumentException("stepName must be non-empty.", nameof(stepName));

        string m = (currentM ?? "").Trim();
        string newStepRef = StepRef(stepName);

        if (TrySplitLet(m, out string body, out string finalRef))
        {
            // body is "s0 = ..., #\"s1\" = ..." ; finalRef is the identifier after `in`.
            string expr = buildExpr(finalRef.Trim());
            var sb = new StringBuilder();
            sb.Append("let\n    ").Append(body.Trim());
            // ensure a trailing comma before the new step
            if (!body.TrimEnd().EndsWith(",")) sb.Append(',');
            sb.Append("\n    ").Append(newStepRef).Append(" = ").Append(expr);
            sb.Append("\nin\n    ").Append(newStepRef);
            return sb.ToString();
        }

        // not a let-block: wrap the bare expression as Source, then add the step.
        string sourceExpr = buildExpr("Source");
        var w = new StringBuilder();
        w.Append("let\n    Source = ").Append(m).Append(',');
        w.Append("\n    ").Append(newStepRef).Append(" = ").Append(sourceExpr);
        w.Append("\nin\n    ").Append(newStepRef);
        return w.ToString();
    }

    /// <summary>
    /// Split a <c>let ... in ...</c> expression into its step body (everything between let and in) and the
    /// identifier referenced after <c>in</c>. Returns false when the expression is not a let-block.
    /// </summary>
    internal static bool TrySplitLet(string m, out string body, out string finalRef)
    {
        body = ""; finalRef = "";
        string s = (m ?? "").Trim();
        if (!s.StartsWith("let", StringComparison.Ordinal) ||
            (s.Length > 3 && char.IsLetterOrDigit(s[3])))   // "letters" must not be mistaken for "let"
            return false;

        // find the top-level (depth 0) " in " that closes the let. Scan respecting strings, brackets, braces, parens.
        int afterLet = 3;
        int depth = 0; bool inStr = false;
        for (int i = afterLet; i < s.Length; i++)
        {
            char c = s[i];
            if (inStr)
            {
                if (c == '"')
                {
                    if (i + 1 < s.Length && s[i + 1] == '"') { i++; continue; }   // doubled quote inside a string
                    inStr = false;
                }
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                default:
                    if (depth == 0 && IsKeywordAt(s, i, "in"))
                    {
                        body = s.Substring(afterLet, i - afterLet).Trim();
                        finalRef = s.Substring(i + 2).Trim();
                        return body.Length > 0 && finalRef.Length > 0;
                    }
                    break;
            }
        }
        return false;
    }

    private static bool IsKeywordAt(string s, int i, string kw)
    {
        if (i + kw.Length > s.Length) return false;
        if (string.CompareOrdinal(s, i, kw, 0, kw.Length) != 0) return false;
        bool leftOk = i == 0 || !(char.IsLetterOrDigit(s[i - 1]) || s[i - 1] == '_');
        int r = i + kw.Length;
        bool rightOk = r >= s.Length || !(char.IsLetterOrDigit(s[r]) || s[r] == '_');
        return leftOk && rightOk;
    }

    // ------------------------------------------------------------------ M literal helpers

    /// <summary>An M identifier reference using the quoted form: <c>#"Name"</c>.</summary>
    internal static string StepRef(string name) => "#\"" + EscapeM(name) + "\"";

    /// <summary>Escape a string for an M double-quoted literal (double the embedded quotes).</summary>
    internal static string EscapeM(string s) => (s ?? "").Replace("\"", "\"\"");

    /// <summary>An M double-quoted string literal.</summary>
    internal static string Str(string s) => "\"" + EscapeM(s) + "\"";

    /// <summary>An M list of string literals: <c>{"a", "b"}</c>.</summary>
    internal static string StrList(IEnumerable<string> items) =>
        "{" + string.Join(", ", (items ?? Array.Empty<string>()).Select(Str)) + "}";

    /// <summary>An M table reference - a query identifier, quoted when it is not a simple identifier.</summary>
    internal static string TableRef(string name) =>
        IsSimpleIdentifier(name) ? name : StepRef(name);

    private static bool IsSimpleIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (!(char.IsLetter(s[0]) || s[0] == '_')) return false;
        foreach (var c in s) if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        return true;
    }

    /// <summary>Map a friendly type token to the M type literal used by Table.TransformColumnTypes.</summary>
    internal static string MType(string token) => (token ?? "text").Trim().ToLowerInvariant() switch
    {
        "int" or "int64" or "integer" or "whole" => "Int64.Type",
        "number" or "double" or "real" or "float" or "decimal" => "type number",
        "currency" or "fixed" => "Currency.Type",
        "date" => "type date",
        "datetime" => "type datetime",
        "time" => "type time",
        "bool" or "boolean" or "logical" => "type logical",
        "text" or "string" => "type text",
        _ => "type text",
    };

    private static string JoinKindLiteral(string kind) => (kind ?? "Inner").Trim().ToLowerInvariant() switch
    {
        "inner" => "JoinKind.Inner",
        "leftouter" or "left" => "JoinKind.LeftOuter",
        "rightouter" or "right" => "JoinKind.RightOuter",
        "fullouter" or "full" => "JoinKind.FullOuter",
        "leftanti" => "JoinKind.LeftAnti",
        "rightanti" => "JoinKind.RightAnti",
        _ => throw new ArgumentException($"Unknown joinKind '{kind}'. Use Inner, LeftOuter, RightOuter, FullOuter, LeftAnti or RightAnti."),
    };

    private static string GroupOpExpr(string op, string? column, string prevRowVar)
    {
        // prevRowVar is the lambda row variable, e.g. "_". column is referenced as _[Col].
        string col = column != null ? $"{prevRowVar}[{EscapeM(column)}]" : "";
        return (op ?? "").Trim().ToLowerInvariant() switch
        {
            "sum" => $"List.Sum(List.Transform({prevRowVar}, each _[{EscapeM(column!)}]))",
            "count" => $"Table.RowCount({prevRowVar})",
            "countdistinct" => $"List.Count(List.Distinct(List.Transform({prevRowVar}, each _[{EscapeM(column!)}])))",
            "average" => $"List.Average(List.Transform({prevRowVar}, each _[{EscapeM(column!)}]))",
            "min" => $"List.Min(List.Transform({prevRowVar}, each _[{EscapeM(column!)}]))",
            "max" => $"List.Max(List.Transform({prevRowVar}, each _[{EscapeM(column!)}]))",
            "all" => prevRowVar,
            _ => throw new ArgumentException($"Unknown aggregation op '{op}'. Use Sum, Count, Average, Min, Max, CountDistinct or All."),
        };
    }

    private static string GroupOpResultType(string op) => (op ?? "").Trim().ToLowerInvariant() switch
    {
        "count" or "countdistinct" => "Int64.Type",
        "all" => "type table",
        _ => "type number",
    };

    // ------------------------------------------------------------------ transforms

    /// <summary>Merge Queries -&gt; Table.NestedJoin into a new column, optionally expanded with Table.ExpandTableColumn.</summary>
    public static string MergeQueries(string currentM, string rightTable, IReadOnlyList<string> leftKeys,
        IReadOnlyList<string> rightKeys, string joinKind, IReadOnlyList<string>? expandColumns)
    {
        if (leftKeys == null || leftKeys.Count == 0) throw new ArgumentException("leftKeys is required.");
        if (rightKeys == null || rightKeys.Count == 0) throw new ArgumentException("rightKeys is required.");
        if (leftKeys.Count != rightKeys.Count) throw new ArgumentException("leftKeys and rightKeys must have the same length.");

        string newCol = rightTable;   // the nested-table column name
        string jk = JoinKindLiteral(joinKind);
        string merged = AppendStep(currentM, "Merged Queries",
            prev => $"Table.NestedJoin({prev}, {StrList(leftKeys)}, {TableRef(rightTable)}, {StrList(rightKeys)}, {Str(newCol)}, {jk})");

        if (expandColumns == null || expandColumns.Count == 0) return merged;

        // Expanded column names default to the source column names (no prefix), matching the UI's "use original name" option off.
        return AppendStep(merged, "Expanded " + newCol,
            prev => $"Table.ExpandTableColumn({prev}, {Str(newCol)}, {StrList(expandColumns)}, {StrList(expandColumns)})");
    }

    /// <summary>Append Queries -&gt; Table.Combine.</summary>
    public static string AppendQueries(string currentM, IReadOnlyList<string> otherTables)
    {
        if (otherTables == null || otherTables.Count == 0) throw new ArgumentException("otherTables is required.");
        return AppendStep(currentM, "Appended Query",
            prev => $"Table.Combine({{{prev}, {string.Join(", ", otherTables.Select(TableRef))}}})");
    }

    /// <summary>Pivot Column -&gt; Table.Pivot. aggregation defaults to summing the value column.</summary>
    public static string PivotColumn(string currentM, string attributeColumn, string valueColumn, string? aggregation)
    {
        string agg = (aggregation ?? "sum").Trim().ToLowerInvariant();
        string aggFn = agg switch
        {
            "sum" => "List.Sum",
            "count" => "List.Count",
            "average" or "avg" => "List.Average",
            "min" => "List.Min",
            "max" => "List.Max",
            _ => throw new ArgumentException($"Unknown pivot aggregation '{aggregation}'. Use Sum, Count, Average, Min or Max."),
        };
        return AppendStep(currentM, "Pivoted Column",
            prev => $"Table.Pivot({prev}, List.Distinct({prev}[{EscapeM(attributeColumn)}]), {Str(attributeColumn)}, {Str(valueColumn)}, {aggFn})");
    }

    /// <summary>Group By -&gt; Table.Group.</summary>
    public static string GroupBy(string currentM, IReadOnlyList<string> keyColumns,
        IReadOnlyList<(string name, string op, string? column)> aggregations)
    {
        if (keyColumns == null || keyColumns.Count == 0) throw new ArgumentException("keyColumns is required.");
        if (aggregations == null || aggregations.Count == 0) throw new ArgumentException("at least one aggregation is required.");

        var aggParts = aggregations.Select(a =>
            $"{{{Str(a.name)}, each {GroupOpExpr(a.op, a.column, "_")}, {GroupOpResultType(a.op)}}}");
        string aggList = "{" + string.Join(", ", aggParts) + "}";
        return AppendStep(currentM, "Grouped Rows",
            prev => $"Table.Group({prev}, {StrList(keyColumns)}, {aggList})");
    }

    /// <summary>
    /// Split Column -&gt; Table.SplitColumn with the appropriate Splitter.*. Emits <paramref name="parts"/>
    /// output columns named <c>&lt;col&gt;.1 .. &lt;col&gt;.N</c> (the UI default names). parts defaults to 2.
    /// </summary>
    public static string SplitColumn(string currentM, string column, string by, string arg, int parts = 2)
    {
        if (parts < 1) throw new ArgumentException("parts must be one or greater.");
        string splitter = SplitterExpr(by, arg);
        string outNames = "{" + string.Join(", ",
            Enumerable.Range(1, parts).Select(i => Str($"{column}.{i}"))) + "}";
        return AppendStep(currentM, "Split Column",
            prev => $"Table.SplitColumn({prev}, {Str(column)}, {splitter}, {outNames})");
    }

    /// <summary>
    /// Split Column INTO ROWS -&gt; split the column into a list (extractValues form of Table.SplitColumn) then
    /// Table.ExpandListColumn so each part becomes its own row. Only a delimiter split makes sense for rows.
    /// </summary>
    public static string SplitColumnToRows(string currentM, string column, string delimiter)
    {
        if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column is required.");
        string splitter = $"Splitter.SplitTextByDelimiter({Str(delimiter)}, QuoteStyle.Csv)";
        // pass the splitter as extractValues: Table.SplitColumn(t, col, splitter) keeps col as a list column.
        string split = AppendStep(currentM, "Split Column to Rows",
            prev => $"Table.SplitColumn({prev}, {Str(column)}, {splitter})");
        return AppendStep(split, "Expanded " + column,
            prev => $"Table.ExpandListColumn({prev}, {Str(column)})");
    }

    /// <summary>Map a split mode + argument to the matching Splitter.* expression.</summary>
    private static string SplitterExpr(string by, string arg) => (by ?? "").Trim().ToLowerInvariant() switch
    {
        "delimiter" => $"Splitter.SplitTextByDelimiter({Str(arg)}, QuoteStyle.Csv)",
        "positions" => $"Splitter.SplitTextByPositions({{{arg}}})",
        "lengths" => $"Splitter.SplitTextByRepeatedLengths({arg})",
        _ => throw new ArgumentException($"Unknown split mode '{by}'. Use delimiter, positions or lengths."),
    };

    /// <summary>Replace Values -&gt; Table.ReplaceValue.</summary>
    public static string ReplaceValues(string currentM, string column, string find, string replace)
    {
        return AppendStep(currentM, "Replaced Value",
            prev => $"Table.ReplaceValue({prev}, {Str(find)}, {Str(replace)}, Replacer.ReplaceText, {StrList(new[] { column })})");
    }

    /// <summary>
    /// Change Type -&gt; Table.TransformColumnTypes. When <paramref name="culture"/> is supplied (e.g. "en-US",
    /// "en-NZ") it is passed as the locale-aware third argument so text is parsed against that culture's
    /// date/number conventions - the documented fix for the top silent type-conversion corruption source.
    /// </summary>
    public static string ChangeColumnType(string currentM, IReadOnlyList<(string column, string type)> types, string? culture = null)
    {
        if (types == null || types.Count == 0) throw new ArgumentException("at least one column type is required.");
        var parts = types.Select(t => $"{{{Str(t.column)}, {MType(t.type)}}}");
        string typeList = "{" + string.Join(", ", parts) + "}";
        string cultureArg = string.IsNullOrWhiteSpace(culture) ? "" : $", {Str(culture.Trim())}";
        return AppendStep(currentM, "Changed Type",
            prev => $"Table.TransformColumnTypes({prev}, {typeList}{cultureArg})");
    }

    /// <summary>
    /// Detect Data Type -&gt; auto-detect each column's type from its data and apply it. Power Query's editor
    /// gesture computes a type list from a sample at design time; the portable, refresh-safe author form is a
    /// self-contained Table.TransformColumnTypes whose {name, type} pairs are derived at evaluation time from
    /// the first non-null value of every column - which is what this emits.
    /// </summary>
    public static string DetectColumnTypes(string currentM)
    {
        const string detector =
            "Table.TransformColumnTypes(_prev_, " +
            "List.Transform(Table.ColumnNames(_prev_), (c) => " +
            "{c, let v = List.First(List.RemoveNulls(Table.Column(_prev_, c)), null) in " +
            "if v is number then type number else if v is logical then type logical " +
            "else if v is date then type date else if v is datetime then type datetime " +
            "else if v is time then type time else type text}))";
        return AppendStep(currentM, "Detected Column Types",
            prev => detector.Replace("_prev_", prev));
    }

    /// <summary>Filter Rows -&gt; Table.SelectRows with an each-condition. The condition uses <c>_</c> for the row.</summary>
    public static string FilterRows(string currentM, string mCondition)
    {
        if (string.IsNullOrWhiteSpace(mCondition)) throw new ArgumentException("mCondition is required.");
        string cond = mCondition.Trim();
        // accept a bare boolean expression and wrap it as `each ...`; pass through an explicit each/function.
        string predicate = cond.StartsWith("each ", StringComparison.Ordinal) || cond.StartsWith("(", StringComparison.Ordinal)
            ? cond
            : "each " + cond;
        return AppendStep(currentM, "Filtered Rows", prev => $"Table.SelectRows({prev}, {predicate})");
    }

    /// <summary>Add Custom Column -&gt; Table.AddColumn.</summary>
    public static string AddCustomColumn(string currentM, string name, string mExpression)
    {
        if (string.IsNullOrWhiteSpace(mExpression)) throw new ArgumentException("mExpression is required.");
        return AppendStep(currentM, "Added Custom",
            prev => $"Table.AddColumn({prev}, {Str(name)}, each {mExpression.Trim()})");
    }

    /// <summary>Add Index Column -&gt; Table.AddIndexColumn.</summary>
    public static string AddIndexColumn(string currentM, string? name, int start, int step)
    {
        string col = string.IsNullOrWhiteSpace(name) ? "Index" : name!;
        return AppendStep(currentM, "Added Index",
            prev => $"Table.AddIndexColumn({prev}, {Str(col)}, {start.ToString(CultureInfo.InvariantCulture)}, {step.ToString(CultureInfo.InvariantCulture)}, Int64.Type)");
    }

    /// <summary>Remove Columns -&gt; Table.RemoveColumns.</summary>
    public static string RemoveColumns(string currentM, IReadOnlyList<string> columns)
    {
        if (columns == null || columns.Count == 0) throw new ArgumentException("columns is required.");
        return AppendStep(currentM, "Removed Columns",
            prev => $"Table.RemoveColumns({prev}, {StrList(columns)})");
    }

    /// <summary>Rename Columns -&gt; Table.RenameColumns.</summary>
    public static string RenameColumns(string currentM, IReadOnlyList<(string from, string to)> renames)
    {
        if (renames == null || renames.Count == 0) throw new ArgumentException("renames is required.");
        var parts = renames.Select(r => $"{{{Str(r.from)}, {Str(r.to)}}}");
        return AppendStep(currentM, "Renamed Columns",
            prev => $"Table.RenameColumns({prev}, {{{string.Join(", ", parts)}}})");
    }

    /// <summary>Fill Down -&gt; Table.FillDown.</summary>
    public static string FillDown(string currentM, IReadOnlyList<string> columns)
    {
        if (columns == null || columns.Count == 0) throw new ArgumentException("columns is required.");
        return AppendStep(currentM, "Filled Down", prev => $"Table.FillDown({prev}, {StrList(columns)})");
    }

    /// <summary>Fill Up -&gt; Table.FillUp.</summary>
    public static string FillUp(string currentM, IReadOnlyList<string> columns)
    {
        if (columns == null || columns.Count == 0) throw new ArgumentException("columns is required.");
        return AppendStep(currentM, "Filled Up", prev => $"Table.FillUp({prev}, {StrList(columns)})");
    }

    /// <summary>Remove Duplicates -&gt; Table.Distinct (over the whole row, or only the given key columns).</summary>
    public static string RemoveDuplicates(string currentM, IReadOnlyList<string>? columns)
    {
        if (columns == null || columns.Count == 0)
            return AppendStep(currentM, "Removed Duplicates", prev => $"Table.Distinct({prev})");
        return AppendStep(currentM, "Removed Duplicates", prev => $"Table.Distinct({prev}, {StrList(columns)})");
    }

    /// <summary>First Row as Headers -&gt; Table.PromoteHeaders.</summary>
    public static string PromoteHeaders(string currentM) =>
        AppendStep(currentM, "Promoted Headers", prev => $"Table.PromoteHeaders({prev}, [PromoteAllScalars=true])");

    /// <summary>Transpose -&gt; Table.Transpose.</summary>
    public static string Transpose(string currentM) =>
        AppendStep(currentM, "Transposed Table", prev => $"Table.Transpose({prev})");

    // ------------------------------------------------------------------ Wave J: remaining transforms

    /// <summary>Unpivot Columns -&gt; Table.Unpivot (the listed columns become attribute/value pairs).</summary>
    public static string UnpivotColumns(string currentM, IReadOnlyList<string> columns)
    {
        if (columns == null || columns.Count == 0) throw new ArgumentException("columns is required.");
        return AppendStep(currentM, "Unpivoted Columns",
            prev => $"Table.Unpivot({prev}, {StrList(columns)}, \"Attribute\", \"Value\")");
    }

    /// <summary>Unpivot Other Columns -&gt; Table.UnpivotOtherColumns (keep the listed columns, unpivot the rest).</summary>
    public static string UnpivotOtherColumns(string currentM, IReadOnlyList<string> keepColumns)
    {
        if (keepColumns == null || keepColumns.Count == 0) throw new ArgumentException("keepColumns is required.");
        return AppendStep(currentM, "Unpivoted Other Columns",
            prev => $"Table.UnpivotOtherColumns({prev}, {StrList(keepColumns)}, \"Attribute\", \"Value\")");
    }

    /// <summary>Merge Columns -&gt; Table.CombineColumns (+ Combiner.CombineTextByDelimiter) into one new column.</summary>
    public static string MergeColumns(string currentM, IReadOnlyList<string> columns, string separator, string newColumnName)
    {
        if (columns == null || columns.Count < 2) throw new ArgumentException("at least two columns are required to merge.");
        if (string.IsNullOrWhiteSpace(newColumnName)) throw new ArgumentException("newColumnName is required.");
        return AppendStep(currentM, "Merged Columns",
            prev => $"Table.CombineColumns({prev}, {StrList(columns)}, Combiner.CombineTextByDelimiter({Str(separator ?? "")}, QuoteStyle.None), {Str(newColumnName)})");
    }

    /// <summary>
    /// Expand Column -&gt; Table.ExpandRecordColumn / Table.ExpandTableColumn / Table.ExpandListColumn.
    /// kind = record | table | list (default record). For record/table, fields[] selects which inner
    /// fields to surface (the expanded names default to the field names); for list, fields are ignored.
    /// </summary>
    public static string ExpandColumn(string currentM, string column, IReadOnlyList<string>? fields, string? kind)
    {
        if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column is required.");
        string k = (kind ?? "record").Trim().ToLowerInvariant();
        switch (k)
        {
            case "list":
                return AppendStep(currentM, "Expanded " + column,
                    prev => $"Table.ExpandListColumn({prev}, {Str(column)})");
            case "record":
            case "table":
                if (fields == null || fields.Count == 0)
                    throw new ArgumentException("fields is required when expanding a record or table column.");
                string fn = k == "table" ? "Table.ExpandTableColumn" : "Table.ExpandRecordColumn";
                return AppendStep(currentM, "Expanded " + column,
                    prev => $"{fn}({prev}, {Str(column)}, {StrList(fields)}, {StrList(fields)})");
            default:
                throw new ArgumentException($"Unknown expand kind '{kind}'. Use record, table or list.");
        }
    }

    /// <summary>Keep Top Rows -&gt; Table.FirstN.</summary>
    public static string KeepTopRows(string currentM, int count)
    {
        if (count < 0) throw new ArgumentException("count must be zero or greater.");
        return AppendStep(currentM, "Kept First Rows",
            prev => $"Table.FirstN({prev}, {count.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>Keep Bottom Rows -&gt; Table.LastN.</summary>
    public static string KeepBottomRows(string currentM, int count)
    {
        if (count < 0) throw new ArgumentException("count must be zero or greater.");
        return AppendStep(currentM, "Kept Last Rows",
            prev => $"Table.LastN({prev}, {count.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>Remove Top Rows / Skip -&gt; Table.Skip.</summary>
    public static string SkipRows(string currentM, int count)
    {
        if (count < 0) throw new ArgumentException("count must be zero or greater.");
        return AppendStep(currentM, "Skipped Rows",
            prev => $"Table.Skip({prev}, {count.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>Keep Range of Rows -&gt; Table.Range (skip <c>offset</c> rows then keep <c>count</c>).</summary>
    public static string KeepRangeRows(string currentM, int offset, int count)
    {
        if (offset < 0) throw new ArgumentException("offset must be zero or greater.");
        if (count < 0) throw new ArgumentException("count must be zero or greater.");
        return AppendStep(currentM, "Kept Range of Rows",
            prev => $"Table.Range({prev}, {offset.ToString(CultureInfo.InvariantCulture)}, {count.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>Sort -&gt; Table.Sort with one or more {column, direction} keys (direction Ascending | Descending).</summary>
    public static string SortRows(string currentM, IReadOnlyList<(string column, string direction)> sorts)
    {
        if (sorts == null || sorts.Count == 0) throw new ArgumentException("at least one sort key is required.");
        var parts = sorts.Select(s => $"{{{Str(s.column)}, {OrderLiteral(s.direction)}}}");
        return AppendStep(currentM, "Sorted Rows",
            prev => $"Table.Sort({prev}, {{{string.Join(", ", parts)}}})");
    }

    private static string OrderLiteral(string direction) => (direction ?? "Ascending").Trim().ToLowerInvariant() switch
    {
        "asc" or "ascending" => "Order.Ascending",
        "desc" or "descending" => "Order.Descending",
        _ => throw new ArgumentException($"Unknown sort direction '{direction}'. Use Ascending or Descending."),
    };

    /// <summary>Choose Columns -&gt; Table.SelectColumns (keep only the listed columns, in that order).</summary>
    public static string SelectColumns(string currentM, IReadOnlyList<string> columns)
    {
        if (columns == null || columns.Count == 0) throw new ArgumentException("columns is required.");
        return AppendStep(currentM, "Removed Other Columns",
            prev => $"Table.SelectColumns({prev}, {StrList(columns)})");
    }

    /// <summary>Reorder Columns -&gt; Table.ReorderColumns (the given order; remaining columns keep their place).</summary>
    public static string ReorderColumns(string currentM, IReadOnlyList<string> order)
    {
        if (order == null || order.Count == 0) throw new ArgumentException("order is required.");
        return AppendStep(currentM, "Reordered Columns",
            prev => $"Table.ReorderColumns({prev}, {StrList(order)})");
    }

    /// <summary>Duplicate Column -&gt; Table.DuplicateColumn.</summary>
    public static string DuplicateColumn(string currentM, string column, string newName)
    {
        if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column is required.");
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName is required.");
        return AppendStep(currentM, "Duplicated Column",
            prev => $"Table.DuplicateColumn({prev}, {Str(column)}, {Str(newName)})");
    }

    /// <summary>
    /// Transform Column -&gt; Table.TransformColumns with the mapped scalar function. The operation set is
    /// documented in <see cref="TransformColumnFunction"/>: text (upper/lower/trim/clean/length/...),
    /// number (round/abs/floor/ceiling/...), and date (year/month/day/startofmonth/endofmonth/...).
    /// </summary>
    public static string TransformColumn(string currentM, string column, string operation)
    {
        if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column is required.");
        var (fn, resultType) = TransformColumnFunction(operation);
        return AppendStep(currentM, "Transformed Column",
            prev => $"Table.TransformColumns({prev}, {{{{{Str(column)}, {fn}, {resultType}}}}})");
    }

    /// <summary>
    /// Map a friendly operation token to the M lambda (a <c>each &lt;fn&gt;(_)</c> form) and the result
    /// type passed to Table.TransformColumns. Returns (lambda, resultType). FLAG: this is the supported
    /// operation set - extend here, not at the call sites.
    /// </summary>
    internal static (string fn, string resultType) TransformColumnFunction(string operation) =>
        (operation ?? "").Trim().ToLowerInvariant() switch
        {
            // text
            "upper" or "uppercase" => ("each Text.Upper(_)", "type text"),
            "lower" or "lowercase" => ("each Text.Lower(_)", "type text"),
            "trim" => ("each Text.Trim(_)", "type text"),
            "clean" => ("each Text.Clean(_)", "type text"),
            "proper" or "capitalise" or "capitalize" => ("each Text.Proper(_)", "type text"),
            "length" or "len" => ("each Text.Length(_)", "Int64.Type"),
            // number
            "round" => ("each Number.Round(_, 0)", "type number"),
            "abs" or "absolute" => ("each Number.Abs(_)", "type number"),
            "floor" => ("each Number.RoundDown(_)", "type number"),
            "ceiling" or "ceil" => ("each Number.RoundUp(_)", "type number"),
            "sign" => ("each Number.Sign(_)", "Int64.Type"),
            "sqrt" => ("each Number.Sqrt(_)", "type number"),
            // date
            "year" => ("each Date.Year(_)", "Int64.Type"),
            "month" => ("each Date.Month(_)", "Int64.Type"),
            "day" => ("each Date.Day(_)", "Int64.Type"),
            "quarter" => ("each Date.QuarterOfYear(_)", "Int64.Type"),
            "weekofyear" => ("each Date.WeekOfYear(_)", "Int64.Type"),
            "dayofweek" => ("each Date.DayOfWeek(_)", "Int64.Type"),
            "startofmonth" => ("each Date.StartOfMonth(_)", "type date"),
            "endofmonth" => ("each Date.EndOfMonth(_)", "type date"),
            "startofyear" => ("each Date.StartOfYear(_)", "type date"),
            "endofyear" => ("each Date.EndOfYear(_)", "type date"),
            "monthname" => ("each Date.MonthName(_)", "type text"),
            "dayname" => ("each Date.DayOfWeekName(_)", "type text"),
            _ => throw new ArgumentException(
                $"Unknown transform operation '{operation}'. Text: upper, lower, trim, clean, proper, length. " +
                "Number: round, abs, floor, ceiling, sign, sqrt. " +
                "Date: year, month, day, quarter, weekofyear, dayofweek, startofmonth, endofmonth, startofyear, endofyear, monthname, dayname."),
        };

    // ------------------------------------------------------------------ Wave J: folding-control hints

    /// <summary>Table.Buffer - load the table into memory, stopping query folding downstream of this step.</summary>
    public static string BufferTable(string currentM) =>
        AppendStep(currentM, "Buffered Table", prev => $"Table.Buffer({prev})");

    /// <summary>Table.StopFolding - prevent the M engine folding further operations into the source query.</summary>
    public static string StopFolding(string currentM) =>
        AppendStep(currentM, "Stopped Folding", prev => $"Table.StopFolding({prev})");

    // ------------------------------------------------------------------ Wave J: connector source steps

    /// <summary>
    /// Build the M expression for a connector's SOURCE step. Used to seed (or replace) the first step of a
    /// query. connector is case-insensitive; the mapping (per the documented data-access surface):
    ///   sql -&gt; Sql.Database(server, database [, [Query=...]]); web -&gt; Web.Contents(url [, [RelativePath=..., Headers=...]]);
    ///   odata -&gt; OData.Feed(url); excel -&gt; Excel.Workbook(File.Contents(path), true); csv -&gt; Csv.Document(File.Contents(path), [Delimiter, QuoteStyle]);
    ///   folder -&gt; Folder.Files(path); sharepoint -&gt; SharePoint.Files(url, [ApiVersion=15]); azureblob -&gt; AzureStorage.Blobs(account).
    /// <paramref name="parameters"/> is the connector argument bag (keys documented per connector).
    /// </summary>
    public static string SourceStep(string connector, IReadOnlyDictionary<string, string> parameters)
    {
        var p = parameters ?? new Dictionary<string, string>();
        string Req(string key) => p.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v
            : throw new ArgumentException($"connector '{connector}' requires parameter '{key}'.");
        bool Opt(string key, out string v) => p.TryGetValue(key, out v!) && !string.IsNullOrWhiteSpace(v);

        switch ((connector ?? "").Trim().ToLowerInvariant())
        {
            case "sql":
            case "sql.database":
            {
                string server = Req("server");
                string db = Req("database");
                if (Opt("query", out var q))
                    return $"Sql.Database({Str(server)}, {Str(db)}, [Query={Str(q)}])";
                return $"Sql.Database({Str(server)}, {Str(db)})";
            }
            // generic relational connectors that share the X.Database(server, database) shape.
            case "oracle":
            case "oracle.database":
                return RelationalDatabase("Oracle.Database", Req("server"), p);
            case "mysql":
            case "mysql.database":
                return RelationalDatabase("MySQL.Database", Req("server"), p);
            case "postgresql":
            case "postgres":
            case "postgresql.database":
                return RelationalDatabase("PostgreSQL.Database", Req("server"), p);
            case "db2":
            case "db2.database":
                return RelationalDatabase("Db2.Database", Req("server"), p);
            case "web":
            case "web.contents":
            {
                string url = Req("url");
                var opts = new List<string>();
                if (Opt("relativePath", out var rp)) opts.Add($"RelativePath={Str(rp)}");
                if (Opt("query", out var query)) opts.Add($"Query={query}");   // raw M record, e.g. [#"$top"="10"]
                if (Opt("headers", out var h)) opts.Add($"Headers={h}");        // raw M record, e.g. [#"Authorization"="Bearer x"]
                if (Opt("content", out var content)) opts.Add($"Content={content}");   // M binary for a POST body, e.g. Text.ToBinary("{...}")
                if (Opt("apiKeyName", out var ak)) opts.Add($"ApiKeyName={Str(ak)}");
                if (Opt("manualStatusHandling", out var msh)) opts.Add($"ManualStatusHandling={{{msh}}}");   // e.g. {404, 500}
                if (Opt("isRetry", out var retry)) opts.Add($"IsRetry={NormBool(retry)}");
                if (Opt("timeout", out var to)) opts.Add($"Timeout={to}");   // raw M duration, e.g. #duration(0,0,1,40)
                return opts.Count > 0
                    ? $"Web.Contents({Str(url)}, [{string.Join(", ", opts)}])"
                    : $"Web.Contents({Str(url)})";
            }
            case "odata":
            case "odata.feed":
            {
                string url = Req("url");
                // fold $filter/$select/$top through the Query record (the refresh-safe OData pattern).
                var q = new List<string>();
                if (Opt("filter", out var f)) q.Add($"#\"$filter\"={Str(f)}");
                if (Opt("select", out var sel)) q.Add($"#\"$select\"={Str(sel)}");
                if (Opt("top", out var top)) q.Add($"#\"$top\"={Str(top)}");
                if (Opt("orderby", out var ob)) q.Add($"#\"$orderby\"={Str(ob)}");
                return q.Count > 0
                    ? $"OData.Feed({Str(url)}, null, [Query=[{string.Join(", ", q)}]])"
                    : $"OData.Feed({Str(url)})";
            }
            case "excel":
            case "excel.workbook":
                return $"Excel.Workbook(File.Contents({Str(Req("path"))}), null, true)";
            case "csv":
            case "csv.document":
            {
                string path = Req("path");
                var opts = new List<string>();
                opts.Add(Opt("delimiter", out var d) ? $"Delimiter={Str(d)}" : "Delimiter=\",\"");
                if (Opt("columns", out var cols)) opts.Add($"Columns={cols}");
                opts.Add("Encoding=65001");
                opts.Add("QuoteStyle=QuoteStyle.Csv");
                return $"Csv.Document(File.Contents({Str(path)}), [{string.Join(", ", opts)}])";
            }
            case "json":
            case "json.document":
            {
                // file path -> Json.Document(File.Contents(path)); url -> Json.Document(Web.Contents(url)).
                if (Opt("url", out var jurl))
                    return $"Json.Document(Web.Contents({Str(jurl)}))";
                return $"Json.Document(File.Contents({Str(Req("path"))}))";
            }
            case "xml":
            case "xml.tables":
            {
                if (Opt("url", out var xurl))
                    return $"Xml.Tables(Web.Contents({Str(xurl)}))";
                return $"Xml.Tables(File.Contents({Str(Req("path"))}))";
            }
            case "pdf":
            case "pdf.tables":
                return $"Pdf.Tables(File.Contents({Str(Req("path"))}))";
            case "html":
            case "html.table":
            {
                // html source: a url (Web.Contents) or a file path (File.Contents).
                string src = Opt("url", out var hurl)
                    ? $"Web.Contents({Str(hurl)})"
                    : $"File.Contents({Str(Req("path"))})";
                return $"Html.Table({src}, {{}})";
            }
            case "analysisservices":
            case "analysisservices.database":
            {
                string server = Req("server");
                string db = Req("database");
                // an MDX or DAX query folds through the [Query=...] option.
                if (Opt("query", out var q))
                    return $"AnalysisServices.Database({Str(server)}, {Str(db)}, [Query={Str(q)}])";
                return $"AnalysisServices.Database({Str(server)}, {Str(db)})";
            }
            case "odbc":
            case "odbc.datasource":
            {
                // a DSN/connection string -> Odbc.DataSource(connStr); a SQL query -> Odbc.Query(connStr, sql).
                string conn = Opt("connectionString", out var cs) ? cs : Req("dsn");
                if (Opt("query", out var q))
                    return $"Odbc.Query({Str(conn)}, {Str(q)})";
                return $"Odbc.DataSource({Str(conn)})";
            }
            case "oledb":
            case "oledb.datasource":
            {
                string conn = Req("connectionString");
                if (Opt("query", out var q))
                    return $"OleDb.Query({Str(conn)}, {Str(q)})";
                return $"OleDb.DataSource({Str(conn)})";
            }
            case "folder":
            case "folder.files":
                return $"Folder.Files({Str(Req("path"))})";
            case "sharepoint":
            case "sharepoint.files":
                return $"SharePoint.Files({Str(Req("url"))}, [ApiVersion=15])";
            case "azureblob":
            case "azurestorage.blobs":
                return $"AzureStorage.Blobs({Str(Req("account"))})";
            case "azuretable":
            case "azurestorage.tables":
                return $"AzureStorage.Tables({Str(Req("account"))})";
            case "datalake":
            case "azurestorage.datalake":
                return $"AzureStorage.DataLake({Str(Req("url"))})";
            case "deltalake":
            case "deltalake.table":
                // DeltaLake.Table over a Data Lake folder contents.
                return $"DeltaLake.Table(AzureStorage.DataLake({Str(Req("url"))}))";
            case "cdm":
            case "dataverse":
            case "cdm.contents":
            {
                if (Opt("url", out var curl))
                    return $"Cdm.Contents({Str(curl)})";
                return $"Cdm.Contents(AzureStorage.DataLake({Str(Req("dataLakeUrl"))}))";
            }
            default:
                throw new ArgumentException(
                    $"Unknown connector '{connector}'. Use sql, oracle, mysql, postgresql, db2, web, odata, " +
                    "excel, csv, json, xml, pdf, html, analysisservices, odbc, oledb, folder, sharepoint, " +
                    "azureblob, azuretable, datalake, deltalake or cdm.");
        }
    }

    /// <summary>X.Database(server[, database][, [Query=...]]) for the generic relational connectors.</summary>
    private static string RelationalDatabase(string fn, string server, IReadOnlyDictionary<string, string> p)
    {
        bool Opt(string key, out string v) => p.TryGetValue(key, out v!) && !string.IsNullOrWhiteSpace(v);
        bool hasDb = Opt("database", out var db);
        bool hasQuery = Opt("query", out var q);
        if (!hasDb)
            return $"{fn}({Str(server)})";
        if (hasQuery)
            return $"{fn}({Str(server)}, {Str(db)}, [Query={Str(q)}])";
        return $"{fn}({Str(server)}, {Str(db)})";
    }

    private static string NormBool(string v) => (v ?? "").Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => "true",
        _ => "false",
    };

    /// <summary>
    /// Seed or replace a query's SOURCE step with <paramref name="sourceExpr"/>. When the current M is a
    /// let-block, the first step's right-hand side is replaced in place (step names and downstream steps are
    /// untouched). When it is a bare expression (or empty), a fresh <c>let Source = &lt;expr&gt; in Source</c>
    /// is produced. This is the connector-step counterpart to <see cref="AppendStep"/>.
    /// </summary>
    public static string ReplaceSourceStep(string currentM, string sourceExpr)
    {
        if (string.IsNullOrWhiteSpace(sourceExpr)) throw new ArgumentException("sourceExpr is required.");
        string m = (currentM ?? "").Trim();

        if (TrySplitLet(m, out string body, out string finalRef))
        {
            // body is "name = rhs[, #\"next\" = ...]" - replace only the FIRST step's right-hand side.
            int eq = IndexOfTopLevelEquals(body);
            if (eq >= 0)
            {
                int comma = IndexOfTopLevelComma(body, eq + 1);
                string firstName = body.Substring(0, eq).TrimEnd();
                string rest = comma >= 0 ? body.Substring(comma + 1).Trim() : "";   // downstream steps, comma stripped

                var sb = new StringBuilder();
                sb.Append("let\n    ").Append(firstName).Append(" = ").Append(sourceExpr.Trim());
                if (rest.Length > 0) sb.Append(",\n    ").Append(rest);
                sb.Append("\nin\n    ").Append(finalRef.Trim());
                return sb.ToString();
            }
        }

        // bare expression or empty: a fresh single-step query.
        var w = new StringBuilder();
        w.Append("let\n    Source = ").Append(sourceExpr.Trim());
        w.Append("\nin\n    Source");
        return w.ToString();
    }

    /// <summary>Index of the first top-level <c>=</c> in a let body (skips strings/brackets and <c>=&gt;</c>/<c>&gt;=</c>/<c>&lt;=</c>/<c>&lt;&gt;</c>).</summary>
    private static int IndexOfTopLevelEquals(string body)
    {
        int depth = 0; bool inStr = false;
        for (int i = 0; i < body.Length; i++)
        {
            char c = body[i];
            if (inStr) { if (c == '"') { if (i + 1 < body.Length && body[i + 1] == '"') { i++; continue; } inStr = false; } continue; }
            switch (c)
            {
                case '"': inStr = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case '=':
                    if (depth == 0)
                    {
                        char prev = i > 0 ? body[i - 1] : ' ';
                        char next = i + 1 < body.Length ? body[i + 1] : ' ';
                        if (prev != '>' && prev != '<' && prev != '=' && next != '=' && next != '>')
                            return i;
                    }
                    break;
            }
        }
        return -1;
    }

    /// <summary>Index of the first top-level <c>,</c> at or after <paramref name="from"/> (skips strings/brackets).</summary>
    private static int IndexOfTopLevelComma(string body, int from)
    {
        int depth = 0; bool inStr = false;
        for (int i = from; i < body.Length; i++)
        {
            char c = body[i];
            if (inStr) { if (c == '"') { if (i + 1 < body.Length && body[i + 1] == '"') { i++; continue; } inStr = false; } continue; }
            switch (c)
            {
                case '"': inStr = true; break;
                case '(' or '[' or '{': depth++; break;
                case ')' or ']' or '}': depth--; break;
                case ',': if (depth == 0) return i; break;
            }
        }
        return -1;
    }

    // ------------------------------------------------------------------ Wave J: M parameter expression

    /// <summary>
    /// Build the M expression text for a Power Query PARAMETER (the body of a NamedExpression). It mirrors
    /// what Power Query writes for a "Manage Parameters" entry: a default value literal carrying the
    /// <c>meta [IsParameterQuery=true, ...]</c> record. type = Text | Number | Logical | DateTime.
    /// When allowedValues are supplied they become the <c>List</c> the parameter is restricted to.
    /// </summary>
    public static string ParameterExpression(string type, string? defaultValue, IReadOnlyList<string>? allowedValues)
    {
        string t = NormaliseParamType(type);
        string defLiteral = ParamLiteral(t, defaultValue);

        var meta = new List<string> { "IsParameterQuery=true" };
        if (allowedValues != null && allowedValues.Count > 0)
        {
            string list = "{" + string.Join(", ", allowedValues.Select(v => ParamLiteral(t, v))) + "}";
            meta.Add($"List={list}");
        }
        else
        {
            meta.Add("List=null");
        }
        meta.Add("DefaultValue=null");
        meta.Add($"Type=\"{t}\"");
        meta.Add("IsParameterQueryRequired=true");

        return $"{defLiteral} meta [{string.Join(", ", meta)}]";
    }

    /// <summary>The PQ parameter type token, validated.</summary>
    internal static string NormaliseParamType(string type) => (type ?? "Text").Trim().ToLowerInvariant() switch
    {
        "text" or "string" => "Text",
        "number" or "double" or "decimal" => "Number",
        "logical" or "bool" or "boolean" => "Logical",
        "datetime" or "date" => "DateTime",
        _ => throw new ArgumentException($"Unknown parameter type '{type}'. Use Text, Number, Logical or DateTime."),
    };

    /// <summary>Render a value as the M literal for the given parameter type.</summary>
    private static string ParamLiteral(string normalisedType, string? value)
    {
        string v = (value ?? "").Trim();
        switch (normalisedType)
        {
            case "Number":
                if (v.Length == 0) return "0";
                if (!double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    throw new ArgumentException($"Parameter value '{value}' is not a valid Number.");
                return d.ToString(CultureInfo.InvariantCulture);
            case "Logical":
                return v.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" => "true",
                    "false" or "0" or "no" or "" => "false",
                    _ => throw new ArgumentException($"Parameter value '{value}' is not a valid Logical (use true/false)."),
                };
            case "DateTime":
                // emit a #datetime(...) literal where possible; otherwise pass the value through quoted.
                if (DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return $"#datetime({dt.Year}, {dt.Month}, {dt.Day}, {dt.Hour}, {dt.Minute}, {dt.Second})";
                return Str(v);
            default:   // Text
                return Str(v);
        }
    }

    // ============================================================ Wave L: error rows, row trims, value/error replace

    /// <summary>Remove Errors -&gt; Table.RemoveRowsWithErrors (whole row, or only the given columns).</summary>
    public static string RemoveErrors(string currentM, IReadOnlyList<string>? columns)
    {
        if (columns == null || columns.Count == 0)
            return AppendStep(currentM, "Removed Errors", prev => $"Table.RemoveRowsWithErrors({prev})");
        return AppendStep(currentM, "Removed Errors",
            prev => $"Table.RemoveRowsWithErrors({prev}, {StrList(columns)})");
    }

    /// <summary>Keep Errors -&gt; Table.SelectRowsWithErrors (whole row, or only the given columns).</summary>
    public static string KeepErrors(string currentM, IReadOnlyList<string>? columns)
    {
        if (columns == null || columns.Count == 0)
            return AppendStep(currentM, "Kept Errors", prev => $"Table.SelectRowsWithErrors({prev})");
        return AppendStep(currentM, "Kept Errors",
            prev => $"Table.SelectRowsWithErrors({prev}, {StrList(columns)})");
    }

    /// <summary>
    /// Replace Errors -&gt; Table.ReplaceErrorValues. Each replacement is a {column, value} pair where the value
    /// is an M literal of the given type (text|number|logical|null). Table.ReplaceErrorValues is per-column,
    /// so an all-columns replacement is expressed by listing each column with the same value.
    /// </summary>
    public static string ReplaceErrors(string currentM,
        IReadOnlyList<(string column, string? value, string valueType)> replacements)
    {
        if (replacements == null || replacements.Count == 0)
            throw new ArgumentException("at least one {column, value} replacement is required.");
        var parts = replacements.Select(r =>
            $"{{{Str(r.column)}, {TypedLiteral(r.valueType, r.value)}}}");
        string list = "{" + string.Join(", ", parts) + "}";
        return AppendStep(currentM, "Replaced Errors",
            prev => $"Table.ReplaceErrorValues({prev}, {list})");
    }

    /// <summary>
    /// Remove Blank Rows -&gt; Table.SelectRows keeping only rows where at least one field is non-blank. Mirrors
    /// the M Power Query writes for "Remove Blank Rows": Table.SelectRows(t, each not List.IsEmpty(
    /// List.RemoveMatchingItems(Record.FieldValues(_), {"", null}))).
    /// </summary>
    public static string RemoveBlankRows(string currentM) =>
        AppendStep(currentM, "Removed Blank Rows",
            prev => $"Table.SelectRows({prev}, each not List.IsEmpty(List.RemoveMatchingItems(Record.FieldValues(_), {{\"\", null}})))");

    /// <summary>Remove Bottom Rows -&gt; Table.RemoveLastN.</summary>
    public static string RemoveBottomRows(string currentM, int count)
    {
        if (count < 0) throw new ArgumentException("count must be zero or greater.");
        return AppendStep(currentM, "Removed Bottom Rows",
            prev => $"Table.RemoveLastN({prev}, {count.ToString(CultureInfo.InvariantCulture)})");
    }

    /// <summary>
    /// Remove Alternate Rows -&gt; Table.AlternateRows(table, firstKept, taken, skipped) - keep the first
    /// <paramref name="firstKept"/> rows, then repeatedly take <paramref name="taken"/> and skip
    /// <paramref name="skipped"/>. (Note: M's Table.AlternateRows actually SKIPS the first/taken block and
    /// KEEPS the skipped block, matching the editor's "Remove Alternate Rows" gesture.)
    /// </summary>
    public static string RemoveAlternateRows(string currentM, int firstKept, int taken, int skipped)
    {
        if (firstKept < 0 || taken < 0 || skipped < 0)
            throw new ArgumentException("firstKept, taken and skipped must be zero or greater.");
        string Inv(int n) => n.ToString(CultureInfo.InvariantCulture);
        return AppendStep(currentM, "Removed Alternate Rows",
            prev => $"Table.AlternateRows({prev}, {Inv(firstKept)}, {Inv(taken)}, {Inv(skipped)})");
    }

    /// <summary>
    /// Replace Value (whole-cell) -&gt; Table.ReplaceValue with Replacer.ReplaceValue. Unlike
    /// <see cref="ReplaceValues"/> (text substring, Replacer.ReplaceText), this swaps the WHOLE cell value and
    /// supports non-text and null both ways (e.g. null-&gt;0, 0-&gt;null, -1-&gt;0). valueType applies to BOTH the
    /// find and the replace literal: text | number | logical | null.
    /// </summary>
    public static string ReplaceValue(string currentM, string column, string? oldValue, string? newValue, string valueType)
    {
        if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column is required.");
        string find = TypedLiteral(valueType, oldValue);
        string repl = TypedLiteral(valueType, newValue);
        return AppendStep(currentM, "Replaced Value",
            prev => $"Table.ReplaceValue({prev}, {find}, {repl}, Replacer.ReplaceValue, {StrList(new[] { column })})");
    }

    /// <summary>Render an M literal for a value of the given friendly type: text | number | logical | null.</summary>
    internal static string TypedLiteral(string valueType, string? value)
    {
        string t = (valueType ?? "text").Trim().ToLowerInvariant();
        if (t == "null") return "null";
        string v = (value ?? "").Trim();
        switch (t)
        {
            case "null-or-empty" when v.Length == 0:
                return "null";
            case "number":
            case "double":
            case "decimal":
            case "int":
            case "int64":
            case "integer":
                if (v.Length == 0) return "null";
                if (!double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out var d))
                    throw new ArgumentException($"Value '{value}' is not a valid number.");
                return d.ToString(CultureInfo.InvariantCulture);
            case "logical":
            case "bool":
            case "boolean":
                return v.ToLowerInvariant() switch
                {
                    "true" or "1" or "yes" => "true",
                    "false" or "0" or "no" => "false",
                    "" => "null",
                    _ => throw new ArgumentException($"Value '{value}' is not a valid logical (use true/false)."),
                };
            case "text":
            case "string":
                return value == null ? "null" : Str(value);
            default:
                throw new ArgumentException($"Unknown value type '{valueType}'. Use text, number, logical or null.");
        }
    }

    // ============================================================ Wave L: demote headers, move column, conditional column

    /// <summary>Demote Headers -&gt; Table.DemoteHeaders (push the current column names back down into a data row).</summary>
    public static string DemoteHeaders(string currentM) =>
        AppendStep(currentM, "Demoted Headers", prev => $"Table.DemoteHeaders({prev})");

    /// <summary>
    /// Move Column -&gt; Table.ReorderColumns with a computed order. position = start | end | before | after.
    /// For before/after, <paramref name="refColumn"/> is required. The reorder uses
    /// Table.ColumnNames(prev) so it does not need the full schema up front.
    /// </summary>
    public static string MoveColumn(string currentM, string column, string position, string? refColumn)
    {
        if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column is required.");
        string col = Str(column);
        string pos = (position ?? "").Trim().ToLowerInvariant();
        string order = pos switch
        {
            "start" or "begin" or "beginning" or "first" =>
                $"List.Distinct(List.Combine({{{{{col}}}, Table.ColumnNames(_prev_)}}))",
            "end" or "last" =>
                $"List.Distinct(List.Combine({{List.RemoveItems(Table.ColumnNames(_prev_), {{{col}}}), {{{col}}}}}))",
            "before" => MoveRelativeOrder(col, RequireRef(refColumn, position), before: true),
            "after" => MoveRelativeOrder(col, RequireRef(refColumn, position), before: false),
            _ => throw new ArgumentException($"Unknown position '{position}'. Use start, end, before or after."),
        };
        return AppendStep(currentM, "Reordered Columns",
            prev => $"Table.ReorderColumns({prev}, {order.Replace("_prev_", prev)})");
    }

    private static string RequireRef(string? refColumn, string position) =>
        string.IsNullOrWhiteSpace(refColumn)
            ? throw new ArgumentException($"position '{position}' requires refColumn.")
            : Str(refColumn);

    /// <summary>Build a List.InsertRange-based order placing <paramref name="col"/> before/after <paramref name="refCol"/>.</summary>
    private static string MoveRelativeOrder(string col, string refCol, bool before)
    {
        // others = column names with the moved column removed; find ref's index; insert col before/after it.
        string others = $"List.RemoveItems(Table.ColumnNames(_prev_), {{{col}}})";
        string idx = before
            ? $"List.PositionOf({others}, {refCol})"
            : $"List.PositionOf({others}, {refCol}) + 1";
        return $"List.InsertRange({others}, {idx}, {{{col}}})";
    }

    /// <summary>
    /// Add Conditional Column -&gt; Table.AddColumn with a nested if/then/else chain (the structured
    /// Conditional Column builder). Each rule is {column, op, value, result}; op = eq|ne|gt|ge|lt|le|
    /// contains|startswith|endswith. <paramref name="elseResult"/> is the final else. Results and values are
    /// emitted as typed M literals via <paramref name="resultType"/> / per-rule value handling.
    /// </summary>
    public static string AddConditionalColumn(string currentM, string name,
        IReadOnlyList<(string column, string op, string? value, string? result)> rules,
        string? elseResult, string valueType = "text", string resultType = "text")
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required.");
        if (rules == null || rules.Count == 0) throw new ArgumentException("at least one rule is required.");

        var sb = new StringBuilder();
        foreach (var r in rules)
        {
            string lhs = $"[{EscapeM(r.column)}]";
            string rv = TypedLiteral(valueType, r.value);
            string cond = ConditionExpr(lhs, r.op, rv, r.value);
            sb.Append("if ").Append(cond).Append(" then ")
              .Append(TypedLiteral(resultType, r.result)).Append(" else ");
        }
        sb.Append(TypedLiteral(resultType, elseResult));
        string chain = sb.ToString();
        return AppendStep(currentM, "Added Conditional Column",
            prev => $"Table.AddColumn({prev}, {Str(name)}, each {chain}, {MType(resultType)})");
    }

    private static string ConditionExpr(string lhs, string op, string rvLiteral, string? rawValue) =>
        (op ?? "").Trim().ToLowerInvariant() switch
        {
            "eq" or "=" or "equals" => $"{lhs} = {rvLiteral}",
            "ne" or "<>" or "notequals" => $"{lhs} <> {rvLiteral}",
            "gt" or ">" => $"{lhs} > {rvLiteral}",
            "ge" or ">=" => $"{lhs} >= {rvLiteral}",
            "lt" or "<" => $"{lhs} < {rvLiteral}",
            "le" or "<=" => $"{lhs} <= {rvLiteral}",
            "contains" => $"Text.Contains({lhs}, {Str(rawValue ?? "")})",
            "startswith" => $"Text.StartsWith({lhs}, {Str(rawValue ?? "")})",
            "endswith" => $"Text.EndsWith({lhs}, {Str(rawValue ?? "")})",
            _ => throw new ArgumentException(
                $"Unknown condition op '{op}'. Use eq, ne, gt, ge, lt, le, contains, startswith or endswith."),
        };

    // ============================================================ Wave L: fuzzy join / group / cluster

    /// <summary>
    /// Fuzzy Merge -&gt; Table.FuzzyNestedJoin (+ optional Table.ExpandTableColumn). Options: threshold (0..1,
    /// default 0.8), ignoreCase, ignoreSpace, and an optional transformationTable (an M query name of a
    /// {From, To} mapping table). Mirrors Merge's nested-then-expand shape.
    /// </summary>
    public static string FuzzyMerge(string currentM, string rightTable, IReadOnlyList<string> leftKeys,
        IReadOnlyList<string> rightKeys, string joinKind, double threshold, bool? ignoreCase, bool? ignoreSpace,
        string? transformationTable, IReadOnlyList<string>? expandColumns)
    {
        if (leftKeys == null || leftKeys.Count == 0) throw new ArgumentException("leftKeys is required.");
        if (rightKeys == null || rightKeys.Count == 0) throw new ArgumentException("rightKeys is required.");
        if (leftKeys.Count != rightKeys.Count) throw new ArgumentException("leftKeys and rightKeys must have the same length.");

        string newCol = rightTable;
        string jk = JoinKindLiteral(joinKind);
        var opts = new List<string> { $"JoinKind={jk}", $"Threshold={threshold.ToString(CultureInfo.InvariantCulture)}" };
        if (ignoreCase.HasValue) opts.Add($"IgnoreCase={(ignoreCase.Value ? "true" : "false")}");
        if (ignoreSpace.HasValue) opts.Add($"IgnoreSpace={(ignoreSpace.Value ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(transformationTable)) opts.Add($"TransformationTable={TableRef(transformationTable!)}");
        string optRec = "[" + string.Join(", ", opts) + "]";

        string merged = AppendStep(currentM, "Fuzzy Merged Queries",
            prev => $"Table.FuzzyNestedJoin({prev}, {StrList(leftKeys)}, {TableRef(rightTable)}, {StrList(rightKeys)}, {Str(newCol)}, {optRec})");

        if (expandColumns == null || expandColumns.Count == 0) return merged;
        return AppendStep(merged, "Expanded " + newCol,
            prev => $"Table.ExpandTableColumn({prev}, {Str(newCol)}, {StrList(expandColumns)}, {StrList(expandColumns)})");
    }

    /// <summary>
    /// Fuzzy Group -&gt; Table.FuzzyGroup: collapse near-duplicate key values into one group with aggregates.
    /// Emits Table.FuzzyGroup(prev, keys, {aggregations}, [Threshold=...]). The aggregation tuples reuse the
    /// same op vocabulary as <see cref="GroupBy"/>.
    /// </summary>
    public static string FuzzyGroup(string currentM, IReadOnlyList<string> keyColumns,
        IReadOnlyList<(string name, string op, string? column)> aggregations, double? threshold)
    {
        if (keyColumns == null || keyColumns.Count == 0) throw new ArgumentException("keyColumns is required.");
        if (aggregations == null || aggregations.Count == 0) throw new ArgumentException("at least one aggregation is required.");
        var aggParts = aggregations.Select(a =>
            $"{{{Str(a.name)}, each {GroupOpExpr(a.op, a.column, "_")}, {GroupOpResultType(a.op)}}}");
        string aggList = "{" + string.Join(", ", aggParts) + "}";
        string keys = keyColumns.Count == 1 ? Str(keyColumns[0]) : StrList(keyColumns);
        string optArg = threshold.HasValue ? $", [Threshold={threshold.Value.ToString(CultureInfo.InvariantCulture)}]" : "";
        return AppendStep(currentM, "Fuzzy Grouped Rows",
            prev => $"Table.FuzzyGroup({prev}, {keys}, {aggList}{optArg})");
    }

    /// <summary>
    /// Fuzzy Cluster Column -&gt; Table.AddFuzzyClusterColumn: add a column whose value is the canonical
    /// cluster representative of <paramref name="column"/> (groups near-identical text values).
    /// </summary>
    public static string FuzzyClusterColumn(string currentM, string column, string newColumn, double? threshold)
    {
        if (string.IsNullOrWhiteSpace(column)) throw new ArgumentException("column is required.");
        if (string.IsNullOrWhiteSpace(newColumn)) throw new ArgumentException("newColumn is required.");
        string optArg = threshold.HasValue ? $", [Threshold={threshold.Value.ToString(CultureInfo.InvariantCulture)}]" : "";
        return AppendStep(currentM, "Added Fuzzy Cluster",
            prev => $"Table.AddFuzzyClusterColumn({prev}, {Str(column)}, {Str(newColumn)}{optArg})");
    }

    // ============================================================ Wave Q: community generators + folding + dataflow
    // These emit either a FULL M query (a self-contained let-block, for add_table_from_m) or one appended step
    // (via AppendStep, for the discrete-transform path). The community sources (gorilla.bi, Chris Webb,
    // Imke Feldmann, Ben Gribaudo, Goodly, powerquery.how) are distilled into the M shapes below.

    // ---------------------------------------------------------------- calendar / date table generators

    private static int InvMonth(int fiscalYearEndMonth)
    {
        if (fiscalYearEndMonth < 1 || fiscalYearEndMonth > 12)
            throw new ArgumentException("fiscalYearEndMonth must be 1..12.");
        return fiscalYearEndMonth;
    }

    /// <summary>
    /// Generate a full calendar (date) table query: List.Dates over [startExpr .. endExpr] expanded into a
    /// table with ~20 part columns (Year, Quarter, QuarterName, Month, MonthName, MonthShort, MonthNo,
    /// YearMonthNo, YearMonth, ISOWeek, ISOYear, WeekdayNo, DayName, DayNo, DayOfYear, StartOfMonth,
    /// EndOfMonth, IsWeekend). startExpr/endExpr are RAW M (e.g. <c>#date(2018,1,1)</c> or a parameter
    /// reference). When <paramref name="fiscalYearEndMonth"/> is supplied (1..12), FiscalYear / FiscalMonthNo /
    /// FiscalQuarter columns are added. <paramref name="locale"/> feeds the culture argument of the
    /// Date.*Name functions so MonthName/DayName render in that culture.
    /// </summary>
    public static string GenerateCalendarTableM(string startExpr, string endExpr,
        int? fiscalYearEndMonth = null, string? locale = null)
    {
        if (string.IsNullOrWhiteSpace(startExpr)) throw new ArgumentException("startExpr is required (raw M, e.g. #date(2020,1,1)).");
        if (string.IsNullOrWhiteSpace(endExpr)) throw new ArgumentException("endExpr is required (raw M, e.g. #date(2025,12,31)).");
        string cultureArg = string.IsNullOrWhiteSpace(locale) ? "" : $", {Str(locale.Trim())}";

        var sb = new StringBuilder();
        sb.Append("let\n");
        sb.Append("    StartDate = ").Append(startExpr.Trim()).Append(",\n");
        sb.Append("    EndDate = ").Append(endExpr.Trim()).Append(",\n");
        sb.Append("    DayCount = Duration.Days(Duration.From(EndDate - StartDate)) + 1,\n");
        sb.Append("    Dates = List.Dates(StartDate, DayCount, #duration(1, 0, 0, 0)),\n");
        sb.Append("    Source = Table.FromList(Dates, Splitter.SplitByNothing(), {\"Date\"}, null, ExtraValues.Error),\n");
        sb.Append("    TypedDate = Table.TransformColumnTypes(Source, {{\"Date\", type date}}),\n");
        sb.Append("    WithParts = Table.AddColumn(TypedDate, \"Year\", each Date.Year([Date]), Int64.Type),\n");
        sb.Append("    AddQuarter = Table.AddColumn(WithParts, \"Quarter\", each Date.QuarterOfYear([Date]), Int64.Type),\n");
        sb.Append("    AddQuarterName = Table.AddColumn(AddQuarter, \"QuarterName\", each \"Q\" & Text.From(Date.QuarterOfYear([Date])), type text),\n");
        sb.Append("    AddMonth = Table.AddColumn(AddQuarterName, \"MonthNo\", each Date.Month([Date]), Int64.Type),\n");
        sb.Append($"    AddMonthName = Table.AddColumn(AddMonth, \"MonthName\", each Date.MonthName([Date]{cultureArg}), type text),\n");
        sb.Append($"    AddMonthShort = Table.AddColumn(AddMonthName, \"MonthShort\", each Text.Start(Date.MonthName([Date]{cultureArg}), 3), type text),\n");
        sb.Append("    AddYearMonthNo = Table.AddColumn(AddMonthShort, \"YearMonthNo\", each Date.Year([Date]) * 100 + Date.Month([Date]), Int64.Type),\n");
        sb.Append("    AddYearMonth = Table.AddColumn(AddYearMonthNo, \"YearMonth\", each Text.From(Date.Year([Date])) & \"-\" & Text.PadStart(Text.From(Date.Month([Date])), 2, \"0\"), type text),\n");
        sb.Append("    AddIsoWeek = Table.AddColumn(AddYearMonth, \"ISOWeek\", each Date.WeekOfYear([Date], Day.Monday), Int64.Type),\n");
        sb.Append("    AddIsoYear = Table.AddColumn(AddIsoWeek, \"ISOYear\", each Date.Year(Date.AddDays([Date], 26 - Date.WeekOfYear([Date], Day.Monday) * 7)), Int64.Type),\n");
        sb.Append("    AddWeekdayNo = Table.AddColumn(AddIsoYear, \"WeekdayNo\", each Date.DayOfWeek([Date], Day.Monday) + 1, Int64.Type),\n");
        sb.Append($"    AddDayName = Table.AddColumn(AddWeekdayNo, \"DayName\", each Date.DayOfWeekName([Date]{cultureArg}), type text),\n");
        sb.Append("    AddDayNo = Table.AddColumn(AddDayName, \"DayNo\", each Date.Day([Date]), Int64.Type),\n");
        sb.Append("    AddDayOfYear = Table.AddColumn(AddDayNo, \"DayOfYear\", each Date.DayOfYear([Date]), Int64.Type),\n");
        sb.Append("    AddStartOfMonth = Table.AddColumn(AddDayOfYear, \"StartOfMonth\", each Date.StartOfMonth([Date]), type date),\n");
        sb.Append("    AddEndOfMonth = Table.AddColumn(AddStartOfMonth, \"EndOfMonth\", each Date.EndOfMonth([Date]), type date),\n");
        sb.Append("    AddIsWeekend = Table.AddColumn(AddEndOfMonth, \"IsWeekend\", each Date.DayOfWeek([Date], Day.Monday) >= 5, type logical)");

        string last = "AddIsWeekend";
        if (fiscalYearEndMonth.HasValue)
        {
            int fe = InvMonth(fiscalYearEndMonth.Value);
            // fiscal year starts the month AFTER the fiscal-year-end month; months on/after the start shift into the next fiscal year.
            int fs = fe == 12 ? 1 : fe + 1;
            string fsLit = fs.ToString(CultureInfo.InvariantCulture);
            sb.Append(",\n    AddFiscalMonthNo = Table.AddColumn(AddIsWeekend, \"FiscalMonthNo\", each Number.Mod(Date.Month([Date]) - ").Append(fsLit).Append(", 12) + 1, Int64.Type)");
            sb.Append(",\n    AddFiscalYear = Table.AddColumn(AddFiscalMonthNo, \"FiscalYear\", each if Date.Month([Date]) >= ").Append(fsLit).Append(" then Date.Year([Date]) + 1 else Date.Year([Date]), Int64.Type)");
            sb.Append(",\n    AddFiscalQuarter = Table.AddColumn(AddFiscalYear, \"FiscalQuarter\", each Number.RoundUp((Number.Mod(Date.Month([Date]) - ").Append(fsLit).Append(", 12) + 1) / 3), Int64.Type)");
            last = "AddFiscalQuarter";
        }

        sb.Append("\nin\n    ").Append(last);
        return sb.ToString();
    }

    /// <summary>
    /// Generate a retail 4-4-5 (/4-5-4 /5-4-4) calendar query. From <paramref name="startDate"/> (raw M
    /// <c>#date(...)</c>) it lays out <paramref name="periodsPerYear"/> periods per year where each period is
    /// 4 or 5 weeks per the chosen <paramref name="weeksPattern"/>, repeating the quarter pattern. Emits one
    /// row per WEEK with PeriodOfYear, WeekOfPeriod, WeekOfYear, WeekStart, WeekEnd and a RetailYear index.
    /// </summary>
    public static string Generate445CalendarM(string startDate, int weeksPattern, int periodsPerYear, int yearsToGenerate)
    {
        if (string.IsNullOrWhiteSpace(startDate)) throw new ArgumentException("startDate is required (raw M, e.g. #date(2024,1,29)).");
        if (yearsToGenerate < 1) throw new ArgumentException("yearsToGenerate must be one or greater.");
        // the per-period week counts for one quarter, derived from the pattern; quarters repeat to fill the year.
        int[] q = weeksPattern switch
        {
            445 => new[] { 4, 4, 5 },
            454 => new[] { 4, 5, 4 },
            544 => new[] { 5, 4, 4 },
            _ => throw new ArgumentException("weeksPattern must be 445, 454 or 544."),
        };
        if (periodsPerYear != 12 && periodsPerYear != 13)
            throw new ArgumentException("periodsPerYear must be 12 or 13.");
        // build the period -> week-count list. For 12 periods: repeat the 3-period quarter four times.
        // For 13 periods (Lunar/13x4): every period is 4 weeks (the pattern is unused but recorded).
        var weeksByPeriod = new List<int>();
        if (periodsPerYear == 13)
            for (int i = 0; i < 13; i++) weeksByPeriod.Add(4);
        else
            for (int quarter = 0; quarter < 4; quarter++) weeksByPeriod.AddRange(q);
        string weeksList = "{" + string.Join(", ", weeksByPeriod) + "}";

        var sb = new StringBuilder();
        sb.Append("let\n");
        sb.Append("    StartDate = ").Append(startDate.Trim()).Append(",\n");
        sb.Append("    Years = ").Append(yearsToGenerate.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        sb.Append("    WeeksByPeriod = ").Append(weeksList).Append(",\n");
        sb.Append("    TotalWeeksPerYear = List.Sum(WeeksByPeriod),\n");
        // one record per (retail year, period, week-in-period); WeekIndex is the running 0-based week from StartDate.
        sb.Append("    Rows = List.Combine(List.Transform({0 .. Years - 1}, (y) =>\n");
        sb.Append("        List.Combine(List.Transform({0 .. List.Count(WeeksByPeriod) - 1}, (p) =>\n");
        sb.Append("            List.Transform({1 .. WeeksByPeriod{p}}, (w) =>\n");
        sb.Append("                let\n");
        sb.Append("                    WeeksBeforePeriod = List.Sum(List.FirstN(WeeksByPeriod, p)),\n");
        sb.Append("                    WeekOfYear = WeeksBeforePeriod + w,\n");
        sb.Append("                    WeekIndex = y * TotalWeeksPerYear + WeekOfYear - 1,\n");
        sb.Append("                    WeekStart = Date.AddDays(StartDate, WeekIndex * 7)\n");
        sb.Append("                in\n");
        sb.Append("                    [RetailYear = y + 1, PeriodOfYear = p + 1, WeekOfPeriod = w, WeekOfYear = WeekOfYear, WeekIndex = WeekIndex + 1, WeekStart = WeekStart, WeekEnd = Date.AddDays(WeekStart, 6)])))),\n");
        sb.Append("    Source = Table.FromRecords(Rows),\n");
        sb.Append("    Typed = Table.TransformColumnTypes(Source, {{\"RetailYear\", Int64.Type}, {\"PeriodOfYear\", Int64.Type}, {\"WeekOfPeriod\", Int64.Type}, {\"WeekOfYear\", Int64.Type}, {\"WeekIndex\", Int64.Type}, {\"WeekStart\", type date}, {\"WeekEnd\", type date}})\n");
        sb.Append("in\n    Typed");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- paginated REST source

    /// <summary>
    /// Generate a paginated REST source query using a List.Generate page loop, accumulating each page then
    /// Table.Combine-ing them. mode = offset (page param + size param, stop when a page is short/empty) or
    /// cursor (read the next cursor/token from <paramref name="nextField"/>, stop when it is null). Each page
    /// is wrapped in Table.Buffer (the documented ~70% speed-up inside List.Generate). The base URL is kept
    /// STATIC and the page key folded through Web.Contents [Query=...] (the refresh-correct pattern). The
    /// records are read from <paramref name="dataPath"/> (a field on the JSON payload, e.g. "value" or
    /// "data"; empty = the payload IS the array).
    /// </summary>
    public static string PaginatedRestSourceM(string baseUrl, string mode, string dataPath,
        string? pageParam, string? sizeParam, int pageSize, string? nextField, string? recordFieldsExpr)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) throw new ArgumentException("baseUrl is required.");
        string m = (mode ?? "offset").Trim().ToLowerInvariant();
        string dp = (dataPath ?? "").Trim();
        // read the array of records out of one page's parsed JSON: either a named field or the whole body.
        string RecordsOf(string json) => dp.Length == 0 ? json : $"{json}[{EscapeM(dp)}]";

        var sb = new StringBuilder();
        sb.Append("let\n");
        sb.Append("    BaseUrl = ").Append(Str(baseUrl)).Append(",\n");

        if (m == "offset")
        {
            string pp = string.IsNullOrWhiteSpace(pageParam) ? "offset" : pageParam!.Trim();
            string spName = string.IsNullOrWhiteSpace(sizeParam) ? "limit" : sizeParam!.Trim();
            int size = pageSize > 0 ? pageSize : 100;
            string sizeLit = size.ToString(CultureInfo.InvariantCulture);
            sb.Append("    PageSize = ").Append(sizeLit).Append(",\n");
            // state record: [Offset, Data (current page rows)]. Continue while the last page filled the page size.
            sb.Append("    Pages = List.Generate(\n");
            sb.Append("        () => [Offset = 0, Data = GetPage(0)],\n");
            sb.Append("        (state) => List.Count(state[Data]) > 0,\n");
            sb.Append("        (state) => [Offset = state[Offset] + PageSize, Data = GetPage(state[Offset] + PageSize)],\n");
            sb.Append("        (state) => state[Data]),\n");
            sb.Append("    GetPage = (offset as number) as list =>\n");
            sb.Append("        let\n");
            sb.Append($"            Response = Web.Contents(BaseUrl, [Query = [{EscapeM(pp)} = Text.From(offset), {EscapeM(spName)} = Text.From(PageSize)]]),\n");
            sb.Append("            Json = Json.Document(Response),\n");
            sb.Append($"            Records = {RecordsOf("Json")},\n");
            sb.Append("            Buffered = List.Buffer(Records)\n");
            sb.Append("        in\n");
            sb.Append("            Buffered,\n");
        }
        else if (m == "cursor")
        {
            string nf = string.IsNullOrWhiteSpace(nextField)
                ? throw new ArgumentException("cursor mode requires nextField (the JSON field holding the next cursor/token).")
                : nextField!.Trim();
            string cp = string.IsNullOrWhiteSpace(pageParam) ? "cursor" : pageParam!.Trim();
            sb.Append("    Pages = List.Generate(\n");
            sb.Append("        () => [Cursor = null, Page = GetPage(null), First = true],\n");
            sb.Append("        (state) => state[First] or state[Cursor] <> null,\n");
            sb.Append("        (state) => [Cursor = state[Page][Next], Page = GetPage(state[Page][Next]), First = false],\n");
            sb.Append("        (state) => state[Page][Rows]),\n");
            sb.Append("    GetPage = (cursor) as record =>\n");
            sb.Append("        let\n");
            sb.Append($"            Query = if cursor = null then [] else [{EscapeM(cp)} = Text.From(cursor)],\n");
            sb.Append("            Response = Web.Contents(BaseUrl, [Query = Query]),\n");
            sb.Append("            Json = Json.Document(Response),\n");
            sb.Append($"            Records = {RecordsOf("Json")},\n");
            sb.Append($"            Next = Record.FieldOrDefault(Json, {Str(nf)}, null)\n");
            sb.Append("        in\n");
            sb.Append("            [Rows = List.Buffer(Records), Next = Next],\n");
        }
        else throw new ArgumentException("mode must be offset or cursor.");

        sb.Append("    AllRows = List.Combine(Pages),\n");
        string toTable = string.IsNullOrWhiteSpace(recordFieldsExpr)
            ? "Table.FromRecords(AllRows)"
            : $"Table.FromRecords(AllRows, {recordFieldsExpr!.Trim()})";
        sb.Append("    Source = ").Append(toTable).Append("\n");
        sb.Append("in\n    Source");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- combine folder files

    /// <summary>
    /// Generate a robust "combine files from folder" query: Folder.Files -&gt; filter by extension -&gt; per-file
    /// transform -&gt; Table.Combine. Crucially it does NOT use the Power Query default "Expand with a sample
    /// file" (which silently drops columns that appear only in later files); Table.Combine over per-file tables
    /// is schema-drift safe. fileType = csv | excel. <paramref name="keepFilename"/> adds [Source.Name] and
    /// [Source.Folder Path]; <paramref name="skipErrors"/> wraps each file's parse in try..otherwise so one bad
    /// file does not fail the whole refresh.
    /// </summary>
    public static string CombineFolderFilesM(string folderPath, string fileType, string? delimiter,
        int skipRows, bool promoteHeaders, bool keepFilename, bool skipErrors)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) throw new ArgumentException("folderPath is required.");
        string ft = (fileType ?? "csv").Trim().ToLowerInvariant();
        string ext = ft switch
        {
            "csv" => ".csv",
            "excel" or "xlsx" => ".xlsx",
            _ => throw new ArgumentException("fileType must be csv or excel."),
        };

        var sb = new StringBuilder();
        sb.Append("let\n");
        sb.Append("    Source = Folder.Files(").Append(Str(folderPath)).Append("),\n");
        sb.Append($"    Filtered = Table.SelectRows(Source, each Text.Lower([Extension]) = {Str(ext)}),\n");

        // the per-file reader: parse one [Content] binary into a table.
        var reader = new StringBuilder();
        reader.Append("(content as binary) as table =>\n        let\n");
        if (ft == "csv")
        {
            string del = string.IsNullOrWhiteSpace(delimiter) ? "," : delimiter!;
            reader.Append($"            Csv = Csv.Document(content, [Delimiter = {Str(del)}, Encoding = 65001, QuoteStyle = QuoteStyle.Csv]),\n");
            string afterSkip = skipRows > 0 ? "Skipped" : "Csv";
            if (skipRows > 0)
                reader.Append($"            Skipped = Table.Skip(Csv, {skipRows.ToString(CultureInfo.InvariantCulture)}),\n");
            reader.Append("            Promoted = ").Append(promoteHeaders ? $"Table.PromoteHeaders({afterSkip}, [PromoteAllScalars=true])" : afterSkip).Append("\n");
            reader.Append("        in\n            Promoted");
        }
        else
        {
            reader.Append("            Workbook = Excel.Workbook(content, null, true),\n");
            reader.Append("            FirstSheet = Workbook{0}[Data],\n");
            string afterSkip = skipRows > 0 ? "Skipped" : "FirstSheet";
            if (skipRows > 0)
                reader.Append($"            Skipped = Table.Skip(FirstSheet, {skipRows.ToString(CultureInfo.InvariantCulture)}),\n");
            reader.Append("            Promoted = ").Append(promoteHeaders ? $"Table.PromoteHeaders({afterSkip}, [PromoteAllScalars=true])" : afterSkip).Append("\n");
            reader.Append("        in\n            Promoted");
        }
        sb.Append("    ReadFile = ").Append(reader).Append(",\n");

        // add a [Data] table column per file; optionally keep the filename/folder, optionally swallow parse errors.
        string callReader = skipErrors
            ? "try ReadFile([Content]) otherwise #table({}, {})"
            : "ReadFile([Content])";
        sb.Append($"    WithData = Table.AddColumn(Filtered, \"Data\", each {callReader}),\n");

        if (keepFilename)
        {
            // attach the source name + folder to every row of each per-file table BEFORE combining.
            sb.Append("    Tagged = Table.AddColumn(WithData, \"Tagged\", each Table.AddColumn(Table.AddColumn([Data], \"Source.Name\", (r) => [Name]), \"Source.Folder Path\", (r) => [Folder Path])),\n");
            sb.Append("    Combined = Table.Combine(Tagged[Tagged])\n");
        }
        else
        {
            sb.Append("    Combined = Table.Combine(WithData[Data])\n");
        }
        sb.Append("in\n    Combined");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- control-table renames + bulk header transform

    /// <summary>
    /// Rename columns from a CONTROL TABLE: Table.RenameColumns(prev, Table.ToRows(Table.SelectColumns(
    /// mappingTable, {oldCol, newCol})), MissingField.Ignore). Driving the renames from a maintained mapping
    /// query (with MissingField.Ignore so absent columns are skipped) is the community pattern for portable,
    /// data-driven header standardisation.
    /// </summary>
    public static string RenameColumnsFromMapping(string currentM, string mappingTable, string oldCol, string newCol)
    {
        if (string.IsNullOrWhiteSpace(mappingTable)) throw new ArgumentException("mappingTable is required.");
        if (string.IsNullOrWhiteSpace(oldCol)) throw new ArgumentException("oldCol is required.");
        if (string.IsNullOrWhiteSpace(newCol)) throw new ArgumentException("newCol is required.");
        string rows = $"Table.ToRows(Table.SelectColumns({TableRef(mappingTable)}, {{{Str(oldCol)}, {Str(newCol)}}}))";
        return AppendStep(currentM, "Renamed From Mapping",
            prev => $"Table.RenameColumns({prev}, {rows}, MissingField.Ignore)");
    }

    /// <summary>
    /// Bulk-transform every column NAME with Table.TransformColumnNames. transform =
    /// snakeToSpace (underscores -&gt; spaces) | toUpper | toLower | trim | prefix (each name gets
    /// <paramref name="arg"/> prepended) | camelSplit (insert a space before each interior capital).
    /// Schema-agnostic: it never needs the column list up front.
    /// </summary>
    public static string TransformAllColumnNames(string currentM, string transform, string? arg)
    {
        string fn = (transform ?? "").Trim().ToLowerInvariant() switch
        {
            "snaketospace" => "each Text.Replace(_, \"_\", \" \")",
            "toupper" or "upper" => "Text.Upper",
            "tolower" or "lower" => "Text.Lower",
            "trim" => "Text.Trim",
            "prefix" => $"each {Str(arg ?? "")} & _",
            "camelsplit" => "each Text.Combine(List.Transform(Text.ToList(_), (c) => if c <> \"\" and c = Text.Upper(c) and c <> Text.Lower(c) then \" \" & c else c))",
            _ => throw new ArgumentException("transform must be snakeToSpace, toUpper, toLower, trim, prefix or camelSplit."),
        };
        return AppendStep(currentM, "Transformed Column Names",
            prev => $"Table.TransformColumnNames({prev}, {fn})");
    }

    // ---------------------------------------------------------------- group keeping all columns

    /// <summary>
    /// Group By keeping ALL columns: Table.Group(prev, keys, {{"AllRows", each _, type table}}) then expand
    /// the grouped table back out over its non-key columns - the workaround for the editor's Group By dropping
    /// every non-aggregated column. The expanded set is derived at evaluation time from the grouped sub-table's
    /// own column names (minus the keys) so it survives schema drift.
    /// </summary>
    public static string GroupKeepAllColumns(string currentM, IReadOnlyList<string> keys)
    {
        if (keys == null || keys.Count == 0) throw new ArgumentException("keys is required.");
        string grouped = AppendStep(currentM, "Grouped Keep All",
            prev => $"Table.Group({prev}, {StrList(keys)}, {{{{\"AllRows\", each _, type table}}}})");
        // non-key columns = the first grouped sub-table's columns minus the key columns.
        string others = $"List.Difference(Table.ColumnNames(_prev_{{0}}[AllRows]), {StrList(keys)})";
        return AppendStep(grouped, "Expanded AllRows",
            prev => $"Table.ExpandTableColumn({prev}, \"AllRows\", {others.Replace("_prev_", prev)})");
    }

    // ---------------------------------------------------------------- running total (fast, buffered)

    /// <summary>
    /// Add a FAST running total of <paramref name="valueColumn"/> ordered by <paramref name="orderColumn"/>,
    /// optionally restarting within each <paramref name="groupColumn"/> partition. Uses List.Buffer + a single
    /// List.Accumulate pass over the buffered value list (O(n)) rather than a per-row re-scan (O(n^2)) - the
    /// documented performant pattern. The table is sorted, indexed, then each row's total is the accumulated
    /// sum up to its index (reset when the group key changes, in the grouped variant).
    /// </summary>
    public static string RunningTotalM(string currentM, string valueColumn, string orderColumn, string? groupColumn)
    {
        if (string.IsNullOrWhiteSpace(valueColumn)) throw new ArgumentException("valueColumn is required.");
        if (string.IsNullOrWhiteSpace(orderColumn)) throw new ArgumentException("orderColumn is required.");

        bool grouped = !string.IsNullOrWhiteSpace(groupColumn);
        string sortKeys = grouped
            ? $"{{{{{Str(groupColumn!)}, Order.Ascending}}, {{{Str(orderColumn)}, Order.Ascending}}}}"
            : $"{{{{{Str(orderColumn)}, Order.Ascending}}}}";
        string sorted = AppendStep(currentM, "Sorted For Running Total",
            prev => $"Table.Sort({prev}, {sortKeys})");
        string indexed = AppendStep(sorted, "Indexed For Running Total",
            prev => $"Table.AddIndexColumn({prev}, \"RT_Index\", 0, 1, Int64.Type)");

        // buffer the value list (and group list, if any) ONCE, accumulate per index.
        if (!grouped)
        {
            const string body =
                "let Buffered = List.Buffer(Table.Column(_prev_, \"" + "__VAL__" + "\")) in " +
                "Table.AddColumn(_prev_, \"Running Total\", each List.Sum(List.FirstN(Buffered, [RT_Index] + 1)), type number)";
            string expr = body.Replace("__VAL__", EscapeM(valueColumn));
            return AppendStep(indexed, "Added Running Total", prev => expr.Replace("_prev_", prev));
        }
        else
        {
            // grouped: reset at each group boundary. Sum the buffered values from the group's first index up to this row.
            const string body =
                "let Vals = List.Buffer(Table.Column(_prev_, \"" + "__VAL__" + "\")), " +
                "Grps = List.Buffer(Table.Column(_prev_, \"" + "__GRP__" + "\")) in " +
                "Table.AddColumn(_prev_, \"Running Total\", each " +
                "let i = [RT_Index], g = Grps{i}, " +
                "start = List.PositionOf(Grps, g) in " +
                "List.Sum(List.Range(Vals, start, i - start + 1)), type number)";
            string expr = body.Replace("__VAL__", EscapeM(valueColumn)).Replace("__GRP__", EscapeM(groupColumn!));
            return AppendStep(indexed, "Added Running Total", prev => expr.Replace("_prev_", prev));
        }
    }

    // ---------------------------------------------------------------- text pivot / null-preserving unpivot

    /// <summary>
    /// Pivot TEXT values: Table.Pivot with a Text.Combine aggregation (the documented 5th-argument fix - the
    /// default pivot errors when the value column is text and has &gt;1 value per cell). Values colliding in a
    /// cell are joined with <paramref name="delimiter"/> (default ", ").
    /// </summary>
    public static string PivotTextValues(string currentM, string attributeColumn, string valueColumn, string? delimiter)
    {
        if (string.IsNullOrWhiteSpace(attributeColumn)) throw new ArgumentException("attributeColumn is required.");
        if (string.IsNullOrWhiteSpace(valueColumn)) throw new ArgumentException("valueColumn is required.");
        string del = delimiter ?? ", ";
        return AppendStep(currentM, "Pivoted Text Column",
            prev => $"Table.Pivot({prev}, List.Distinct({prev}[{EscapeM(attributeColumn)}]), {Str(attributeColumn)}, {Str(valueColumn)}, each Text.Combine(List.Transform(_, (v) => Text.From(v)), {Str(del)}))");
    }

    /// <summary>
    /// Unpivot keeping NULL rows: replace nulls with a sentinel, Table.UnpivotOtherColumns, then restore the
    /// sentinel to null - because plain unpivot silently DROPS rows whose value is null. The columns to keep
    /// (not unpivot) are <paramref name="keepColumns"/>.
    /// </summary>
    public static string UnpivotKeepNulls(string currentM, IReadOnlyList<string> keepColumns)
    {
        if (keepColumns == null || keepColumns.Count == 0) throw new ArgumentException("keepColumns is required.");
        const string sentinel = "##NULL##";
        // replace null -> sentinel across every column EXCEPT the keep columns (those keep their nulls intact for the unpivot key).
        string toUnpivot = $"List.Difference(Table.ColumnNames(_prev_), {StrList(keepColumns)})";
        string replaced = AppendStep(currentM, "Replaced Nulls For Unpivot",
            prev => $"Table.ReplaceValue({prev}, null, {Str(sentinel)}, Replacer.ReplaceValue, {toUnpivot.Replace("_prev_", prev)})");
        string unpivoted = AppendStep(replaced, "Unpivoted Other Columns",
            prev => $"Table.UnpivotOtherColumns({prev}, {StrList(keepColumns)}, \"Attribute\", \"Value\")");
        return AppendStep(unpivoted, "Restored Nulls",
            prev => $"Table.ReplaceValue({prev}, {Str(sentinel)}, null, Replacer.ReplaceValue, {{\"Value\"}})");
    }

    /// <summary>
    /// Dynamic Unpivot Other Columns: unpivot every column NOT in <paramref name="keepColumns"/>, deriving the
    /// unpivot set from Table.ColumnNames at evaluation time so any NEW attribute columns added to the source
    /// later are unpivoted automatically (Table.UnpivotOtherColumns already does this; this variant pins the
    /// keep set and is explicit about the dynamic intent).
    /// </summary>
    public static string DynamicUnpivotOtherColumns(string currentM, IReadOnlyList<string> keepColumns)
    {
        if (keepColumns == null || keepColumns.Count == 0) throw new ArgumentException("keepColumns is required.");
        return AppendStep(currentM, "Dynamic Unpivoted Other Columns",
            prev => $"Table.UnpivotOtherColumns({prev}, List.Intersect({{Table.ColumnNames({prev}), {StrList(keepColumns)}}}), \"Attribute\", \"Value\")");
    }

    /// <summary>
    /// Concatenate text within groups: Table.Group(prev, keys, {{out, each Text.Combine(.., delimiter)}}) -
    /// the inverse of split-to-rows, collapsing each group's <paramref name="textColumn"/> values into one
    /// delimited string. Output column defaults to "&lt;textColumn&gt; Concatenated".
    /// </summary>
    public static string ConcatenateWithGroupBy(string currentM, IReadOnlyList<string> keys, string textColumn,
        string? delimiter, string? outputColumn)
    {
        if (keys == null || keys.Count == 0) throw new ArgumentException("keys is required.");
        if (string.IsNullOrWhiteSpace(textColumn)) throw new ArgumentException("textColumn is required.");
        string del = delimiter ?? ", ";
        string outCol = string.IsNullOrWhiteSpace(outputColumn) ? textColumn + " Concatenated" : outputColumn!;
        return AppendStep(currentM, "Concatenated By Group",
            prev => $"Table.Group({prev}, {StrList(keys)}, {{{{{Str(outCol)}, each Text.Combine(List.Transform(Table.Column(_, {Str(textColumn)}), (v) => Text.From(v)), {Str(del)}), type text}}}})");
    }

    // ---------------------------------------------------------------- query folding (advanced)

    /// <summary>
    /// FLAG (advanced): scaffold a Table.View that implements custom query folding over a non-foldable source.
    /// Emits Table.View(null, [ handlers ]) wrapping the previous step. <paramref name="handlers"/> selects
    /// which optimisation handlers to include (case-insensitive): GetType, GetRows, OnTake, OnSkip,
    /// OnSelectColumns, OnSelectRows, GetRowCount. GetType + GetRows form the mandatory baseline; OnTake is the
    /// cheapest win (a bounded preview without reading the whole source). The bodies are TEMPLATES that defer
    /// to the underlying table and MUST be specialised to actually fold into the native source.
    /// </summary>
    public static string AddTableViewFolding(string currentM, IReadOnlyList<string> handlers)
    {
        var set = new HashSet<string>((handlers ?? Array.Empty<string>())
            .Select(h => (h ?? "").Trim().ToLowerInvariant()).Where(h => h.Length > 0));
        // always provide the mandatory baseline so the view is valid.
        set.Add("gettype");
        set.Add("getrows");

        var parts = new List<string>();
        if (set.Contains("gettype"))
            parts.Add("            GetType = () => Value.Type(prev)");
        if (set.Contains("getrows"))
            parts.Add("            GetRows = () => prev");
        if (set.Contains("ontake"))
            parts.Add("            OnTake = (count as number) => Table.FirstN(prev, count)");
        if (set.Contains("onskip"))
            parts.Add("            OnSkip = (count as number) => Table.Skip(prev, count)");
        if (set.Contains("onselectcolumns"))
            parts.Add("            OnSelectColumns = (columns as list) => Table.SelectColumns(prev, columns)");
        if (set.Contains("onselectrows"))
            parts.Add("            OnSelectRows = (condition) => Table.SelectRows(prev, condition)");
        if (set.Contains("getrowcount"))
            parts.Add("            GetRowCount = () => Table.RowCount(prev)");

        string handlersRec = "[\n" + string.Join(",\n", parts) + "\n        ]";
        return AppendStep(currentM, "Table View Folding",
            prev => $"(let prev = {prev} in Table.View(null, {handlersRec}))");
    }

    /// <summary>
    /// Value.NativeQuery folding: run a native query against a source connection and keep downstream folding
    /// ALIVE via [EnableFolding=true]. Emits Value.NativeQuery(sourceExpr, nativeQuery, params, [EnableFolding=true]).
    /// <paramref name="sourceExpr"/> is the raw M source (e.g. a Sql.Database(...) reference);
    /// <paramref name="paramsExpr"/> is the optional raw M parameter record/list (default null).
    /// </summary>
    public static string ValueNativeQueryFolding(string currentM, string sourceExpr, string nativeQuery, string? paramsExpr)
    {
        if (string.IsNullOrWhiteSpace(sourceExpr)) throw new ArgumentException("sourceExpr is required.");
        if (string.IsNullOrWhiteSpace(nativeQuery)) throw new ArgumentException("nativeQuery is required.");
        string pe = string.IsNullOrWhiteSpace(paramsExpr) ? "null" : paramsExpr!.Trim();
        return AppendStep(currentM, "Native Query",
            _ => $"Value.NativeQuery({sourceExpr.Trim()}, {Str(nativeQuery)}, {pe}, [EnableFolding=true])");
    }

    /// <summary>
    /// Perf heuristic: wrap a referenced list or table in List.Buffer / Table.Buffer to cache it for repeated
    /// reads (e.g. a lookup list scanned once per row). kind = list | table. This complements the per-query
    /// Table.Buffer step by buffering a NAMED reference rather than the current pipeline tail.
    /// </summary>
    public static string SetListBuffer(string currentM, string referenceExpr, string kind)
    {
        if (string.IsNullOrWhiteSpace(referenceExpr)) throw new ArgumentException("referenceExpr is required.");
        string fn = (kind ?? "list").Trim().ToLowerInvariant() switch
        {
            "list" => "List.Buffer",
            "table" => "Table.Buffer",
            _ => throw new ArgumentException("kind must be list or table."),
        };
        string stepName = fn == "List.Buffer" ? "Buffered List" : "Buffered Reference";
        return AppendStep(currentM, stepName, _ => $"{fn}({referenceExpr.Trim()})");
    }

    // ---------------------------------------------------------------- dataflow model.json export

    /// <summary>
    /// FLAG (best-known shape; the inner pbi:mashup layout is undocumented - verify against a real export):
    /// build a Power BI dataflow model.json document. Root carries name/version "1.0", a culture, and an
    /// entities[] array of LocalEntity objects each with attributes[] ({name, dataType}) and partitions[].
    /// The query mashup is embedded once at the root in the "pbi:mashup" extension as a document string. Each
    /// entity is <paramref name="entities"/> = (entityName, mExpression, attributes[]).
    /// </summary>
    public static string ExportDataflowModelJson(string dataflowName,
        IReadOnlyList<(string name, string mExpression, IReadOnlyList<(string name, string dataType)> attributes)> entities,
        string? culture)
    {
        if (string.IsNullOrWhiteSpace(dataflowName)) throw new ArgumentException("dataflowName is required.");
        if (entities == null || entities.Count == 0) throw new ArgumentException("at least one entity is required.");
        string cul = string.IsNullOrWhiteSpace(culture) ? "en-US" : culture!.Trim();

        // assemble all entity queries into ONE M document: shared <Name> = <expr>; (the dataflow mashup section).
        var doc = new StringBuilder();
        doc.Append("section Section1;\r\n");
        foreach (var e in entities)
        {
            string ident = IsSimpleIdentifier(e.name) ? e.name : "#\"" + e.name.Replace("\"", "\"\"") + "\"";
            doc.Append("shared ").Append(ident).Append(" = ").Append(e.mExpression.Trim()).Append(";\r\n");
        }

        var entityObjs = entities.Select(e =>
        {
            var attrs = (e.attributes ?? Array.Empty<(string, string)>())
                .Select(a => new Dictionary<string, object?>
                {
                    ["name"] = a.name,
                    ["dataType"] = MapCdmDataType(a.dataType),
                }).ToList();
            return new Dictionary<string, object?>
            {
                ["$type"] = "LocalEntity",
                ["name"] = e.name,
                ["description"] = "",
                ["pbi:refreshPolicy"] = null,
                ["attributes"] = attrs,
                ["partitions"] = new List<object>
                {
                    new Dictionary<string, object?>
                    {
                        ["name"] = e.name + "-partition",
                        ["refreshTime"] = null,
                        ["location"] = null,
                    },
                },
            };
        }).ToList();

        var root = new Dictionary<string, object?>
        {
            ["name"] = dataflowName,
            ["description"] = "",
            ["version"] = "1.0",
            ["culture"] = cul,
            ["modifiedTime"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture),
            ["pbi:mashup"] = new Dictionary<string, object?>
            {
                ["fastCombine"] = false,
                ["allowNativeQueries"] = false,
                ["queriesMetadata"] = entities.ToDictionary(
                    e => e.name,
                    e => (object?)new Dictionary<string, object?> { ["queryId"] = e.name, ["queryName"] = e.name, ["loadEnabled"] = true }),
                ["document"] = doc.ToString(),
            },
            ["annotations"] = new List<object>
            {
                new Dictionary<string, object?> { ["name"] = "pbi:QueryGroups", ["value"] = "[]" },
            },
            ["entities"] = entityObjs,
        };

        return System.Text.Json.JsonSerializer.Serialize(root,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Map a friendly type token to a CDM/dataflow attribute dataType.</summary>
    private static string MapCdmDataType(string token) => (token ?? "string").Trim().ToLowerInvariant() switch
    {
        "int" or "int64" or "integer" or "whole" or "long" => "int64",
        "number" or "double" or "real" or "float" or "decimal" => "double",
        "currency" or "fixed" => "decimal",
        "date" => "date",
        "datetime" or "datetimezone" => "dateTime",
        "time" => "time",
        "bool" or "boolean" or "logical" => "boolean",
        "guid" => "guid",
        "text" or "string" => "string",
        _ => "string",
    };
}
