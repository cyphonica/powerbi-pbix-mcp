using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

/// <summary>
/// OFFLINE template-model editing - read and edit the model of a CLOSED .pbit (Power BI template) by editing
/// its plain-JSON DataModelSchema (TMSL) directly on disk. No Power BI Desktop, no window and no running
/// engine: a template carries no data, so the model can be read and rewritten as JSON. Every edit writes the
/// .pbit back in place with a .bak guard beside it; save_template_model is the explicit save-as. This is the
/// JSON counterpart to the engine-backed edit_measure_offline (which needs a Desktop window and real data).
/// </summary>
[McpServerToolType]
public static class TemplateModelTools
{
    [McpServerTool(Name = "open_template_model")]
    [Description(
        "Read the model of a closed .pbit template's DataModelSchema on disk, with no Power BI Desktop and no " +
        "engine: returns each table with its columns (name + data type + key properties) and measures (name + " +
        "DAX expression + display folder), plus the relationships (from/to). A .pbit that has no DataModelSchema " +
        "part (a live-connection or PBIR-only template) comes back ok:false with a clear note.")]
    public static string OpenTemplateModel(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath)
        => J.Try(() => templates.OpenTemplateModel(pbitPath));

    [McpServerTool(Name = "add_template_measure")]
    [Description(
        "Add a measure to a table in a closed .pbit template's model, editing the DataModelSchema JSON on disk " +
        "with no Power BI Desktop. Collision-checked: fails if a measure of that name already exists on the " +
        "table. The .pbit is written back in place (the original is copied to a .bak beside it first).")]
    public static string AddTemplateMeasure(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath,
        [Description("home table for the measure")] string table,
        [Description("measure name")] string name,
        [Description("DAX expression, e.g. SUM(Sales[Amount])")] string expression,
        [Description("format string, e.g. \"#,0\" or \"0.0%\" (optional)")] string? formatString = null,
        [Description("display folder (optional)")] string? displayFolder = null)
        => J.Try(() => templates.AddTemplateMeasure(pbitPath, table, name, expression, formatString, displayFolder));

    [McpServerTool(Name = "update_template_measure")]
    [Description(
        "Update an existing measure's DAX expression, format string and/or display folder in a closed .pbit " +
        "template's model, editing the DataModelSchema JSON on disk with no Power BI Desktop. Any omitted field " +
        "is left unchanged. Fails if the measure does not exist. Written back in place (with a .bak guard).")]
    public static string UpdateTemplateMeasure(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath,
        [Description("home table of the measure")] string table,
        [Description("measure name")] string name,
        [Description("new DAX expression (omit to keep)")] string? expression = null,
        [Description("new format string (omit to keep)")] string? formatString = null,
        [Description("new display folder (omit to keep)")] string? displayFolder = null)
        => J.Try(() => templates.UpdateTemplateMeasure(pbitPath, table, name, expression, formatString, displayFolder));

    [McpServerTool(Name = "delete_template_measure")]
    [Description(
        "Delete a measure from a table in a closed .pbit template's model, editing the DataModelSchema JSON on " +
        "disk with no Power BI Desktop. Fails if the measure does not exist. Written back in place (with a .bak " +
        "guard).")]
    public static string DeleteTemplateMeasure(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath,
        [Description("home table of the measure")] string table,
        [Description("measure name")] string name)
        => J.Try(() => templates.DeleteTemplateMeasure(pbitPath, table, name));

    [McpServerTool(Name = "set_template_column")]
    [Description(
        "Patch properties of an existing column in a closed .pbit template's model - data type, format string, " +
        "hidden flag, sort-by column and default summarisation - editing the DataModelSchema JSON on disk with " +
        "no Power BI Desktop. Any omitted property is left unchanged. Fails if the column does not exist. " +
        "Written back in place (with a .bak guard).")]
    public static string SetTemplateColumn(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath,
        [Description("table that owns the column")] string table,
        [Description("column name")] string column,
        [Description("data type, e.g. int64, decimal, double, string, dateTime, boolean (omit to keep)")] string? dataType = null,
        [Description("format string, e.g. \"#,0\" or \"0.0%\" (omit to keep)")] string? formatString = null,
        [Description("hide (true) or show (false) the column (omit to keep)")] bool? isHidden = null,
        [Description("sort-by column name (omit to keep)")] string? sortByColumn = null,
        [Description("default summarisation, e.g. none, sum, count, average, min, max (omit to keep)")] string? summarizeBy = null)
        => J.Try(() => templates.SetTemplateColumn(pbitPath, table, column, dataType, formatString, isHidden, sortByColumn, summarizeBy));

    [McpServerTool(Name = "add_template_relationship")]
    [Description(
        "Add a relationship to a closed .pbit template's model, editing the DataModelSchema JSON on disk with no " +
        "Power BI Desktop. Both endpoints must exist (the tables and columns are validated). A fresh GUID name " +
        "is generated. Collision-checked: fails if an identical from/to relationship already exists. Written " +
        "back in place (with a .bak guard).")]
    public static string AddTemplateRelationship(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath,
        [Description("the 'many' side table")] string fromTable,
        [Description("the 'many' side column")] string fromColumn,
        [Description("the 'one' side table")] string toTable,
        [Description("the 'one' side column")] string toColumn,
        [Description("cross-filtering: oneDirection (default) or bothDirections (or automatic)")] string? crossFilteringBehavior = null,
        [Description("whether the relationship is active (default true)")] bool isActive = true)
        => J.Try(() => templates.AddTemplateRelationship(pbitPath, fromTable, fromColumn, toTable, toColumn, crossFilteringBehavior, isActive));

    [McpServerTool(Name = "delete_template_relationship")]
    [Description(
        "Delete the relationship whose endpoints match from/to from a closed .pbit template's model, editing the " +
        "DataModelSchema JSON on disk with no Power BI Desktop. Fails if no such relationship exists. Written " +
        "back in place (with a .bak guard).")]
    public static string DeleteTemplateRelationship(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath,
        [Description("the 'many' side table")] string fromTable,
        [Description("the 'many' side column")] string fromColumn,
        [Description("the 'one' side table")] string toTable,
        [Description("the 'one' side column")] string toColumn)
        => J.Try(() => templates.DeleteTemplateRelationship(pbitPath, fromTable, fromColumn, toTable, toColumn));

    [McpServerTool(Name = "save_template_model")]
    [Description(
        "Rewrite a closed .pbit template's ZIP with the current DataModelSchema, preserving every other part " +
        "(Report/Layout, [Content_Types].xml, SecurityBindings, Version, etc.) byte-for-byte - no Power BI " +
        "Desktop. With no outPath it re-packs in place (the original is copied to a .bak first); with an outPath " +
        "it writes an edited copy there and leaves the source untouched. The edit tools already persist on each " +
        "call, so this is the explicit save-as (or an integrity re-pack).")]
    public static string SaveTemplateModel(TemplateModelService templates,
        [Description("path to the closed .pbit template")] string pbitPath,
        [Description("optional destination .pbit for a save-as copy (omit to re-pack in place)")] string? outPath = null)
        => J.Try(() => templates.SaveTemplateModel(pbitPath, outPath));
}
