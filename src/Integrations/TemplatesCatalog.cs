using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The template gallery: the public, customer-facing projection of the built-in Solution starters, served at
/// GET /templates. Each entry is the "pick a starting point" card the cloud shows before a build - id, title,
/// industry, a one-line description, the headline KPIs, the model tables and whether sample data ships with it.
///
/// Honest by construction: every field is pulled from the Solution's OWN files (solution.json / recipe.json /
/// schema.json / README) - it NEVER invents a title, industry, KPI or table the Solution does not declare. A
/// field the Solution does not provide is OMITTED from the card rather than faked, so the gallery can only ever
/// show what really ships.
///
///   id            - the Solution key (e.g. "finance/cfo-overview"); the build-from-template allowlist token
///   title         - solution.json "name"
///   industry      - solution.json "vertical"
///   description   - solution.json "summary"
///   kpis          - the recipe's config.kpis[].label (the headline cards); omitted when the recipe has none
///   tables        - the model tables, from the input schema's files[] (CSV stems), else the sample data stems
///   hasSampleData - true when the Solution bundles sample CSVs that exist on disk (so it can be built as a demo)
///
/// The catalog reuses <see cref="SolutionLibrary"/> for resolution (the same env-anchored solutions tree the
/// /build and /ingest Solution paths use), so a template id is always a real, known Solution.
/// </summary>
public static class TemplatesCatalog
{
    /// <summary>One gallery card. Optional fields are null/empty when the Solution does not declare them, and
    /// are dropped from the served JSON by <see cref="Card.ToJson"/> so the gallery never shows a faked value.</summary>
    public sealed class Card
    {
        public string Id { get; init; } = "";
        public string? Title { get; init; }
        public string? Industry { get; init; }
        public string? Description { get; init; }
        public List<string>? Kpis { get; init; }       // null => the Solution's recipe declares no KPI cards
        public List<string>? Tables { get; init; }      // null => neither schema nor sample data names the tables
        public bool HasSampleData { get; init; }

        /// <summary>Serialise to the served shape, OMITTING any field the Solution did not provide (never faked).</summary>
        public JsonObject ToJson()
        {
            var o = new JsonObject { ["id"] = Id, ["hasSampleData"] = HasSampleData };
            if (!string.IsNullOrWhiteSpace(Title)) o["title"] = Title;
            if (!string.IsNullOrWhiteSpace(Industry)) o["industry"] = Industry;
            if (!string.IsNullOrWhiteSpace(Description)) o["description"] = Description;
            if (Kpis is { Count: > 0 }) o["kpis"] = ToArray(Kpis);
            if (Tables is { Count: > 0 }) o["tables"] = ToArray(Tables);
            return o;
        }

        private static JsonArray ToArray(IEnumerable<string> xs)
        {
            var a = new JsonArray();
            foreach (var x in xs) a.Add(x);
            return a;
        }
    }

    /// <summary>The gallery cards for every built-in Solution, ordered as <see cref="SolutionLibrary"/> orders
    /// them (by industry then name). Read live so a Solution added after start-up still appears - the underlying
    /// catalog is cached by SolutionLibrary, this projection is cheap.</summary>
    public static List<Card> Cards()
        => SolutionLibrary.All.Select(BuildCard).ToList();

    /// <summary>The served JSON projection: one OMIT-the-absent object per Solution.</summary>
    public static JsonArray Templates()
    {
        var arr = new JsonArray();
        foreach (var c in Cards()) arr.Add(c.ToJson());
        return arr;
    }

    /// <summary>
    /// The allowlist check for build-from-template: resolve a caller-supplied template id to a REAL Solution, or
    /// null. Rejects anything that is not an exact known id - a traversal attempt ("../", an absolute or rooted
    /// path, an embedded separator the catalog does not list) can never match, because the id is matched against
    /// the known Solution keys, never used as a path segment.
    ///
    /// A full Solution key (one containing "/", e.g. "finance/cfo-overview") matches that exact catalogued
    /// Solution. A leaf-id alias (no "/", e.g. "cfo-overview") is accepted ONLY when EXACTLY ONE catalogued
    /// Solution carries that leaf; when two or more share it (e.g. both business-exec/executive-summary and
    /// retail-fmcg/executive-summary have the leaf "executive-summary") the alias is ambiguous and resolves to
    /// null - the caller must supply the full key. This is a real uniqueness test over <see cref="SolutionLibrary.All"/>,
    /// not the order-dependent first match SolutionLibrary.Find would give.
    /// </summary>
    public static SolutionLibrary.SolutionInfo? Resolve(string? templateId)
    {
        if (string.IsNullOrWhiteSpace(templateId)) return null;
        string id = templateId.Trim();

        // hard-reject obviously path-y ids up front (defence in depth - the matches below would miss them anyway)
        if (id.Contains("..") || id.Contains('\\') || Path.IsPathRooted(id) || id.StartsWith("/"))
            return null;

        var catalog = SolutionLibrary.All;

        // a full key (carries a "/") must match a catalogued Solution's id exactly - unambiguous by construction
        if (id.Contains('/'))
            return catalog.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

        // a leaf alias (no "/") is accepted only when EXACTLY ONE catalogued Solution carries that leaf; two or
        // more sharing the leaf is ambiguous and resolves to null (require the full key), never an order-dependent pick
        var byLeaf = catalog
            .Where(s => string.Equals(Leaf(s.Id), id, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();
        return byLeaf.Count == 1 ? byLeaf[0] : null;
    }

    /// <summary>The last path segment of a Solution id (e.g. "executive-summary" from "retail-fmcg/executive-summary").</summary>
    private static string Leaf(string id) { int i = id.LastIndexOf('/'); return i >= 0 ? id[(i + 1)..] : id; }

    // ---- card construction (all metadata pulled from the Solution's own files) ----------------------

    private static Card BuildCard(SolutionLibrary.SolutionInfo s)
    {
        return new Card
        {
            Id = s.Id,
            Title = Blank(s.Name),
            Industry = Blank(s.Vertical),
            Description = Blank(s.Summary),
            Kpis = KpisFromRecipe(s),
            Tables = TablesFromSchemaOrSamples(s),
            HasSampleData = HasSampleData(s),
        };
    }

    private static string? Blank(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

    /// <summary>The headline KPI labels from the recipe's config.kpis[].label, in order. Returns null when the
    /// recipe declares no kpis array (e.g. a category-style recipe) - so the card omits the field, never fakes it.</summary>
    private static List<string>? KpisFromRecipe(SolutionLibrary.SolutionInfo s)
    {
        var rec = SolutionLibrary.Recipe(s);
        if (rec?["config"] is not JsonObject cfg || cfg["kpis"] is not JsonArray kpis) return null;
        var labels = kpis
            .OfType<JsonObject>()
            .Select(k => (string?)k["label"])
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l!)
            .ToList();
        return labels.Count > 0 ? labels : null;
    }

    /// <summary>The model tables. Prefer the input schema's declared files[] (the validated table set), falling
    /// back to the sample data filenames - both are the real CSV stems the model is built on. Null when the
    /// Solution names tables in neither place (so the field is omitted, not faked).</summary>
    private static List<string>? TablesFromSchemaOrSamples(SolutionLibrary.SolutionInfo s)
    {
        var fromSchema = TablesFromSchema(s.SchemaPath);
        if (fromSchema is { Count: > 0 }) return fromSchema;

        var fromSamples = s.SampleData
            .Select(p => Path.GetFileNameWithoutExtension(p))
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return fromSamples.Count > 0 ? fromSamples : null;
    }

    private static List<string>? TablesFromSchema(string? schemaPath)
    {
        if (schemaPath == null || !File.Exists(schemaPath)) return null;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(schemaPath)) is not JsonObject root) return null;
            if (root["files"] is not JsonArray files) return null;
            var names = files
                .OfType<JsonObject>()
                .Select(f => (string?)f["file"])
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => Path.GetFileNameWithoutExtension(f!))
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return names.Count > 0 ? names : null;
        }
        catch { return null; }   // a malformed schema simply omits the tables field, never throws
    }

    /// <summary>True when the Solution bundles sample CSVs that actually exist on disk - i.e. it can be built as a
    /// no-upload demo via build-from-template.</summary>
    private static bool HasSampleData(SolutionLibrary.SolutionInfo s)
        => s.SampleData.Count > 0 && s.SampleData.Any(File.Exists);
}
