using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

/// <summary>
/// Tool surface for the ENHANCED report format (PBIR): the folder-tree report definition that becomes the
/// only report format at GA. Mirrors the legacy report tools but reads/writes the per-file definition/ tree
/// (page.json / visual.json / bookmarks) inside a .pbix or a PBIP folder, routing all authoring through the
/// shared property/encoder/filter engine.
/// </summary>
[McpServerToolType]
public static class PbirTools
{
    private static readonly JsonSerializerOptions Ci = new() { PropertyNameCaseInsensitive = true };
    private record BindIn(string? role, string table, string field, string? kind);

    private static List<FieldBinding> ParseFields(string? json, string defaultRole)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return new List<FieldBinding>();
        var items = JsonSerializer.Deserialize<List<BindIn>>(json, Ci)
                    ?? throw new ArgumentException("fields JSON did not parse to an array of {role,table,field,kind}.");
        return items.Select(i => new FieldBinding(
            string.IsNullOrWhiteSpace(i.role) ? defaultRole : i.role!,
            i.table, i.field,
            string.IsNullOrWhiteSpace(i.kind) ? "column" : i.kind!)).ToList();
    }

    [McpServerTool(Name = "detect_report_format")]
    [Description("Classify a report source as legacy | pbir | pbip. Robust on both a .pbix (a ZIP: Report/Layout = legacy, Report/definition/ = pbir) and a PBIP project folder (a *.pbir pointer or a definition/ folder). Returns the classification plus the evidence.")]
    public static string DetectReportFormat(PbirService pbir,
        [Description("absolute path to a .pbix file or a PBIP project folder")] string path)
        => J.Try(() => pbir.DetectReportFormat(path));

    [McpServerTool(Name = "read_pbir")]
    [Description("Open an enhanced (PBIR) report from a .pbix or a PBIP folder, parse the whole definition tree into memory, and return a structured summary (pages + visual counts, bookmarks, dataset reference). Returns a pbirSessionId reused by the other pbir tools. The source must be CLOSED in Power BI Desktop.")]
    public static string ReadPbir(PbirService pbir,
        [Description("absolute path to a PBIR .pbix or a PBIP project folder")] string path)
        => J.Try(() => pbir.ReadPbir(path));

    [McpServerTool(Name = "list_pbir_pages")]
    [Description("List the pages of an open PBIR report (GUID name, displayName, size, displayOption, visual count), in page order.")]
    public static string ListPbirPages(PbirService pbir, string pbirSessionId)
        => J.Try(() => pbir.ListPages(pbirSessionId));

    [McpServerTool(Name = "get_pbir_page")]
    [Description("Get one PBIR page's page.json plus the names/types of its visuals. page = the GUID name or the displayName.")]
    public static string GetPbirPage(PbirService pbir, string pbirSessionId,
        [Description("page GUID name or displayName")] string page)
        => J.Try(() => pbir.GetPage(pbirSessionId, page));

    [McpServerTool(Name = "get_pbir_visual")]
    [Description("Get one PBIR visual's visual.json (position + visual{visualType,query,objects,...} + filterConfig), verbatim.")]
    public static string GetPbirVisual(PbirService pbir, string pbirSessionId,
        [Description("page GUID name or displayName")] string page,
        [Description("visual GUID name")] string visual)
        => J.Try(() => pbir.GetVisual(pbirSessionId, page, visual));

    [McpServerTool(Name = "save_pbir")]
    [Description("Persist the open PBIR report, re-emitting ONLY changed files and preserving every other entry byte-for-byte, GUID names intact, the DataModel untouched, and SecurityBindings stripped. For a .pbix this repacks the zip; for a PBIP folder it writes the changed files. The source must NOT be open in Power BI Desktop.")]
    public static string SavePbir(PbirService pbir, string pbirSessionId)
        => J.Try(() => pbir.Save(pbirSessionId));

    [McpServerTool(Name = "create_pbir_page")]
    [Description("Add a new page to a PBIR report: a fresh GUID folder definition/pages/<guid>/page.json plus an update to pages.json (append to pageOrder). Returns the new page GUID name (use it for adding visuals).")]
    public static string CreatePbirPage(PbirService pbir, string pbirSessionId,
        [Description("page title shown on the tab")] string displayName,
        int? width = null, int? height = null)
        => J.Try(() => pbir.CreatePage(pbirSessionId, displayName, width, height));

    [McpServerTool(Name = "add_pbir_visual")]
    [Description("Add a visual to a PBIR page: a fresh GUID folder definition/pages/<page>/visuals/<guid>/visual.json. The query (projections + prototypeQuery) is built by the shared query builder. fields = JSON array of {role,table,field,kind} (kind=column|measure; role=Category|Y|Values|Rows|Columns|...). Returns the new visual GUID name.")]
    public static string AddPbirVisual(PbirService pbir, string pbirSessionId,
        [Description("page GUID name or displayName")] string page,
        [Description("visual type id, e.g. clusteredColumnChart | tableEx | card | slicer")] string visualType,
        [Description("JSON array of {role,table,field,kind}")] string fields = "[]",
        double x = 16, double y = 16, double width = 400, double height = 300, string? title = null)
        => J.Try(() => pbir.AddVisual(pbirSessionId, page, visualType, ParseFields(fields, "Values"),
            x, y, width, height, title));

    [McpServerTool(Name = "set_pbir_visual_format")]
    [Description("Set ONE formatting property on a PBIR visual, merged into its objects tree via the shared encoder. card = the formatting object (e.g. legend, dataLabels, title, background). bucket = objects (data cards) | visualContainerObjects (chrome). value is a SCALAR (true/false, a number, 'text', '#RRGGBB') encoded by property kind, OR (when valueIsJson=true) a pre-shaped structured PBI JSON value written verbatim (a measure-bound expr {\"measure\":\"T[M]\"}, a FillRule, an image, gradient stops).")]
    public static string SetPbirVisualFormat(PbirService pbir, string pbirSessionId,
        [Description("page GUID name or displayName")] string page,
        [Description("visual GUID name")] string visual,
        [Description("formatting object/card id, e.g. legend|dataLabels|title|background")] string card,
        [Description("property id, e.g. show|fontSize|labelColor|fontColor")] string property,
        [Description("the value: a scalar by default, or structured PBI JSON when valueIsJson=true")] string value,
        [Description("objects | visualContainerObjects")] string bucket = "objects",
        [Description("set true to parse value as a JSON node written verbatim (the Wave M nested-object behaviour)")] bool valueIsJson = false)
        => J.Try(() =>
        {
            JsonNode? v = valueIsJson
                ? JsonNode.Parse(value)
                : ScalarNode(value);
            return pbir.SetVisualFormat(pbirSessionId, page, visual, card, property, v, bucket);
        });

    [McpServerTool(Name = "add_pbir_filter")]
    [Description("Add a filter to a PBIR report at scope = report | page | visual, written into the PBIR filterConfig.filters array via the shared filter builders. kind=categorical|in|values -> an is-one-of values list (pass values); else a comparison/blank filter (op=gt|gte|lt|lte|eq|ne|isblank|isnotblank). fieldKind=column|measure. valueType=string|int|double|decimal|bool|datetime|color drives the literal type suffix.")]
    public static string AddPbirFilter(PbirService pbir, string pbirSessionId,
        [Description("report | page | visual")] string scope,
        string table, string field,
        [Description("categorical|in|values for a values list; comparison|advanced (or empty) for a comparison/blank filter")] string kind = "comparison",
        [Description("column|measure (for the comparison form)")] string fieldKind = "column",
        [Description("gt|gte|lt|lte|eq|ne|isblank|isnotblank (comparison form)")] string? op = null,
        [Description("JSON array of string values (for a categorical list, or a single value for a comparison)")] string? values = null,
        [Description("string|int|double|decimal|bool|datetime|color")] string valueType = "string",
        [Description("page GUID name or displayName (required for page/visual scope)")] string? page = null,
        [Description("visual GUID name (required for visual scope)")] string? visual = null)
        => J.Try(() =>
        {
            List<string>? vals = null;
            if (!string.IsNullOrWhiteSpace(values))
                vals = JsonSerializer.Deserialize<List<string>>(values, Ci)
                       ?? throw new ArgumentException("values must be a JSON array of strings.");
            return pbir.AddFilter(pbirSessionId, scope, page, visual, table, field, kind, fieldKind, op, vals, valueType);
        });

    [McpServerTool(Name = "add_pbir_bookmark")]
    [Description("Add a bookmark to a PBIR report: a definition/bookmarks/<guid>.bookmark.json (explorationState with each visual's display.mode) plus a bookmarks.json index entry. hiddenVisuals = JSON array of the visual GUID names to hide; every other visual on the page is shown.")]
    public static string AddPbirBookmark(PbirService pbir, string pbirSessionId,
        [Description("bookmark title")] string displayName,
        [Description("page GUID name or displayName the bookmark activates")] string page,
        [Description("JSON array of visual GUID names to hide")] string hiddenVisuals = "[]")
        => J.Try(() => pbir.SetBookmark(pbirSessionId, displayName, page, ParseStringList(hiddenVisuals), existingName: null));

    [McpServerTool(Name = "set_pbir_bookmark")]
    [Description("Overwrite an existing PBIR bookmark's explorationState in place (keeps its GUID name). hiddenVisuals = JSON array of the visual GUID names to hide on the bookmark's page.")]
    public static string SetPbirBookmark(PbirService pbir, string pbirSessionId,
        [Description("the bookmark GUID name to overwrite")] string name,
        [Description("bookmark title")] string displayName,
        [Description("page GUID name or displayName the bookmark activates")] string page,
        [Description("JSON array of visual GUID names to hide")] string hiddenVisuals = "[]")
        => J.Try(() => pbir.SetBookmark(pbirSessionId, displayName, page, ParseStringList(hiddenVisuals), existingName: name));

    [McpServerTool(Name = "convert_legacy_to_pbir")]
    [Description("BEST-EFFORT explode a legacy Report/Layout (.pbix or a raw Layout JSON file) into a PBIR definition tree written to a target folder (default <name>.Report next to the source): pages -> page.json, visualContainers -> visual.json, generating GUID names. Returns a pbirSessionId over the new tree. FLAG: best-effort - open in Power BI Desktop to validate/upgrade. pbir->legacy is not implemented.")]
    public static string ConvertLegacyToPbir(PbirService pbir,
        [Description("absolute path to the legacy .pbix (or a raw Report/Layout JSON file)")] string legacyPath,
        [Description("target folder for the PBIR tree (optional; defaults to <name>.Report beside the source)")] string? targetFolder = null)
        => J.Try(() => pbir.ConvertLegacyToPbir(legacyPath, targetFolder));

    // a scalar string -> a typed JsonNode (bool/number kept native so the encoder picks the right kind).
    private static JsonNode? ScalarNode(string value)
    {
        if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(true);
        if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase)) return JsonValue.Create(false);
        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var d)
            && !value.StartsWith("#"))
            return JsonValue.Create(d);
        return JsonValue.Create(value);
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]") return new List<string>();
        return JsonSerializer.Deserialize<List<string>>(json, Ci)
               ?? throw new ArgumentException("expected a JSON array of strings.");
    }
}
