using System.Text.Json.Nodes;

namespace SuperBiMcp.Services;

/// <summary>
/// Wave G4 report-side rename propagation. Both report formats route through the SAME binding
/// rewriter the broken-binding fixer already uses (<see cref="RepairMap"/> + RewriteInline /
/// RewriteScopedQuery): the legacy path reuses fix_broken_visuals' core over the Layout tree, and
/// the PBIR path walks every parsed definition file (visual.json query state, prototypeQuery,
/// filterConfig at report/page/visual scope, conditional-formatting / chrome objects, bookmarks)
/// plus the queryRef strings that must stay in step with the rewritten expressions.
/// </summary>
public sealed partial class ReportService
{
    /// <summary>The registered session object for a legacy report session id (rename-target seam).</summary>
    internal ReportSession SessionOf(string reportSessionId) => _sessions.GetReport(reportSessionId);

    /// <summary>Legacy Layout propagation: fieldMap "Old Table[Old Field]" -> "New Table[New Field]"
    /// plus entityMap "Old Table" -> "New Table" (a table rename carries EVERY field of that table,
    /// named in the map or not). Same rewrite surface as fix_broken_visuals.</summary>
    internal object PropagateRenamesLegacy(ReportSession session,
        IReadOnlyDictionary<string, string> fieldMap, IReadOnlyDictionary<string, string> entityMap)
        => FixBrokenVisualsCore(session, RepairMap.Build(fieldMap, entityMap));

    /// <summary>PBIR propagation over a parsed definition tree: every JSON file is walked with the
    /// shared binding rewriter, then queryRef / nativeQueryRef / Select-Name strings are re-pointed.
    /// Touched entries are marked Dirty so save_pbir (or the path target's flush) re-emits exactly
    /// those files.</summary>
    internal static object PropagateRenamesPbir(PbirService.PbirModel model,
        IReadOnlyDictionary<string, string> fieldMap, IReadOnlyDictionary<string, string> entityMap)
    {
        var rep = RepairMap.Build(fieldMap, entityMap);

        // "Table.Field" map for the queryRef strings, derived from the bracketed field map
        var dotMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in fieldMap)
        {
            var (ot, of) = SplitFieldRef(k);
            var (nt, nf) = SplitFieldRef(v);
            dotMap[$"{ot}.{of}"] = $"{nt}.{nf}";
        }

        var files = new List<object>();
        foreach (var rel in model.Order)
        {
            var entry = model.Entries[rel];
            if (entry.Json is not JsonObject json) continue;
            if (rel.EndsWith(".pbir", StringComparison.OrdinalIgnoreCase)) continue;   // dataset pointer - no bindings

            var applied = new List<string>();
            var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            RewriteInline(json, rep, applied, renames);

            // per-file merged queryRef map: the global field map plus whatever this file's rewrites
            // recorded (entity-fallback repairs produce "oldT.F" -> "newT.F" pairs not in the field map)
            var merged = new Dictionary<string, string>(dotMap, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in renames) merged[k] = v;
            int queryRefs = RewriteQueryRefStrings(json, merged, entityMap);

            if (applied.Count > 0 || queryRefs > 0)
            {
                entry.Dirty = true;
                files.Add(new { file = rel, applied = applied.Distinct().ToList(), queryRefsRewritten = queryRefs });
            }
        }
        return new
        {
            ok = true,
            format = "pbir",
            filesChanged = files.Count,
            files,
            note = files.Count == 0
                ? "No PBIR binding referenced a renamed object."
                : "Rewrote projections, prototype queries, filters, sorts, conditional-formatting/chrome bindings and queryRefs in the listed files.",
        };
    }

    /// <summary>Walk any JSON subtree and re-point the queryRef-style strings: queryRef,
    /// nativeQueryRef, and a Select entry's Name (an object carrying Measure/Column). These carry
    /// "Table.Field" text that must match the rewritten expressions beside them.</summary>
    internal static int RewriteQueryRefStrings(JsonNode? node,
        IReadOnlyDictionary<string, string> dotMap, IReadOnlyDictionary<string, string> entityMap)
    {
        int count = 0;
        if (node is JsonObject obj)
        {
            foreach (var key in QueryRefKeys)
                if ((string?)obj[key] is { Length: > 0 } qr)
                {
                    string rewritten = RewriteQueryRef(qr, dotMap, entityMap);
                    if (!rewritten.Equals(qr, StringComparison.Ordinal)) { obj[key] = rewritten; count++; }
                }
            if ((obj["Measure"] ?? obj["Column"]) is JsonObject && (string?)obj["Name"] is { Length: > 0 } name)
            {
                string rewritten = RewriteQueryRef(name, dotMap, entityMap);
                if (!rewritten.Equals(name, StringComparison.Ordinal)) { obj["Name"] = rewritten; count++; }
            }
            foreach (var (_, child) in obj) count += RewriteQueryRefStrings(child, dotMap, entityMap);
        }
        else if (node is JsonArray arr)
        {
            foreach (var child in arr) count += RewriteQueryRefStrings(child, dotMap, entityMap);
        }
        return count;
    }

    private static readonly string[] QueryRefKeys = { "queryRef", "nativeQueryRef" };

    /// <summary>Rewrite one "Table.Field" queryRef: exact match first, then an aggregate wrapper
    /// (Sum(Table.Field) - the inner ref is rewritten recursively), then a bare table-prefix match
    /// for a table rename whose field kept its name.</summary>
    internal static string RewriteQueryRef(string queryRef,
        IReadOnlyDictionary<string, string> dotMap, IReadOnlyDictionary<string, string> entityMap)
    {
        if (dotMap.TryGetValue(queryRef, out var mapped)) return mapped;
        int open = queryRef.IndexOf('(');
        if (open > 0 && queryRef.EndsWith(")", StringComparison.Ordinal))
        {
            string inner = queryRef[(open + 1)..^1];
            string rewritten = RewriteQueryRef(inner, dotMap, entityMap);
            if (!rewritten.Equals(inner, StringComparison.Ordinal))
                return queryRef[..(open + 1)] + rewritten + ")";
        }
        int dot = queryRef.IndexOf('.');
        if (dot > 0 && entityMap.TryGetValue(queryRef[..dot], out var newTable))
            return newTable + queryRef[dot..];
        return queryRef;
    }
}
