using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SuperBiMcp.Services;

/// <summary>
/// Wave G2 read-only surface on the LEGACY report path: the wireframe lint collector, theme
/// compliance / colour inventory / recolour, the one-call report documentation artifact, the
/// DataMashup credential audit, and read-back symmetry for the write-only filter/settings/slicer
/// surface (get_report_filters / get_report_settings / get_slicer_defaults). Everything here is a
/// reader (recolor_report is the one mutator, and it only rewrites colour literals in place).
/// </summary>
public sealed partial class ReportService
{
    // ================================================================ wireframe lint (legacy collector)

    /// <summary>validate_wireframe on a legacy report session: collect every page's canvas + visual
    /// geometry and hand it to the shared <see cref="WireframeAuditor"/>.</summary>
    public object ValidateWireframe(string reportSessionId, string? pageName = null)
        => WireframeAuditor.Audit(CollectWireframePages(reportSessionId, pageName));

    /// <summary>Collect the format-agnostic wireframe geometry from the legacy Report/Layout: position
    /// from config.layouts[0].position (falling back to the container's own x/y/w/h), hidden state from
    /// singleVisual.display.mode, and the visible title for friendly labels.</summary>
    internal List<WirePage> CollectWireframePages(string reportSessionId, string? pageName)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var sections = pageName is null
            ? Sections(root).OfType<JsonObject>().ToList()
            : new List<JsonObject> { FindSection(root, pageName) };

        var pages = new List<WirePage>();
        foreach (var section in sections)
        {
            var visuals = new List<WireVisual>();
            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
                if (JsonNode.Parse(cfg) is not JsonObject co) continue;
                var sv = co["singleVisual"] as JsonObject;

                double x, y, w, h, z;
                if ((co["layouts"] as JsonArray)?.FirstOrDefault() is JsonObject l0 && l0["position"] is JsonObject p)
                { x = Num(p["x"]) ?? 0; y = Num(p["y"]) ?? 0; w = Num(p["width"]) ?? 0; h = Num(p["height"]) ?? 0; z = Num(p["z"]) ?? 0; }
                else
                { x = Num(vc["x"]) ?? 0; y = Num(vc["y"]) ?? 0; w = Num(vc["width"]) ?? 0; h = Num(vc["height"]) ?? 0; z = Num(vc["z"]) ?? 0; }

                bool hidden = string.Equals((string?)sv?["display"]?["mode"], "hidden", StringComparison.OrdinalIgnoreCase);
                visuals.Add(new WireVisual(
                    (string?)co["name"] ?? "(unnamed)", (string?)sv?["visualType"],
                    x, y, w, h, z, hidden, sv != null ? TitleText(sv) : null));
            }
            pages.Add(new WirePage(
                (string?)section["displayName"] ?? (string?)section["name"] ?? "(page)",
                Num(section["width"]) ?? 1280, Num(section["height"]) ?? 720, visuals));
        }
        return pages;
    }

    // ================================================================ theme compliance / colours

    /// <summary>Collect the current custom theme (null when none) plus every visual's parsed
    /// objects/vcObjects trees - the shared input for the theme audits.</summary>
    internal (JsonObject? theme, List<ThemeVisualEntry> visuals) CollectThemeInputs(string reportSessionId)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var cfg = JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject;
        var theme = cfg?["themeCollection"]?["customTheme"] as JsonObject;

        var visuals = new List<ThemeVisualEntry>();
        foreach (var section in Sections(root).OfType<JsonObject>())
        {
            string page = (string?)section["displayName"] ?? (string?)section["name"] ?? "(page)";
            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc || (string?)vc["config"] is not string c) continue;
                if (JsonNode.Parse(c) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;
                visuals.Add(new ThemeVisualEntry(page,
                    TitleText(sv) ?? (string?)co["name"] ?? "(unnamed)", (string?)sv["visualType"],
                    sv["objects"] as JsonObject, sv["vcObjects"] as JsonObject));
            }
        }
        return (theme, visuals);
    }

    /// <summary>audit_theme_compliance: hard-coded formatting overrides that fight the theme.</summary>
    public object AuditThemeCompliance(string reportSessionId)
    {
        var (theme, visuals) = CollectThemeInputs(reportSessionId);
        return ThemeAuditor.AuditCompliance(theme, visuals);
    }

    /// <summary>extract_report_colors: the report-wide colour inventory (visuals + theme, with locations).</summary>
    public object ExtractReportColors(string reportSessionId)
    {
        var (theme, visuals) = CollectThemeInputs(reportSessionId);
        return ThemeAuditor.CollectColors(theme, visuals);
    }

    /// <summary>recolor_report: find/replace colour literals across EVERY visual config, every page config
    /// and the report config (which carries the theme) in one call. colorMap = {"#OLD":"#NEW", ...}.
    /// The quoted expr spelling ('#RRGGBB') and the plain theme spelling (#RRGGBB) are both handled.</summary>
    public object RecolorReport(string reportSessionId, string colorMapJson)
    {
        var map = ThemeAuditor.ParseColorMap(colorMapJson);
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var tally = new Dictionary<string, int>(StringComparer.Ordinal);
        int visualsTouched = 0, total = 0;

        foreach (var section in Sections(root).OfType<JsonObject>())
        {
            // the page config (background/wallpaper colours live here)
            if ((string?)section["config"] is string sCfg && JsonNode.Parse(sCfg) is JsonObject sco)
            {
                int n = ThemeAuditor.RecolourNode(sco, map, tally);
                if (n > 0) { section["config"] = sco.ToJsonString(JsonOpts); total += n; }
            }
            // every visual's whole config (objects + vcObjects + any inline colour)
            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
                if (JsonNode.Parse(cfg) is not JsonObject co) continue;
                int n = ThemeAuditor.RecolourNode(co, map, tally);
                if (n > 0) { vc["config"] = co.ToJsonString(JsonOpts); visualsTouched++; total += n; }
            }
        }

        // the report config: the custom theme plus any other report-level colour
        int themeReplacements = 0;
        if ((string?)root["config"] is string rCfg && JsonNode.Parse(rCfg) is JsonObject rco)
        {
            themeReplacements = ThemeAuditor.RecolourNode(rco, map, tally);
            if (themeReplacements > 0) { root["config"] = rco.ToJsonString(JsonOpts); total += themeReplacements; }
        }

        if (total > 0) session.Dirty = true;
        return new
        {
            ok = true,
            replaced = total,
            visualsTouched,
            themeAndReportConfigReplacements = themeReplacements,
            perColor = tally.OrderByDescending(kv => kv.Value)
                .Select(kv => new { color = kv.Key, replaced = kv.Value, mappedTo = map[kv.Key] }).ToList(),
            note = total == 0 ? "no mapped colour literal was found - nothing changed." : "run save_report to persist.",
        };
    }

    // ================================================================ document_report

    /// <summary>document_report: a one-call Markdown artifact covering pages, visuals and field bindings
    /// (plus filters, bookmarks and the theme), rendered purely from the existing readers. When outPath is
    /// given the Markdown is also written to disk (UTF-8, no BOM).</summary>
    public object DocumentReport(string reportSessionId, string? outPath = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var cfg = JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject;
        string? themeName = (string?)cfg?["themeCollection"]?["customTheme"]?["name"];
        var reportFilters = JsonNode.Parse((string?)root["filters"] ?? "[]") as JsonArray ?? new JsonArray();
        int bookmarkCount = (cfg?["bookmarks"] as JsonArray)?.Count ?? 0;

        var sb = new StringBuilder();
        sb.AppendLine($"# Report documentation - {Path.GetFileName(session.PbixPath)}");
        sb.AppendLine();
        sb.AppendLine($"- Source: `{session.PbixPath}`");
        sb.AppendLine($"- Generated: {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"- Theme: {(themeName ?? "(base theme)")}");
        sb.AppendLine($"- Report-level filters: {reportFilters.Count}");
        sb.AppendLine($"- Bookmarks: {bookmarkCount}");
        sb.AppendLine();

        int pageCount = 0, visualCount = 0;
        foreach (var section in Sections(root).OfType<JsonObject>()
                     .OrderBy(s => (int?)Num(s["ordinal"]) ?? 0))
        {
            pageCount++;
            string page = (string?)section["displayName"] ?? (string?)section["name"] ?? "(page)";
            var pageFilters = JsonNode.Parse((string?)section["filters"] ?? "[]") as JsonArray ?? new JsonArray();
            sb.AppendLine($"## Page: {page}");
            sb.AppendLine();
            sb.AppendLine($"Canvas {Num(section["width"]) ?? 1280} x {Num(section["height"]) ?? 720}, page-level filters: {pageFilters.Count}");
            sb.AppendLine();
            sb.AppendLine("| Visual | Type | Position | Size | Title | Fields |");
            sb.AppendLine("|---|---|---|---|---|---|");

            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc || (string?)vc["config"] is not string c) continue;
                if (JsonNode.Parse(c) is not JsonObject co) continue;
                var sv = co["singleVisual"] as JsonObject;
                visualCount++;

                double x, y, w, h;
                if ((co["layouts"] as JsonArray)?.FirstOrDefault() is JsonObject l0 && l0["position"] is JsonObject p)
                { x = Num(p["x"]) ?? 0; y = Num(p["y"]) ?? 0; w = Num(p["width"]) ?? 0; h = Num(p["height"]) ?? 0; }
                else
                { x = Num(vc["x"]) ?? 0; y = Num(vc["y"]) ?? 0; w = Num(vc["width"]) ?? 0; h = Num(vc["height"]) ?? 0; }

                var fields = new List<string>();
                if (sv?["projections"] is JsonObject proj)
                    foreach (var (role, refs) in proj)
                        if (refs is JsonArray ra)
                            foreach (var r in ra)
                                if ((string?)r?["queryRef"] is string q) fields.Add($"{role}: {q}");

                sb.AppendLine($"| {Md((string?)co["name"])} | {Md((string?)sv?["visualType"])} " +
                              $"| ({Math.Round(x)},{Math.Round(y)}) | {Math.Round(w)}x{Math.Round(h)} " +
                              $"| {Md(sv != null ? TitleText(sv) : null)} | {Md(string.Join("; ", fields))} |");
            }
            sb.AppendLine();
        }

        string markdown = sb.ToString();
        if (!string.IsNullOrWhiteSpace(outPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath!)) ?? ".");
            File.WriteAllText(outPath!, markdown, new UTF8Encoding(false));
        }
        return new { ok = true, pages = pageCount, visuals = visualCount, outPath, markdown };
    }

    private static string Md(string? s) => string.IsNullOrEmpty(s) ? "" : s.Replace("|", "\\|").Replace("\n", " ");

    // ================================================================ filter read-back

    /// <summary>get_report_filters: parse the report / page / visual filter arrays into structured form -
    /// the read-back for a filter surface that was previously write-only.</summary>
    public object GetReportFilters(string reportSessionId)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var filters = new List<object>();

        foreach (var f in (JsonNode.Parse((string?)root["filters"] ?? "[]") as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            filters.Add(ParseFilterEntry(f, "report", null, null));

        foreach (var section in Sections(root).OfType<JsonObject>())
        {
            string page = (string?)section["displayName"] ?? (string?)section["name"] ?? "(page)";
            foreach (var f in (JsonNode.Parse((string?)section["filters"] ?? "[]") as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                filters.Add(ParseFilterEntry(f, "page", page, null));

            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc) continue;
                string? vname = null;
                if ((string?)vc["config"] is string c && JsonNode.Parse(c) is JsonObject co) vname = (string?)co["name"];
                foreach (var f in (JsonNode.Parse((string?)vc["filters"] ?? "[]") as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                    filters.Add(ParseFilterEntry(f, "visual", page, vname));
            }
        }
        return new { ok = true, filterCount = filters.Count, filters };
    }

    /// <summary>Parse one FilterContainer object to structured form: scope + target field + flags + the
    /// decoded condition. Symmetric with the builders (BuildScopeFilter / BuildTopNFilter /
    /// BuildRelativeFilter / BuildIncludeExcludeFilter), so whatever the write tools produce reads back.</summary>
    internal static object ParseFilterEntry(JsonObject f, string scope, string? page, string? visual)
    {
        string fieldKind = f["expression"]?["Measure"] != null ? "measure" : "column";
        var whereArr = f["filter"]?["Where"] as JsonArray;
        object? condition = null;
        if (whereArr?.FirstOrDefault() is JsonObject w0 && w0["Condition"] is JsonObject cond)
            condition = ParseCondition(cond);
        return new
        {
            scope, page, visual,
            name = (string?)f["name"],
            type = (string?)f["type"],
            table = FilterEntity(f),
            field = FilterProperty(f),
            fieldKind,
            displayName = (string?)f["displayName"],
            isHiddenInViewMode = (bool?)f["isHiddenInViewMode"],
            isLockedInViewMode = (bool?)f["isLockedInViewMode"],
            condition,
        };
    }

    /// <summary>Decode a semantic-query filter Condition into a plain structured object. Handles the node
    /// kinds our builders emit (In / Comparison / Top / RelativeDate / RelativeTime / Contains /
    /// StartsWith / And / Or / Not); anything else comes back as kind=raw with the JSON attached.</summary>
    internal static object ParseCondition(JsonObject cond)
    {
        // Not wraps the negated shapes: eq under Not is "ne", eq-null under Not is "is not blank",
        // In under Not is an exclude. The wrapper is surfaced as kind=not with the inner condition.
        if (cond["Not"]?["Expression"] is JsonObject notInner)
            return new { kind = "not", inner = ParseCondition(notInner) };

        if (cond["In"] is JsonObject inNode)
        {
            var values = new List<string?>();
            if (inNode["Values"] is JsonArray rows)
                foreach (var row in rows)
                    if ((row as JsonArray)?.FirstOrDefault() is JsonObject lit
                        && (string?)lit["Literal"]?["Value"] is string raw)
                        values.Add(UnwrapLiteral(raw));
            return new { kind = "in", values };
        }

        if (cond["Comparison"] is JsonObject cmp)
        {
            int kindCode = (int?)Num(cmp["ComparisonKind"]) ?? 0;
            string? raw = (string?)cmp["Right"]?["Literal"]?["Value"];
            string op = kindCode switch { 1 => "gt", 2 => "gte", 3 => "lt", 4 => "lte", _ => "eq" };
            if (op == "eq" && string.Equals(raw, "null", StringComparison.OrdinalIgnoreCase))
                return new { kind = "comparison", op = "isblank", value = (string?)null };
            return new { kind = "comparison", op, value = raw != null ? UnwrapLiteral(raw) : null };
        }

        if (cond["Top"] is JsonObject top)
        {
            var ob = (top["OrderBy"] as JsonArray)?.FirstOrDefault() as JsonObject;
            int dir = (int?)Num(ob?["Direction"]) ?? 2;
            var byExpr = (ob?["Expression"]?["Measure"] ?? ob?["Expression"]?["Column"]) as JsonObject;
            return new
            {
                kind = "topN",
                n = (int?)Num(top["Count"]) ?? 0,
                direction = dir == 1 ? "bottom" : "top",
                byField = (string?)byExpr?["Property"],
            };
        }

        foreach (var relKey in new[] { "RelativeDate", "RelativeTime" })
            if (cond[relKey] is JsonObject rel)
            {
                int range = (int?)Num(rel["TimeRange"]) ?? 0;
                int unit = (int?)Num(rel["TimeUnit"]) ?? 0;
                return new
                {
                    kind = relKey == "RelativeDate" ? "relativeDate" : "relativeTime",
                    mode = range switch { 1 => "Next", 2 => "This", _ => "Last" },
                    count = (int?)Num(rel["Amount"]) ?? 0,
                    unit = unit switch { 0 => "Days", 1 => "Weeks", 2 => "Months", 3 => "Years", 4 => "Hours", 5 => "Minutes", _ => "Seconds" },
                    includeCurrent = (bool?)rel["IncludeCurrent"],
                    calendar = (bool?)rel["Calendar"],
                };
            }

        foreach (var textKey in new[] { "Contains", "StartsWith" })
            if (cond[textKey] is JsonObject txt)
            {
                string? raw = (string?)txt["Right"]?["Literal"]?["Value"];
                return new
                {
                    kind = textKey == "Contains" ? "contains" : "startswith",
                    value = raw != null ? UnwrapLiteral(raw) : null,
                };
            }

        foreach (var joinKey in new[] { "And", "Or" })
            if (cond[joinKey] is JsonObject join)
                return new
                {
                    kind = joinKey == "And" ? "and" : "or",
                    left = join["Left"] is JsonObject l ? ParseCondition(l) : null,
                    right = join["Right"] is JsonObject r ? ParseCondition(r) : null,
                };

        return new { kind = "raw", raw = cond.ToJsonString(JsonOpts) };
    }

    /// <summary>Unwrap a raw semantic-query literal: strip surrounding single quotes (text) and a trailing
    /// L/D numeric suffix; datespan()/null pass through as written.</summary>
    internal static string UnwrapLiteral(string raw)
    {
        string s = raw.Trim();
        if (s.Length >= 2 && s[0] == '\'' && s[^1] == '\'') return s[1..^1].Replace("''", "'");
        if (s.Length >= 2 && (s.EndsWith("L", StringComparison.Ordinal) || s.EndsWith("D", StringComparison.Ordinal))
            && s[..^1].All(ch => char.IsDigit(ch) || ch is '.' or '-' or '+' or 'E' or 'e'))
            return s[..^1];
        return s;
    }

    // ================================================================ settings + slicer read-back

    /// <summary>get_report_settings: read back the report-level behaviour toggles set_report_settings
    /// writes into config.settings, decoded to plain values (Desktop-authored plain booleans included).</summary>
    public object GetReportSettings(string reportSessionId)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var cfg = JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject;
        var settingsNode = cfg?["settings"] as JsonObject;

        var settings = new Dictionary<string, object?>();
        if (settingsNode != null)
            foreach (var (key, val) in settingsNode)
            {
                if (val is JsonValue jv)
                {
                    if (jv.TryGetValue<bool>(out var b)) { settings[key] = b; continue; }
                    if (jv.TryGetValue<double>(out var d)) { settings[key] = d; continue; }
                    settings[key] = jv.ToString();
                }
                else settings[key] = Decode(val);   // the encoded expr.Literal shape our writer produces
            }

        return new
        {
            ok = true,
            settingCount = settings.Count,
            settings,
            hasCustomTheme = cfg?["themeCollection"]?["customTheme"] is JsonObject,
            themeName = (string?)cfg?["themeCollection"]?["customTheme"]?["name"],
            activeSectionIndex = (int?)Num(cfg?["activeSectionIndex"]),
        };
    }

    /// <summary>get_slicer_defaults: for every slicer (one page or all pages) read back its bound field,
    /// selection mode flags (strictSingleSelect / singleSelect), display mode, and any default selection
    /// values written as a Categorical filter in the slicer's own container - the read-back partner of
    /// set_slicer_selection / fix_slicer_single_select.</summary>
    public object GetSlicerDefaults(string reportSessionId, string? page = null)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var sections = page is null
            ? Sections(root).OfType<JsonObject>().ToList()
            : new List<JsonObject> { FindSection(root, page) };

        var slicers = new List<object>();
        foreach (var section in sections)
        {
            string pageName = (string?)section["displayName"] ?? (string?)section["name"] ?? "(page)";
            foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            {
                if (node is not JsonObject vc || (string?)vc["config"] is not string c) continue;
                if (JsonNode.Parse(c) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;
                if (!string.Equals((string?)sv["visualType"], "slicer", StringComparison.OrdinalIgnoreCase)) continue;

                var (table, field) = TryBoundField(sv);

                // selection-mode flags off objects.selection[0].properties
                bool? strictSingleSelect = null, singleSelect = null;
                if ((sv["objects"]?["selection"] as JsonArray)?.FirstOrDefault() is JsonObject s0
                    && s0["properties"] is JsonObject sp)
                {
                    strictSingleSelect = DecodeBool(sp["strictSingleSelect"]);
                    singleSelect = DecodeBool(sp["singleSelect"]);
                }
                // display mode (Dropdown / List / ...) off objects.data[0].properties.mode
                string? mode = null;
                if ((sv["objects"]?["data"] as JsonArray)?.FirstOrDefault() is JsonObject d0
                    && d0["properties"] is JsonObject dp)
                    mode = Decode(dp["mode"]);

                // default selection = Categorical/In filters on the bound field in the container filters
                var defaults = new List<string?>();
                foreach (var f in (JsonNode.Parse((string?)vc["filters"] ?? "[]") as JsonArray ?? new JsonArray()).OfType<JsonObject>())
                {
                    if (field != null && !string.Equals(FilterProperty(f), field, StringComparison.Ordinal)) continue;
                    var whereArr = f["filter"]?["Where"] as JsonArray;
                    if (whereArr?.FirstOrDefault() is JsonObject w0 && w0["Condition"]?["In"]?["Values"] is JsonArray rows)
                        foreach (var row in rows)
                            if ((row as JsonArray)?.FirstOrDefault() is JsonObject lit
                                && (string?)lit["Literal"]?["Value"] is string raw)
                                defaults.Add(UnwrapLiteral(raw));
                }

                slicers.Add(new
                {
                    page = pageName,
                    name = (string?)co["name"],
                    title = TitleText(sv),
                    boundField = (table != null && field != null) ? $"{table}[{field}]" : null,
                    strictSingleSelect,
                    singleSelect,
                    mode,
                    defaultValues = defaults,
                });
            }
        }
        return new { ok = true, slicerCount = slicers.Count, slicers };
    }

    private static bool? DecodeBool(JsonNode? node)
    {
        string? s = Decode(node);
        if (s is null) return null;
        return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) ? true
            : string.Equals(s, "false", StringComparison.OrdinalIgnoreCase) ? false : null;
    }

    // ================================================================ DataMashup credential audit

    /// <summary>audit_datamashup_credentials: report whether the DataMashup embeds credential material -
    /// connection strings carrying Password=/pwd=/AccountKey=/..., plus whether a PermissionBindings blob
    /// is present. PRESENCE AND LOCATION ONLY: the indicator name, query and line number are reported;
    /// secret VALUES are never echoed into the result.</summary>
    public object AuditDataMashupCredentials(string pbixPath)
    {
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        MashupContainer c;
        using (var zip = ZipFile.OpenRead(pbixPath))
        {
            var entry = FindMashupEntry(zip);
            if (entry == null)
                return new
                {
                    ok = true, pbixPath, hasDataMashup = false, credentialIndicators = Array.Empty<object>(),
                    indicatorCount = 0, permissionBindingsPresent = false,
                    note = "No DataMashup part - M lives in the DataModel/TMDL; nothing to scan here.",
                };
            byte[] raw; using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); raw = ms.ToArray(); }
            c = ParseMashup(raw);
        }
        string? m = ReadSection1M(c.PackageParts);
        var indicators = m != null ? DetectCredentialIndicators(m) : new List<object>();
        return new
        {
            ok = true,
            pbixPath,
            hasDataMashup = true,
            permissionBindingsPresent = c.PermissionBindings.Length > 0,
            indicatorCount = indicators.Count,
            credentialIndicators = indicators,
            note = "Presence and location only - secret values are NEVER echoed. PermissionBindings is a "
                 + "SHA-256 over the formulas, not a credential, but its presence means Desktop wrote this M.",
        };
    }

    // credential-looking key names inside M string literals / query options (case-insensitive).
    private static readonly Regex CredentialRx = new(
        @"\b(password|pwd|secret|client_secret|accountkey|account_key|apikey|api_key|access_token|accesstoken|sharedaccesssignature|sas_token|authorization|bearer)\b\s*[=:]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SharedQueryRx = new(
        @"^\s*shared\s+(?:#""(?<q>[^""]+)""|(?<n>[A-Za-z_][A-Za-z0-9_.]*))\s*=",
        RegexOptions.Compiled);

    /// <summary>Pure detector: scan a Section1.m document line by line for credential-material indicators.
    /// Reports the indicator keyword, the owning shared query and the 1-based line number - never the
    /// value that follows the indicator.</summary>
    internal static List<object> DetectCredentialIndicators(string section1M)
    {
        var results = new List<object>();
        string currentQuery = "(section header)";
        var lines = section1M.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var qm = SharedQueryRx.Match(lines[i]);
            if (qm.Success)
                currentQuery = qm.Groups["q"].Success ? qm.Groups["q"].Value : qm.Groups["n"].Value;

            foreach (Match match in CredentialRx.Matches(lines[i]))
                results.Add(new
                {
                    indicator = match.Groups[1].Value.ToLowerInvariant(),
                    query = currentQuery,
                    line = i + 1,
                });
        }
        return results;
    }
}
