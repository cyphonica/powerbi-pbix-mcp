using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SuperBiMcp.Services;

/// <summary>One visual's formatting trees as the theme lint sees them (parsed, read-only).</summary>
public sealed record ThemeVisualEntry(string Page, string Visual, string? Type,
    JsonObject? Objects, JsonObject? VcObjects);

/// <summary>
/// Theme-compliance + colour-inventory engine over parsed formatting trees. Pure JSON walking - no
/// session, no disk - so every rule unit-tests without a report. Colour literals appear in two spellings:
/// plain "#RRGGBB" (theme slots) and single-quoted "'#RRGGBB'" (expr.Literal.Value inside visual
/// objects/vcObjects); both are recognised, and the recolour pass preserves whichever spelling it found.
/// </summary>
public static class ThemeAuditor
{
    private static readonly Regex HexRx = new("^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$", RegexOptions.Compiled);

    /// <summary>Normalise a raw string to an uppercase "#RRGGBB[AA]" colour literal, accepting the
    /// single-quoted expr form; null when the string is not a colour literal.</summary>
    internal static string? AsColourLiteral(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') s = s[1..^1];
        return HexRx.IsMatch(s) ? s.ToUpperInvariant() : null;
    }

    /// <summary>Walk a JSON tree yielding every colour literal with its dotted path.</summary>
    internal static IEnumerable<(string path, string colour)> WalkColours(JsonNode? node, string path)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var (k, v) in o)
                    foreach (var hit in WalkColours(v, path.Length == 0 ? k : path + "." + k))
                        yield return hit;
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                    foreach (var hit in WalkColours(a[i], $"{path}[{i}]"))
                        yield return hit;
                break;
            case JsonValue v:
                if (v.TryGetValue<string>(out var s) && AsColourLiteral(s) is string c)
                    yield return (path, c);
                break;
        }
    }

    /// <summary>Every colour the theme itself declares (palette + structural + sentiment + CF stops +
    /// text classes + visualStyle defaults), normalised - the "on-theme" set.</summary>
    internal static HashSet<string> ThemeColourSet(JsonObject? theme)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (theme == null) return set;
        foreach (var (_, colour) in WalkColours(theme, ""))
            set.Add(colour);
        return set;
    }

    // ---------------------------------------------------------------- compliance audit

    /// <summary>audit_theme_compliance: walk every visual's objects/vcObjects trees against the theme and
    /// report hard-coded formatting that fights it - off-palette colour literals, on-palette colours that
    /// FREEZE the palette (a theme swap will not restyle them), font family/size overrides of the theme's
    /// title text class, and card-style (background/border) overrides where the theme already styles cards.</summary>
    public static object AuditCompliance(JsonObject? theme, IReadOnlyList<ThemeVisualEntry> visuals)
    {
        var themeColours = ThemeColourSet(theme);
        string? themeTitleFont = (string?)theme?["textClasses"]?["title"]?["fontFace"];
        double? themeTitleSize = AsNumber(theme?["textClasses"]?["title"]?["fontSize"]);
        bool themeStylesCards = theme?["visualStyles"]?["*"]?["*"] is JsonObject def
                                && (def["background"] != null || def["border"] != null);

        var violations = new List<object>();
        foreach (var v in visuals)
        {
            // hard-coded colour literals across BOTH buckets
            foreach (var (bucket, tree) in new (string, JsonObject?)[] { ("objects", v.Objects), ("vcObjects", v.VcObjects) })
            {
                if (tree == null) continue;
                foreach (var (path, colour) in WalkColours(tree, bucket))
                {
                    if (theme == null)
                        violations.Add(Violation(v, "hard-coded-colour", path,
                            $"hard-coded {colour} (no custom theme to judge it against).",
                            "apply generate_theme / apply_report_theme, then re-run this audit"));
                    else if (!themeColours.Contains(colour))
                        violations.Add(Violation(v, "off-palette-colour", path,
                            $"hard-coded {colour} is not in the theme's palette or structural colours.",
                            "recolor_report (map it onto a theme colour) or set_visual_format to clear the override"));
                    else
                        violations.Add(Violation(v, "hard-coded-theme-colour", path,
                            $"hard-coded {colour} duplicates a theme colour - a theme swap will NOT restyle it.",
                            "clear the override with set_visual_format / clear_visual_styling and let the theme drive it"));
                }
            }

            // font family / size overrides on the title card vs the theme's title text class
            if (v.VcObjects?["title"] is JsonArray ta && ta.FirstOrDefault() is JsonObject t0
                && t0["properties"] is JsonObject tp)
            {
                string? font = LiteralText(tp["fontFamily"]);
                if (font != null && themeTitleFont != null
                    && !font.Contains(themeTitleFont, StringComparison.OrdinalIgnoreCase)
                    && !themeTitleFont.Contains(font, StringComparison.OrdinalIgnoreCase))
                    violations.Add(Violation(v, "font-override", "vcObjects.title.fontFamily",
                        $"title font '{font}' overrides the theme's title text class ('{themeTitleFont}').",
                        "clear the override (set_visual_format) or change the theme text class (set_theme_text_class)"));

                double? size = AsNumber(ParseNumericLiteral(tp["fontSize"]));
                if (size.HasValue && themeTitleSize.HasValue && Math.Abs(size.Value - themeTitleSize.Value) > 0.01)
                    violations.Add(Violation(v, "font-size-override", "vcObjects.title.fontSize",
                        $"title font size {size} overrides the theme's title text class size ({themeTitleSize}).",
                        "clear the override (set_visual_format) or change the theme text class (set_theme_text_class)"));
            }

            // card-style overrides where the theme already styles every visual's card
            if (themeStylesCards && v.VcObjects != null)
                foreach (var card in new[] { "background", "border", "dropShadow" })
                    if (v.VcObjects[card] is JsonArray ca && ca.FirstOrDefault() is JsonObject c0
                        && c0["properties"] is JsonObject cp && cp.Count > 0)
                        violations.Add(Violation(v, "card-style-override", $"vcObjects.{card}",
                            $"per-visual {card} override where the theme's visualStyles already set the card look.",
                            "clear_visual_styling (or set_visual_format) so the theme drives the card style"));
        }

        return new
        {
            ok = true,
            hasCustomTheme = theme != null,
            themeName = (string?)theme?["name"],
            themeColourCount = themeColours.Count,
            visualsScanned = visuals.Count,
            violations,
            violationCount = violations.Count,
            verdict = violations.Count == 0 ? "compliant" : "review",
            note = theme == null
                ? "No custom theme is applied - every hard-coded colour is listed; apply a theme to make compliance meaningful."
                : "Overrides listed most-specific first; kinds: off-palette-colour, hard-coded-theme-colour, font-override, font-size-override, card-style-override.",
        };
    }

    private static object Violation(ThemeVisualEntry v, string kind, string where, string detail, string suggestedFix)
        => new { page = v.Page, visual = v.Visual, visualType = v.Type, kind, location = where, detail, suggestedFix };

    // ---------------------------------------------------------------- colour inventory

    /// <summary>extract_report_colors: a report-wide inventory of every hardcoded colour literal across the
    /// visuals AND the theme, with the exact locations of each.</summary>
    public static object CollectColors(JsonObject? theme, IReadOnlyList<ThemeVisualEntry> visuals)
    {
        var byColour = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void Hit(string colour, string where)
        {
            if (!byColour.TryGetValue(colour, out var list)) byColour[colour] = list = new List<string>();
            list.Add(where);
        }

        int themeHits = 0, visualHits = 0;
        if (theme != null)
            foreach (var (path, colour) in WalkColours(theme, "theme"))
            { Hit(colour, path); themeHits++; }
        foreach (var v in visuals)
        {
            foreach (var (path, colour) in WalkColours(v.Objects, $"{v.Page}/{v.Visual}.objects"))
            { Hit(colour, path); visualHits++; }
            foreach (var (path, colour) in WalkColours(v.VcObjects, $"{v.Page}/{v.Visual}.vcObjects"))
            { Hit(colour, path); visualHits++; }
        }

        var colours = byColour
            .OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => new { color = kv.Key, count = kv.Value.Count, locations = kv.Value })
            .ToList();
        return new
        {
            ok = true,
            distinctColors = colours.Count,
            themeOccurrences = themeHits,
            visualOccurrences = visualHits,
            colors = colours,
        };
    }

    // ---------------------------------------------------------------- recolour

    /// <summary>Parse a recolour map {"#OLD":"#NEW", ...}; keys and values are normalised and validated.</summary>
    internal static Dictionary<string, string> ParseColorMap(string colorMapJson)
    {
        var spec = JsonNode.Parse(colorMapJson) as JsonObject
                   ?? throw new ArgumentException("colorMap must be a JSON object of oldColour -> newColour.");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in spec)
        {
            string from = AsColourLiteral(k) ?? throw new ArgumentException($"'{k}' is not a hex colour literal.");
            string to = AsColourLiteral(v?.ToString()) ?? throw new ArgumentException($"'{v}' is not a hex colour literal.");
            map[from] = to;
        }
        if (map.Count == 0) throw new ArgumentException("colorMap has no entries.");
        return map;
    }

    /// <summary>Replace every mapped colour literal IN PLACE across a JSON tree, preserving the quoted /
    /// plain spelling of each occurrence. Returns the replacement count and tallies per colour.</summary>
    internal static int RecolourNode(JsonNode? node, IReadOnlyDictionary<string, string> map,
        IDictionary<string, int>? tally = null)
    {
        int replaced = 0;
        switch (node)
        {
            case JsonObject o:
                foreach (var key in o.Select(kv => kv.Key).ToList())
                {
                    if (o[key] is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        string raw = s.Trim();
                        bool quoted = raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'';
                        if (AsColourLiteral(raw) is string c && map.TryGetValue(c, out var to))
                        {
                            o[key] = quoted ? $"'{to}'" : to;
                            replaced++;
                            if (tally != null) tally[c] = tally.TryGetValue(c, out var n) ? n + 1 : 1;
                        }
                    }
                    else replaced += RecolourNode(o[key], map, tally);
                }
                break;
            case JsonArray a:
                for (int i = 0; i < a.Count; i++)
                {
                    if (a[i] is JsonValue jv && jv.TryGetValue<string>(out var s))
                    {
                        string raw = s.Trim();
                        bool quoted = raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'';
                        if (AsColourLiteral(raw) is string c && map.TryGetValue(c, out var to))
                        {
                            a[i] = quoted ? $"'{to}'" : to;
                            replaced++;
                            if (tally != null) tally[c] = tally.TryGetValue(c, out var n) ? n + 1 : 1;
                        }
                    }
                    else replaced += RecolourNode(a[i], map, tally);
                }
                break;
        }
        return replaced;
    }

    // ---------------------------------------------------------------- small helpers

    /// <summary>Unwrap an expr.Literal.Value text literal ('Segoe UI' -> Segoe UI); null otherwise.</summary>
    private static string? LiteralText(JsonNode? node)
    {
        JsonNode? n = node;
        if (n is JsonObject eo && eo["expr"] is JsonNode e) n = e;
        string? raw = (n as JsonObject)?["Literal"]?["Value"]?.GetValue<string>();
        if (raw == null && node is JsonValue v && v.TryGetValue<string>(out var s)) raw = s;
        if (raw == null) return null;
        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'') raw = raw[1..^1].Replace("''", "'");
        return raw;
    }

    private static string? ParseNumericLiteral(JsonNode? node)
    {
        string? t = LiteralText(node);
        if (t == null) return null;
        return t.EndsWith("D", StringComparison.OrdinalIgnoreCase) || t.EndsWith("L", StringComparison.OrdinalIgnoreCase)
            ? t[..^1] : t;
    }

    private static double? AsNumber(object? o)
    {
        if (o is null) return null;
        if (o is JsonNode n)
        {
            try { return n.GetValue<double>(); }
            catch { try { return n.GetValue<int>(); } catch { o = n.ToString(); } }
        }
        return double.TryParse(o.ToString(), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
