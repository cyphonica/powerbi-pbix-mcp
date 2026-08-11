using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Agent;

/// <summary>
/// Renders the authoritative tool index (docs/automation-capabilities/GENERATED-TOOL-INDEX.md)
/// from the same reflection registry the agent/HTTP surfaces use, so the capability docs can
/// never drift from the code. `SuperBiMcp capability-map` writes the file; `capability-map
/// --check` re-renders in memory and exits 2 when the committed file has drifted (the CI gate).
/// Output is deterministic: ordinal sort, LF line endings, no timestamps or environment data.
/// </summary>
public static class CapabilityMap
{
    private const string DefaultRelPath = "docs/automation-capabilities/GENERATED-TOOL-INDEX.md";

    // ToolRegistry wants an IServiceProvider only for Invoke(); rendering never invokes a tool.
    private sealed class NoServices : IServiceProvider { public object? GetService(Type _) => null; }

    public static string Render()
    {
        var registry = new ToolRegistry(new NoServices());
        var tools = registry.All.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var byClass = tools.GroupBy(t => t.DeclaringType)
                           .OrderBy(g => g.Key, StringComparer.Ordinal)
                           .ToList();

        var sb = new StringBuilder();
        sb.Append("# Super-BI-MCP tool index\n\n");
        sb.Append("Generated from [McpServerTool] attributes by `SuperBiMcp capability-map` - do not hand-edit.\n\n");
        sb.Append($"Total tools: **{tools.Count}**\n\n");

        sb.Append("| Class | File | Tools |\n|---|---|---|\n");
        foreach (var g in byClass)
            sb.Append($"| {g.Key} | src/Tools/{g.Key}.cs | {g.Count()} |\n");

        foreach (var g in byClass)
        {
            sb.Append($"\n## {g.Key} ({g.Count()})\n\n");
            sb.Append("| Tool | Description | Args |\n|---|---|---|\n");
            foreach (var t in g)
                sb.Append($"| {t.Name} | {FirstSentence(t.Description)} | {Args(t)} |\n");
        }
        return sb.ToString();
    }

    /// <summary>`capability-map [out.md]` writes the index; `capability-map --check` exits 2 on drift.</summary>
    public static int Run(string[] args)
    {
        bool check = args.Skip(1).Any(a => a == "--check");
        string? explicitPath = args.Skip(1).FirstOrDefault(a => !a.StartsWith('-'));
        string path = explicitPath
            ?? Path.Combine(RepoRoot(), DefaultRelPath.Replace('/', Path.DirectorySeparatorChar));

        string fresh = Render();
        if (check)
        {
            string committed = File.Exists(path) ? File.ReadAllText(path).Replace("\r\n", "\n") : "";
            if (committed != fresh)
            {
                Console.WriteLine("capability map is stale - run: SuperBiMcp capability-map");
                return 2;
            }
            Console.WriteLine($"capability map is current ({path})");
            return 0;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, fresh, new UTF8Encoding(false));
        Console.WriteLine($"wrote {path}");
        return 0;
    }

    /// <summary>Walk up from the binary until the directory holding src/SuperBiMcp.csproj (the repo root).</summary>
    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "SuperBiMcp.csproj")))
                return dir.FullName;
        throw new InvalidOperationException(
            $"repo root not found - no src/SuperBiMcp.csproj above {AppContext.BaseDirectory}; pass an explicit output path");
    }

    private static string FirstSentence(string description)
    {
        string d = description.Replace('\r', ' ').Replace('\n', ' ').Trim();
        for (int i = d.IndexOf(". ", StringComparison.Ordinal); i >= 0; i = d.IndexOf(". ", i + 1, StringComparison.Ordinal))
        {
            // "e.g. " / "i.e. " are not sentence ends
            if (d[..i].EndsWith("e.g", StringComparison.OrdinalIgnoreCase)
                || d[..i].EndsWith("i.e", StringComparison.OrdinalIgnoreCase)) continue;
            d = d[..(i + 1)];
            break;
        }
        return d.Replace("|", "\\|");
    }

    // args come from the registry's InputSchema (same scalar-vs-service rule as the SDK):
    // property order follows the method signature; optional args carry a trailing '?'.
    private static string Args(ToolRegistry.ToolInfo t)
    {
        var props = t.InputSchema["properties"] as JsonObject;
        if (props == null || props.Count == 0) return "-";
        var required = (t.InputSchema["required"] as JsonArray)?.Select(n => n!.GetValue<string>()).ToHashSet()
            ?? new HashSet<string>();
        return string.Join(", ", props.Select(p =>
            $"{p.Key}:{p.Value!["type"]}{(required.Contains(p.Key) ? "" : "?")}"));
    }
}
