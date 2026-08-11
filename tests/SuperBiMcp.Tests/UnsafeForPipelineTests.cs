using System.Text.Json.Nodes;
using SuperBiMcp.Agent;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Guards the [UnsafeForPipeline] marker on the interactive-attach tools (connect_model,
/// list_open_models). The registry must keep them discoverable (the local MCP stdio server and
/// the capability map still list them) while refusing them on both pipeline surfaces it feeds:
/// ToAnthropicTools must drop them before any caller filter runs, and Invoke must return a
/// refusal before resolving services. All offline - discovery and refusal never touch Desktop.
/// </summary>
public sealed class UnsafeForPipelineTests
{
    // ToolRegistry only needs a provider for Invoke(); the refusal must fire before it is used.
    private sealed class NoServices : IServiceProvider { public object? GetService(Type _) => null; }

    private static readonly ToolRegistry Registry = new(new NoServices());

    private static readonly string[] Marked = { "connect_model", "list_open_models" };

    [Fact]
    public void Marked_tools_are_flagged_but_stay_discoverable()
    {
        foreach (var name in Marked)
        {
            var t = Registry.Get(name);
            Assert.NotNull(t);
            Assert.True(t!.UnsafeForPipeline, $"{name} should carry the UnsafeForPipeline flag");
        }
        // a live-model tool without the marker stays unflagged
        var summary = Registry.Get("get_model_summary");
        Assert.NotNull(summary);
        Assert.False(summary!.UnsafeForPipeline);
    }

    [Fact]
    public void Descriptions_carry_the_unsafe_label()
    {
        foreach (var name in Marked)
            Assert.StartsWith("UNSAFE-FOR-PIPELINE (interactive attach only):", Registry.Get(name)!.Description);
    }

    [Fact]
    public void ToAnthropicTools_drops_marked_tools_even_with_an_allow_all_filter()
    {
        foreach (var tools in new[] { Registry.ToAnthropicTools(), Registry.ToAnthropicTools(_ => true) })
        {
            var names = tools.Select(n => (string?)n!["name"]).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var name in Marked)
                Assert.DoesNotContain(name, names);
            // the drop is surgical: unmarked tools still flow through
            Assert.Contains("get_model_summary", names);
        }
    }

    [Fact]
    public void Invoke_refuses_marked_tools_before_resolving_services()
    {
        foreach (var name in Marked)
        {
            // NoServices cannot resolve ModelService, so reaching the dispatch path would throw;
            // a clean JSON refusal proves the guard fires first.
            var result = JsonNode.Parse(Registry.Invoke(name, null))!;
            Assert.False((bool)result["ok"]!);
            Assert.Contains("interactive-attach only", (string)result["error"]!);
        }
    }

    [Fact]
    public void Invoke_still_reports_unknown_tools_as_unknown()
    {
        var result = JsonNode.Parse(Registry.Invoke("no_such_tool", null))!;
        Assert.False((bool)result["ok"]!);
        Assert.Contains("unknown tool", (string)result["error"]!);
    }
}
