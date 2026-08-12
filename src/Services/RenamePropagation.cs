using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>One requested rename. objectType = table | column | measure. Table carries the
/// (OLD) host table name for columns and measures; null for a table rename.</summary>
public sealed record RenameSpec(string ObjectType, string? Table, string OldName, string NewName);

/// <summary>
/// Wave G4 rename-propagation core: the validated rename batch (<see cref="RenameSet"/>), the
/// string-literal / comment / word-boundary aware DAX reference rewriter (<see cref="DaxRenamer"/> -
/// the generalisation of fix_case_sensitive_dax's matcher), the M query-name rewriter
/// (<see cref="MRenamer"/>), and the model-wide walkers that apply them to every expression site
/// (<see cref="ModelRenamer"/>). Everything here is pure / in-memory-TOM so the whole surface is
/// unit-testable without a live engine.
/// </summary>
public sealed class RenameSet
{
    /// <summary>old table name -> new table name.</summary>
    public Dictionary<string, string> TableMap { get; } = new(StringComparer.OrdinalIgnoreCase);

    // old table name -> (old column name -> new column name)
    private readonly Dictionary<string, Dictionary<string, string>> _columns = new(StringComparer.OrdinalIgnoreCase);
    // old measure name -> (new name, OLD home table name). Measure names are model-wide.
    private readonly Dictionary<string, (string NewName, string HomeTable)> _measures = new(StringComparer.OrdinalIgnoreCase);
    // every measure name in the model (bare-bracket ownership: [X] is a measure when any measure is named X)
    private readonly HashSet<string> _allMeasureNames = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEmpty => TableMap.Count == 0 && _columns.Count == 0 && _measures.Count == 0;

    public bool TryTable(string table, out string newName) => TableMap.TryGetValue(table, out newName!);

    public bool TryColumn(string table, string column, out string newName)
    {
        newName = column;
        return _columns.TryGetValue(table, out var cols) && cols.TryGetValue(column, out newName!);
    }

    public bool TryMeasure(string measure, out string newName, out string homeTable)
    {
        newName = measure; homeTable = "";
        if (!_measures.TryGetValue(measure, out var t)) return false;
        newName = t.NewName; homeTable = t.HomeTable;
        return true;
    }

    /// <summary>Bare-bracket ownership check: a model measure carries this name, so a bare [name]
    /// reference is treated as that measure, never as a same-named column.</summary>
    public bool MeasureNameExists(string name) => _allMeasureNames.Contains(name);

    public IEnumerable<(string Table, string Old, string New)> ColumnRenames =>
        _columns.SelectMany(t => t.Value.Select(c => (t.Key, c.Key, c.Value)));

    public IEnumerable<(string HomeTable, string Old, string New)> MeasureRenames =>
        _measures.Select(m => (m.Value.HomeTable, m.Key, m.Value.NewName));

    /// <summary>Build a validated rename set against the CURRENT model (old names everywhere).
    /// Throws on the first invalid spec - the single-rename tools want a hard failure; batch
    /// callers pre-filter with <see cref="RenamePlan.Validate"/>. Collision rules: a target name
    /// may not already exist unless that holder is itself renamed away by this set; a case-only
    /// rename of the same object is always allowed.</summary>
    public static RenameSet Build(TOM.Model model, IEnumerable<RenameSpec> specs)
    {
        var set = new RenameSet();
        foreach (var t in model.Tables)
            foreach (var m in t.Measures)
                set._allMeasureNames.Add(m.Name);

        foreach (var spec in specs)
        {
            string? error = set.TryAdd(model, spec);
            if (error != null) throw new InvalidOperationException(error);
        }
        if (set.IsEmpty) throw new InvalidOperationException("No renames to apply.");
        return set;
    }

    /// <summary>Validate one spec against the model plus the set built so far; add it when clean.
    /// Returns null on success, else the reason it cannot be applied. Shared by Build (throws)
    /// and RenamePlan.Validate (skips + reports).</summary>
    internal string? TryAdd(TOM.Model model, RenameSpec spec)
    {
        string type = (spec.ObjectType ?? "").Trim().ToLowerInvariant();
        string oldName = (spec.OldName ?? "").Trim();
        string newName = (spec.NewName ?? "").Trim();
        if (oldName.Length == 0 || newName.Length == 0) return $"{type}: oldName and newName are required.";
        if (oldName.Equals(newName, StringComparison.Ordinal)) return $"{type} '{oldName}': oldName equals newName (no-op).";
        bool caseOnly = oldName.Equals(newName, StringComparison.OrdinalIgnoreCase);

        switch (type)
        {
            case "table":
            {
                var t = model.Tables.Find(oldName);
                if (t == null) return $"table '{oldName}' not found.";
                if (TableMap.ContainsKey(oldName)) return $"table '{oldName}' is renamed twice in this batch.";
                if (!caseOnly)
                {
                    // chains/swaps (rename A->B while B->C) are refused outright: a single-pass JSON
                    // rewrite could cascade them, so the target must be genuinely free
                    if (model.Tables.Find(newName) != null)
                        return $"table '{oldName}': a table named '{newName}' already exists.";
                    if (TableMap.ContainsKey(newName) || TableMap.Values.Contains(newName, StringComparer.OrdinalIgnoreCase))
                        return $"table '{oldName}': '{newName}' is already part of another rename in this batch.";
                }
                TableMap[oldName] = newName;
                return null;
            }
            case "column":
            {
                string table = (spec.Table ?? "").Trim();
                if (table.Length == 0) return $"column '{oldName}': table is required.";
                var t = model.Tables.Find(table);
                if (t == null) return $"column '{table}[{oldName}]': table '{table}' not found.";
                var c = t.Columns.Find(oldName);
                if (c == null || c.Type == TOM.ColumnType.RowNumber) return $"column '{table}[{oldName}]' not found.";
                var cols = _columns.TryGetValue(table, out var existing) ? existing
                    : _columns[table] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (cols.ContainsKey(oldName)) return $"column '{table}[{oldName}]' is renamed twice in this batch.";
                if (!caseOnly)
                {
                    if (t.Columns.Find(newName) != null)
                        return $"column '{table}[{oldName}]': '{table}' already has a column named '{newName}'.";
                    if (cols.ContainsKey(newName) || cols.Values.Contains(newName, StringComparer.OrdinalIgnoreCase))
                        return $"column '{table}[{oldName}]': '{newName}' is already part of another rename in this batch.";
                }
                cols[oldName] = newName;
                return null;
            }
            case "measure":
            {
                string table = (spec.Table ?? "").Trim();
                if (table.Length == 0) return $"measure '{oldName}': table is required.";
                var t = model.Tables.Find(table);
                if (t == null) return $"measure '{table}[{oldName}]': table '{table}' not found.";
                if (t.Measures.Find(oldName) == null) return $"measure '{oldName}' not found on '{table}'.";
                if (_measures.ContainsKey(oldName)) return $"measure '{oldName}' is renamed twice in this batch.";
                if (!caseOnly)
                {
                    // measure names are a MODEL-WIDE name space; chains/swaps refused as above
                    if (model.Tables.Any(tb => tb.Measures.Find(newName) != null))
                        return $"measure '{oldName}': a measure named '{newName}' already exists (measure names are model-wide).";
                    if (_measures.ContainsKey(newName) || _measures.Values.Select(v => v.NewName).Contains(newName, StringComparer.OrdinalIgnoreCase))
                        return $"measure '{oldName}': '{newName}' is already part of another rename in this batch.";
                }
                _measures[oldName] = (newName, table);
                return null;
            }
            default:
                return $"unknown objectType '{spec.ObjectType}' - use table, column or measure.";
        }
    }
}

/// <summary>
/// The DAX reference rewriter: 'Table'[Column], bare Table[Column], standalone 'Table', [Measure]
/// and Table[Measure] forms, with string-literal awareness (nothing inside "..." is touched),
/// comment awareness (// -- /* */ are copied verbatim), word-boundary bare-identifier matching
/// (renaming Sales never touches SalesLY), ']]' / '''' escape handling, and case-insensitive name
/// matching (DAX object names are case-insensitive). Standalone UNQUOTED table references (e.g.
/// ALL ( Sales ) without brackets or quotes) are deliberately left alone - a bare word could be a
/// VAR or a function, and corrupting those is worse than a residual reference.
/// </summary>
public static class DaxRenamer
{
    /// <summary>Rewrite every renamed reference in one DAX expression. hostTable is the OLD name of
    /// the table whose row context the expression runs in (a calculated column's table, an RLS
    /// filter's table) so bare [Column] references there are rewritten too - but never when a model
    /// measure owns that bare name.</summary>
    public static string Rewrite(string dax, RenameSet set, string? hostTable = null)
    {
        if (string.IsNullOrEmpty(dax) || set.IsEmpty) return dax;
        var sb = new StringBuilder(dax.Length + 16);
        int i = 0, n = dax.Length;
        while (i < n)
        {
            char ch = dax[i];

            // ---- string literal: copied verbatim ("" is an escaped quote) ----
            if (ch == '"')
            {
                int j = i + 1;
                while (j < n)
                {
                    if (dax[j] == '"')
                    {
                        if (j + 1 < n && dax[j + 1] == '"') { j += 2; continue; }
                        j++; break;
                    }
                    j++;
                }
                sb.Append(dax, i, j - i);
                i = j;
                continue;
            }
            // ---- comments: copied verbatim ----
            if (ch == '/' && i + 1 < n && dax[i + 1] == '/') { i = AppendLineComment(dax, sb, i); continue; }
            if (ch == '-' && i + 1 < n && dax[i + 1] == '-') { i = AppendLineComment(dax, sb, i); continue; }
            if (ch == '/' && i + 1 < n && dax[i + 1] == '*')
            {
                int end = dax.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int j = end < 0 ? n : end + 2;
                sb.Append(dax, i, j - i);
                i = j;
                continue;
            }
            // ---- quoted table reference (a quoted identifier is ALWAYS a table in DAX) ----
            if (ch == '\'')
            {
                var q = ReadQuoted(dax, i);
                if (q == null) { sb.Append(dax, i, n - i); break; }   // unterminated - leave as-is
                var (tableOld, afterQuote) = q.Value;
                if (afterQuote < n && dax[afterQuote] == '[')
                {
                    var b = ReadBracket(dax, afterQuote);
                    if (b == null) { sb.Append(dax, i, n - i); break; }
                    var (fieldOld, afterBracket) = b.Value;
                    var (tableNew, fieldNew) = MapQualified(set, tableOld, fieldOld);
                    AppendTable(sb, tableNew, wasQuoted: true);
                    sb.Append('[').Append(fieldNew.Replace("]", "]]")).Append(']');
                    i = afterBracket;
                }
                else
                {
                    string tableNew = set.TryTable(tableOld, out var nt) ? nt : tableOld;
                    AppendTable(sb, tableNew, wasQuoted: true);
                    i = afterQuote;
                }
                continue;
            }
            // ---- bare [Field]: a measure anywhere, or a host-table column ----
            if (ch == '[')
            {
                var b = ReadBracket(dax, i);
                if (b == null) { sb.Append(dax, i, n - i); break; }
                var (inner, after) = b.Value;
                string mapped = MapBare(set, inner, hostTable);
                sb.Append('[').Append(mapped.Replace("]", "]]")).Append(']');
                i = after;
                continue;
            }
            // ---- bare identifier: only a table reference when immediately followed by '[' ----
            if (char.IsLetter(ch) || ch == '_')
            {
                int s0 = i;
                while (i < n && (char.IsLetterOrDigit(dax[i]) || dax[i] == '_')) i++;
                string ident = dax[s0..i];
                if (i < n && dax[i] == '[')
                {
                    var b = ReadBracket(dax, i);
                    if (b == null) { sb.Append(dax, s0, n - s0); break; }
                    var (fieldOld, afterBracket) = b.Value;
                    var (tableNew, fieldNew) = MapQualified(set, ident, fieldOld);
                    AppendTable(sb, tableNew, wasQuoted: false);
                    sb.Append('[').Append(fieldNew.Replace("]", "]]")).Append(']');
                    i = afterBracket;
                }
                else sb.Append(ident);
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    private static int AppendLineComment(string s, StringBuilder sb, int start)
    {
        int nl = s.IndexOf('\n', start);
        int end = nl < 0 ? s.Length : nl + 1;
        sb.Append(s, start, end - start);
        return end;
    }

    /// <summary>Read a 'quoted identifier' from s[start] == '\''; returns (unescaped name, index
    /// after the closing quote), or null when unterminated. '' is an escaped quote.</summary>
    private static (string name, int after)? ReadQuoted(string s, int start)
    {
        var sb = new StringBuilder();
        int i = start + 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == '\'')
            {
                if (i + 1 < s.Length && s[i + 1] == '\'') { sb.Append('\''); i += 2; continue; }
                return (sb.ToString(), i + 1);
            }
            sb.Append(c); i++;
        }
        return null;
    }

    /// <summary>Read a [bracketed name] from s[start] == '['; returns (unescaped name, index after
    /// the closing bracket), or null when unterminated. ]] is an escaped bracket.</summary>
    private static (string name, int after)? ReadBracket(string s, int start)
    {
        var sb = new StringBuilder();
        int i = start + 1;
        while (i < s.Length)
        {
            char c = s[i];
            if (c == ']')
            {
                if (i + 1 < s.Length && s[i + 1] == ']') { sb.Append(']'); i += 2; continue; }
                return (sb.ToString(), i + 1);
            }
            sb.Append(c); i++;
        }
        return null;
    }

    private static (string table, string field) MapQualified(RenameSet set, string table, string field)
    {
        string nf = field;
        if (set.TryColumn(table, field, out var nc)) nf = nc;
        else if (set.TryMeasure(field, out var nm, out var home) && home.Equals(table, StringComparison.OrdinalIgnoreCase)) nf = nm;
        string nt = set.TryTable(table, out var t2) ? t2 : table;
        return (nt, nf);
    }

    private static string MapBare(RenameSet set, string inner, string? hostTable)
    {
        if (set.TryMeasure(inner, out var nm, out _)) return nm;
        // a bare bracket owned by a measure name is a measure reference - never rewrite it as a column
        if (hostTable != null && !set.MeasureNameExists(inner) && set.TryColumn(hostTable, inner, out var nc)) return nc;
        return inner;
    }

    private static void AppendTable(StringBuilder sb, string table, bool wasQuoted)
    {
        if (wasQuoted || NeedsQuoting(table))
            sb.Append('\'').Append(table.Replace("'", "''")).Append('\'');
        else
            sb.Append(table);
    }

    /// <summary>A table name is safe unquoted only as letters/digits/underscores starting with a
    /// letter or underscore; anything else (spaces, punctuation, leading digit) must be quoted.</summary>
    internal static bool NeedsQuoting(string name)
    {
        if (name.Length == 0) return true;
        if (!(char.IsLetter(name[0]) || name[0] == '_')) return true;
        return name.Any(c => !(char.IsLetterOrDigit(c) || c == '_'));
    }
}

/// <summary>
/// The M (Power Query) query-name rewriter for a table rename where the table name IS the query
/// name: rewrites #"Old Name" quoted-identifier references and bare OldName identifier references
/// (word-boundary, Ordinal - M is case-sensitive) across partition M and shared expressions.
/// String literals and comments are copied verbatim.
/// </summary>
public static class MRenamer
{
    public static string RewriteTableRefs(string m, string oldName, string newName)
    {
        if (string.IsNullOrEmpty(m)) return m;
        var sb = new StringBuilder(m.Length + 8);
        bool bareOldValid = IsBareIdentifier(oldName);
        int i = 0, n = m.Length;
        while (i < n)
        {
            char c = m[i];
            // string literal ("" is an escaped quote)
            if (c == '"')
            {
                int j = i + 1;
                while (j < n)
                {
                    if (m[j] == '"')
                    {
                        if (j + 1 < n && m[j + 1] == '"') { j += 2; continue; }
                        j++; break;
                    }
                    j++;
                }
                sb.Append(m, i, j - i);
                i = j;
                continue;
            }
            // comments
            if (c == '/' && i + 1 < n && m[i + 1] == '/')
            {
                int nl = m.IndexOf('\n', i);
                int end = nl < 0 ? n : nl + 1;
                sb.Append(m, i, end - i);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < n && m[i + 1] == '*')
            {
                int close = m.IndexOf("*/", i + 2, StringComparison.Ordinal);
                int end = close < 0 ? n : close + 2;
                sb.Append(m, i, end - i);
                i = end;
                continue;
            }
            // #"quoted identifier" reference
            if (c == '#' && i + 1 < n && m[i + 1] == '"')
            {
                int j = i + 2;
                var inner = new StringBuilder();
                bool terminated = false;
                while (j < n)
                {
                    if (m[j] == '"')
                    {
                        if (j + 1 < n && m[j + 1] == '"') { inner.Append('"'); j += 2; continue; }
                        j++; terminated = true; break;
                    }
                    inner.Append(m[j]); j++;
                }
                if (terminated && inner.ToString().Equals(oldName, StringComparison.Ordinal))
                    sb.Append(RefToken(newName));
                else
                    sb.Append(m, i, j - i);
                i = j;
                continue;
            }
            // bare identifier (word-boundary; M identifiers may carry dots - Table.SelectRows is ONE token)
            if (char.IsLetter(c) || c == '_')
            {
                int s0 = i;
                while (i < n && (char.IsLetterOrDigit(m[i]) || m[i] == '_' || m[i] == '.')) i++;
                string ident = m[s0..i];
                if (bareOldValid && ident.Equals(oldName, StringComparison.Ordinal))
                    sb.Append(RefToken(newName));
                else
                    sb.Append(ident);
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static string RefToken(string name) =>
        IsBareIdentifier(name) ? name : "#\"" + name.Replace("\"", "\"\"") + "\"";

    internal static bool IsBareIdentifier(string name)
    {
        if (name.Length == 0) return false;
        if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
        return name.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}

/// <summary>Counts of rewritten DAX sites by category, plus every site label.</summary>
public sealed class ModelDaxRewriteResult
{
    public Dictionary<string, int> Categories { get; } = new(StringComparer.Ordinal);
    public List<string> Sites { get; } = new();
    public int Total => Categories.Values.Sum();
    internal void Hit(string category, string site)
    {
        Categories[category] = Categories.GetValueOrDefault(category) + 1;
        Sites.Add($"{category}: {site}");
    }
}

/// <summary>The model-wide walkers that apply a <see cref="RenameSet"/> to every expression site
/// and to the TOM objects themselves. Call order matters: RewriteModelDax runs BEFORE
/// ApplyTomRenames (host tables still carry old names), RewriteModelM after (partitions are found
/// on the renamed tables).</summary>
public static class ModelRenamer
{
    /// <summary>Rewrite every DAX expression site in the model: measures (expression, dynamic
    /// format string, detail rows, KPI), calculated columns, calculated tables, calculation items
    /// (expression + format-string expression), table default detail rows, and RLS filter
    /// expressions. Pure text rewriting - no SaveChanges.</summary>
    public static ModelDaxRewriteResult RewriteModelDax(TOM.Model model, RenameSet set)
    {
        var result = new ModelDaxRewriteResult();

        void Rw(string category, string site, Func<string?> get, Action<string> put, string? hostTable = null)
        {
            string expr = get() ?? "";
            if (expr.Length == 0) return;
            string rewritten = DaxRenamer.Rewrite(expr, set, hostTable);
            if (!rewritten.Equals(expr, StringComparison.Ordinal))
            {
                put(rewritten);
                result.Hit(category, site);
            }
        }

        foreach (var t in model.Tables)
        {
            foreach (var m in t.Measures)
            {
                string site = $"{t.Name}[{m.Name}]";
                Rw("measures", site, () => m.Expression, v => m.Expression = v);
                if (m.FormatStringDefinition != null)
                    Rw("formatStringExpressions", site, () => m.FormatStringDefinition.Expression, v => m.FormatStringDefinition.Expression = v);
                if (m.DetailRowsDefinition != null)
                    Rw("detailRowsExpressions", site, () => m.DetailRowsDefinition.Expression, v => m.DetailRowsDefinition.Expression = v);
                if (m.KPI is { } kpi)
                {
                    Rw("kpiExpressions", site, () => kpi.TargetExpression, v => kpi.TargetExpression = v);
                    Rw("kpiExpressions", site, () => kpi.StatusExpression, v => kpi.StatusExpression = v);
                    Rw("kpiExpressions", site, () => kpi.TrendExpression, v => kpi.TrendExpression = v);
                }
            }
            foreach (var c in t.Columns.OfType<TOM.CalculatedColumn>())
                Rw("calculatedColumns", $"{t.Name}[{c.Name}]", () => c.Expression, v => c.Expression = v, hostTable: t.Name);
            foreach (var p in t.Partitions)
                if (p.Source is TOM.CalculatedPartitionSource cps)
                    Rw("calculatedTables", $"{t.Name}/{p.Name}", () => cps.Expression, v => cps.Expression = v);
            if (t.DefaultDetailRowsDefinition != null)
                Rw("detailRowsExpressions", t.Name, () => t.DefaultDetailRowsDefinition.Expression, v => t.DefaultDetailRowsDefinition.Expression = v);
            if (t.CalculationGroup is { } cg)
                foreach (var item in cg.CalculationItems)
                {
                    string site = $"{t.Name}.{item.Name}";
                    Rw("calculationItems", site, () => item.Expression, v => item.Expression = v);
                    if (item.FormatStringDefinition != null)
                        Rw("formatStringExpressions", site, () => item.FormatStringDefinition.Expression, v => item.FormatStringDefinition.Expression = v);
                }
        }
        foreach (var role in model.Roles)
            foreach (var tp in role.TablePermissions)
                // the RLS filter's row context is its table, so bare [Column] references resolve there
                Rw("rlsFilterExpressions", $"{role.Name}/{tp.Table?.Name}", () => tp.FilterExpression, v => tp.FilterExpression = v,
                    hostTable: tp.Table?.Name);
        return result;
    }

    /// <summary>Apply the TOM object renames. Measures and columns first (found via their OLD host
    /// table names), tables last. Validation already ran in <see cref="RenameSet.Build"/>.</summary>
    public static List<string> ApplyTomRenames(TOM.Model model, RenameSet set)
    {
        var applied = new List<string>();
        foreach (var (home, oldName, newName) in set.MeasureRenames)
        {
            var t = model.Tables.Find(home) ?? throw new InvalidOperationException($"Table '{home}' not found.");
            var m = t.Measures.Find(oldName) ?? throw new InvalidOperationException($"Measure '{oldName}' not found on '{home}'.");
            m.Name = newName;
            applied.Add($"measure {home}[{oldName}] -> [{newName}]");
        }
        foreach (var (table, oldName, newName) in set.ColumnRenames)
        {
            var t = model.Tables.Find(table) ?? throw new InvalidOperationException($"Table '{table}' not found.");
            var c = t.Columns.Find(oldName) ?? throw new InvalidOperationException($"Column '{table}[{oldName}]' not found.");
            c.Name = newName;
            applied.Add($"column {table}[{oldName}] -> [{newName}]");
        }
        foreach (var (oldName, newName) in set.TableMap)
        {
            var t = model.Tables.Find(oldName) ?? throw new InvalidOperationException($"Table '{oldName}' not found.");
            t.Name = newName;
            applied.Add($"table {oldName} -> {newName}");
        }
        return applied;
    }

    /// <summary>Rewrite M references to renamed tables (the table name is the partition/query name):
    /// every M partition expression and shared (named) M expression, plus renaming the renamed
    /// table's partition when it carried the old table name. Call AFTER ApplyTomRenames.</summary>
    public static (int sites, List<string> partitionsRenamed) RewriteModelM(TOM.Model model, RenameSet set)
    {
        int sites = 0;
        var partitionsRenamed = new List<string>();
        foreach (var (oldName, newName) in set.TableMap)
        {
            foreach (var t in model.Tables)
                foreach (var p in t.Partitions)
                    if (p.Source is TOM.MPartitionSource mps && mps.Expression is { Length: > 0 } expr)
                    {
                        string rewritten = MRenamer.RewriteTableRefs(expr, oldName, newName);
                        if (!rewritten.Equals(expr, StringComparison.Ordinal)) { mps.Expression = rewritten; sites++; }
                    }
            foreach (var ne in model.Expressions)
                if (ne.Kind == TOM.ExpressionKind.M && ne.Expression is { Length: > 0 } expr)
                {
                    string rewritten = MRenamer.RewriteTableRefs(expr, oldName, newName);
                    if (!rewritten.Equals(expr, StringComparison.Ordinal)) { ne.Expression = rewritten; sites++; }
                }
            // the renamed table's own partition keeps the query name in step with the table name
            var renamed = model.Tables.Find(newName);
            var part = renamed?.Partitions.Find(oldName);
            if (renamed != null && part != null && renamed.Partitions.Find(newName) == null)
            {
                part.Name = newName;
                partitionsRenamed.Add($"{newName}/{oldName} -> {newName}");
            }
        }
        return (sites, partitionsRenamed);
    }

    /// <summary>Model wiring that follows a rename automatically because TOM holds OBJECT references
    /// (verified by tests): sort-by columns, hierarchy levels, relationship ends, variations and
    /// translations pointing at renamed objects. Informational counts, computed BEFORE the rename.</summary>
    public static object WiringSummary(TOM.Model model, RenameSet set)
    {
        int sortBy = 0, hierarchyLevels = 0, relationshipEnds = 0, translations = 0, variations = 0;
        bool ColRenamed(string table, string col) => set.TryColumn(table, col, out _);

        foreach (var t in model.Tables)
        {
            bool tableRenamed = set.TryTable(t.Name, out _);
            foreach (var c in t.Columns)
            {
                if (c.SortByColumn != null && ColRenamed(t.Name, c.SortByColumn.Name)) sortBy++;
                foreach (var v in c.Variations)
                    if (tableRenamed
                        || (v.DefaultColumn != null && ColRenamed(v.DefaultColumn.Table?.Name ?? "", v.DefaultColumn.Name))
                        || (v.DefaultHierarchy?.Table != null && set.TryTable(v.DefaultHierarchy.Table.Name, out _)))
                        variations++;
            }
            foreach (var h in t.Hierarchies)
                foreach (var l in h.Levels)
                    if (l.Column != null && (tableRenamed || ColRenamed(t.Name, l.Column.Name))) hierarchyLevels++;
        }
        foreach (var r in model.Relationships.OfType<TOM.SingleColumnRelationship>())
        {
            if (r.FromColumn != null && (set.TryTable(r.FromTable?.Name ?? "", out _) || ColRenamed(r.FromTable?.Name ?? "", r.FromColumn.Name))) relationshipEnds++;
            if (r.ToColumn != null && (set.TryTable(r.ToTable?.Name ?? "", out _) || ColRenamed(r.ToTable?.Name ?? "", r.ToColumn.Name))) relationshipEnds++;
        }
        foreach (var cu in model.Cultures)
            foreach (var ot in cu.ObjectTranslations)
                switch (ot.Object)
                {
                    case TOM.Table tt when set.TryTable(tt.Name, out _): translations++; break;
                    case TOM.Column cc when cc.Table != null && (set.TryTable(cc.Table.Name, out _) || ColRenamed(cc.Table.Name, cc.Name)): translations++; break;
                    case TOM.Measure mm when set.TryMeasure(mm.Name, out _, out _): translations++; break;
                }

        return new
        {
            sortByColumns = sortBy,
            hierarchyLevels,
            relationshipEnds,
            variations,
            translations,
            note = "These are TOM object references - they carry over automatically when the object is renamed.",
        };
    }

    /// <summary>The report-side rename maps, built from the OLD model (before ApplyTomRenames):
    /// fieldMap "Old Table[Old Field]" -> "New Table[New Field]" for every renamed column/measure,
    /// and entityMap "Old Table" -> "New Table" for table renames (every field on a renamed table
    /// follows via the entity map even when the field itself keeps its name).</summary>
    public static (Dictionary<string, string> fieldMap, Dictionary<string, string> entityMap)
        BuildReportMaps(TOM.Model model, RenameSet set)
    {
        var fieldMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var entityMap = new Dictionary<string, string>(set.TableMap, StringComparer.OrdinalIgnoreCase);

        foreach (var (table, oldName, newName) in set.ColumnRenames)
        {
            string newTable = set.TryTable(table, out var nt) ? nt : table;
            fieldMap[$"{table}[{oldName}]"] = $"{newTable}[{newName}]";
        }
        foreach (var (home, oldName, newName) in set.MeasureRenames)
        {
            string newTable = set.TryTable(home, out var nt) ? nt : home;
            fieldMap[$"{home}[{oldName}]"] = $"{newTable}[{newName}]";
        }
        return (fieldMap, entityMap);
    }
}

/// <summary>apply_rename_plan parsing + per-row validation over the audit_naming plan shape.</summary>
public static class RenamePlan
{
    private sealed class PlanRow
    {
        public string? ObjectType { get; set; }
        public string? Table { get; set; }
        public string? OldName { get; set; }
        public string? NewName { get; set; }
    }

    private static readonly JsonSerializerOptions Ci = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parse the plan json. Accepts audit_naming's {renamePlan:{renames:[...]}} envelope,
    /// a {renames:[...]} object, or a bare [...] array. Rows missing required fields come back in
    /// rejected (with the reason) rather than aborting the batch.</summary>
    public static (List<RenameSpec> specs, List<object> rejected) Parse(string planJson)
    {
        if (string.IsNullOrWhiteSpace(planJson))
            throw new ArgumentException("planJson is empty - pass audit_naming's renamePlan (or {renames:[...]}).");
        var node = JsonNode.Parse(planJson)
            ?? throw new ArgumentException("planJson is not valid JSON.");
        JsonArray? rows = node as JsonArray
            ?? node["renames"] as JsonArray
            ?? node["renamePlan"]?["renames"] as JsonArray;
        if (rows == null)
            throw new ArgumentException("planJson has no renames array - expected {renames:[{objectType, table, oldName, newName}]} (audit_naming's renamePlan) or a bare array.");

        var specs = new List<RenameSpec>();
        var rejected = new List<object>();
        int index = 0;
        foreach (var n in rows)
        {
            index++;
            var row = n?.Deserialize<PlanRow>(Ci);
            string type = (row?.ObjectType ?? "").Trim().ToLowerInvariant();
            if (row == null || type.Length == 0 || string.IsNullOrWhiteSpace(row.OldName) || string.IsNullOrWhiteSpace(row.NewName))
            {
                rejected.Add(new { row = index, reason = "row needs objectType, oldName and newName (and table for column/measure rows).", raw = n?.ToJsonString() });
                continue;
            }
            // a table row may omit table (oldName IS the table)
            string? table = string.IsNullOrWhiteSpace(row.Table) ? (type == "table" ? null : row.Table) : row.Table!.Trim();
            specs.Add(new RenameSpec(type, table, row.OldName!.Trim(), row.NewName!.Trim()));
        }
        return (specs, rejected);
    }

    /// <summary>Row-wise validation against the model: rows that cannot apply (missing object,
    /// collision, duplicate) are skipped WITH the reason; the survivors form one atomic batch.</summary>
    public static (List<RenameSpec> valid, List<object> skipped) Validate(TOM.Model model, IReadOnlyList<RenameSpec> specs)
    {
        var probe = new RenameSet();   // collision tracking only - Build re-validates the survivors
        var valid = new List<RenameSpec>();
        var skipped = new List<object>();
        foreach (var spec in specs)
        {
            string? error = probe.TryAdd(model, spec);
            if (error == null) valid.Add(spec);
            else skipped.Add(new { spec.ObjectType, spec.Table, spec.OldName, spec.NewName, reason = error });
        }
        return (valid, skipped);
    }
}
