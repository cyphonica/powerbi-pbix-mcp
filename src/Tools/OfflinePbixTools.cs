using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

/// <summary>
/// Engine-backed offline .pbix operations - pbix-mcp-style abilities over a CLOSED .pbix, driven by the
/// LOCAL Power BI Desktop that is installed on this Windows box. Each tool briefly opens the file in Power
/// BI Desktop (a window appears), lets its embedded engine load the model, does the work, and closes
/// Desktop again. No VertiPaq / model compiler is re-implemented; the local Desktop engine does the load.
/// These need a real interactive Windows machine with Power BI Desktop, so they run on the local MCP server
/// only (like the other Desktop-driving tools, they are not wired onto the unattended HTTP surface).
/// </summary>
[McpServerToolType]
public static class OfflinePbixTools
{
    [McpServerTool(Name = "eval_dax_offline")]
    [Description(
        "Evaluate a DAX query against a CLOSED .pbix and return the result rows (columns + rows). Briefly " +
        "opens the file in the local Power BI Desktop (a Desktop window appears), runs the query against its " +
        "engine, then closes Desktop. Accepts a full 'EVALUATE ...' query or a bare table expression " +
        "(EVALUATE is prepended). Same result shape as run_dax, but no session / no manually-open Desktop.")]
    public static string EvalDaxOffline(OfflinePbixService offline,
        [Description("path to the closed .pbix")] string pbixPath,
        [Description("DAX query (EVALUATE ...) or a table expression")] string dax,
        [Description("seconds to wait for the model to load before giving up (default 180)")] int timeoutSec = 180)
        => J.Try(() => offline.EvalDaxOffline(pbixPath, dax, timeoutSec));

    [McpServerTool(Name = "read_table_offline")]
    [Description(
        "Read up to topN rows of one table from a CLOSED .pbix (EVALUATE TOPN(topN, 'table')). Briefly opens " +
        "the file in the local Power BI Desktop (a Desktop window appears), reads the rows, then closes " +
        "Desktop. Returns columns + rows; when the row cap is hit the result flags truncated:true.")]
    public static string ReadTableOffline(OfflinePbixService offline,
        [Description("path to the closed .pbix")] string pbixPath,
        [Description("table name to read")] string table,
        [Description("max rows to return (default 1000)")] int topN = 1000)
        => J.Try(() => offline.ReadTableOffline(pbixPath, table, topN));

    [McpServerTool(Name = "get_model_offline")]
    [Description(
        "Read the model of a CLOSED .pbix: tables, columns with data types, measures with their DAX, " +
        "relationships and named M expressions. Briefly opens the file in the local Power BI Desktop (a " +
        "Desktop window appears), reads the model via TOM, then closes Desktop. The offline form of " +
        "get_model_summary - no session / no manually-open Desktop.")]
    public static string GetModelOffline(OfflinePbixService offline,
        [Description("path to the closed .pbix")] string pbixPath,
        [Description("seconds to wait for the model to load before giving up (default 180)")] int timeoutSec = 180)
        => J.Try(() => offline.GetModelOffline(pbixPath, timeoutSec));

    [McpServerTool(Name = "edit_measure_offline")]
    [Description(
        "Add or edit a measure on a CLOSED .pbix and save it back to disk. Briefly opens the file in the " +
        "local Power BI Desktop (a Desktop window appears), adds/updates the measure via TOM, then drives " +
        "Desktop's own File > Save (scripted Ctrl+S) so the change lands in the .pbix, and closes Desktop. " +
        "An existing measure is updated (omit a field to keep it); a new one needs expression. Reports the " +
        "before/after expression, format string and display folder, and whether the disk save was confirmed.")]
    public static string EditMeasureOffline(OfflinePbixService offline,
        [Description("path to the closed .pbix")] string pbixPath,
        [Description("home table of the measure")] string table,
        [Description("measure name")] string name,
        [Description("DAX expression (required to create a new measure; omit to keep an existing one)")] string? expression = null,
        [Description("format string, e.g. \"#,0\" or \"0.0%\" (omit to keep)")] string? formatString = null,
        [Description("display folder (omit to keep)")] string? displayFolder = null,
        [Description("seconds to wait for the model to load before giving up (default 180)")] int timeoutSec = 180,
        [Description("scripted File > Save attempts before giving up (default 3)")] int saveRetries = 3)
        => J.Try(() => offline.EditMeasureOffline(pbixPath, table, name, expression, formatString, displayFolder, timeoutSec, saveRetries));
}
