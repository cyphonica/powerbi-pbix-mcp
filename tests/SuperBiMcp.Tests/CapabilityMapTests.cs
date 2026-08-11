using SuperBiMcp.Agent;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Guards the generated capability map (docs/automation-capabilities/GENERATED-TOOL-INDEX.md):
/// Render() must be deterministic, mirror the reflection registry's tool count, and match the
/// committed file after CRLF normalisation - so any tool change without a regenerated index
/// fails the suite, not just the CI drift step.
/// </summary>
public sealed class CapabilityMapTests
{
    // ToolRegistry only needs a provider for Invoke(); discovery works without services.
    private sealed class NoServices : IServiceProvider { public object? GetService(Type _) => null; }

    [Fact]
    public void Render_is_deterministic()
    {
        Assert.Equal(CapabilityMap.Render(), CapabilityMap.Render());
    }

    [Fact]
    public void Render_lists_known_tools_and_total_matches_registry()
    {
        string md = CapabilityMap.Render();
        Assert.Contains("| add_measure |", md);
        Assert.Contains("| read_pbir |", md);

        int discovered = new ToolRegistry(new NoServices()).All.Count;
        Assert.True(discovered >= 396, $"registry discovered only {discovered} tools (expected >= 396)");
        Assert.Contains($"Total tools: **{discovered}**", md);
    }

    [Fact]
    public void Committed_index_matches_generator()
    {
        string path = Path.Combine(RepoRoot(), "docs", "automation-capabilities", "GENERATED-TOOL-INDEX.md");
        Assert.True(File.Exists(path), $"missing {path} - run: SuperBiMcp capability-map");
        string committed = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.Equal(CapabilityMap.Render(), committed);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "src", "SuperBiMcp.csproj")))
                return dir.FullName;
        throw new InvalidOperationException("repo root not found above " + AppContext.BaseDirectory);
    }
}
