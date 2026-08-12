using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

/// <summary>
/// Wave G3 CROSS-LAYER tools: the model and the report analysed as ONE artifact. Competing stacks
/// cross-reference the two layers; Super BI's find_unused is model-only and run_bpa needs a live
/// session - these tools close both gaps: model_report_usage / impact_analysis / scan_broken_refs
/// join a live model session to either report reader (legacy Layout session, PBIR session, or a
/// PBIR path), and dax_lint / dax_suggest_rewrite run a PURE OFFLINE static linter that needs no
/// session at all.
/// </summary>
[McpServerToolType]
public static class CrossLayerTools
{
    /// <summary>Resolve reportSource to collected field uses: a legacy reportSessionId, an open
    /// pbirSessionId, or a path to a PBIR .pbix / PBIP folder - both readers feed one core.</summary>
    private static (List<ReportFieldUse> Uses, string Kind) ResolveUses(ReportService report, PbirService pbir, string reportSource)
    {
        string src = (reportSource ?? "").Trim();
        if (src.Length == 0)
            throw new ArgumentException("reportSource is required: a reportSessionId (open_report), a pbirSessionId (read_pbir), or a path to a PBIR .pbix / PBIP folder.");
        try { return (report.CollectFieldUses(src), "legacy Layout (reportSessionId)"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unknown reportSessionId")) { }
        try { return (CrossLayerAnalyzer.CollectPbirUses(pbir.SessionModel(src)), "PBIR (pbirSessionId)"); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unknown pbirSessionId")) { }
        if (File.Exists(src) || Directory.Exists(src))
            return (CrossLayerAnalyzer.CollectPbirUses(pbir.OpenModel(src)), "PBIR (path)");
        throw new InvalidOperationException($"reportSource '{src}' is not an open report session, an open PBIR session, or an existing path.");
    }

    private static List<string>? SplitList(string? csv) =>
        string.IsNullOrWhiteSpace(csv) ? null
            : csv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    [McpServerTool(Name = "model_report_usage")]
    [Description("CROSS-LAYER usage analysis: join the live semantic model (sessionId) with the report (reportSource = a reportSessionId from open_report, a pbirSessionId from read_pbir, or a path to a PBIR .pbix/PBIP folder) and classify EVERY model field three ways. DIRECT = bound in the report - a visual projection, filter, slicer, tooltip, sparkline, sort, or a conditional-formatting/chrome binding (colour measures, icon rules, image URLs, dynamic titles - the bindings naive scanners miss). INDIRECT = reached via a direct measure's DAX lineage, a relationship path between in-play tables, a sort-by column, or a model-internal reference. UNUSED = neither - the safe-to-remove shortlist that find_unused (model-only) cannot produce. Read-only.")]
    public static string ModelReportUsage(ModelService model, ReportService report, PbirService pbir,
        [Description("live model sessionId (connect_model)")] string sessionId,
        [Description("a reportSessionId, a pbirSessionId, or a PBIR .pbix / PBIP folder path")] string reportSource)
        => J.Try(() =>
        {
            var (uses, kind) = ResolveUses(report, pbir, reportSource);
            return model.ModelReportUsage(sessionId, uses, kind);
        });

    [McpServerTool(Name = "impact_analysis")]
    [Description("BLAST RADIUS of one model object (a measure name or Table[Field]): every model dependant that transitively references it through DAX, plus - when reportSource is given - every report visual touching the object or any dependant measure (page/visual/context). The pre-rename / pre-delete safety check. Read-only.")]
    public static string ImpactAnalysis(ModelService model, ReportService report, PbirService pbir,
        [Description("live model sessionId (connect_model)")] string sessionId,
        [Description("the object: Table[Field] or a bare measure name")] string objectName,
        [Description("optional report side: a reportSessionId, a pbirSessionId, or a PBIR path (omit for a model-only radius)")] string? reportSource = null)
        => J.Try(() =>
        {
            var uses = string.IsNullOrWhiteSpace(reportSource)
                ? new List<ReportFieldUse>()
                : ResolveUses(report, pbir, reportSource!).Uses;
            return model.ImpactAnalysis(sessionId, objectName, uses);
        });

    [McpServerTool(Name = "scan_broken_refs")]
    [Description("Flag report bindings that point at MISSING model fields (renamed/deleted tables, columns or measures): every projection, filter, sort and conditional-formatting binding is resolved against the live model, and each broken ref reports where it is bound plus repair suggestions (same field on another table, closest name on the same table). reportSource = a reportSessionId, a pbirSessionId, or a PBIR path. Repair legacy sessions with fix_broken_visuals. Read-only.")]
    public static string ScanBrokenRefs(ModelService model, ReportService report, PbirService pbir,
        [Description("live model sessionId (connect_model)")] string sessionId,
        [Description("a reportSessionId, a pbirSessionId, or a PBIR .pbix / PBIP folder path")] string reportSource)
        => J.Try(() =>
        {
            var (uses, kind) = ResolveUses(report, pbir, reportSource);
            return model.ScanBrokenRefs(sessionId, uses, kind);
        });

    [McpServerTool(Name = "dax_lint")]
    [Description("PURE OFFLINE DAX static linter - no live session needed for a raw expression (the gap run_bpa cannot cover: linting a CANDIDATE expression before it is applied). Pass expression for offline lint; or sessionId to lint live measures (all, one table's, or one measure via table+measure). Rules with line numbers, severity and a rewrite hint: FILTER(wholeTable) inside CALCULATE, nested CALCULATE, '/' where DIVIDE belongs, IFERROR wrapping, '+ 0' blank suppression, EARLIER usage, SUMMARIZE used for aggregation, and UNKNOWN_FUNCTION - the AI-hallucinated-function catcher, checked against a maintained static catalogue (model UDFs auto-admitted in session mode; admit new engine functions via extraFunctions, comma-separated).")]
    public static string DaxLint(ModelService model,
        [Description("a raw DAX expression to lint offline (mutually exclusive with sessionId)")] string? expression = null,
        [Description("live model sessionId - lints measures instead of a raw expression")] string? sessionId = null,
        [Description("with sessionId: restrict to one table")] string? table = null,
        [Description("with sessionId: restrict to one measure")] string? measure = null,
        [Description("comma-separated extra function names to accept as known")] string? extraFunctions = null)
        => J.Try(() =>
        {
            bool hasExpr = !string.IsNullOrWhiteSpace(expression);
            bool hasSession = !string.IsNullOrWhiteSpace(sessionId);
            if (hasExpr == hasSession)
                throw new ArgumentException("Pass exactly one of expression (offline) or sessionId (live measures).");
            if (hasExpr)
                return (object)new { ok = true, mode = "offline", result = DaxLinter.LintResult("(expression)", expression!, SplitList(extraFunctions)) };
            return model.DaxLintSession(sessionId!, table, measure, SplitList(extraFunctions));
        });

    [McpServerTool(Name = "dax_suggest_rewrite")]
    [Description("Concrete BEFORE/AFTER rewrites for a DAX expression's lint findings - pure offline, no session. Mechanical fixes are applied: 'a / b' -> DIVIDE(a, b), IFERROR(x / y, alt) -> DIVIDE(x, y, alt), a trailing '+ 0' removed, and FILTER(Table, single-column predicate) inside CALCULATE collapsed to the bare predicate. Findings with no safe mechanical fix (EARLIER, SUMMARIZE aggregations, nested CALCULATE, ...) come back as hint-only notes. Also returns the full suggested expression with every non-overlapping rewrite applied.")]
    public static string DaxSuggestRewrite(
        [Description("the DAX expression to rewrite")] string expression,
        [Description("comma-separated extra function names to accept as known")] string? extraFunctions = null)
        => J.Try(() =>
        {
            var (rewrites, notes, suggested) = DaxLinter.SuggestRewrite(expression, SplitList(extraFunctions));
            return new
            {
                ok = true,
                rewriteCount = rewrites.Count,
                rewrites = rewrites.Select(r => new { rule = r.Rule, line = r.Line, before = r.Before, after = r.After }).ToList(),
                notes = notes.Select(n => new { rule = n.Rule, severity = n.Severity, line = n.Line, message = n.Message, hint = n.Hint }).ToList(),
                suggested,
                changed = !string.Equals(suggested, expression, StringComparison.Ordinal),
            };
        });
}
