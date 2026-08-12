using System.Text.Json.Nodes;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>One model-field reference found in a report: where it is bound and in what role.
/// Context values: projection | filter | sort | conditional-formatting | chrome-binding | tooltip.</summary>
public sealed record ReportFieldUse(string Table, string Field, bool IsMeasure, string Context, string Page, string Visual);

/// <summary>
/// Wave G3 cross-layer core: the PURE logic that joins the semantic model with the report layer.
/// The collectors (legacy Layout in ReportService, PBIR here) produce format-agnostic
/// <see cref="ReportFieldUse"/> rows; this class classifies every model field as DIRECT (bound to
/// a visual projection / filter / slicer / tooltip / conditional-formatting binding), INDIRECT
/// (reached through a direct measure's DAX lineage, a relationship path, a sort-by column or a
/// model-internal reference) or UNUSED (the safe-to-remove shortlist), computes an object's blast
/// radius (impact_analysis), and flags report bindings that point at missing model objects
/// (scan_broken_refs). Everything here is static and unit-testable against a new TOM.Model().
/// </summary>
public static class CrossLayerAnalyzer
{
    // ---------------------------------------------------------------- model inventory

    /// <summary>A flattened model schema: every column/measure keyed "table[field]" (lower-case).</summary>
    public sealed class ModelInventory
    {
        public Dictionary<string, string> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);   // key -> display "Table[Col]"
        public Dictionary<string, string> Measures { get; } = new(StringComparer.OrdinalIgnoreCase);  // key -> display "Table[Measure]"
        public Dictionary<string, string> MeasureDax { get; } = new(StringComparer.OrdinalIgnoreCase);      // key -> expression
        public Dictionary<string, string> MeasureHomeByName { get; } = new(StringComparer.OrdinalIgnoreCase); // measure name -> table
        public List<(string FromTable, string FromColumn, string ToTable, string ToColumn)> Relationships { get; } = new();
        public Dictionary<string, string> SortBy { get; } = new(StringComparer.OrdinalIgnoreCase);    // column key -> sort-by column key
        public HashSet<string> ModelInternallyUsed { get; } = new(StringComparer.OrdinalIgnoreCase);  // keys NOT in find_unused's shortlist
        public HashSet<string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public static string Key(string table, string field) => $"{table.Trim()}[{field.Trim()}]";

    /// <summary>Flatten a TOM model into the inventory the classifiers run over. Reuses
    /// ModelService.FindUnusedCore so "model-internal use" is judged by the exact same rules as
    /// find_unused (relationships, sort-by, hierarchies, RLS, measure / calc DAX).</summary>
    public static ModelInventory BuildInventory(TOM.Model model)
    {
        var inv = new ModelInventory();
        foreach (var t in model.Tables)
        {
            inv.Tables.Add(t.Name);
            foreach (var c in t.Columns)
            {
                if (c.Type == TOM.ColumnType.RowNumber) continue;
                inv.Columns[Key(t.Name, c.Name)] = $"{t.Name}[{c.Name}]";
                if (c.SortByColumn != null)
                    inv.SortBy[Key(t.Name, c.Name)] = Key(t.Name, c.SortByColumn.Name);
            }
            foreach (var m in t.Measures)
            {
                inv.Measures[Key(t.Name, m.Name)] = $"{t.Name}[{m.Name}]";
                inv.MeasureDax[Key(t.Name, m.Name)] = m.Expression ?? "";
                inv.MeasureHomeByName[m.Name] = t.Name;
            }
        }
        foreach (var r in model.Relationships.OfType<TOM.SingleColumnRelationship>())
            if (r.FromColumn != null && r.ToColumn != null)
                inv.Relationships.Add((r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name));

        // model-internal use = everything find_unused does NOT list as unused
        var (unusedCols, unusedMeasures) = ModelService.FindUnusedCore(model);
        var unused = new HashSet<string>(unusedCols.Concat(unusedMeasures), StringComparer.OrdinalIgnoreCase);
        foreach (var k in inv.Columns.Keys.Concat(inv.Measures.Keys))
            if (!unused.Contains(k)) inv.ModelInternallyUsed.Add(k);
        return inv;
    }

    // ---------------------------------------------------------------- DAX lineage

    /// <summary>The (measure keys, column keys) a DAX expression references, resolved against the
    /// inventory. Measures match on "[Name]"; columns match on "Table[Col]" / "'Table'[Col]", plus
    /// bare "[Col]" only when no measure shares the name (measures own bare-bracket by convention).</summary>
    internal static (HashSet<string> measures, HashSet<string> columns) DaxRefs(ModelInventory inv, string dax)
    {
        var measures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(dax)) return (measures, columns);

        foreach (var (key, disp) in inv.Measures)
        {
            string name = disp[(disp.IndexOf('[') + 1)..^1];
            if (dax.IndexOf($"[{name}]", StringComparison.OrdinalIgnoreCase) >= 0) measures.Add(key);
        }
        foreach (var (key, disp) in inv.Columns)
        {
            int lb = disp.IndexOf('[');
            string table = disp[..lb], col = disp[lb..];
            if (dax.IndexOf($"{table}{col}", StringComparison.OrdinalIgnoreCase) >= 0
                || dax.IndexOf($"'{table}'{col}", StringComparison.OrdinalIgnoreCase) >= 0)
                columns.Add(key);
            else if (!inv.MeasureHomeByName.ContainsKey(col[1..^1])
                     && dax.IndexOf(col, StringComparison.OrdinalIgnoreCase) >= 0)
                columns.Add(key);
        }
        return (measures, columns);
    }

    // ---------------------------------------------------------------- classification (model_report_usage)

    public static object Classify(ModelInventory inv, IReadOnlyList<ReportFieldUse> uses, string sourceKind)
    {
        // 1) DIRECT: every report binding that resolves to a real model field
        var direct = new Dictionary<string, List<ReportFieldUse>>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<ReportFieldUse>();
        foreach (var u in uses)
        {
            string key = Key(u.Table, u.Field);
            if (inv.Columns.ContainsKey(key) || inv.Measures.ContainsKey(key))
            {
                if (!direct.TryGetValue(key, out var list)) direct[key] = list = new List<ReportFieldUse>();
                list.Add(u);
            }
            else unresolved.Add(u);
        }

        // 2) INDIRECT via DAX lineage: transitive closure from the directly bound measures
        var indirect = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        void MarkIndirect(string key, string reason)
        {
            if (direct.ContainsKey(key)) return;
            if (!indirect.TryGetValue(key, out var reasons)) indirect[key] = reasons = new List<string>();
            if (!reasons.Contains(reason)) reasons.Add(reason);
        }

        var queue = new Queue<string>(direct.Keys.Where(inv.Measures.ContainsKey));
        var visited = new HashSet<string>(queue, StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            string mk = queue.Dequeue();
            var (refMeasures, refColumns) = DaxRefs(inv, inv.MeasureDax.GetValueOrDefault(mk, ""));
            foreach (var rm in refMeasures)
            {
                if (rm.Equals(mk, StringComparison.OrdinalIgnoreCase)) continue;
                MarkIndirect(rm, $"DAX lineage of {Display(inv, mk)}");
                if (visited.Add(rm)) queue.Enqueue(rm);
            }
            foreach (var rc in refColumns)
                MarkIndirect(rc, $"DAX lineage of {Display(inv, mk)}");
        }

        // 3) INDIRECT via relationship paths: tables hosting any direct/lineage field are "in
        // play"; every relationship whose two ends are reachable from an in-play table carries the
        // filter flow, so its key columns are load-bearing (bridge tables get pulled in too)
        var inPlayTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in direct.Keys.Concat(indirect.Keys))
        {
            int lb = key.IndexOf('[');
            if (lb > 0) inPlayTables.Add(key[..lb]);
        }
        var reachable = new HashSet<string>(inPlayTables, StringComparer.OrdinalIgnoreCase);
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (var r in inv.Relationships)
            {
                if (reachable.Contains(r.FromTable) && reachable.Add(r.ToTable)) grew = true;
                if (reachable.Contains(r.ToTable) && reachable.Add(r.FromTable)) grew = true;
            }
        }
        foreach (var r in inv.Relationships)
            if (reachable.Contains(r.FromTable) && reachable.Contains(r.ToTable))
            {
                MarkIndirect(Key(r.FromTable, r.FromColumn), $"relationship path {r.FromTable} -> {r.ToTable}");
                MarkIndirect(Key(r.ToTable, r.ToColumn), $"relationship path {r.FromTable} -> {r.ToTable}");
            }

        // 4) INDIRECT via sort-by of a direct/indirect column, then model-internal references
        foreach (var (colKey, sortKey) in inv.SortBy)
            if (direct.ContainsKey(colKey) || indirect.ContainsKey(colKey))
                MarkIndirect(sortKey, $"sort-by column of {Display(inv, colKey)}");
        foreach (var key in inv.ModelInternallyUsed)
            MarkIndirect(key, "model-internal reference (relationship / hierarchy / RLS / DAX)");

        // 5) everything else is UNUSED - the safe-to-remove shortlist
        var fields = new List<object>();
        var unusedList = new List<string>();
        foreach (var (key, disp) in inv.Columns.OrderBy(k => k.Value, StringComparer.OrdinalIgnoreCase)
                     .Concat(inv.Measures.OrderBy(k => k.Value, StringComparer.OrdinalIgnoreCase)))
        {
            bool isMeasure = inv.Measures.ContainsKey(key);
            if (direct.TryGetValue(key, out var us))
                fields.Add(new
                {
                    @object = disp, kind = isMeasure ? "measure" : "column", tier = "DIRECT",
                    reasons = us.Select(u => u.Context).Distinct().OrderBy(c => c, StringComparer.Ordinal).ToArray(),
                    visuals = us.Select(u => $"{u.Page}/{u.Visual}").Distinct().OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                });
            else if (indirect.TryGetValue(key, out var reasons))
                fields.Add(new { @object = disp, kind = isMeasure ? "measure" : "column", tier = "INDIRECT",
                    reasons = reasons.ToArray(), visuals = Array.Empty<string>() });
            else
            {
                fields.Add(new { @object = disp, kind = isMeasure ? "measure" : "column", tier = "UNUSED",
                    reasons = Array.Empty<string>(), visuals = Array.Empty<string>() });
                unusedList.Add(disp);
            }
        }

        return new
        {
            ok = true,
            source = new
            {
                kind = sourceKind,
                bindings = uses.Count,
                visualsTouched = uses.Select(u => $"{u.Page}/{u.Visual}").Distinct().Count(),
            },
            summary = new
            {
                direct = direct.Count,
                indirect = indirect.Count,
                unused = unusedList.Count,
                columns = inv.Columns.Count,
                measures = inv.Measures.Count,
            },
            fields,
            safeToRemove = unusedList,
            unresolvedBindings = unresolved
                .Select(u => new { field = $"{u.Table}[{u.Field}]", u.Page, u.Visual, u.Context })
                .Distinct().ToList(),
            note = "DIRECT = bound in the report (projection / filter / sort / tooltip / conditional-formatting / chrome bindings). INDIRECT = reached via a direct measure's DAX lineage, a relationship path, a sort-by column, or a model-internal reference. UNUSED = neither - the safe-to-remove shortlist. Unresolved bindings point at fields the model does not have - run scan_broken_refs.",
        };
    }

    private static string Display(ModelInventory inv, string key) =>
        inv.Measures.TryGetValue(key, out var m) ? m : inv.Columns.TryGetValue(key, out var c) ? c : key;

    // ---------------------------------------------------------------- impact analysis

    /// <summary>Blast radius of one model object: the transitive model dependants (measures whose
    /// DAX reaches it) plus every report visual touching the object or any dependant.</summary>
    public static object Impact(ModelInventory inv, IReadOnlyList<ReportFieldUse> uses, string objectName)
    {
        // resolve the target: Table[Field], [Measure], or a bare measure name
        string trimmed = (objectName ?? "").Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) trimmed = trimmed[1..^1].Trim();
        string? targetKey = null;
        if (trimmed.Contains('['))
        {
            targetKey = trimmed;
            if (!inv.Columns.ContainsKey(targetKey) && !inv.Measures.ContainsKey(targetKey)) targetKey = null;
        }
        else if (inv.MeasureHomeByName.TryGetValue(trimmed, out var home))
        {
            targetKey = Key(home, trimmed);
        }
        if (targetKey == null)
            throw new InvalidOperationException($"Object '{objectName}' not found - pass Table[Field] or a measure name.");

        bool targetIsMeasure = inv.Measures.ContainsKey(targetKey);
        string targetDisp = Display(inv, targetKey);

        // transitive dependants: measures whose DAX references the target (directly or via a chain)
        var dependants = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);  // key -> via
        var frontier = new Queue<string>();
        frontier.Enqueue(targetKey);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetKey };
        while (frontier.Count > 0)
        {
            string cur = frontier.Dequeue();
            foreach (var (mk, dax) in inv.MeasureDax)
            {
                if (seen.Contains(mk)) continue;
                var (refMeasures, refColumns) = DaxRefs(inv, dax);
                if (refMeasures.Contains(cur) || refColumns.Contains(cur))
                {
                    dependants[mk] = Display(inv, cur);
                    seen.Add(mk);
                    frontier.Enqueue(mk);
                }
            }
        }

        // report visuals touching the target or any dependant measure
        var hitKeys = new HashSet<string>(dependants.Keys, StringComparer.OrdinalIgnoreCase) { targetKey };
        var visuals = uses
            .Where(u => hitKeys.Contains(Key(u.Table, u.Field)))
            .Select(u => new { page = u.Page, visual = u.Visual, via = Display(inv, Key(u.Table, u.Field)), context = u.Context })
            .DistinctBy(v => $"{v.page}/{v.visual}/{v.via}/{v.context}")
            .OrderBy(v => $"{v.page}/{v.visual}", StringComparer.Ordinal)
            .ToList();

        return new
        {
            ok = true,
            @object = targetDisp,
            kind = targetIsMeasure ? "measure" : "column",
            modelDependants = dependants
                .Select(d => new { @object = Display(inv, d.Key), via = d.Value })
                .OrderBy(d => d.@object, StringComparer.OrdinalIgnoreCase).ToList(),
            reportVisuals = visuals,
            counts = new { modelDependants = dependants.Count, reportVisuals = visuals.Select(v => $"{v.page}/{v.visual}").Distinct().Count() },
            note = "Rename or delete this object and every listed dependant measure and report visual is in the blast radius.",
        };
    }

    // ---------------------------------------------------------------- broken refs

    /// <summary>Report bindings that point at model fields which do not exist (renamed / deleted
    /// tables, columns or measures), with repair suggestions: the same field on another table, or
    /// the closest-named field on the same table (edit distance &lt;= 3).</summary>
    public static object FindBrokenRefs(ModelInventory inv, IReadOnlyList<ReportFieldUse> uses, string sourceKind)
    {
        var broken = new List<object>();
        foreach (var group in uses.GroupBy(u => Key(u.Table, u.Field), StringComparer.OrdinalIgnoreCase))
        {
            string key = group.Key;
            if (inv.Columns.ContainsKey(key) || inv.Measures.ContainsKey(key)) continue;

            var sample = group.First();
            var suggestions = new List<string>();
            // same field name on a different table (a moved measure / relocated column)
            foreach (var (k, disp) in inv.Columns.Concat(inv.Measures))
            {
                string field = disp[(disp.IndexOf('[') + 1)..^1];
                if (field.Equals(sample.Field, StringComparison.OrdinalIgnoreCase)) suggestions.Add(disp);
            }
            // closest name on the SAME table (a renamed field)
            string prefix = sample.Table + "[";
            foreach (var (k, disp) in inv.Columns.Concat(inv.Measures))
            {
                if (!disp.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                string field = disp[(disp.IndexOf('[') + 1)..^1];
                if (EditDistance(field.ToLowerInvariant(), sample.Field.ToLowerInvariant()) <= 3
                    && !suggestions.Contains(disp)) suggestions.Add(disp);
            }
            bool tableExists = inv.Tables.Contains(sample.Table);
            broken.Add(new
            {
                field = $"{sample.Table}[{sample.Field}]",
                kind = sample.IsMeasure ? "measure" : "column",
                tableExists,
                bindings = group.Select(u => new { u.Page, u.Visual, u.Context })
                    .DistinctBy(b => $"{b.Page}/{b.Visual}/{b.Context}").ToList(),
                suggestions = suggestions.Take(5).ToList(),
            });
        }
        return new
        {
            ok = true,
            source = sourceKind,
            brokenCount = broken.Count,
            bindingsScanned = uses.Count,
            broken,
            note = broken.Count == 0
                ? "Every report binding resolves to a live model field."
                : "Repair with fix_broken_visuals(reportSessionId, repairMap) where repairMap = {\"Old Table[Old Field]\":\"New Table[New Field]\", ...} (legacy report sessions).",
        };
    }

    /// <summary>Levenshtein distance, small-string use only. Pure + testable.</summary>
    internal static int EditDistance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }

    // ---------------------------------------------------------------- PBIR uses collector

    /// <summary>Collect every model-field reference from a PBIR definition tree: queryState /
    /// prototypeQuery projections, filterConfig filters at report / page / visual scope, sorts,
    /// and the conditional-formatting / chrome bindings inside objects / visualContainerObjects
    /// (the bindings naive scanners miss - colour measures, icon rules, image URLs, dynamic
    /// titles). The same walker semantics as the legacy collector so both readers feed one core.</summary>
    public static List<ReportFieldUse> CollectPbirUses(PbirService.PbirModel model)
    {
        var uses = new List<ReportFieldUse>();

        // page displayName lookup for friendly labels
        var pageNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in model.Order)
            if (rel.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase) && model.Entries[rel].Json is JsonObject pj)
            {
                string name = (string?)pj["name"] ?? rel.Split('/')[^2];
                pageNames[name] = (string?)pj["displayName"] ?? name;
            }

        foreach (var rel in model.Order)
        {
            var json = model.Entries[rel].Json;
            if (json == null) continue;

            if (rel.Equals("report.json", StringComparison.OrdinalIgnoreCase))
            {
                CollectExprRefs(json["filterConfig"], null, "filter", "(report)", "(report)", uses);
            }
            else if (rel.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase))
            {
                string pname = (string?)json["name"] ?? rel.Split('/')[^2];
                string plabel = pageNames.GetValueOrDefault(pname, pname);
                CollectExprRefs(json["filterConfig"], null, "filter", plabel, "(page)", uses);
            }
            else if (rel.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase))
            {
                var parts = rel.Split('/');
                string pname = parts.Length >= 4 ? parts[2] : "(page)";
                string plabel = pageNames.GetValueOrDefault(pname, pname);
                string vname = (string?)json["name"] ?? (parts.Length >= 2 ? parts[^2] : "(visual)");

                var visual = json["visual"] as JsonObject;
                var query = visual?["query"] as JsonObject;
                CollectExprRefs(query?["queryState"], null, "projection", plabel, vname, uses);
                CollectExprRefs(query?["projections"], null, "projection", plabel, vname, uses);
                var pq = query?["prototypeQuery"] as JsonObject;
                CollectExprRefs(pq?["Select"], AliasMap(pq), "projection", plabel, vname, uses);
                CollectExprRefs(pq?["OrderBy"], AliasMap(pq), "sort", plabel, vname, uses);
                CollectExprRefs(query?["sortDefinition"], null, "sort", plabel, vname, uses);
                CollectExprRefs(visual?["objects"], null, "conditional-formatting", plabel, vname, uses);
                CollectExprRefs(visual?["visualContainerObjects"], null, "chrome-binding", plabel, vname, uses);
                CollectExprRefs(json["filterConfig"], null, "filter", plabel, vname, uses);
            }
        }
        return uses;
    }

    /// <summary>alias -> entity map from a semantic query's From clause (null-safe).</summary>
    internal static Dictionary<string, string>? AliasMap(JsonObject? query)
    {
        if (query?["From"] is not JsonArray from) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in from)
            if (f is JsonObject fo && (string?)fo["Name"] is { Length: > 0 } a && (string?)fo["Entity"] is { Length: > 0 } e)
                map[a] = e;
        return map;
    }

    /// <summary>
    /// Recursively find every {Column|Measure|HierarchyLevel|Aggregation-wrapped} field expression
    /// under a node: {"Expression":{"SourceRef":{"Entity"|"Source"}}, "Property"}. "Entity" is the
    /// table inline (PBIR / filters / conditional formatting); "Source" is a query alias resolved
    /// via the nearest From clause. A nested From (a sparkline's own scoped query, a filter's own
    /// expression) re-scopes aliases for its subtree.
    /// </summary>
    public static void CollectExprRefs(JsonNode? node, Dictionary<string, string>? aliases,
        string context, string page, string visual, List<ReportFieldUse> uses)
    {
        if (node == null) return;
        if (node is JsonObject obj)
        {
            // a node carrying its own From re-scopes aliases for everything beneath it
            var scope = obj["From"] is JsonArray ? (AliasMap(obj) ?? aliases) : aliases;

            bool isMeasure = obj["Measure"] is JsonObject;
            var expr = (obj["Measure"] ?? obj["Column"]) as JsonObject;
            if (expr is JsonObject eo
                && (string?)eo["Property"] is { Length: > 0 } field
                && eo["Expression"]?["SourceRef"] is JsonObject srcRef)
            {
                string? table = (string?)srcRef["Entity"];
                if (table == null && (string?)srcRef["Source"] is { Length: > 0 } alias && scope != null)
                    scope.TryGetValue(alias, out table);
                if (!string.IsNullOrWhiteSpace(table))
                    uses.Add(new ReportFieldUse(table!, field, isMeasure, context, page, visual));
            }
            foreach (var (_, child) in obj)
                CollectExprRefs(child, scope, context, page, visual, uses);
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr)
                CollectExprRefs(child, aliases, context, page, visual, uses);
        }
        else if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 1 && s[0] == '{'
                 && (s.Contains("\"Column\"") || s.Contains("\"Measure\"") || s.Contains("\"From\"")))
        {
            // legacy layout stores filters / config as embedded JSON strings - parse and recurse
            try { CollectExprRefs(JsonNode.Parse(s), aliases, context, page, visual, uses); }
            catch { /* not JSON after all - leave it */ }
        }
    }
}
