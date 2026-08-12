using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

[McpServerToolType]
public static class ReportTools
{
    private static readonly JsonSerializerOptions Ci = new() { PropertyNameCaseInsensitive = true };
    private record BindIn(string? role, string table, string field, string? kind);

    private static List<FieldBinding> Parse(string json, string defaultRole)
    {
        var items = JsonSerializer.Deserialize<List<BindIn>>(json, Ci)
                    ?? throw new ArgumentException("bindings JSON did not parse to an array of {table,field,kind,role}.");
        return items.Select(i => new FieldBinding(
            string.IsNullOrWhiteSpace(i.role) ? defaultRole : i.role!,
            i.table, i.field,
            string.IsNullOrWhiteSpace(i.kind) ? "column" : i.kind!)).ToList();
    }

    [McpServerTool(Name = "open_report")]
    [Description("Open a .pbix's report for editing (pages/visuals). The .pbix must be CLOSED in Power BI Desktop. Returns a reportSessionId.")]
    public static string OpenReport(ReportService report,
        [Description("absolute path to the .pbix")] string pbixPath)
        => J.Try(() => report.Open(pbixPath));

    [McpServerTool(Name = "list_pages")]
    [Description("List the report pages (name, displayName, size, visual count).")]
    public static string ListPages(ReportService report, string reportSessionId)
        => J.Try(() => report.ListPages(reportSessionId));

    [McpServerTool(Name = "add_page")]
    [Description("Add a report page. Returns its internal pageName (use that for adding visuals).")]
    public static string AddPage(ReportService report, string reportSessionId,
        [Description("page title shown on the tab")] string displayName,
        int width = 1280, int height = 720)
        => J.Try(() => report.AddPage(reportSessionId, displayName, width, height));

    [McpServerTool(Name = "delete_page")]
    [Description("Delete a report page by name or displayName.")]
    public static string DeletePage(ReportService report, string reportSessionId, string pageName)
        => J.Try(() => report.DeletePage(reportSessionId, pageName));

    [McpServerTool(Name = "clear_pages")]
    [Description("Remove ALL pages from the report (e.g. to replace a stale report with a fresh one).")]
    public static string ClearPages(ReportService report, string reportSessionId)
        => J.Try(() => report.ClearPages(reportSessionId));

    [McpServerTool(Name = "set_page_visibility")]
    [Description("Hide or show a report page (the tab). A hidden page stays in the file and keeps working, but viewers in the Power BI Service do not see its tab - use it to keep a page out of a published report. hidden=false shows it again.")]
    public static string SetPageVisibility(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string pageName,
        bool hidden = true)
        => J.Try(() => report.SetPageVisibility(reportSessionId, pageName, hidden));

    [McpServerTool(Name = "list_visuals")]
    [Description("List the visuals on a page (name, type, position).")]
    public static string ListVisuals(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string pageName)
        => J.Try(() => report.ListVisuals(reportSessionId, pageName));

    [McpServerTool(Name = "add_card")]
    [Description("Add a KPI card showing one measure.")]
    public static string AddCard(ReportService report, string reportSessionId, string pageName,
        string table, [Description("measure name")] string measure,
        double x = 16, double y = 16, double width = 200, double height = 120, string? title = null)
        => J.Try(() => report.AddVisual(reportSessionId, pageName, "card", x, y, 0, width, height,
            new[] { new FieldBinding("Values", table, measure, "measure") }, title));

    [McpServerTool(Name = "add_slicer")]
    [Description("Add a slicer for one column or measure. Defaults to a compact Dropdown (what users almost always want); pass mode=List for the classic vertical list, Between/Single for numeric/date ranges.")]
    public static string AddSlicer(ReportService report, string reportSessionId, string pageName,
        string table, string field, [Description("column|measure")] string kind = "column",
        double x = 16, double y = 16, double width = 200, double height = 240, string? title = null,
        [Description("Dropdown|List|Between|Single")] string mode = "Dropdown")
        => J.Try(() => report.AddVisual(reportSessionId, pageName, "slicer", x, y, 0, width, height,
            new[] { new FieldBinding("Values", table, field, kind) }, title, mode));

    [McpServerTool(Name = "add_table_visual")]
    [Description("Add a table visual. fields = JSON array of {table,field,kind} (kind=column|measure), shown left-to-right.")]
    public static string AddTableVisual(ReportService report, string reportSessionId, string pageName,
        [Description("JSON array of {table,field,kind}")] string fields,
        double x = 16, double y = 160, double width = 600, double height = 360, string? title = null)
        => J.Try(() => report.AddVisual(reportSessionId, pageName, "tableEx", x, y, 0, width, height,
            Parse(fields, "Values"), title));

    [McpServerTool(Name = "add_matrix")]
    [Description("Add a matrix. rows/columns/values = JSON arrays of {table,field,kind}. Rows/columns are typically columns; values are measures.")]
    public static string AddMatrix(ReportService report, string reportSessionId, string pageName,
        [Description("JSON array of {table,field} for rows")] string rows,
        [Description("JSON array of {table,field,kind} for values (measures)")] string values,
        [Description("JSON array of {table,field} for columns (optional, '[]' for none)")] string columns = "[]",
        double x = 16, double y = 160, double width = 700, double height = 380, string? title = null)
        => J.Try(() =>
        {
            var binds = new List<FieldBinding>();
            binds.AddRange(Parse(rows, "Rows"));
            if (!string.IsNullOrWhiteSpace(columns) && columns.Trim() != "[]") binds.AddRange(Parse(columns, "Columns"));
            binds.AddRange(Parse(values, "Values"));
            return report.AddVisual(reportSessionId, pageName, "pivotTable", x, y, 0, width, height, binds, title);
        });

    [McpServerTool(Name = "add_chart")]
    [Description("Add a chart. chartType = clusteredColumnChart | clusteredBarChart | lineChart | pieChart | donutChart | areaChart. Category is the axis, value is the measure.")]
    public static string AddChart(ReportService report, string reportSessionId, string pageName,
        string chartType,
        [Description("axis/category table")] string categoryTable,
        [Description("axis/category field (column)")] string categoryField,
        [Description("value table")] string valueTable,
        [Description("value measure")] string valueMeasure,
        [Description("optional legend/series table")] string? seriesTable = null,
        [Description("optional legend/series field")] string? seriesField = null,
        double x = 16, double y = 160, double width = 560, double height = 360, string? title = null)
        => J.Try(() =>
        {
            var binds = new List<FieldBinding>
            {
                new("Category", categoryTable, categoryField, "column"),
                new("Y", valueTable, valueMeasure, "measure"),
            };
            if (!string.IsNullOrWhiteSpace(seriesTable) && !string.IsNullOrWhiteSpace(seriesField))
                binds.Add(new FieldBinding("Series", seriesTable!, seriesField!, "column"));
            return report.AddVisual(reportSessionId, pageName, chartType, x, y, 0, width, height, binds, title);
        });

    [McpServerTool(Name = "add_visual")]
    [Description("Add any visual type with explicit role bindings. bindings = JSON array of {role,table,field,kind}. Roles depend on visualType (e.g. Category/Y for charts, Rows/Columns/Values for pivotTable, Values for card/slicer/tableEx).")]
    public static string AddVisual(ReportService report, string reportSessionId, string pageName,
        [Description("visual type id, e.g. card, tableEx, pivotTable, slicer, clusteredColumnChart, lineChart")] string visualType,
        [Description("JSON array of {role,table,field,kind}")] string bindings,
        double x = 16, double y = 16, double width = 400, double height = 300, string? title = null)
        => J.Try(() => report.AddVisual(reportSessionId, pageName, visualType, x, y, 0, width, height,
            Parse(bindings, "Values"), title));

    [McpServerTool(Name = "add_textbox")]
    [Description("Add a formatted text box - use for page headers, section titles and captions.")]
    public static string AddTextbox(ReportService report, string reportSessionId, string pageName, string text,
        double x = 24, double y = 16, double width = 700, double height = 48,
        [Description("font size in pt")] double fontSize = 20, bool bold = true,
        [Description("hex colour e.g. #1A4480")] string? color = null,
        [Description("left|center|right")] string? align = null)
        => J.Try(() => report.AddTextbox(reportSessionId, pageName, text, x, y, width, height, fontSize, bold, color, align));

    [McpServerTool(Name = "set_page_background")]
    [Description("Set a page's background colour for a clean, professional canvas.")]
    public static string SetPageBackground(ReportService report, string reportSessionId, string pageName,
        [Description("hex colour e.g. #F5F7FA")] string color,
        [Description("0 = opaque, 100 = fully transparent")] double transparency = 0)
        => J.Try(() => report.SetPageBackground(reportSessionId, pageName, color, transparency));

    [McpServerTool(Name = "style_visual")]
    [Description("Give one visual the modern 'floating card' look: background fill, rounded corners, drop shadow, and hide the header. e.g. background=#FFFFFF, cornerRadius=8, shadow=true, showHeader=false, borderColor=#E6E9EF.")]
    public static string StyleVisual(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("hex card fill e.g. #FFFFFF")] string? background = null,
        [Description("corner radius in px, e.g. 8")] double? cornerRadius = null,
        [Description("drop shadow on/off")] bool? shadow = null,
        [Description("show the visual header bar")] bool? showHeader = null,
        [Description("hex border colour e.g. #E6E9EF")] string? borderColor = null)
        => J.Try(() => report.StyleVisual(reportSessionId, pageName, visualName, background, cornerRadius, shadow, showHeader, borderColor));

    [McpServerTool(Name = "style_page")]
    [Description("One-shot 'make it beautiful' pass: apply a consistent card style (fill, rounded corners, shadow, header) to every DATA visual on a page (skips textboxes/shapes). Pair with set_page_background for a clean canvas.")]
    public static string StylePage(ReportService report, string reportSessionId, string pageName,
        [Description("hex card fill e.g. #FFFFFF")] string? background = "#FFFFFF",
        [Description("corner radius in px, e.g. 8")] double? cornerRadius = 8,
        [Description("drop shadow on/off")] bool? shadow = true,
        [Description("show the visual header bar")] bool? showHeader = false,
        [Description("hex border colour e.g. #E6E9EF")] string? borderColor = "#E6E9EF")
        => J.Try(() => report.StylePage(reportSessionId, pageName, background, cornerRadius, shadow, showHeader, borderColor));

    [McpServerTool(Name = "clear_visual_styling")]
    [Description("FLATTEN visuals to a plain 'non-premium' look the SAFE way. Removes the decorative chrome (border, dropShadow, stylePreset, visualHeader/visualHeaderTooltip and plain opaque backgrounds) from each visual's vcObjects. PRESERVES deliberate formatting by default: a custom-text or explicit-show title (e.g. a custom heading, or a title hidden on an overlay chart) and a transparent (overlay) background are kept. NEVER touches a button's navigation action (visualLink), bookmark/page-navigation actions, or the data + conditional formatting (singleVisual.objects). actionButton visuals are skipped entirely. This replaces the buggy 'delete the whole vcObjects bucket' flatten that destroyed meaningful titles/overlays and broke nav buttons. pageName omitted = all pages; visualName omitted = all visuals on the page. Set removeTitles=true to also strip deliberate titles (the old aggressive behaviour). Returns visuals touched, decorative keys removed, and a count of action/visualLink keys PRESERVED.")]
    public static string ClearVisualStyling(ReportService report, string reportSessionId,
        [Description("page name or displayName (omit = all pages)")] string? pageName = null,
        [Description("visual name (omit = all visuals on the page)")] string? visualName = null,
        [Description("also set the page background to solid #FFFFFF")] bool whiteBackground = false,
        [Description("also remove deliberate custom/explicit titles (default false = keep them)")] bool removeTitles = false)
        => J.Try(() => report.ClearVisualStyling(reportSessionId, pageName, visualName, whiteBackground, removeTitles));

    [McpServerTool(Name = "add_shape")]
    [Description("Add a decorative rounded rectangle panel. Place it BEHIND a group of visuals (lower z) to visually group them and add depth - the panel technique for sectioning a report.")]
    public static string AddShape(ReportService report, string reportSessionId, string pageName,
        double x, double y, double width, double height,
        [Description("hex fill e.g. #FFFFFF (null = no fill)")] string? fill = "#FFFFFF",
        [Description("corner radius in px")] double cornerRadius = 12,
        [Description("drop shadow")] bool shadow = true,
        [Description("hex outline colour e.g. #E6E9EF (null = none)")] string? lineColor = null)
        => J.Try(() => report.AddShape(reportSessionId, pageName, x, y, width, height, fill, cornerRadius, shadow, lineColor));

    [McpServerTool(Name = "add_nav_button")]
    [Description("Add a navigation button that jumps to another page (multi-page navigation). Structure matched to Power BI ground truth. Compose several into a nav bar/sidebar. targetPage = the destination page name or displayName.")]
    public static string AddNavButton(ReportService report, string reportSessionId, string pageName,
        [Description("button label")] string text,
        [Description("destination page (name or displayName)")] string targetPage,
        double x = 24, double y = 124, double width = 150, double height = 40,
        [Description("button fill hex")] string fillColor = "#16365C",
        [Description("button text hex")] string textColor = "#FFFFFF")
        => J.Try(() => report.AddNavButton(reportSessionId, pageName, text, targetPage, x, y, width, height, fillColor, textColor));

    [McpServerTool(Name = "add_drillthrough")]
    [Description("Make a page a DRILL-THROUGH target on a column: right-clicking a data point of that column on any other page offers 'Drill through' to this page, filtered to that value. Adds the drill-through page filter + the Back button. Ground-truthed from a real report.")]
    public static string AddDrillthrough(ReportService report, string reportSessionId, string pageName,
        [Description("table of the drill column, e.g. Dim_Product")] string drillTable,
        [Description("column to drill on, e.g. Brand")] string drillColumn)
        => J.Try(() => report.AddDrillthrough(reportSessionId, pageName, drillTable, drillColumn));

    [McpServerTool(Name = "auto_mobile_layout")]
    [Description("Auto-generate a phone (mobile) layout for a page: stacks the significant visuals (slicers, KPI value cards, tables, main charts) vertically on the 320-wide phone canvas; skips decorative shapes, tiny deltas and sparklines. A sensible mobile view in one call. Ground-truthed (layouts id 1).")]
    public static string AutoMobileLayout(ReportService report, string reportSessionId, string pageName)
        => J.Try(() => report.AutoMobileLayout(reportSessionId, pageName));

    [McpServerTool(Name = "set_mobile_position")]
    [Description("Place one visual on the phone (mobile) layout at x,y,width,height on the 320-wide phone canvas (a second layouts entry). Use for precise mobile control. mobileFormat (optional) is a set_visual_format-shaped JSON { vcObjects/objects } of MOBILE-SPECIFIC formatting overrides (e.g. a smaller title or hidden legend on phone) stamped onto the mobile layout entry.")]
    public static string SetMobilePosition(ReportService report, string reportSessionId, string pageName, string visualName,
        double x, double y, double width, double height,
        [Description("optional JSON of mobile-specific formatting overrides { vcObjects/objects }")] string? mobileFormat = null)
        => J.Try(() => report.SetMobilePosition(reportSessionId, pageName, visualName, x, y, width, height, mobileFormat));

    [McpServerTool(Name = "set_visual_visibility")]
    [Description("Show or hide a visual in the base layout (display.mode). Use list_visuals to get names.")]
    public static string SetVisualVisibility(ReportService report, string reportSessionId, string pageName, string visualName, bool hidden)
        => J.Try(() => report.SetVisualVisibility(reportSessionId, pageName, visualName, hidden));

    [McpServerTool(Name = "add_view_switcher")]
    [Description("THE premium pattern from pro reports: one page, a button bar that swaps between VIEWS - each view shows its own visuals and hides the rest. Builds the Display-only bookmarks + the buttons + sets the initial state to the first view. views = JSON array of {name, visuals:[visualName,...]} (visual names from list_visuals/add_* results). Ground-truthed from a real report.")]
    public static string AddViewSwitcher(ReportService report, string reportSessionId, string pageName,
        [Description("JSON array of {name, visuals:[visualName,...]}")] string views,
        double x = 24, double y = 124, double buttonWidth = 150, double buttonHeight = 40, double gap = 8,
        [Description("inactive button fill")] string fillColor = "#5B7494",
        [Description("active (first) button fill")] string activeFillColor = "#16365C",
        [Description("button text colour")] string textColor = "#FFFFFF")
        => J.Try(() => report.AddViewSwitcher(reportSessionId, pageName, views, x, y, buttonWidth, buttonHeight, gap, fillColor, activeFillColor, textColor));

    [McpServerTool(Name = "add_image")]
    [Description("Place an image (e.g. a brand LOGO) on a page - embeds the file into the .pbix as a resource and adds an image visual. Completes the brand kit (logo IN the report, not just the palette). scaling = Fit | Fill | Normal.")]
    public static string AddImage(ReportService report, string reportSessionId, string pageName,
        [Description("path to the image file (png/jpg)")] string imagePath,
        double x = 24, double y = 24, double width = 160, double height = 56,
        [Description("Fit | Fill | Normal")] string scaling = "Fit")
        => J.Try(() => report.AddImage(reportSessionId, pageName, imagePath, x, y, width, height, scaling));

    [McpServerTool(Name = "apply_report_theme")]
    [Description("Apply a report theme (palette + fonts + structural colours) - the single biggest lever for a professional look. preset = executive|vibrant|slate|sunset|forest, OR pass a full Power BI theme JSON in themeJson to override.")]
    public static string ApplyReportTheme(ReportService report, string reportSessionId,
        [Description("executive|vibrant|slate|sunset|forest")] string preset = "executive",
        [Description("optional full Power BI theme JSON (overrides preset)")] string? themeJson = null)
        => J.Try(() => report.ApplyReportTheme(reportSessionId, preset, themeJson));

    [McpServerTool(Name = "build_executive_report")]
    [Description("RECIPE: build a complete premium 'Executive' dashboard page in ONE call from a JSON config. Composes a brand theme, a navy banner + gold seam, a slicer filter-bar, a row of premium KPI cards (value + delta + sparkline), a hero trend line, and a 'by segment' bar chart whose colour encodes growth. Point it at any client model by mapping the config fields. config = JSON with: title, subtitle, headline, headlineLabel, brandColor, accentColor, logoPath (brand kit - palette + logo in banner), canvasWidth/canvasHeight (default 1280x720; use 1920x1080 for a Full-HD pro report), factTable, dateTable, dateColumn, trendMeasure, segmentTable, segmentColumn, segmentValueMeasure, growthMeasure, slicers:[{table,column,title}], kpis:[{measure,label,delta,trend}].")]
    public static string BuildExecutiveReport(ReportService report, string reportSessionId,
        [Description("JSON config mapping the recipe to the client model")] string config)
        => J.Try(() => report.BuildExecutiveReport(reportSessionId, config));

    [McpServerTool(Name = "build_crossretailer_compare")]
    [Description("RECIPE: a Total-Market compare page across two retailers/panels (pairs with conform_dimension). Config: compareTable/compareColumn (the conformed dimension), retailerA/retailerB ({label,table,measure}), optional totalMeasure, slicers, and the additive options clearPages/pagePrefix. Builds banner + slicer bar + a KPI row (Total Market / Retailer A / Retailer B) + a by-entity table + an A-vs-B grouped bar. The pbix must be CLOSED. Verify the render in Desktop after.")]
    public static string BuildCrossRetailerCompare(ReportService report, string reportSessionId, string config)
        => J.Try(() => report.BuildCrossRetailerCompare(reportSessionId, config));

    [McpServerTool(Name = "build_grid_report")]
    [Description("RECIPE: a flexible GRID dashboard - compose ANY visuals in one call. config.visuals is an array of {type, title, span(1-12), rows, category:{table,column}, series:{table,column}, values:[{table,measure}]}; they flow into a 12-column grid that fills the canvas. Supports the full palette: column/bar/line/area/pie/donut/funnel/ribbon/combo + scatter/treemap + tables/cards. The open-ended composer behind the template library. clearPages/pagePrefix to append.")]
    public static string BuildGridReport(ReportService report, string reportSessionId, string config)
        => J.Try(() => report.BuildGridReport(reportSessionId, config));

    [McpServerTool(Name = "list_recipes")]
    [Description("The recipe CATALOG: lists every report template, what it produces, and the config fields that map it to a model. Call this first to pick a template, then call that recipe's tool with a JSON config.")]
    public static string ListRecipes(ReportService report)
        => J.Try(() => report.ListRecipes());

    [McpServerTool(Name = "build_category_report")]
    [Description("RECIPE 2: a beautified multi-page CATEGORY REVIEW (the standard FMCG scan template) in ONE call - Performance, Price & Volume, Share and Distribution pages, branded + themed + nav-linked. Conditionally-formatted Brand matrices, a volume/price combo chart and a segment share breakdown. config = JSON with: title, subtitle, headline, headlineLabel, brandColor, accentColor, logoPath, canvasWidth/canvasHeight, factTable, brandTable, brandColumn, segmentTable, segmentColumn, dateTable, dateColumn, slicers:[{table,column,title}], measures:{sales,volume,price,distribution,growth,usw,salesTrend}.")]
    public static string BuildCategoryReport(ReportService report, string reportSessionId,
        [Description("JSON config mapping the recipe to the client model")] string config)
        => J.Try(() => report.BuildCategoryReport(reportSessionId, config));

    [McpServerTool(Name = "add_kpi_card")]
    [Description("Add a PREMIUM KPI card: a white rounded panel (shadow) with an uppercase label, a big value, an optional coloured delta ('vs LY') and an optional sparkline - composed from proven primitives so it always renders. Far richer than add_card. Pass deltaMeasure for the comparison, and trendMeasure + dateTable/dateColumn for the sparkline.")]
    public static string AddKpiCard(ReportService report, string reportSessionId, string pageName,
        [Description("table that owns the measures")] string table,
        [Description("the headline measure")] string valueMeasure,
        [Description("uppercase label, e.g. SALES")] string label,
        double x = 24, double y = 200, double width = 240, double height = 150,
        [Description("optional delta/comparison measure, e.g. Sales Growth %")] string? deltaMeasure = null,
        [Description("optional table for the sparkline trend measure (defaults to table)")] string? trendTable = null,
        [Description("optional trend measure for the sparkline")] string? trendMeasure = null,
        [Description("date table for the sparkline axis")] string? dateTable = null,
        [Description("date column for the sparkline axis")] string? dateColumn = null,
        [Description("accent colour for the delta + sparkline")] string accentColor = "#1B8A4B",
        [Description("colour for the big value")] string valueColor = "#16365C")
        => J.Try(() => report.AddKpiCard(reportSessionId, pageName, table, valueMeasure, label, x, y, width, height,
            deltaMeasure, trendTable, trendMeasure, dateTable, dateColumn, accentColor, valueColor));

    [McpServerTool(Name = "auto_arrange")]
    [Description("Auto-arrange a page into a clean professional grid in ONE call: header textboxes full-width at top, then a slicer filter-bar, then a KPI-card row, then the data visuals (charts/tables) in a balanced grid that fills the page - consistent margins, gutters and alignment. Add visuals roughly, then call this to snap them into a designed layout. Decorative shapes/images stay put.")]
    public static string AutoArrange(ReportService report, string reportSessionId, string pageName,
        [Description("canvas width (default = page width)")] double? canvasWidth = null,
        [Description("canvas height (default = page height)")] double? canvasHeight = null,
        [Description("outer margin px")] double margin = 24,
        [Description("gap between visuals px")] double gutter = 16,
        [Description("header textbox height px")] double headerHeight = 52,
        [Description("slicer height px")] double slicerHeight = 56,
        [Description("KPI card height px")] double kpiHeight = 110,
        [Description("max data visuals per row (1-4)")] int maxPerRow = 3)
        => J.Try(() => report.AutoArrange(reportSessionId, pageName, canvasWidth, canvasHeight, margin, gutter, headerHeight, slicerHeight, kpiHeight, maxPerRow));

    [McpServerTool(Name = "align_visuals")]
    [Description("Align, distribute or match-size a set of visuals: mode = left|right|top|bottom|centerx|centery|samewidth|sameheight|distributeh|distributev. visualNames = comma-separated visual names (from list_visuals). Precise tidy-up on top of auto_arrange.")]
    public static string AlignVisuals(ReportService report, string reportSessionId, string pageName,
        [Description("comma-separated visual names")] string visualNames,
        [Description("left|right|top|bottom|centerx|centery|samewidth|sameheight|distributeh|distributev")] string mode)
        => J.Try(() => report.AlignVisuals(reportSessionId, pageName,
            visualNames.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(), mode));

    [McpServerTool(Name = "read_theme")]
    [Description("Read the report's current custom theme: name, palette, structural colours, whether it sets visualStyle defaults, plus the full theme JSON to inspect or re-apply.")]
    public static string ReadTheme(ReportService report, string reportSessionId)
        => J.Try(() => report.ReadTheme(reportSessionId));

    [McpServerTool(Name = "generate_theme")]
    [Description("Generate AND (by default) apply a complete professional theme: an 8-colour palette derived from a primaryColor (hex), an explicit colors list, or a named style (executive|vibrant|slate|sunset|forest); structural colours + fonts; AND visualStyle defaults so EVERY visual automatically gets the card look - rounded corners, drop shadow, consistent title font, header hidden. This is the theme-driven 'looks pro' layer; pair it with auto_arrange. Set cardStyle=false for flat visuals, dark=true for a dark theme.")]
    public static string GenerateTheme(ReportService report, string reportSessionId,
        [Description("theme name")] string name = "Custom",
        [Description("primary brand colour hex e.g. #16365C (palette is derived from it)")] string? primaryColor = null,
        [Description("explicit palette as comma-separated hex (overrides primaryColor/style)")] string? colors = null,
        [Description("named style if no primaryColor: executive|vibrant|slate|sunset|forest")] string style = "executive",
        [Description("font family e.g. Segoe UI")] string fontFamily = "Segoe UI",
        [Description("dark theme")] bool dark = false,
        [Description("card corner radius px")] double cornerRadius = 8,
        [Description("drop shadow on every visual")] bool shadow = true,
        [Description("apply the card look (bg/border/shadow) to all visuals via the theme")] bool cardStyle = true,
        [Description("apply to the report now (false = just return the JSON)")] bool apply = true,
        [Description("path to a brand LOGO image (png/jpg) - the palette is extracted from it (overrides primaryColor/colors). The brand kit.")] string? logoPath = null)
        => J.Try(() => report.GenerateTheme(reportSessionId, name, primaryColor, colors, style, fontFamily, dark, cornerRadius, shadow, cardStyle, apply, logoPath));

    [McpServerTool(Name = "modify_theme")]
    [Description("Tweak the report's current custom theme in place: change the palette (primaryColor or colors list), background/foreground, or the card defaults (cornerRadius/shadow/font). Run generate_theme or apply_report_theme first.")]
    public static string ModifyTheme(ReportService report, string reportSessionId,
        [Description("new primary colour hex (re-derives palette)")] string? primaryColor = null,
        [Description("explicit palette as comma-separated hex")] string? colors = null,
        [Description("background hex")] string? background = null,
        [Description("foreground hex")] string? foreground = null,
        [Description("card corner radius px")] double? cornerRadius = null,
        [Description("drop shadow on/off")] bool? shadow = null,
        [Description("title font family")] string? fontFamily = null)
        => J.Try(() => report.ModifyTheme(reportSessionId, primaryColor, colors, background, foreground, cornerRadius, shadow, fontFamily));

    [McpServerTool(Name = "set_visual_property")]
    [Description("Set ANY visual formatting property (the universal escape hatch). objectName/propertyName are Power BI formatting ids e.g. labels/show, legend/position, categoryAxis/showAxisTitle, valueAxis/start, dataPoint/fill. kind = text|number|bool|color|raw. target = objects (data formatting) or vcObjects (container: title, background, border).")]
    public static string SetVisualProperty(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("formatting object id, e.g. labels, legend, categoryAxis, dataPoint, title")] string objectName,
        [Description("property id, e.g. show, position, fontSize, color, labelDisplayUnits")] string propertyName,
        [Description("the value, e.g. true, Top, 12, #16365C")] string value,
        [Description("text|number|bool|color|raw")] string kind = "text",
        [Description("objects|vcObjects")] string target = "objects")
        => J.Try(() => report.SetVisualProperty(reportSessionId, pageName, visualName, objectName, propertyName, value, kind, target));

    // ============================================================================================
    //  OFFLINE REPORT-VISUAL EDIT - one-shot tools that open a CLOSED .pbix, patch Report/Layout and write it
    //  back (Repack; the DataModel is byte-preserved). No open_report/save_report round-trip needed.
    // ============================================================================================

    [McpServerTool(Name = "update_visual_property")]
    [Description("GENERAL offline report-visual editor: open a CLOSED .pbix, set ANY property at a JSON path UNDER a visual's singleVisual config, then write the .pbix back (DataModel preserved). Locate the visual by its config name, displayName, its visible TITLE (e.g. 'Chart Period'), or a bound field (Table.Field / table / field). property_path navigates under singleVisual and creates missing objects/arrays, e.g. 'objects.selection[0].properties.strictSingleSelect', 'drillFilterOtherVisuals', 'display.mode'. valueKind: auto (default = JSON if it parses, else string) | raw | json | string | literal | bool | number | color | text - literal/bool/number/color/text wrap the value as a Power BI formatting literal expr so a formatting-card property can be set by path. Returns the before/after value at that path. The .pbix must NOT be open in Power BI Desktop.")]
    public static string UpdateVisualProperty(ReportService report,
        [Description("absolute path to the .pbix (must be CLOSED in Power BI Desktop)")] string pbix,
        [Description("page name or displayName")] string page,
        [Description("visual to edit: config name, displayName, its visible TITLE, or a bound field")] string visualIdOrTitle,
        [Description("JSON path under singleVisual, e.g. objects.selection[0].properties.strictSingleSelect")] string propertyPath,
        [Description("the value to set")] string value,
        [Description("auto|raw|json|string|literal|bool|number|color|text")] string valueKind = "auto")
        => J.Try(() => report.UpdateVisualPropertyOffline(pbix, page, visualIdOrTitle, propertyPath, value, valueKind));

    [McpServerTool(Name = "set_slicer_selection")]
    [Description("TARGETED offline slicer fix: open a CLOSED .pbix, make a slicer SINGLE-SELECT (and optionally pre-select a default value), then write the .pbix back (DataModel preserved). Single-select writes the ground-truth objects.selection[0].properties.strictSingleSelect = true - Power BI's 'Single select', which forces exactly one item AND auto-picks the first available option when none is selected. THIS is the fix for a field-parameter axis that flat-lines: a field-parameter-bound chart only renders when its slicer is filtered to ONE option. Locate the slicer by its title (e.g. 'Chart Period'), config name, or its bound field (e.g. 'Brand Chart Axis'). default_value (optional) is written as a Categorical selection filter in the slicer's container so it opens on that specific value. The .pbix must NOT be open in Power BI Desktop.")]
    public static string SetSlicerSelection(ReportService report,
        [Description("absolute path to the .pbix (must be CLOSED in Power BI Desktop)")] string pbix,
        [Description("page name or displayName")] string page,
        [Description("the slicer: its title (e.g. 'Chart Period'), config name, or bound field (e.g. 'Brand Chart Axis')")] string slicer,
        [Description("true = single-select (writes strictSingleSelect: forces one item, auto-picks the first if none)")] bool single_select = true,
        [Description("optional default value to pre-select (e.g. 'MonthYear')")] string? default_value = null)
        => J.Try(() => report.SetSlicerSelectionOffline(pbix, page, slicer, single_select, default_value));

    [McpServerTool(Name = "fix_slicer_single_select")]
    [Description("ONE-CALL flat-line fix for a field-parameter-axis chart. Give ONLY the .pbix path and the page name (both are in a request like 'fix the flat line in Category vs Brand Share of <path>') - no slicer name, no boolean, no multi-step. Opens the CLOSED .pbix, finds the slicer bound to the page's FIELD PARAMETER (auto-detected: a slicer whose bound field is a field parameter - table==column, or the single-column-table field plotted on a chart axis), makes it SINGLE-SELECT (writes the ground-truth objects.selection[0].properties.strictSingleSelect=true), then writes the .pbix back (DataModel byte-preserved). THAT is the whole fix: a field-parameter-bound chart flat-lines because every option renders at once; single-select forces one option AND auto-picks the field parameter's first option (e.g. MonthYear) on open. If several field-parameter slicers exist, pass the optional 'chart' (its title/name/bound field) to disambiguate; if you already know a specific value, pass 'default_value' to pin the opening option (otherwise single-select auto-picks the first). Returns {ok, page, slicer, fieldParameter, strictSingleSelect, default_applied, before, after}. The .pbix must NOT be open in Power BI Desktop.")]
    public static string FixSlicerSingleSelect(ReportService report,
        [Description("absolute path to the .pbix (must be CLOSED in Power BI Desktop)")] string pbix,
        [Description("the page: its displayName or name - a partial/loose match is accepted")] string page,
        [Description("optional: a chart on the page (title/name/bound field) to disambiguate WHICH field-parameter slicer to fix")] string? chart = null,
        [Description("optional value to pin as the opening option; omit to let single-select auto-pick the field parameter's first option")] string? default_value = null)
        => J.Try(() => report.FixSlicerSingleSelectOffline(pbix, page, chart, default_value));

    [McpServerTool(Name = "tidy_slicer_layout")]
    [Description("ONE-CALL conservative slicer cleanup for a CLOSED .pbix. Give ONLY the .pbix path (tidies EVERY page) or the .pbix + a page name (tidies that page). It (1) DE-OVERLAPS: any slicer whose bounding box overlaps another slicer is nudged by the SMALLEST clean move that clears the collision - kept near its original spot, on-canvas, aligned to its neighbours, no new overlap; and (2) SNAP-ALIGNS: a slicer whose left or top edge sits a few px off a shared edge is snapped onto it. CONSERVATIVE by design for client reports: only SLICERS ever move, sizes are NEVER changed, and any nudge/snap that would create fresh overlap is rejected. Slicer-over-NON-slicer overlaps (e.g. a State slicer deliberately layered over a wide date-label card) are FLAGGED, not moved, unless deOverlapNonSlicers=true. Writes the .pbix back offline (Report/Layout patched, DataModel byte-preserved). The .pbix must NOT be open in Power BI Desktop. Returns { ok, pagesScanned, pagesTidied, moveCount, moves:[{page,slicer,reason,before{x,y,w,h},after{x,y,w,h}}], flaggedCount, flagged, persistedToDisk }.")]
    public static string TidySlicerLayout(ReportService report,
        [Description("absolute path to the .pbix (must be CLOSED in Power BI Desktop)")] string pbix,
        [Description("optional: a single page (name/displayName, loose match) to tidy; omit to tidy ALL pages")] string? page = null,
        [Description("also nudge slicers off NON-slicer visuals (default false: those overlaps are flagged, not moved, since they are often a deliberate header layering)")] bool deOverlapNonSlicers = false)
        => J.Try(() => report.TidySlicerLayoutOffline(pbix, page, deOverlapNonSlicers));

    [McpServerTool(Name = "match_slicer_layout")]
    [Description("Learn the slicer ROW STRUCTURE (row count, per-row y, height, left start, horizontal gap) from hand-fixed REFERENCE pages, then re-lay the slicers on TARGET pages onto that structure. Median-based inference tolerates reference noise (a slicer nudged off-canvas, a top row whose y drifts a few px between pages) - structure is INFERRED, never copied pixel-for-pixel. Slicers only: non-slicer visuals are never touched; widths are preserved (they shrink proportionally only when a row physically cannot fit the canvas). Writes the .pbix back offline (Report/Layout patched, DataModel byte-preserved). The .pbix must NOT be open in Power BI Desktop. Returns { ok, learned{referencePages,rows:[{row,y,height,leftStart,gap,samples}],rowPitch}, pagesMatched, totalMoves, pages:[{page,rowsDetected,moves:[{slicer,row,before,after}]}], persistedToDisk }.")]
    public static string MatchSlicerLayout(ReportService report,
        [Description("absolute path to the .pbix (must be CLOSED in Power BI Desktop)")] string pbix,
        [Description("comma-separated REFERENCE pages to learn from: ordinals (e.g. '0,1,2,3') and/or page names/displayNames")] string reference_pages,
        [Description("comma-separated TARGET pages to re-lay; omit (or 'all') for every OTHER slicer-bearing page")] string? targets = null)
        => J.Try(() => report.MatchSlicerLayoutOffline(pbix, reference_pages, targets));

    [McpServerTool(Name = "tidy_slicer_layout_v2")]
    [Description("Row-aware slicer tidy (v2): cluster a page's slicers into ROWS, top-align each row onto one shared baseline y (the row median), pull off-canvas slicers back onto the canvas, and re-pack each row with EVEN-GAP spacing (one uniform gap = the median of the row's existing gaps, squeezed only as needed to stay on-canvas), which also clears within-row overlaps. Positions only (sizes NEVER change), slicers only (non-slicer visuals never move); a row wider than the canvas even packed flush is FLAGGED, not forced. Writes the .pbix back offline (Report/Layout patched, DataModel byte-preserved). The .pbix must NOT be open in Power BI Desktop. Returns { ok, rows:[{page,row,y,members,aligned}], moveCount, moves:[{page,slicer,row,reason,before,after}], flaggedCount, flagged, persistedToDisk }.")]
    public static string TidySlicerLayoutV2(ReportService report,
        [Description("absolute path to the .pbix (must be CLOSED in Power BI Desktop)")] string pbix,
        [Description("optional: a single page (name/displayName, loose match) to tidy; omit to tidy ALL pages")] string? page = null)
        => J.Try(() => report.TidySlicerLayoutV2Offline(pbix, page));

    [McpServerTool(Name = "move_visual")]
    [Description("Move an existing visual to new x/y (and optional z order).")]
    public static string MoveVisual(ReportService report, string reportSessionId, string pageName, string visualName,
        double? x = null, double? y = null, double? z = null)
        => J.Try(() => report.SetVisualBounds(reportSessionId, pageName, visualName, x, y, z, null, null));

    [McpServerTool(Name = "resize_visual")]
    [Description("Resize an existing visual to new width/height.")]
    public static string ResizeVisual(ReportService report, string reportSessionId, string pageName, string visualName,
        double? width = null, double? height = null)
        => J.Try(() => report.SetVisualBounds(reportSessionId, pageName, visualName, null, null, null, width, height));

    [McpServerTool(Name = "set_data_labels")]
    [Description("Show/hide data labels on a chart, with optional display units (None|Thousands|Millions|Billions|Auto) and decimal places.")]
    public static string SetDataLabels(ReportService report, string reportSessionId, string pageName, string visualName,
        bool show = true, string? displayUnits = null, int? decimals = null)
        => J.Try(() =>
        {
            report.SetVisualProperty(reportSessionId, pageName, visualName, "labels", "show", show ? "true" : "false", "bool", "objects");
            if (displayUnits != null) report.SetVisualProperty(reportSessionId, pageName, visualName, "labels", "labelDisplayUnits", DisplayUnitCode(displayUnits), "raw", "objects");
            if (decimals != null) report.SetVisualProperty(reportSessionId, pageName, visualName, "labels", "labelPrecision", decimals.Value.ToString(), "number", "objects");
            return new { ok = true, visualName, dataLabels = show };
        });

    [McpServerTool(Name = "set_legend")]
    [Description("Show/hide a chart legend and set its position (Top|Bottom|Left|Right|TopCenter|... ).")]
    public static string SetLegend(ReportService report, string reportSessionId, string pageName, string visualName,
        bool show = true, string position = "Top")
        => J.Try(() =>
        {
            report.SetVisualProperty(reportSessionId, pageName, visualName, "legend", "show", show ? "true" : "false", "bool", "objects");
            if (show) report.SetVisualProperty(reportSessionId, pageName, visualName, "legend", "position", position, "text", "objects");
            return new { ok = true, visualName, legend = show, position };
        });

    [McpServerTool(Name = "set_axis")]
    [Description("Configure a chart axis. axis = x (category) or y (value): toggle the axis and its title, set the value-axis start/end, display units (None|Thousands|Millions|Billions|Auto), and gridlines.")]
    public static string SetAxis(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("x (category) | y (value)")] string axis = "y",
        bool? show = null, [Description("show the axis title")] bool? showTitle = null,
        [Description("value-axis min")] double? start = null, [Description("value-axis max")] double? end = null,
        [Description("None|Thousands|Millions|Billions|Auto")] string? displayUnits = null,
        bool? gridlines = null)
        => J.Try(() =>
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string obj = axis.Equals("x", StringComparison.OrdinalIgnoreCase) ? "categoryAxis" : "valueAxis";
            void P(string prop, string val, string kind) => report.SetVisualProperty(reportSessionId, pageName, visualName, obj, prop, val, kind, "objects");
            if (show != null) P("show", show.Value ? "true" : "false", "bool");
            if (showTitle != null) P("showAxisTitle", showTitle.Value ? "true" : "false", "bool");
            if (start != null) P("start", start.Value.ToString(inv), "number");
            if (end != null) P("end", end.Value.ToString(inv), "number");
            if (displayUnits != null) P("labelDisplayUnits", DisplayUnitCode(displayUnits), "raw");
            if (gridlines != null) P("gridlineShow", gridlines.Value ? "true" : "false", "bool");
            return new { ok = true, visualName, axis = obj };
        });

    [McpServerTool(Name = "set_data_color")]
    [Description("Set a chart's data colour (the default series fill). For a branded single-measure chart, point this at a theme colour.")]
    public static string SetDataColor(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("hex colour e.g. #16365C")] string color)
        => J.Try(() =>
        {
            report.SetVisualProperty(reportSessionId, pageName, visualName, "dataPoint", "defaultColor", color, "color", "objects");
            return new { ok = true, visualName, color };
        });

    [McpServerTool(Name = "format_title")]
    [Description("Format a visual's title: text, colour, font size, alignment (left|center|right), show/hide.")]
    public static string FormatTitle(ReportService report, string reportSessionId, string pageName, string visualName,
        string? text = null, [Description("hex colour")] string? color = null, double? size = null,
        [Description("left|center|right")] string? align = null, bool? show = null)
        => J.Try(() =>
        {
            void P(string prop, string val, string kind) => report.SetVisualProperty(reportSessionId, pageName, visualName, "title", prop, val, kind, "vcObjects");
            if (show != null) P("show", show.Value ? "true" : "false", "bool");
            if (text != null) P("text", text, "text");
            if (color != null) P("fontColor", color, "color");
            if (size != null) P("fontSize", size.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), "number");
            if (align != null) P("alignment", align, "text");
            return new { ok = true, visualName };
        });

    [McpServerTool(Name = "set_totals")]
    [Description("Show/hide totals on a table and subtotals on a matrix.")]
    public static string SetTotals(ReportService report, string reportSessionId, string pageName, string visualName, bool show = true)
        => J.Try(() =>
        {
            string v = show ? "true" : "false";
            report.SetVisualProperty(reportSessionId, pageName, visualName, "total", "show", v, "bool", "objects");
            report.SetVisualProperty(reportSessionId, pageName, visualName, "subTotals", "show", v, "bool", "objects");
            return new { ok = true, visualName, totals = show };
        });

    [McpServerTool(Name = "set_color_scale")]
    [Description("Apply a GRADIENT colour scale to a visual property driven by a measure - the signature enterprise feature. e.g. colour a table value's background light->dark by [Total Sales]. objectName/propertyName presets: dataPoint/fill (chart bars/columns), values/backColor (table/matrix cell background), values/fontColor (text colour). Pass a centerColor for a 3-colour (diverging) scale. Data min/max is auto-computed. For DISCRETE colour bands by value range instead, use set_conditional_formatting.")]
    public static string SetColorScale(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the colour")] string measure,
        [Description("hex colour for the low end")] string minColor = "#EAF1F7",
        [Description("hex colour for the high end")] string maxColor = "#16365C",
        [Description("optional middle colour for a diverging scale")] string? centerColor = null,
        [Description("dataPoint | values | (any formatting object)")] string objectName = "dataPoint",
        [Description("fill | backColor | fontColor")] string propertyName = "fill",
        [Description("explicit data value for the low colour (omit for auto)")] double? minValue = null,
        [Description("explicit data value for the high colour (omit for auto)")] double? maxValue = null,
        [Description("explicit data value for the middle colour")] double? midValue = null,
        [Description("for a TABLE/MATRIX column: the column's queryRef e.g. 'Dim_Product.Brand' so the colour scale targets that column's background. Omit for charts.")] string? metadata = null)
        => J.Try(() => report.SetConditionalFormatting(reportSessionId, pageName, visualName, objectName, propertyName, measureTable, measure, minColor, maxColor, centerColor, minValue, maxValue, midValue, metadata));

    private static string DisplayUnitCode(string u) => u.ToLowerInvariant() switch
    {
        "none" => "0", "auto" => "0", "thousands" => "1000D", "millions" => "1000000D",
        "billions" => "1000000000D", "trillions" => "1000000000000D", _ => "0",
    };

    [McpServerTool(Name = "set_visual_sort")]
    [Description("Set the SORT ORDER of an EXISTING visual (table/matrix/chart): order its query by a field, asc or desc. kind=column|measure. Use descending=true to sort a ranking by sales/value so the top performers lead and blank-rank/zero rows fall to the bottom (the fix for 'blanks at the top'). The field should be one already shown in the visual.")]
    public static string SetVisualSort(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("table that owns the sort field")] string table,
        [Description("field (column or measure) to sort by")] string field,
        [Description("column|measure")] string kind = "measure",
        [Description("true = descending (largest first), false = ascending")] bool descending = true)
        => J.Try(() => report.SetVisualSort(reportSessionId, pageName, visualName, table, field, kind, descending));

    [McpServerTool(Name = "add_visual_filter")]
    [Description("Add a VISUAL-LEVEL filter to an existing visual. op = gt|gte|lt|lte|eq|ne|isblank|isnotblank. kind=column|measure. The classic use: exclude discontinued/zero rows from a ranking table, e.g. op=isnotblank on 'Sales 52W TY' (or op=gt value=0), so -100%/blank-rank SKUs stop cluttering the top. valueType=int|decimal|string (default decimal) for comparison values.")]
    public static string AddVisualFilter(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("table that owns the filter field")] string table,
        [Description("field (column or measure) to filter on")] string field,
        [Description("column|measure")] string kind = "measure",
        [Description("gt|gte|lt|lte|eq|ne|isblank|isnotblank")] string op = "isnotblank",
        [Description("comparison value (ignored for isblank/isnotblank)")] string? value = null,
        [Description("int|decimal|string")] string valueType = "decimal")
        => J.Try(() => report.AddVisualFilter(reportSessionId, pageName, visualName, table, field, kind, op, value, valueType));

    [McpServerTool(Name = "set_visual_fields")]
    [Description("Replace the field bindings of an EXISTING visual - change which columns/measures a table/matrix/chart shows WITHOUT deleting it (preserves position, formatting, conditional formatting). bindings = JSON array of {role,table,field,kind}. Roles: tableEx/card=Values; matrix/pivotTable=Rows/Columns/Values; charts=Category/Y/Y2/Series/X/Size. Clears the sort - re-apply with set_visual_sort.")]
    public static string SetVisualFields(ReportService report, string reportSessionId, string pageName, string visualName,
        [Description("JSON array of {role,table,field,kind}")] string bindings)
        => J.Try(() => report.SetVisualFields(reportSessionId, pageName, visualName, Parse(bindings, "Values")));

    [McpServerTool(Name = "delete_visual")]
    [Description("Delete a visual from a page by its visual name.")]
    public static string DeleteVisual(ReportService report, string reportSessionId, string pageName, string visualName)
        => J.Try(() => report.DeleteVisual(reportSessionId, pageName, visualName));

    // ============================================================================================
    //  UNIVERSAL VISUAL FORMATTING - total customizability: read/set ANY formatting property on ANY visual.
    // ============================================================================================

    [McpServerTool(Name = "get_visual_format")]
    [Description("Read the DECODED, simplified current formatting of one visual (READ-ONLY). Returns its type, position {x,y,w,h}, the decoded vcObjects {object:{property:value}} (container: title, background, border, ...), the decoded objects {object:{property:value}} (visual-specific: labels, legend, categoryAxis, ...) and its field bindings. Use this to inspect before set_visual_format. page/visual resolve by name or displayName.")]
    public static string GetVisualFormat(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name or displayName")] string visual)
        => J.Try(() => report.GetVisualFormat(reportSessionId, page, visual));

    [McpServerTool(Name = "set_visual_format")]
    [Description("Set ANY formatting on a visual (TOTAL customizability) - the universal styling engine. formatJson e.g. { \"vcObjects\": { \"title\": {\"show\":true,\"text\":\"My Title\"}, \"background\": {\"show\":true,\"color\":\"#FFFFFF\",\"transparency\":100} }, \"objects\": { \"legend\": {\"show\":false} } }. Each property is encoded by kind (show->bool, text->text, transparency/fontSize->number, color/fontColor->colour, alignment/position->enum; otherwise inferred: boolean->bool, number->number, #RRGGBB->colour, else text) and MERGED in - untouched objects/properties and any selector are preserved. Returns the objects/properties changed.")]
    public static string SetVisualFormat(ReportService report, PropertyCatalog catalog, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name or displayName")] string visual,
        [Description("JSON: { vcObjects:{obj:{prop:value}}, objects:{obj:{prop:value}} }")] string formatJson,
        [Description("when true, REJECT (do not apply) if validation finds unknown cards/properties or type mismatches; default false (apply + warn)")] bool strict = false)
        => J.Try(() =>
        {
            var (warnings, known) = ValidateAgainstCatalog(report, catalog, reportSessionId, page, visual, formatJson);
            if (strict && warnings.Count > 0)
                return new { ok = false, error = "strict validation failed", warnings, warningCount = warnings.Count, applied = false };
            var result = report.SetVisualFormat(reportSessionId, page, visual, formatJson);
            return WithWarnings(result, warnings, known);
        });

    /// <summary>Resolve the visual's type and validate a formatJson against the registry. Never throws -
    /// a resolution/parse hiccup yields an empty (advisory) warning set so formatting still proceeds.</summary>
    private static (List<string> warnings, bool known) ValidateAgainstCatalog(
        ReportService report, PropertyCatalog catalog, string reportSessionId, string page, string visual, string formatJson)
    {
        try
        {
            var got = report.GetVisualFormat(reportSessionId, page, visual);
            string? type = JsonSerializer.SerializeToNode(got)?["type"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(type) || !catalog.Knows(type)) return (new List<string>(), false);
            var r = catalog.Validate(type, formatJson);
            return (r.Warnings.ToList(), !r.UnknownVisualType);
        }
        catch { return (new List<string>(), false); }
    }

    /// <summary>Fold a non-fatal validation-warnings list into a tool result object.</summary>
    private static object WithWarnings(object result, List<string> warnings, bool known)
    {
        var node = JsonSerializer.SerializeToNode(result) as JsonObject ?? new JsonObject();
        node["validated"] = known;
        node["warnings"] = JsonSerializer.SerializeToNode(warnings);
        node["warningCount"] = warnings.Count;
        return node;
    }

    [McpServerTool(Name = "set_visual_title")]
    [Description("Set a visual's TITLE (typed convenience over set_visual_format). All params after page/visual are optional - set only what you want. Preserve a custom title by passing show=false on an overlay, or set custom text. Use this to restore a title clear_visual_styling must never destroy.")]
    public static string SetVisualTitle(ReportService report, string reportSessionId, string page, string visual,
        bool? show = null, string? text = null,
        [Description("hex colour e.g. #16365C")] string? fontColor = null,
        [Description("left|center|right")] string? alignment = null,
        double? fontSize = null)
        => J.Try(() => report.SetVisualTitle(reportSessionId, page, visual, show, text, fontColor, alignment, fontSize));

    [McpServerTool(Name = "set_visual_background")]
    [Description("Set a visual's BACKGROUND (typed convenience over set_visual_format). show/color/transparency all optional. transparency 0 = opaque, 100 = fully transparent (an OVERLAY background, which clear_visual_styling preserves).")]
    public static string SetVisualBackground(ReportService report, string reportSessionId, string page, string visual,
        bool? show = null,
        [Description("hex colour e.g. #FFFFFF")] string? color = null,
        [Description("0 = opaque, 100 = fully transparent")] double? transparency = null)
        => J.Try(() => report.SetVisualBackground(reportSessionId, page, visual, show, color, transparency));

    [McpServerTool(Name = "set_visual_border")]
    [Description("Set a visual's BORDER (typed convenience over set_visual_format). show/color/radius all optional.")]
    public static string SetVisualBorder(ReportService report, string reportSessionId, string page, string visual,
        bool? show = null,
        [Description("hex colour e.g. #E6E9EF")] string? color = null,
        [Description("corner radius in px")] double? radius = null)
        => J.Try(() => report.SetVisualBorder(reportSessionId, page, visual, show, color, radius));

    [McpServerTool(Name = "set_visual_position")]
    [Description("Set a visual's POSITION/size. Updates BOTH the visualContainer x/y/width/height AND the config layouts[0].position so the move sticks. All of x/y/width/height optional.")]
    public static string SetVisualPosition(ReportService report, string reportSessionId, string page, string visual,
        double? x = null, double? y = null, double? width = null, double? height = null)
        => J.Try(() => report.SetVisualPosition(reportSessionId, page, visual, x, y, width, height));

    // ============================================================================================
    //  REPORT-BUILDING: clone pages/visuals, report-level bookmarks, a bookmark-bound button, and
    //  discrete rule-based conditional formatting - so a dashboard can be reproduced through the engine.
    // ============================================================================================

    [McpServerTool(Name = "clone_page")]
    [Description("Deep-clone a report page: copies the whole section, assigns a fresh unique section name and a new ordinal at the end, sets newDisplayName, and regenerates EVERY visual's id so ids stay globally unique across the report. Returns the new page name.")]
    public static string ClonePage(ReportService report, string reportSessionId,
        [Description("source page name or displayName to clone")] string sourcePage,
        [Description("tab title for the new page")] string newDisplayName)
        => J.Try(() => report.ClonePage(reportSessionId, sourcePage, newDisplayName));

    [McpServerTool(Name = "clone_visual")]
    [Description("Deep-clone one visual (with a fresh unique id) onto its page or onto targetPage. On the same page the copy is nudged so it is visible. Returns the new visual id.")]
    public static string CloneVisual(ReportService report, string reportSessionId,
        [Description("page name or displayName the visual is on")] string page,
        [Description("visual name to clone")] string visual,
        [Description("destination page (omit = same page)")] string? targetPage = null)
        => J.Try(() => report.CloneVisual(reportSessionId, page, visual, targetPage));

    [McpServerTool(Name = "list_bookmarks")]
    [Description("List the report's bookmarks: each one's name, displayName, active page, and the visual ids it hides.")]
    public static string ListBookmarks(ReportService report, string reportSessionId)
        => J.Try(() => report.ListBookmarks(reportSessionId));

    [McpServerTool(Name = "add_bookmark")]
    [Description("Add a report-level bookmark that captures a page's visualContainers with the listed visuals HIDDEN and the rest shown (the view-state pattern). Names it 'Bookmark'+16 hex. hiddenVisuals = comma-separated visual ids (from list_visuals). Returns the new bookmark name. Wire it to a button with add_action_button.")]
    public static string AddBookmark(ReportService report, string reportSessionId,
        [Description("page name or displayName the bookmark captures")] string page,
        [Description("bookmark title shown in the pane")] string displayName,
        [Description("comma-separated visual ids to hide (the rest are shown)")] string hiddenVisuals = "",
        [Description("also capture the live DATA state (filter/slicer values, sort, drill, cross-highlight) - not just which visuals show")] bool captureData = false)
        => J.Try(() => report.AddBookmark(reportSessionId, page, displayName, SplitIds(hiddenVisuals), captureData));

    [McpServerTool(Name = "update_bookmark")]
    [Description("Re-set which visuals a bookmark hides: listed visuals become hidden, all other recorded visuals become shown. hiddenVisuals = comma-separated visual ids.")]
    public static string UpdateBookmark(ReportService report, string reportSessionId,
        [Description("bookmark name or displayName")] string name,
        [Description("comma-separated visual ids to hide")] string hiddenVisuals = "",
        [Description("re-capture the live DATA state (filter/slicer values, sort, drill, cross-highlight) from the bookmark's page")] bool captureData = false)
        => J.Try(() => report.UpdateBookmark(reportSessionId, name, SplitIds(hiddenVisuals), captureData));

    [McpServerTool(Name = "delete_bookmark")]
    [Description("Delete a report-level bookmark by name or displayName.")]
    public static string DeleteBookmark(ReportService report, string reportSessionId,
        [Description("bookmark name or displayName")] string name)
        => J.Try(() => report.DeleteBookmark(reportSessionId, name));

    [McpServerTool(Name = "add_action_button")]
    [Description("Add a button bound to a BOOKMARK (the view-switch pattern): clicking it applies the bookmark (e.g. swap which visuals show). Writes the exact visualLink {type='Bookmark', bookmark=<name>} shape the live product uses, plus the button label. bookmarkName = a bookmark from list_bookmarks/add_bookmark. Returns the new visual id.")]
    public static string AddActionButton(ReportService report, string reportSessionId,
        [Description("page name or displayName to place the button on")] string page,
        [Description("button label")] string text,
        [Description("the bookmark name the button applies")] string bookmarkName,
        double x = 24, double y = 124, double width = 150, double height = 40)
        => J.Try(() => report.AddActionButton(reportSessionId, page, text, bookmarkName, x, y, width, height));

    [McpServerTool(Name = "set_conditional_formatting")]
    [Description("Apply DISCRETE rule-based BACKGROUND colour bands to a measure column in a table/matrix: each value in [min, max) renders its band colour. rules = JSON array of {min, max, color} (e.g. positive band #C6EFCE, negative band #e68f96). Writes the standard Power BI rule-based conditional-formatting structure (RuleDefinition rules with a >=min AND <max condition per band), NOT a gradient. For a smooth colour scale instead, use set_color_scale.")]
    public static string SetConditionalFormatting(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("table/matrix visual name")] string visual,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the colour")] string measure,
        [Description("JSON array of {min, max, color} bands")] string rules,
        [Description("formatting object (default values for table/matrix)")] string objectName = "values",
        [Description("formatting property (default backColor)")] string propertyName = "backColor")
        => J.Try(() => report.SetConditionalFormattingRules(reportSessionId, page, visual, measureTable, measure,
            ParseRules(rules), objectName, propertyName));

    private record RuleIn(double min, double max, string? color);

    private static List<(double, double, string)> ParseRules(string json)
    {
        var items = JsonSerializer.Deserialize<List<RuleIn>>(json, Ci)
                    ?? throw new ArgumentException("rules JSON did not parse to an array of {min,max,color}.");
        return items.Select(r => (r.min, r.max, r.color
            ?? throw new ArgumentException("each rule needs a 'color' (e.g. \"#C6EFCE\")."))).ToList();
    }

    // ============================================================================================
    //  STRUCTURE & ANALYTICS - group/ungroup, z-order (front/back), page type & visual tooltip page,
    //  analytics reference lines, and conditional-format variants (font colour, data bars, icons).
    // ============================================================================================

    [McpServerTool(Name = "group_visuals")]
    [Description("GROUP a set of visuals into a single group (like Desktop's right-click > Group). Creates a group container and stamps each child visual with parentGroupName. visualNames = comma-separated visual names (from list_visuals) - at least two. groupName is the optional display name shown in the selection pane. Returns the new group name.")]
    public static string GroupVisuals(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("comma-separated visual names to group (at least two)")] string visualNames,
        [Description("optional group display name")] string? groupName = null)
        => J.Try(() => report.GroupVisuals(reportSessionId, page,
            visualNames.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(), groupName));

    [McpServerTool(Name = "ungroup_visuals")]
    [Description("UNGROUP a group: clears parentGroupName from every child and removes the group container. groupName = the group name returned by group_visuals.")]
    public static string UngroupVisuals(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("the group name (from group_visuals)")] string groupName)
        => J.Try(() => report.UngroupVisuals(reportSessionId, page, groupName));

    [McpServerTool(Name = "set_visual_z_order")]
    [Description("Set a visual's z (stack) order explicitly. Updates BOTH the container z and the config layouts position so the order sticks. Higher z renders on top.")]
    public static string SetVisualZOrder(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name")] string visual,
        [Description("z order (higher = on top)")] double z)
        => J.Try(() => report.SetVisualZOrder(reportSessionId, page, visual, z));

    [McpServerTool(Name = "bring_to_front")]
    [Description("Bring a visual to the FRONT of the page (z = current max + 1) so it renders on top of everything else.")]
    public static string BringToFront(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name")] string visual)
        => J.Try(() => report.BringToFront(reportSessionId, page, visual));

    [McpServerTool(Name = "send_to_back")]
    [Description("Send a visual to the BACK of the page (z = current min - 1) - e.g. push a decorative panel behind the data visuals.")]
    public static string SendToBack(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name")] string visual)
        => J.Try(() => report.SendToBack(reportSessionId, page, visual));

    [McpServerTool(Name = "set_page_type")]
    [Description("Set a page's TYPE: standard | tooltip | drillthrough. tooltip sets the small tooltip canvas (320x240) and flags the page as a report-page tooltip; drillthrough flags it as a drill-through target. Pair tooltip with set_visual_tooltip_page. Verify the render in Desktop.")]
    public static string SetPageType(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("standard | tooltip | drillthrough")] string type = "standard")
        => J.Try(() => report.SetPageType(reportSessionId, page, type));

    [McpServerTool(Name = "set_visual_tooltip_page")]
    [Description("Point a visual's hover TOOLTIP at a report-page tooltip (instead of the default data tooltip). Writes the visualTooltip {type='ReportPage', section=<tooltip page>} on the visual. The tooltip page should already be flagged with set_page_type tooltip. Verify the render in Desktop.")]
    public static string SetVisualTooltipPage(ReportService report, string reportSessionId,
        [Description("page name or displayName the visual is on")] string page,
        [Description("visual name")] string visual,
        [Description("the tooltip page (name or displayName)")] string tooltipPage)
        => J.Try(() => report.SetVisualTooltipPage(reportSessionId, page, visual, tooltipPage));

    [McpServerTool(Name = "add_analytics_line")]
    [Description("Add an ANALYTICS reference line to a chart. kind = constant | min | max | average | median | trend | forecast. constant needs value; min/max/average/median compute from measureTable+measure; trend/forecast need no value. Optional label (data-label text on the line) and color (hex). Structure matched to the legacy analytics shape - verify the render in Desktop.")]
    public static string AddAnalyticsLine(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("chart visual name")] string visual,
        [Description("constant | min | max | average | median | trend | forecast")] string kind,
        [Description("value for a constant line")] double? value = null,
        [Description("table that owns the measure (for min/max/average/median/forecast)")] string? measureTable = null,
        [Description("measure that drives the line")] string? measure = null,
        [Description("optional line label")] string? label = null,
        [Description("line colour hex e.g. #E81123")] string? color = null)
        => J.Try(() => report.AddAnalyticsLine(reportSessionId, page, visual, kind, value, measureTable, measure, label, color));

    [McpServerTool(Name = "set_font_color_rules")]
    [Description("Apply DISCRETE rule-based FONT-COLOUR bands to a measure column in a table/matrix: each value in [min, max) renders its band text colour. Same rule structure as set_conditional_formatting, applied to the text colour instead of the background. rules = JSON array of {min, max, color}.")]
    public static string SetFontColorRules(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("table/matrix visual name")] string visual,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the colour")] string measure,
        [Description("JSON array of {min, max, color} bands")] string rules)
        => J.Try(() => report.SetFontColorRules(reportSessionId, page, visual, measureTable, measure, ParseRules(rules)));

    [McpServerTool(Name = "set_data_bars")]
    [Description("Add DATA BARS to a measure column in a table/matrix: an in-cell bar whose length tracks the measure, with a positive colour and an optional negative colour. reverseDirection draws bars right-to-left; hideText shows the bar only (no value text); axisColor sets the zero-axis colour; minValue/maxValue pin the bar scale (omit for auto - the default fixes the old min==max bug where bars never rendered). Verify the render in Desktop.")]
    public static string SetDataBars(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("table/matrix visual name")] string visual,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the bars")] string measure,
        [Description("positive bar colour hex e.g. #1B8A4B")] string positiveColor,
        [Description("optional negative bar colour hex e.g. #E81123")] string? negativeColor = null,
        [Description("draw bars right-to-left")] bool reverseDirection = false,
        [Description("show the bar only, hide the value text")] bool hideText = false,
        [Description("zero-axis colour hex (default #000000)")] string? axisColor = null,
        [Description("explicit minimum bar value (omit for auto)")] double? minValue = null,
        [Description("explicit maximum bar value (omit for auto)")] double? maxValue = null)
        => J.Try(() => report.SetDataBars(reportSessionId, page, visual, measureTable, measure, positiveColor, negativeColor,
            reverseDirection, hideText, axisColor, minValue, maxValue));

    [McpServerTool(Name = "set_icon_rules")]
    [Description("Apply DISCRETE rule-based ICONS to a measure column in a table/matrix: each value in [min, max) maps to an icon. rules = JSON array of {min, max, color}. glyphs = optional comma-separated icon glyph names (one per band, e.g. ArrowDown,ArrowSideways,ArrowUp) - when given, each band uses that glyph instead of being colour-keyed. iconSet picks the glyph family (e.g. ThreeArrowsColored, ThreeFlags, ThreeTrafficLights1). layout = left|right|icon-only (where the icon sits relative to the value). Verify in Desktop.")]
    public static string SetIconRules(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("table/matrix visual name")] string visual,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the icons")] string measure,
        [Description("JSON array of {min, max, color} bands")] string rules,
        [Description("optional comma-separated glyph names, one per band")] string? glyphs = null,
        [Description("optional icon-set family, e.g. ThreeArrowsColored")] string? iconSet = null,
        [Description("left | right | icon-only")] string? layout = null)
        => J.Try(() => report.SetIconRules(reportSessionId, page, visual, measureTable, measure, ParseRules(rules),
            string.IsNullOrWhiteSpace(glyphs) ? null : SplitIds(glyphs), iconSet, layout));

    // ============================================================================================
    //  WAVE M - STRUCTURED / DATA-BOUND VISUAL BUILDERS (plot-area/card images, rich textbox, gradient &
    //  map conditional formatting, azure/shape map sources, error bars, anomaly detection, full forecast,
    //  play axis, slicer CF). Each writes pre-shaped structured PBI JSON - verify the render in Desktop.
    // ============================================================================================

    [McpServerTool(Name = "set_plot_area_image")]
    [Description("Set a chart's PLOT-AREA background image (the plotArea image object). imageUrlOrPath = a local file (embedded into the report) or an http(s)/data URL. scaling = Fit|Fill|Normal. transparency 0-100 optional. Verify the render in Desktop.")]
    public static string SetPlotAreaImage(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("chart visual name")] string visual,
        [Description("local image file or http(s)/data URL")] string imageUrlOrPath,
        [Description("Fit|Fill|Normal")] string scaling = "Fit",
        [Description("0-100 image transparency")] double? transparency = null)
        => J.Try(() => report.SetPlotAreaImage(reportSessionId, page, visual, imageUrlOrPath, scaling, transparency));

    [McpServerTool(Name = "set_image_source")]
    [Description("Re-source an EXISTING image visual: rewrite its sourceFile to a new image (add_image only creates new ones). imageUrlOrPath = a local file (embedded) or an http(s)/data URL. Preserves the visual's current scaling.")]
    public static string SetImageSource(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("the image visual's name")] string visual,
        [Description("local image file or http(s)/data URL")] string imageUrlOrPath)
        => J.Try(() => report.SetImageSource(reportSessionId, page, visual, imageUrlOrPath));

    [McpServerTool(Name = "set_textbox_content")]
    [Description("Set RICH multi-run text on a textbox (add_textbox is single-run). runs = JSON array of {text, fontFamily?, fontSize?, color?, bold?, italic?, url?} - each becomes a styled run on one paragraph; url makes a run a hyperlink. bulleted=true renders the paragraph as a bullet. Replaces the textbox's current content.")]
    public static string SetTextboxContent(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("the textbox visual's name")] string visual,
        [Description("JSON array of {text, fontFamily?, fontSize?, color?, bold?, italic?, url?}")] string runs,
        [Description("render as a bulleted paragraph")] bool bulleted = false)
        => J.Try(() =>
        {
            var arr = JsonNode.Parse(runs) as JsonArray ?? throw new ArgumentException("runs must be a JSON array of {text,...}.");
            return report.SetTextboxContent(reportSessionId, page, visual, arr, bulleted);
        });

    [McpServerTool(Name = "set_gradient_color")]
    [Description("Set a measure-driven GRADIENT-STOP saturation colour on a card that set_color_scale does not target (treemap/funnel/map dataPoint fill). minColor/maxColor (and optional centerColor for a 3-stop ramp) drive the fill by the measure; min/center/max set explicit stop values. card defaults to dataPoint.")]
    public static string SetGradientColor(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (treemap/funnel/map)")] string visual,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the colour")] string measure,
        [Description("low-end colour hex")] string minColor,
        [Description("high-end colour hex")] string maxColor,
        [Description("optional centre colour hex (3-stop)")] string? centerColor = null,
        [Description("formatting card (default dataPoint)")] string card = "dataPoint",
        double? min = null, double? center = null, double? max = null)
        => J.Try(() => report.SetGradientColor(reportSessionId, page, visual, card, measureTable, measure,
            minColor, maxColor, centerColor, min, center, max));

    [McpServerTool(Name = "set_map_conditional_formatting")]
    [Description("Set measure-driven conditional FILL on a filledMap/azureMap filled layer (dataPoint fillColor). Pass rules = JSON array of {min,max,color} bands for discrete CF, OR minColor+maxColor (and optional centerColor) for a gradient. target defaults to fill. Verify the render in Desktop.")]
    public static string SetMapConditionalFormatting(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("filled-map visual name")] string visual,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the colour")] string measure,
        [Description("JSON array of {min,max,color} bands (discrete); omit for gradient")] string? rules = null,
        [Description("gradient low colour hex (use with maxColor)")] string? minColor = null,
        [Description("gradient high colour hex")] string? maxColor = null,
        [Description("gradient centre colour hex (3-stop)")] string? centerColor = null,
        [Description("fill (default)")] string target = "fill")
        => J.Try(() =>
        {
            List<(double, double, string)>? r = string.IsNullOrWhiteSpace(rules) ? null : ParseRules(rules!);
            (string, string, string?, double?, double?, double?)? grad =
                (!string.IsNullOrWhiteSpace(minColor) && !string.IsNullOrWhiteSpace(maxColor))
                    ? (minColor!, maxColor!, centerColor, (double?)null, (double?)null, (double?)null) : null;
            return report.SetMapConditionalFormatting(reportSessionId, page, visual, measureTable, measure, r, grad, target);
        });

    [McpServerTool(Name = "set_azuremap_layer_source")]
    [Description("Set an azureMap data layer source. layer=reference: a reference layer from a local .json/.geojson file (embedded), an http(s) URL, or an inline GeoJSON string. layer=tile: a custom tile-layer URL TEMPLATE (e.g. https://.../{z}/{x}/{y}.png). Verify the render in Desktop.")]
    public static string SetAzureMapLayerSource(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("azureMap visual name")] string visual,
        [Description("reference | tile")] string layer,
        [Description("file path, URL, inline GeoJSON, or tile URL template")] string source)
        => J.Try(() => report.SetAzureMapLayerSource(reportSessionId, page, visual, layer, source));

    [McpServerTool(Name = "set_shapemap_custom_map")]
    [Description("Set a custom TopoJSON/GeoJSON map on a shapeMap (the custom-map upload). topojsonOrGeojson = a local .json file (embedded), an http(s) URL, or an inline JSON string. Verify the render in Desktop.")]
    public static string SetShapeMapCustomMap(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("shapeMap visual name")] string visual,
        [Description("local .json file, URL, or inline TopoJSON/GeoJSON")] string topojsonOrGeojson)
        => J.Try(() => report.SetShapeMapCustomMap(reportSessionId, page, visual, topojsonOrGeojson));

    [McpServerTool(Name = "add_error_bars")]
    [Description("Add ERROR BARS to a chart (the errorBars object). kind=byField: upperField (+ lowerField unless symmetrical) measures bound from measureTable. kind=byPercentage: percent of the value. relation = Absolute|Relative. symmetrical optional. band = Fill|Line|Both. Verify the render in Desktop.")]
    public static string AddErrorBars(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("chart visual name")] string visual,
        [Description("byField | byPercentage")] string kind,
        [Description("table that owns the bound measures (byField)")] string? measureTable = null,
        [Description("upper-bound measure (byField)")] string? upperField = null,
        [Description("lower-bound measure (byField, omit if symmetrical)")] string? lowerField = null,
        [Description("percent of value (byPercentage)")] double? percent = null,
        [Description("Absolute | Relative")] string relation = "Absolute",
        [Description("symmetrical bars")] bool? symmetrical = null,
        [Description("Fill | Line | Both")] string? band = null)
        => J.Try(() => report.AddErrorBars(reportSessionId, page, visual, kind, measureTable, upperField, lowerField,
            percent, relation, symmetrical, band));

    [McpServerTool(Name = "add_anomaly_detection")]
    [Description("Add ANOMALY DETECTION to a line chart (the anomalyDetection object). sensitivity 0-100 optional (higher = more anomalies). explainBy = comma-separated fields the explanation groups by. Verify the render in Desktop.")]
    public static string AddAnomalyDetection(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("line-chart visual name")] string visual,
        [Description("0-100 sensitivity")] double? sensitivity = null,
        [Description("comma-separated explain-by fields")] string explainBy = "")
        => J.Try(() => report.AddAnomalyDetection(reportSessionId, page, visual, sensitivity, SplitIds(explainBy)));

    [McpServerTool(Name = "set_forecast")]
    [Description("Set a FULL forecast on a line chart (the forecast object: length/units/ignore-last/confidence/seasonality) - more complete than add_analytics_line forecast, which only sets the band. units = Point|Day|Month|Year. confidenceInterval e.g. 0.95. Verify the render in Desktop.")]
    public static string SetForecast(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("line-chart visual name")] string visual,
        [Description("forecast length")] double length,
        [Description("Point|Day|Month|Year")] string? units = null,
        [Description("points to ignore at the end")] double? ignoreLast = null,
        [Description("confidence interval e.g. 0.95")] double? confidenceInterval = null,
        [Description("seasonality (points per cycle)")] double? seasonality = null)
        => J.Try(() => report.SetForecast(reportSessionId, page, visual, length, units, ignoreLast, confidenceInterval, seasonality));

    [McpServerTool(Name = "set_play_axis")]
    [Description("Bind a field to a scatter chart's PLAY AXIS (a data binding: the Play projection role). field = \"Table.Column\". Animates the scatter over the field's values. Verify the render in Desktop.")]
    public static string SetPlayAxis(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("scatter-chart visual name")] string visual,
        [Description("the play field as Table.Column")] string field)
        => J.Try(() => report.SetPlayAxis(reportSessionId, page, visual, field));

    [McpServerTool(Name = "set_card_image")]
    [Description("Set a hero/callout IMAGE on a cardVisual (the new card's image element). imageUrlOrPath = a local file (embedded) or an http(s)/data URL. fit = Fit|Fill|Normal. Verify the render in Desktop.")]
    public static string SetCardImage(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("cardVisual name")] string visual,
        [Description("local image file or http(s)/data URL")] string imageUrlOrPath,
        [Description("Fit|Fill|Normal")] string? fit = null)
        => J.Try(() => report.SetCardImage(reportSessionId, page, visual, imageUrlOrPath, fit));

    [McpServerTool(Name = "set_slicer_conditional_formatting")]
    [Description("Apply measure-driven conditional formatting to a SLICER or button-slicer (generalises the table/matrix FillRule builders). target = fill|font|callout. rules = JSON array of {min,max,color} bands driven by the measure. Verify the render in Desktop.")]
    public static string SetSlicerConditionalFormatting(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("slicer visual name")] string visual,
        [Description("fill | font | callout")] string target,
        [Description("table that owns the measure")] string measureTable,
        [Description("measure that drives the colour")] string measure,
        [Description("JSON array of {min, max, color} bands")] string rules)
        => J.Try(() => report.SetSlicerConditionalFormatting(reportSessionId, page, visual, target, measureTable, measure, ParseRules(rules)));

    private static List<string> SplitIds(string csv) =>
        (csv ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    // ============================================================================================
    //  INTERACTIONS & FILTERS PARITY - page/report filters, remove, edit-interactions, filter pane,
    //  lock/hide filters, and slicer sync. Brings the engine to parity with Desktop's interactions UI.
    // ============================================================================================

    // Parse a comma-separated or JSON-array values string into a list (for categorical "is one of" filters).
    private static List<string>? ParseValues(string? values)
    {
        if (string.IsNullOrWhiteSpace(values)) return null;
        string v = values.Trim();
        if (v.StartsWith("["))
        {
            var arr = JsonSerializer.Deserialize<List<JsonElement>>(v, Ci);
            return arr?.Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText())
                       .Where(s => s.Length > 0).ToList();
        }
        return v.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }

    [McpServerTool(Name = "add_page_filter")]
    [Description("Add a PAGE-level filter (filters every visual on that page). kind=categorical writes an 'is one of' values filter (pass values, comma-separated or a JSON array); any other kind writes a comparison/blank filter (op = gt|gte|lt|lte|eq|ne|isblank|isnotblank) on a column or measure (fieldKind=column|measure). valueType=int|decimal|string for the values.")]
    public static string AddPageFilter(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("table that owns the filter field")] string table,
        [Description("field (column or measure) to filter on")] string field,
        [Description("categorical (values list) | comparison")] string kind = "categorical",
        [Description("for a comparison filter: column|measure")] string fieldKind = "column",
        [Description("comparison op: gt|gte|lt|lte|eq|ne|isblank|isnotblank")] string? op = null,
        [Description("categorical values (comma-separated or JSON array); or a single comparison value")] string? values = null,
        [Description("int|decimal|string")] string valueType = "string")
        => J.Try(() => report.AddPageFilter(reportSessionId, page, table, field, kind, fieldKind, op, ParseValues(values), valueType));

    [McpServerTool(Name = "add_report_filter")]
    [Description("Add a REPORT-level filter (filters every page in the report). kind=categorical writes an 'is one of' values filter (pass values, comma-separated or a JSON array); any other kind writes a comparison/blank filter (op = gt|gte|lt|lte|eq|ne|isblank|isnotblank) on a column or measure (fieldKind=column|measure). valueType=int|decimal|string.")]
    public static string AddReportFilter(ReportService report, string reportSessionId,
        [Description("table that owns the filter field")] string table,
        [Description("field (column or measure) to filter on")] string field,
        [Description("categorical (values list) | comparison")] string kind = "categorical",
        [Description("for a comparison filter: column|measure")] string fieldKind = "column",
        [Description("comparison op: gt|gte|lt|lte|eq|ne|isblank|isnotblank")] string? op = null,
        [Description("categorical values (comma-separated or JSON array); or a single comparison value")] string? values = null,
        [Description("int|decimal|string")] string valueType = "string")
        => J.Try(() => report.AddReportFilter(reportSessionId, table, field, kind, fieldKind, op, ParseValues(values), valueType));

    [McpServerTool(Name = "remove_filter")]
    [Description("Remove a matching filter (by table[field]) at a given scope = visual|page|report. For scope=visual pass page+visual; for scope=page pass page; scope=report needs neither. Returns how many filters were removed.")]
    public static string RemoveFilter(ReportService report, string reportSessionId,
        [Description("visual|page|report")] string scope,
        [Description("table that owns the filter field")] string table,
        [Description("field to match")] string field,
        [Description("page name (required for scope=visual|page)")] string? page = null,
        [Description("visual name (required for scope=visual)")] string? visual = null)
        => J.Try(() => report.RemoveFilter(reportSessionId, scope, page, visual, table, field));

    [McpServerTool(Name = "set_visual_interactions")]
    [Description("Set how a SOURCE visual affects a TARGET visual when a data point is selected (edit interactions). interaction = filter | highlight | none. sourceVisual/targetVisual are visual names (from list_visuals). Writes a { source, target, type } override in the page config (type: 1=filter, 2=highlight, 3=none). Verify the render in Desktop.")]
    public static string SetVisualInteractions(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("the visual whose selections drive the interaction")] string sourceVisual,
        [Description("the visual that is affected")] string targetVisual,
        [Description("filter | highlight | none")] string interaction)
        => J.Try(() => report.SetVisualInteractions(reportSessionId, page, sourceVisual, targetVisual, interaction));

    [McpServerTool(Name = "set_filter_pane")]
    [Description("Show/hide and expand/collapse the FILTER PANE. Omit page to set it report-wide; pass page to override one page. visible=false hides the pane entirely; expanded=false collapses it. Set at least one.")]
    public static string SetFilterPane(ReportService report, string reportSessionId,
        [Description("page name or displayName (omit = report-wide default)")] string? page = null,
        [Description("show the filter pane")] bool? visible = null,
        [Description("expand (vs collapse) the filter pane")] bool? expanded = null)
        => J.Try(() => report.SetFilterPane(reportSessionId, page, visible, expanded));

    [McpServerTool(Name = "lock_filter")]
    [Description("LOCK a filter in view mode (shown in the filter pane but cannot be changed by viewers). scope=visual|page|report (visual needs page+visual; page needs page). Matches the filter on table[field]. Sets the filter's isLockedInViewMode flag.")]
    public static string LockFilter(ReportService report, string reportSessionId,
        [Description("visual|page|report")] string scope,
        [Description("table that owns the filter field")] string table,
        [Description("field to match")] string field,
        [Description("true = locked, false = unlocked")] bool locked = true,
        [Description("page name (required for scope=visual|page)")] string? page = null,
        [Description("visual name (required for scope=visual)")] string? visual = null)
        => J.Try(() => report.SetFilterFlags(reportSessionId, scope, page, visual, table, field, locked, null));

    [McpServerTool(Name = "hide_filter")]
    [Description("HIDE a filter in view mode (still applied to the report but not shown in the filter pane to viewers). scope=visual|page|report (visual needs page+visual; page needs page). Matches the filter on table[field]. Sets the filter's isHiddenInViewMode flag.")]
    public static string HideFilter(ReportService report, string reportSessionId,
        [Description("visual|page|report")] string scope,
        [Description("table that owns the filter field")] string table,
        [Description("field to match")] string field,
        [Description("true = hidden, false = shown")] bool hidden = true,
        [Description("page name (required for scope=visual|page)")] string? page = null,
        [Description("visual name (required for scope=visual)")] string? visual = null)
        => J.Try(() => report.SetFilterFlags(reportSessionId, scope, page, visual, table, field, null, hidden));

    [McpServerTool(Name = "sync_slicer")]
    [Description("Add a slicer to a SYNC GROUP so its field stays in sync across pages (slicers sharing a groupName sync). groupName defaults to the slicer's bound field. fieldChanges/filterChanges control what syncs (both default true). The slicer must also be PLACED on each page (use clone_visual) for it to appear there - this wires the sync, not the placement.")]
    public static string SyncSlicer(ReportService report, string reportSessionId,
        [Description("page name or displayName the slicer is on")] string page,
        [Description("the slicer visual name (from list_visuals)")] string slicerVisual,
        [Description("sync group name (omit = use the slicer's field name)")] string? groupName = null,
        [Description("sync the field selection")] bool fieldChanges = true,
        [Description("sync the filter state")] bool filterChanges = true)
        => J.Try(() => report.SyncSlicer(reportSessionId, page, slicerVisual, groupName, fieldChanges, filterChanges));

    // ---- report-polish tools ------------------------------------------------------------------

    [McpServerTool(Name = "set_report_settings")]
    [Description("Set REPORT-LEVEL behaviour toggles (ExplorationSettings). settings = a JSON object of toggle->value. Booleans: useStylableVisualContainerHeader, hideVisualContainerHeader, defaultFilterActionIsDataFilter (filter vs highlight on click), defaultDrillFilterOtherVisuals, useCrossReportDrillthrough, allowChangeFilterTypes, allowInlineExploration (personalise visuals), useEnhancedTooltips, useScaledTooltips. Enums: exportDataMode (AllowSummarized|AllowSummarizedAndUnderlying|None), pagesPosition (PagesPane|Bottom). Merges - untouched toggles are kept.")]
    public static string SetReportSettings(ReportService report, string reportSessionId,
        [Description("JSON object of toggle -> value, e.g. {\"defaultFilterActionIsDataFilter\":true,\"exportDataMode\":\"None\"}")] string settings)
        => J.Try(() => report.SetReportSettings(reportSessionId, settings));

    [McpServerTool(Name = "set_page_display")]
    [Description("Set a page's DISPLAY OPTION (how the canvas fits the screen: FitToPage|FitToWidth|ActualSize) and/or its VISIBILITY (AlwaysVisible|HiddenInViewMode - hide the page from viewers). Both optional; set at least one.")]
    public static string SetPageDisplay(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("FitToPage | FitToWidth | ActualSize")] string? displayOption = null,
        [Description("AlwaysVisible | HiddenInViewMode")] string? visibility = null)
        => J.Try(() => report.SetPageDisplay(reportSessionId, page, displayOption, visibility));

    [McpServerTool(Name = "set_visual_format_selector")]
    [Description("Set a SELECTOR-SCOPED formatting entry on a visual so a card targets a data scope: a single series, a category, the grand total/subtotal, or a wildcard/conditional scope. Unlocks per-series colours, per-category formatting and total/subtotal formatting. bucket=objects|vcObjects. properties = JSON {prop:value} (encoded like set_visual_format). selector = JSON: {\"data\":[{\"scopeId\":...},{\"roles\":[\"Series\"]},{\"dataViewWildcard\":{\"matchingOption\":0}}]} | {\"metadata\":\"Table.Field\"} | {\"total\":true}. Updates the matching selector entry in place without clobbering the default card.")]
    public static string SetVisualFormatSelector(ReportService report, PropertyCatalog catalog, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("objects | vcObjects")] string bucket,
        [Description("formatting object/card name, e.g. dataPoint | labels | values")] string @object,
        [Description("JSON object of prop -> value")] string properties,
        [Description("JSON selector spec (data / metadata / total)")] string selector,
        [Description("when true, REJECT (do not apply) if validation finds an unknown card/property or type mismatch; default false (apply + warn)")] bool strict = false)
        => J.Try(() =>
        {
            // validate the {object:{prop:val}} shape against the registry (bare-card map).
            var asFormat = new JsonObject { [@object] = JsonNode.Parse(properties) }.ToJsonString();
            var (warnings, known) = ValidateAgainstCatalog(report, catalog, reportSessionId, page, visual, asFormat);
            if (strict && warnings.Count > 0)
                return (object)new { ok = false, error = "strict validation failed", warnings, warningCount = warnings.Count, applied = false };
            var result = report.SetVisualFormatSelector(reportSessionId, page, visual, bucket, @object, properties, selector);
            return WithWarnings(result, warnings, known);
        });

    [McpServerTool(Name = "set_bookmark_options")]
    [Description("Set a bookmark's OPTIONS - the Data / Display / Current-page / Selected-visuals toggles. suppressData=true turns DATA off, suppressDisplay=true turns DISPLAY off, suppressActiveSection=true turns CURRENT-PAGE off. targetVisuals = JSON array of visual names to scope the bookmark to those visuals (empty array clears the scope). Only the supplied options change.")]
    public static string SetBookmarkOptions(ReportService report, string reportSessionId,
        [Description("bookmark name or displayName")] string bookmark,
        [Description("suppress DATA (capture display only)")] bool? suppressData = null,
        [Description("suppress DISPLAY (capture data only)")] bool? suppressDisplay = null,
        [Description("suppress the CURRENT-PAGE switch")] bool? suppressActiveSection = null,
        [Description("JSON array of visual names to scope to (omit = leave as-is, [] = clear)")] string? targetVisuals = null)
        => J.Try(() =>
        {
            List<string>? tv = targetVisuals == null ? null
                : JsonSerializer.Deserialize<List<string>>(targetVisuals, Ci) ?? new List<string>();
            return report.SetBookmarkOptions(reportSessionId, bookmark, suppressData, suppressDisplay, suppressActiveSection, tv);
        });

    [McpServerTool(Name = "set_visual_display_mode")]
    [Description("Set a visual's DISPLAY STATE on the page: normal (default), hidden, spotlight (dim everything else), maximize, or focus. Writes singleVisual.display.mode. Note: spotlight/focus are live states that may need a bookmark to persist in Desktop; normal removes the override.")]
    public static string SetVisualDisplayMode(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("normal | spotlight | maximize | focus | hidden")] string mode)
        => J.Try(() => report.SetVisualDisplayMode(reportSessionId, page, visual, mode));

    [McpServerTool(Name = "add_report_measure")]
    [Description("Add a REPORT-LEVEL measure (a DAX measure stored in the report, not the model - handy when you cannot edit the model). table = the host table it attaches to. Written into layout.config.modelExtensions (legacy best-known shape; confirm in Desktop). Re-adding the same name updates it.")]
    public static string AddReportMeasure(ReportService report, string reportSessionId,
        [Description("host table the measure attaches to")] string table,
        [Description("measure name")] string name,
        [Description("DAX expression")] string daxExpression,
        [Description("optional format string, e.g. \"#,0\" or \"0.0%\"")] string? formatString = null,
        [Description("optional display folder")] string? displayFolder = null)
        => J.Try(() => report.AddReportMeasure(reportSessionId, table, name, daxExpression, formatString, displayFolder));

    // ---- remaining report-layer tools (visual calcs, sparklines, drill, navigators, theme authoring,
    //       accessibility, personalisation, custom visuals, wallpaper) -------------------------------

    [McpServerTool(Name = "add_visual_calculation")]
    [Description("Author a VISUAL CALCULATION on a visual: an in-visual DAX expression over the visual's own result matrix (e.g. RUNNINGSUM([Sales]), MOVINGAVERAGE([Sales],3), PERCENTOFTOTAL([Sales])) - distinct from a model measure. Written to singleVisual.visualCalculations[] and projected as a Values column so it renders. Re-adding the same name updates it.")]
    public static string AddVisualCalculation(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("the calculation name (becomes a column)")] string name,
        [Description("the visual-calculation DAX expression")] string daxExpression)
        => J.Try(() => report.AddVisualCalculation(reportSessionId, page, visual, name, daxExpression));

    [McpServerTool(Name = "add_sparkline")]
    [Description("Add a NATIVE SPARKLINE column to a table/matrix: a per-row mini line driven by a value measure across a category (e.g. trend over month). Writes objects.sparkline with the line measure + category bindings. valueTable/valueMeasure = the line measure; categoryTable/categoryField = the axis.")]
    public static string AddSparkline(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("the table/matrix visual name")] string visual,
        [Description("value measure's table")] string valueTable,
        [Description("value measure name")] string valueMeasure,
        [Description("category (axis) table")] string categoryTable,
        [Description("category (axis) field")] string categoryField,
        [Description("optional line colour hex")] string? lineColor = null)
        => J.Try(() => report.AddSparkline(reportSessionId, page, visual, valueTable, valueMeasure, categoryTable, categoryField, lineColor));

    [McpServerTool(Name = "set_drillthrough_fields")]
    [Description("Wire the CARRIED FIELDS of a DRILL-THROUGH page: fields = JSON array of {table,field}. Each becomes a drill-through page filter (so right-clicking that field elsewhere drills through to this page filtered to the value). keepAllFilters toggles the 'keep all filters' switch. Replaces any existing drill-through fields. Pair with set_page_type drillthrough / add_drillthrough.")]
    public static string SetDrillthroughFields(ReportService report, string reportSessionId,
        [Description("the drill-through page name or displayName")] string page,
        [Description("JSON array of {table,field} carried fields")] string fields,
        [Description("keep all incoming filters on drill-through")] bool? keepAllFilters = null)
        => J.Try(() =>
        {
            var items = JsonSerializer.Deserialize<List<BindIn>>(fields, Ci)
                        ?? throw new ArgumentException("fields must be a JSON array of {table,field}.");
            var list = items.Select(i => (i.table, i.field)).ToList();
            return report.SetDrillthroughFields(reportSessionId, page, list, keepAllFilters);
        });

    [McpServerTool(Name = "set_drilldown")]
    [Description("Set DRILL-DOWN behaviour on a visual with a drillable (hierarchy/multi-level) axis: expandToNextLevel (a click expands to the next level rather than drilling) and drillOnClick (single-click drills). Seeds the saved drill state (expansionStates). Set at least one toggle.")]
    public static string SetDrilldown(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("expand to the next level on click")] bool? expandToNextLevel = null,
        [Description("single-click drills down")] bool? drillOnClick = null)
        => J.Try(() => report.SetDrilldown(reportSessionId, page, visual, expandToNextLevel, drillOnClick));

    [McpServerTool(Name = "add_page_navigator")]
    [Description("Add a built-in PAGE NAVIGATOR visual that auto-lists the report's pages as navigation buttons (no manual buttons needed). Position with x/y/width/height.")]
    public static string AddPageNavigator(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        double x = 16, double y = 16, double width = 640, double height = 48)
        => J.Try(() => report.AddNavigator(reportSessionId, page, "pageNavigator", x, y, width, height));

    [McpServerTool(Name = "add_bookmark_navigator")]
    [Description("Add a built-in BOOKMARK NAVIGATOR visual that auto-lists the report's bookmarks as buttons (the no-code way to wire a view-switcher). Position with x/y/width/height.")]
    public static string AddBookmarkNavigator(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        double x = 16, double y = 16, double width = 640, double height = 48)
        => J.Try(() => report.AddNavigator(reportSessionId, page, "bookmarkNavigator", x, y, width, height));

    [McpServerTool(Name = "set_theme_data_colors")]
    [Description("Set the report theme's DATA COLOURS (the categorical palette). colors = comma-separated hex. Run generate_theme / apply_report_theme first.")]
    public static string SetThemeDataColors(ReportService report, string reportSessionId,
        [Description("comma-separated hex colours, e.g. #16365C,#2E86AB,#5BC0BE")] string colors)
        => J.Try(() => report.SetThemeDataColors(reportSessionId,
            colors.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()));

    [McpServerTool(Name = "set_theme_sentiment_colors")]
    [Description("Set the report theme's SENTIMENT colours: good / neutral / bad (the KPI sentiment ramp). Each is a hex colour; supply at least one. Run generate_theme first.")]
    public static string SetThemeSentimentColors(ReportService report, string reportSessionId,
        [Description("good (positive) hex")] string? good = null,
        [Description("neutral hex")] string? neutral = null,
        [Description("bad (negative) hex")] string? bad = null)
        => J.Try(() => report.SetThemeSentimentColors(reportSessionId, good, neutral, bad));

    [McpServerTool(Name = "set_theme_cf_colors")]
    [Description("Set the report theme's CONDITIONAL-FORMATTING gradient stops: min / center / max / null. Each is a hex colour; supply at least one. Run generate_theme first.")]
    public static string SetThemeCfColors(ReportService report, string reportSessionId,
        [Description("minimum (low) hex")] string? min = null,
        [Description("center (mid) hex")] string? center = null,
        [Description("maximum (high) hex")] string? max = null,
        [Description("null (blank) hex")] string? nul = null)
        => J.Try(() => report.SetThemeCfColors(reportSessionId, min, center, max, nul));

    [McpServerTool(Name = "set_theme_structural_colors")]
    [Description("Set the report theme's STRUCTURAL colours. colors = JSON object with any of: firstLevelElements, secondLevelElements, thirdLevelElements, fourthLevelElements, background, secondaryBackground, tableAccent (foreground is an alias of firstLevelElements). Each value is a hex colour; only supplied keys change. Run generate_theme first.")]
    public static string SetThemeStructuralColors(ReportService report, string reportSessionId,
        [Description("JSON object of structural key -> hex")] string colors)
        => J.Try(() => report.SetThemeStructuralColors(reportSessionId, colors));

    [McpServerTool(Name = "set_theme_text_class")]
    [Description("Set/merge one theme TEXT CLASS: class = callout|title|header|label or a secondary class (largeTitle|semiboldLabel|largeLabel|smallLabel|lightLabel|boldLabel|largeLightLabel|smallLightLabel). Any subset of fontFace / fontSize / color. Merges - untouched props are kept. Run generate_theme first.")]
    public static string SetThemeTextClass(ReportService report, string reportSessionId,
        [Description("the text class name")] string textClass,
        [Description("font face, e.g. Segoe UI Semibold")] string? fontFace = null,
        [Description("font size in pt")] double? fontSize = null,
        [Description("colour hex")] string? color = null)
        => J.Try(() => report.SetThemeTextClass(reportSessionId, textClass, fontFace, fontSize, color));

    [McpServerTool(Name = "add_theme_visual_style_preset")]
    [Description("Add a NAMED visual-style PRESET to the theme so a visual can opt into a look (theme.visualStyles[visualType][presetName]). cardProperties = JSON object of cardName -> property map or array, e.g. {\"title\":{\"fontSize\":14,\"bold\":true},\"background\":{\"show\":true,\"color\":{\"solid\":{\"color\":\"#FFFFFF\"}}}}. Run generate_theme first.")]
    public static string AddThemeVisualStylePreset(ReportService report, string reportSessionId,
        [Description("canonical visualType key, e.g. lineChart, tableEx")] string visualType,
        [Description("the preset name shown in the style gallery")] string presetName,
        [Description("JSON object of cardName -> property map/array")] string cardProperties)
        => J.Try(() => report.AddThemeVisualStylePreset(reportSessionId, visualType, presetName, cardProperties));

    [McpServerTool(Name = "set_alt_text")]
    [Description("Set a visual's ACCESSIBILITY ALT TEXT (read by screen readers): written to singleVisual.objects.general.altText.")]
    public static string SetAltText(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("the alt text")] string text)
        => J.Try(() => report.SetAltText(reportSessionId, page, visual, text));

    [McpServerTool(Name = "set_tab_order")]
    [Description("Set the KEYBOARD TAB ORDER of a page's visuals: visualOrder = comma-separated visual names in the order a keyboard user tabs through them. Writes each visual's layouts[0].position.tabOrder. Visuals not listed keep their order.")]
    public static string SetTabOrder(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("comma-separated visual names in tab order")] string visualOrder)
        => J.Try(() => report.SetTabOrder(reportSessionId, page,
            visualOrder.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()));

    [McpServerTool(Name = "set_personalization")]
    [Description("Turn PERSONALISATION (inline exploration) on/off. scope=report sets the report-level allowInlineExploration setting (viewers can re-jig visuals for themselves). scope=page sets the per-page personalizeVisual toggle (perVisualPersonalize).")]
    public static string SetPersonalization(ReportService report, string reportSessionId,
        [Description("report | page")] string scope = "report",
        [Description("page name (required for scope=page)")] string? page = null,
        [Description("allow inline exploration (scope=report)")] bool? allowInlineExploration = null,
        [Description("per-visual personalise toggle (scope=page)")] bool? perVisualPersonalize = null)
        => J.Try(() => report.SetPersonalization(reportSessionId, scope, page, allowInlineExploration, perVisualPersonalize));

    [McpServerTool(Name = "register_custom_visual")]
    [Description("Register an imported CUSTOM VISUAL into the report so its visualType is usable: adds the guid to layout.config.publicCustomVisuals, and when a .pbiviz path is given, stages the file into resourcePackages. name = the visual's visualType/guid; guid defaults to name.")]
    public static string RegisterCustomVisual(ReportService report, string reportSessionId,
        [Description("the custom visual's visualType/guid")] string name,
        [Description("optional explicit guid (defaults to name)")] string? guid = null,
        [Description("optional absolute path to the .pbiviz file to embed")] string? path = null)
        => J.Try(() => report.RegisterCustomVisual(reportSessionId, name, guid, path));

    [McpServerTool(Name = "set_page_wallpaper")]
    [Description("Set the page WALLPAPER (the grey margin OUTSIDE the canvas), distinct from set_page_background (which colours the canvas area). color = hex, transparency = 0-100. Writes the page outspace object.")]
    public static string SetPageWallpaper(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("wallpaper colour hex")] string? color = null,
        [Description("transparency 0-100")] double? transparency = null)
        => J.Try(() => report.SetPageWallpaper(reportSessionId, page, color, transparency));

    // ============================================================================================
    //  WAVE N - report-feature gap closers: bookmark data-state capture, page rename/reorder/resize,
    //  advanced filter types, field-value/web-url CF, cross-report drillthrough, tooltip bindings,
    //  rich button actions, mobile show/hide, page tab order + show-no-data, org custom visuals.
    // ============================================================================================

    [McpServerTool(Name = "set_bookmark_data_state")]
    [Description("Re-capture an existing bookmark's DATA state from the page's CURRENT live state: filter/slicer values, saved sort, drill position and cross-highlight (not just which visuals are shown). This is the 'update bookmark with current data' action. Keeps the bookmark's hidden/shown visuals. page (optional) re-anchors which page is captured (defaults to the bookmark's active page).")]
    public static string SetBookmarkDataState(ReportService report, string reportSessionId,
        [Description("bookmark name or displayName")] string bookmark,
        [Description("page to capture (omit = the bookmark's active page)")] string? page = null)
        => J.Try(() => report.SetBookmarkDataState(reportSessionId, bookmark, page));

    [McpServerTool(Name = "rename_page")]
    [Description("Rename a report page (set its tab title / displayName). The internal page name is unchanged so bookmarks and navigation keep working.")]
    public static string RenamePage(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("the new tab title")] string newName)
        => J.Try(() => report.RenamePage(reportSessionId, page, newName));

    [McpServerTool(Name = "reorder_pages")]
    [Description("Reorder the report's pages. orderedNames = comma-separated page names/displayNames in the desired left-to-right order; each gets a fresh ordinal in that order. Pages not listed keep their relative order and are appended after.")]
    public static string ReorderPages(ReportService report, string reportSessionId,
        [Description("comma-separated page names in the desired order")] string orderedNames)
        => J.Try(() => report.ReorderPages(reportSessionId,
            orderedNames.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList()));

    [McpServerTool(Name = "resize_page")]
    [Description("Resize a page's canvas to an explicit width x height (pixels). Writes the section size + the pageSize object so it sticks in Desktop. For named presets use set_canvas_preset.")]
    public static string ResizePage(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("canvas width in px")] double width,
        [Description("canvas height in px")] double height)
        => J.Try(() => report.ResizePage(reportSessionId, page, width, height));

    [McpServerTool(Name = "set_canvas_preset")]
    [Description("Set a page's canvas to a named PRESET: 16:9 (1280x720), 4:3 (1024x768), letter (816x1056), tooltip (320x240) or mobile (320x568). preset=custom needs width+height. Writes the section size + pageSize object.")]
    public static string SetCanvasPreset(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("16:9 | 4:3 | letter | tooltip | mobile | custom")] string preset,
        [Description("width in px (preset=custom)")] double? width = null,
        [Description("height in px (preset=custom)")] double? height = null)
        => J.Try(() => report.SetCanvasPreset(reportSessionId, page, preset, width, height));

    [McpServerTool(Name = "add_topn_filter")]
    [Description("Add a TOP-N (or bottom-N) filter at a scope=visual|page|report: keep the top/bottom n of table[field] ranked by byTable[byMeasure]. direction=top|bottom. For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddTopNFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the field to rank")] string table,
        [Description("the column being ranked/limited")] string field,
        [Description("how many to keep")] int n,
        [Description("table that owns the ranking measure")] string byTable,
        [Description("the measure that ranks")] string byMeasure,
        [Description("top | bottom")] string direction = "top",
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.AddTopNFilter(reportSessionId, scope, page, visual, table, field, n, byTable, byMeasure, direction));

    [McpServerTool(Name = "add_relative_date_filter")]
    [Description("Add a RELATIVE DATE filter at a scope=visual|page|report on a date column: mode=Last|Next|This, count units of unit=Days|Weeks|Months|Years. includeCurrent includes the current period; calendar=true aligns to calendar boundaries (vs a rolling window). For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddRelativeDateFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the date column")] string table,
        [Description("the date column")] string field,
        [Description("Last | Next | This")] string mode,
        [Description("number of units")] int count,
        [Description("Days | Weeks | Months | Years")] string unit,
        [Description("include the current period")] bool includeCurrent = false,
        [Description("calendar-aligned (vs rolling)")] bool calendar = false,
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.AddRelativeDateFilter(reportSessionId, scope, page, visual, table, field, mode, count, unit, includeCurrent, calendar));

    [McpServerTool(Name = "add_relative_time_filter")]
    [Description("Add a RELATIVE TIME filter at a scope=visual|page|report on a datetime column: mode=Last|Next|This, count units of unit=Hours|Minutes|Seconds. includeCurrent includes the current period. For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddRelativeTimeFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the datetime column")] string table,
        [Description("the datetime column")] string field,
        [Description("Last | Next | This")] string mode,
        [Description("number of units")] int count,
        [Description("Hours | Minutes | Seconds")] string unit,
        [Description("include the current period")] bool includeCurrent = false,
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.AddRelativeTimeFilter(reportSessionId, scope, page, visual, table, field, mode, count, unit, includeCurrent));

    [McpServerTool(Name = "add_include_exclude_filter")]
    [Description("Add an INCLUDE or EXCLUDE filter (the right-click include/exclude these values) at a scope=visual|page|report. values = comma-separated or JSON array. exclude=true excludes them (Not In), false includes them (In). valueType=int|decimal|string. For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddIncludeExcludeFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the column")] string table,
        [Description("the column")] string field,
        [Description("values (comma-separated or JSON array)")] string values,
        [Description("true = exclude, false = include")] bool exclude = false,
        [Description("int|decimal|string")] string valueType = "string",
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.AddIncludeExcludeFilter(reportSessionId, scope, page, visual, table, field,
            ParseValues(values) ?? new List<string>(), valueType, exclude));

    private record CondIn(string op, string? value);

    [McpServerTool(Name = "add_advanced_filter")]
    [Description("Add an ADVANCED multi-condition filter (the And/Or advanced filter card) at a scope=visual|page|report: up to two conditions on the same field joined by And or Or. conditions = JSON array of {op,value} where op=gt|gte|lt|lte|eq|ne|isblank|isnotblank|contains|startswith. combine=and|or. fieldKind=column|measure, valueType=int|decimal|string. For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddAdvancedFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the field")] string table,
        [Description("the field")] string field,
        [Description("JSON array of {op,value} (1-2 conditions)")] string conditions,
        [Description("and | or")] string combine = "and",
        [Description("column | measure")] string fieldKind = "column",
        [Description("int|decimal|string")] string valueType = "string",
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() =>
        {
            var items = JsonSerializer.Deserialize<List<CondIn>>(conditions, Ci)
                        ?? throw new ArgumentException("conditions must be a JSON array of {op,value}.");
            var list = items.Select(c => (c.op, c.value)).ToList();
            return report.AddAdvancedFilter(reportSessionId, scope, page, visual, table, field, fieldKind, combine, list, valueType);
        });

    [McpServerTool(Name = "set_field_value_cf")]
    [Description("FORMAT BY FIELD VALUE conditional formatting on a table/matrix column: a measure that returns a hex/CSS colour drives the column's background, font or icon colour DIRECTLY (not a rule/gradient - the measure's returned colour IS the colour). target=background|font|icon. colorMeasure = \"Table[Measure]\" (or pass colorMeasureTable). column = the column's queryRef e.g. 'Dim_Product.Brand'.")]
    public static string SetFieldValueCf(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("table/matrix visual name")] string visual,
        [Description("the column's queryRef, e.g. Dim_Product.Brand")] string column,
        [Description("background | font | icon")] string target,
        [Description("colour measure, e.g. Fact[Colour] or just the measure name with colorMeasureTable")] string colorMeasure,
        [Description("table that owns the colour measure (if not in colorMeasure)")] string? colorMeasureTable = null)
        => J.Try(() => report.SetFieldValueCf(reportSessionId, page, visual, column, target, colorMeasure, colorMeasureTable));

    [McpServerTool(Name = "set_web_url_cf")]
    [Description("WEB URL conditional formatting on a table/matrix column: a measure that returns a URL string makes the cell a clickable link. urlMeasure = \"Table[Measure]\" (or pass urlMeasureTable). column = the column's queryRef.")]
    public static string SetWebUrlCf(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("table/matrix visual name")] string visual,
        [Description("the column's queryRef, e.g. Dim_Product.Brand")] string column,
        [Description("URL measure, e.g. Fact[Link]")] string urlMeasure,
        [Description("table that owns the URL measure (if not in urlMeasure)")] string? urlMeasureTable = null)
        => J.Try(() => report.SetWebUrlCf(reportSessionId, page, visual, column, urlMeasure, urlMeasureTable));

    [McpServerTool(Name = "set_cross_report_drillthrough")]
    [Description("Enable (or disable) CROSS-REPORT drill-through on a drill-through page so it can be a target from OTHER reports in the workspace. Writes the page pageBinding.referenceScope=CrossReport + flips the report useCrossReportDrillthrough setting. The page should already be set_page_type drillthrough with carried fields. Verify in Desktop.")]
    public static string SetCrossReportDrillthrough(ReportService report, string reportSessionId,
        [Description("the drill-through page name or displayName")] string page,
        [Description("true = enable cross-report, false = same-report only")] bool enable = true)
        => J.Try(() => report.SetCrossReportDrillthrough(reportSessionId, page, enable));

    [McpServerTool(Name = "set_tooltip_field_binding")]
    [Description("Bind a TOOLTIP PAGE to specific fields so it only shows when hovering those fields. fields = JSON array of {table,field}. Writes the tooltip page's pageBinding {type=Tooltip, parameters}. The page should already be set_page_type tooltip. Verify in Desktop.")]
    public static string SetTooltipFieldBinding(ReportService report, string reportSessionId,
        [Description("the tooltip page name or displayName")] string tooltipPage,
        [Description("JSON array of {table,field} to bind the tooltip to")] string fields)
        => J.Try(() =>
        {
            var items = JsonSerializer.Deserialize<List<BindIn>>(fields, Ci)
                        ?? throw new ArgumentException("fields must be a JSON array of {table,field}.");
            return report.SetTooltipFieldBinding(reportSessionId, tooltipPage, items.Select(i => (i.table, i.field)).ToList());
        });

    [McpServerTool(Name = "add_tooltip_fields")]
    [Description("Add EXTRA FIELDS to a visual's DEFAULT (data) tooltip (the Tooltips field-well), so they show on hover. fields = JSON array of {table,field,kind} (kind=measure|column). Adds the projections + query Select entries. Distinct from set_visual_tooltip_page (which swaps the whole tooltip for a report page).")]
    public static string AddTooltipFields(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("JSON array of {table,field,kind}")] string fields)
        => J.Try(() => report.AddTooltipFields(reportSessionId, page, visual, Parse(fields, "Tooltips")));

    [McpServerTool(Name = "add_button")]
    [Description("Add a BUTTON with any action: actionType = bookmark | back | pageNavigation | drillthrough | qna | webUrl | clearAllSlicers | applyAllSlicers. bookmark needs bookmarkName; pageNavigation/drillthrough need a destination page (in destinationOrUrl); webUrl needs a url (in destinationOrUrl); back/qna/clearAllSlicers/applyAllSlicers need neither (clearAllSlicers/applyAllSlicers act on the page's slicers, e.g. a native Clear-all-slicers button). Writes the actionButton visualLink the product uses. Returns the new visual id.")]
    public static string AddButton(ReportService report, string reportSessionId,
        [Description("page name or displayName to place the button on")] string page,
        [Description("button label")] string text,
        [Description("bookmark | back | pageNavigation | drillthrough | qna | webUrl | clearAllSlicers | applyAllSlicers")] string actionType = "bookmark",
        [Description("bookmark name (actionType=bookmark)")] string? bookmarkName = null,
        [Description("destination page (pageNavigation/drillthrough) OR url (webUrl)")] string? destinationOrUrl = null,
        double x = 24, double y = 124, double width = 150, double height = 40)
        => J.Try(() => report.AddActionButtonEx(reportSessionId, page, text, actionType, bookmarkName, destinationOrUrl, x, y, width, height));

    [McpServerTool(Name = "set_mobile_visibility")]
    [Description("Show or HIDE a visual on the PHONE (mobile) layout only - its desktop visibility is unchanged. visible=false hides it from the phone view. Seeds a mobile layout entry mirroring the desktop position if none exists.")]
    public static string SetMobileVisibility(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("true = show on phone, false = hide")] bool visible)
        => J.Try(() => report.SetMobileVisibility(reportSessionId, page, visual, visible));

    [McpServerTool(Name = "set_page_tab_order")]
    [Description("Set a page's KEYBOARD TAB ORDER with explicit HIDE: orderedVisuals (comma-separated) get sequential tab order; hidden (comma-separated) get tabOrder -1 (removed from the tab sequence). Visuals in neither list keep their order. Set at least one list.")]
    public static string SetPageTabOrder(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("comma-separated visual names in tab order")] string orderedVisuals = "",
        [Description("comma-separated visual names to remove from tab order")] string hidden = "")
        => J.Try(() => report.SetPageTabOrder(reportSessionId, page, SplitIds(orderedVisuals), SplitIds(hidden)));

    [McpServerTool(Name = "set_show_items_no_data")]
    [Description("Toggle 'Show items with no data' for one field in a visual: categories with no rows still appear (e.g. all months even with zero sales). The field must already be projected on the visual. Writes the projection's showAll flag.")]
    public static string SetShowItemsNoData(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("table that owns the field")] string table,
        [Description("the field")] string field,
        [Description("true = show items with no data")] bool show = true)
        => J.Try(() => report.SetShowItemsNoData(reportSessionId, page, visual, table, field, show));

    [McpServerTool(Name = "register_org_custom_visual")]
    [Description("Register an ORGANIZATION-store custom visual into the report so its visualType is usable: adds the guid to layout.config.organizationCustomVisuals (the tenant's org store, vs the public AppSource store register_custom_visual uses). name = the visual's visualType/guid; guid defaults to name. A .pbiviz path is rarely needed for org visuals but is staged when given.")]
    public static string RegisterOrgCustomVisual(ReportService report, string reportSessionId,
        [Description("the custom visual's visualType/guid")] string name,
        [Description("optional explicit guid (defaults to name)")] string? guid = null,
        [Description("optional absolute path to the .pbiviz file to embed")] string? path = null)
        => J.Try(() => report.RegisterOrgCustomVisual(reportSessionId, name, guid, path));

    [McpServerTool(Name = "save_report")]
    [Description("Write all report edits back into the .pbix (patches Report/Layout in the ZIP). The .pbix must be CLOSED in Power BI Desktop.")]
    public static string SaveReport(ReportService report, string reportSessionId)
        => J.Try(() => report.Save(reportSessionId));

    // ===================================================================== visual-property registry

    [McpServerTool(Name = "list_visual_types")]
    [Description("List every canonical Power BI visualType key the formatting registry knows (52 types), each tagged data | container, with its card count. Use these keys with list_visual_properties / get_visual_schema and as a visual's type when adding visuals.")]
    public static string ListVisualTypes(PropertyCatalog catalog)
        => J.Try(() => catalog.ListVisualTypes());

    [McpServerTool(Name = "list_visual_properties")]
    [Description("List ALL formatting cards and their properties for one visualType (commonCards like title/background/border PLUS the visual-specific cards). Each property reports its type (bool|number|text|color|enum|object), enum values where applicable, and numeric min/max where declared. The complete discovery surface for what set_visual_format can set on a visual.")]
    public static string ListVisualProperties(PropertyCatalog catalog,
        [Description("a canonical visualType key, e.g. lineChart, tableEx, slicer")] string visualType)
        => J.Try(() => catalog.ListVisualProperties(visualType));

    [McpServerTool(Name = "get_visual_schema")]
    [Description("Get the formatting schema for one visualType as a compact structured object - the same cards -> properties -> {type, enum, min, max} as list_visual_properties, optionally scoped to a single card (e.g. legend, valueAxis, title). Omit card for the full schema.")]
    public static string GetVisualSchema(PropertyCatalog catalog,
        [Description("a canonical visualType key, e.g. lineChart")] string visualType,
        [Description("optional single card to scope to, e.g. legend")] string? card = null)
        => J.Try(() => catalog.ListVisualProperties(visualType, card));

    [McpServerTool(Name = "validate_visual_format")]
    [Description("Validate a formatJson against the registry for a given visualType WITHOUT applying it. formatJson is the set_visual_format shape ({vcObjects:{card:{prop:val}}, objects:{card:{prop:val}}}) or a bare {card:{prop:val}} map. Returns non-fatal warnings: unknown cards, unknown properties, and type/enum/range mismatches. Custom visuals and newer properties legitimately fall outside the catalogue, so warnings are advisory, not errors.")]
    public static string ValidateVisualFormat(PropertyCatalog catalog,
        [Description("a canonical visualType key, e.g. lineChart")] string visualType,
        [Description("JSON: { vcObjects:{card:{prop:value}}, objects:{card:{prop:value}} } or a bare {card:{prop:value}} map")] string formatJson)
        => J.Try(() =>
        {
            var r = catalog.Validate(visualType, formatJson);
            return new
            {
                ok = true,
                visualType,
                known = !r.UnknownVisualType,
                valid = r.Valid,
                warnings = r.Warnings,
                warningCount = r.Warnings.Count,
            };
        });

    // ================================================================ Wave O: report-side helpers
    [McpServerTool(Name = "bind_dynamic_title")]
    [Description("Bind a visual's TITLE to a text MEASURE (expression-based title): the measure (e.g. a SELECTEDVALUE narrative with an All/multiple fallback) becomes the title text and title show is forced on. Author the text measure first with add_dynamic_title_measure (model side). titleMeasure = \"Table[Measure]\" (or pass titleMeasureTable). page/visual resolve by name or displayName.")]
    public static string BindDynamicTitle(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name or displayName")] string visual,
        [Description("the title measure, \"Table[Measure]\" or just the measure with titleMeasureTable")] string titleMeasure,
        [Description("the measure's table (if not embedded in titleMeasure)")] string? titleMeasureTable = null)
        => J.Try(() => report.BindDynamicTitle(reportSessionId, page, visual, titleMeasure, titleMeasureTable));

    [McpServerTool(Name = "set_button_state_cf")]
    [Description("Measure-driven per-STATE button formatting: a measure returning a hex colour drives a button's fill/text/icon colour for a given state. state=default|hover|pressed|selected; target=fill|text|icon. button is the actionButton's visual name. colorMeasure = \"Table[Measure]\".")]
    public static string SetButtonStateCf(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("the button visual name")] string button,
        [Description("the colour measure, \"Table[Measure]\"")] string colorMeasure,
        [Description("default | hover | pressed | selected")] string state = "default",
        [Description("fill | text | icon")] string target = "fill",
        [Description("the measure's table (if not embedded in colorMeasure)")] string? colorMeasureTable = null)
        => J.Try(() => report.SetButtonStateCf(reportSessionId, page, button, colorMeasure, state, target, colorMeasureTable));

    [McpServerTool(Name = "set_global_wildcard_defaults")]
    [Description("Set the theme's GLOBAL WILDCARD defaults (\"*\":{\"*\":[...]}) - house-style formatting for every card of every visual in one shot. props = JSON { cardName: {prop:value} | [{prop:value}] }, merged into any existing defaults. The card card uses a \"$id\":\"default\" quirk - supply such a body verbatim to target it.")]
    public static string SetGlobalWildcardDefaults(ReportService report, string reportSessionId,
        [Description("JSON map of cardName -> property map / array, e.g. {\"title\":{\"fontColor\":\"#16365C\"},\"border\":[{\"show\":true,\"radius\":8}]}")] string props)
        => J.Try(() => report.SetGlobalWildcardDefaults(reportSessionId, props));

    [McpServerTool(Name = "generate_palette")]
    [Description("Compute a palette and write it into the theme: dataColors[] + good/neutral/bad + CF min/center/max. mode=harmonic (hue-wheel off baseColor) | monochrome (lightness ramp of baseColor) | gradient (interpolate baseColor->... actually gradientFrom->gradientTo). For gradient pass gradientFrom + gradientTo; otherwise pass baseColor.")]
    public static string GeneratePalette(ReportService report, string reportSessionId,
        [Description("how many data colours to emit")] int n = 8,
        [Description("harmonic | monochrome | gradient")] string mode = "harmonic",
        [Description("base colour hex (harmonic/monochrome)")] string? baseColor = null,
        [Description("gradient start hex (gradient mode)")] string? gradientFrom = null,
        [Description("gradient end hex (gradient mode)")] string? gradientTo = null)
        => J.Try(() => report.GeneratePaletteIntoTheme(reportSessionId, baseColor, gradientFrom, gradientTo, n, mode));

    [McpServerTool(Name = "add_html_content_block")]
    [Description("Add the HTML Content (lite) custom visual bound to a DAX measure that returns HTML/CSS: registers the visual type, adds the visual, and binds the measure to its content (values) role. daxHtmlMeasure = \"Table[Measure]\" (the measure must already exist and return an HTML string). visualGuid overrides the visual type id (confirm the certified guid in Desktop).")]
    public static string AddHtmlContentBlock(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("a label/title for the visual")] string name,
        [Description("the DAX HTML measure, \"Table[Measure]\"")] string daxHtmlMeasure,
        double x, double y, double w, double h,
        [Description("override the HTML Content visual guid (optional)")] string? visualGuid = null)
        => J.Try(() => report.AddHtmlContentBlock(reportSessionId, page, name, daxHtmlMeasure, x, y, w, h, visualGuid));

    [McpServerTool(Name = "apply_report_template")]
    [Description("Apply a reusable REPORT TEMPLATE in one call: a bundle of theme + wallpaper + canvas-preset + nav settings. template = JSON { theme?:{...}, wallpaper?:{color,transparency}, canvas?:{preset,width,height}, nav?:{<ExplorationSettings>} }. page targets the wallpaper + canvas (defaults to the first page). Round-trips with save_report_template.")]
    public static string ApplyReportTemplate(ReportService report, string reportSessionId,
        [Description("the template JSON bundle")] string template,
        [Description("page to apply wallpaper/canvas to (optional; default first page)")] string? page = null)
        => J.Try(() => report.ApplyReportTemplate(reportSessionId, template, page));

    [McpServerTool(Name = "save_report_template")]
    [Description("Save the report's current look as a reusable TEMPLATE bundle (inverse of apply_report_template): captures the live theme, the page's wallpaper + canvas size, and the report nav settings into one templateJson to re-apply later. page defaults to the first page.")]
    public static string SaveReportTemplate(ReportService report, string reportSessionId,
        [Description("a name for the template")] string name,
        [Description("page to capture wallpaper/canvas from (optional; default first page)")] string? page = null)
        => J.Try(() => report.SaveReportTemplate(reportSessionId, name, page));

    // ============================================================================================
    //  WAVE S - filter/query AST completeness, the Deneb/Vega-Lite visual, and DataMashup M edit.
    // ============================================================================================

    [McpServerTool(Name = "add_between_filter")]
    [Description("Add a BETWEEN filter (lo <= field <= hi) at a scope=visual|page|report: two Comparisons (GTE + LTE) joined by And. valueType picks the literal encoding (int|long|decimal|double|datetime|...). fieldKind=column|measure. For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddBetweenFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the field")] string table,
        [Description("the field")] string field,
        [Description("lower bound (inclusive)")] string lo,
        [Description("upper bound (inclusive)")] string hi,
        [Description("int|long|decimal|double|datetime|string")] string valueType = "double",
        [Description("column | measure")] string fieldKind = "column",
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.AddBetweenFilter(reportSessionId, scope, page, visual, table, field, fieldKind, lo, hi, valueType));

    [McpServerTool(Name = "add_does_not_contain_filter")]
    [Description("Add a DOES-NOT-CONTAIN filter (Not(Contains)) on a text column at a scope=visual|page|report. For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddDoesNotContainFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the column")] string table,
        [Description("the text column")] string field,
        [Description("the substring the value must NOT contain")] string value,
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.AddDoesNotContainFilter(reportSessionId, scope, page, visual, table, field, value));

    [McpServerTool(Name = "add_fixed_anchor_relative_date_filter")]
    [Description("Add a FIXED-ANCHOR relative-date window (UI-impossible): Last/Next N units measured from a LITERAL anchor date instead of Now. Encoded as a GTE + LT pair of datetime Comparisons. mode=Last|Next, unit=Days|Weeks|Months|Years. anchorDate=ISO date (e.g. 2024-06-30). For scope=visual pass page+visual; scope=page pass page.")]
    public static string AddFixedAnchorRelativeDateFilter(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the date column")] string table,
        [Description("the date column")] string field,
        [Description("the literal anchor date, ISO (e.g. 2024-06-30)")] string anchorDate,
        [Description("Last | Next")] string mode,
        [Description("number of units")] int count,
        [Description("Days | Weeks | Months | Years")] string unit,
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.AddFixedAnchorRelativeDateFilter(reportSessionId, scope, page, visual, table, field, anchorDate, mode, count, unit));

    [McpServerTool(Name = "edit_visual_aggregation")]
    [Description("Change the AGGREGATION applied to a field projected on a visual: wraps the Select node's Column/Measure in an Aggregation { Function }. aggregation=Sum|Avg|Min|Max|Count|CountNonNull|Median|StdDev|Var (index 0..8). field='Table.Field' (the queryRef). scopedEvalBaseline=true wraps it in a context-free ScopedEval/AllRolesRef baseline (FLAG: confirm shape in Desktop).")]
    public static string EditVisualAggregation(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("the field, 'Table.Field'")] string field,
        [Description("Sum|Avg|Min|Max|Count|CountNonNull|Median|StdDev|Var")] string aggregation,
        [Description("wrap in a ScopedEval/AllRolesRef context-free baseline")] bool scopedEvalBaseline = false)
        => J.Try(() => report.EditVisualAggregation(reportSessionId, page, visual, field, aggregation, scopedEvalBaseline));

    [McpServerTool(Name = "add_visual_topn")]
    [Description("Add a VISUAL-LEVEL Top N: rank rankTable[rankField] by byTable[byMeasure], keep the top/bottom n, with the ranking applied in the visual's own query (a Top node in prototypeQuery.Where). direction=Top|Bottom. Distinct from add_topn_filter (which writes a filter card).")]
    public static string AddVisualTopN(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name (from list_visuals)")] string visual,
        [Description("table that owns the ranked field")] string rankTable,
        [Description("the column being ranked/limited")] string rankField,
        [Description("table that owns the ranking measure")] string byTable,
        [Description("the measure that ranks")] string byMeasure,
        [Description("how many to keep")] int n,
        [Description("Top | Bottom")] string direction = "Top")
        => J.Try(() => report.AddVisualTopN(reportSessionId, page, visual, rankTable, rankField, byTable, byMeasure, n, direction));

    [McpServerTool(Name = "set_filter_restatement")]
    [Description("Set a filter card's RESTATEMENT (custom display label) and/or its lock/hide flags on a matching filter (by table[field]) at a scope=visual|page|report. displayName overrides the card's auto label; isHiddenInViewMode / isLockedInViewMode toggle hide/lock. For scope=visual pass page+visual; scope=page pass page.")]
    public static string SetFilterRestatement(ReportService report, string reportSessionId,
        [Description("visual | page | report")] string scope,
        [Description("table that owns the filter field")] string table,
        [Description("the field to match")] string field,
        [Description("custom card label (the restatement)")] string? displayName = null,
        [Description("hide the card in view mode")] bool? isHiddenInViewMode = null,
        [Description("lock the card in view mode")] bool? isLockedInViewMode = null,
        [Description("page (scope=visual|page)")] string? page = null,
        [Description("visual (scope=visual)")] string? visual = null)
        => J.Try(() => report.SetFilterRestatement(reportSessionId, scope, page, visual, table, field, displayName, isHiddenInViewMode, isLockedInViewMode));

    [McpServerTool(Name = "add_deneb_visual")]
    [Description("Add a DENEB (Vega / Vega-Lite) custom visual to a page: registers the Deneb guid, adds the visual, binds dataRoles into Deneb's 'values' data role, and writes objects.vega[0].properties (jsonSpec one-lined + single-quoted in a literal, jsonConfig, provider=vega|vegaLite, renderMode=svg|canvas, enable* booleans). spec/config = JSON strings. dataRoles = JSON array of {table,field,kind}. FLAG: visualGuid defaults to the published Deneb guid - confirm in Desktop.")]
    public static string AddDenebVisual(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("a label/title for the visual")] string name,
        [Description("the Vega/Vega-Lite spec as a JSON string")] string spec,
        [Description("vega | vegaLite")] string provider = "vegaLite",
        [Description("svg | canvas")] string renderMode = "svg",
        [Description("optional Vega config as a JSON string")] string? config = null,
        [Description("enable Deneb tooltips")] bool enableTooltips = true,
        [Description("enable Deneb selection")] bool enableSelection = true,
        [Description("enable Deneb cross-highlight")] bool enableHighlight = false,
        double x = 24, double y = 24, double w = 480, double h = 320,
        [Description("JSON array of {table,field,kind} to bind into Deneb's data role")] string? dataRoles = null,
        [Description("override the Deneb visual guid (confirm in Desktop)")] string? visualGuid = null)
        => J.Try(() =>
        {
            var binds = string.IsNullOrWhiteSpace(dataRoles) ? new List<FieldBinding>() : Parse(dataRoles!, "values");
            return report.AddDenebVisual(reportSessionId, page, name, spec, provider, renderMode, config,
                enableTooltips, enableSelection, enableHighlight, x, y, w, h, binds, visualGuid);
        });

    [McpServerTool(Name = "get_datamashup_info")]
    [Description("Report whether a .pbix has a DataMashup (Power Query M) part and the query names it declares. READ-ONLY. If absent, the M lives in the DataModel/TMDL (enhanced-metadata PBI_V3) - use get_table_m / set_partition_m instead.")]
    public static string GetDataMashupInfo(ReportService report,
        [Description("absolute path to the .pbix")] string pbixPath)
        => J.Try(() => report.GetDataMashupInfo(pbixPath));

    [McpServerTool(Name = "extract_power_query")]
    [Description("Extract the full Section1.m (the plain-text Power Query M for every query) from a .pbix's DataMashup part, plus the declared query names. READ-ONLY. If there is no DataMashup part, the M is in the DataModel/TMDL - use get_table_m.")]
    public static string ExtractPowerQuery(ReportService report,
        [Description("absolute path to the .pbix")] string pbixPath)
        => J.Try(() => report.ExtractPowerQuery(pbixPath));

    [McpServerTool(Name = "update_power_query")]
    [Description("Replace the ENTIRE Section1.m (the full 'section Section1; shared Query = ...;' document) inside a .pbix's DataMashup, clearing PermissionBindings (Desktop recomputes the SHA-256 on open). RISKY - work on a COPY; the .pbix must be CLOSED in Desktop; refuses protected client paths.")]
    public static string UpdatePowerQuery(ReportService report,
        [Description("absolute path to the .pbix (a COPY)")] string pbixPath,
        [Description("the full replacement Section1.m document")] string newM)
        => J.Try(() => report.UpdatePowerQuery(pbixPath, newM));

    [McpServerTool(Name = "rewrite_connection_string")]
    [Description("Find/replace a literal substring (a server/db name, file path, URL, ...) across Section1.m inside a .pbix's DataMashup, clearing PermissionBindings. Returns how many occurrences were replaced. RISKY - work on a COPY; the .pbix must be CLOSED in Desktop; refuses protected client paths.")]
    public static string RewriteConnectionString(ReportService report,
        [Description("absolute path to the .pbix (a COPY)")] string pbixPath,
        [Description("the literal substring to find")] string find,
        [Description("the replacement substring")] string replace)
        => J.Try(() => report.RewriteConnectionString(pbixPath, find, replace));

    // ================================================================ Wave G2: read-only audits + read-back

    [McpServerTool(Name = "validate_wireframe")]
    [Description("READ-ONLY layout lint over visual positions + page size (no fixes applied): visual OVERLAP pairs, OFF-CANVAS placement (negative or beyond the page bounds), tiny/zero-size visuals, z-order anomalies (a data visual rendering ABOVE an overlapping slicer), plus margin/gap statistics per page. Every violation names the existing fixer tool (auto_arrange / align_visuals / tidy_slicer_layout_v2 / move_visual / resize_visual / set_visual_z_order). Accepts a LEGACY reportSessionId (from open_report) OR a PBIR pbirSessionId (from read_pbir) - the same shared geometry checker runs over both readers.")]
    public static string ValidateWireframe(ReportService report, PbirService pbir,
        [Description("a reportSessionId (open_report) or pbirSessionId (read_pbir)")] string reportSessionId,
        [Description("one page (name or displayName); omit for every page")] string? pageName = null)
        => J.Try(() => reportSessionId.StartsWith("pbir", StringComparison.OrdinalIgnoreCase)
            ? pbir.ValidateWireframe(reportSessionId, pageName)
            : report.ValidateWireframe(reportSessionId, pageName));

    [McpServerTool(Name = "audit_theme_compliance")]
    [Description("READ-ONLY theme lint: walk every visual's objects/vcObjects formatting trees against the report's custom theme (read_theme's defaults) and report hard-coded overrides that fight it - off-palette colour literals, on-palette colours that FREEZE the palette so a theme swap will not restyle them, title font family/size overrides of the theme text classes, and per-visual card-style overrides where the theme's visualStyles already set the look.")]
    public static string AuditThemeCompliance(ReportService report, string reportSessionId)
        => J.Try(() => report.AuditThemeCompliance(reportSessionId));

    [McpServerTool(Name = "extract_report_colors")]
    [Description("READ-ONLY report-wide colour inventory: every hardcoded colour literal across all visuals AND the theme, with the exact locations of each occurrence (theme.dataColors[2], page/visual objects paths). The scouting pass before recolor_report.")]
    public static string ExtractReportColors(ReportService report, string reportSessionId)
        => J.Try(() => report.ExtractReportColors(reportSessionId));

    [McpServerTool(Name = "recolor_report")]
    [Description("Find/replace colour literals across ALL visuals and the theme in one call: colorMap = JSON {\"#OLD\":\"#NEW\", ...}. Handles both the plain theme spelling (#RRGGBB) and the quoted expr spelling ('#RRGGBB') inside visual objects, preserving each occurrence's form. Returns per-colour replacement counts; run save_report to persist.")]
    public static string RecolorReport(ReportService report, string reportSessionId,
        [Description("JSON object of oldColour -> newColour hex literals")] string colorMap)
        => J.Try(() => report.RecolorReport(reportSessionId, colorMap));

    [McpServerTool(Name = "document_report")]
    [Description("One-call Markdown documentation artifact for a report: pages, every visual (type, position, size, title, field bindings), page/report filter counts, bookmarks and the theme - rendered purely from the existing readers. When outPath is given the Markdown is also written to disk.")]
    public static string DocumentReport(ReportService report, string reportSessionId,
        [Description("optional path to write the .md file to")] string? outPath = null)
        => J.Try(() => report.DocumentReport(reportSessionId, outPath));

    [McpServerTool(Name = "get_report_filters")]
    [Description("READ-ONLY read-back of the report's filter surface: every report / page / visual-level filter parsed to structured form - scope, table[field], filter type, lock/hide flags, and the decoded condition (values list, comparison op + value, topN, relative date/time, and/or chains). The read partner of the add_*_filter tools.")]
    public static string GetReportFilters(ReportService report, string reportSessionId)
        => J.Try(() => report.GetReportFilters(reportSessionId));

    [McpServerTool(Name = "get_report_settings")]
    [Description("READ-ONLY read-back of the report-level behaviour toggles (config.settings) that set_report_settings writes, decoded to plain values, plus the custom-theme name when one is applied. The read partner of set_report_settings.")]
    public static string GetReportSettings(ReportService report, string reportSessionId)
        => J.Try(() => report.GetReportSettings(reportSessionId));

    [McpServerTool(Name = "get_slicer_defaults")]
    [Description("READ-ONLY read-back of every slicer's selection state: bound field, strictSingleSelect / singleSelect flags, display mode, and any default selection values written as Categorical filters in the slicer's own container. The read partner of set_slicer_selection / fix_slicer_single_select. Scope to one page or omit for all pages.")]
    public static string GetSlicerDefaults(ReportService report, string reportSessionId,
        [Description("one page (name or displayName); omit for every page")] string? page = null)
        => J.Try(() => report.GetSlicerDefaults(reportSessionId, page));

    [McpServerTool(Name = "pbix_doctor")]
    [Description("READ-ONLY 17-point file-level container scan of a CLOSED .pbix: zip part inventory vs expected parts, Version/DataModel/DataMashup presence and sizes, stale SecurityBindings and DataMashup PermissionBindings, sensitivity-label parts, zero-byte / truncated / duplicate parts, and whether the report part is legacy or PBIR. Never writes.")]
    public static string PbixDoctorTool(
        [Description("absolute path to the .pbix")] string pbixPath)
        => J.Try(() => PbixDoctor.Run(pbixPath));

    [McpServerTool(Name = "audit_datamashup_credentials")]
    [Description("READ-ONLY credential audit of a .pbix's DataMashup: reports whether the embedded M carries credential material (connection strings with Password=/pwd=/AccountKey=/SAS tokens/etc) and whether a PermissionBindings blob is present. Reports PRESENCE and LOCATION only (indicator, query, line number) - secret values are NEVER echoed.")]
    public static string AuditDataMashupCredentials(ReportService report,
        [Description("absolute path to the .pbix")] string pbixPath)
        => J.Try(() => report.AuditDataMashupCredentials(pbixPath));

    // ============================================================================================
    //  WAVE G3 REPORT ERGONOMICS - broken-binding repair, visual-type change with role remapping,
    //  bulk per-visual batches, the query data-role catalogue, and the visual-side field-parameter
    //  binder. All on the legacy Layout path (the reportSessionId tools).
    // ============================================================================================

    [McpServerTool(Name = "fix_broken_visuals")]
    [Description("Repair visual bindings that point at RENAMED or MOVED model fields (found by scan_broken_refs). repairMap = JSON {\"Old Table[Old Field]\":\"New Table[New Field]\", ...}. Rewrites the same binding paths set_visual_fields writes - prototypeQuery From/Select (aliases repointed when the table changed), projections queryRefs, sorts, visual filters, and the conditional-formatting / chrome bindings - plus page and report filters. Returns one row per changed target and lists any repair key that never matched.")]
    public static string FixBrokenVisuals(ReportService report, string reportSessionId,
        [Description("JSON object mapping broken refs to replacements: {\"T[F]\":\"T2[F2]\", ...}")] string repairMap)
        => J.Try(() => report.FixBrokenVisuals(reportSessionId, repairMap));

    [McpServerTool(Name = "change_visual_type")]
    [Description("Change a visual's TYPE preserving its data bindings, position and applicable formatting. A deprecated target is modernised automatically (card -> cardVisual, table -> tableEx, matrix -> pivotTable). Projection roles are remapped through the curated data-role registry (list_visual_data_roles): same-named roles carry straight over, the rest fall to the first compatible role by kind, and per-role caps drop overflow (reported, never silent). Data-formatting cards the new type does not declare are dropped and reported; chrome (title/background/border) always survives. Sort is cleared - re-apply with set_visual_sort.")]
    public static string ChangeVisualType(ReportService report, PropertyCatalog catalog, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name")] string visual,
        [Description("the new visualType, e.g. lineChart | clusteredBarChart | tableEx (or a deprecated alias: card/table/matrix)")] string newType)
        => J.Try(() => report.ChangeVisualType(reportSessionId, page, visual, newType, catalog));

    [McpServerTool(Name = "bulk_bind_visuals")]
    [Description("BATCH set_visual_fields: rebind MANY visuals in one call instead of one round-trip each. items = JSON array of {page, visual, bindings:[{role,table,field,kind}, ...]}. One result row per target; a failing item never aborts the rest (partial failure is reported per item, never silently all-or-nothing).")]
    public static string BulkBindVisuals(ReportService report, string reportSessionId,
        [Description("JSON array of {page, visual, bindings:[{role,table,field,kind}]}")] string items)
        => J.Try(() => report.BulkBindVisuals(reportSessionId, items));

    [McpServerTool(Name = "bulk_set_visual_format")]
    [Description("BATCH set_visual_format: apply formatting to MANY visuals in one call. items = JSON array of {page, visual, format:{vcObjects:{card:{prop:value}}, objects:{card:{prop:value}}}} (format takes the exact set_visual_format formatJson shape). One result row per target; per-item failures reported, the rest still apply.")]
    public static string BulkSetVisualFormat(ReportService report, string reportSessionId,
        [Description("JSON array of {page, visual, format:{...}}")] string items)
        => J.Try(() => report.BulkSetVisualFormat(reportSessionId, items));

    [McpServerTool(Name = "bulk_delete_visuals")]
    [Description("BATCH delete_visual: delete MANY visuals in one call. items = JSON array of {page, visual}. One result row per target; a missing visual fails its own row only, the rest still delete.")]
    public static string BulkDeleteVisuals(ReportService report, string reportSessionId,
        [Description("JSON array of {page, visual}")] string items)
        => J.Try(() => report.BulkDeleteVisuals(reportSessionId, items));

    [McpServerTool(Name = "list_visual_data_roles")]
    [Description("The QUERY data roles a visual type takes (Category / Y / Series / Values / Rows / ...), each with what it accepts (Grouping | Measure | GroupingOrMeasure), its per-role field cap, and the deprecated -> modern type mapping (card -> cardVisual, table -> tableEx, matrix -> pivotTable). Role metadata is HAND-CURATED for the mainstream visual types because the bundled theme schema only describes formatting cards, not query roles - coverage is reported honestly (curated | none). The role half of the discovery surface; list_visual_properties is the formatting half.")]
    public static string ListVisualDataRoles(PropertyCatalog catalog,
        [Description("a visualType key, e.g. clusteredColumnChart, tableEx, scatterChart (deprecated aliases accepted)")] string visualType)
        => J.Try(() => VisualDataRoles.ListRoles(visualType, catalog.Knows(VisualDataRoles.Modernize(visualType))));

    [McpServerTool(Name = "bind_field_parameter")]
    [Description("Bind a FIELD PARAMETER to a visual so it actually swaps fields when opened in Desktop - the visual-side piece the model-side add_field_parameter cannot write. The chosen role's projection becomes the parameter COLUMN with active=true (the dynamic-projection marker) and the prototypeQuery Select swaps to the parameter column; the four model-side pieces (the NAMEOF calculated table, ParameterMetadata, SortByColumn, GroupByColumns) come from add_field_parameter - run it first. role defaults to the visual's measure well (Y, else Values).")]
    public static string BindFieldParameter(ReportService report, string reportSessionId,
        [Description("page name or displayName")] string page,
        [Description("visual name")] string visual,
        [Description("the field-parameter table name (from add_field_parameter)")] string parameterTable,
        [Description("the parameter's display column; defaults to the table name (add_field_parameter's convention)")] string? parameterColumn = null,
        [Description("the projection role to swap (Y | Values | Category | ...); defaults to Y, else Values")] string? role = null)
        => J.Try(() => report.BindFieldParameter(reportSessionId, page, visual, parameterTable, parameterColumn, role));
}
