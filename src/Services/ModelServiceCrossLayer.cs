namespace SuperBiMcp.Services;

/// <summary>
/// Wave G3 model-side entry points for the cross-layer tools: resolve the live session's TOM
/// model, flatten it through <see cref="CrossLayerAnalyzer.BuildInventory"/> and delegate to the
/// pure analyzers (classification, impact, broken refs), plus the live-session face of the
/// offline DAX linter (lint one measure or every measure). All read-only - nothing here mutates
/// the model.
/// </summary>
public sealed partial class ModelService
{
    /// <summary>model_report_usage: classify every model field DIRECT / INDIRECT / UNUSED against
    /// the report's collected field uses.</summary>
    public object ModelReportUsage(string sessionId, IReadOnlyList<ReportFieldUse> uses, string sourceKind)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var inv = CrossLayerAnalyzer.BuildInventory(model);
        return CrossLayerAnalyzer.Classify(inv, uses, sourceKind);
    }

    /// <summary>impact_analysis: the blast radius of one model object - transitive model
    /// dependants plus the report visuals touching it (empty uses = model-only radius).</summary>
    public object ImpactAnalysis(string sessionId, string objectName, IReadOnlyList<ReportFieldUse> uses)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var inv = CrossLayerAnalyzer.BuildInventory(model);
        return CrossLayerAnalyzer.Impact(inv, uses, objectName);
    }

    /// <summary>scan_broken_refs: report bindings pointing at model fields that do not exist.</summary>
    public object ScanBrokenRefs(string sessionId, IReadOnlyList<ReportFieldUse> uses, string sourceKind)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var inv = CrossLayerAnalyzer.BuildInventory(model);
        return CrossLayerAnalyzer.FindBrokenRefs(inv, uses, sourceKind);
    }

    /// <summary>dax_lint over the live session: one measure (table + measure) or every measure in
    /// the model. The model's UDF names are admitted to the function catalogue automatically so a
    /// user-defined function never trips UNKNOWN_FUNCTION.</summary>
    public object DaxLintSession(string sessionId, string? table, string? measure, IEnumerable<string>? extraFunctions)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var extra = new List<string>(extraFunctions ?? Array.Empty<string>());
        foreach (var fn in model.Functions) extra.Add(fn.Name);

        var targets = new List<(string Label, string Dax)>();
        foreach (var t in model.Tables)
        {
            if (!string.IsNullOrWhiteSpace(table) && !t.Name.Equals(table, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var m in t.Measures)
            {
                if (!string.IsNullOrWhiteSpace(measure) && !m.Name.Equals(measure, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(m.Expression))
                    targets.Add(($"{t.Name}[{m.Name}]", m.Expression));
            }
        }
        if (targets.Count == 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(measure)
                ? "No measures with expressions found in scope."
                : $"Measure '{measure}' not found{(string.IsNullOrWhiteSpace(table) ? "" : $" on table '{table}'")}.");

        var results = targets.Select(t => DaxLinter.LintResult(t.Label, t.Dax, extra)).ToList();
        var shaped = results.Select(r => System.Text.Json.JsonSerializer.SerializeToNode(r)!).ToList();
        return new
        {
            ok = true,
            measuresLinted = results.Count,
            findingCount = shaped.Sum(n => (int)n["findingCount"]!),
            errors = shaped.Sum(n => (int)n["errors"]!),
            warnings = shaped.Sum(n => (int)n["warnings"]!),
            measures = results,
        };
    }
}
