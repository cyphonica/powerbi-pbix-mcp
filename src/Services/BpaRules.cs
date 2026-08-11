using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>
/// The Best Practice Analyzer (BPA) quality engine: a structured catalogue of lint rules - each with a
/// pure check predicate over the Tabular Object Model and an optional autofix that names the precise TOM
/// property it sets - plus the runner, the fixer and a Tabular Editor ruleset importer.
///
/// The catalogue merges the two community rulesets: TabularEditor/BestPracticeRules and
/// semantic-link-labs' _model_bpa_rules. Rule IDs match those sources (UPPER_SNAKE) where they exist so a
/// user can cross-reference. Everything here is static and operates on a <c>new TOM.Model()</c> tree, so it
/// is fully unit-testable with no live Analysis Services server.
/// </summary>
public static class BpaRules
{
    // ---------------------------------------------------------------- the BpaRule shape

    public enum BpaScope { Model, Table, Column, Measure, Relationship, Partition, Hierarchy }

    /// <summary>An object flagged by a rule, plus a handle to the live TOM object for the fixer.</summary>
    public sealed record BpaTarget(string ObjectType, string ObjectName, TOM.MetadataObject? Tom);

    /// <summary>
    /// A single best-practice rule. <see cref="Check"/> returns the objects that violate it.
    /// <see cref="Fix"/> (optional) mutates the precise TOM property and returns the description of the
    /// change; rules with no safe automatic remedy leave <see cref="Fix"/> null and are flag-only.
    /// <see cref="FixedProperty"/> documents which property the autofix sets (for the catalogue + guidance).
    /// </summary>
    public sealed record BpaRule(
        string Id,
        string Category,
        int Severity,                       // 1 info, 2 warning, 3 error
        BpaScope Scope,
        string Description,
        Func<TOM.Model, IEnumerable<BpaTarget>> Check,
        string? FixedProperty = null,
        Func<TOM.MetadataObject, string>? Fix = null,
        bool ManualOnly = false)            // imported TE-expression rules we cannot evaluate
    {
        public bool Fixable => Fix != null;
    }

    public const int Info = 1, Warning = 2, Error = 3;

    public const string CatPerformance = "Performance";
    public const string CatDax = "DAXExpressions";
    public const string CatError = "ErrorPrevention";
    public const string CatMaintenance = "Maintenance";
    public const string CatNaming = "NamingConventions";
    public const string CatFormatting = "Formatting";
    public const string CatMetadata = "Metadata";
    public const string CatLayout = "RelationshipsLayout";

    // ---------------------------------------------------------------- small TOM helpers (pure)

    private static bool IsAutoDate(string n) =>
        n.StartsWith("LocalDateTable", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("DateTableTemplate", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<TOM.Table> UserTables(TOM.Model m) =>
        m.Tables.Where(t => !IsAutoDate(t.Name));

    private static IEnumerable<TOM.Column> DataCols(TOM.Table t) =>
        t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber);

    private static bool IsCalcGroup(TOM.Table t) => t.CalculationGroup != null;

    private static BpaTarget T(TOM.Table t) => new("Table", t.Name, t);
    private static BpaTarget T(TOM.Column c) => new("Column", $"{c.Table.Name}[{c.Name}]", c);
    private static BpaTarget T(TOM.Measure me) => new("Measure", $"{me.Table.Name}[{me.Name}]", me);
    private static BpaTarget T(TOM.Model m) => new("Model", string.IsNullOrEmpty(m.Name) ? "Model" : m.Name, m);
    private static BpaTarget T(TOM.Hierarchy h) => new("Hierarchy", $"{h.Table.Name}[{h.Name}]", h);
    private static BpaTarget T(TOM.Partition p) => new("Partition", $"{p.Table.Name}.{p.Name}", p);
    private static BpaTarget T(TOM.SingleColumnRelationship r) => new("Relationship",
        r.Name ?? $"{Safe(r.FromTable)}->{Safe(r.ToTable)}", r);

    private static string Safe(TOM.Table? t) => t?.Name ?? "?";

    private static readonly HashSet<TOM.DataType> Numeric = new()
        { TOM.DataType.Int64, TOM.DataType.Double, TOM.DataType.Decimal };

    /// <summary>Strip block / line comments + string literals so DAX scans don't false-positive on text.</summary>
    private static string StripDax(string? dax)
    {
        if (string.IsNullOrEmpty(dax)) return "";
        var s = Regex.Replace(dax, @"/\*.*?\*/", " ", RegexOptions.Singleline);   // /* ... */
        s = Regex.Replace(s, @"//[^\n\r]*", " ");                                  // // ...
        s = Regex.Replace(s, "\"(?:[^\"]|\"\")*\"", "\"\"");                       // "string literals"
        return s;
    }

    /// <summary>Word-boundary, case-insensitive DAX function presence test on the stripped expression.</summary>
    private static bool UsesFunc(string strippedDax, string func) =>
        Regex.IsMatch(strippedDax, $@"\b{Regex.Escape(func)}\s*\(", RegexOptions.IgnoreCase);

    // a column is "key-like" by name (FK/PK heuristic for the relationship-layer rules).
    private static bool KeyLikeName(string n) =>
        n.EndsWith("Key", StringComparison.OrdinalIgnoreCase) ||
        n.EndsWith("ID", StringComparison.OrdinalIgnoreCase) ||
        n.Equals("Id", StringComparison.OrdinalIgnoreCase);

    private static HashSet<TOM.Column> RelationshipColumns(TOM.Model m)
    {
        var set = new HashSet<TOM.Column>();
        foreach (var r in m.Relationships.OfType<TOM.SingleColumnRelationship>())
        {
            if (r.FromColumn != null) set.Add(r.FromColumn);
            if (r.ToColumn != null) set.Add(r.ToColumn);
        }
        return set;
    }

    // the "many"/foreign-key side columns (relationship FromColumn on a Many cardinality).
    private static HashSet<TOM.Column> ForeignKeyColumns(TOM.Model m)
    {
        var set = new HashSet<TOM.Column>();
        foreach (var r in m.Relationships.OfType<TOM.SingleColumnRelationship>())
            if (r.FromColumn != null && r.FromCardinality == TOM.RelationshipEndCardinality.Many)
                set.Add(r.FromColumn);
        return set;
    }

    // tables on the "many" side of at least one relationship = fact-like.
    private static HashSet<TOM.Table> FactTables(TOM.Model m)
    {
        var set = new HashSet<TOM.Table>();
        foreach (var r in m.Relationships.OfType<TOM.SingleColumnRelationship>())
            if (r.FromTable != null && r.FromCardinality == TOM.RelationshipEndCardinality.Many)
                set.Add(r.FromTable);
        return set;
    }

    private static bool HasDateTable(TOM.Model m) => UserTables(m).Any(t =>
        string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase) ||
        t.Columns.Any(c => string.Equals(c.DataCategory, "Time", StringComparison.OrdinalIgnoreCase) && c.IsKey));

    // ================================================================ the catalogue

    private static List<BpaRule>? _catalogue;
    public static IReadOnlyList<BpaRule> All => _catalogue ??= Build();

    public static BpaRule? ById(string id) =>
        All.FirstOrDefault(r => string.Equals(r.Id, id, StringComparison.OrdinalIgnoreCase));

    private static List<BpaRule> Build()
    {
        var rules = new List<BpaRule>();
        void R(BpaRule r) => rules.Add(r);

        // ---------------------------------------------------------- Performance ----------------------------------------------------------

        // ISAVAILABLEINMDX false on hidden non-attribute (non sort-by-target, non-key) columns. Autofix.
        R(new("ISAVAILABLEINMDX_FALSE_NONATTRIBUTE_COLUMNS", CatPerformance, Warning, BpaScope.Column,
            "Set IsAvailableInMDX = false on hidden columns that are not a sort-by target and not a key - saves memory/processing.",
            m =>
            {
                var sortTargets = SortByTargets(m);
                return UserTables(m).SelectMany(DataCols)
                    .Where(c => c.IsHidden && c.IsAvailableInMDX && !c.IsKey && !sortTargets.Contains(c)
                                && !IsCalcGroup(c.Table))
                    .Select(T);
            },
            FixedProperty: "Column.IsAvailableInMDX = false",
            Fix: o => { ((TOM.Column)o).IsAvailableInMDX = false; return $"{Name(o)}: IsAvailableInMDX -> false"; }));

        R(new("MARK_PRIMARY_KEYS", CatPerformance, Info, BpaScope.Column,
            "Mark each dimension's primary-key column with IsKey - the engine uses it for relationship integrity and joins.",
            m =>
            {
                var oneSideKeys = OneSideKeyColumns(m);
                return oneSideKeys.Where(c => !c.IsKey && c.Type != TOM.ColumnType.RowNumber).Select(T);
            },
            FixedProperty: "Column.IsKey = true",
            Fix: o => { ((TOM.Column)o).IsKey = true; return $"{Name(o)}: IsKey -> true"; }));

        R(new("AVOID_BIDIRECTIONAL_RELATIONSHIPS", CatPerformance, Warning, BpaScope.Relationship,
            "Avoid bi-directional relationships - they slow queries and can produce ambiguous filter paths. Autofix sets single-direction.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => r.CrossFilteringBehavior == TOM.CrossFilteringBehavior.BothDirections).Select(T),
            FixedProperty: "SingleColumnRelationship.CrossFilteringBehavior = OneDirection",
            Fix: o => { ((TOM.SingleColumnRelationship)o).CrossFilteringBehavior = TOM.CrossFilteringBehavior.OneDirection;
                        return $"{Name(o)}: CrossFilteringBehavior -> OneDirection"; }));

        R(new("AUTO_DATE_TIME_ON", CatPerformance, Warning, BpaScope.Model,
            "Auto date/time generates a hidden date table per date column, bloating the model. Turn it off and use a single marked date table.",
            m => m.Tables.Any(t => IsAutoDate(t.Name)) ? new[] { T(m) } : Enumerable.Empty<BpaTarget>()));

        R(new("LARGE_TABLES_SHOULD_BE_PARTITIONED_OR_DUAL", CatPerformance, Info, BpaScope.Table,
            "Large import tables benefit from Dual storage mode (or partitioning) so composite/Direct Lake queries can fold; review big single-partition tables.",
            m => UserTables(m).Where(t => !IsCalcGroup(t) && t.Partitions.Count == 1
                    && t.Partitions[0].Mode == TOM.ModeType.Import && DataCols(t).Count() > 30).Select(T)));

        R(new("MANY_PARTITIONS", CatPerformance, Info, BpaScope.Table,
            "A table with very many partitions is hard to refresh and maintain - consider incremental-refresh policy partitioning instead.",
            m => UserTables(m).Where(t => t.Partitions.Count > 25).Select(T)));

        R(new("SET_ENCODING_HINT_FOR_LARGE_FACTS", CatPerformance, Info, BpaScope.Column,
            "Numeric fact measure-base columns can be hinted to Value encoding to skip dictionary build; review high-cardinality numeric fact columns.",
            m =>
            {
                var facts = FactTables(m);
                return facts.SelectMany(DataCols).Where(c => Numeric.Contains(c.DataType)
                    && c.EncodingHint == TOM.EncodingHintType.Default && !KeyLikeName(c.Name)).Select(T);
            }));

        R(new("DISABLE_STRING_LENGTH_ATTRIBUTES", CatPerformance, Info, BpaScope.Model,
            "Setting an explicit DataCoverageDefinition / encoding hints model-wide helps VertiPaq; review model storage settings.",
            m => Enumerable.Empty<BpaTarget>()));  // descriptive guardrail (no reliable TOM signal)

        R(new("MINIMISE_CALCULATED_COLUMNS", CatPerformance, Warning, BpaScope.Column,
            "Calculated columns cost memory and refresh time; push the logic into Power Query (M) or a measure where possible.",
            m => UserTables(m).SelectMany(t => t.Columns.OfType<TOM.CalculatedColumn>()).Select(T)));

        R(new("REMOVE_REDUNDANT_RELATIONSHIPS", CatPerformance, Info, BpaScope.Relationship,
            "Two relationships connecting the same pair of tables (only one active) add ambiguity overhead - confirm both are needed.",
            m =>
            {
                var rels = m.Relationships.OfType<TOM.SingleColumnRelationship>()
                    .Where(r => r.FromTable != null && r.ToTable != null).ToList();
                var dups = new List<BpaTarget>();
                foreach (var grp in rels.GroupBy(r => PairKey(r.FromTable!.Name, r.ToTable!.Name)))
                    if (grp.Count() > 1) dups.AddRange(grp.Select(T));
                return dups;
            }));

        R(new("AVOID_CALCULATED_TABLES", CatPerformance, Info, BpaScope.Table,
            "Calculated tables are evaluated in the model and add memory; prefer a Power Query / source table where feasible.",
            m => UserTables(m).Where(t => !IsCalcGroup(t)
                    && t.Partitions.Any(p => p.Source is TOM.CalculatedPartitionSource)).Select(T)));

        // ---------------------------------------------------------- DAXExpressions ----------------------------------------------------------

        R(new("DAX_USE_DIVIDE", CatDax, Warning, BpaScope.Measure,
            "Use DIVIDE(n, d) instead of the '/' operator so divide-by-zero returns BLANK instead of an error/infinity.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => HasRawDivision(me.Expression)).Select(T)));   // flag-only (no safe auto-rewrite)

        R(new("USE_TREATAS_INSTEAD_OF_INTERSECT", CatDax, Warning, BpaScope.Measure,
            "Use TREATAS for virtual relationships instead of INTERSECT - it is faster and clearer.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => UsesFunc(StripDax(me.Expression), "INTERSECT")).Select(T)));

        R(new("SUMMARIZE_WITHOUT_ADDCOLUMNS", CatDax, Warning, BpaScope.Measure,
            "Do not add columns inside SUMMARIZE; use SUMMARIZE for grouping only and ADDCOLUMNS for the additions (avoids context-transition bugs).",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => SummarizeAddsColumns(me.Expression)).Select(T)));

        R(new("AVOID_IFERROR", CatDax, Warning, BpaScope.Measure,
            "IFERROR forces row-by-row evaluation and hides real errors; prefer DIVIDE / defensive logic.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => UsesFunc(StripDax(me.Expression), "IFERROR")).Select(T)));

        R(new("MEASURE_REFERENCES_COLUMN_DIRECTLY", CatDax, Warning, BpaScope.Measure,
            "A measure that wraps a single base column directly (e.g. SUM with no other logic) is fine, but referencing a column without an aggregator is invalid; review naked column references.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => ReferencesNakedColumn(me.Expression)).Select(T)));

        R(new("AVOID_COUNTROWS_FILTER_FOR_DISTINCTCOUNT", CatDax, Info, BpaScope.Measure,
            "COUNTROWS(DISTINCT(...)) is slower and noisier than DISTINCTCOUNT(...).",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => Regex.IsMatch(StripDax(me.Expression),
                       @"COUNTROWS\s*\(\s*DISTINCT\s*\(", RegexOptions.IgnoreCase)).Select(T)));

        R(new("FILTER_USING_TRUE_FALSE", CatDax, Info, BpaScope.Measure,
            "FILTER(Table, [Measure] = TRUE) is redundant; the predicate alone suffices.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => Regex.IsMatch(StripDax(me.Expression),
                       @"=\s*(TRUE|FALSE)\s*\(\s*\)", RegexOptions.IgnoreCase)).Select(T)));

        R(new("USE_VAR_INSTEAD_OF_REPEATED_EXPRESSIONS", CatDax, Info, BpaScope.Measure,
            "Long measures with the same sub-expression repeated should hoist it into a VAR for clarity and speed.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => (me.Expression?.Length ?? 0) > 400
                       && !UsesKeyword(me.Expression, "VAR")).Select(T)));

        R(new("AVOID_DIVIDE_BY_FIXED_ZERO_GUARD", CatDax, Info, BpaScope.Measure,
            "An IF([d]=0, BLANK(), n/[d]) guard is exactly what DIVIDE replaces - simplify with DIVIDE.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => Regex.IsMatch(StripDax(me.Expression ?? ""),
                       @"IF\s*\(.*=\s*0", RegexOptions.IgnoreCase | RegexOptions.Singleline)
                       && HasRawDivision(me.Expression)).Select(T)));

        R(new("AVOID_USING_FORMAT_FOR_NUMBERS", CatDax, Info, BpaScope.Measure,
            "FORMAT() returns text and breaks sorting/aggregation - use a measure FormatString or dynamic format-string expression instead.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => UsesFunc(StripDax(me.Expression), "FORMAT")).Select(T)));

        // ---------------------------------------------------------- ErrorPrevention ----------------------------------------------------------

        R(new("EVALUATEANDLOG_IN_PRODUCTION", CatError, Error, BpaScope.Measure,
            "EVALUATEANDLOG is a debugging function and must not ship to production - it leaks query internals and slows evaluation.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => UsesFunc(StripDax(me.Expression), "EVALUATEANDLOG")).Select(T),
            FixedProperty: "Measure.Expression (strip EVALUATEANDLOG(x) -> x)",
            Fix: o => { var me = (TOM.Measure)o; me.Expression = StripEvaluateAndLog(me.Expression);
                        return $"{Name(o)}: stripped EVALUATEANDLOG wrapper(s)"; }));

        R(new("RELATIONSHIP_COLUMNS_DATATYPE_MISMATCH", CatError, Error, BpaScope.Relationship,
            "Both sides of a relationship must share a data type; a mismatch silently drops to a blank member.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => r.FromColumn != null && r.ToColumn != null
                       && !SameTypeFamily(r.FromColumn.DataType, r.ToColumn.DataType)).Select(T)));

        R(new("INACTIVE_RELATIONSHIPS_NO_USERELATIONSHIP", CatError, Warning, BpaScope.Relationship,
            "An inactive relationship is dead weight unless a measure activates it with USERELATIONSHIP.",
            m =>
            {
                var allDax = AllDax(m);
                return m.Relationships.OfType<TOM.SingleColumnRelationship>()
                    .Where(r => !r.IsActive
                        && !allDax.Any(d => d.IndexOf("USERELATIONSHIP", StringComparison.OrdinalIgnoreCase) >= 0))
                    .Select(T);
            }));

        R(new("CALCULATED_COLUMNS_SHOULD_BE_M_OR_MEASURES", CatError, Info, BpaScope.Column,
            "Calculated columns referencing measures or RELATED can break with data changes; consider M or a measure.",
            m => UserTables(m).SelectMany(t => t.Columns.OfType<TOM.CalculatedColumn>())
                  .Where(c => UsesFunc(StripDax(c.Expression), "RELATED")
                       || Regex.IsMatch(StripDax(c.Expression), @"\[[^\]]+\]")).Select(T)));

        R(new("AVOID_FLOATING_POINT_DATA_TYPES", CatError, Warning, BpaScope.Column,
            "Currency/amount columns stored as Double (floating point) accumulate rounding error - use Decimal (Fixed Decimal). Flag (a blind retype would change values).",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => c.DataType == TOM.DataType.Double && Currencyish(c.Name)).Select(T)));   // flag-only

        R(new("MODEL_HAS_NO_DATE_TABLE_MARKED", CatError, Warning, BpaScope.Model,
            "No table is marked as a date table (DataCategory='Time' on a key date column) - time intelligence will be unreliable.",
            m => (!HasDateTable(m) && UserTables(m).Count() > 1) ? new[] { T(m) } : Enumerable.Empty<BpaTarget>()));

        R(new("UNDEFINED_SORT_BY_COLUMN_MISMATCH", CatError, Warning, BpaScope.Column,
            "A column whose SortByColumn has a different cardinality (more sort values than display values) will error at query time; verify the 1:1 mapping.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => c.SortByColumn != null && c.SortByColumn.Type == TOM.ColumnType.RowNumber).Select(T)));

        R(new("RELATIONSHIP_ON_HIGH_CARDINALITY_DATETIME", CatError, Info, BpaScope.Relationship,
            "Joining on a DateTime column with a time component causes mismatches; relate on a date-only column or integer date key.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => r.FromColumn?.DataType == TOM.DataType.DateTime
                       && r.ToColumn?.DataType == TOM.DataType.DateTime).Select(T)));

        R(new("CALC_GROUP_PRECEDENCE_GAP", CatError, Warning, BpaScope.Table,
            "When two calculation groups exist they must have distinct precedences; a gap/duplicate makes the apply order ambiguous.",
            m =>
            {
                var groups = UserTables(m).Where(IsCalcGroup).ToList();
                if (groups.Count < 2) return Enumerable.Empty<BpaTarget>();
                var precs = groups.Select(g => g.CalculationGroup.Precedence).ToList();
                return precs.Distinct().Count() != precs.Count ? groups.Select(T) : Enumerable.Empty<BpaTarget>();
            }));

        R(new("DATA_TYPE_OF_RELATIONSHIP_COLUMNS_SHOULD_BE_INTEGER", CatError, Info, BpaScope.Relationship,
            "Integer relationship keys join faster and more reliably than string keys.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => r.FromColumn?.DataType == TOM.DataType.String).Select(T)));

        R(new("M_CONTAINS_REF_ERROR", CatError, Error, BpaScope.Partition,
            "The M query text contains the literal #REF! - a deleted Excel range leaked into the query and the load will fail on refresh.",
            m => UserTables(m).SelectMany(t => t.Partitions)
                  .Where(p => p.Source is TOM.MPartitionSource mp && (mp.Expression ?? "").Contains("#REF!")).Select(T)
                  .Concat(m.Expressions
                      .Where(e => e.Kind == TOM.ExpressionKind.M && (e.Expression ?? "").Contains("#REF!"))
                      .Select(e => new BpaTarget("Expression", e.Name, e)))));   // flag-only

        R(new("BROKEN_SOURCE_NAMED_RANGE", CatError, Error, BpaScope.Partition,
            "The partition loads a DefinedName from an Excel workbook whose defined names contain #REF! (broken named ranges) - scheduled refresh will fail until the workbook is repaired.",
            m => UserTables(m).SelectMany(t => t.Partitions).SelectMany(p =>
            {
                if (p.Source is not TOM.MPartitionSource mp) return Enumerable.Empty<BpaTarget>();
                string expr = mp.Expression ?? "";
                if (!Regex.IsMatch(expr, @"Kind\s*=\s*""DefinedName""", RegexOptions.IgnoreCase))
                    return Enumerable.Empty<BpaTarget>();
                var path = Regex.Match(expr, @"File\.Contents\(\s*""([^""]+\.xls[xmb]?)""\s*\)", RegexOptions.IgnoreCase);
                if (!path.Success) return Enumerable.Empty<BpaTarget>();
                try   // per-partition isolation: a missing/locked workbook must never crash or false-positive the rule
                {
                    string workbook = path.Groups[1].Value;
                    if (!File.Exists(workbook)) return Enumerable.Empty<BpaTarget>();
                    var broken = ExcelService.ReadDefinedNames(workbook)
                        .Where(d => d.RefersTo.Contains("#REF!")).Select(d => d.Name).ToList();
                    if (broken.Count == 0) return Enumerable.Empty<BpaTarget>();
                    return new[] { new BpaTarget("Partition",
                        $"{p.Table.Name}.{p.Name} (broken names: {string.Join(", ", broken)})", p) };
                }
                catch { return Enumerable.Empty<BpaTarget>(); }
            })));   // flag-only

        // ---------------------------------------------------------- Maintenance ----------------------------------------------------------

        R(new("UNUSED_COLUMNS", CatMaintenance, Warning, BpaScope.Column,
            "A column referenced nowhere (no relationship, sort-by, hierarchy, RLS or DAX) is dead weight - remove or hide it.",
            m => UnusedColumns(m).Select(T),
            FixedProperty: "Column.IsHidden = true (conservative - hide rather than delete)",
            Fix: o => { ((TOM.Column)o).IsHidden = true; return $"{Name(o)}: IsHidden -> true (unused; hidden, not deleted)"; }));

        R(new("UNUSED_MEASURES", CatMaintenance, Info, BpaScope.Measure,
            "A measure referenced by no other measure (and not used in the report layer) may be dead - confirm before removing.",
            m => UnusedMeasures(m).Select(T)));   // flag-only (report-layer usage not visible here)

        R(new("OBJECTS_WITH_NO_DISPLAY_FOLDER", CatMaintenance, Info, BpaScope.Measure,
            "Measures with no DisplayFolder clutter the field list; group them into folders.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => string.IsNullOrWhiteSpace(me.DisplayFolder)).Select(T)));

        R(new("CALCULATION_ITEMS_MUST_HAVE_EXPRESSION", CatMaintenance, Error, BpaScope.Table,
            "Every calculation item must have a non-empty DAX expression.",
            m => UserTables(m).Where(IsCalcGroup)
                  .Where(t => t.CalculationGroup.CalculationItems.Any(ci => string.IsNullOrWhiteSpace(ci.Expression)))
                  .Select(T)));

        R(new("REMOVE_ROLES_WITH_NO_MEMBERS", CatMaintenance, Info, BpaScope.Model,
            "A security role with no members and no table permissions does nothing - remove it.",
            m => m.Roles.Any(r => r.Members.Count == 0 && r.TablePermissions.Count == 0)
                 ? new[] { T(m) } : Enumerable.Empty<BpaTarget>()));

        R(new("PERSPECTIVES_SHOULD_HAVE_OBJECTS", CatMaintenance, Info, BpaScope.Model,
            "An empty perspective exposes the whole model - populate it or remove it.",
            m => m.Perspectives.Any(p => p.PerspectiveTables.Count == 0)
                 ? new[] { T(m) } : Enumerable.Empty<BpaTarget>()));

        R(new("PARTITION_QUERY_SHOULD_NOT_USE_SELECT_STAR", CatMaintenance, Info, BpaScope.Partition,
            "A partition that pulls SELECT * (or the whole table with no column projection) loads columns you do not need.",
            m => UserTables(m).SelectMany(t => t.Partitions)
                  .Where(p => p.Source is TOM.MPartitionSource mp
                       && Regex.IsMatch(mp.Expression ?? "", @"select\s+\*", RegexOptions.IgnoreCase)).Select(T)));

        R(new("DISCOURAGE_IMPLICIT_MEASURES", CatMaintenance, Info, BpaScope.Model,
            "Set DiscourageImplicitMeasures = true so users build explicit measures rather than dragging raw columns to the values well.",
            m => !m.DiscourageImplicitMeasures ? new[] { T(m) } : Enumerable.Empty<BpaTarget>(),
            FixedProperty: "Model.DiscourageImplicitMeasures = true",
            Fix: o => { ((TOM.Model)o).DiscourageImplicitMeasures = true; return "Model: DiscourageImplicitMeasures -> true"; }));

        R(new("IMPLICIT_MEASURES_DISCOURAGED", CatMaintenance, Info, BpaScope.Column,
            "When DiscourageImplicitMeasures is off, numeric non-hidden fact columns get a default SummarizeBy that creates implicit measures - set SummarizeBy = None on report-only numeric columns.",
            m =>
            {
                if (m.DiscourageImplicitMeasures) return Enumerable.Empty<BpaTarget>();
                var facts = FactTables(m);
                return facts.SelectMany(DataCols).Where(c => Numeric.Contains(c.DataType) && !c.IsHidden
                    && c.SummarizeBy != TOM.AggregateFunction.None && !KeyLikeName(c.Name)).Select(T);
            },
            FixedProperty: "Column.SummarizeBy = None",
            Fix: o => { ((TOM.Column)o).SummarizeBy = TOM.AggregateFunction.None;
                        return $"{Name(o)}: SummarizeBy -> None"; }));

        R(new("DROP_UNUSED_PERSPECTIVES_AND_CULTURES", CatMaintenance, Info, BpaScope.Model,
            "Stray translation cultures with no translated objects add maintenance noise - prune them.",
            m => m.Cultures.Any(c => c.ObjectTranslations.Count == 0 && !string.Equals(c.Name, m.Culture, StringComparison.OrdinalIgnoreCase))
                 ? new[] { T(m) } : Enumerable.Empty<BpaTarget>()));

        R(new("CALCULATION_GROUP_HAS_NO_ITEMS", CatMaintenance, Warning, BpaScope.Table,
            "A calculation group with no calculation items does nothing.",
            m => UserTables(m).Where(t => IsCalcGroup(t) && t.CalculationGroup.CalculationItems.Count == 0).Select(T)));

        // ---------------------------------------------------------- NamingConventions ----------------------------------------------------------

        R(new("NO_SPECIAL_CHARACTERS_IN_NAMES", CatNaming, Warning, BpaScope.Column,
            "Object names should avoid tabs, carriage returns and line feeds (they break TMDL and the field list).",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => c.Name.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0).Select(T),
            FixedProperty: "Column.Name (collapse special whitespace to single spaces)",
            Fix: o => { var c = (TOM.Column)o; c.Name = CollapseWhitespace(c.Name);
                        return $"{Name(o)}: removed special characters from name"; }));

        R(new("NO_TRAILING_SPACES_IN_NAMES", CatNaming, Warning, BpaScope.Column,
            "Object names should not have leading or trailing spaces - they are invisible and cause lookup bugs.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => c.Name != c.Name.Trim()).Select(T),
            FixedProperty: "Column.Name = Name.Trim()",
            Fix: o => { var c = (TOM.Column)o; c.Name = c.Name.Trim(); return $"{Name(o)}: trimmed surrounding spaces"; }));

        R(new("MEASURES_NOT_PREFIXED_WITH_TABLE", CatNaming, Info, BpaScope.Measure,
            "A measure name should not be prefixed with its table name (e.g. 'Sales Total Sales') - it is redundant in the field list.",
            m => UserTables(m).SelectMany(t => t.Measures
                    .Where(me => me.Name.StartsWith(t.Name + " ", StringComparison.OrdinalIgnoreCase))
                    .Select(me => T(me)))));

        R(new("COLUMN_NAMED_SAME_AS_TABLE", CatNaming, Info, BpaScope.Column,
            "A column with the same name as its table is confusing in references; rename one.",
            m => UserTables(m).SelectMany(t => DataCols(t)
                    .Where(c => string.Equals(c.Name, t.Name, StringComparison.OrdinalIgnoreCase))
                    .Select(c => T(c)))));

        R(new("OBJECT_NAMES_SHOULD_USE_TITLE_CASE", CatNaming, Info, BpaScope.Measure,
            "Measure names read better in Title Case (first letter of each word capitalised).",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => !string.IsNullOrEmpty(me.Name) && char.IsLower(me.Name[0])).Select(T)));

        R(new("RELATIONSHIP_NAMES_SHOULD_BE_MEANINGFUL", CatNaming, Info, BpaScope.Relationship,
            "Auto-generated GUID-like relationship names are hard to maintain; give relationships meaningful names.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => !string.IsNullOrEmpty(r.Name) && Guid.TryParse(r.Name, out _)).Select(T)));

        R(new("TABLE_NAMES_SHOULD_NOT_BE_RESERVED_WORDS", CatNaming, Info, BpaScope.Table,
            "Avoid DAX reserved words (Date, Month, Year, Day, Hour, ...) as table names - they need quoting and confuse functions.",
            m => UserTables(m).Where(t => ReservedWords.Contains(t.Name)).Select(T)));

        R(new("AVOID_ABBREVIATIONS_IN_NAMES", CatNaming, Info, BpaScope.Column,
            "Names like Qty / Amt / Nbr are unclear for self-service users; spell them out.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => AbbrevPattern.IsMatch(c.Name)).Select(T)));

        R(new("MEASURE_NAMES_NO_SPECIAL_CHARACTERS", CatNaming, Warning, BpaScope.Measure,
            "Measure names should avoid tabs/CR/LF (they break TMDL and the field list).",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => me.Name.IndexOfAny(new[] { '\t', '\r', '\n' }) >= 0).Select(T),
            FixedProperty: "Measure.Name (collapse special whitespace)",
            Fix: o => { var me = (TOM.Measure)o; me.Name = CollapseWhitespace(me.Name);
                        return $"{Name(o)}: removed special characters from name"; }));

        R(new("MEASURE_NAMES_NO_TRAILING_SPACES", CatNaming, Warning, BpaScope.Measure,
            "Measure names should not have leading/trailing spaces.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => me.Name != me.Name.Trim()).Select(T),
            FixedProperty: "Measure.Name = Name.Trim()",
            Fix: o => { var me = (TOM.Measure)o; me.Name = me.Name.Trim(); return $"{Name(o)}: trimmed surrounding spaces"; }));

        R(new("TABLE_NAMES_NO_TRAILING_SPACES", CatNaming, Warning, BpaScope.Table,
            "Table names should not have leading/trailing spaces.",
            m => UserTables(m).Where(t => t.Name != t.Name.Trim()).Select(T),
            FixedProperty: "Table.Name = Name.Trim()",
            Fix: o => { var t = (TOM.Table)o; t.Name = t.Name.Trim(); return $"{Name(o)}: trimmed surrounding spaces"; }));

        R(new("HIERARCHY_LEVELS_SHOULD_NOT_DUPLICATE_NAMES", CatNaming, Info, BpaScope.Hierarchy,
            "A hierarchy with two levels of the same name is confusing in the field list.",
            m => UserTables(m).SelectMany(t => t.Hierarchies)
                  .Where(h => h.Levels.Select(l => l.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != h.Levels.Count)
                  .Select(T)));

        // ---------------------------------------------------------- Formatting ----------------------------------------------------------

        R(new("NO_FORMAT_STRING_ON_MEASURES", CatFormatting, Warning, BpaScope.Measure,
            "Every measure should have a FormatString so numbers render consistently across visuals.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => string.IsNullOrWhiteSpace(me.FormatString)).Select(T),
            FixedProperty: "Measure.FormatString = \"#,0.00\"",
            Fix: o => { ((TOM.Measure)o).FormatString = "#,0.00"; return $"{Name(o)}: FormatString -> \"#,0.00\""; }));

        R(new("NUMERIC_COLUMNS_NO_FORMAT_STRING", CatFormatting, Warning, BpaScope.Column,
            "Visible numeric/date columns should have a FormatString.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => !c.IsHidden && (Numeric.Contains(c.DataType) || c.DataType == TOM.DataType.DateTime)
                       && string.IsNullOrWhiteSpace(c.FormatString)).Select(T),
            FixedProperty: "Column.FormatString",
            Fix: o => { var c = (TOM.Column)o;
                        c.FormatString = c.DataType == TOM.DataType.DateTime ? "General Date"
                            : c.DataType == TOM.DataType.Int64 ? "#,0" : "#,0.00";
                        return $"{Name(o)}: FormatString -> \"{c.FormatString}\""; }));

        R(new("WHOLE_NUMBERS_THOUSANDS_SEPARATOR", CatFormatting, Info, BpaScope.Column,
            "Whole-number columns read better with a thousands separator (#,0).",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => !c.IsHidden && c.DataType == TOM.DataType.Int64 && !KeyLikeName(c.Name)
                       && !string.IsNullOrWhiteSpace(c.FormatString)
                       && !c.FormatString.Contains(',')).Select(T),
            FixedProperty: "Column.FormatString = \"#,0\"",
            Fix: o => { ((TOM.Column)o).FormatString = "#,0"; return $"{Name(o)}: FormatString -> \"#,0\""; }));

        R(new("PERCENT_COLUMNS_FORMAT_STRING", CatFormatting, Info, BpaScope.Column,
            "A column whose name implies a percentage should use a percent format string (0.0%).",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => !c.IsHidden && Numeric.Contains(c.DataType)
                       && (c.Name.Contains("%") || c.Name.Contains("percent", StringComparison.OrdinalIgnoreCase)
                           || c.Name.EndsWith("Pct", StringComparison.OrdinalIgnoreCase))
                       && (c.FormatString ?? "").IndexOf('%') < 0).Select(T),
            FixedProperty: "Column.FormatString = \"0.0%\"",
            Fix: o => { ((TOM.Column)o).FormatString = "0.0%"; return $"{Name(o)}: FormatString -> \"0.0%\""; }));

        R(new("PERCENT_MEASURES_FORMAT_STRING", CatFormatting, Info, BpaScope.Measure,
            "A measure whose name implies a percentage should use a percent format string (0.0%).",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => (me.Name.Contains("%") || me.Name.Contains("percent", StringComparison.OrdinalIgnoreCase)
                           || me.Name.EndsWith("Pct", StringComparison.OrdinalIgnoreCase))
                       && (me.FormatString ?? "").IndexOf('%') < 0).Select(T),
            FixedProperty: "Measure.FormatString = \"0.0%\"",
            Fix: o => { ((TOM.Measure)o).FormatString = "0.0%"; return $"{Name(o)}: FormatString -> \"0.0%\""; }));

        R(new("DATE_COLUMNS_FORMAT_STRING", CatFormatting, Info, BpaScope.Column,
            "Date columns should have an explicit date FormatString rather than the locale default.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => !c.IsHidden && c.DataType == TOM.DataType.DateTime
                       && string.IsNullOrWhiteSpace(c.FormatString)).Select(T),
            FixedProperty: "Column.FormatString = \"yyyy-mm-dd\"",
            Fix: o => { ((TOM.Column)o).FormatString = "yyyy-mm-dd"; return $"{Name(o)}: FormatString -> \"yyyy-mm-dd\""; }));

        R(new("FORMAT_STRING_NO_LEADING_APOSTROPHE", CatFormatting, Info, BpaScope.Measure,
            "A FormatString beginning with an apostrophe is usually a paste artefact and renders literally.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => (me.FormatString ?? "").StartsWith("'")).Select(T),
            FixedProperty: "Measure.FormatString (trim leading apostrophe)",
            Fix: o => { var me = (TOM.Measure)o; me.FormatString = me.FormatString!.TrimStart('\'');
                        return $"{Name(o)}: removed leading apostrophe from FormatString"; }));

        R(new("FORMAT_STRING_ON_PK_SHOULD_BE_PLAIN", CatFormatting, Info, BpaScope.Column,
            "An integer surrogate-key column should NOT carry a thousands-separator format (keys are not read as quantities).",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => c.DataType == TOM.DataType.Int64 && KeyLikeName(c.Name)
                       && (c.FormatString ?? "").Contains(',')).Select(T),
            FixedProperty: "Column.FormatString = \"0\"",
            Fix: o => { ((TOM.Column)o).FormatString = "0"; return $"{Name(o)}: FormatString -> \"0\""; }));

        R(new("CURRENCY_COLUMNS_FORMAT_STRING", CatFormatting, Info, BpaScope.Column,
            "A currency/amount column reads better with a currency format string.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => !c.IsHidden && (c.DataType == TOM.DataType.Decimal || c.DataType == TOM.DataType.Double)
                       && Currencyish(c.Name) && string.IsNullOrWhiteSpace(c.FormatString)).Select(T),
            FixedProperty: "Column.FormatString = \"\\$#,0.00\"",
            Fix: o => { ((TOM.Column)o).FormatString = "\\$#,0.00"; return $"{Name(o)}: FormatString -> currency"; }));

        R(new("BOOLEAN_COLUMNS_SHOULD_NOT_HAVE_NUMERIC_FORMAT", CatFormatting, Info, BpaScope.Column,
            "A boolean column with a numeric format string renders as 0/1 instead of True/False.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => c.DataType == TOM.DataType.Boolean
                       && !string.IsNullOrWhiteSpace(c.FormatString)
                       && c.FormatString!.IndexOf("yes", StringComparison.OrdinalIgnoreCase) < 0
                       && c.FormatString.IndexOf("true", StringComparison.OrdinalIgnoreCase) < 0).Select(T),
            FixedProperty: "Column.FormatString = \"\" (clear)",
            Fix: o => { ((TOM.Column)o).FormatString = ""; return $"{Name(o)}: cleared numeric format on boolean"; }));

        R(new("KPI_MEASURES_SHOULD_HAVE_FORMAT", CatFormatting, Info, BpaScope.Measure,
            "A measure that backs a KPI should have an explicit FormatString so the KPI value renders cleanly.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => me.KPI != null && string.IsNullOrWhiteSpace(me.FormatString)).Select(T),
            FixedProperty: "Measure.FormatString = \"#,0.00\"",
            Fix: o => { ((TOM.Measure)o).FormatString = "#,0.00"; return $"{Name(o)}: FormatString -> \"#,0.00\""; }));

        // ---------------------------------------------------------- Metadata ----------------------------------------------------------

        R(new("HIDE_FOREIGN_KEYS", CatMetadata, Warning, BpaScope.Column,
            "Foreign-key columns (the many side of a relationship) should be hidden from the field list - users pick the dimension attribute, not the key.",
            m =>
            {
                var fks = ForeignKeyColumns(m);
                return fks.Where(c => !c.IsHidden).Select(T);
            },
            FixedProperty: "Column.IsHidden = true",
            Fix: o => { ((TOM.Column)o).IsHidden = true; return $"{Name(o)}: IsHidden -> true (foreign key)"; }));

        R(new("HIDE_FACT_TABLE_COLUMNS", CatMetadata, Warning, BpaScope.Column,
            "Raw fact-table columns (non-key) should be hidden - users consume measures, not the underlying columns.",
            m =>
            {
                var facts = FactTables(m);
                var rels = RelationshipColumns(m);
                return facts.SelectMany(DataCols)
                    .Where(c => !c.IsHidden && !c.IsKey && !rels.Contains(c) && c.SortByColumn == null).Select(T);
            },
            FixedProperty: "Column.IsHidden = true",
            Fix: o => { ((TOM.Column)o).IsHidden = true; return $"{Name(o)}: IsHidden -> true (raw fact column)"; }));

        R(new("MISSING_DESCRIPTION", CatMetadata, Info, BpaScope.Measure,
            "Measures should have a Description so self-service users understand them (it shows in the field-list tooltip).",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => string.IsNullOrWhiteSpace(me.Description)).Select(T)));

        R(new("TABLE_MISSING_DESCRIPTION", CatMetadata, Info, BpaScope.Table,
            "Tables should have a Description for the data-catalogue / lineage view.",
            m => UserTables(m).Where(t => !IsCalcGroup(t) && string.IsNullOrWhiteSpace(t.Description)).Select(T)));

        R(new("SET_DATA_CATEGORY_FOR_GEO", CatMetadata, Info, BpaScope.Column,
            "Geographic columns (Country/State/City/Postcode/Lat/Long) should set a geo DataCategory so maps work.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => GeoName(c.Name) && string.IsNullOrEmpty(c.DataCategory)).Select(T),
            FixedProperty: "Column.DataCategory (Country/StateOrProvince/City/PostalCode/Latitude/Longitude)",
            Fix: o => { var c = (TOM.Column)o; c.DataCategory = GeoCategory(c.Name);
                        return $"{Name(o)}: DataCategory -> {c.DataCategory}"; }));

        R(new("SET_DATA_CATEGORY_FOR_URL", CatMetadata, Info, BpaScope.Column,
            "A column holding web/image links should set DataCategory = WebUrl / ImageUrl so visuals render them.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => c.DataType == TOM.DataType.String && UrlName(c.Name)
                       && string.IsNullOrEmpty(c.DataCategory)).Select(T),
            FixedProperty: "Column.DataCategory = WebURL / ImageURL",
            Fix: o => { var c = (TOM.Column)o;
                        c.DataCategory = c.Name.Contains("image", StringComparison.OrdinalIgnoreCase) ? "ImageURL" : "WebURL";
                        return $"{Name(o)}: DataCategory -> {c.DataCategory}"; }));

        R(new("MEASURES_SHOULD_NOT_BE_HIDDEN_WITHOUT_REASON", CatMetadata, Info, BpaScope.Measure,
            "A hidden measure with no description is hard to maintain - document why it is internal.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => me.IsHidden && string.IsNullOrWhiteSpace(me.Description)).Select(T)));

        R(new("ADD_DISPLAY_FOLDER_FOR_COLUMNS", CatMetadata, Info, BpaScope.Column,
            "Visible columns on wide tables read better grouped into display folders.",
            m => UserTables(m).Where(t => DataCols(t).Count(c => !c.IsHidden) > 12)
                  .SelectMany(t => DataCols(t).Where(c => !c.IsHidden && string.IsNullOrWhiteSpace(c.DisplayFolder)))
                  .Select(T)));

        R(new("PROVIDE_FORMAT_STRING_EXPRESSION_FOR_CALC_ITEMS", CatMetadata, Info, BpaScope.Table,
            "Calculation items that change the meaning of a measure (e.g. YoY %) should carry their own format-string expression.",
            m => UserTables(m).Where(IsCalcGroup)
                  .Where(t => t.CalculationGroup.CalculationItems.Any(ci =>
                       string.IsNullOrWhiteSpace(ci.FormatStringDefinition?.Expression))).Select(T)));

        R(new("KEY_COLUMNS_SHOULD_BE_HIDDEN_OR_KEYED", CatMetadata, Info, BpaScope.Column,
            "A column named like a key that is neither hidden nor marked IsKey is probably mis-modelled.",
            m => UserTables(m).SelectMany(DataCols)
                  .Where(c => KeyLikeName(c.Name) && !c.IsHidden && !c.IsKey).Select(T)));

        R(new("HIERARCHIES_SHOULD_HAVE_DISPLAY_FOLDER_OR_DESCRIPTION", CatMetadata, Info, BpaScope.Hierarchy,
            "A hierarchy with no description leaves self-service users guessing what its drill path means.",
            m => UserTables(m).SelectMany(t => t.Hierarchies)
                  .Where(h => string.IsNullOrWhiteSpace(h.Description)).Select(T)));

        // ---------------------------------------------------------- RelationshipsLayout ----------------------------------------------------------

        R(new("BIDIRECTIONAL_RELATIONSHIP", CatLayout, Warning, BpaScope.Relationship,
            "Bi-directional cross-filtering creates ambiguous filter propagation in a star schema - prefer single direction with a measure-side CROSSFILTER where truly needed. Autofix sets OneDirection.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => r.CrossFilteringBehavior == TOM.CrossFilteringBehavior.BothDirections).Select(T),
            FixedProperty: "SingleColumnRelationship.CrossFilteringBehavior = OneDirection",
            Fix: o => { ((TOM.SingleColumnRelationship)o).CrossFilteringBehavior = TOM.CrossFilteringBehavior.OneDirection;
                        return $"{Name(o)}: CrossFilteringBehavior -> OneDirection"; }));

        R(new("MANY_TO_MANY_RELATIONSHIPS", CatLayout, Warning, BpaScope.Relationship,
            "Many-to-many relationships are slow and can double-count - introduce a bridge dimension instead.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => r.FromCardinality == TOM.RelationshipEndCardinality.Many
                       && r.ToCardinality == TOM.RelationshipEndCardinality.Many).Select(T)));

        R(new("TABLE_NOT_IN_ANY_RELATIONSHIP", CatLayout, Info, BpaScope.Table,
            "A table that joins to nothing is an island (fine for a parameter/disconnected table, otherwise a modelling gap).",
            m =>
            {
                if (UserTables(m).Count() <= 1) return Enumerable.Empty<BpaTarget>();
                var inRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in m.Relationships.OfType<TOM.SingleColumnRelationship>())
                { if (r.FromTable != null) inRel.Add(r.FromTable.Name); if (r.ToTable != null) inRel.Add(r.ToTable.Name); }
                return UserTables(m).Where(t => !IsCalcGroup(t) && !inRel.Contains(t.Name)).Select(T);
            }));

        R(new("AMBIGUOUS_RELATIONSHIP_PATHS", CatLayout, Warning, BpaScope.Relationship,
            "Multiple active relationships into one table from another via different paths cause ambiguity - keep one active.",
            m =>
            {
                var active = m.Relationships.OfType<TOM.SingleColumnRelationship>()
                    .Where(r => r.IsActive && r.FromTable != null && r.ToTable != null).ToList();
                var dups = new List<BpaTarget>();
                foreach (var grp in active.GroupBy(r => PairKey(r.FromTable!.Name, r.ToTable!.Name)))
                    if (grp.Count() > 1) dups.AddRange(grp.Select(T));
                return dups;
            }));

        R(new("RELATIONSHIP_TO_HIGH_CARDINALITY_STRING", CatLayout, Info, BpaScope.Relationship,
            "Relating on a long free-text string key bloats the dictionary - introduce an integer surrogate key.",
            m => m.Relationships.OfType<TOM.SingleColumnRelationship>()
                  .Where(r => r.ToColumn?.DataType == TOM.DataType.String
                       && (r.ToColumn?.Name.Length ?? 0) > 0 && !KeyLikeName(r.ToColumn!.Name)).Select(T)));

        R(new("RELATIONSHIP_CROSSES_FACT_TO_FACT", CatLayout, Warning, BpaScope.Relationship,
            "Two fact tables related directly (fact-to-fact) usually needs a conformed dimension instead.",
            m =>
            {
                var facts = FactTables(m);
                return m.Relationships.OfType<TOM.SingleColumnRelationship>()
                    .Where(r => r.FromTable != null && r.ToTable != null
                        && facts.Contains(r.FromTable) && facts.Contains(r.ToTable)).Select(T);
            }));

        R(new("ACTIVE_RELATIONSHIP_BETWEEN_SAME_TABLES_AS_INACTIVE", CatLayout, Info, BpaScope.Relationship,
            "A pair of tables with both an active and an inactive relationship is a common role-playing-dimension pattern - confirm USERELATIONSHIP is wired up.",
            m =>
            {
                var byPair = m.Relationships.OfType<TOM.SingleColumnRelationship>()
                    .Where(r => r.FromTable != null && r.ToTable != null)
                    .GroupBy(r => PairKey(r.FromTable!.Name, r.ToTable!.Name));
                return byPair.Where(g => g.Any(r => r.IsActive) && g.Any(r => !r.IsActive))
                    .SelectMany(g => g.Where(r => !r.IsActive)).Select(T);
            }));

        R(new("DIMENSION_TABLE_SHOULD_BE_ONE_SIDE", CatLayout, Info, BpaScope.Relationship,
            "A dimension that appears on the 'many' side of every relationship is probably modelled the wrong way round.",
            m =>
            {
                var facts = FactTables(m);
                return m.Relationships.OfType<TOM.SingleColumnRelationship>()
                    .Where(r => r.FromTable != null && r.FromCardinality == TOM.RelationshipEndCardinality.Many
                        && (r.FromTable.Name.Contains("Dim", StringComparison.OrdinalIgnoreCase)
                            || r.FromTable.Name.StartsWith("D_", StringComparison.OrdinalIgnoreCase))).Select(T);
            }));

        R(new("SNOWFLAKE_DIMENSION_CHAIN", CatLayout, Info, BpaScope.Table,
            "A dimension that is itself filtered by another dimension (snowflake) can usually be flattened into a single dimension for performance.",
            m =>
            {
                var facts = FactTables(m);
                var rels = m.Relationships.OfType<TOM.SingleColumnRelationship>().ToList();
                var snow = new List<BpaTarget>();
                foreach (var t in UserTables(m))
                {
                    if (IsCalcGroup(t) || facts.Contains(t)) continue;
                    bool isOneSide = rels.Any(r => r.ToTable == t && r.ToCardinality == TOM.RelationshipEndCardinality.One);
                    bool isManySide = rels.Any(r => r.FromTable == t && r.FromCardinality == TOM.RelationshipEndCardinality.Many
                        && r.ToTable != null && !facts.Contains(r.ToTable));
                    if (isOneSide && isManySide) snow.Add(T(t));
                }
                return snow;
            }));

        // ---------------------------------------------------------- extra ErrorPrevention + DAX (catalogue depth)

        R(new("COLUMN_WITH_NO_SOURCE", CatError, Error, BpaScope.Column,
            "A data column with no SourceColumn cannot be populated on refresh.",
            m => UserTables(m).SelectMany(t => t.Columns.OfType<TOM.DataColumn>())
                  .Where(c => string.IsNullOrWhiteSpace(c.SourceColumn)).Select(T)));

        R(new("MEASURE_WITH_EMPTY_EXPRESSION", CatError, Error, BpaScope.Measure,
            "A measure with an empty DAX expression errors at query time.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => string.IsNullOrWhiteSpace(me.Expression)).Select(T)));

        R(new("AVOID_NESTED_CALCULATE", CatDax, Info, BpaScope.Measure,
            "Deeply nested CALCULATE calls are hard to reason about and often hide a context-transition bug.",
            m => UserTables(m).SelectMany(t => t.Measures)
                  .Where(me => System.Text.RegularExpressions.Regex.Matches(StripDax(me.Expression),
                       @"\bCALCULATE\s*\(", System.Text.RegularExpressions.RegexOptions.IgnoreCase).Count >= 3).Select(T)));

        R(new("AVOID_EARLIER_USE_VARS", CatDax, Info, BpaScope.Column,
            "EARLIER in a calculated column is a legacy pattern - use VAR to capture the outer row context instead.",
            m => UserTables(m).SelectMany(t => t.Columns.OfType<TOM.CalculatedColumn>())
                  .Where(c => UsesFunc(StripDax(c.Expression), "EARLIER")).Select(T)));

        return rules;
    }

    // ================================================================ rule-specific predicates

    private static HashSet<TOM.Column> SortByTargets(TOM.Model m)
    {
        var set = new HashSet<TOM.Column>();
        foreach (var t in m.Tables)
            foreach (var c in t.Columns)
                if (c.SortByColumn != null) set.Add(c.SortByColumn);
        return set;
    }

    // the "one" side (dimension) columns of relationships = primary-key candidates.
    private static HashSet<TOM.Column> OneSideKeyColumns(TOM.Model m)
    {
        var set = new HashSet<TOM.Column>();
        foreach (var r in m.Relationships.OfType<TOM.SingleColumnRelationship>())
            if (r.ToColumn != null && r.ToCardinality == TOM.RelationshipEndCardinality.One)
                set.Add(r.ToColumn);
        return set;
    }

    /// <summary>'/' division operator outside of a comment/string/DIVIDE call.</summary>
    private static bool HasRawDivision(string? dax)
    {
        var s = StripDax(dax);
        // a '/' with operands either side - a real divide, not a path or date literal.
        return Regex.IsMatch(s, @"[\)\]\w\.]\s*/\s*[\(\[\w\-]");
    }

    private static bool SummarizeAddsColumns(string? dax)
    {
        var s = StripDax(dax);
        var m = Regex.Match(s, @"SUMMARIZE\s*\(", RegexOptions.IgnoreCase);
        if (!m.Success) return false;
        // find the matched closing paren and look for a "...", literal-name, <expr> column add inside.
        int start = m.Index + m.Length;
        int depth = 1, i = start;
        for (; i < s.Length && depth > 0; i++)
        {
            if (s[i] == '(') depth++;
            else if (s[i] == ')') depth--;
        }
        string inside = s.Substring(start, Math.Max(0, i - start - 1));
        // a quoted name immediately followed by a comma and an expression = an added column inside SUMMARIZE.
        return Regex.IsMatch(inside, "\"[^\"]*\"\\s*,", RegexOptions.Singleline);
    }

    private static bool ReferencesNakedColumn(string? dax)
    {
        var s = StripDax(dax);
        // Table[Col] with no surrounding aggregator at top level is hard to prove statically; flag the
        // unambiguous case: an expression that is exactly a single fully-qualified column reference.
        return Regex.IsMatch(s.Trim(),
            @"^'?[\w ]+'?\[[^\]]+\]$");
    }

    private static bool UsesKeyword(string? dax, string kw) =>
        Regex.IsMatch(StripDax(dax), $@"(^|\W){Regex.Escape(kw)}(\W|$)", RegexOptions.IgnoreCase);

    private static bool SameTypeFamily(TOM.DataType a, TOM.DataType b)
    {
        static int Fam(TOM.DataType d) => d switch
        {
            TOM.DataType.Int64 or TOM.DataType.Double or TOM.DataType.Decimal => 1,
            TOM.DataType.String => 2,
            TOM.DataType.DateTime => 3,
            TOM.DataType.Boolean => 4,
            _ => 0,
        };
        return Fam(a) == Fam(b);
    }

    private static bool Currencyish(string n) =>
        Regex.IsMatch(n, @"amount|amt|price|cost|revenue|sales|total|value|currency|dollar|salary|wage|fee|charge|balance|payment",
            RegexOptions.IgnoreCase);

    private static bool GeoName(string n) => Regex.IsMatch(n,
        @"^(country|state|province|city|town|suburb|region|postcode|postal|zip|latitude|longitude|lat|lon|lng)\b|address",
        RegexOptions.IgnoreCase);

    private static string GeoCategory(string n)
    {
        n = n.ToLowerInvariant();
        if (n.StartsWith("country")) return "Country";
        if (n.Contains("state") || n.Contains("province")) return "StateOrProvince";
        if (n.StartsWith("city") || n.StartsWith("town") || n.StartsWith("suburb")) return "City";
        if (n.Contains("post") || n.Contains("zip")) return "PostalCode";
        if (n.StartsWith("lat")) return "Latitude";
        if (n.StartsWith("lon") || n.StartsWith("lng")) return "Longitude";
        if (n.Contains("address")) return "Address";
        return "Place";
    }

    private static bool UrlName(string n) => Regex.IsMatch(n,
        @"url|uri|link|website|web site|image|photo|picture|logo|thumbnail", RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
        { "Date", "Time", "Month", "Year", "Day", "Hour", "Minute", "Second", "Now", "Today", "Week", "Quarter" };

    private static readonly Regex AbbrevPattern = new(
        @"\b(Qty|Amt|Nbr|Num|Desc|Cnt|Pct|Avg|Mgr|Dept|Cust|Prod|Acct)\b",
        RegexOptions.IgnoreCase);

    private static string CollapseWhitespace(string s) =>
        Regex.Replace(s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();

    private static string PairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a} {b}" : $"{b} {a}";

    private static List<string> AllDax(TOM.Model m)
    {
        var list = new List<string>();
        foreach (var t in m.Tables)
        {
            foreach (var me in t.Measures) if (!string.IsNullOrEmpty(me.Expression)) list.Add(me.Expression);
            foreach (var c in t.Columns.OfType<TOM.CalculatedColumn>()) if (!string.IsNullOrEmpty(c.Expression)) list.Add(c.Expression);
            if (IsCalcGroup(t))
                foreach (var ci in t.CalculationGroup.CalculationItems) if (!string.IsNullOrEmpty(ci.Expression)) list.Add(ci.Expression);
        }
        foreach (var r in m.Roles) foreach (var tp in r.TablePermissions) if (!string.IsNullOrEmpty(tp.FilterExpression)) list.Add(tp.FilterExpression);
        return list;
    }

    // ---- unused-object detection (mirrors FindUnusedCore so run_bpa reuses the same logic shape) ----

    internal static List<TOM.Column> UnusedColumns(TOM.Model m)
    {
        var daxBlobs = AllDax(m);
        var used = new HashSet<TOM.Column>();
        foreach (var r in m.Relationships.OfType<TOM.SingleColumnRelationship>())
        { if (r.FromColumn != null) used.Add(r.FromColumn); if (r.ToColumn != null) used.Add(r.ToColumn); }
        foreach (var t in m.Tables)
        {
            foreach (var c in t.Columns) if (c.SortByColumn != null) used.Add(c.SortByColumn);
            foreach (var h in t.Hierarchies) foreach (var lvl in h.Levels) if (lvl.Column != null) used.Add(lvl.Column);
        }
        var result = new List<TOM.Column>();
        foreach (var t in UserTables(m))
        {
            if (IsCalcGroup(t)) continue;
            foreach (var c in t.Columns)
            {
                if (c.Type == TOM.ColumnType.RowNumber) continue;
                if (used.Contains(c)) continue;
                if (daxBlobs.Any(d => d.IndexOf($"[{c.Name}]", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                result.Add(c);
            }
        }
        return result;
    }

    internal static List<TOM.Measure> UnusedMeasures(TOM.Model m)
    {
        var result = new List<TOM.Measure>();
        foreach (var t in UserTables(m))
            foreach (var me in t.Measures)
            {
                string self = me.Expression ?? "";
                bool referenced = false;
                foreach (var ot in m.Tables)
                    foreach (var om in ot.Measures)
                    {
                        if (ReferenceEquals(om, me)) continue;
                        if ((om.Expression ?? "").IndexOf($"[{me.Name}]", StringComparison.OrdinalIgnoreCase) >= 0) { referenced = true; break; }
                    }
                if (!referenced) result.Add(me);
            }
        return result;
    }

    /// <summary>Strip EVALUATEANDLOG(expr [, "label" [, n]]) wrappers, returning the inner expression(s).</summary>
    internal static string StripEvaluateAndLog(string? dax)
    {
        if (string.IsNullOrEmpty(dax)) return dax ?? "";
        string s = dax;
        // repeatedly find the innermost EVALUATEANDLOG( ... ) and replace with its first argument.
        for (int guard = 0; guard < 100; guard++)
        {
            var m = Regex.Match(s, @"EVALUATEANDLOG\s*\(", RegexOptions.IgnoreCase);
            if (!m.Success) break;
            int open = m.Index + m.Length - 1;     // index of '('
            int depth = 0, i = open, firstArgEnd = -1;
            for (; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '(') depth++;
                else if (ch == ')') { depth--; if (depth == 0) break; }
                else if (ch == ',' && depth == 1 && firstArgEnd < 0) firstArgEnd = i;
            }
            if (i >= s.Length) break;               // unbalanced - leave as-is
            int close = i;
            int argStart = open + 1;
            int argEnd = firstArgEnd >= 0 ? firstArgEnd : close;
            string inner = s.Substring(argStart, argEnd - argStart).Trim();
            s = s.Substring(0, m.Index) + inner + s.Substring(close + 1);
        }
        return s;
    }

    private static string Name(TOM.MetadataObject o) => o switch
    {
        TOM.Column c => $"{c.Table.Name}[{c.Name}]",
        TOM.Measure me => $"{me.Table.Name}[{me.Name}]",
        TOM.Hierarchy h => $"{h.Table.Name}[{h.Name}]",
        TOM.Table t => t.Name,
        TOM.SingleColumnRelationship r => r.Name ?? $"{Safe(r.FromTable)}->{Safe(r.ToTable)}",
        TOM.Model mo => string.IsNullOrEmpty(mo.Name) ? "Model" : mo.Name,
        _ => o.GetType().Name,
    };

    // ================================================================ runner

    public sealed record Finding(string RuleId, string Category, int Severity, string ObjectType,
        string ObjectName, string Message, bool Fixable);

    /// <summary>
    /// Run the catalogue against a model, optionally filtered. Returns every violation as a flat finding.
    /// Pure - takes a model, no live server.
    /// </summary>
    public static List<Finding> Run(TOM.Model model,
        IReadOnlyCollection<string>? categories = null,
        IReadOnlyCollection<int>? severities = null,
        IReadOnlyCollection<string>? ruleIds = null,
        string? scope = null)
    {
        var findings = new List<Finding>();
        BpaScope? scopeFilter = null;
        if (!string.IsNullOrWhiteSpace(scope) && Enum.TryParse<BpaScope>(scope, ignoreCase: true, out var sc))
            scopeFilter = sc;

        foreach (var rule in All)
        {
            if (rule.ManualOnly) continue;                      // imported descriptive-only rules never auto-run a check
            if (categories is { Count: > 0 } && !categories.Contains(rule.Category, StringComparer.OrdinalIgnoreCase)) continue;
            if (severities is { Count: > 0 } && !severities.Contains(rule.Severity)) continue;
            if (ruleIds is { Count: > 0 } && !ruleIds.Contains(rule.Id, StringComparer.OrdinalIgnoreCase)) continue;
            if (scopeFilter is { } want && rule.Scope != want) continue;

            IEnumerable<BpaTarget> hits;
            try { hits = rule.Check(model).ToList(); }
            catch { continue; }                                 // a defensive rule must never sink the whole run

            foreach (var hit in hits)
                findings.Add(new Finding(rule.Id, rule.Category, rule.Severity,
                    hit.ObjectType, hit.ObjectName, rule.Description, rule.Fixable));
        }
        return findings;
    }

    // ================================================================ fixer

    public sealed record FixOutcome(string ObjectName, string Change);

    /// <summary>
    /// Apply a rule's autofix to every matching object (or a single named object). Pure mutation - the
    /// caller is responsible for SaveChanges. When <paramref name="dryRun"/> is true NO property is set;
    /// the returned outcomes describe what WOULD change.
    /// </summary>
    public static (BpaRule rule, List<FixOutcome> outcomes) Fix(TOM.Model model, string ruleId,
        string? objectName, bool dryRun)
    {
        var rule = ById(ruleId)
            ?? throw new InvalidOperationException($"Unknown BPA rule '{ruleId}'. Call list_bpa_rules to see the catalogue.");
        if (rule.Fix == null)
            throw new InvalidOperationException(
                $"Rule '{rule.Id}' has no safe automatic fix (it is flag-only). Guidance: {rule.Description}");

        var targets = rule.Check(model).ToList();
        if (!string.IsNullOrWhiteSpace(objectName))
            targets = targets.Where(t => string.Equals(t.ObjectName, objectName, StringComparison.OrdinalIgnoreCase)).ToList();

        var outcomes = new List<FixOutcome>();
        foreach (var t in targets)
        {
            if (t.Tom == null) continue;
            if (dryRun)
                outcomes.Add(new FixOutcome(t.ObjectName, $"WOULD set {rule.FixedProperty}"));
            else
                outcomes.Add(new FixOutcome(t.ObjectName, rule.Fix(t.Tom)));
        }
        return (rule, outcomes);
    }

    // ================================================================ Tabular Editor ruleset import

    public sealed record ImportResult(int total, int mappedToBuiltIn, int registeredAsManual,
        List<object> mapped, List<object> manual, List<string> skipped);

    /// <summary>
    /// Parse a Tabular Editor BPARules.json document and reconcile it with our built-in catalogue.
    /// A rule whose ID matches a built-in is MAPPED (we run our own evaluable check). A rule whose logic is
    /// a Tabular Editor dynamic C#/LINQ expression we cannot evaluate is registered DESCRIPTIVE-ONLY
    /// (id/severity/description surfaced, check returns "manual"). We never attempt to execute the TE
    /// expression string.
    /// </summary>
    public static ImportResult Import(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Provide the BPARules.json content.");

        JsonNode? root;
        try { root = JsonNode.Parse(json); }
        catch (Exception ex) { throw new InvalidOperationException($"BPARules.json is not valid JSON: {ex.Message}"); }

        // TE stores either a bare array of rules, or an object with a "Rules" array.
        JsonArray arr = root as JsonArray
            ?? (root as JsonObject)?["Rules"] as JsonArray
            ?? throw new InvalidOperationException("Expected a JSON array of rules, or an object with a 'Rules' array.");

        var mapped = new List<object>();
        var manual = new List<object>();
        var skipped = new List<string>();
        var builtInIds = new HashSet<string>(All.Select(r => r.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var node in arr)
        {
            if (node is not JsonObject ro) { skipped.Add("(non-object rule entry)"); continue; }
            string? id = (ro["ID"] ?? ro["Id"] ?? ro["id"])?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id)) { skipped.Add("(rule with no ID)"); continue; }

            string? name = (ro["Name"])?.GetValue<string>();
            string? desc = (ro["Description"])?.GetValue<string>() ?? name;
            string? expr = (ro["Expression"])?.GetValue<string>();
            int sev = ro["Severity"] is { } s && int.TryParse(s.ToString(), out var sv) ? sv : 2;

            if (builtInIds.Contains(id!))
            {
                var b = ById(id!)!;
                mapped.Add(new { id = b.Id, category = b.Category, severity = b.Severity,
                    fixable = b.Fixable, source = "built-in check", description = b.Description });
            }
            else
            {
                // descriptive-only: we surface it but FLAG that its TE dynamic expression is not evaluated.
                manual.Add(new { id, name, severity = sev, description = desc,
                    evaluation = "manual",
                    flag = "Tabular Editor dynamic expression not evaluated by this engine - shown for reference only",
                    teExpressionPresent = !string.IsNullOrWhiteSpace(expr) });
            }
        }

        return new ImportResult(mapped.Count + manual.Count, mapped.Count, manual.Count, mapped, manual, skipped);
    }

    // ================================================================ catalogue listing

    public static object Catalogue(string? category = null)
    {
        var src = All.Where(r =>
            string.IsNullOrWhiteSpace(category) || string.Equals(r.Category, category, StringComparison.OrdinalIgnoreCase));
        var rules = src.Select(r => new
        {
            id = r.Id,
            category = r.Category,
            severity = r.Severity,
            severityLabel = r.Severity == Error ? "error" : r.Severity == Warning ? "warning" : "info",
            scope = r.Scope.ToString(),
            fixable = r.Fixable,
            fixedProperty = r.FixedProperty,
            description = r.Description,
        }).ToList();

        var byCategory = All.GroupBy(r => r.Category)
            .ToDictionary(g => g.Key, g => new { count = g.Count(), fixable = g.Count(r => r.Fixable) });

        return new
        {
            totalRules = All.Count,
            fixableRules = All.Count(r => r.Fixable),
            flagOnlyRules = All.Count(r => !r.Fixable),
            byCategory,
            rules,
        };
    }
}
