using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp;

/// <summary>
/// The Solution catalog - the reusable units that turn "your data" into "your dashboard" without the
/// customer ever touching a .pbix. Each Solution (a folder under the solutions/ tree with a solution.json)
/// bundles a model spec (the scaffold spec), an input schema, recommended recipe defaults, sample data, a
/// schema-context note for the AI prompter, and - once materialised on the VM by the bake worker - a
/// starter .pbix the agent opens and builds on.
///
/// Loaded from SUPERBI_SOLUTIONS_DIR, else a "solutions" folder next to the exe, else the nearest
/// "solutions" folder walking up from the binary (dev). Mirrors the TemplateLibrary pattern: a cached
/// catalog a host can serve verbatim.
/// </summary>
public static class SolutionLibrary
{
    public sealed class SolutionInfo
    {
        // public catalog fields (safe for a host to serve verbatim)
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Vertical { get; set; } = "";
        public string Status { get; set; } = "available";
        public string Summary { get; set; } = "";
        public string Proof { get; set; } = "";
        public List<string> Inputs { get; set; } = new();
        public bool Ready { get; set; }                  // is a materialised starter present?

        // internal resolution (not serialised to the public catalog)
        [System.Text.Json.Serialization.JsonIgnore] public string Dir { get; set; } = "";
        [System.Text.Json.Serialization.JsonIgnore] public string? ModelSpecPath { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string? RecipePath { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string? SchemaPath { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string? ContextPath { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string? StarterPath { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public List<string> SampleData { get; set; } = new();
    }

    private static readonly object _lock = new();
    private static List<SolutionInfo>? _all;

    public static IReadOnlyList<SolutionInfo> All
    {
        get { lock (_lock) { return _all ??= Load(); } }
    }

    /// <summary>The public catalog projection (no file paths). Ready is read live so a starter materialised
    /// after the engine started shows without a restart.</summary>
    public static IEnumerable<object> Catalog => CatalogWhere(_ => true);

    /// <summary>The public catalog projection filtered by <paramref name="predicate"/> (same shape as
    /// <see cref="Catalog"/>). Lets a host serve a restricted subset of the catalog.</summary>
    public static IEnumerable<object> CatalogWhere(Func<SolutionInfo, bool> predicate) => All.Where(predicate).Select(s => new
    {
        // camelCase keys so a JS front end (reading id/name/status/summary/ready)
        // populates; the default serializer would otherwise emit PascalCase and the
        // consumer would render empty.
        id = s.Id, name = s.Name, vertical = s.Vertical, status = s.Status,
        summary = s.Summary, proof = s.Proof, inputs = s.Inputs,
        ready = s.StarterPath != null && File.Exists(s.StarterPath),
    });

    public static SolutionInfo? Find(string id) =>
        All.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)
                             || string.Equals(LeafId(s.Id), id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The recipe.json contents (recipe + config) for /build, or null.</summary>
    public static JsonObject? Recipe(SolutionInfo s)
    {
        if (s.RecipePath == null || !File.Exists(s.RecipePath)) return null;
        try { return JsonNode.Parse(File.ReadAllText(s.RecipePath)) as JsonObject; } catch { return null; }
    }

    /// <summary>The schema-context prose passed to the AI prompter, or "".</summary>
    public static string Context(SolutionInfo s)
        => s.ContextPath != null && File.Exists(s.ContextPath) ? File.ReadAllText(s.ContextPath) : "";

    // ---- loading -------------------------------------------------------------------------------

    public static string? SolutionsDir()
    {
        string? env = Environment.GetEnvironmentVariable("SUPERBI_SOLUTIONS_DIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;

        string baseDir = AppContext.BaseDirectory;
        string next = Path.Combine(baseDir, "solutions");
        if (Directory.Exists(next)) return next;

        // dev: walk up from the binary to find a sibling "solutions" folder
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string cand = Path.Combine(dir.FullName, "solutions");
            if (Directory.Exists(cand)) return cand;
        }
        return null;
    }

    private static List<SolutionInfo> Load()
    {
        var list = new List<SolutionInfo>();
        string? root = SolutionsDir();
        if (root == null) return list;

        // materialise the walk inside a try so a traversal error degrades to a partial catalog (never throws)
        List<string> manifests;
        try { manifests = Directory.EnumerateFiles(root, "solution.json", SearchOption.AllDirectories).ToList(); }
        catch (Exception ex) { Console.Error.WriteLine("[solutions] enumerate failed: " + ex.Message); manifests = new(); }

        foreach (var manifest in manifests)
        {
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(manifest)) as JsonObject;
                if (o == null) continue;
                string dir = Path.GetDirectoryName(manifest)!;
                string? Rel(string key) => (string?)o[key] is { Length: > 0 } p ? Path.GetFullPath(Path.Combine(dir, p)) : null;

                var s = new SolutionInfo
                {
                    Id = (string?)o["id"] ?? LeafFromDir(dir, root),
                    Name = (string?)o["name"] ?? "",
                    Vertical = (string?)o["vertical"] ?? "",
                    Status = (string?)o["status"] ?? "available",
                    Summary = (string?)o["summary"] ?? "",
                    Proof = (string?)o["proof"] ?? "",
                    Inputs = (o["inputs"] as JsonArray)?.Select(n => (string?)n ?? "").Where(x => x.Length > 0).ToList() ?? new(),
                    Dir = dir,
                    ModelSpecPath = Rel("modelSpec"),
                    RecipePath = Rel("recipe"),
                    SchemaPath = Rel("inputSchema"),
                    ContextPath = Rel("schemaContext"),
                    StarterPath = Rel("starter"),
                    SampleData = (o["sampleData"] as JsonArray)?.Select(n => (string?)n).Where(x => x is { Length: > 0 })
                                    .Select(p => Path.GetFullPath(Path.Combine(dir, p!))).ToList() ?? new(),
                };
                s.Ready = s.StarterPath != null && File.Exists(s.StarterPath);
                list.Add(s);
            }
            catch { /* skip a malformed manifest, keep the rest of the catalog */ }
        }
        return list.OrderBy(s => s.Vertical).ThenBy(s => s.Name).ToList();
    }

    private static string LeafId(string id) { int i = id.LastIndexOf('/'); return i >= 0 ? id[(i + 1)..] : id; }
    private static string LeafFromDir(string dir, string root)
        => Path.GetRelativePath(root, dir).Replace('\\', '/');
}
