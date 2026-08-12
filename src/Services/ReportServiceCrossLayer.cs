using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Services;

/// <summary>
/// Wave G3 cross-layer + report-ergonomics surface on the LEGACY report path: the model-field
/// usage collector (feeding model_report_usage / impact_analysis / scan_broken_refs), the
/// broken-binding fixer, change_visual_type with role remapping, the bulk per-visual batch ops
/// and the visual-side field-parameter binder. The collector emits the same format-agnostic
/// <see cref="ReportFieldUse"/> rows as the PBIR collector so one pure core serves both readers.
/// </summary>
public sealed partial class ReportService
{
    // ================================================================ field-usage collector (legacy)

    /// <summary>Collect every model-field reference in the report: visual projections (tooltips
    /// tagged separately), sorts, visual / page / report filters, and the conditional-formatting +
    /// chrome bindings buried in objects / vcObjects (colour measures, icon rules, image URLs,
    /// dynamic titles - the bindings naive scanners miss).</summary>
    internal List<ReportFieldUse> CollectFieldUses(string reportSessionId)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var uses = new List<ReportFieldUse>();
        ParseFilterString((string?)root["filters"], "(report)", "(report)", uses);

        foreach (var section in Sections(root).OfType<JsonObject>())
        {
            string page = (string?)section["displayName"] ?? (string?)section["name"] ?? "(page)";
            ParseFilterString((string?)section["filters"], page, "(page)", uses);

            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc) continue;
                string vname = "(unnamed)";
                if ((string?)vc["config"] is string cfg && JsonNode.Parse(cfg) is JsonObject co)
                {
                    vname = (string?)co["name"] ?? vname;
                    if (co["singleVisual"] is JsonObject sv)
                    {
                        // projections: role-aware so tooltip-well fields are tagged as such
                        var roleByRef = ProjectionRoles(sv);
                        foreach (var b in SelectBindings(sv))
                        {
                            var roles = roleByRef.GetValueOrDefault(b.QueryRef) ?? new List<string> { "Values" };
                            foreach (var role in roles.Distinct())
                                uses.Add(new ReportFieldUse(b.Table, b.Field, b.Measure,
                                    role.Equals("Tooltips", StringComparison.OrdinalIgnoreCase) ? "tooltip" : "projection",
                                    page, vname));
                        }
                        var pq = sv["prototypeQuery"] as JsonObject;
                        CrossLayerAnalyzer.CollectExprRefs(pq?["OrderBy"], CrossLayerAnalyzer.AliasMap(pq), "sort", page, vname, uses);
                        CrossLayerAnalyzer.CollectExprRefs(sv["sortDefinition"], null, "sort", page, vname, uses);
                        CrossLayerAnalyzer.CollectExprRefs(sv["objects"], null, "conditional-formatting", page, vname, uses);
                        CrossLayerAnalyzer.CollectExprRefs(sv["vcObjects"], null, "chrome-binding", page, vname, uses);
                    }
                }
                ParseFilterString((string?)vc["filters"], page, vname, uses);
            }
        }
        return uses;
    }

    private static void ParseFilterString(string? filters, string page, string visual, List<ReportFieldUse> uses)
    {
        if (string.IsNullOrWhiteSpace(filters)) return;
        try { CrossLayerAnalyzer.CollectExprRefs(JsonNode.Parse(filters), null, "filter", page, visual, uses); }
        catch { /* a malformed filters string never breaks the scan */ }
    }

    /// <summary>queryRef -> the projection roles carrying it (a field may sit in several wells).</summary>
    internal static Dictionary<string, List<string>> ProjectionRoles(JsonObject sv)
    {
        var map = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (sv["projections"] is not JsonObject projections) return map;
        foreach (var (role, entries) in projections)
            if (entries is JsonArray arr)
                foreach (var e in arr)
                    if (e is JsonObject eo && (string?)eo["queryRef"] is { Length: > 0 } qr)
                    {
                        if (!map.TryGetValue(qr, out var roles)) map[qr] = roles = new List<string>();
                        roles.Add(role);
                    }
        return map;
    }

    /// <summary>The prototypeQuery Select entries resolved to (queryRef, table, field, isMeasure) -
    /// the same resolution rules as <see cref="VisualFieldRefs"/> but keeping the queryRef.</summary>
    internal static List<(string QueryRef, string Table, string Field, bool Measure)> SelectBindings(JsonObject sv)
    {
        var result = new List<(string, string, string, bool)>();
        if (sv["prototypeQuery"] is not JsonObject pq) return result;
        var aliases = CrossLayerAnalyzer.AliasMap(pq) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pq["Select"] is not JsonArray select) return result;
        foreach (var node in select)
        {
            if (node is not JsonObject sel) continue;
            bool isMeasure = sel["Measure"] is JsonObject;
            var expr = (sel["Measure"] ?? sel["Column"]) as JsonObject;
            string? field = (string?)expr?["Property"];
            string? alias = (string?)expr?["Expression"]?["SourceRef"]?["Source"];
            string? table = alias != null && aliases.TryGetValue(alias, out var ent) ? ent
                : (string?)expr?["Expression"]?["SourceRef"]?["Entity"];
            string? name = (string?)sel["Name"];
            if ((table == null || field == null) && name is { Length: > 0 } && name.Contains('.'))
            {
                int dot = name.IndexOf('.');
                table ??= name[..dot];
                field ??= name[(dot + 1)..];
            }
            if (!string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(field))
                result.Add((name ?? $"{table}.{field}", table!, field!, isMeasure));
        }
        return result;
    }

    // ================================================================ broken-binding fixer

    private sealed class RepairMap
    {
        private readonly Dictionary<string, (string Table, string Field)> _map = new(StringComparer.OrdinalIgnoreCase);
        // Wave G4: entity-level renames (a table rename carries EVERY field of that table). A field
        // miss falls back here - the table part is re-pointed and the field name is kept.
        private readonly Dictionary<string, string> _entities = new(StringComparer.OrdinalIgnoreCase);
        public IReadOnlyCollection<string> Keys => _map.Keys.Concat(_entities.Keys).ToList();
        public readonly HashSet<string> Hit = new(StringComparer.OrdinalIgnoreCase);

        public static RepairMap Parse(string repairMapJson)
        {
            var obj = JsonNode.Parse(repairMapJson) as JsonObject
                ?? throw new ArgumentException("repairMap must be a JSON object {\"Old Table[Old Field]\":\"New Table[New Field]\", ...}.");
            var rm = new RepairMap();
            foreach (var (k, v) in obj)
            {
                var (ot, of) = SplitFieldRef(k);
                var (nt, nf) = SplitFieldRef((string?)v ?? "");
                rm._map[CrossLayerAnalyzer.Key(ot, of)] = (nt, nf);
            }
            if (rm._map.Count == 0) throw new ArgumentException("repairMap is empty.");
            return rm;
        }

        /// <summary>Wave G4 factory for the rename propagators: bracketed field pairs plus an
        /// entity (table-rename) map. Either side may be empty, never both.</summary>
        public static RepairMap Build(IReadOnlyDictionary<string, string> fieldMap,
            IReadOnlyDictionary<string, string>? entityMap)
        {
            var rm = new RepairMap();
            foreach (var (k, v) in fieldMap)
            {
                var (ot, of) = SplitFieldRef(k);
                var (nt, nf) = SplitFieldRef(v);
                rm._map[CrossLayerAnalyzer.Key(ot, of)] = (nt, nf);
            }
            if (entityMap != null)
                foreach (var (k, v) in entityMap) rm._entities[k.Trim()] = v.Trim();
            if (rm._map.Count == 0 && rm._entities.Count == 0)
                throw new ArgumentException("rename maps are empty - nothing to rewrite.");
            return rm;
        }

        public bool TryRepair(string table, string field, out string newTable, out string newField)
        {
            newTable = table; newField = field;
            if (_map.TryGetValue(CrossLayerAnalyzer.Key(table, field), out var t))
            {
                newTable = t.Table; newField = t.Field;
                Hit.Add(CrossLayerAnalyzer.Key(table, field));
                return true;
            }
            if (_entities.TryGetValue(table, out var nt2))
            {
                newTable = nt2;             // field name carries over on a table rename
                Hit.Add(table);
                return true;
            }
            return false;
        }

        /// <summary>Entity-only repair: a bare table reference (a From entry, a table-scoped
        /// SourceRef with no Property) follows a table rename.</summary>
        public bool TryRepairEntity(string table, out string newTable)
        {
            if (_entities.TryGetValue(table, out newTable!))
            {
                Hit.Add(table);
                return true;
            }
            newTable = table;
            return false;
        }
    }

    internal static (string table, string field) SplitFieldRef(string fieldRef)
    {
        string s = (fieldRef ?? "").Trim();
        int lb = s.IndexOf('[');
        int rb = s.LastIndexOf(']');
        if (lb <= 0 || rb <= lb) throw new ArgumentException($"'{fieldRef}' must be in the form Table[Field].");
        return (s[..lb].Trim().Trim('\''), s[(lb + 1)..rb].Trim());
    }

    /// <summary>
    /// fix_broken_visuals: rewrite visual bindings that point at renamed / moved model fields.
    /// repairMap = {"Old Table[Old Field]":"New Table[New Field]", ...}. Rewrites the same binding
    /// paths set_visual_fields writes - prototypeQuery From/Select (aliases repointed when the
    /// table changed), projections queryRefs, sorts, visual-level filters, and the
    /// conditional-formatting / chrome bindings in objects / vcObjects - plus page and report
    /// filters. Bindings not named in the map are untouched.
    /// </summary>
    public object FixBrokenVisuals(string reportSessionId, string repairMapJson)
    {
        var session = _sessions.GetReport(reportSessionId);
        var rep = RepairMap.Parse(repairMapJson);
        return FixBrokenVisualsCore(session, rep);
    }

    /// <summary>The rewrite engine behind fix_broken_visuals AND the Wave G4 rename propagation -
    /// the map source differs (user JSON vs a validated rename batch), the walking is identical.</summary>
    private static object FixBrokenVisualsCore(ReportSession session, RepairMap rep)
    {
        var root = session.Layout.Root;
        var details = new List<object>();

        // report-level filters
        var reportApplied = RewriteFilterString(root, "filters", rep);
        if (reportApplied.Count > 0)
            details.Add(new { page = "(report)", visual = "(report filters)", applied = reportApplied });

        foreach (var section in Sections(root).OfType<JsonObject>())
        {
            string page = (string?)section["displayName"] ?? (string?)section["name"] ?? "(page)";
            var pageApplied = RewriteFilterString(section, "filters", rep);
            if (pageApplied.Count > 0)
                details.Add(new { page, visual = "(page filters)", applied = pageApplied });

            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc) continue;
                var applied = new List<string>();

                if ((string?)vc["config"] is string cfg && JsonNode.Parse(cfg) is JsonObject co)
                {
                    string vname = (string?)co["name"] ?? "(unnamed)";
                    if (co["singleVisual"] is JsonObject sv)
                    {
                        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        if (sv["prototypeQuery"] is JsonObject pq)
                        {
                            RewriteScopedQuery(pq, rep, applied, renames);
                            RefreshSelectNames(pq, renames);
                        }
                        // projections carry the "Table.Field" queryRefs the Select renamed
                        if (sv["projections"] is JsonObject projections && renames.Count > 0)
                            foreach (var (_, entries) in projections)
                                if (entries is JsonArray arr)
                                    foreach (var e in arr)
                                        if (e is JsonObject eo && (string?)eo["queryRef"] is { } qr
                                            && renames.TryGetValue(qr, out var nqr))
                                            eo["queryRef"] = nqr;

                        RewriteInline(sv["sortDefinition"], rep, applied, renames);
                        RewriteInline(sv["objects"], rep, applied, renames);
                        RewriteInline(sv["vcObjects"], rep, applied, renames);
                    }
                    if (applied.Count > 0) vc["config"] = co.ToJsonString(JsonOpts);

                    var filterApplied = RewriteFilterString(vc, "filters", rep);
                    applied.AddRange(filterApplied);
                    if (applied.Count > 0)
                        details.Add(new { page, visual = vname, applied = applied.Distinct().ToList() });
                }
            }
        }

        if (details.Count > 0) session.Dirty = true;
        var unmatched = rep.Keys.Where(k => !rep.Hit.Contains(k)).ToList();
        return new
        {
            ok = true,
            repairsRequested = rep.Keys.Count,
            targetsChanged = details.Count,
            details,
            unmatchedRepairs = unmatched,
            note = unmatched.Count > 0
                ? "unmatchedRepairs were never found in any binding - check the spelling with scan_broken_refs."
                : "Every repair key was applied at least once.",
        };
    }

    /// <summary>Rewrite an embedded-JSON filters string property in place; returns what changed.</summary>
    private static List<string> RewriteFilterString(JsonObject owner, string prop, RepairMap rep)
    {
        var applied = new List<string>();
        if ((string?)owner[prop] is not { Length: > 0 } text) return applied;
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(text); } catch { return applied; }
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        RewriteInline(parsed, rep, applied, renames);
        if (applied.Count > 0) owner[prop] = parsed!.ToJsonString(JsonOpts);
        return applied;
    }

    /// <summary>Rewrite field refs under any node: a subtree owning its own From is handled as a
    /// scoped semantic query (aliases repointed); bare {Expression.SourceRef.Entity, Property}
    /// nodes (filters' expression slot, conditional-formatting exprs) are rewritten inline.</summary>
    private static void RewriteInline(JsonNode? node, RepairMap rep, List<string> applied, Dictionary<string, string> renames)
    {
        if (node is JsonObject obj)
        {
            if (obj["From"] is JsonArray && (obj["Where"] is not null || obj["Select"] is not null || obj["OrderBy"] is not null))
            {
                RewriteScopedQuery(obj, rep, applied, renames);
                return;
            }
            if ((string?)obj["Property"] is { Length: > 0 } field
                && obj["Expression"]?["SourceRef"] is JsonObject srcRef
                && (string?)srcRef["Entity"] is { Length: > 0 } entity
                && rep.TryRepair(entity, field, out var nt, out var nf))
            {
                srcRef["Entity"] = nt;
                obj["Property"] = nf;
                Record(applied, renames, entity, field, nt, nf);
            }
            // a bare table reference (a SourceRef with no Property beside it - hierarchy exprs,
            // table-scoped filters, drillthrough targets) follows a table rename
            if ((string?)obj["Entity"] is { Length: > 0 } soloEntity && obj["Property"] is null
                && rep.TryRepairEntity(soloEntity, out var soloNew))
            {
                obj["Entity"] = soloNew;
                applied.Add($"{soloEntity} -> {soloNew} (table)");
            }
            foreach (var (_, child) in obj) RewriteInline(child, rep, applied, renames);
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr) RewriteInline(child, rep, applied, renames);
        }
    }

    /// <summary>Rewrite the alias-scoped refs of ONE semantic query (a prototypeQuery or a filter's
    /// From/Where block): resolve each {SourceRef.Source, Property} through the From aliases, apply
    /// the repair, and repoint the alias - renaming the From Entity when the alias is wholly
    /// repaired to one table, otherwise adding a fresh alias so unrepaired refs stay intact.</summary>
    private static void RewriteScopedQuery(JsonObject query, RepairMap rep, List<string> applied, Dictionary<string, string> renames)
    {
        if (query["From"] is not JsonArray from) return;
        var aliasPre = CrossLayerAnalyzer.AliasMap(query) ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // pass 1: collect every alias-scoped ref (recursing separately into nested scoped queries)
        var refs = new List<(JsonObject Expr, string Alias, string Entity, string Field)>();
        void Walk(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (!ReferenceEquals(o, query) && o["From"] is JsonArray
                    && (o["Where"] is not null || o["Select"] is not null || o["OrderBy"] is not null))
                {
                    RewriteScopedQuery(o, rep, applied, renames);
                    return;
                }
                if ((string?)o["Property"] is { Length: > 0 } f && o["Expression"]?["SourceRef"] is JsonObject sr)
                {
                    if ((string?)sr["Source"] is { Length: > 0 } a && aliasPre.TryGetValue(a, out var ent))
                        refs.Add((o, a, ent, f));
                    else if ((string?)sr["Entity"] is { Length: > 0 } ent2 && rep.TryRepair(ent2, f, out var nt2, out var nf2))
                    {
                        sr["Entity"] = nt2; o["Property"] = nf2;
                        Record(applied, renames, ent2, f, nt2, nf2);
                    }
                }
                foreach (var (_, child) in o) Walk(child);
            }
            else if (n is JsonArray arr)
            {
                foreach (var child in arr) Walk(child);
            }
        }
        foreach (var (_, child) in query) Walk(child);

        // pass 2: decide per alias whether its From Entity can simply be renamed
        foreach (var aliasGroup in refs.GroupBy(r => r.Alias, StringComparer.OrdinalIgnoreCase))
        {
            var repaired = new List<(JsonObject Expr, string Entity, string Field, string NewTable, string NewField)>();
            bool hasUnrepaired = false;
            foreach (var r in aliasGroup)
            {
                if (rep.TryRepair(r.Entity, r.Field, out var nt, out var nf))
                    repaired.Add((r.Expr, r.Entity, r.Field, nt, nf));
                else hasUnrepaired = true;
            }
            if (repaired.Count == 0) continue;

            var targets = repaired.Select(r => r.NewTable).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            string oldEntity = aliasGroup.First().Entity;
            bool sameTable = targets.Count == 1 && targets[0].Equals(oldEntity, StringComparison.OrdinalIgnoreCase);
            bool renameWholeAlias = !sameTable && targets.Count == 1 && !hasUnrepaired;

            if (renameWholeAlias)
            {
                foreach (var f in from)
                    if (f is JsonObject fo && (string?)fo["Name"] == aliasGroup.Key) fo["Entity"] = targets[0];
            }
            foreach (var r in repaired)
            {
                if (!sameTable && !renameWholeAlias)
                {
                    string alias = AliasFor(from, r.NewTable);
                    ((JsonObject)r.Expr["Expression"]!["SourceRef"]!)["Source"] = alias;
                }
                r.Expr["Property"] = r.NewField;
                Record(applied, renames, r.Entity, r.Field, r.NewTable, r.NewField);
            }
        }

        // a From entry no field ref touched (or an empty query) still follows a table rename
        foreach (var f in from)
            if (f is JsonObject fo && (string?)fo["Entity"] is { Length: > 0 } fromEntity
                && rep.TryRepairEntity(fromEntity, out var fromNew))
            {
                fo["Entity"] = fromNew;
                applied.Add($"{fromEntity} -> {fromNew} (table)");
            }
    }

    /// <summary>The From alias for a table, adding a fresh entry when the table is not yet joined.</summary>
    private static string AliasFor(JsonArray from, string table)
    {
        foreach (var f in from)
            if (f is JsonObject fo && string.Equals((string?)fo["Entity"], table, StringComparison.OrdinalIgnoreCase)
                && (string?)fo["Name"] is { Length: > 0 } a)
                return a;
        var used = from.OfType<JsonObject>().Select(fo => (string?)fo["Name"]).Where(n => n != null).ToHashSet();
        int n = from.Count + 1;
        string alias;
        do { alias = "t" + n++; } while (used.Contains(alias));
        from.Add(new JsonObject { ["Name"] = alias, ["Entity"] = table, ["Type"] = 0 });
        return alias;
    }

    private static void Record(List<string> applied, Dictionary<string, string> renames,
        string oldTable, string oldField, string newTable, string newField)
    {
        applied.Add($"{oldTable}[{oldField}] -> {newTable}[{newField}]");
        renames[$"{oldTable}.{oldField}"] = $"{newTable}.{newField}";
    }

    /// <summary>Recompute each Select entry's "Table.Field" Name from its (possibly rewritten)
    /// expression, so the projections queryRefs and the query stay in step.</summary>
    private static void RefreshSelectNames(JsonObject pq, Dictionary<string, string> renames)
    {
        if (renames.Count == 0 || pq["Select"] is not JsonArray select) return;
        foreach (var node in select)
            if (node is JsonObject sel && (string?)sel["Name"] is { } name && renames.TryGetValue(name, out var nn))
                sel["Name"] = nn;
    }

    // ================================================================ change_visual_type

    /// <summary>
    /// change_visual_type: swap a visual to a new type PRESERVING its data bindings, position and
    /// applicable formatting. A deprecated target (card / table / matrix) is modernised via the
    /// catalogue mapping. Projection roles are remapped through the curated data-role registry:
    /// same-named roles carry straight over, the rest fall to the first compatible role by kind
    /// (measure vs grouping), and per-role caps drop overflow (reported, never silent). Formatting
    /// cards the new type does not declare in the property registry are dropped and reported;
    /// chrome (vcObjects) always survives.
    /// </summary>
    public object ChangeVisualType(string reportSessionId, string page, string visual, string newType, PropertyCatalog catalog)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        string oldType = (string?)sv["visualType"] ?? "(unknown)";
        string target = VisualDataRoles.Modernize(newType);
        bool modernised = !target.Equals(newType.Trim(), StringComparison.Ordinal);
        if (target.Equals(oldType, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Visual '{visual}' is already a {oldType}.");

        var targetRoles = VisualDataRoles.RolesFor(target);
        var roleByRef = ProjectionRoles(sv);
        var selects = SelectBindings(sv);
        var remapped = new List<object>();
        var dropped = new List<string>();

        if (targetRoles != null && selects.Count > 0)
        {
            // flatten current bindings preserving projection order and the active (field-parameter) flag
            var bindings = new List<(string Role, string Table, string Field, bool Measure, bool Active)>();
            if (sv["projections"] is JsonObject projections)
            {
                var byRef = selects.ToDictionary(s => s.QueryRef, s => s, StringComparer.OrdinalIgnoreCase);
                foreach (var (role, entries) in projections)
                    if (entries is JsonArray arr)
                        foreach (var e in arr)
                            if (e is JsonObject eo && (string?)eo["queryRef"] is { } qr && byRef.TryGetValue(qr, out var s))
                                bindings.Add((role, s.Table, s.Field, s.Measure, (bool?)eo["active"] == true));
            }
            if (bindings.Count == 0)
                bindings = selects.Select(s => ("Values", s.Table, s.Field, s.Measure, false)).ToList();

            // remap each binding onto the target role set
            var perRole = new Dictionary<string, List<(string Table, string Field, bool Measure, bool Active)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var b in bindings)
            {
                var exact = targetRoles.FirstOrDefault(r => r.Name.Equals(b.Role, StringComparison.OrdinalIgnoreCase));
                var chosen = exact ?? (b.Measure
                    ? targetRoles.FirstOrDefault(r => r.AcceptsMeasure)
                    : targetRoles.FirstOrDefault(r => r.AcceptsGrouping));
                if (chosen == null) { dropped.Add($"{b.Table}[{b.Field}] (no {(b.Measure ? "measure" : "grouping")} role on {target})"); continue; }
                if (!perRole.TryGetValue(chosen.Name, out var list)) perRole[chosen.Name] = list = new List<(string, string, bool, bool)>();
                if (chosen.Max is int cap && list.Count >= cap)
                { dropped.Add($"{b.Table}[{b.Field}] (role {chosen.Name} capped at {cap})"); continue; }
                list.Add((b.Table, b.Field, b.Measure, b.Active));
                remapped.Add(new { field = $"{b.Table}[{b.Field}]", from = b.Role, to = chosen.Name });
            }

            // rebuild the query with the same shapes set_visual_fields writes, active flags kept
            var flat = perRole.SelectMany(kv => kv.Value.Select(v => (Role: kv.Key, v.Table, v.Field, v.Measure, v.Active))).ToList();
            var tables = flat.Select(b => b.Table).Distinct().ToList();
            var alias = tables.Select((t, i) => (t, a: "t" + (i + 1))).ToDictionary(p => p.t, p => p.a);
            var from = new JsonArray();
            foreach (var t in tables) from.Add(new JsonObject { ["Name"] = alias[t], ["Entity"] = t, ["Type"] = 0 });
            var select = new JsonArray();
            foreach (var b in flat)
            {
                var expr = new JsonObject { ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = alias[b.Table] } }, ["Property"] = b.Field };
                var sel = new JsonObject();
                sel[b.Measure ? "Measure" : "Column"] = expr;
                sel["Name"] = $"{b.Table}.{b.Field}";
                select.Add(sel);
            }
            var newProjections = new JsonObject();
            foreach (var grp in flat.GroupBy(b => b.Role))
            {
                var arr = new JsonArray();
                foreach (var b in grp)
                {
                    var entry = new JsonObject { ["queryRef"] = $"{b.Table}.{b.Field}" };
                    if (b.Active) entry["active"] = true;
                    arr.Add(entry);
                }
                newProjections[grp.Key] = arr;
            }
            var pq = (sv["prototypeQuery"] as JsonObject) ?? new JsonObject { ["Version"] = 2 };
            pq["From"] = from;
            pq["Select"] = select;
            pq.Remove("OrderBy");                              // the sort may reference a dropped field
            sv["prototypeQuery"] = pq;
            sv["projections"] = newProjections;
        }

        // formatting: drop data cards the new type does not declare (chrome always survives)
        var droppedCards = new List<string>();
        if (catalog.Knows(target) && sv["objects"] is JsonObject objects)
        {
            var known = catalog.Get(target)!.Cards;
            foreach (var cardName in objects.Select(kv => kv.Key).ToList())
                if (!known.ContainsKey(cardName))
                {
                    objects.Remove(cardName);
                    droppedCards.Add(cardName);
                }
        }

        sv["visualType"] = target;
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new
        {
            ok = true,
            page,
            visual = (string?)co["name"],
            from = oldType,
            to = target,
            modernised,
            roleCoverage = targetRoles != null ? "curated" : "none",
            remapped,
            droppedBindings = dropped,
            droppedFormatCards = droppedCards,
            note = targetRoles == null
                ? "No curated role metadata for the target type - projections carried over unchanged; verify in Desktop."
                : "Bindings remapped through the curated role registry; position and chrome formatting preserved. Sort cleared (re-apply with set_visual_sort if wanted).",
        };
    }

    // ================================================================ bulk ops (per-item isolation)

    private static readonly JsonSerializerOptions CiOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Run one batch: parse items, apply op per item, one result row per target - a
    /// failing item never aborts the rest (the anti all-or-nothing contract).</summary>
    private static object RunBatch<T>(string itemsJson, Func<T, object> op, Func<T, string, string> label)
    {
        var items = JsonSerializer.Deserialize<List<T>>(itemsJson, CiOpts)
            ?? throw new ArgumentException("items must be a JSON array.");
        if (items.Count == 0) throw new ArgumentException("items is empty.");
        var results = new List<object>();
        int okCount = 0;
        foreach (var item in items)
        {
            try
            {
                var r = op(item);
                okCount++;
                results.Add(new { ok = true, target = label(item, ""), result = r });
            }
            catch (Exception ex)
            {
                results.Add(new { ok = false, target = label(item, ""), error = ex.Message });
            }
        }
        return new { ok = okCount == items.Count, items = items.Count, succeeded = okCount, failed = items.Count - okCount, results };
    }

    internal sealed record BulkBindItem(string page, string visual, List<BulkBindField>? bindings);
    internal sealed record BulkBindField(string? role, string table, string field, string? kind);
    internal sealed record BulkFormatItem(string page, string visual, JsonNode? format);
    internal sealed record BulkDeleteItem(string page, string visual);

    /// <summary>bulk_bind_visuals: set_visual_fields over many visuals in one call.</summary>
    public object BulkBindVisuals(string reportSessionId, string itemsJson)
        => RunBatch<BulkBindItem>(itemsJson, item =>
        {
            if (item.bindings == null || item.bindings.Count == 0)
                throw new ArgumentException("bindings is required per item: [{role,table,field,kind}, ...].");
            var binds = item.bindings.Select(b => new FieldBinding(
                string.IsNullOrWhiteSpace(b.role) ? "Values" : b.role!, b.table, b.field,
                string.IsNullOrWhiteSpace(b.kind) ? "column" : b.kind!)).ToList();
            return SetVisualFields(reportSessionId, item.page, item.visual, binds);
        }, (item, _) => $"{item.page}/{item.visual}");

    /// <summary>bulk_set_visual_format: set_visual_format over many visuals in one call.</summary>
    public object BulkSetVisualFormat(string reportSessionId, string itemsJson)
        => RunBatch<BulkFormatItem>(itemsJson, item =>
        {
            if (item.format is null) throw new ArgumentException("format is required per item (the set_visual_format formatJson object).");
            return SetVisualFormat(reportSessionId, item.page, item.visual, item.format.ToJsonString(JsonOpts));
        }, (item, _) => $"{item.page}/{item.visual}");

    /// <summary>bulk_delete_visuals: delete_visual over many visuals in one call.</summary>
    public object BulkDeleteVisuals(string reportSessionId, string itemsJson)
        => RunBatch<BulkDeleteItem>(itemsJson, item => DeleteVisual(reportSessionId, item.page, item.visual),
            (item, _) => $"{item.page}/{item.visual}");

    // ================================================================ bind_field_parameter (visual side)

    /// <summary>
    /// bind_field_parameter: author the VISUAL-side query state that makes a field parameter swap
    /// fields in Desktop. The model side (add_field_parameter) already carries four of the five
    /// pieces - the calculated NAMEOF table, the ParameterMetadata extended property, the display
    /// column's SortByColumn and its GroupByColumns binding. The fifth piece is here: the visual's
    /// projection for the chosen role becomes the parameter COLUMN with active=true (the dynamic
    /// projection marker), and the prototypeQuery Select swaps to the parameter column. Desktop
    /// expands the active projection to the user-selected fields at render time.
    /// </summary>
    public object BindFieldParameter(string reportSessionId, string page, string visual,
        string parameterTable, string? parameterColumn = null, string? role = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        string pcol = string.IsNullOrWhiteSpace(parameterColumn) ? parameterTable : parameterColumn!;
        string paramRef = $"{parameterTable}.{pcol}";

        var projections = (sv["projections"] as JsonObject) ?? new JsonObject();
        // pick the role: explicit, else the visual's measure well (Y), else Values, else its first role
        string targetRole = !string.IsNullOrWhiteSpace(role) ? role!.Trim()
            : projections.ContainsKey("Y") ? "Y"
            : projections.ContainsKey("Values") ? "Values"
            : projections.Select(kv => kv.Key).FirstOrDefault() ?? "Values";

        // what the parameter replaces in that role (kept for the report + Select pruning)
        var replacedRefs = new List<string>();
        if (projections[targetRole] is JsonArray oldArr)
            foreach (var e in oldArr)
                if (e is JsonObject eo && (string?)eo["queryRef"] is { Length: > 0 } qr) replacedRefs.Add(qr);

        projections[targetRole] = new JsonArray { new JsonObject { ["queryRef"] = paramRef, ["active"] = true } };
        sv["projections"] = projections;

        // refs still projected by OTHER roles must keep their Select entries
        var stillUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (r, entries) in projections)
            if (entries is JsonArray arr)
                foreach (var e in arr)
                    if (e is JsonObject eo && (string?)eo["queryRef"] is { Length: > 0 } qr) stillUsed.Add(qr);

        var pq = (sv["prototypeQuery"] as JsonObject) ?? new JsonObject { ["Version"] = 2 };
        var from = pq["From"] as JsonArray ?? (JsonArray)(pq["From"] = new JsonArray());
        var select = pq["Select"] as JsonArray ?? (JsonArray)(pq["Select"] = new JsonArray());

        for (int i = select.Count - 1; i >= 0; i--)
            if (select[i] is JsonObject sel && (string?)sel["Name"] is { } nm
                && replacedRefs.Contains(nm, StringComparer.OrdinalIgnoreCase) && !stillUsed.Contains(nm))
                select.RemoveAt(i);

        string alias = AliasFor(from, parameterTable);
        bool present = select.OfType<JsonObject>().Any(s => string.Equals((string?)s["Name"], paramRef, StringComparison.OrdinalIgnoreCase));
        if (!present)
            select.Add(new JsonObject
            {
                ["Column"] = new JsonObject
                {
                    ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = alias } },
                    ["Property"] = pcol,
                },
                ["Name"] = paramRef,
            });

        // prune From aliases no Select entry references any more
        var usedAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in select.OfType<JsonObject>())
            if ((string?)((s["Measure"] ?? s["Column"]) as JsonObject)?["Expression"]?["SourceRef"]?["Source"] is { Length: > 0 } a)
                usedAliases.Add(a);
        for (int i = from.Count - 1; i >= 0; i--)
            if (from[i] is JsonObject fo && (string?)fo["Name"] is { Length: > 0 } a && !usedAliases.Contains(a))
                from.RemoveAt(i);

        pq.Remove("OrderBy");                                  // a sort on a replaced field would dangle
        sv["prototypeQuery"] = pq;
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new
        {
            ok = true,
            page,
            visual = (string?)co["name"],
            parameter = $"{parameterTable}[{pcol}]",
            role = targetRole,
            replaced = replacedRefs,
            note = "Visual-side piece written: the role's projection is now the parameter column with active=true. The model-side pieces (NAMEOF calculated table, ParameterMetadata, SortByColumn, GroupByColumns) come from add_field_parameter - run it first if the table does not exist yet. Open in Desktop to verify the swap.",
        };
    }
}
