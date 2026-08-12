using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Proves the embedded SuperBiBase base theme - an ORIGINAL theme we author and own (it replaced the
/// CY24SU06 file extracted from a Desktop install, which we could not redistribute) - and the palette-merge
/// pipeline built on it. Every generated .pbix embeds this theme as its active base theme, so these are
/// load-bearing checks:
///   - the embedded resource loads, parses, is named SuperBiBase and carries a &gt;= 8 colour dataColors palette,
///   - every colour key the palette merge overrides exists on the base, plus textClasses/visualStyles structure,
///   - every top-level key is known to the bundled reportThemeSchema (and the schema's required keys are present),
///   - ResolveStagedTheme(Default) is the plain SuperBiBase byte-for-byte at the right part path,
///   - a preset palette merges to SuperBiBase-&lt;Palette&gt; with the palette's colours and the base's structure,
///   - a brand colour wins as SuperBiBase-Brand, leading with the brand hex,
///   - the Studio catalog's Default swatch matches the base palette's primary,
///   - no CY24SU06 resource remains in the assembly.
/// </summary>
public sealed class BaseThemeTests
{
    // the colour keys ResolveStagedTheme/MergePaletteIntoBase override on the base theme - keep in sync
    // with ReportLayoutBuilder.PaletteColorKeys
    private static readonly string[] PaletteColorKeys =
    {
        "dataColors", "background", "foreground", "tableAccent",
        "good", "neutral", "bad", "maximum", "center", "minimum", "null",
    };

    private static byte[] LoadResource(string suffix)
    {
        var asm = typeof(ReportLayoutBuilder).Assembly;
        string? resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        Assert.False(resName == null, $"embedded resource '*{suffix}' not found in the assembly");
        using var stream = asm.GetManifestResourceStream(resName!)!;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static JsonObject ParseObj(byte[] bytes)
    {
        var obj = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(bytes)) as JsonObject;
        Assert.NotNull(obj);
        return obj!;
    }

    private static JsonObject BaseTheme() => ParseObj(LoadResource("SuperBiBase.json"));

    // ---------------------------------------------------------------- the embedded base theme itself

    [Fact]
    public void EmbeddedSuperBiBase_Loads_Parses_WithFullPaletteAndStructure()
    {
        var theme = BaseTheme();
        Assert.Equal("SuperBiBase", (string?)theme["name"]);
        Assert.Equal(ReportLayoutBuilder.BaseThemeName, (string?)theme["name"]);

        // a non-empty dataColors palette of >= 8 DISTINCT, well-formed #RRGGBB entries
        var dc = theme["dataColors"] as JsonArray;
        Assert.NotNull(dc);
        var colours = dc!.Select(n => (string?)n).ToList();
        Assert.True(colours.Count >= 8, $"expected >= 8 dataColors, got {colours.Count}");
        Assert.Equal(colours.Count, colours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(colours, c =>
        {
            Assert.Matches("^#[0-9A-F]{6}$", c!);
        });

        // every colour key the palette merge overrides exists on the base (so a merge never ADDS keys)
        foreach (var key in PaletteColorKeys)
            Assert.True(theme.ContainsKey(key), $"base theme should carry palette colour key '{key}'");

        // the structural families the renderer reads
        Assert.IsType<JsonObject>(theme["textClasses"]);
        foreach (var cls in new[] { "callout", "title", "header", "label" })
            Assert.IsType<JsonObject>(theme["textClasses"]![cls]);
        var styles = Assert.IsType<JsonObject>(theme["visualStyles"]);
        Assert.IsType<JsonObject>(styles["*"]);                       // the wildcard defaults block
        Assert.IsType<JsonObject>(styles["*"]!["*"]);
        Assert.IsType<JsonObject>(styles["page"]);                    // page outspace/background defaults
    }

    [Fact]
    public void SuperBiBase_TopLevelKeys_AreAllKnownToTheBundledSchema()
    {
        var theme = BaseTheme();
        // the schema holds duplicate property names deep inside (gridlineColor/gridLineColor), which JsonNode
        // rejects - JsonDocument tolerates them, and we only read the top level here
        using var doc = JsonDocument.Parse(LoadResource("reportThemeSchema-2.155.json"));
        var props = doc.RootElement.GetProperty("properties");

        // the schema's required keys are present on the theme ...
        foreach (var req in doc.RootElement.GetProperty("required").EnumerateArray())
            Assert.True(theme.ContainsKey(req.GetString()!), $"theme is missing required key '{req.GetString()}'");

        // ... and every top-level theme key is a property the schema knows (additionalProperties is false)
        foreach (var kv in theme)
            Assert.True(props.TryGetProperty(kv.Key, out _), $"theme key '{kv.Key}' is not in the schema");

        // spot-check types the code relies on
        Assert.True(theme["name"] is JsonValue);
        Assert.True(theme["dataColors"] is JsonArray);
        Assert.True(theme["visualStyles"] is JsonObject);
    }

    // ---------------------------------------------------------------- staged theme resolution

    [Fact]
    public void ResolveStagedTheme_Default_IsPlainSuperBiBase_ByteForByte()
    {
        var staged = ReportLayoutBuilder.ResolveStagedTheme(null);
        Assert.Equal("SuperBiBase", staged.ThemeName);
        Assert.Equal("Report/StaticResources/SharedResources/BaseThemes/SuperBiBase.json", staged.PartName);
        Assert.Equal(LoadResource("SuperBiBase.json"), staged.Bytes);   // unchanged, byte-for-byte
    }

    [Fact]
    public void ResolveStagedTheme_Ocean_MergesPaletteColours_AndPreservesStructure()
    {
        var staged = ReportLayoutBuilder.ResolveStagedTheme("ocean");
        Assert.Equal("SuperBiBase-Ocean", staged.ThemeName);
        Assert.Equal("Report/StaticResources/SharedResources/BaseThemes/SuperBiBase-Ocean.json", staged.PartName);

        var merged = ParseObj(staged.Bytes);
        var baseTheme = BaseTheme();
        var ocean = ParseObj(LoadResource("theme-ocean.json"));

        Assert.Equal("SuperBiBase-Ocean", (string?)merged["name"]);

        // the palette's colour keys took over (compared against the ACTUAL embedded palette resource)
        foreach (var key in PaletteColorKeys)
            Assert.True(JsonNode.DeepEquals(ocean[key], merged[key]),
                $"merged theme should carry the Ocean palette's '{key}'");

        // every structural family is the base's, untouched
        Assert.True(JsonNode.DeepEquals(baseTheme["visualStyles"], merged["visualStyles"]));
        Assert.True(JsonNode.DeepEquals(baseTheme["textClasses"], merged["textClasses"]));

        // non-palette colour keys ride through from the base unchanged
        foreach (var key in new[] { "backgroundLight", "backgroundNeutral", "hyperlink", "visitedHyperlink" })
            Assert.True(JsonNode.DeepEquals(baseTheme[key], merged[key]),
                $"non-palette key '{key}' should be preserved from the base");
    }

    [Fact]
    public void ResolveStagedTheme_BrandColour_Wins_AndLeadsThePalette()
    {
        var staged = ReportLayoutBuilder.ResolveStagedTheme("ocean", "#16365C");   // brand beats the preset
        Assert.Equal("SuperBiBase-Brand", staged.ThemeName);

        var merged = ParseObj(staged.Bytes);
        Assert.Equal("SuperBiBase-Brand", (string?)merged["name"]);
        Assert.Equal("#16365C", (string?)((JsonArray)merged["dataColors"]!)[0]);
        Assert.True(JsonNode.DeepEquals(BaseTheme()["visualStyles"], merged["visualStyles"]));
    }

    [Fact]
    public void ThemeCatalog_DefaultSwatch_MatchesTheBasePrimaryColour()
    {
        var catalog = JsonNode.Parse(JsonSerializer.Serialize(ReportLayoutBuilder.ThemeCatalog())) as JsonArray;
        var def = catalog!.OfType<JsonObject>().First(t => (string?)t["key"] == "default");
        string basePrimary = (string?)((JsonArray)BaseTheme()["dataColors"]!)[0] ?? "";
        Assert.Equal(basePrimary, (string?)def["primaryColor"]);
    }

    // ---------------------------------------------------------------- the extracted theme is gone

    [Fact]
    public void NoExtractedCY24SU06Resource_RemainsInTheAssembly()
    {
        var names = typeof(ReportLayoutBuilder).Assembly.GetManifestResourceNames();
        Assert.DoesNotContain(names, n => n.Contains("CY24SU06", StringComparison.OrdinalIgnoreCase));
    }
}
