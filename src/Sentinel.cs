using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp;

/// <summary>
/// Super-BI Sentinel - the trust layer. A model integrity SNAPSHOT (sentinel_snapshot, taken against a
/// live model) captures the grand total, per-table row counts, per-group totals and per-measure health.
/// Sentinel.Diff compares two snapshots (e.g. before vs after a refresh) and raises ranked alerts when
/// integrity regresses: a whole category vanished, rows collapsed, totals dropped, a measure started
/// erroring. This is CI/observability for BI - it tells you the refresh broke a report BEFORE a customer
/// does, explains why, and proposes the fix. Diff is pure (two JSON files), so it runs headless:
///
///   SuperBiMcp sentinel-diff before.json after.json     (exit 1 if any critical regression - a CI gate)
/// </summary>
public static class Sentinel
{
    public static int Run(string[] args)
    {
        try
        {
            if (args.Length < 3) { Console.Error.WriteLine("usage: SuperBiMcp sentinel-diff <before.json> <after.json>"); return 2; }
            if (!File.Exists(args[1]) || !File.Exists(args[2])) { Console.Error.WriteLine("snapshot file(s) not found."); return 2; }
            var res = Diff(File.ReadAllText(args[1]), File.ReadAllText(args[2]));
            string json = JsonSerializer.Serialize(res, Cli.Pretty);
            Console.WriteLine(json);
            var status = (JsonNode.Parse(json) as JsonObject)?["status"]?.GetValue<string>();
            return status == "fail" ? 1 : 0;   // non-zero so CI / a pipeline can gate on it
        }
        catch (Exception ex) { Console.Error.WriteLine("ERROR: " + ex.Message); return 2; }
    }

    public static object Diff(string beforeJson, string afterJson)
    {
        var a = JsonNode.Parse(beforeJson) as JsonObject ?? throw new InvalidOperationException("'before' snapshot is not valid JSON.");
        var b = JsonNode.Parse(afterJson) as JsonObject ?? throw new InvalidOperationException("'after' snapshot is not valid JSON.");
        var alerts = new List<object>();
        int crit = 0;
        void Alert(bool critical, object obj) { if (critical) crit++; alerts.Add(obj); }
        string anchor = (string?)a["anchorMeasure"] ?? "the anchor measure";

        // 1. grand total collapse
        double ga = Num(a["grandTotal"]), gb = Num(b["grandTotal"]);
        if (ga > 0 && gb < ga * 0.999)
            Alert(gb < ga * 0.9, new
            {
                severity = gb < ga * 0.9 ? "critical" : "warn",
                type = "total-drop",
                detail = $"Grand total fell {Pct(ga, gb)} ({Fmt(ga)} -> {Fmt(gb)}) for [{anchor}].",
                was = ga, now = gb,
            });

        // 2. table row collapse
        var bTables = (b["tables"] as JsonArray)?.OfType<JsonObject>().ToDictionary(t => (string?)t["name"] ?? "", t => Num(t["rows"])) ?? new();
        foreach (var t in (a["tables"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
        {
            string name = (string?)t["name"] ?? ""; double ra = Num(t["rows"]);
            if (bTables.TryGetValue(name, out var rb) && ra > 0 && rb < ra * 0.999)
                Alert(rb < ra * 0.9, new
                {
                    severity = rb < ra * 0.9 ? "critical" : "warn",
                    type = "rows-drop",
                    detail = $"Table '{name}' lost rows: {ra:#,0} -> {rb:#,0} ({Pct(ra, rb)}).",
                    was = ra, now = rb,
                });
        }

        // 3. a whole group value vanishing - the category-dropped / a-brand class
        var ag = a["groups"] as JsonObject; var bg = b["groups"] as JsonObject;
        if (ag != null)
            foreach (var kv in ag)
            {
                if (kv.Value is not JsonObject vals1) continue;
                var vals2 = bg?[kv.Key] as JsonObject;
                var dropped = new List<(string v, double was)>();
                foreach (var vv in vals1)
                {
                    double was = Num(vv.Value);
                    double now = vals2 != null && vals2[vv.Key] != null ? Num(vals2[vv.Key]) : 0;
                    if (was > 0 && now <= 0) dropped.Add((vv.Key, was));
                }
                foreach (var d in dropped.OrderByDescending(x => x.was).Take(8))
                    Alert(true, new
                    {
                        severity = "critical",
                        type = "category-dropped",
                        detail = $"{kv.Key} = \"{d.v}\" disappeared after the refresh: was {Fmt(d.was)}, now zero.",
                        groupedBy = kv.Key, value = d.v, was = d.was, now = 0.0,
                        likelyCause = "a whole value going to zero after a refresh is almost never a real sales drop - it is a systematic exclusion upstream (a filter, mis-attribution, or over-scoped feed).",
                        proposedFix = "trace the source feed for this value; if it was re-attributed / mis-scoped, re-attribute and reload (the classic mis-attribution pattern). Run find_attribution_gaps to confirm the cluster, then patch the model M.",
                    });
                if (dropped.Count > 8)
                    Alert(true, new { severity = "critical", type = "category-dropped-more", detail = $"...and {dropped.Count - 8} more values of {kv.Key} also dropped to zero." });
            }

        // 4. measures that started erroring
        var am = a["measures"] as JsonObject; var bm = b["measures"] as JsonObject;
        if (am != null && bm != null)
            foreach (var mv in am)
            {
                string s1 = (string?)mv.Value ?? "ok"; string s2 = (string?)bm[mv.Key] ?? "ok";
                if (s1 == "ok" && s2.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                    Alert(true, new
                    {
                        severity = "critical",
                        type = "measure-broke",
                        detail = $"Measure [{mv.Key}] now ERRORS where it was healthy: {s2}",
                        measure = mv.Key,
                        proposedFix = "guard the measure (DIVIDE / BLANK / HASONEVALUE) - run audit_robustness to find which selections break it.",
                    });
            }

        string status = crit > 0 ? "fail" : alerts.Count > 0 ? "review" : "pass";
        return new
        {
            ok = true,
            status,
            verdict = status == "fail"
                ? $"{crit} CRITICAL integrity regression(s) since the last snapshot - do NOT trust this refresh; a report is now wrong."
                : status == "review"
                    ? $"{alerts.Count} minor change(s) since the last snapshot - worth a look."
                    : "No integrity regressions - the refresh is clean.",
            before = (string?)a["takenAt"], after = (string?)b["takenAt"],
            criticalAlerts = crit,
            alertCount = alerts.Count,
            alerts,
        };
    }

    private static double Num(JsonNode? n)
    {
        if (n == null) return 0;
        try { return n.GetValue<double>(); } catch { }
        try { return double.TryParse(n.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var d) ? d : 0; } catch { return 0; }
    }
    private static string Pct(double a, double b) => a == 0 ? "n/a" : $"{(b - a) / a * 100:0.0}%";
    private static string Fmt(double v) => "$" + v.ToString("#,0", CultureInfo.InvariantCulture);
}
