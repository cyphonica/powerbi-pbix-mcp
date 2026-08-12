namespace SuperBiMcp.Services;

/// <summary>
/// Wave G3 QUERY data-role catalogue: for each mainstream visual type, the projection roles its
/// query takes (Category / Y / Series / Values / Rows / ...), each role's kind (grouping column,
/// measure, or either) and its cardinality cap where the visual enforces one. The report-theme
/// schema the PropertyCatalog ingests only describes FORMATTING cards, not query roles, so this
/// half is hand-curated from the visual capabilities of the bundled Desktop build - coverage is
/// reported honestly per type (curated vs none). Also owns the deprecated -> modern visual-type
/// mapping (card -> cardVisual, table -> tableEx, matrix -> pivotTable) that change_visual_type
/// applies. Pure static data + lookups - fully unit-testable.
/// </summary>
public static class VisualDataRoles
{
    /// <summary>One query data role: name, what it accepts, and the per-role field cap (null = no cap).</summary>
    public sealed record RoleInfo(string Name, string Kind, int? Max, string Description)
    {
        public bool AcceptsMeasure => Kind is "Measure" or "GroupingOrMeasure";
        public bool AcceptsGrouping => Kind is "Grouping" or "GroupingOrMeasure";
    }

    /// <summary>Deprecated -> modern visual-type mapping (the legacy names Desktop still reads but
    /// no longer writes).</summary>
    public static readonly IReadOnlyDictionary<string, string> DeprecatedToModern =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["card"] = "cardVisual",
            ["table"] = "tableEx",
            ["matrix"] = "pivotTable",
        };

    /// <summary>Reverse view of <see cref="DeprecatedToModern"/> (modern -> its legacy alias).</summary>
    public static readonly IReadOnlyDictionary<string, string> ModernToDeprecated =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cardVisual"] = "card",
            ["tableEx"] = "table",
            ["pivotTable"] = "matrix",
        };

    private static RoleInfo G(string name, int? max, string desc) => new(name, "Grouping", max, desc);
    private static RoleInfo M(string name, int? max, string desc) => new(name, "Measure", max, desc);
    private static RoleInfo E(string name, int? max, string desc) => new(name, "GroupingOrMeasure", max, desc);

    private static readonly RoleInfo[] CartesianRoles =
    {
        G("Category", null, "the axis (multiple fields form a drill hierarchy)"),
        G("Series", 1, "the legend / colour split"),
        M("Y", null, "the plotted values"),
        E("Tooltips", null, "extra hover fields"),
    };

    // hand-curated per-type role sets; keys are the canonical visualType ids the builders use
    private static readonly Dictionary<string, RoleInfo[]> Roles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["card"] = new[] { M("Values", 1, "the single number shown") },
        ["cardVisual"] = new[] { M("Values", null, "the numbers shown (the new card takes several)") },
        ["multiRowCard"] = new[] { E("Values", null, "the fields listed per card row") },
        ["kpi"] = new[] { M("Indicator", 1, "the value"), G("TrendAxis", 1, "the trend axis"), M("Goal", 2, "target goal(s)") },
        ["gauge"] = new[] { M("Y", 1, "the value"), M("MinValue", 1, "gauge minimum"), M("MaxValue", 1, "gauge maximum"), M("TargetValue", 1, "the target marker") },
        ["tableEx"] = new[] { E("Values", null, "the columns, left to right") },
        ["table"] = new[] { E("Values", null, "the columns, left to right") },
        ["pivotTable"] = new[] { G("Rows", null, "row groups"), G("Columns", null, "column groups"), M("Values", null, "the cell measures") },
        ["matrix"] = new[] { G("Rows", null, "row groups"), G("Columns", null, "column groups"), M("Values", null, "the cell measures") },
        ["slicer"] = new[] { E("Values", 1, "the field sliced") },
        ["barChart"] = CartesianRoles,
        ["columnChart"] = CartesianRoles,
        ["clusteredBarChart"] = CartesianRoles,
        ["clusteredColumnChart"] = CartesianRoles,
        ["hundredPercentStackedBarChart"] = CartesianRoles,
        ["hundredPercentStackedColumnChart"] = CartesianRoles,
        ["lineChart"] = CartesianRoles,
        ["areaChart"] = CartesianRoles,
        ["stackedAreaChart"] = CartesianRoles,
        ["ribbonChart"] = CartesianRoles,
        ["lineStackedColumnComboChart"] = new[]
        {
            G("Category", null, "the shared axis"), G("Series", 1, "the column legend"),
            M("Y", null, "column values"), M("Y2", null, "line values"), E("Tooltips", null, "extra hover fields"),
        },
        ["lineClusteredColumnComboChart"] = new[]
        {
            G("Category", null, "the shared axis"), G("Series", 1, "the column legend"),
            M("Y", null, "column values"), M("Y2", null, "line values"), E("Tooltips", null, "extra hover fields"),
        },
        ["pieChart"] = new[] { G("Category", 1, "the legend slices"), G("Details", 1, "slice detail split"), M("Y", null, "slice sizes"), E("Tooltips", null, "extra hover fields") },
        ["donutChart"] = new[] { G("Category", 1, "the legend slices"), G("Details", 1, "slice detail split"), M("Y", null, "slice sizes"), E("Tooltips", null, "extra hover fields") },
        ["funnel"] = new[] { G("Category", 1, "the funnel stages"), M("Y", 1, "the stage values"), E("Tooltips", null, "extra hover fields") },
        ["waterfallChart"] = new[] { G("Category", 1, "the axis"), G("Breakdown", 1, "the per-step breakdown"), M("Y", 1, "the values"), E("Tooltips", null, "extra hover fields") },
        ["treemap"] = new[] { G("Group", 1, "top-level tiles"), G("Details", 1, "tile subdivision"), M("Values", 1, "tile sizes"), E("Tooltips", null, "extra hover fields") },
        ["scatterChart"] = new[]
        {
            G("Category", 1, "detail points"), G("Series", 1, "the legend"),
            M("X", 1, "x-axis measure"), M("Y", 1, "y-axis measure"), M("Size", 1, "bubble size"),
            G("Play", 1, "the play axis"), E("Tooltips", null, "extra hover fields"),
        },
    };

    /// <summary>The role set for a visual type, or null when no curated metadata exists.</summary>
    public static IReadOnlyList<RoleInfo>? RolesFor(string visualType)
    {
        if (string.IsNullOrWhiteSpace(visualType)) return null;
        return Roles.TryGetValue(visualType.Trim(), out var r) ? r : null;
    }

    /// <summary>The modern equivalent when the given type is deprecated; otherwise the type itself.</summary>
    public static string Modernize(string visualType) =>
        DeprecatedToModern.TryGetValue((visualType ?? "").Trim(), out var modern) ? modern : (visualType ?? "").Trim();

    /// <summary>list_visual_data_roles: roles + cardinality + the deprecated/modern mapping for one
    /// type, with the coverage stated honestly. formattingRegistryKnows reflects the bundled
    /// report-theme schema (the PropertyCatalog), which is a separate axis from role curation.</summary>
    public static object ListRoles(string visualType, bool formattingRegistryKnows)
    {
        string asked = (visualType ?? "").Trim();
        string modern = Modernize(asked);
        var roles = RolesFor(asked) ?? RolesFor(modern);
        bool deprecated = DeprecatedToModern.ContainsKey(asked);
        return new
        {
            ok = true,
            visualType = asked,
            deprecated,
            modernEquivalent = deprecated ? modern : null,
            legacyAlias = ModernToDeprecated.TryGetValue(asked, out var old) ? old : null,
            coverage = roles != null ? "curated" : "none",
            formattingRegistryKnows,
            roles = roles?.Select(r => new
            {
                role = r.Name,
                accepts = r.Kind,
                maxFields = r.Max,
                description = r.Description,
            }).ToArray(),
            note = roles != null
                ? "Role metadata is hand-curated for the mainstream visual types (the bundled theme schema only describes formatting cards, not query roles)."
                : "No curated role metadata for this visual type - the theme schema does not carry query roles, and this type is outside the hand-curated set. Read an existing visual of this type (get_visual_format / get_pbir_visual) to see its projections.",
            deprecatedToModern = DeprecatedToModern,
        };
    }
}
