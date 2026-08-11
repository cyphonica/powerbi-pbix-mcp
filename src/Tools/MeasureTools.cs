using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

[McpServerToolType]
public static class MeasureTools
{
    [McpServerTool(Name = "add_measure")]
    [Description("Add a DAX measure to a table. Commits to the live model (persist with File > Save in Desktop).")]
    public static string AddMeasure(
        ModelService model,
        [Description("sessionId from connect_model")] string sessionId,
        [Description("home table for the measure")] string table,
        [Description("measure name")] string name,
        [Description("DAX expression, e.g. SUM(Sales[Amount])")] string dax,
        [Description("format string, e.g. \"#,0\" or \"0.0%\"")] string? formatString = null,
        [Description("display folder")] string? displayFolder = null,
        [Description("description")] string? description = null)
        => J.Try(() => model.AddMeasure(sessionId, table, name, dax, formatString, displayFolder, description));

    [McpServerTool(Name = "add_narrative_measure")]
    [Description("Auto-narrative: create a dynamic DAX text measure like 'Grated, Sliced drove +2.1% growth'. Names the top-N dimension members by contribution to the change (current - prior) and states the overall growth. Display it in a card (it updates live with slicers). Insight text without the native smart-narrative visual.")]
    public static string AddNarrativeMeasure(ModelService model, string sessionId,
        [Description("home table for the new measure")] string homeTable,
        [Description("the new measure's name, e.g. Sales Story")] string measureName,
        [Description("dimension table, e.g. Dim_Product")] string dimTable,
        [Description("dimension column, e.g. Segment")] string dimColumn,
        [Description("current-period measure, e.g. Sales (Period)")] string currentMeasure,
        [Description("prior-period measure, e.g. Sales (Period) PY")] string priorMeasure,
        [Description("overall growth % measure, e.g. Sales Growth %")] string growthMeasure,
        [Description("how many top contributors to name")] int topN = 2)
        => J.Try(() => model.AddNarrativeMeasure(sessionId, homeTable, measureName, dimTable, dimColumn, currentMeasure, priorMeasure, growthMeasure, topN));

    [McpServerTool(Name = "update_measure")]
    [Description("Update an existing measure's DAX, format string or display folder.")]
    public static string UpdateMeasure(
        ModelService model,
        string sessionId, string table, string name,
        [Description("new DAX (omit to keep)")] string? dax = null,
        [Description("new format string (omit to keep)")] string? formatString = null,
        [Description("new display folder (omit to keep)")] string? displayFolder = null)
        => J.Try(() => model.UpdateMeasure(sessionId, table, name, dax, formatString, displayFolder));

    [McpServerTool(Name = "set_measure_properties")]
    [Description("Set a measure's metadata that update_measure does not cover: hide/show it (hidden), set its description, and/or rename it (newName). Any omitted property is left unchanged. Use update_measure for the DAX / format string / display folder.")]
    public static string SetMeasureProperties(ModelService model, string sessionId, string table, string measure,
        [Description("hide (true) or show (false) the measure")] bool? hidden = null,
        [Description("description for self-service users")] string? description = null,
        [Description("rename the measure to this name")] string? newName = null)
        => J.Try(() => model.SetMeasureProperties(sessionId, table, measure, hidden, description, newName));

    [McpServerTool(Name = "delete_measure")]
    [Description("Delete a measure from a table.")]
    public static string DeleteMeasure(ModelService model, string sessionId, string table, string name)
        => J.Try(() => model.DeleteMeasure(sessionId, table, name));

    [McpServerTool(Name = "list_measures")]
    [Description("List measures (with DAX) for the whole model or one table.")]
    public static string ListMeasures(ModelService model, string sessionId,
        [Description("limit to this table (optional)")] string? table = null)
        => J.Try(() => model.ListMeasures(sessionId, table));

    [McpServerTool(Name = "validate_dax")]
    [Description("Validate a DAX expression against the live model without changing anything (runs EVALUATE ROW).")]
    public static string ValidateDax(ModelService model, string sessionId,
        [Description("DAX expression to validate")] string dax)
        => J.Try(() => model.ValidateDax(sessionId, dax));

    [McpServerTool(Name = "run_dax")]
    [Description("Run a DAX query against the live model and RETURN the result rows (columns + rows). Accepts a full 'EVALUATE ...' query or a bare table expression (EVALUATE is prepended). Use to verify a refresh, get row counts, check date ranges, or preview data.")]
    public static string RunDax(ModelService model, string sessionId,
        [Description("DAX query (EVALUATE ...) or a table expression")] string dax,
        [Description("max rows to return (default 100)")] int maxRows = 100)
        => J.Try(() => model.RunDax(sessionId, dax, maxRows));

    [McpServerTool(Name = "connect_xmla")]
    [Description("Connect to a Fabric/Premium XMLA endpoint (powerbi://...) instead of local Power BI Desktop. Returns a sessionId that every session-based tool (run_dax, validate_dax, export_tmdl, run_bpa...) accepts unchanged - DAX runs against the Service, no Desktop needed. Endpoint/catalog/token default from DAXOPS_XMLA_ENDPOINT / DAXOPS_XMLA_CATALOG / DAXOPS_PBI_TOKEN; the token is held in memory only and never echoed.")]
    public static string ConnectXmla(
        ModelService model,
        [Description("XMLA endpoint, e.g. powerbi://api.powerbi.com/v1.0/myorg/WorkspaceName (default: env DAXOPS_XMLA_ENDPOINT)")] string? endpoint = null,
        [Description("dataset name in the workspace - required with an endpoint (default: env DAXOPS_XMLA_CATALOG)")] string? catalog = null,
        [Description("AAD access token (default: env DAXOPS_PBI_TOKEN)")] string? accessToken = null)
        => J.Try(() => model.ConnectXmla(endpoint, catalog, accessToken));

    [McpServerTool(Name = "assert_measure")]
    [Description("Evaluate a scalar DAX expression (EVALUATE ROW) and compare the result to an expected value: numeric-vs-numeric within tolerance, otherwise ordinal string compare of the invariant-culture rendering. A mismatch returns pass:false (not an error) - the single-assert form of run_golden_set.")]
    public static string AssertMeasure(ModelService model, string sessionId,
        [Description("scalar DAX expression, e.g. [Total Sales] or CALCULATE([Sales], Dim[Year]=2025)")] string dax,
        [Description("expected value, invariant-culture (e.g. 12345.67)")] string expected,
        [Description("absolute numeric tolerance (default 1e-6)")] double tolerance = 1e-6)
        => J.Try(() => model.AssertMeasure(sessionId, dax, expected, tolerance));

    [McpServerTool(Name = "save_golden_set")]
    [Description("Capture a measure-regression baseline: evaluate every model measure (or a comma-separated subset) via EVALUATE ROW and write a deterministic, git-friendly golden-set JSON file (sorted by name, no timestamps). A measure that errors is recorded as an error-state golden so regressions to/from errors are caught. Replay with run_golden_set.")]
    public static string SaveGoldenSet(ModelService model, string sessionId,
        [Description("output file path for the golden-set JSON")] string path,
        [Description("comma-separated measure names to capture (omit for all model measures)")] string? measures = null)
        => J.Try(() => model.SaveGoldenSet(sessionId, path, measures));

    [McpServerTool(Name = "run_golden_set")]
    [Description("Replay a golden-set file against the live model: every golden is re-evaluated and compared to its baseline (numeric tolerance 1e-6, error-state matching). Returns total/passed/failed plus per-failure expected vs actual - the numbers gate for verifying a measure change in CI.")]
    public static string RunGoldenSet(ModelService model, string sessionId,
        [Description("golden-set JSON file written by save_golden_set")] string path)
        => J.Try(() => model.RunGoldenSet(sessionId, path));
}
