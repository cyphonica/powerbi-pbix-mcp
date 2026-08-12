using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp;

/// <summary>
/// Builds a legacy Power BI <c>Report/Layout</c> JSON document - a MULTI-PAGE consultant-style report - straight
/// from the model spec the rest of the engine already authors (the same {tables[],expressions[],relationships[]}
/// JSON Headless.GenerateProject consumes). No live engine, no template: it reads the spec's real tables,
/// columns (+dataType), measures and relationships and binds every visual generically to those names, so the
/// same builder works for ALL solutions (business-exec, retail-fmcg, fitness, sales, ...).
///
/// Pages produced (each a 1280x720 section, displayOption 1, Overview = active section 0):
///   Overview      - up to 4 KPI cards, a hero time-series, and a primary breakdown.
///   By &lt;Dim&gt; - one per detected dimension (up to 3): a sorted bar/column + a tableEx of that dim + measures.
///   Trend         - a multi-measure line by the date column + a tableEx of date + measures.
///   Detail        - a granular tableEx (primary dimension + date + measures).
///
/// The shapes mirror what the live product emits and what ReportService.BuildSingleVisual / AddContainer produce:
/// config is a nested-as-escaped-string at the report, section and visual levels; queryRef strings are
/// "Table.Field"; the prototypeQuery Select Name equals the projection queryRef (that string IS the bind); a
/// cross-table visual carries a From alias per table. Every builder is defensive: a visual whose inputs are
/// missing is skipped, never emitted half-formed. The SuperBiBase theme + resourcePackages are preserved.
/// </summary>
public static class ReportLayoutBuilder
{
    // PBI's literal style: do not over-escape (matches ReportService.JsonOpts).
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private const double PageWidth = 1280;
    private const double PageHeight = 720;
    private const double Margin = 24;
    private const double Gap = 16;

    /// <summary>The built-in base theme the generated report binds to. Power BI Desktop's renderer requires a
    /// current theme (it reads themeCurrent.customTheme on load and throws "Cannot read properties of undefined
    /// (reading 'customTheme')" when the report declares none). SuperBiBase is an original base theme we
    /// author and own (Resources/SuperBiBase.json); the .json is folded into the package as a static resource
    /// (see StageTheme), so the report is self-contained and the name resolves from the embedded part.</summary>
    public const string BaseThemeName = "SuperBiBase";

    /// <summary>The pbix part path of the bundled base theme, relative to the report folder. Place the theme
    /// bytes at <c>&lt;name&gt;.Report/StaticResources/SharedResources/BaseThemes/SuperBiBase.json</c> in the PBIP
    /// and PbixCompiler folds it to this part. The Layout's resourcePackages references the same path.</summary>
    public const string ThemeStaticResourceRelPath = "SharedResources/BaseThemes/" + BaseThemeName + ".json";

    // -------------------------------------------------------------------- theme library (selectable palettes)
    //
    // Power BI renders colours from the REGISTERED base theme part (Report/StaticResources/.../BaseThemes/<name>
    // .json) and ignores an inline themeCollection.customTheme. So a palette is applied by MERGING its colours
    // INTO the SuperBiBase base theme JSON and embedding THAT as the active base theme part (named
    // SuperBiBase-<Palette> so it is unambiguous), referenced from themeCollection.baseTheme. Only the colour
    // keys are overridden; every structural/visualStyles property of SuperBiBase is preserved (that is what
    // makes the report render). "Default" embeds the plain SuperBiBase byte-for-byte (the unchanged base look).

    /// <summary>One palette in the library: a stable key, a display name, and its primary colour (for a swatch).
    /// The default carries no resource (it IS the bare SuperBiBase base theme).</summary>
    public sealed record ThemeEntry(string Key, string DisplayName, string PrimaryColor, string? ResourceName);

    /// <summary>A resolved active base theme: the theme NAME (e.g. SuperBiBase or SuperBiBase-Ocean), the part
    /// path it is embedded at, and its JSON bytes (plain SuperBiBase for Default, or SuperBiBase colour-merged
    /// with a palette).</summary>
    public sealed record StagedTheme(string ThemeName, string PartName, byte[] Bytes);

    // the top-level colour keys a palette overrides on the base theme (structural keys are left untouched)
    private static readonly string[] PaletteColorKeys =
    {
        "dataColors", "background", "foreground", "tableAccent",
        "good", "neutral", "bad", "maximum", "center", "minimum", "null",
    };

    private static readonly IReadOnlyList<ThemeEntry> Themes = new List<ThemeEntry>
    {
        new("default",  "Default",  "#2E6EA6", null),                  // plain SuperBiBase (blue), no override
        new("ocean",    "Ocean",    "#0B6FA4", "theme-ocean.json"),
        new("graphite", "Graphite", "#3C4858", "theme-graphite.json"),
        new("emerald",  "Emerald",  "#0E8A5F", "theme-emerald.json"),
        new("amber",    "Amber",    "#E08A00", "theme-amber.json"),
        new("violet",   "Violet",   "#6B4EC2", "theme-violet.json"),
        new("maroon",   "Maroon",   "#7B1E2B", "theme-maroon.json"),
    };

    /// <summary>
    /// The theme catalog for a front end: one entry per selectable palette ({ key, name, primaryColor }), in
    /// display order with Default first. Read this like the templates/solutions catalogs and surface it to
    /// a picker; the returned objects serialize to camelCase keys (key/name/primaryColor).
    /// </summary>
    public static IReadOnlyList<object> ThemeCatalog()
        => Themes.Select(t => (object)new { key = t.Key, name = t.DisplayName, primaryColor = t.PrimaryColor }).ToList();

    /// <summary>Resolve a caller-supplied theme key to its library entry; unknown/blank -> Default.</summary>
    private static ThemeEntry ResolveTheme(string? themeKey)
    {
        string k = (themeKey ?? "").Trim().ToLowerInvariant();
        return Themes.FirstOrDefault(t => string.Equals(t.Key, k, StringComparison.OrdinalIgnoreCase)) ?? Themes[0];
    }

    /// <summary>
    /// Resolve the ACTIVE base theme to embed. When <paramref name="brandColor"/> is a valid hex it WINS over
    /// <paramref name="themeKey"/>: a full cohesive palette is generated from that brand colour and merged into
    /// SuperBiBase (named SuperBiBase-Brand). Otherwise: for Default the plain SuperBiBase bytes (unchanged);
    /// for a preset palette key, SuperBiBase colour-merged with that palette (SuperBiBase-&lt;Palette&gt;). Both
    /// <see cref="Build"/> and the Http build paths call this with the SAME args so the embedded part, filename
    /// and the themeCollection.baseTheme reference always agree. Falls back to plain SuperBiBase on any failure.
    /// </summary>
    public static StagedTheme ResolveStagedTheme(string? themeKey, string? brandColor = null)
    {
        byte[] baseBytes = LoadEmbeddedTheme();   // SuperBiBase.json

        // brand colour wins: merge a generated palette object into the base theme
        var brandPalette = NormalizeHex(brandColor) is string hex ? BrandPaletteObject(hex) : null;
        if (brandPalette != null)
            return MergePaletteIntoBase(baseBytes, brandPalette, "Brand") ?? new StagedTheme(BaseThemeName, PartPathFor(BaseThemeName), baseBytes);

        var entry = ResolveTheme(themeKey);
        if (entry.ResourceName == null)   // Default -> plain SuperBiBase, byte-for-byte
            return new StagedTheme(BaseThemeName, PartPathFor(BaseThemeName), baseBytes);

        var palette = LoadPaletteJson(entry.ResourceName);
        if (palette == null) return new StagedTheme(BaseThemeName, PartPathFor(BaseThemeName), baseBytes);
        return MergePaletteIntoBase(baseBytes, palette, entry.DisplayName) ?? new StagedTheme(BaseThemeName, PartPathFor(BaseThemeName), baseBytes);
    }

    /// <summary>Merge a palette colour object into the SuperBiBase base theme (colour keys only; structure kept)
    /// and rename it SuperBiBase-&lt;suffix&gt;. Returns null on parse failure.</summary>
    private static StagedTheme? MergePaletteIntoBase(byte[] baseBytes, JsonObject palette, string suffix)
    {
        try
        {
            var baseTheme = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(baseBytes)) as JsonObject;
            if (baseTheme == null) return null;
            foreach (var key in PaletteColorKeys)
                if (palette[key] is JsonNode v)
                    baseTheme[key] = v.DeepClone();
            string themeName = BaseThemeName + "-" + suffix;
            baseTheme["name"] = themeName;
            byte[] merged = System.Text.Encoding.UTF8.GetBytes(baseTheme.ToJsonString(JsonOpts));
            return new StagedTheme(themeName, PartPathFor(themeName), merged);
        }
        catch { return null; }
    }

    // -------------------------------------------------------------------- brand colour -> palette

    /// <summary>
    /// Generate a cohesive ~10-colour data palette FROM a single brand hex (white-label branding). palette[0] is
    /// the brand colour exactly; the rest are harmonious tints/shades produced by varying HSL lightness (and a
    /// small hue rotation) around the brand colour, so multi-series charts read as one brand family. Returns the
    /// same kind of list the preset theme dataColors use. Invalid hex -> a single safe colour.
    /// </summary>
    public static IReadOnlyList<string> PaletteFromBrandColor(string? brandHex)
    {
        string? hex = NormalizeHex(brandHex);
        if (hex == null) return new[] { "#2E6EA6" };
        var (h, s, l) = HexToHsl(hex);
        var ramp = new List<string> { hex };   // [0] = the exact brand colour
        // a spread of lightnesses around the brand, with a gentle hue rotation, for distinct but harmonious series
        double[] lights = { l, Clamp01(l - 0.18), Clamp01(l + 0.18), Clamp01(l - 0.32), Clamp01(l + 0.30),
                            Clamp01(l - 0.10), Clamp01(l + 0.10), Clamp01(l - 0.24), Clamp01(l + 0.22), Clamp01(l - 0.40) };
        for (int i = 1; i < lights.Length; i++)
        {
            double hh = (h + i * 8.0) % 360.0;                 // small hue steps keep them related, not identical
            double ss = Math.Clamp(s * (i % 2 == 0 ? 0.92 : 1.0), 0.25, 1.0);
            ramp.Add(HslToHex(hh, ss, lights[i]));
        }
        return ramp;
    }

    /// <summary>The full theme palette OBJECT for a brand colour (dataColors + accent colours), in the same shape
    /// the preset theme-*.json files use, so it merges into the base theme exactly like a preset palette.</summary>
    private static JsonObject BrandPaletteObject(string brandHex)
    {
        var ramp = PaletteFromBrandColor(brandHex);
        var (h, s, l) = HexToHsl(brandHex);
        string dark = HslToHex(h, Math.Clamp(s, 0.2, 1.0), Clamp01(Math.Min(l, 0.22)));   // a dark shade for foreground
        string light = HslToHex(h, Math.Clamp(s * 0.5, 0.1, 1.0), Clamp01(Math.Max(l, 0.94)));
        var arr = new JsonArray(); foreach (var c in ramp) arr.Add(c);
        return new JsonObject
        {
            ["dataColors"] = arr,
            ["background"] = "#FFFFFF",
            ["foreground"] = dark,
            ["tableAccent"] = brandHex,
            ["good"] = "#1AAB40",
            ["neutral"] = ramp.Count > 2 ? ramp[2] : brandHex,
            ["bad"] = "#D64550",
            ["maximum"] = brandHex,
            ["center"] = ramp.Count > 1 ? ramp[1] : brandHex,
            ["minimum"] = light,
            ["null"] = "#CBD5DC",
        };
    }

    /// <summary>Normalise a hex colour to "#RRGGBB" upper-case, or null when not a valid 3/6-digit hex.</summary>
    private static string? NormalizeHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        string h = s.Trim().TrimStart('#');
        if (h.Length == 3) h = string.Concat(h[0], h[0], h[1], h[1], h[2], h[2]);
        if (h.Length != 6) return null;
        foreach (char c in h) if (!Uri.IsHexDigit(c)) return null;
        return "#" + h.ToUpperInvariant();
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0.06, 0.95);

    private static (double h, double s, double l) HexToHsl(string hex)
    {
        string h = hex.TrimStart('#');
        double r = Convert.ToInt32(h.Substring(0, 2), 16) / 255.0;
        double g = Convert.ToInt32(h.Substring(2, 2), 16) / 255.0;
        double b = Convert.ToInt32(h.Substring(4, 2), 16) / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2.0, s = 0, hue = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) hue = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) hue = (b - r) / d + 2;
            else hue = (r - g) / d + 4;
            hue *= 60;
        }
        return (hue, s, l);
    }

    private static string HslToHex(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360; s = Math.Clamp(s, 0, 1); l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60.0) % 2 - 1));
        double m = l - c / 2.0;
        double r1 = 0, g1 = 0, b1 = 0;
        if (h < 60) { r1 = c; g1 = x; }
        else if (h < 120) { r1 = x; g1 = c; }
        else if (h < 180) { g1 = c; b1 = x; }
        else if (h < 240) { g1 = x; b1 = c; }
        else if (h < 300) { r1 = x; b1 = c; }
        else { r1 = c; b1 = x; }
        int R = (int)Math.Round((r1 + m) * 255), G = (int)Math.Round((g1 + m) * 255), B = (int)Math.Round((b1 + m) * 255);
        return $"#{Math.Clamp(R,0,255):X2}{Math.Clamp(G,0,255):X2}{Math.Clamp(B,0,255):X2}";
    }

    /// <summary>The dataColors of a resolved (staged) theme - the palette applied EXPLICITLY to every chart.
    /// Read from the merged theme bytes so it always matches the embedded theme (Default = SuperBiBase dataColors).
    /// Falls back to a single safe colour if the theme cannot be parsed.</summary>
    private static IReadOnlyList<string> PaletteColorsOf(StagedTheme staged)
    {
        try
        {
            var theme = JsonNode.Parse(System.Text.Encoding.UTF8.GetString(staged.Bytes)) as JsonObject;
            if (theme?["dataColors"] is JsonArray arr)
            {
                var list = arr.Select(n => (string?)n).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList();
                if (list.Count > 0) return list;
            }
        }
        catch { }
        return new[] { "#2E6EA6" };
    }

    /// <summary>The pbix part path for a given base theme name.</summary>
    private static string PartPathFor(string themeName) =>
        "Report/StaticResources/SharedResources/BaseThemes/" + themeName + ".json";

    /// <summary>Parse a palette resource (theme-&lt;key&gt;.json) into its colour object, or null.</summary>
    private static JsonObject? LoadPaletteJson(string resourceName)
    {
        try
        {
            var asm = typeof(ReportLayoutBuilder).Assembly;
            string? resName = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceName, StringComparison.OrdinalIgnoreCase));
            if (resName == null) return null;
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null) return null;
            using var sr = new StreamReader(stream);
            return JsonNode.Parse(sr.ReadToEnd()) as JsonObject;
        }
        catch { return null; }
    }

    // a parsed column off a spec table
    private sealed record SpecColumn(string Name, string DataType);
    // a parsed measure off a spec table
    private sealed record SpecMeasure(string Table, string Name);
    // a parsed table off the spec (dateTable flag carried for date detection)
    private sealed record SpecTable(string Name, List<SpecColumn> Columns, bool IsDateTable);
    // a parsed relationship (names only)
    private sealed record SpecRel(string FromTable, string FromColumn, string ToTable, string ToColumn);
    // a detected dimension: its table + the most descriptive display column
    private sealed record Dimension(string Table, string Column);

    /// <summary>The report depth a tier unlocks.</summary>
    private enum Depth { Teaser, Standard, Premium }

    /// <summary>
    /// Map a tier string to the report shape: depth (page-set) and whether the tier carries the "validation"
    /// entitlement (adds a Data Quality page). Unknown tiers fall back to Standard (a real multi-page report)
    /// so an unrecognised value is never crippled; the literal "free" tier is the single-page teaser.
    /// </summary>
    private static (Depth depth, bool validation) TierShape(string? tier)
    {
        string t = (tier ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "free"                  => (Depth.Teaser,   false),
            "personal"              => (Depth.Standard, false),
            "personal_validation"   => (Depth.Standard, true),
            "pro"                   => (Depth.Premium,  true),
            "agency"                => (Depth.Premium,  false),
            "agency_validation"     => (Depth.Premium,  true),
            "dev"                   => (Depth.Premium,  true),   // dev/test sees the richest report
            _                       => (Depth.Standard, false),
        };
    }

    /// <summary>
    /// Build the legacy <c>Report/Layout</c> JSON STRING bound to the spec, with depth scaled to <paramref
    /// name="tier"/>: "free" -> a single teaser page; "personal*" -> standard multi-page;
    /// "pro"/"agency*" -> premium (delta/sparkline cards, share + ranked tables, Growth and Variance, a cross-
    /// dimension matrix, and a Data Quality page for +validation tiers). Returns a blank-but-valid one-page
    /// Layout when the spec has nothing to chart, so the caller always gets an openable report. The SuperBiBase
    /// theme + resourcePackages are always present.
    /// </summary>
    public static string Build(string specJson, string tier, string? themeKey = null, string? title = null,
        string? brandColor = null, string? logoBase64 = null, IReadOnlyList<string>? pages = null)
    {
        JsonObject? spec = null;
        try { spec = JsonNode.Parse(specJson) as JsonObject; } catch { /* fall through to empty */ }

        var (depth, validation) = TierShape(tier);
        // brandColor (white-label) WINS over the preset themeKey; both go through the same explicit-colour pipeline
        var staged = ResolveStagedTheme(themeKey, brandColor);
        var palette = PaletteColorsOf(staged);       // the dataColors to apply EXPLICITLY to every chart
        string? titleText = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        // the brand logo (paid only) - a RegisteredResources image placed in every paid banner, top-right
        var logo = depth != Depth.Teaser ? ResolveLogoPart(logoBase64) : null;
        bool registeredResources = false;   // add the RegisteredResources package once, only if a logo is used

        var (tables, measures, rels) = ParseSpec(spec);
        var dateCol = DetectDateColumn(tables, rels);
        var dimensions = DetectDimensions(tables, rels);
        var deltas = DetectDeltaMeasures(measures);   // TY/LY/Growth% style sets for premium cards

        // ---- assemble the pages ----
        var sections = new JsonArray();
        int sectionIndex = 0;

        void AddPage(string displayName, JsonArray containers)
        {
            // explicit palette colours on every chart (so the palette renders regardless of theme resolution)
            ApplyPaletteColors(containers, palette);
            // additive phone (mobile) layout for every page - desktop layout (id 0) is untouched
            InjectPhoneLayout(containers);
            sections.Add(new JsonObject
            {
                ["id"] = sectionIndex,
                ["name"] = $"ReportSection{sectionIndex + 1}",
                ["displayName"] = displayName,
                ["filters"] = "[]",
                ["ordinal"] = sectionIndex,
                ["visualContainers"] = containers,
                ["config"] = "{}",
                ["displayOption"] = 1,
                ["width"] = PageWidth,
                ["height"] = PageHeight,
            });
            sectionIndex++;
        }

        // The PAID header (banner + a smart slicer strip) is built into each page FIRST; it returns the y where
        // content starts so each page builder sizes its body to the remaining height (no overlap). The Overview
        // banner shows the report title; every other page shows its display name. Free teaser: no header.
        //   slicerDims = the dimension slicers a page shows (date is always added when present); a page excludes
        //   its OWN breakdown dimension. matrix = date only; Data Quality = banner only (no slicers).
        double StartPage(JsonArray containers, string displayName, IReadOnlyList<Dimension> slicerDims, bool withSlicers)
        {
            if (depth == Depth.Teaser) return Margin;   // free teaser has no banner/slicer header
            string bannerTitle = displayName == "Overview" && !string.IsNullOrWhiteSpace(titleText) ? titleText! : displayName;
            if (logo != null) registeredResources = true;   // a logo is placed -> register its package once
            return AddPageHeader(containers, bannerTitle, palette[0], dateCol, slicerDims, withSlicers, logo);
        }

        // ---- the CANDIDATE page set, each tagged with a STABLE key and a lazy emitter. The order here is the
        //      canonical default order; the requested `pages` selection filters + reorders these keys. Every
        //      emitter builds its own page (header + content) and AddPage's it; a page whose content is empty is
        //      not emitted. Overview is always first/always available so the report is never empty.
        var candidates = new List<(string Key, Action Emit)>();

        candidates.Add(("overview", () =>
        {
            var c = new JsonArray();
            double top = StartPage(c, "Overview", dimensions.Take(2).ToList(), withSlicers: true);
            BuildOverviewPage(c, tables, measures, dimensions, dateCol, depth, deltas, titleText, top);
            AddPage("Overview", c);
        }));

        if (depth != Depth.Teaser)
        {
            // one By-<Dimension> page per detected dimension (up to 3); key = "by:<DimColumn>"
            foreach (var dim in dimensions.Take(3))
            {
                var d = dim;   // capture
                candidates.Add(($"by:{d.Column}", () =>
                {
                    var c = new JsonArray();
                    var otherDims = dimensions.Where(x => !(x.Table == d.Table && x.Column == d.Column)).Take(2).ToList();
                    double top = StartPage(c, $"By {d.Column}", otherDims, withSlicers: true);
                    if (BuildDimensionPage(c, measures, d, depth, top) > 0) AddPage($"By {d.Column}", c);
                }));
            }

            if (dateCol != null && measures.Count > 0)
                candidates.Add(("trend", () =>
                {
                    var c = new JsonArray();
                    double top = StartPage(c, "Trend", dimensions.Take(2).ToList(), withSlicers: true);
                    if (BuildTrendPage(c, measures, dateCol.Value, top) > 0) AddPage("Trend", c);
                }));

            if (depth == Depth.Premium)
            {
                candidates.Add(("growth-variance", () =>
                {
                    var c = new JsonArray();
                    double top = StartPage(c, "Growth & Variance", dimensions.Take(2).ToList(), withSlicers: true);
                    if (BuildGrowthVariancePage(c, measures, dimensions, deltas, top) > 0) AddPage("Growth & Variance", c);
                }));

                if (dimensions.Count >= 2 && measures.Count > 0)
                    candidates.Add(("matrix", () =>
                    {
                        var c = new JsonArray();
                        string mTitle = $"{dimensions[0].Column} x {dimensions[1].Column}";
                        double top = StartPage(c, mTitle, new List<Dimension>(), withSlicers: true);
                        if (BuildMatrixPage(c, measures, dimensions[0], dimensions[1], top) > 0) AddPage(mTitle, c);
                    }));
            }

            candidates.Add(("detail", () =>
            {
                var c = new JsonArray();
                double top = StartPage(c, "Detail", dimensions.Take(2).ToList(), withSlicers: true);
                if (BuildDetailPage(c, measures, dimensions, dateCol, top) > 0) AddPage("Detail", c);
            }));

            if (validation)
                candidates.Add(("data-quality", () =>
                {
                    var c = new JsonArray();
                    double top = StartPage(c, "Data Quality", new List<Dimension>(), withSlicers: false);
                    if (BuildDataQualityPage(c, tables, measures, top) > 0) AddPage("Data Quality", c);
                }));
        }

        // ---- select which candidate pages to emit, in which order ----
        var byKey = candidates.ToDictionary(p => p.Key, p => p.Emit, StringComparer.OrdinalIgnoreCase);
        IEnumerable<string> order;
        if (pages != null)
        {
            // the user-chosen selection: keep only keys available at this depth, in the requested order, deduped;
            // ALWAYS keep Overview first (so the report is never empty / a teaser-depth build keeps its one page).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var chosen = new List<string> { "overview" }; seen.Add("overview");
            foreach (var k in pages)
            {
                if (string.IsNullOrWhiteSpace(k)) continue;
                string key = k.Trim();
                if (byKey.ContainsKey(key) && seen.Add(key)) chosen.Add(key);   // unknown / over-tier keys are skipped
            }
            order = chosen;
        }
        else
        {
            order = candidates.Select(p => p.Key);   // default: all candidate pages in canonical order
        }
        foreach (var key in order)
            if (byKey.TryGetValue(key, out var emit)) emit();

        // register the ACTIVE base theme as a SharedResources package (type 2). The renderer resolves the theme -
        // and thus the palette - from this part; the matching merged .json is folded into the pbix.
        var resourcePackages = new JsonArray
        {
            new JsonObject
            {
                ["resourcePackage"] = new JsonObject
                {
                    ["name"] = "SharedResources",
                    ["type"] = 2,
                    ["items"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = 202,   // BaseTheme item type
                            ["path"] = "BaseThemes/" + staged.ThemeName + ".json",
                            ["name"] = staged.ThemeName,
                        },
                    },
                    ["disabled"] = false,
                },
            },
        };
        // when a brand logo was placed, register its RegisteredResources image part (type 1 package, type 100 item)
        if (registeredResources && logo != null)
            resourcePackages.Add(new JsonObject
            {
                ["resourcePackage"] = new JsonObject
                {
                    ["name"] = "RegisteredResources",
                    ["type"] = 1,
                    ["items"] = new JsonArray { new JsonObject { ["type"] = 100, ["path"] = logo.ResName, ["name"] = logo.ResName } },
                    ["disabled"] = false,
                },
            });

        var root = new JsonObject
        {
            ["id"] = 0,
            ["resourcePackages"] = resourcePackages,
            ["sections"] = sections,
            ["config"] = BuildReportConfig(staged.ThemeName).ToJsonString(JsonOpts),   // nested-as-escaped-string
            ["layoutOptimization"] = 0,
        };

        return root.ToJsonString(JsonOpts);
    }

    // -------------------------------------------------------------------- introspection (wizard support)

    /// <summary>One page the generator WOULD produce, for the wizard to show/toggle: a stable key, its display
    /// name, a short description, and whether it is in the default set for the tier (today: all available pages).</summary>
    public sealed record PageInfo(string Key, string Name, string Description, bool IncludedByDefault);

    /// <summary>
    /// The ordered page PLAN the generator would produce for a spec at a tier - the exact stable keys + names
    /// /build accepts, computed from the SAME detection + tier logic Build uses (no report is built). The wizard
    /// renders this as the toggleable page list; the keys round-trip into Build's <c>pages</c> selection.
    /// </summary>
    public static IReadOnlyList<PageInfo> PlanPages(string specJson, string tier)
    {
        JsonObject? spec = null;
        try { spec = JsonNode.Parse(specJson) as JsonObject; } catch { }
        var (depth, validation) = TierShape(tier);
        var (tables, measures, rels) = ParseSpec(spec);
        var dateCol = DetectDateColumn(tables, rels);
        var dimensions = DetectDimensions(tables, rels);

        var plan = new List<PageInfo>
        {
            new("overview", "Overview", "KPI cards, a hero time-series and the primary breakdown.", true),
        };
        if (depth == Depth.Teaser) return plan;   // free teaser: overview only

        foreach (var dim in dimensions.Take(3))
            plan.Add(new($"by:{dim.Column}", $"By {dim.Column}",
                $"Breakdown of the headline measure by {dim.Column} (ranked chart + table).", true));

        if (dateCol != null && measures.Count > 0)
            plan.Add(new("trend", "Trend", "The measures over time by the date column.", true));

        if (depth == Depth.Premium)
        {
            plan.Add(new("growth-variance", "Growth & Variance", "Growth by dimension and the biggest movers.", true));
            if (dimensions.Count >= 2 && measures.Count > 0)
                plan.Add(new("matrix", $"{dimensions[0].Column} x {dimensions[1].Column}",
                    "A cross-dimension matrix of the headline measure.", true));
        }

        plan.Add(new("detail", "Detail", "A granular table of the dimension, date and measures.", true));

        if (validation)
            plan.Add(new("data-quality", "Data Quality", "Row counts loaded per table (live over the model).", true));

        return plan;
    }

    /// <summary>
    /// READ-ONLY introspection of a model spec for the wizard: the detected tables (+columns/types/isKey),
    /// relationships, measures, the date column, the dimensions, the resolved tier and the default page plan.
    /// Builds NO report. <paramref name="rowCounts"/> (optional, table name -> count) is folded into tables when
    /// the caller can supply it (e.g. from the staged sample CSVs); omitted otherwise.
    /// </summary>
    public static JsonObject Introspect(string specJson, string tier, IReadOnlyDictionary<string, long>? rowCounts = null)
    {
        JsonObject? spec = null;
        try { spec = JsonNode.Parse(specJson) as JsonObject; } catch { }
        var (tables, measures, rels) = ParseSpec(spec);
        var dateCol = DetectDateColumn(tables, rels);
        var dimensions = DetectDimensions(tables, rels);

        var tablesArr = new JsonArray();
        foreach (var t in tables)
        {
            var cols = new JsonArray();
            foreach (var c in t.Columns)
            {
                var co = new JsonObject { ["name"] = c.Name, ["dataType"] = c.DataType };
                if (LooksLikeKey(c.Name)) co["isKey"] = true;
                cols.Add(co);
            }
            var to = new JsonObject { ["name"] = t.Name, ["columns"] = cols };
            if (rowCounts != null && rowCounts.TryGetValue(t.Name, out var rc)) to["rowCount"] = rc;
            tablesArr.Add(to);
        }

        var relsArr = new JsonArray();
        foreach (var r in rels)
            relsArr.Add(new JsonObject { ["fromTable"] = r.FromTable, ["fromColumn"] = r.FromColumn, ["toTable"] = r.ToTable, ["toColumn"] = r.ToColumn });

        var measArr = new JsonArray();
        foreach (var m in measures) measArr.Add(new JsonObject { ["name"] = m.Name, ["table"] = m.Table });

        var dimsArr = new JsonArray();
        foreach (var d in dimensions) dimsArr.Add(new JsonObject { ["table"] = d.Table, ["column"] = d.Column });

        var pagesArr = new JsonArray();
        foreach (var p in PlanPages(specJson, tier))
            pagesArr.Add(new JsonObject { ["key"] = p.Key, ["name"] = p.Name, ["description"] = p.Description, ["includedByDefault"] = p.IncludedByDefault });

        return new JsonObject
        {
            ["tables"] = tablesArr,
            ["relationships"] = relsArr,
            ["measures"] = measArr,
            ["dateColumn"] = dateCol == null ? null : new JsonObject { ["table"] = dateCol.Value.table, ["column"] = dateCol.Value.column },
            ["dimensions"] = dimsArr,
            ["tier"] = tier,
            ["defaultPages"] = pagesArr,
        };
    }

    /// <summary>The report-level config object: points themeCollection.baseTheme at the ACTIVE base theme part
    /// (SuperBiBase or the colour-merged SuperBiBase-&lt;Palette&gt;), which the renderer reads on load (required, or it
    /// throws on themeCurrent.customTheme). No inline customTheme - Power BI ignores that and renders from the
    /// registered base theme part, so the palette must live IN the embedded base theme (see ResolveStagedTheme).</summary>
    private static JsonObject BuildReportConfig(string themeName)
    {
        return new JsonObject
        {
            ["version"] = "5.43",
            ["activeSectionIndex"] = 0,
            ["defaultDrillFilterOtherVisuals"] = true,
            ["themeCollection"] = new JsonObject
            {
                ["baseTheme"] = new JsonObject
                {
                    ["name"] = themeName,
                    ["version"] = "5.43",
                    ["type"] = "SharedResources",
                },
            },
            ["settings"] = new JsonObject { ["useStylableVisualContainerHeader"] = true },
        };
    }

    // -------------------------------------------------------------------- page builders

    /// <summary>Overview content (appended to <paramref name="containers"/>, starting at <paramref name="top"/>):
    /// up to 4 KPI cards, a hero time-series (measure[0] by the date column) and a primary breakdown (measure[0]
    /// by the first dimension, sorted desc). Premium upgrades the cards with a delta vs prior period. The paid
    /// banner + slicer strip are added by the caller (AddPageHeader); the FREE teaser gets a plain inline title
    /// here. Returns the number of content visuals added.</summary>
    private static int BuildOverviewPage(JsonArray containers, List<SpecTable> tables, List<SpecMeasure> measures,
        List<Dimension> dimensions, (string table, string column)? dateCol, Depth depth, List<DeltaSet> deltas,
        string? titleText, double top)
    {
        int n = containers.Count;
        if (tables.Count == 0) return 0;
        double z = 1000;

        // FREE teaser only: a plain title bar across the very top (paid pages get the banner from the caller)
        if (depth == Depth.Teaser && !string.IsNullOrWhiteSpace(titleText))
        {
            const double titleH = 48;
            var bar = Textbox(titleText!, fontSize: 22, colorHex: "#1A1A1A", bold: true,
                transparencyPercent: 0, alignment: "left", verticalAlignment: "middle");
            AddContainer(containers, bar, Margin, top, z++, PageWidth - 2 * Margin, titleH);
            top += titleH + Gap;
        }

        double sliceBottom = top;

        // KPI cards: the first ~4 measures across the top. Premium uses delta/sparkline cards where available.
        var cardMeasures = PickCardMeasures(measures, deltas, depth);
        int cardCount = cardMeasures.Count;
        double cardH = 110, cardY = sliceBottom;
        if (cardCount > 0)
        {
            double cardW = (PageWidth - 2 * Margin - Gap * (cardCount - 1)) / cardCount;
            for (int i = 0; i < cardCount; i++)
            {
                var m = cardMeasures[i];
                JsonObject? card = null;
                if (depth == Depth.Premium)
                {
                    var ds = deltas.FirstOrDefault(d => string.Equals(d.Primary.Name, m.Name, StringComparison.Ordinal)
                                                     && string.Equals(d.Primary.Table, m.Table, StringComparison.Ordinal));
                    if (ds != null) card = BuildKpiCard(ds, dateCol);
                }
                card ??= BuildCard(m);
                if (card != null) AddContainer(containers, card, Margin + i * (cardW + Gap), cardY, z++, cardW, cardH);
            }
        }

        double bodyY = cardCount > 0 ? cardY + cardH + Margin : sliceBottom;
        double bodyH = PageHeight - bodyY - Margin;

        var firstMeasure = measures.FirstOrDefault();
        var firstDim = dimensions.FirstOrDefault();

        // hero time-series on the left (or full width when there is no breakdown)
        bool haveTrend = firstMeasure != null && dateCol != null;
        bool haveBreakdown = firstMeasure != null && firstDim != null;
        double halfW = (PageWidth - 2 * Margin - Gap) / 2;

        double x = Margin;
        if (haveTrend)
        {
            var trend = BuildLineChart(new[] { firstMeasure! }, dateCol!.Value.table, dateCol.Value.column, area: true);
            if (trend != null) { AddContainer(containers, trend, x, bodyY, z++, haveBreakdown ? halfW : PageWidth - 2 * Margin, bodyH); x += halfW + Gap; }
        }
        if (haveBreakdown)
        {
            var chart = BuildCategoryChart("clusteredColumnChart", firstMeasure!, firstDim!.Table, firstDim.Column);
            if (chart != null) AddContainer(containers, chart, haveTrend ? x : Margin, bodyY, z++, haveTrend ? halfW : PageWidth - 2 * Margin, bodyH);
        }

        // if neither a trend nor a breakdown was possible, fall back to a detail table so the page is not empty
        if (!haveTrend && !haveBreakdown)
        {
            var detail = BuildTable(tables, measures, null, null);
            if (detail != null) AddContainer(containers, detail, Margin, bodyY, z++, PageWidth - 2 * Margin, bodyH);
        }
        return containers.Count - n;
    }

    /// <summary>
    /// Add a slicer strip across a page at vertical offset <paramref name="top"/>: a date slicer (when a date
    /// column exists) plus up to two dimension slicers (the primary dimension display columns). Returns the strip
    /// height (0 when no slicer could be built). Each slicer is skipped if its column is missing. Paid pages only.
    /// </summary>
    private static double AddSlicerStrip(JsonArray containers, (string table, string column)? dateCol,
        IReadOnlyList<Dimension> dimensions, ref double z, double top)
    {
        var slicerFields = new List<(string table, string column)>();
        if (dateCol != null) slicerFields.Add((dateCol.Value.table, dateCol.Value.column));
        foreach (var d in dimensions) slicerFields.Add((d.Table, d.Column));   // caller decides which dims
        if (slicerFields.Count == 0) return 0;

        const double sliceH = 100;   // tall enough for a dropdown slicer's title row + collapsed control (72 clipped it)
        double sliceW = (PageWidth - 2 * Margin - Gap * (slicerFields.Count - 1)) / slicerFields.Count;
        int placed = 0;
        for (int i = 0; i < slicerFields.Count; i++)
        {
            var (table, column) = slicerFields[i];
            var slicer = BuildSlicer(table, column, $"{table}.{column}");
            if (slicer == null) continue;
            AddContainer(containers, slicer, Margin + i * (sliceW + Gap), top, z++, sliceW, sliceH);
            placed++;
        }
        return placed > 0 ? sliceH : 0;
    }

    /// <summary>A "By &lt;Dimension&gt;" page. Standard: a sorted column chart (measure[0] by the dim) + a tableEx
    /// (dim + first ~3 measures). Premium adds a share/contribution donut and a ranked top/bottom table, in a
    /// 2x2 layout, so the page reads like a real category review.</summary>
    private static int BuildDimensionPage(JsonArray containers, List<SpecMeasure> measures, Dimension dim, Depth depth, double top)
    {
        int n = containers.Count;
        var firstMeasure = measures.FirstOrDefault();
        if (firstMeasure == null) return 0;
        double z = 1000;
        double halfW = (PageWidth - 2 * Margin - Gap) / 2;

        if (depth != Depth.Premium)
        {
            double bodyY = top, bodyH = PageHeight - top - Margin;
            var chart = BuildCategoryChart("clusteredColumnChart", firstMeasure, dim.Table, dim.Column);
            if (chart != null) AddContainer(containers, chart, Margin, bodyY, z++, halfW, bodyH);
            var table = BuildDimensionTable(dim, measures.Take(3).ToList());
            if (table != null) AddContainer(containers, table, Margin + halfW + Gap, bodyY, z++, halfW, bodyH);
            return containers.Count - n;
        }

        // premium 2x2: ranked column (TL), share donut (TR), detail table (BL), ranked top/bottom table (BR)
        double rowH = (PageHeight - top - Margin - Gap) / 2;
        double topY = top, botY = top + rowH + Gap;

        var ranked = BuildCategoryChart("clusteredColumnChart", firstMeasure, dim.Table, dim.Column);
        if (ranked != null) AddContainer(containers, ranked, Margin, topY, z++, halfW, rowH);

        var share = BuildShareChart(firstMeasure, dim.Table, dim.Column);
        if (share != null) AddContainer(containers, share, Margin + halfW + Gap, topY, z++, halfW, rowH);

        var detail = BuildDimensionTable(dim, measures.Take(3).ToList());
        if (detail != null) AddContainer(containers, detail, Margin, botY, z++, halfW, rowH);

        var ranking = BuildRankedTable(dim, firstMeasure, measures.Take(3).ToList());
        if (ranking != null) AddContainer(containers, ranking, Margin + halfW + Gap, botY, z++, halfW, rowH);

        return containers.Count - n;
    }

    /// <summary>Trend page: a multi-measure line chart by the date column (top) + a tableEx of date + measures
    /// (bottom).</summary>
    private static int BuildTrendPage(JsonArray containers, List<SpecMeasure> measures, (string table, string column) dateCol, double top)
    {
        int n = containers.Count;
        double z = 1000;
        var lineMeasures = measures.Take(3).ToList();

        double avail = PageHeight - top - Margin - Gap;
        double topH = avail * 0.6;
        double botH = avail - topH;
        double w = PageWidth - 2 * Margin;

        var line = BuildLineChart(lineMeasures, dateCol.table, dateCol.column, area: false);
        if (line != null) AddContainer(containers, line, Margin, top, z++, w, topH);

        var table = BuildPeriodTable(dateCol, measures.Take(4).ToList());
        if (table != null) AddContainer(containers, table, Margin, top + topH + Gap, z++, w, botH);

        return containers.Count - n;
    }

    /// <summary>Detail page: a granular tableEx with the primary dimension column, the date/period column and the
    /// measures - the "show me the numbers" view.</summary>
    private static int BuildDetailPage(JsonArray containers, List<SpecMeasure> measures, List<Dimension> dimensions,
        (string table, string column)? dateCol, double top)
    {
        int n = containers.Count;
        if (measures.Count == 0 && dimensions.Count == 0) return 0;
        double z = 1000;

        var table = BuildGranularTable(dimensions.FirstOrDefault(), dateCol, measures.Take(5).ToList());
        if (table != null) AddContainer(containers, table, Margin, top, z++, PageWidth - 2 * Margin, PageHeight - top - Margin);
        return containers.Count - n;
    }

    // -------------------------------------------------------------------- premium page builders

    /// <summary>Growth and Variance (premium): a growth measure (e.g. "Revenue Growth %") by the primary
    /// dimension sorted desc (top gainers / losers fall out of the sort), plus a ranked table of the biggest
    /// movers (the same growth measure + the underlying TY/LY measures). Falls back to the primary measure when
    /// the model declares no growth measure, so the page is still meaningful.</summary>
    private static int BuildGrowthVariancePage(JsonArray containers, List<SpecMeasure> measures, List<Dimension> dimensions, List<DeltaSet> deltas, double top)
    {
        int n = containers.Count;
        var dim = dimensions.FirstOrDefault();
        if (dim == null) return 0;

        // prefer a growth/change measure; else the first delta primary; else the first measure
        var growth = measures.FirstOrDefault(m => IsGrowthMeasure(m.Name))
                     ?? deltas.FirstOrDefault()?.Primary
                     ?? measures.FirstOrDefault();
        if (growth == null) return 0;

        double z = 1000;
        double halfW = (PageWidth - 2 * Margin - Gap) / 2;
        double bodyH = PageHeight - top - Margin;

        // movers chart: growth by dim, sorted desc (the gainers lead, the losers trail)
        var chart = BuildCategoryChart("clusteredBarChart", growth, dim.Table, dim.Column);
        if (chart != null) AddContainer(containers, chart, Margin, top, z++, halfW, bodyH);

        // movers table: the dim + the growth measure + the supporting TY/LY measures when present
        var tableMeasures = new List<SpecMeasure> { growth };
        var ds = deltas.FirstOrDefault();
        if (ds != null)
        {
            if (ds.Current != null && !tableMeasures.Any(x => x.Name == ds.Current.Name)) tableMeasures.Add(ds.Current);
            if (ds.Prior != null && !tableMeasures.Any(x => x.Name == ds.Prior.Name)) tableMeasures.Add(ds.Prior);
        }
        var table = BuildRankedTable(dim, growth, tableMeasures);
        if (table != null) AddContainer(containers, table, Margin + halfW + Gap, top, z++, halfW, bodyH);

        return containers.Count - n;
    }

    /// <summary>Cross-dimension matrix (premium): measure[0] with dimension A on Rows and dimension B on Columns
    /// - the pivot that shows where the value concentrates across two cuts at once.</summary>
    private static int BuildMatrixPage(JsonArray containers, List<SpecMeasure> measures, Dimension rowDim, Dimension colDim, double top)
    {
        int n = containers.Count;
        var measure = measures.FirstOrDefault();
        if (measure == null) return 0;
        double z = 1000;
        var matrix = BuildMatrix(measure, rowDim, colDim);
        if (matrix != null) AddContainer(containers, matrix, Margin, top, z++, PageWidth - 2 * Margin, PageHeight - top - Margin);
        return containers.Count - n;
    }

    /// <summary>Data Quality (+validation tiers): a real completeness view - row counts per table via
    /// COUNTROWS measures expressed inline, presented as a table. No fabricated numbers: every value is a live
    /// COUNTROWS over the model's own tables, so it reflects exactly what landed.</summary>
    private static int BuildDataQualityPage(JsonArray containers, List<SpecTable> tables, List<SpecMeasure> measures, double top)
    {
        int n = containers.Count;
        // a table is "real" if it has columns; count rows for each via an inline COUNTROWS in a single-row table.
        var realTables = tables.Where(t => t.Columns.Count > 0).ToList();
        if (realTables.Count == 0) return 0;
        double z = 1000;

        // We cannot author new measures into the model from here, so present row counts using a tableEx bound to
        // the existing measures the model DOES expose, plus a textbox explaining the completeness view. To keep
        // it real and generic, show a per-table card grid of COUNTROWS via the report-level aggregation: a
        // tableEx of the primary fact table's key column count is the honest, model-only signal available.
        // Simplest real signal with no fabricated numbers: a tableEx listing each table and its row count using
        // the visual-level Count aggregation over a column of that table.
        double colW = (PageWidth - 2 * Margin - Gap * (Math.Min(realTables.Count, 4) - 1)) / Math.Min(realTables.Count, 4);
        double cardH = 120;
        int shown = 0;
        foreach (var t in realTables.Take(4))
        {
            var anyCol = t.Columns.FirstOrDefault();
            if (anyCol == null) continue;
            var card = BuildCountCard(t.Name, anyCol.Name);
            if (card != null) { AddContainer(containers, card, Margin + shown * (colW + Gap), top, z++, colW, cardH); shown++; }
        }
        if (shown == 0) return 0;

        // an explanatory note so the page reads as a deliberate QA view, not an empty grid
        var note = Textbox("Data Quality - row counts loaded per table (live COUNTROWS over the model).",
            fontSize: 12, colorHex: "#444444", bold: false, transparencyPercent: 0, alignment: "left", verticalAlignment: "middle");
        AddContainer(containers, note, Margin, top + cardH + Gap, z++, PageWidth - 2 * Margin, 30);

        return containers.Count - n;
    }

    // -------------------------------------------------------------------- generic model detection

    /// <summary>
    /// The date/time column to plot trends against. Order: a table flagged dateTable=true with a date column;
    /// else a relationship "one"-side table named like a calendar with a date column; else any date/datetime
    /// column anywhere; else a column NAMED like a date (Date/WeekEnding/MonthEnding/Period/Week/Month/Year).
    /// </summary>
    private static (string table, string column)? DetectDateColumn(List<SpecTable> tables, List<SpecRel> rels)
    {
        // 1) an explicit date table with a real date column
        foreach (var t in tables.Where(t => t.IsDateTable))
        {
            var c = t.Columns.FirstOrDefault(c => IsDateType(c.DataType));
            if (c != null) return (t.Name, c.Name);
        }
        // 2) a "one"-side dimension table whose name reads like a calendar, with a date column
        var dimTables = rels.Select(r => r.ToTable).Distinct(StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tables.Where(t => dimTables.Contains(t.Name) && LooksLikeCalendar(t.Name)))
        {
            var c = t.Columns.FirstOrDefault(c => IsDateType(c.DataType)) ?? t.Columns.FirstOrDefault(c => LooksLikeDate(c.Name));
            if (c != null) return (t.Name, c.Name);
        }
        // 3) any real date/datetime column anywhere
        foreach (var t in tables)
        {
            var c = t.Columns.FirstOrDefault(c => IsDateType(c.DataType));
            if (c != null) return (t.Name, c.Name);
        }
        // 4) a column merely NAMED like a date (last resort)
        foreach (var t in tables)
        {
            var c = t.Columns.FirstOrDefault(c => LooksLikeDate(c.Name));
            if (c != null) return (t.Name, c.Name);
        }
        return null;
    }

    /// <summary>
    /// The dimensions to break the measures down by: the "one"-side tables of relationships (the lookup tables),
    /// each represented by its most descriptive non-key TEXT column. Calendar-like date tables are excluded (they
    /// drive the Trend page, not a categorical breakdown). Falls back to text columns on any table when the spec
    /// declares no relationships. De-duplicated by (table, column), ordered as the relationships appear.
    /// </summary>
    private static List<Dimension> DetectDimensions(List<SpecTable> tables, List<SpecRel> rels)
    {
        var byName = tables.ToDictionary(t => t.Name, t => t, StringComparer.OrdinalIgnoreCase);
        var dims = new List<Dimension>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // primary: the lookup ("one") side of each relationship, in declaration order
        foreach (var r in rels)
        {
            if (!byName.TryGetValue(r.ToTable, out var t)) continue;
            if (LooksLikeCalendar(t.Name)) continue;                 // the date table is not a categorical dim
            var col = BestDisplayColumn(t);
            if (col == null) continue;
            string key = t.Name + "|" + col;
            if (seen.Add(key)) dims.Add(new Dimension(t.Name, col));
        }

        // fallback: if no relationships gave us a dimension, take descriptive text columns off any table
        if (dims.Count == 0)
        {
            foreach (var t in tables)
            {
                if (LooksLikeCalendar(t.Name)) continue;
                var col = BestDisplayColumn(t);
                if (col == null) continue;
                string key = t.Name + "|" + col;
                if (seen.Add(key)) dims.Add(new Dimension(t.Name, col));
            }
        }
        return dims;
    }

    /// <summary>The most descriptive display column of a (dimension) table: the first non-key TEXT column,
    /// preferring a "name/description/title/label"-ish one. Null when the table has no usable text column.</summary>
    private static string? BestDisplayColumn(SpecTable t)
    {
        var textCols = t.Columns.Where(c => IsTextType(c.DataType) && !LooksLikeKey(c.Name)).ToList();
        if (textCols.Count == 0) return null;
        var descriptive = textCols.FirstOrDefault(c => IsDescriptiveName(c.Name));
        return (descriptive ?? textCols[0]).Name;
    }

    private static bool IsDescriptiveName(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return n.Contains("name") || n.Contains("description") || n.Contains("title")
            || n.Contains("label") || n.Contains("brand") || n.Contains("item") || n.Contains("product");
    }

    private static bool LooksLikeCalendar(string tableName)
    {
        string n = tableName.Trim().ToLowerInvariant();
        return n is "calendar" or "date" or "dates" or "dim date" or "dimdate" or "time" || n.Contains("calendar") || n.Contains("date");
    }

    private static bool LooksLikeDate(string columnName)
    {
        string n = columnName.Trim().ToLowerInvariant();
        return n.Contains("date") || n.EndsWith("ending") || n is "period" or "week" or "month" or "year"
            || n.Contains("weekending") || n.Contains("monthending") || n.Contains("week ending") || n.Contains("month ending");
    }

    private static bool IsDateType(string dt) => dt is "datetime" or "date" or "time";

    // -------------------------------------------------------------------- delta / sparkline detection

    /// <summary>A delta set for a premium KPI card: a primary measure, its current+prior period measures (for
    /// the "vs prior" delta) and a growth measure (the % change), each present only when the model declares it.</summary>
    private sealed record DeltaSet(SpecMeasure Primary, SpecMeasure? Current, SpecMeasure? Prior, SpecMeasure? Growth);

    /// <summary>
    /// Detect TY/LY/Growth% style measure families from their NAMES so premium cards can show a delta + change.
    /// For a base name like "Revenue" we look for "Total Revenue"/"Revenue", a current ("TY Revenue"/"This Year"
    /// flavour), a prior ("LY Revenue"/"Last Year"), and a growth ("Revenue Growth %"). Generic across solutions
    /// (business-exec: TY/LY Revenue + Revenue Growth %; retail-fmcg: TY/LY Sales + Sales Growth %). A base with
    /// no current/prior/growth companion is skipped (those get plain cards).
    /// </summary>
    private static List<DeltaSet> DetectDeltaMeasures(List<SpecMeasure> measures)
    {
        var sets = new List<DeltaSet>();
        if (measures.Count == 0) return sets;

        // index by lowercased name for companion lookup
        SpecMeasure? Find(Func<string, bool> pred) => measures.FirstOrDefault(m => pred(m.Name.ToLowerInvariant()));

        // candidate base tokens: the noun in a "Total X" / "X" primary measure (e.g. Revenue, Sales, Units, Orders)
        var bases = new List<(SpecMeasure primary, string token)>();
        foreach (var m in measures)
        {
            string n = m.Name.Trim();
            string low = n.ToLowerInvariant();
            // a primary is "Total X" or a bare noun, but NOT itself a TY/LY/growth/share measure
            if (IsGrowthMeasure(n) || low.StartsWith("ty ") || low.StartsWith("ly ") || low.Contains("share")) continue;
            string token = low.StartsWith("total ") ? n.Substring(6).Trim() : n;
            if (string.IsNullOrWhiteSpace(token)) continue;
            bases.Add((m, token.ToLowerInvariant()));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (primary, token) in bases)
        {
            if (!seen.Add(token)) continue;
            var current = Find(x => (x.StartsWith("ty ") || x.Contains("this year") || x.StartsWith("current")) && x.Contains(token));
            var prior   = Find(x => (x.StartsWith("ly ") || x.Contains("last year") || x.StartsWith("prior")) && x.Contains(token));
            var growth  = Find(x => x.Contains(token) && (x.Contains("growth") || x.Contains("change") || x.Contains(" vs ")) && x.Contains("%")
                                 || (x.Contains(token) && x.Contains("growth")));
            // only a delta set when at least a growth OR a current+prior pair exists
            if (growth != null || (current != null && prior != null))
                sets.Add(new DeltaSet(primary, current, prior, growth));
        }
        return sets;
    }

    private static bool IsGrowthMeasure(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return (n.Contains("growth") || n.Contains("change")) && (n.Contains("%") || n.Contains("pts") || n.Contains("pct"));
    }

    /// <summary>The measures to show as Overview KPI cards. Premium leads with the delta-set primaries (so the
    /// upgraded cards appear first), then fills with other measures up to 4; otherwise the first 4 measures.</summary>
    private static List<SpecMeasure> PickCardMeasures(List<SpecMeasure> measures, List<DeltaSet> deltas, Depth depth)
    {
        if (depth != Depth.Premium || deltas.Count == 0) return measures.Take(4).ToList();
        var ordered = new List<SpecMeasure>();
        foreach (var d in deltas) if (ordered.Count < 4) ordered.Add(d.Primary);
        foreach (var m in measures)
        {
            if (ordered.Count >= 4) break;
            if (!ordered.Any(x => x.Name == m.Name && x.Table == m.Table)) ordered.Add(m);
        }
        return ordered;
    }

    // -------------------------------------------------------------------- spec parsing

    private static (List<SpecTable> tables, List<SpecMeasure> measures, List<SpecRel> rels) ParseSpec(JsonObject? spec)
    {
        var tables = new List<SpecTable>();
        var measures = new List<SpecMeasure>();
        var rels = new List<SpecRel>();
        if (spec?["tables"] is JsonArray tarr)
        {
            foreach (var tn in tarr)
            {
                if (tn is not JsonObject to) continue;
                if ((string?)to["name"] is not string tname || string.IsNullOrWhiteSpace(tname)) continue;

                var cols = new List<SpecColumn>();
                if (to["columns"] is JsonArray carr)
                    foreach (var cn in carr)
                        if (cn is JsonObject co && (string?)co["name"] is string cname && !string.IsNullOrWhiteSpace(cname))
                            cols.Add(new SpecColumn(cname, ((string?)co["dataType"] ?? "string").ToLowerInvariant()));
                bool isDate = (bool?)to["dateTable"] == true;
                tables.Add(new SpecTable(tname, cols, isDate));

                if (to["measures"] is JsonArray marr)
                    foreach (var mn in marr)
                        if (mn is JsonObject mo && (string?)mo["name"] is string mname && !string.IsNullOrWhiteSpace(mname))
                            measures.Add(new SpecMeasure(tname, mname));
            }
        }
        if (spec?["relationships"] is JsonArray rarr)
            foreach (var rn in rarr)
                if (rn is JsonObject ro
                    && (string?)ro["fromTable"] is string ft && (string?)ro["fromColumn"] is string fc
                    && (string?)ro["toTable"] is string tt && (string?)ro["toColumn"] is string tc
                    && !string.IsNullOrWhiteSpace(ft) && !string.IsNullOrWhiteSpace(tt))
                    rels.Add(new SpecRel(ft, fc, tt, tc));
        return (tables, measures, rels);
    }

    private static bool IsTextType(string dt) => dt is "string" or "text" or "";
    private static bool IsNumericType(string dt) =>
        dt is "int64" or "int" or "integer" or "whole" or "double" or "real" or "float" or "number"
           or "decimal" or "currency" or "fixed";

    private static bool LooksLikeKey(string name)
    {
        string n = name.Trim().ToLowerInvariant();
        return n is "id" || n.EndsWith("id") || n.EndsWith("key") || n.EndsWith("_id") || n.EndsWith(" id");
    }

    // -------------------------------------------------------------------- singleVisual builders

    /// <summary>card showing one model measure (role "Values", queryRef "Table.Measure").</summary>
    private static JsonObject? BuildCard(SpecMeasure m)
    {
        string queryRef = $"{m.Table}.{m.Name}";
        var select = new JsonArray { MeasureSelect(m.Table, m.Name, "c") };
        var projections = new JsonObject { ["Values"] = new JsonArray { Proj(queryRef) } };
        return SingleVisual("card", projections, From("c", m.Table), select, m.Name);
    }

    /// <summary>
    /// A legacy "slicer" visual bound to one column (a dimension display column or the date column), rendered as
    /// a DROPDOWN. The column sits in the "Values" role with active:true on the projection (the form Power BI
    /// normalizes a slicer field to); queryRef equals the Select Name. The dropdown is set via the slicer "data"
    /// card's "mode" property = Dropdown - the exact name/encoding ReportService.BuildSingleVisual uses
    /// (objects.data[].properties.mode = Lit("'Dropdown'")) and the value confirmed against the theme schema's
    /// slicer mode enum (VerticalList | HorizontalList | ... | Dropdown). A selection filters the visuals on its
    /// page (native slicer behavior). <paramref name="syncGroup"/> is reserved for future cross-page sync.
    /// </summary>
    private static JsonObject? BuildSlicer(string table, string column, string? syncGroup)
    {
        string queryRef = $"{table}.{column}";
        var select = new JsonArray { ColumnSelect(column, "s", queryRef) };
        var projections = new JsonObject { ["Values"] = new JsonArray { ProjActive(queryRef) } };   // active:true
        _ = syncGroup;   // per-page slicer for now; see summary on cross-page sync
        var sv = SingleVisual("slicer", projections, From("s", table), select, column);
        // dropdown mode: the slicer "data" card, mode = Dropdown (proven encoding from ReportService)
        sv["objects"] = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject { ["properties"] = new JsonObject { ["mode"] = Lit("'Dropdown'") } },
            },
        };
        return sv;
    }

    /// <summary>
    /// A premium KPI card: a multiRowCard showing the primary value alongside its growth and prior-period
    /// measures (the "delta vs prior period"), and - when a date column exists - a tiny line as a sparkline
    /// hint via an extra trend measure. Every queryRef equals a Select Name. Falls back to a plain card upstream
    /// when no delta companion exists.
    /// </summary>
    private static JsonObject? BuildKpiCard(DeltaSet ds, (string table, string column)? dateCol)
    {
        // a multiRowCard renders several measure rows (value + delta + growth) in one tile.
        var (from, aliasOf) = BeginFrom();
        var select = new JsonArray();
        var values = new JsonArray();

        void AddMeasure(SpecMeasure? m)
        {
            if (m == null) return;
            string a = AddFrom(from, aliasOf, m.Table);
            string r = $"{m.Table}.{m.Name}";
            if (values.Any(v => (string?)v?["queryRef"] == r)) return;   // no dup queryRef
            select.Add(MeasureSelect(m.Table, m.Name, a));
            values.Add(Proj(r));
        }
        AddMeasure(ds.Primary);
        AddMeasure(ds.Growth);
        AddMeasure(ds.Current);
        AddMeasure(ds.Prior);
        if (values.Count == 0) return null;

        var projections = new JsonObject { ["Values"] = values };
        return SingleVisual("multiRowCard", projections, from, select, ds.Primary.Name);
    }

    /// <summary>A pieChart (donut-style share/contribution): a measure split by a category column. Generic
    /// cross-table From; the category in the "Category" role, the measure in "Y".</summary>
    private static JsonObject? BuildShareChart(SpecMeasure measure, string catTable, string catColumn)
    {
        string measRef = $"{measure.Table}.{measure.Name}";
        string catRef = $"{catTable}.{catColumn}";

        var from = new JsonArray { FromEntry("m", measure.Table) };
        string catAlias = "m";
        if (!string.Equals(catTable, measure.Table, StringComparison.Ordinal))
        {
            catAlias = "c";
            from.Add(FromEntry("c", catTable));
        }
        var select = new JsonArray
        {
            MeasureSelect(measure.Table, measure.Name, "m"),
            ColumnSelect(catColumn, catAlias, catRef),
        };
        var projections = new JsonObject
        {
            ["Y"] = new JsonArray { Proj(measRef) },
            ["Category"] = new JsonArray { ProjActive(catRef) },
        };
        return WithLegendBottom(SingleVisual("pieChart", projections, from, select, $"{measure.Name} share by {catColumn}"));
    }

    /// <summary>A ranked tableEx: the dimension column + a set of measures, sorted descending by the rank
    /// measure (the "top movers" view). Cross-table aliasing; the sort uses the rank measure's alias.</summary>
    private static JsonObject? BuildRankedTable(Dimension dim, SpecMeasure rankMeasure, List<SpecMeasure> measures)
    {
        var (from, aliasOf) = BeginFrom();
        string dimAlias = AddFrom(from, aliasOf, dim.Table);
        string dimRef = $"{dim.Table}.{dim.Column}";

        var select = new JsonArray { ColumnSelect(dim.Column, dimAlias, dimRef) };
        var values = new JsonArray { Proj(dimRef) };

        // ensure the rank measure is included
        var measureList = new List<SpecMeasure>();
        if (!measures.Any(x => x.Name == rankMeasure.Name && x.Table == rankMeasure.Table)) measureList.Add(rankMeasure);
        measureList.AddRange(measures);

        string rankAlias = "";
        foreach (var m in measureList)
        {
            string a = AddFrom(from, aliasOf, m.Table);
            if (m.Name == rankMeasure.Name && m.Table == rankMeasure.Table) rankAlias = a;
            string r = $"{m.Table}.{m.Name}";
            if (values.Any(v => (string?)v?["queryRef"] == r)) continue;
            select.Add(MeasureSelect(m.Table, m.Name, a));
            values.Add(Proj(r));
        }
        if (values.Count <= 1) return null;

        var projections = new JsonObject { ["Values"] = values };
        var sv = SingleVisual("tableEx", projections, from, select, $"Top {dim.Column} by {rankMeasure.Name}");
        if (!string.IsNullOrEmpty(rankAlias))
            sv["prototypeQuery"]!["OrderBy"] = new JsonArray
            {
                new JsonObject
                {
                    ["Direction"] = 2,
                    ["Expression"] = new JsonObject { ["Measure"] = MeasureExpr(rankMeasure.Table, rankMeasure.Name, rankAlias) },
                },
            };
        return sv;
    }

    /// <summary>A matrix (pivotTable): a measure with one dimension on Rows and another on Columns. Cross-table
    /// From with one alias per distinct table; the role keys are "Rows", "Columns" and "Values".</summary>
    private static JsonObject? BuildMatrix(SpecMeasure measure, Dimension rowDim, Dimension colDim)
    {
        var (from, aliasOf) = BeginFrom();
        string rowAlias = AddFrom(from, aliasOf, rowDim.Table);
        string colAlias = AddFrom(from, aliasOf, colDim.Table);
        string measAlias = AddFrom(from, aliasOf, measure.Table);

        string rowRef = $"{rowDim.Table}.{rowDim.Column}";
        string colRef = $"{colDim.Table}.{colDim.Column}";
        string measRef = $"{measure.Table}.{measure.Name}";

        var select = new JsonArray
        {
            ColumnSelect(rowDim.Column, rowAlias, rowRef),
            ColumnSelect(colDim.Column, colAlias, colRef),
            MeasureSelect(measure.Table, measure.Name, measAlias),
        };
        var projections = new JsonObject
        {
            ["Rows"] = new JsonArray { Proj(rowRef) },
            ["Columns"] = new JsonArray { Proj(colRef) },
            ["Values"] = new JsonArray { Proj(measRef) },
        };
        return SingleVisual("pivotTable", projections, from, select, $"{measure.Name}: {rowDim.Column} x {colDim.Column}");
    }

    /// <summary>A card showing a Count aggregation over a table column (the table's row count - a live, honest
    /// completeness signal for the Data Quality page). The aggregation lives in the Select via Aggregation.</summary>
    private static JsonObject? BuildCountCard(string table, string column)
    {
        string aggRef = $"CountNonNull({table}.{column})";
        var select = new JsonArray
        {
            new JsonObject
            {
                ["Aggregation"] = new JsonObject
                {
                    ["Expression"] = new JsonObject
                    {
                        ["Column"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = "c" } },
                            ["Property"] = column,
                        },
                    },
                    ["Function"] = 5,   // CountNonNull
                },
                ["Name"] = aggRef,
            },
        };
        var projections = new JsonObject { ["Values"] = new JsonArray { Proj(aggRef) } };
        return SingleVisual("card", projections, From("c", table), select, $"{table} rows");
    }

    /// <summary>A category chart (clusteredColumnChart or clusteredBarChart): a measure on Y/Value, a category
    /// column on Category (axis), sorted descending by the measure.</summary>
    private static JsonObject? BuildCategoryChart(string visualType, SpecMeasure measure, string catTable, string catColumn)
    {
        string measRef = $"{measure.Table}.{measure.Name}";
        string catRef = $"{catTable}.{catColumn}";

        var from = new JsonArray { FromEntry("m", measure.Table) };
        string catAlias = "m";
        if (!string.Equals(catTable, measure.Table, StringComparison.Ordinal))
        {
            catAlias = "c";
            from.Add(FromEntry("c", catTable));
        }

        var select = new JsonArray
        {
            MeasureSelect(measure.Table, measure.Name, "m"),
            ColumnSelect(catColumn, catAlias, catRef),
        };
        var projections = new JsonObject
        {
            ["Y"] = new JsonArray { Proj(measRef) },
            ["Category"] = new JsonArray { ProjActive(catRef) },
        };
        var sv = SingleVisual(visualType, projections, from, select, $"{measure.Name} by {catColumn}");
        sv["prototypeQuery"]!["OrderBy"] = new JsonArray
        {
            new JsonObject
            {
                ["Direction"] = 2,
                ["Expression"] = new JsonObject { ["Measure"] = MeasureExpr(measure.Table, measure.Name, "m") },
            },
        };
        return WithDataLabels(sv);
    }

    /// <summary>A time-series: one or more measures on Y/Values plotted against the date column on Category
    /// (axis), sorted ascending by date so the line reads left-to-right. <paramref name="area"/> emits an
    /// areaChart; otherwise a lineChart.</summary>
    private static JsonObject? BuildLineChart(IReadOnlyList<SpecMeasure> measures, string dateTable, string dateColumn, bool area)
    {
        if (measures.Count == 0) return null;
        string dateRef = $"{dateTable}.{dateColumn}";

        // From: one alias for the measure table(s) (they typically share the fact table) + the date table.
        var from = new JsonArray();
        var aliasOf = new Dictionary<string, string>(StringComparer.Ordinal);
        string NextAlias(string table)
        {
            if (aliasOf.TryGetValue(table, out var a)) return a;
            string na = "t" + aliasOf.Count;
            aliasOf[table] = na;
            from.Add(FromEntry(na, table));
            return na;
        }
        // ensure the date table has an alias too
        string dateAlias = NextAlias(dateTable);

        var select = new JsonArray();
        var yProj = new JsonArray();
        foreach (var m in measures)
        {
            string a = NextAlias(m.Table);
            select.Add(MeasureSelect(m.Table, m.Name, a));
            yProj.Add(Proj($"{m.Table}.{m.Name}"));
        }
        select.Add(ColumnSelect(dateColumn, dateAlias, dateRef));

        var projections = new JsonObject
        {
            ["Y"] = yProj,
            ["Category"] = new JsonArray { ProjActive(dateRef) },
        };
        string title = measures.Count == 1 ? $"{measures[0].Name} over time" : "Trend over time";
        var sv = SingleVisual(area ? "areaChart" : "lineChart", projections, from, select, title);
        // ascending by the date so the axis reads chronologically
        sv["prototypeQuery"]!["OrderBy"] = new JsonArray
        {
            new JsonObject
            {
                ["Direction"] = 1,
                ["Expression"] = new JsonObject
                {
                    ["Column"] = new JsonObject
                    {
                        ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = dateAlias } },
                        ["Property"] = dateColumn,
                    },
                },
            },
        };
        // multi-measure lines read better with a legend; single-measure with data labels
        return measures.Count > 1 ? WithLegendBottom(sv) : WithDataLabels(sv);
    }

    /// <summary>tableEx over a single host table: a few non-numeric columns then 2-3 measures, flat "Values"
    /// role. Used by the Overview fallback. <paramref name="dim"/>/<paramref name="dateCol"/> are unused here -
    /// the host table is auto-picked.</summary>
    private static JsonObject? BuildTable(List<SpecTable> tables, List<SpecMeasure> measures, Dimension? dim, (string, string)? dateCol)
    {
        var host = tables.FirstOrDefault(t => t.Columns.Count > 0 && measures.Any(m => m.Table == t.Name))
                   ?? tables.FirstOrDefault(t => t.Columns.Count > 0);
        if (host == null) return null;

        var cols = host.Columns.Where(c => !IsNumericType(c.DataType)).Take(3).ToList();
        if (cols.Count == 0) cols = host.Columns.Take(3).ToList();
        var meas = measures.Where(m => m.Table == host.Name).Take(3).ToList();
        if (cols.Count == 0 && meas.Count == 0) return null;

        var select = new JsonArray();
        var values = new JsonArray();
        foreach (var c in cols)
        {
            string r = $"{host.Name}.{c.Name}";
            select.Add(ColumnSelect(c.Name, "t", r));
            values.Add(Proj(r));
        }
        foreach (var m in meas)
        {
            string r = $"{host.Name}.{m.Name}";
            select.Add(MeasureSelect(host.Name, m.Name, "t"));
            values.Add(Proj(r));
        }
        var projections = new JsonObject { ["Values"] = values };
        return SingleVisual("tableEx", projections, From("t", host.Name), select, "Detail");
    }

    /// <summary>tableEx: a dimension display column + a set of measures (cross-table; one From alias each).</summary>
    private static JsonObject? BuildDimensionTable(Dimension dim, List<SpecMeasure> measures)
    {
        if (measures.Count == 0) return null;
        var (from, aliasOf) = BeginFrom();
        string dimAlias = AddFrom(from, aliasOf, dim.Table);
        string dimRef = $"{dim.Table}.{dim.Column}";

        var select = new JsonArray { ColumnSelect(dim.Column, dimAlias, dimRef) };
        var values = new JsonArray { Proj(dimRef) };
        foreach (var m in measures)
        {
            string a = AddFrom(from, aliasOf, m.Table);
            select.Add(MeasureSelect(m.Table, m.Name, a));
            values.Add(Proj($"{m.Table}.{m.Name}"));
        }
        var projections = new JsonObject { ["Values"] = values };
        return SingleVisual("tableEx", projections, from, select, $"{dim.Column} detail");
    }

    /// <summary>tableEx: the date/period column + a set of measures (cross-table).</summary>
    private static JsonObject? BuildPeriodTable((string table, string column) dateCol, List<SpecMeasure> measures)
    {
        if (measures.Count == 0) return null;
        var (from, aliasOf) = BeginFrom();
        string dateAlias = AddFrom(from, aliasOf, dateCol.table);
        string dateRef = $"{dateCol.table}.{dateCol.column}";

        var select = new JsonArray { ColumnSelect(dateCol.column, dateAlias, dateRef) };
        var values = new JsonArray { Proj(dateRef) };
        foreach (var m in measures)
        {
            string a = AddFrom(from, aliasOf, m.Table);
            select.Add(MeasureSelect(m.Table, m.Name, a));
            values.Add(Proj($"{m.Table}.{m.Name}"));
        }
        var projections = new JsonObject { ["Values"] = values };
        return SingleVisual("tableEx", projections, from, select, "By period");
    }

    /// <summary>tableEx: a granular view - the primary dimension column (when present), the date/period column
    /// (when present) and the measures.</summary>
    private static JsonObject? BuildGranularTable(Dimension? dim, (string table, string column)? dateCol, List<SpecMeasure> measures)
    {
        var (from, aliasOf) = BeginFrom();
        var select = new JsonArray();
        var values = new JsonArray();

        if (dim != null)
        {
            string a = AddFrom(from, aliasOf, dim.Table);
            string r = $"{dim.Table}.{dim.Column}";
            select.Add(ColumnSelect(dim.Column, a, r));
            values.Add(Proj(r));
        }
        if (dateCol != null)
        {
            string a = AddFrom(from, aliasOf, dateCol.Value.table);
            string r = $"{dateCol.Value.table}.{dateCol.Value.column}";
            select.Add(ColumnSelect(dateCol.Value.column, a, r));
            values.Add(Proj(r));
        }
        foreach (var m in measures)
        {
            string a = AddFrom(from, aliasOf, m.Table);
            select.Add(MeasureSelect(m.Table, m.Name, a));
            values.Add(Proj($"{m.Table}.{m.Name}"));
        }
        if (values.Count == 0) return null;
        var projections = new JsonObject { ["Values"] = values };
        return SingleVisual("tableEx", projections, from, select, "Detail");
    }

    // -------------------------------------------------------------------- From-alias helpers (cross-table)

    private static (JsonArray from, Dictionary<string, string> aliasOf) BeginFrom()
        => (new JsonArray(), new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Reuse-or-create a From alias for a table (one entry per distinct table), returning the alias.</summary>
    private static string AddFrom(JsonArray from, Dictionary<string, string> aliasOf, string table)
    {
        if (aliasOf.TryGetValue(table, out var a)) return a;
        string na = "t" + aliasOf.Count;
        aliasOf[table] = na;
        from.Add(FromEntry(na, table));
        return na;
    }

    /// <summary>
    /// The premium title banner: a basicShape rectangle filled with the palette primary colour. Encoding proven
    /// against a production report - objects.fill[].properties = { show, fillColor.solid.color, transparency:0D }.
    /// Carries a drop shadow but no border (it is a full-width band, not a card).
    /// </summary>
    private static JsonObject BuildBannerShape(string primaryHex)
    {
        return new JsonObject
        {
            ["visualType"] = "basicShape",
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject
            {
                ["shape"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["tileShape"] = Lit("'rectangle'") } } },
                ["fill"] = new JsonArray
                {
                    new JsonObject { ["properties"] = new JsonObject
                    {
                        ["show"] = Lit("true"),
                        ["fillColor"] = SolidColor(primaryHex),
                        ["transparency"] = Lit("0D"),
                    } },
                },
            },
        };
    }

    /// <summary>
    /// The premium banner title textbox: white bold ~24pt text on top of the banner. Encoding proven against a
    /// production report - objects.general[].properties.paragraphs[].textRuns[].textStyle {fontSize "24pt",
    /// fontWeight "bold", color "#FFFFFF"}, left-aligned, with vcObjects background.show=false +
    /// visualHeader.show=false so only the text shows over the banner.
    /// </summary>
    private static JsonObject BuildBannerTitle(string text)
    {
        return new JsonObject
        {
            ["visualType"] = "textbox",
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject
            {
                ["general"] = new JsonArray
                {
                    new JsonObject { ["properties"] = new JsonObject
                    {
                        ["paragraphs"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["horizontalTextAlignment"] = "left",
                                ["textRuns"] = new JsonArray
                                {
                                    new JsonObject
                                    {
                                        ["value"] = text,
                                        ["textStyle"] = new JsonObject
                                        {
                                            ["fontSize"] = "24pt",
                                            ["fontWeight"] = "bold",
                                            ["color"] = "#FFFFFF",
                                        },
                                    },
                                },
                            },
                        },
                        ["verticalAlignment"] = Lit("'middle'"),
                    } },
                },
            },
            ["vcObjects"] = new JsonObject
            {
                ["background"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("false") } } },
                ["visualHeader"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("false") } } },
            },
        };
    }

    /// <summary>
    /// Add the PAID page header to a page (at the very top) and return the y where CONTENT should start so the
    /// page builder sizes its body to the remaining height (no overlap):
    ///   - the premium banner (palette-coloured basicShape + white bold title textbox), title = bannerTitle, then
    ///   - (when withSlicers) a slicer strip: the date slicer (if a date column exists) + the given dimension
    ///     slicers, all dropdown mode. Skips a slicer whose column is missing.
    /// Reuses BuildBannerShape/BuildBannerTitle/AddSlicerStrip so every page reads identically. Banner + slicers
    /// are excluded from the card border/shadow (basicShape/textbox/slicer handled in AddContainer/by type).
    /// </summary>
    private static double AddPageHeader(JsonArray containers, string bannerTitle, string palettePrimary,
        (string table, string column)? dateCol, IReadOnlyList<Dimension> slicerDims, bool withSlicers, LogoPart? logo)
    {
        const double bannerH = 56;
        double z = 100000;   // header sits above content; AddContainer skips card style for basicShape/textbox
        double bw = PageWidth - 2 * Margin;

        var banner = BuildBannerShape(palettePrimary);
        AddContainer(containers, banner, Margin, Margin, z++, bw, bannerH);
        var titleBox = BuildBannerTitle(bannerTitle);
        // leave room on the right for the logo so the white title (left) never overlaps it
        double titleW = bw - 40 - (logo != null ? bannerH + Gap : 0);
        AddContainer(containers, titleBox, Margin + 20, Margin, z++, titleW, bannerH);

        // brand logo: a square image visual in the banner top-RIGHT, sized to the banner height (no card style)
        if (logo != null)
        {
            double logoSize = bannerH - 8;
            double logoX = Margin + bw - logoSize - 8;   // right edge of the banner, small inset
            var img = BuildLogoVisual(logo.ResName);
            AddContainer(containers, img, logoX, Margin + 4, z++, logoSize, logoSize);
        }

        double top = Margin + bannerH + Gap;
        if (withSlicers)
        {
            double consumed = AddSlicerStrip(containers, dateCol, slicerDims, ref z, top);
            if (consumed > 0) top += consumed + Gap;
        }
        return top;
    }

    // -------------------------------------------------------------------- brand logo (white-label)

    /// <summary>A decoded brand logo: a deterministic resource name (so Build and the part-stager agree without
    /// shared state), the pbix part path it is embedded at, and its bytes.</summary>
    public sealed record LogoPart(string ResName, string PartName, byte[] Bytes);

    /// <summary>
    /// Decode a base64 PNG/JPG brand logo into a RegisteredResources image part. The resource name is derived
    /// from a content hash so <see cref="Build"/> (which writes the image visuals + package registration) and the
    /// Http build paths (which fold the part into the .pbix) compute the SAME name independently. Returns null for
    /// no/invalid logo input (banner stays text-only). Reuses the engine's RegisteredResources image convention
    /// (part under Report/StaticResources/RegisteredResources/, package type 1, item type 100) - the identical
    /// shape ReportService.AddImage/EmbedImageResource produce.
    /// </summary>
    public static LogoPart? ResolveLogoPart(string? logoBase64)
    {
        if (string.IsNullOrWhiteSpace(logoBase64)) return null;
        string b64 = logoBase64.Trim();
        // accept a data URL ("data:image/png;base64,....") or a bare base64 string
        int comma = b64.IndexOf(',');
        string ext = "png";
        if (b64.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (b64.Contains("jpeg", StringComparison.OrdinalIgnoreCase) || b64.Contains("jpg", StringComparison.OrdinalIgnoreCase)) ext = "jpg";
            if (comma >= 0) b64 = b64[(comma + 1)..];
        }
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64.Trim()); } catch { return null; }
        if (bytes.Length < 8) return null;
        // sniff PNG vs JPG from the magic bytes (overrides the data-URL hint when present)
        if (bytes[0] == 0x89 && bytes[1] == 0x50) ext = "png";
        else if (bytes[0] == 0xFF && bytes[1] == 0xD8) ext = "jpg";

        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12].ToLowerInvariant();
        string resName = "logo" + hash + "." + ext;
        string partName = "Report/StaticResources/RegisteredResources/" + resName;
        return new LogoPart(resName, partName, bytes);
    }

    /// <summary>An "image" singleVisual referencing the embedded RegisteredResources logo by name. The exact
    /// ResourcePackageItem shape ReportService.AddImage emits (PackageName RegisteredResources, PackageType 1).</summary>
    private static JsonObject BuildLogoVisual(string resName)
    {
        return new JsonObject
        {
            ["visualType"] = "image",
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject
            {
                ["general"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject() } },
                ["image"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                {
                    ["sourceFile"] = new JsonObject { ["image"] = new JsonObject
                    {
                        ["name"] = Lit($"'{resName}'"),
                        ["url"] = new JsonObject { ["expr"] = new JsonObject { ["ResourcePackageItem"] = new JsonObject
                        {
                            ["PackageName"] = "RegisteredResources",
                            ["PackageType"] = 1,
                            ["ItemName"] = resName,
                        } } },
                        ["scaling"] = Lit("'Fit'"),
                    } },
                } } },
            },
        };
    }

    /// <summary>
    /// A legacy "textbox" singleVisual carrying one paragraph with one styled text run. The renderer reads the
    /// textRuns[].textStyle for font size/color/weight and the run-level transparency for the watermark fade;
    /// paragraph horizontalTextAlignment and the general.verticalAlignment center it. No projections / no
    /// prototypeQuery: a textbox is static content, so it never queries the model.
    /// </summary>
    private static JsonObject Textbox(string text, double fontSize, string colorHex, bool bold,
        int transparencyPercent, string alignment, string verticalAlignment)
    {
        var textStyle = new JsonObject
        {
            ["fontSize"] = $"{fontSize.ToString(System.Globalization.CultureInfo.InvariantCulture)}px",
            ["color"] = colorHex,
            ["fontWeight"] = bold ? "bold" : "normal",
        };
        var run = new JsonObject { ["value"] = text, ["textStyle"] = textStyle };
        if (transparencyPercent > 0) run["transparency"] = transparencyPercent;

        var paragraph = new JsonObject
        {
            ["horizontalTextAlignment"] = alignment,
            ["textRuns"] = new JsonArray { run },
        };

        return new JsonObject
        {
            ["visualType"] = "textbox",
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject
            {
                ["general"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["paragraphs"] = new JsonArray { paragraph },
                            ["verticalAlignment"] = Lit($"'{verticalAlignment}'"),
                        },
                    },
                },
            },
        };
    }

    // -------------------------------------------------------------------- theme staging

    /// <summary>
    /// Write the ACTIVE base theme (plain SuperBiBase for Default, or the colour-merged SuperBiBase-&lt;Palette&gt; for a
    /// palette key) into the PBIP's report folder so PbixCompiler folds it into the package as the
    /// <c>Report/StaticResources/SharedResources/BaseThemes/&lt;themeName&gt;.json</c> part the Layout references.
    /// Best-effort and idempotent: a missing report folder or write failure is swallowed so it never breaks a
    /// build. Returns the part-relative path written, or null when no report folder was found. The chosen theme
    /// MUST match the one <see cref="Build"/> used (same themeKey) so the part name and the Layout reference agree.
    /// </summary>
    public static string? StageTheme(string pbipFolder, string? themeKey = null, string? brandColor = null)
    {
        if (string.IsNullOrWhiteSpace(pbipFolder) || !Directory.Exists(pbipFolder)) return null;
        StagedTheme staged;
        try { staged = ResolveStagedTheme(themeKey, brandColor); } catch { return null; }
        try
        {
            string? reportDir = Directory.GetDirectories(pbipFolder, "*.Report", SearchOption.AllDirectories).FirstOrDefault();
            if (reportDir == null) return null;
            string dest = Path.Combine(reportDir, "StaticResources", "SharedResources", "BaseThemes", staged.ThemeName + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, staged.Bytes);
            return staged.PartName;
        }
        catch { return null; }
    }

    /// <summary>
    /// Write the brand logo (when supplied) into the PBIP's report folder as the RegisteredResources image part
    /// so PbixCompiler folds it into the package and the banner image visual resolves on open. The resource name
    /// matches what <see cref="Build"/> wrote (a content hash via ResolveLogoPart), so part + reference agree.
    /// Best-effort: a missing report folder / invalid logo simply stages nothing. Returns the part path or null.
    /// </summary>
    public static string? StageLogo(string pbipFolder, string? logoBase64)
    {
        if (string.IsNullOrWhiteSpace(pbipFolder) || !Directory.Exists(pbipFolder)) return null;
        var logo = ResolveLogoPart(logoBase64);
        if (logo == null) return null;
        try
        {
            string? reportDir = Directory.GetDirectories(pbipFolder, "*.Report", SearchOption.AllDirectories).FirstOrDefault();
            if (reportDir == null) return null;
            string dest = Path.Combine(reportDir, "StaticResources", "RegisteredResources", logo.ResName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllBytes(dest, logo.Bytes);
            return logo.PartName;
        }
        catch { return null; }
    }

    /// <summary>The bytes of the embedded SuperBiBase base theme.</summary>
    private static byte[] LoadEmbeddedTheme()
    {
        var asm = typeof(ReportLayoutBuilder).Assembly;
        string resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains(BaseThemeName, StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Embedded base theme '{BaseThemeName}.json' not found in the assembly.");
        using var stream = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException($"Could not open embedded resource '{resName}'.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // -------------------------------------------------------------------- shared shape helpers

    private static JsonObject SingleVisual(string visualType, JsonObject projections, JsonArray from, JsonArray select, string? title)
    {
        var sv = new JsonObject
        {
            ["visualType"] = visualType,
            ["projections"] = projections,
            ["prototypeQuery"] = new JsonObject { ["Version"] = 2, ["From"] = from, ["Select"] = select },
            ["drillFilterOtherVisuals"] = true,
        };
        if (!string.IsNullOrWhiteSpace(title))
            sv["vcObjects"] = new JsonObject
            {
                ["title"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["text"] = Lit($"'{title!.Replace("'", "''")}'"),
                            ["show"] = Lit("true"),
                        },
                    },
                },
            };
        return sv;
    }

    /// <summary>Turn ON data labels for a chart visual (the "designed" look) by adding a singleVisual.objects
    /// block. Additive: merges into any existing objects. Safe for column/bar/line/area/pie - PBI ignores a
    /// label object a visual does not support.</summary>
    private static JsonObject WithDataLabels(JsonObject sv)
    {
        var objects = (sv["objects"] as JsonObject) ?? new JsonObject();
        objects["labels"] = new JsonArray
        {
            new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true") } },
        };
        sv["objects"] = objects;
        return sv;
    }

    /// <summary>Position a chart legend (the polished default: bottom). Additive into singleVisual.objects.</summary>
    private static JsonObject WithLegendBottom(JsonObject sv)
    {
        var objects = (sv["objects"] as JsonObject) ?? new JsonObject();
        objects["legend"] = new JsonArray
        {
            new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true"), ["position"] = Lit("'Bottom'") } },
        };
        sv["objects"] = objects;
        return sv;
    }

    /// <summary>
    /// The premium container card look: rounded corners (the visual "border" card with a radius) + a drop
    /// shadow (the "dropShadow" card, BottomRight preset). Applied to EVERY visual container so the report
    /// reads as designed tiles. Encoding is the exact proven legacy vcObjects model + Lit() used by
    /// ReportService.ApplyStyle (border.radius as a "D"-suffixed number literal; dropShadow.preset as a string
    /// literal) and validated against the bundled theme schema (Border.radius:number; the dropShadow card).
    /// Additive into vcObjects; never touches the data binding.
    /// </summary>
    private static void ApplyPremiumCardStyle(JsonObject singleVisual)
    {
        var vc = (singleVisual["vcObjects"] as JsonObject) ?? new JsonObject();
        // rounded corners: the border card, show + radius (a number literal, "D"-suffixed, matching ApplyStyle)
        vc["border"] = new JsonArray
        {
            new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true"), ["radius"] = Lit("8D") } },
        };
        // drop shadow: the dropShadow card, show + a polished BottomRight preset (string literal, matching ApplyStyle)
        vc["dropShadow"] = new JsonArray
        {
            new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true"), ["preset"] = Lit("'BottomRight'") } },
        };
        singleVisual["vcObjects"] = vc;
    }

    // -------------------------------------------------------------------- explicit palette colours

    /// <summary>A solid-fill colour value: <c>{solid:{color:{expr:{Literal:{Value:"'#hex'"}}}}}</c> - the exact
    /// shape ReportService.Lit(value, wrapSolid:true) produces (Lit("'#hex'") wrapped in solid/color).</summary>
    private static JsonObject SolidColor(string hex) =>
        new() { ["solid"] = new JsonObject { ["color"] = Lit($"'{hex}'") } };

    /// <summary>
    /// Set EXPLICIT series colours on every chart from the palette, so the report renders the palette regardless
    /// of how Power BI resolves the embedded theme. Post-processes the page's built containers (like
    /// InjectPhoneLayout): reads each chart's visualType + Y projections and writes singleVisual.objects.dataPoint:
    ///   - dataPoint.defaultColor = palette[0] (the single-series / default fill), and
    ///   - for a chart with multiple Y series (e.g. the multi-measure Trend line) a per-series dataPoint.fill
    ///     selector keyed by the series queryRef metadata, cycling the palette.
    /// Colour encoding is the proven legacy form: defaultColor/fill = a solid colour literal; the per-series
    /// selector = { data:[{dataViewWildcard:{matchingOption:0}}], metadata: "Table.Measure" } (ReportService
    /// SetConditionalFormatting writes the identical dataPoint/fill selector). Non-chart visuals are skipped.
    /// </summary>
    private static void ApplyPaletteColors(JsonArray containers, IReadOnlyList<string> palette)
    {
        if (palette.Count == 0) return;
        var chartTypes = new HashSet<string>(StringComparer.Ordinal)
        { "areaChart", "lineChart", "clusteredColumnChart", "clusteredBarChart", "columnChart", "barChart", "pieChart", "donutChart" };

        foreach (var c in containers.OfType<JsonObject>())
        {
            JsonObject cfg;
            try { cfg = JsonNode.Parse((string)c["config"]!)!.AsObject(); } catch { continue; }
            var sv = cfg["singleVisual"] as JsonObject;
            string vt = (string?)sv?["visualType"] ?? "";
            if (sv == null || !chartTypes.Contains(vt)) continue;

            // the Y (value) series queryRefs - the series we colour
            var yRefs = new List<string>();
            if (sv["projections"]?["Y"] is JsonArray yArr)
                foreach (var p in yArr) if ((string?)p?["queryRef"] is string qr) yRefs.Add(qr);

            var objects = (sv["objects"] as JsonObject) ?? new JsonObject();
            var dataPoint = new JsonArray();
            // default fill = palette[0] (recolours the primary series / single-series charts)
            dataPoint.Add(new JsonObject { ["properties"] = new JsonObject { ["defaultColor"] = SolidColor(palette[0]) } });
            // per-series colours for multi-series charts (cycle the palette by series ordinal)
            if (yRefs.Count > 1)
                for (int i = 0; i < yRefs.Count; i++)
                    dataPoint.Add(new JsonObject
                    {
                        ["selector"] = new JsonObject
                        {
                            ["data"] = new JsonArray { new JsonObject { ["dataViewWildcard"] = new JsonObject { ["matchingOption"] = 0 } } },
                            ["metadata"] = yRefs[i],
                        },
                        ["properties"] = new JsonObject { ["fill"] = SolidColor(palette[i % palette.Count]) },
                    });
            objects["dataPoint"] = dataPoint;
            sv["objects"] = objects;

            c["config"] = cfg.ToJsonString(JsonOpts);
        }
    }

    private static JsonArray From(string alias, string entity) => new() { FromEntry(alias, entity) };

    private static JsonObject FromEntry(string alias, string entity) =>
        new() { ["Name"] = alias, ["Entity"] = entity, ["Type"] = 0 };

    private static JsonObject ColumnSelect(string column, string alias, string queryRef) => new()
    {
        ["Column"] = new JsonObject
        {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = alias } },
            ["Property"] = column,
        },
        ["Name"] = queryRef,
    };

    private static JsonObject MeasureSelect(string table, string measure, string alias) => new()
    {
        ["Measure"] = MeasureExpr(table, measure, alias),
        ["Name"] = $"{table}.{measure}",
    };

    private static JsonObject MeasureExpr(string table, string measure, string alias) => new()
    {
        ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = alias } },
        ["Property"] = measure,
    };

    private static JsonObject Proj(string queryRef) => new() { ["queryRef"] = queryRef };
    private static JsonObject ProjActive(string queryRef) => new() { ["queryRef"] = queryRef, ["active"] = true };

    private static JsonObject Lit(string value) =>
        new() { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = value } } };

    /// <summary>Wrap a singleVisual in a visualContainer: position lives BOTH on the wrapper and inside
    /// config.layouts[0].position (kept consistent), and config is the nested-as-escaped-string PBI expects.
    /// The premium card look (rounded corners + drop shadow) is applied universally here to every visual EXCEPT
    /// textboxes (the transparent watermark / note / banner-title overlays) and the basicShape banner (a full-
    /// width band, not a card), which must not render as a bordered card.</summary>
    private static void AddContainer(JsonArray containers, JsonObject singleVisual,
        double x, double y, double z, double width, double height)
    {
        string vtype = (string?)singleVisual["visualType"] ?? "";
        if (vtype != "textbox" && vtype != "basicShape" && vtype != "image")
            ApplyPremiumCardStyle(singleVisual);   // rounded corners + drop shadow on every real visual
        x = Math.Round(x, 2); y = Math.Round(y, 2); width = Math.Round(width, 2); height = Math.Round(height, 2);
        var config = new JsonObject
        {
            ["name"] = NewVisualId(),
            ["layouts"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 0,
                    ["position"] = new JsonObject
                    {
                        ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = width, ["height"] = height, ["tabOrder"] = (int)z,
                    },
                },
            },
            ["singleVisual"] = singleVisual,
        };
        containers.Add(new JsonObject
        {
            ["x"] = x, ["y"] = y, ["z"] = z, ["width"] = width, ["height"] = height,
            ["config"] = config.ToJsonString(JsonOpts),   // nested-as-escaped-string
            ["filters"] = "[]",
        });
    }

    // ---- phone (mobile) layout. Desktop reads layout id 0; an additive layout id 1 holds the PHONE position
    //      and is IGNORED by the desktop renderer, so it can never break the desktop view. After a page's
    //      desktop containers are built we post-process them: stack each visual vertically on a standard
    //      320-wide phone canvas in z order. Watermark textboxes are kept (they overlay on phone too).
    private const double PhoneWidth = 320;
    private const double PhoneGap = 8;
    private const double PhoneMargin = 8;

    /// <summary>
    /// Inject an additive phone (id 1) layout into every visual on the page, stacking them vertically on a
    /// 320-wide phone canvas in z (add) order. Pure post-processing of the already-built desktop containers:
    /// it only ADDS a layouts[id=1] entry inside each container's config and never touches the id=0 desktop
    /// layout, the wrapper position, or the singleVisual - so the desktop render is unchanged.
    /// </summary>
    private static void InjectPhoneLayout(JsonArray containers)
    {
        // order tiles by their desktop z so the stack reads top-to-bottom as authored
        var ordered = containers
            .OfType<JsonObject>()
            .Select(c =>
            {
                JsonObject cfg;
                try { cfg = JsonNode.Parse((string)c["config"]!)!.AsObject(); } catch { return ((JsonObject)c, (JsonObject?)null, 0.0); }
                double z = 0; try { z = (double)cfg["layouts"]!.AsArray()[0]!["position"]!["z"]!; } catch { }
                return ((JsonObject)c, (JsonObject?)cfg, z);
            })
            .Where(t => t.Item2 != null)
            .OrderBy(t => t.Item3)
            .ToList();

        double phoneY = PhoneMargin;
        foreach (var (container, cfg, _) in ordered)
        {
            var layouts = cfg!["layouts"]!.AsArray();
            var sv = cfg["singleVisual"] as JsonObject;
            string vt = (string?)sv?["visualType"] ?? "";
            // tiles sized by type: tables/matrix tall, cards short, charts/slicers standard
            double ph = vt is "tableEx" or "pivotTable" ? 280
                      : vt is "card" or "multiRowCard" ? 96
                      : vt == "slicer" ? 88
                      : 150;
            layouts.Add(new JsonObject
            {
                ["id"] = 1,   // phone layout id (additive; desktop ignores it)
                ["position"] = new JsonObject
                {
                    ["x"] = PhoneMargin,
                    ["y"] = Math.Round(phoneY, 2),
                    ["z"] = 0,
                    ["width"] = PhoneWidth - 2 * PhoneMargin,
                    ["height"] = ph,
                    ["tabOrder"] = 0,
                },
            });
            phoneY += ph + PhoneGap;
            // re-serialize the config back into the container (the only mutation: the added id=1 layout)
            container["config"] = cfg.ToJsonString(JsonOpts);
        }
    }

    /// <summary>A fresh visual id (config.name): 32 random hex chars, the width Power BI uses.</summary>
    private static string NewVisualId() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
}
