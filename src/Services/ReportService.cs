using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SuperBiMcp.Services;

public sealed record FieldBinding(string Role, string Table, string Field, string Kind);

/// <summary>Parsed Report/Layout document plus the bytes we need to round-trip it.</summary>
public sealed class ReportLayout
{
    public required JsonObject Root { get; init; }
    public required string LayoutPartName { get; init; }   // "Report/Layout"
}

/// <summary>
/// Edits the report definition (legacy Report/Layout JSON) inside a .pbix:
/// pages, visuals, slicers, tables, matrices, charts and cards. The report is
/// plain JSON, so unlike the model it persists straight to disk - but the .pbix
/// must be CLOSED in Power BI Desktop while we patch the ZIP.
/// </summary>
public sealed partial class ReportService
{
    private readonly SessionStore _sessions;
    private readonly ILogger<ReportService> _log;

    public ReportService(SessionStore sessions, ILogger<ReportService> log)
    {
        _sessions = sessions;
        _log = log;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,  // match PBI's literal style
        WriteIndented = false,
    };

    // ------------------------------------------------------------------- open
    public object Open(string pbixPath)
    {
        var session = LoadReportSession(pbixPath);
        return new { reportSessionId = session.Id, pbixPath, pages = Sections(session.Layout.Root).Count, format = "legacy" };
    }

    /// <summary>Load a .pbix's Report/Layout into a fresh, registered ReportSession. Shared by open_report and
    /// the one-shot OFFLINE edit tools (update_visual_property / set_slicer_selection). The .pbix must be CLOSED
    /// in Power BI Desktop.</summary>
    private ReportSession LoadReportSession(string pbixPath)
    {
        if (!File.Exists(pbixPath))
            throw new FileNotFoundException($"pbix not found: {pbixPath}");

        byte[] bytes;
        using (var zip = ZipFile.OpenRead(pbixPath))
        {
            var entry = zip.GetEntry("Report/Layout")
                        ?? throw new InvalidOperationException("This .pbix has no Report/Layout (PBIR format not yet supported).");
            using var s = entry.Open(); using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray();
        }

        // Report/Layout is UTF-16-LE (BOM optional). Decode and parse.
        string text = new UnicodeEncoding(false, true).GetString(StripBom(bytes));
        var root = JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidOperationException("Report/Layout root is not a JSON object.");

        var session = new ReportSession
        {
            Id = _sessions.NewId("report"),
            PbixPath = pbixPath,
            Layout = new ReportLayout { Root = root, LayoutPartName = "Report/Layout" },
        };
        _sessions.AddReport(session);
        return session;
    }

    public object ListPages(string reportSessionId)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var pages = Sections(root).Select(s => new
        {
            name = (string?)s["name"],
            displayName = (string?)s["displayName"],
            ordinal = (int?)Num(s["ordinal"]),
            width = Num(s["width"]),
            height = Num(s["height"]),
            visuals = (s["visualContainers"] as JsonArray)?.Count ?? 0,
        });
        return new { pages };
    }

    public object AddPage(string reportSessionId, string displayName, int width, int height)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var sections = Sections(root);
        int ordinal = sections.Count == 0 ? 0 : sections.Max(s => (int?)s["ordinal"] ?? 0) + 1;
        string name = "ReportSection" + Guid.NewGuid().ToString("N");

        var section = new JsonObject
        {
            ["name"] = name,
            ["displayName"] = displayName,
            ["filters"] = "[]",
            ["ordinal"] = ordinal,
            ["visualContainers"] = new JsonArray(),
            ["config"] = "{}",
            ["width"] = width,
            ["height"] = height,
            ["displayOption"] = 1,
        };
        (root["sections"] as JsonArray)!.Add(section);
        session.Dirty = true;
        return new { ok = true, pageName = name, displayName, ordinal };
    }

    /// <summary>Rename a page: set its section.displayName (the tab title). Matches by name or current
    /// displayName; the internal section.name is unchanged so bookmarks/navigation keep working.</summary>
    public object RenamePage(string reportSessionId, string page, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new ArgumentException("newName is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        string? old = (string?)section["displayName"];
        section["displayName"] = newName;
        session.Dirty = true;
        return new { ok = true, page = (string?)section["name"], oldName = old, displayName = newName };
    }

    /// <summary>Reorder the report's pages: orderedNames lists pages (by name or displayName) in the desired
    /// left-to-right order. Each named page gets a fresh ordinal 0..n in that order; any pages NOT listed keep
    /// their relative order and are appended after the listed ones.</summary>
    public object ReorderPages(string reportSessionId, IReadOnlyList<string> orderedNames)
    {
        if (orderedNames.Count == 0) throw new ArgumentException("orderedNames must list at least one page.");
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var sections = Sections(root);

        var applied = new List<string>();
        int ordinal = 0;
        var done = new HashSet<JsonObject>();
        foreach (var nm in orderedNames)
        {
            var section = FindSection(root, nm);
            section["ordinal"] = ordinal++;
            done.Add(section);
            applied.Add((string?)section["name"] ?? nm);
        }
        // pages not named keep their relative order, appended after the explicitly-ordered ones.
        foreach (var node in sections.OfType<JsonObject>().Where(s => !done.Contains(s))
                     .OrderBy(s => (int?)Num(s["ordinal"]) ?? 0).ToList())
            node["ordinal"] = ordinal++;

        session.Dirty = true;
        return new { ok = true, ordered = applied.ToArray(), count = applied.Count, totalPages = sections.Count };
    }

    /// <summary>Resize a page's canvas: set its section.width/height (and the pageSize object so the size
    /// sticks in Desktop). Pass a custom width/height in pixels.</summary>
    public object ResizePage(string reportSessionId, string page, double width, double height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentException("width and height must be positive.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        ApplyPageSize(section, width, height, "Custom");
        session.Dirty = true;
        return new { ok = true, page = (string?)section["name"], width, height };
    }

    /// <summary>Set a page's CANVAS PRESET: 16:9 (1280x720), 4:3 (1024x768), letter (816x1056), tooltip
    /// (320x240) or mobile (320x568). preset=custom needs explicit width/height. Writes section.width/height +
    /// the pageSize object (type + width/height) so Desktop honours the preset.</summary>
    public object SetCanvasPreset(string reportSessionId, string page, string preset, double? width, double? height)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        string p = (preset ?? "").Trim().ToLowerInvariant();
        (double w, double h, string type) = p switch
        {
            "16:9" or "16x9" or "widescreen" => (1280, 720, "16:9"),
            "4:3" or "4x3" or "standard"      => (1024, 768, "4:3"),
            "letter"                          => (816, 1056, "Letter"),
            "tooltip"                         => (320, 240, "Tooltip"),
            "mobile" or "phone"               => (320, 568, "Custom"),
            "custom"                          => (width ?? throw new ArgumentException("preset=custom needs width + height."),
                                                  height ?? throw new ArgumentException("preset=custom needs width + height."), "Custom"),
            _ => throw new ArgumentException($"unknown preset '{preset}' (use 16:9|4:3|letter|tooltip|mobile|custom)."),
        };
        ApplyPageSize(section, w, h, type);
        session.Dirty = true;
        return new { ok = true, page = (string?)section["name"], preset = p, width = w, height = h, type };
    }

    /// <summary>Write a page's canvas size onto BOTH the section width/height fields and the
    /// config.objects.pageSize card (type/width/height) so the size is durable in Desktop.</summary>
    private void ApplyPageSize(JsonObject section, double width, double height, string type)
    {
        section["width"] = width;
        section["height"] = height;
        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var objects = (cfg["objects"] as JsonObject) ?? new JsonObject(); cfg["objects"] = objects;
        objects["pageSize"] = new JsonArray
        {
            new JsonObject { ["properties"] = new JsonObject
            {
                ["type"] = Lit($"'{type}'"),
                ["width"] = Lit(width.ToString(Inv) + "D"),
                ["height"] = Lit(height.ToString(Inv) + "D"),
            } },
        };
        section["config"] = cfg.ToJsonString(JsonOpts);
    }

    public object DeletePage(string reportSessionId, string pageName)
    {
        var session = _sessions.GetReport(reportSessionId);
        var sections = Sections(session.Layout.Root);
        for (int i = 0; i < sections.Count; i++)
            if (sections[i] is JsonObject so &&
                ((string?)so["name"] == pageName || (string?)so["displayName"] == pageName))
            {
                sections.RemoveAt(i);
                session.Dirty = true;
                return new { ok = true, deleted = pageName };
            }
        throw new InvalidOperationException($"Page '{pageName}' not found.");
    }

    public object ClearPages(string reportSessionId)
    {
        var session = _sessions.GetReport(reportSessionId);
        var sections = Sections(session.Layout.Root);
        int n = sections.Count;
        sections.Clear();
        session.Dirty = true;
        return new { ok = true, removed = n };
    }

    /// <summary>Hide or show a report page. A hidden page stays in the file and keeps working
    /// (drill-through, bookmarks, nav buttons) but its tab is not shown to viewers in the Power BI
    /// Service - the standard way to keep a page out of a published report. Sets "visibility" in the
    /// section config: 1 = hidden in view mode; absent = visible.</summary>
    public object SetPageVisibility(string reportSessionId, string pageName, bool hidden)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        if (hidden) cfg["visibility"] = 1;
        else cfg.Remove("visibility");
        section["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page = pageName, hidden };
    }

    // ----------------------------------------------------------------- visuals
    public object AddVisual(string reportSessionId, string pageName, string visualType,
        double x, double y, double z, double width, double height,
        IReadOnlyList<FieldBinding> bindings, string? title, string? slicerMode = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var containers = (section["visualContainers"] as JsonArray)!;

        string visualName = Guid.NewGuid().ToString("N");
        var singleVisual = BuildSingleVisual(visualType, bindings, title, slicerMode);
        AddContainer(containers, visualName, singleVisual, x, y, z, width, height);
        session.Dirty = true;
        return new { ok = true, visualName, page = pageName, visualType, fields = bindings.Count };
    }

    /// <summary>Add a formatted text box (page headers / titles / captions).</summary>
    public object AddTextbox(string reportSessionId, string pageName, string text,
        double x, double y, double width, double height, double fontSize, bool bold, string? color, string? align)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var containers = (section["visualContainers"] as JsonArray)!;
        string visualName = Guid.NewGuid().ToString("N");

        // Guarantee the box is tall/wide enough for the font so the title never clips or
        // gets a scrollbar (1pt ~= 1.333px; + line spacing + textbox padding).
        double minH = fontSize * 1.6 + 14;
        if (height < minH) height = minH;
        if (width < fontSize * 6) width = fontSize * 6;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var textStyle = new JsonObject { ["fontSize"] = fontSize.ToString(inv) + "pt" };
        if (bold) textStyle["fontWeight"] = "bold";
        if (!string.IsNullOrWhiteSpace(color)) textStyle["color"] = color;

        var singleVisual = new JsonObject
        {
            ["visualType"] = "textbox",
            ["objects"] = new JsonObject
            {
                ["general"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["paragraphs"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["textRuns"] = new JsonArray { new JsonObject { ["value"] = text, ["textStyle"] = textStyle } },
                                    ["horizontalTextAlignment"] = string.IsNullOrWhiteSpace(align) ? "left" : align,
                                },
                            },
                        },
                    },
                },
            },
            // a textbox must never carry a fill/header - force the container transparent so it never
            // shows as a white block over a banner/coloured background (themes can't be relied on here).
            ["vcObjects"] = new JsonObject
            {
                ["background"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("false") } } },
                ["visualHeader"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("false") } } },
            },
            ["drillFilterOtherVisuals"] = true,
        };
        AddContainer(containers, visualName, singleVisual, x, y, 0, width, height);
        session.Dirty = true;
        return new { ok = true, visualName, page = pageName, type = "textbox" };
    }

    /// <summary>Set a page's background colour (professional canvas styling).</summary>
    public object SetPageBackground(string reportSessionId, string pageName, string color, double transparency)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var objects = (cfg["objects"] as JsonObject) ?? new JsonObject();
        objects["background"] = new JsonArray
        {
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["color"] = Lit($"'{color}'", wrapSolid: true),
                    ["transparency"] = Lit(transparency.ToString(System.Globalization.CultureInfo.InvariantCulture) + "D"),
                },
            },
        };
        cfg["objects"] = objects;
        section["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page = pageName, background = color };
    }

    /// <summary>Place an image (e.g. a brand logo) on a page: embeds the file as a RegisteredResources
    /// part of the .pbix and adds an image visual referencing it. The other half of the brand kit.</summary>
    public object AddImage(string reportSessionId, string pageName, string imagePath,
        double x, double y, double width, double height, string scaling)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException($"image not found: {imagePath}");
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var section = FindSection(root, pageName);
        var containers = (section["visualContainers"] as JsonArray)!;

        string ext = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg") ext = "jpg";
        string origName = Path.GetFileName(imagePath);
        string resName = "logo" + Guid.NewGuid().ToString("N")[..12] + "." + ext;
        session.PendingResources["Report/StaticResources/RegisteredResources/" + resName] = File.ReadAllBytes(imagePath);

        // register the resource (resourcePackages is a top-level Layout property)
        var packages = (root["resourcePackages"] as JsonArray) ?? new JsonArray();
        root["resourcePackages"] = packages;
        JsonObject? reg = null;
        foreach (var p in packages)
            if (p is JsonObject po && po["resourcePackage"] is JsonObject rp && (string?)rp["name"] == "RegisteredResources") { reg = rp; break; }
        if (reg == null)
        {
            reg = new JsonObject { ["name"] = "RegisteredResources", ["type"] = 1, ["items"] = new JsonArray(), ["disabled"] = false };
            packages.Add(new JsonObject { ["resourcePackage"] = reg });
        }
        ((reg["items"] as JsonArray)!).Add(new JsonObject { ["type"] = 100, ["path"] = resName, ["name"] = resName });

        // the image visual
        string visualName = Guid.NewGuid().ToString("N");
        var sv = new JsonObject
        {
            ["visualType"] = "image",
            ["objects"] = new JsonObject
            {
                ["general"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject() } },
                ["image"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject {
                    ["sourceFile"] = new JsonObject { ["image"] = new JsonObject {
                        ["name"] = Lit($"'{origName.Replace("'", "''")}'"),
                        ["url"] = new JsonObject { ["expr"] = new JsonObject { ["ResourcePackageItem"] = new JsonObject {
                            ["PackageName"] = "RegisteredResources", ["PackageType"] = 1, ["ItemName"] = resName } } },
                        ["scaling"] = Lit($"'{scaling}'"),
                    } },
                } } },
            },
            ["drillFilterOtherVisuals"] = true,
        };
        AddContainer(containers, visualName, sv, x, y, 100, width, height);
        session.Dirty = true;
        return new { ok = true, visualName, page = pageName, resource = resName, scaling };
    }

    /// <summary>Embed an image file into the .pbix as a RegisteredResources part and register it in the
    /// top-level resourcePackages, returning the resource name. Shared by add_image and every re-source /
    /// plot-area / card-image builder so they all produce the identical ResourcePackageItem shape.</summary>
    private static string EmbedImageResource(ReportSession session, string imagePath)
    {
        if (!File.Exists(imagePath)) throw new FileNotFoundException($"image not found: {imagePath}");
        var root = session.Layout.Root;
        string ext = Path.GetExtension(imagePath).TrimStart('.').ToLowerInvariant();
        if (ext == "jpeg") ext = "jpg";
        string resName = "img" + Guid.NewGuid().ToString("N")[..12] + "." + ext;
        session.PendingResources["Report/StaticResources/RegisteredResources/" + resName] = File.ReadAllBytes(imagePath);

        var packages = (root["resourcePackages"] as JsonArray) ?? new JsonArray();
        root["resourcePackages"] = packages;
        JsonObject? reg = null;
        foreach (var p in packages)
            if (p is JsonObject po && po["resourcePackage"] is JsonObject rp && (string?)rp["name"] == "RegisteredResources") { reg = rp; break; }
        if (reg == null)
        {
            reg = new JsonObject { ["name"] = "RegisteredResources", ["type"] = 1, ["items"] = new JsonArray(), ["disabled"] = false };
            packages.Add(new JsonObject { ["resourcePackage"] = reg });
        }
        ((reg["items"] as JsonArray)!).Add(new JsonObject { ["type"] = 100, ["path"] = resName, ["name"] = resName });
        return resName;
    }

    /// <summary>True for an http(s):// or data: image reference (used directly as a URL) rather than a local file.</summary>
    private static bool IsImageUrl(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
        s.StartsWith("data:", StringComparison.OrdinalIgnoreCase);

    /// <summary>Build a sourceFile.image node for an image, from either a local file (embedded as a
    /// ResourcePackageItem) or an http(s)/data URL (a bare url literal). scaling = Fit|Fill|Normal.</summary>
    private static JsonObject ImageSourceFile(ReportSession session, string imageUrlOrPath, string scaling)
    {
        var img = new JsonObject();
        if (IsImageUrl(imageUrlOrPath))
        {
            img["url"] = Lit($"'{imageUrlOrPath.Replace("'", "''")}'");
        }
        else
        {
            string resName = EmbedImageResource(session, imageUrlOrPath);
            img["name"] = Lit($"'{Path.GetFileName(imageUrlOrPath).Replace("'", "''")}'");
            img["url"] = new JsonObject { ["expr"] = new JsonObject { ["ResourcePackageItem"] = new JsonObject {
                ["PackageName"] = "RegisteredResources", ["PackageType"] = 1, ["ItemName"] = resName } } };
        }
        if (!string.IsNullOrWhiteSpace(scaling)) img["scaling"] = Lit($"'{scaling}'");
        return new JsonObject { ["image"] = img };
    }

    private static JsonObject Sel(JsonObject props, string id) =>
        new() { ["properties"] = props, ["selector"] = new JsonObject { ["id"] = id } };

    private static string ResolvePageName(JsonObject root, string pageRef)
    {
        foreach (var s in Sections(root))
            if (s is JsonObject so && ((string?)so["name"] == pageRef || (string?)so["displayName"] == pageRef))
                return (string?)so["name"] ?? pageRef;
        return pageRef;
    }

    /// <summary>Add a navigation button that jumps to another page (the multi-page nav primitive).
    /// Structure matched to Power BI ground truth (actionButton + visualLink PageNavigation).</summary>
    public object AddNavButton(string reportSessionId, string pageName, string text, string targetPage,
        double x, double y, double width, double height, string fillColor, string textColor)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var containers = (section["visualContainers"] as JsonArray)!;
        string targetName = ResolvePageName(session.Layout.Root, targetPage);

        var sv = new JsonObject
        {
            ["visualType"] = "actionButton",
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject
            {
                ["icon"] = new JsonArray { Sel(new JsonObject { ["shapeType"] = Lit("'blank'") }, "default") },
                ["text"] = new JsonArray
                {
                    new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true") } },
                    Sel(new JsonObject { ["text"] = Lit($"'{text.Replace("'", "''")}'"), ["fontColor"] = Lit($"'{textColor}'", true), ["fontSize"] = Lit("11D") }, "default"),
                },
                ["fill"] = new JsonArray
                {
                    new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true") } },
                    Sel(new JsonObject { ["fillColor"] = Lit($"'{fillColor}'", true) }, "default"),
                },
            },
            ["vcObjects"] = new JsonObject
            {
                ["visualLink"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                {
                    ["show"] = Lit("true"),
                    ["type"] = Lit("'PageNavigation'"),
                    ["navigationSection"] = Lit($"'{targetName}'"),
                } } },
            },
        };
        AddContainer(containers, Guid.NewGuid().ToString("N"), sv, x, y, 0, width, height);
        session.Dirty = true;
        return new { ok = true, page = pageName, button = text, navigatesTo = targetName };
    }

    /// <summary>Show or hide a visual in the base layout (singleVisual.display.mode = hidden).</summary>
    private void SetVisualHidden(JsonObject section, string visualName, bool hidden)
    {
        foreach (var node in (section["visualContainers"] as JsonArray)!)
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co || (string?)co["name"] != visualName) continue;
            if (co["singleVisual"] is JsonObject sv)
            {
                if (hidden) sv["display"] = new JsonObject { ["mode"] = "hidden" };
                else sv.Remove("display");
                vc["config"] = co.ToJsonString(JsonOpts);
            }
            return;
        }
    }

    /// <summary>Public: set a visual's visibility (handy primitive + drives the view switcher).</summary>
    public object SetVisualVisibility(string reportSessionId, string pageName, string visualName, bool hidden)
    {
        var session = _sessions.GetReport(reportSessionId);
        SetVisualHidden(FindSection(session.Layout.Root, pageName), visualName, hidden);
        session.Dirty = true;
        return new { ok = true, visualName, hidden };
    }

    private JsonObject BuildBookmarkButton(string text, string bookmarkName, string fillColor, string textColor)
        => new()
        {
            ["visualType"] = "actionButton",
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject
            {
                ["icon"] = new JsonArray { Sel(new JsonObject { ["shapeType"] = Lit("'blank'") }, "default") },
                ["text"] = new JsonArray
                {
                    new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true") } },
                    Sel(new JsonObject { ["text"] = Lit($"'{text.Replace("'", "''")}'"), ["fontColor"] = Lit($"'{textColor}'", true), ["fontSize"] = Lit("11D") }, "default"),
                },
                ["fill"] = new JsonArray
                {
                    new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true") } },
                    Sel(new JsonObject { ["fillColor"] = Lit($"'{fillColor}'", true) }, "default"),
                },
            },
            ["vcObjects"] = new JsonObject
            {
                ["visualLink"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
                {
                    ["show"] = Lit("true"),
                    ["type"] = Lit("'Bookmark'"),
                    ["bookmark"] = Lit($"'{bookmarkName}'"),
                } } },
            },
        };

    /// <summary>
    /// The view-switcher (the defining premium pattern): one page, a button bar that swaps between
    /// VIEWS - each view shows its own set of visuals and hides the others. Builds the Display-only
    /// bookmarks + the buttons + sets the initial state to the first view. Structure matched to ground
    /// truth (display.mode hidden/show, suppressData, applyOnlyToTargetVisuals).
    /// </summary>
    public object AddViewSwitcher(string reportSessionId, string pageName, string viewsJson,
        double x, double y, double buttonWidth, double buttonHeight, double gap,
        string fillColor, string activeFillColor, string textColor)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var section = FindSection(root, pageName);
        string pgName = (string?)section["name"] ?? pageName;
        var views = JsonNode.Parse(viewsJson) as JsonArray ?? throw new InvalidOperationException("views must be a JSON array of {name, visuals:[...]}.");

        var allVisuals = new List<string>();
        var viewList = new List<(string name, HashSet<string> visuals)>();
        foreach (var vNode in views)
        {
            if (vNode is not JsonObject vo) continue;
            var set = new HashSet<string>();
            if (vo["visuals"] is JsonArray va)
                foreach (var x2 in va) { if ((string?)x2 is string vn) { set.Add(vn); if (!allVisuals.Contains(vn)) allVisuals.Add(vn); } }
            viewList.Add((((string?)vo["name"]) ?? "View", set));
        }
        if (viewList.Count == 0) throw new InvalidOperationException("no views provided.");

        // initial base state: show view[0]'s visuals, hide the rest
        foreach (var vn in allVisuals) SetVisualHidden(section, vn, !viewList[0].visuals.Contains(vn));

        var cfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var bookmarks = (cfg["bookmarks"] as JsonArray) ?? new JsonArray();
        cfg["bookmarks"] = bookmarks;
        var containers = (section["visualContainers"] as JsonArray)!;
        double bx = x;
        int i = 0;
        foreach (var (vname, set) in viewList)
        {
            string bmName = "Bookmark" + Guid.NewGuid().ToString("N")[..16];
            var vcs = new JsonObject();
            foreach (var vn in allVisuals)
                vcs[vn] = new JsonObject { ["singleVisual"] = new JsonObject { ["display"] = new JsonObject { ["mode"] = set.Contains(vn) ? "show" : "hidden" } } };
            var tvn = new JsonArray(); foreach (var v in allVisuals) tvn.Add(v);
            bookmarks.Add(new JsonObject
            {
                ["displayName"] = vname,
                ["name"] = bmName,
                ["explorationState"] = new JsonObject
                {
                    ["version"] = "1.3",
                    ["activeSection"] = pgName,
                    ["sections"] = new JsonObject { [pgName] = new JsonObject { ["visualContainers"] = vcs } },
                    ["objects"] = new JsonObject(),
                },
                ["options"] = new JsonObject
                {
                    ["targetVisualNames"] = tvn,
                    ["applyOnlyToTargetVisuals"] = true,
                    ["suppressData"] = true,
                    ["suppressActiveSection"] = true,
                },
            });
            AddContainer(containers, Guid.NewGuid().ToString("N"), BuildBookmarkButton(vname, bmName, i == 0 ? activeFillColor : fillColor, textColor), bx, y, 0, buttonWidth, buttonHeight);
            bx += buttonWidth + gap;
            i++;
        }
        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page = pageName, views = viewList.Count, buttons = i, switchableVisuals = allVisuals.Count };
    }

    /// <summary>
    /// Make a page a DRILL-THROUGH target on a column: right-clicking a data point of that column on any
    /// other page offers "Drill through" to this page, filtered to that value. Adds the drill-through
    /// page filter (howCreated 5) + the Back button. Structure matched to Power BI ground truth.
    /// </summary>
    public object AddDrillthrough(string reportSessionId, string pageName, string drillTable, string drillColumn)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);

        var filters = JsonNode.Parse((string?)section["filters"] ?? "[]") as JsonArray ?? new JsonArray();
        filters.Add(new JsonObject
        {
            ["name"] = Guid.NewGuid().ToString("N")[..20],
            ["expression"] = new JsonObject { ["Column"] = new JsonObject
            {
                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = drillTable } },
                ["Property"] = drillColumn,
            } },
            ["type"] = "Categorical",
            ["howCreated"] = 5,   // 5 = drill-through
        });
        section["filters"] = filters.ToJsonString(JsonOpts);

        var containers = (section["visualContainers"] as JsonArray)!;
        var backSv = new JsonObject
        {
            ["visualType"] = "actionButton",
            ["drillFilterOtherVisuals"] = true,
            ["objects"] = new JsonObject { ["icon"] = new JsonArray { Sel(new JsonObject { ["shapeType"] = Lit("'back'") }, "default") } },
            ["vcObjects"] = new JsonObject { ["visualLink"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject
            {
                ["show"] = Lit("true"),
                ["type"] = Lit("'Back'"),
            } } } },
        };
        AddContainer(containers, Guid.NewGuid().ToString("N"), backSv, 16, 16, 0, 48, 48);
        session.Dirty = true;
        return new { ok = true, page = pageName, drillthroughOn = $"{drillTable}[{drillColumn}]", backButton = true };
    }

    private static void SetMobile(JsonObject co, double x, double y, double width, double height, int order)
    {
        var layouts = co["layouts"] as JsonArray ?? new JsonArray(); co["layouts"] = layouts;
        for (int i = layouts.Count - 1; i >= 0; i--) if (layouts[i] is JsonObject l && (int?)Num(l["id"]) == 1) layouts.RemoveAt(i);
        layouts.Add(new JsonObject { ["id"] = 1, ["position"] = new JsonObject
        {
            ["x"] = x, ["y"] = y, ["width"] = width, ["height"] = height, ["z"] = order * 1000, ["tabOrder"] = order * 1000,
        } });
    }

    /// <summary>Place a visual on the phone (mobile) layout - a second layouts entry (id 1) on the 320-wide
    /// canvas. mobileFormatJson (optional) is a set_visual_format-shaped { vcObjects/objects } map of
    /// MOBILE-SPECIFIC formatting overrides; they are stamped onto the mobile layout entry's "objects" so the
    /// phone view can differ from the desktop view (e.g. a smaller title, hidden legend on mobile).</summary>
    public object SetMobilePosition(string reportSessionId, string pageName, string visualName,
        double x, double y, double width, double height, string? mobileFormatJson = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = FindVisual(section, visualName);
        _ = sv;
        SetMobile(co, x, y, width, height, 0);

        string? overrides = null;
        if (!string.IsNullOrWhiteSpace(mobileFormatJson))
        {
            var spec = JsonNode.Parse(mobileFormatJson!) as JsonObject
                       ?? throw new ArgumentException("mobileFormatJson must be a JSON object of { vcObjects/objects }.");
            // find the mobile layout entry (id 1) just written and attach the formatting overrides to it.
            if (co["layouts"] is JsonArray la)
                foreach (var n in la)
                    if (n is JsonObject l && (int?)Num(l["id"]) == 1)
                    {
                        var mObjects = (l["objects"] as JsonObject) ?? new JsonObject(); l["objects"] = mObjects;
                        foreach (var bucketKey in new[] { "vcObjects", "objects" })
                            if (spec[bucketKey] is JsonObject objs)
                            {
                                var bag = (mObjects[bucketKey] as JsonObject) ?? new JsonObject(); mObjects[bucketKey] = bag;
                                foreach (var (objName, propsNode) in objs)
                                    if (propsNode is JsonObject propsMap)
                                        foreach (var (propName, val) in propsMap)
                                            MergeProperty(bag, objName, propName, val);
                            }
                        overrides = spec.ToJsonString(JsonOpts);
                    }
        }

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visualName, mobile = $"{x},{y} {width}x{height}", mobileFormatting = overrides != null };
    }

    /// <summary>Show or HIDE a visual on the PHONE (mobile) layout only - its desktop visibility is unchanged.
    /// Writes the mobile layout entry's (id 1) "isHidden" flag, ensuring a mobile entry exists. visible=false
    /// hides the visual from the phone view; visible=true shows it.</summary>
    public object SetMobileVisibility(string reportSessionId, string pageName, string visualName, bool visible)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, _) = FindVisual(section, visualName);

        var layouts = (co["layouts"] as JsonArray) ?? new JsonArray(); co["layouts"] = layouts;
        JsonObject? mobile = layouts.OfType<JsonObject>().FirstOrDefault(l => (int?)Num(l["id"]) == 1);
        if (mobile is null)
        {
            // seed a mobile entry mirroring the desktop position so hide/show has something to flag.
            double dx = Num(vc["x"]) ?? 0, dy = Num(vc["y"]) ?? 0, dw = Num(vc["width"]) ?? 100, dh = Num(vc["height"]) ?? 80;
            SetMobile(co, dx, dy, dw, dh, 0);
            mobile = layouts.OfType<JsonObject>().First(l => (int?)Num(l["id"]) == 1);
        }
        mobile["isHidden"] = !visible;

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page = (string?)section["name"], visual = visualName, mobileVisible = visible };
    }

    /// <summary>Auto-generate a phone layout: stack the page's significant visuals (slicers, KPI value
    /// cards, tables, main charts) vertically on the 320-wide phone canvas. Decorative shapes, tiny delta
    /// cards and sparklines are skipped. A sensible starting mobile view in one call.</summary>
    public object AutoMobileLayout(string reportSessionId, string pageName)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        const double PW = 320, mg = 8;
        double w = PW - 2 * mg, y = mg;
        int placed = 0;
        foreach (var node in (section["visualContainers"] as JsonArray)!)
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co) continue;
            // skip visuals hidden in the base layout (e.g. non-default view-switcher views) - they would stack blankly on the phone canvas
            if ((string?)co["singleVisual"]?["display"]?["mode"] == "hidden") continue;
            string type = (string?)co["singleVisual"]?["visualType"] ?? "";
            double dW = co["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject p0 ? Num(p0["width"]) ?? 0 : 0;
            bool include = type switch
            {
                "slicer" => true,
                "card" or "multiRowCard" => dW > 150,
                "tableEx" or "pivotTable" => true,
                _ when type.Contains("Chart") => dW > 500,
                _ => false,
            };
            if (!include) continue;
            double h = type switch { "slicer" => 60, "card" or "multiRowCard" => 84, _ when type.Contains("Chart") => 220, _ => 200 };
            SetMobile(co, mg, y, w, h, placed);
            vc["config"] = co.ToJsonString(JsonOpts);
            y += h + mg;
            placed++;
        }
        session.Dirty = true;
        return new { ok = true, page = pageName, mobileVisuals = placed };
    }

    private static JsonObject Lit(string value, bool wrapSolid = false)
    {
        var literal = new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = value } } };
        if (!wrapSolid) return literal;
        return new JsonObject { ["solid"] = new JsonObject { ["color"] = literal } };
    }

    private static void AddContainer(JsonArray containers, string visualName, JsonObject singleVisual,
        double x, double y, double z, double width, double height)
    {
        var config = new JsonObject
        {
            ["name"] = visualName,
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

    public object ListVisuals(string reportSessionId, string pageName)
    {
        var section = FindSection(_sessions.GetReport(reportSessionId).Layout.Root, pageName);
        var visuals = new List<object>();
        foreach (var vc in (section["visualContainers"] as JsonArray)!)
        {
            string? cfg = (string?)vc!["config"];
            string? type = null, vname = null;
            if (cfg != null && JsonNode.Parse(cfg) is JsonObject co)
            {
                vname = (string?)co["name"];
                type = (string?)co["singleVisual"]?["visualType"];
            }
            visuals.Add(new { name = vname, visualType = type, x = Num(vc["x"]), y = Num(vc["y"]),
                width = Num(vc["width"]), height = Num(vc["height"]) });
        }
        return new { page = pageName, visuals };
    }

    public object DeleteVisual(string reportSessionId, string pageName, string visualName)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var arr = (section["visualContainers"] as JsonArray)!;
        for (int i = 0; i < arr.Count; i++)
        {
            string? cfg = (string?)arr[i]!["config"];
            if (cfg != null && JsonNode.Parse(cfg) is JsonObject co && (string?)co["name"] == visualName)
            {
                arr.RemoveAt(i);
                session.Dirty = true;
                return new { ok = true, deleted = visualName };
            }
        }
        throw new InvalidOperationException($"Visual '{visualName}' not found on page '{pageName}'.");
    }

    // -------------------------------------------------------------------- bind safety-net
    /// <summary>
    /// POST-BUILD SAFETY NET: scan every visual on every page and DROP any whose field/measure references do not
    /// resolve against the supplied model (so a card bound to a measure that does not exist - the
    /// "Something's wrong with one or more fields" failure - never reaches the customer).
    ///
    /// knownTables is the set of model table names; knownColumns is the set of resolvable "table[column]" refs;
    /// knownMeasures is the set of model MEASURE names (global - a measure is model-wide, so it resolves regardless
    /// of which table a visual attributes it to). All case-insensitive. A reference is BROKEN only when its table
    /// IS a known model table AND its name is neither a known column on that table NOR a known measure anywhere
    /// (a real typo / invented field). A reference to an UNKNOWN table is left alone (we cannot prove it broken -
    /// the model snapshot may be partial), as are visuals with no field references at all (textboxes, images,
    /// shapes, nav buttons). This makes the pass a strict NO-OP on a clean report and on anything we cannot judge.
    /// Returns the count of dropped visuals and what they referenced (for logging / the build summary).
    /// </summary>
    public object PruneUnresolvedVisuals(string reportSessionId,
        IReadOnlySet<string> knownTables, IReadOnlySet<string> knownColumns, IReadOnlySet<string> knownMeasures)
    {
        // nothing to validate against -> guaranteed no-op (never prune blindly).
        if (knownTables.Count == 0 || (knownColumns.Count == 0 && knownMeasures.Count == 0))
            return new { ok = true, dropped = 0, removed = Array.Empty<object>(), checked_ = 0, skipped = "no model field set" };

        var session = _sessions.GetReport(reportSessionId);
        var dropped = new List<object>();
        int scanned = 0;

        // One reference resolves when: its name is a known measure (global); OR a known column on its table; OR its
        // table is unknown (cannot be proven broken - conservative). The column-vs-measure tag the binder wrote is
        // NOT trusted - a name that exists as either a measure or a column anywhere is given the benefit.
        bool Resolves((string table, string field, bool measure) r) =>
            knownMeasures.Contains(r.field)
            || knownColumns.Contains(FieldKey(r.table, r.field))
            || !knownTables.Contains(r.table);

        foreach (var s in Sections(session.Layout.Root))
        {
            if (s is not JsonObject section || section["visualContainers"] is not JsonArray arr) continue;
            for (int i = arr.Count - 1; i >= 0; i--)   // reverse so removals don't shift unseen indices
            {
                if (arr[i] is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
                if (JsonNode.Parse(cfg) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;

                var refs = VisualFieldRefs(sv);
                if (refs.Count == 0) continue;        // no bindings to judge (textbox/image/shape/button) - leave it
                scanned++;

                var broken = refs.Where(r => !Resolves(r)).ToList();
                if (broken.Count == 0) continue;

                arr.RemoveAt(i);
                session.Dirty = true;
                dropped.Add(new
                {
                    page = (string?)section["displayName"] ?? (string?)section["name"],
                    visual = (string?)co["name"],
                    visualType = (string?)sv["visualType"],
                    badRefs = broken.Select(b => $"{b.table}[{b.field}]").Distinct().ToArray(),
                });
            }
        }

        return new { ok = true, dropped = dropped.Count, removed = dropped.ToArray(), checked_ = scanned };
    }

    /// <summary>
    /// POST-BUILD EMPTY-PAGE CLEANUP: drop any page that carries NO data-bound visual - a page with zero visuals,
    /// or one whose visuals are ALL decorative (textbox / image / shape / basicShape / actionButton, i.e. nothing
    /// with a field/measure binding). The companion to the multi-page prompt fix: when the agent (mis)orchestrates
    /// a recipe so it builds an empty placeholder page plus a separate content page, the blank placeholder is
    /// removed here so it never reaches the customer.
    ///
    /// A page is KEPT when it has at least one visual with a real field reference (a chart / card / table / matrix
    /// / slicer-with-a-field - anything <see cref="VisualFieldRefs"/> reports a binding for). The LAST remaining
    /// page is NEVER removed (a report must have at least one page). Strict NO-OP when every page has content, so
    /// the manual page-checklist build (clean, named, populated pages) is unaffected. Returns the count dropped
    /// and what they were named.
    /// </summary>
    public object PruneEmptyPages(string reportSessionId)
    {
        var session = _sessions.GetReport(reportSessionId);
        var sections = Sections(session.Layout.Root);
        var removed = new List<object>();

        for (int i = sections.Count - 1; i >= 0; i--)
        {
            // never remove the last surviving page - a report must keep >= 1 page. sections shrinks on RemoveAt,
            // so this guard reads the LIVE remaining count directly.
            if (sections.Count <= 1) break;
            if (sections[i] is not JsonObject section) continue;

            if (PageHasDataVisual(section)) continue;   // has real content -> keep (this is the no-op path)

            removed.Add(new
            {
                page = (string?)section["displayName"] ?? (string?)section["name"],
                visuals = (section["visualContainers"] as JsonArray)?.Count ?? 0,
            });
            sections.RemoveAt(i);
            session.Dirty = true;
        }

        return new { ok = true, pagesDropped = removed.Count, removed = removed.ToArray() };
    }

    /// <summary>True when a page has at least one DATA-BOUND visual: a visual whose prototypeQuery binds a field
    /// or measure (chart / card / table / matrix / slicer-with-a-field). Decorative-only pages (textbox / image /
    /// shape / button) and zero-visual pages return false. Uses the same binding extractor the bind safety-net
    /// uses, so "data visual" is judged identically in both passes.</summary>
    private static bool PageHasDataVisual(JsonObject section)
    {
        if (section["visualContainers"] is not JsonArray arr) return false;
        foreach (var vc in arr)
        {
            if (vc is not JsonObject vco || (string?)vco["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;
            if (VisualFieldRefs(sv).Count > 0) return true;
        }
        return false;
    }

    /// <summary>Canonical case-insensitive key for a model column: "table[column]" (brackets disambiguate so
    /// "ab[c]" can never collide with "a[bc]"). The caller builds knownColumns with the SAME scheme.</summary>
    public static string FieldKey(string table, string field) =>
        table.Trim().ToLowerInvariant() + "[" + field.Trim().ToLowerInvariant() + "]";


    /// <summary>Extract every (table, field, isMeasure) reference a visual's prototypeQuery.Select binds. The
    /// Select node shape (BuildSingleVisual / SetVisualFields) is {Column|Measure:{Expression.SourceRef.Source =
    /// alias, Property = field}}; From maps alias -&gt; Entity (table). Returns empty for a visual with no
    /// data bindings, so decorative visuals are never flagged.</summary>
    private static List<(string table, string field, bool measure)> VisualFieldRefs(JsonObject sv)
    {
        var outp = new List<(string, string, bool)>();
        if (sv["prototypeQuery"] is not JsonObject pq) return outp;

        // alias -> entity (table) from the From clause
        var aliasToEntity = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pq["From"] is JsonArray from)
            foreach (var f in from)
                if (f is JsonObject fo && (string?)fo["Name"] is { Length: > 0 } a && (string?)fo["Entity"] is { Length: > 0 } e)
                    aliasToEntity[a] = e;

        if (pq["Select"] is not JsonArray select) return outp;
        foreach (var node in select)
        {
            if (node is not JsonObject sel) continue;
            bool isMeasure = sel["Measure"] is JsonObject;
            var expr = (sel["Measure"] ?? sel["Column"]) as JsonObject;
            string? field = (string?)expr?["Property"];
            string? alias = (string?)expr?["Expression"]?["SourceRef"]?["Source"];
            string? table = alias != null && aliasToEntity.TryGetValue(alias, out var ent) ? ent : null;

            // fall back to the queryRef "Table.Field" Name when the SourceRef alias didn't resolve
            if ((table == null || field == null) && (string?)sel["Name"] is { Length: > 0 } nm && nm.Contains('.'))
            {
                int dot = nm.IndexOf('.');
                table ??= nm[..dot];
                field ??= nm[(dot + 1)..];
            }
            if (!string.IsNullOrWhiteSpace(table) && !string.IsNullOrWhiteSpace(field))
                outp.Add((table!, field!, isMeasure));
        }
        return outp;
    }

    // -------------------------------------------------------------------- sort & filter (existing visuals)
    /// <summary>Set a visual's sort order: order its query by a field (column or measure), ascending or descending.
    /// Writes prototypeQuery.OrderBy reusing the field's own From alias, and clears any stale UI sortDefinition so
    /// the visual opens in this order. The canonical fix for "blank-rank rows float to the top".</summary>
    public object SetVisualSort(string reportSessionId, string pageName, string visualName,
        string table, string field, string kind, bool descending)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = FindVisual(section, visualName);

        if (sv["prototypeQuery"] is not JsonObject pq)
            throw new InvalidOperationException($"Visual '{visualName}' has no prototypeQuery to sort.");
        var from = pq["From"] as JsonArray ?? throw new InvalidOperationException("prototypeQuery has no From clause.");

        // Reuse the visual's own From alias for this table (the OrderBy must reference the query's Source alias,
        // e.g. "d" for Dim_Products). Add the table to From only if it isn't already projected.
        string? srcAlias = null;
        foreach (var f in from)
            if (f is JsonObject fo && (string?)fo["Entity"] == table) { srcAlias = (string?)fo["Name"]; break; }
        if (srcAlias == null)
        {
            srcAlias = "s" + (from.Count + 1);
            from.Add(new JsonObject { ["Name"] = srcAlias, ["Entity"] = table, ["Type"] = 0 });
        }

        bool isMeasure = kind.Equals("measure", StringComparison.OrdinalIgnoreCase);
        var fieldExpr = new JsonObject
        {
            [isMeasure ? "Measure" : "Column"] = new JsonObject
            {
                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = srcAlias } },
                ["Property"] = field,
            },
        };
        pq["OrderBy"] = new JsonArray
        {
            new JsonObject { ["Direction"] = descending ? 2 : 1, ["Expression"] = fieldExpr },
        };
        sv.Remove("sortDefinition");   // drop a stale UI override so the query order wins on open

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visualName, sortedBy = $"{table}[{field}]", direction = descending ? "desc" : "asc" };
    }

    /// <summary>Add a visual-level filter to one visual (vc.filters). Supports comparisons (gt/gte/lt/lte/eq/ne) and
    /// isblank/isnotblank, on a column or measure. The classic use: exclude zero/blank rows from a table
    /// (e.g. Sales 52W TY is not blank) so discontinued SKUs stop cluttering the top of a ranking.</summary>
    public object AddVisualFilter(string reportSessionId, string pageName, string visualName,
        string table, string field, string kind, string op, string? value, string valueType)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, _, _) = FindVisual(section, visualName);

        var filterObj = BuildComparisonFilter(table, field, kind, op, value, valueType);

        var filters = JsonNode.Parse((string?)vc["filters"] ?? "[]") as JsonArray ?? new JsonArray();
        filters.Add(filterObj);
        vc["filters"] = filters.ToJsonString(JsonOpts);
        session.Dirty = true;
        string opl = op.ToLowerInvariant();
        return new { ok = true, visualName, filter = $"{table}[{field}] {op}{(opl is "isblank" or "isnotblank" ? "" : " " + (value ?? "0"))}" };
    }

    // ---- shared filter-object builders (one PBIX filter shape reused at visual / page / report scope) ----

    /// <summary>The PBIX field expression: the top-level filter "expression" references the full Entity; the
    /// inner Where references a From alias. kind = column|measure picks Column vs Measure.</summary>
    private static JsonObject FieldExpr(string kind, string field, JsonObject sourceRef)
    {
        string fieldKind = kind.Equals("measure", StringComparison.OrdinalIgnoreCase) ? "Measure" : "Column";
        return new JsonObject { [fieldKind] = new JsonObject { ["Expression"] = new JsonObject { ["SourceRef"] = sourceRef }, ["Property"] = field } };
    }

    /// <summary>Build an Advanced (comparison / blank) filter object - the exact shape add_visual_filter
    /// has always written. Supports gt/gte/lt/lte/eq/ne and isblank/isnotblank on a column or measure.</summary>
    private static JsonObject BuildComparisonFilter(string table, string field, string kind, string op, string? value, string valueType)
    {
        const string a = "f1";
        var leftExpr = FieldExpr(kind, field, new JsonObject { ["Source"] = a });

        JsonObject condition;
        string opl = op.ToLowerInvariant();
        if (opl is "isblank" or "isnotblank")
        {
            var cmp = new JsonObject
            {
                ["Comparison"] = new JsonObject
                {
                    ["ComparisonKind"] = 0,
                    ["Left"] = leftExpr,
                    ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "null" } },
                },
            };
            condition = opl == "isblank" ? cmp : new JsonObject { ["Not"] = new JsonObject { ["Expression"] = cmp } };
        }
        else
        {
            int kindCode = opl switch
            {
                "eq" or "ne" => 0, "gt" => 1, "gte" => 2, "lt" => 3, "lte" => 4,
                _ => throw new ArgumentException($"unknown operator '{op}' (use gt|gte|lt|lte|eq|ne|isblank|isnotblank)."),
            };
            var cmp = new JsonObject
            {
                ["Comparison"] = new JsonObject
                {
                    ["ComparisonKind"] = kindCode,
                    ["Left"] = leftExpr,
                    ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = FormatLiteral(value ?? "0", valueType) } },
                },
            };
            condition = opl == "ne" ? new JsonObject { ["Not"] = new JsonObject { ["Expression"] = cmp } } : cmp;
        }

        return new JsonObject
        {
            ["name"] = Guid.NewGuid().ToString("N")[..20],
            ["expression"] = FieldExpr(kind, field, new JsonObject { ["Entity"] = table }),
            ["filter"] = new JsonObject
            {
                ["Version"] = 2,
                ["From"] = new JsonArray { new JsonObject { ["Name"] = a, ["Entity"] = table, ["Type"] = 0 } },
                ["Where"] = new JsonArray { new JsonObject { ["Condition"] = condition } },
            },
            ["type"] = "Advanced",
            ["howCreated"] = 0,
        };
    }

    /// <summary>Build a Categorical "is one of [values]" filter (a slicer-style In filter on a column) -
    /// the standard pick-a-list page/report filter. Writes the In(Expressions, Values) Where the product uses.</summary>
    private static JsonObject BuildCategoricalFilter(string table, string field, IReadOnlyList<string> values, string valueType)
    {
        const string a = "f1";
        var colExpr = FieldExpr("column", field, new JsonObject { ["Source"] = a });

        // In: { Expressions:[<column>], Values:[[<literal>], ...] } - each value is a one-element row.
        var valueRows = new JsonArray();
        foreach (var v in values)
            valueRows.Add(new JsonArray { new JsonObject { ["Literal"] = new JsonObject { ["Value"] = FormatLiteral(v, valueType) } } });

        var condition = new JsonObject
        {
            ["In"] = new JsonObject
            {
                ["Expressions"] = new JsonArray { colExpr },
                ["Values"] = valueRows,
            },
        };

        return new JsonObject
        {
            ["name"] = Guid.NewGuid().ToString("N")[..20],
            ["expression"] = FieldExpr("column", field, new JsonObject { ["Entity"] = table }),
            ["filter"] = new JsonObject
            {
                ["Version"] = 2,
                ["From"] = new JsonArray { new JsonObject { ["Name"] = a, ["Entity"] = table, ["Type"] = 0 } },
                ["Where"] = new JsonArray { new JsonObject { ["Condition"] = condition } },
            },
            ["type"] = "Categorical",
            ["howCreated"] = 0,
        };
    }

    /// <summary>Dispatch helper used by the page/report filter tools: kind chooses the filter shape.
    /// "categorical"/"in"/"values" -> a values-list filter; anything else -> a comparison/blank filter
    /// (op = gt|gte|lt|lte|eq|ne|isblank|isnotblank). fieldKind = column|measure for the comparison form.</summary>
    internal static JsonObject BuildScopeFilter(string table, string field, string kind,
        string fieldKind, string? op, IReadOnlyList<string>? values, string valueType)
    {
        string k = (kind ?? "").ToLowerInvariant();
        if (k is "categorical" or "in" or "values")
        {
            if (values == null || values.Count == 0)
                throw new ArgumentException("a categorical filter needs at least one value.");
            return BuildCategoricalFilter(table, field, values, valueType);
        }
        // comparison / blank filter
        return BuildComparisonFilter(table, field, fieldKind, op ?? "isnotblank", values is { Count: > 0 } ? values[0] : null, valueType);
    }

    /// <summary>Add a PAGE-level filter into a section's stringified filters array (same filter object the
    /// visual-level tool writes, just stored at page scope so it filters every visual on the page).</summary>
    public object AddPageFilter(string reportSessionId, string pageName,
        string table, string field, string kind, string fieldKind, string? op, IReadOnlyList<string>? values, string valueType)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var filterObj = BuildScopeFilter(table, field, kind, fieldKind, op, values, valueType);

        var filters = JsonNode.Parse((string?)section["filters"] ?? "[]") as JsonArray ?? new JsonArray();
        filters.Add(filterObj);
        section["filters"] = filters.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, scope = "page", page = pageName, filter = $"{table}[{field}]", type = (string?)filterObj["type"] };
    }

    /// <summary>Add a REPORT-level filter into the top-level layout.filters (stringified array) so it filters
    /// every page in the report.</summary>
    public object AddReportFilter(string reportSessionId,
        string table, string field, string kind, string fieldKind, string? op, IReadOnlyList<string>? values, string valueType)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var filterObj = BuildScopeFilter(table, field, kind, fieldKind, op, values, valueType);

        var filters = JsonNode.Parse((string?)root["filters"] ?? "[]") as JsonArray ?? new JsonArray();
        filters.Add(filterObj);
        root["filters"] = filters.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, scope = "report", filter = $"{table}[{field}]", type = (string?)filterObj["type"] };
    }

    // ---- advanced filter type builders (TopN, RelativeDate, RelativeTime, Include/Exclude, multi-condition) ----

    /// <summary>Write a built filter object into a scope (visual|page|report) and return the scope/type summary.</summary>
    private object AddBuiltFilter(string reportSessionId, string scope, string? page, string? visual, JsonObject filterObj, string label)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (filters, write) = ResolveFilterScope(session.Layout.Root, scope, page, visual);
        filters.Add(filterObj);
        write(filters);
        session.Dirty = true;
        return new { ok = true, scope, page, visual, filter = label, type = (string?)filterObj["type"] };
    }

    /// <summary>The standard FilterContainer envelope: name + top-level expression (full Entity) + the
    /// From/Where filter + type + howCreated. Reused by every filter-type builder.</summary>
    private static JsonObject FilterEnvelope(string table, string field, string kind, JsonObject where, string type)
        => new JsonObject
        {
            ["name"] = Guid.NewGuid().ToString("N")[..20],
            ["expression"] = FieldExpr(kind, field, new JsonObject { ["Entity"] = table }),
            ["filter"] = new JsonObject
            {
                ["Version"] = 2,
                ["From"] = new JsonArray { new JsonObject { ["Name"] = "f1", ["Entity"] = table, ["Type"] = 0 } },
                ["Where"] = new JsonArray { new JsonObject { ["Condition"] = where } },
            },
            ["type"] = type,
            ["howCreated"] = 0,
        };

    /// <summary>Build a TopN filter: keep the top/bottom N of a column ranked by a measure. Encodes the
    /// semantic-query TopN node (Top with an OrderBy on the ranking measure) - the shape Power BI's Top-N
    /// filter card writes.</summary>
    private static JsonObject BuildTopNFilter(string table, string field, int n, string byTable, string byMeasure, string direction)
    {
        const string a = "f1";
        var colExpr = FieldExpr("column", field, new JsonObject { ["Source"] = a });
        var measureExpr = FieldExpr("measure", byMeasure, new JsonObject { ["Source"] = a });
        int dir = direction.Equals("bottom", StringComparison.OrdinalIgnoreCase) ? 1 : 2;   // 2=desc(top), 1=asc(bottom)

        var where = new JsonObject
        {
            ["Top"] = new JsonObject
            {
                ["Expressions"] = new JsonArray { colExpr },
                ["Count"] = n,
                ["OrderBy"] = new JsonArray { new JsonObject { ["Direction"] = dir, ["Expression"] = measureExpr } },
            },
        };
        // the From also needs the ranking-measure entity if it differs (kept on the same alias - same table is typical)
        var env = FilterEnvelope(table, field, "column", where, "TopN");
        if (!string.Equals(byTable, table, StringComparison.Ordinal))
            (env["filter"]!["From"] as JsonArray)!.Add(new JsonObject { ["Name"] = "f2", ["Entity"] = byTable, ["Type"] = 0 });
        return env;
    }

    /// <summary>Add a TopN (top/bottom N) filter at a scope. Keep the top/bottom n of table[field] ranked by
    /// byTable[byMeasure]. direction = top|bottom.</summary>
    public object AddTopNFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, int n, string byTable, string byMeasure, string direction)
    {
        if (n <= 0) throw new ArgumentException("n must be a positive integer.");
        var f = BuildTopNFilter(table, field, n, byTable, byMeasure, direction);
        return AddBuiltFilter(reportSessionId, scope, page, visual, f,
            $"{(direction.Equals("bottom", StringComparison.OrdinalIgnoreCase) ? "bottom" : "top")} {n} {table}[{field}] by {byTable}[{byMeasure}]");
    }

    // RelativeDate/RelativeTime time-unit enum: 0=Day,1=Week,2=Month,3=Year (RelativeTime adds 4=Hour,5=Minute,6=Second).
    private static int TimeUnitCode(string unit) => unit.Trim().ToLowerInvariant() switch
    {
        "day" or "days" => 0, "week" or "weeks" => 1, "month" or "months" => 2, "year" or "years" => 3,
        "hour" or "hours" => 4, "minute" or "minutes" => 5, "second" or "seconds" => 6,
        _ => throw new ArgumentException($"unknown time unit '{unit}' (use Days|Weeks|Months|Years[|Hours|Minutes|Seconds])."),
    };

    // RelativeDate operation: 0=Last,1=Next,2=This (the period direction the card offers).
    private static int RelativeOpCode(string mode) => mode.Trim().ToLowerInvariant() switch
    {
        "last" or "previous" => 0, "next" => 1, "this" or "current" => 2,
        _ => throw new ArgumentException($"unknown relative period '{mode}' (use Last|Next|This)."),
    };

    /// <summary>Build a RelativeDate / RelativeTime filter on a date/time column. mode=Last|Next|This,
    /// count = number of units, unit = Days/Weeks/Months/Years (or Hours/Minutes/Seconds for RelativeTime).
    /// includeToday/calendar toggle the "include today" and calendar-vs-rolling switches. Encodes the
    /// semantic-query RelativeDateFilter node (TimeRange/TimeUnit) the relative-date card writes.</summary>
    private static JsonObject BuildRelativeFilter(string table, string field, string mode, int count, string unit, bool includeCurrent, bool calendar, bool isTime)
    {
        const string a = "f1";
        var colExpr = FieldExpr("column", field, new JsonObject { ["Source"] = a });
        var rel = new JsonObject
        {
            ["Expression"] = colExpr,
            ["TimeRange"] = RelativeOpCode(mode),
            ["TimeUnit"] = TimeUnitCode(unit),
            ["Amount"] = count,
            ["IncludeCurrent"] = includeCurrent,
        };
        if (!isTime) rel["Calendar"] = calendar;   // calendar-aligned (vs rolling) - date filter only
        var where = new JsonObject { [isTime ? "RelativeTime" : "RelativeDate"] = rel };
        return FilterEnvelope(table, field, "column", where, isTime ? "RelativeTime" : "RelativeDate");
    }

    /// <summary>Add a RELATIVE DATE filter (Last/Next/This N Days/Weeks/Months/Years) at a scope.</summary>
    public object AddRelativeDateFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, string mode, int count, string unit, bool includeCurrent, bool calendar)
    {
        var f = BuildRelativeFilter(table, field, mode, count, unit, includeCurrent, calendar, isTime: false);
        return AddBuiltFilter(reportSessionId, scope, page, visual, f, $"{mode} {count} {unit} on {table}[{field}]");
    }

    /// <summary>Add a RELATIVE TIME filter (Last/Next N Hours/Minutes/Seconds) at a scope.</summary>
    public object AddRelativeTimeFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, string mode, int count, string unit, bool includeCurrent)
    {
        var f = BuildRelativeFilter(table, field, mode, count, unit, includeCurrent, calendar: false, isTime: true);
        return AddBuiltFilter(reportSessionId, scope, page, visual, f, $"{mode} {count} {unit} on {table}[{field}]");
    }

    /// <summary>Build an Include/Exclude filter (the right-click "include"/"exclude these values"): an In
    /// condition over the picked values, wrapped in Not for exclude. type = Include|Exclude.</summary>
    private static JsonObject BuildIncludeExcludeFilter(string table, string field, IReadOnlyList<string> values, string valueType, bool exclude)
    {
        const string a = "f1";
        var colExpr = FieldExpr("column", field, new JsonObject { ["Source"] = a });
        var valueRows = new JsonArray();
        foreach (var v in values)
            valueRows.Add(new JsonArray { new JsonObject { ["Literal"] = new JsonObject { ["Value"] = FormatLiteral(v, valueType) } } });
        JsonObject inCond = new JsonObject { ["In"] = new JsonObject { ["Expressions"] = new JsonArray { colExpr }, ["Values"] = valueRows } };
        var where = exclude ? new JsonObject { ["Not"] = new JsonObject { ["Expression"] = inCond } } : inCond;
        return FilterEnvelope(table, field, "column", where, exclude ? "Exclude" : "Include");
    }

    /// <summary>Add an INCLUDE or EXCLUDE filter (right-click include/exclude these values) at a scope.</summary>
    public object AddIncludeExcludeFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, IReadOnlyList<string> values, string valueType, bool exclude)
    {
        if (values.Count == 0) throw new ArgumentException("at least one value is required.");
        var f = BuildIncludeExcludeFilter(table, field, values, valueType, exclude);
        return AddBuiltFilter(reportSessionId, scope, page, visual, f,
            $"{(exclude ? "exclude" : "include")} {values.Count} value(s) on {table}[{field}]");
    }

    /// <summary>A single comparison condition for the multi-condition advanced filter builder. op =
    /// gt|gte|lt|lte|eq|ne|isblank|isnotblank|contains|startswith.</summary>
    private static JsonObject AdvancedCondition(JsonObject leftExpr, string op, string? value, string valueType)
    {
        string opl = op.ToLowerInvariant();
        if (opl is "isblank" or "isnotblank")
        {
            var c = new JsonObject { ["Comparison"] = new JsonObject { ["ComparisonKind"] = 0, ["Left"] = leftExpr.DeepClone(),
                ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "null" } } } };
            return opl == "isblank" ? c : new JsonObject { ["Not"] = new JsonObject { ["Expression"] = c } };
        }
        if (opl is "contains" or "startswith" or "doesnotcontain")
        {
            string nodeKind = opl == "startswith" ? "StartsWith" : "Contains";
            var rightLit = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = FormatLiteral(value ?? "", "string") } };
            JsonObject c = new JsonObject { [nodeKind] = new JsonObject { ["Left"] = leftExpr.DeepClone(), ["Right"] = rightLit } };
            return opl == "doesnotcontain" ? new JsonObject { ["Not"] = new JsonObject { ["Expression"] = c } } : c;
        }
        int kindCode = opl switch
        {
            "eq" or "ne" => 0, "gt" => 1, "gte" => 2, "lt" => 3, "lte" => 4,
            _ => throw new ArgumentException($"unknown operator '{op}' (use gt|gte|lt|lte|eq|ne|isblank|isnotblank|contains|startswith)."),
        };
        var cmp = new JsonObject { ["Comparison"] = new JsonObject { ["ComparisonKind"] = kindCode, ["Left"] = leftExpr.DeepClone(),
            ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = FormatLiteral(value ?? "0", valueType) } } } };
        return opl == "ne" ? new JsonObject { ["Not"] = new JsonObject { ["Expression"] = cmp } } : cmp;
    }

    /// <summary>Add an ADVANCED multi-condition filter (the And/Or two-condition advanced filter card): two
    /// conditions on the same field joined by And or Or. conditions each = (op, value); combine = and|or.
    /// fieldKind = column|measure.</summary>
    public object AddAdvancedFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, string fieldKind, string combine,
        IReadOnlyList<(string op, string? value)> conditions, string valueType)
    {
        if (conditions.Count == 0) throw new ArgumentException("at least one condition is required.");
        var leftExpr = FieldExpr(fieldKind, field, new JsonObject { ["Source"] = "f1" });

        JsonObject where = AdvancedCondition(leftExpr, conditions[0].op, conditions[0].value, valueType);
        string joiner = (combine ?? "and").Trim().ToLowerInvariant();
        string joinerKey = joiner == "or" ? "Or" : "And";
        for (int i = 1; i < conditions.Count; i++)
        {
            var next = AdvancedCondition(leftExpr, conditions[i].op, conditions[i].value, valueType);
            where = new JsonObject { [joinerKey] = new JsonObject { ["Left"] = where, ["Right"] = next } };
        }
        var f = FilterEnvelope(table, field, fieldKind, where, "Advanced");
        return AddBuiltFilter(reportSessionId, scope, page, visual, f,
            $"{table}[{field}] {string.Join($" {joiner} ", conditions.Select(c => c.op + (c.value is null ? "" : " " + c.value)))}");
    }

    // ---- Wave S filter/query AST completeness: between, does-not-contain, fixed-anchor relative date ----

    /// <summary>Build a single Comparison condition (Left op Right-literal) for a given left expression.</summary>
    private static JsonObject ComparisonCond(JsonObject leftExpr, int kindCode, string value, string valueType)
        => new JsonObject
        {
            ["Comparison"] = new JsonObject
            {
                ["ComparisonKind"] = kindCode,
                ["Left"] = leftExpr.DeepClone(),
                ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = FormatLiteral(value, valueType) } },
            },
        };

    /// <summary>Add a BETWEEN filter (lo &lt;= field &lt;= hi) at a scope: two Comparisons (GTE + LTE) joined by
    /// And. valueType picks the literal encoding (int|decimal|double|datetime|...). fieldKind = column|measure.</summary>
    public object AddBetweenFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, string fieldKind, string lo, string hi, string valueType)
    {
        var leftExpr = FieldExpr(fieldKind, field, new JsonObject { ["Source"] = "f1" });
        var gte = ComparisonCond(leftExpr, 2, lo, valueType);   // 2 = GreaterThanOrEqual
        var lte = ComparisonCond(leftExpr, 4, hi, valueType);   // 4 = LessThanOrEqual
        var where = new JsonObject { ["And"] = new JsonObject { ["Left"] = gte, ["Right"] = lte } };
        var f = FilterEnvelope(table, field, fieldKind, where, "Advanced");
        return AddBuiltFilter(reportSessionId, scope, page, visual, f, $"{table}[{field}] between {lo} and {hi}");
    }

    /// <summary>Add a DOES-NOT-CONTAIN filter (Not(Contains)) on a text column at a scope.</summary>
    public object AddDoesNotContainFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, string value)
    {
        var leftExpr = FieldExpr("column", field, new JsonObject { ["Source"] = "f1" });
        var contains = new JsonObject
        {
            ["Contains"] = new JsonObject
            {
                ["Left"] = leftExpr,
                ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = FormatLiteral(value, "string") } },
            },
        };
        var where = new JsonObject { ["Not"] = new JsonObject { ["Expression"] = contains } };
        var f = FilterEnvelope(table, field, "column", where, "Advanced");
        return AddBuiltFilter(reportSessionId, scope, page, visual, f, $"{table}[{field}] does not contain '{value}'");
    }

    /// <summary>Add a FIXED-ANCHOR relative-date window (UI-impossible): Last/Next N units measured from a
    /// LITERAL anchor date instead of Now. Encoded as two Comparisons (GTE lower-bound + LT upper-bound)
    /// against literal datetime boundaries computed from the anchor. mode = Last|Next, unit =
    /// Days|Weeks|Months|Years. "Last 3 Months" from anchor A keeps [A-3 units, A); "Next" keeps [A, A+N units).</summary>
    public object AddFixedAnchorRelativeDateFilter(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, string anchorDate, string mode, int count, string unit)
    {
        if (count <= 0) throw new ArgumentException("count must be a positive integer.");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var anchor = DateTime.Parse(anchorDate.Trim(), inv, System.Globalization.DateTimeStyles.RoundtripKind);

        DateTime Shift(DateTime d, int amount) => unit.Trim().ToLowerInvariant() switch
        {
            "day" or "days" => d.AddDays(amount),
            "week" or "weeks" => d.AddDays(amount * 7),
            "month" or "months" => d.AddMonths(amount),
            "year" or "years" => d.AddYears(amount),
            _ => throw new ArgumentException($"unknown unit '{unit}' (use Days|Weeks|Months|Years)."),
        };

        string m = mode.Trim().ToLowerInvariant();
        DateTime lower, upper;
        if (m is "last" or "previous") { lower = Shift(anchor, -count); upper = anchor; }
        else if (m == "next") { lower = anchor; upper = Shift(anchor, count); }
        else throw new ArgumentException($"unknown mode '{mode}' (use Last|Next).");

        string Iso(DateTime d) => d.ToString("yyyy-MM-ddTHH:mm:ss", inv);
        var leftExpr = FieldExpr("column", field, new JsonObject { ["Source"] = "f1" });
        var gte = ComparisonCond(leftExpr, 2, Iso(lower), "datetime");   // 2 = GreaterThanOrEqual
        var lt = ComparisonCond(leftExpr, 3, Iso(upper), "datetime");    // 3 = LessThan (upper bound exclusive)
        var where = new JsonObject { ["And"] = new JsonObject { ["Left"] = gte, ["Right"] = lt } };
        var f = FilterEnvelope(table, field, "column", where, "Advanced");
        return AddBuiltFilter(reportSessionId, scope, page, visual, f,
            $"{mode} {count} {unit} of {table}[{field}] anchored at {anchor:yyyy-MM-dd}");
    }

    // ---- prototypeQuery editing: aggregation function + visual-level Top N ----

    // Aggregate-function index used by a Select node's Column/Measure Aggregation.Function.
    private static int AggregationCode(string aggregation) => aggregation.Trim().ToLowerInvariant() switch
    {
        "sum" => 0, "avg" or "average" => 1, "min" or "minimum" => 2, "max" or "maximum" => 3,
        "count" => 4, "countnonnull" or "countdistinct" => 5, "median" => 6, "stddev" or "standarddeviation" => 7,
        "var" or "variance" => 8,
        _ => throw new ArgumentException($"unknown aggregation '{aggregation}' (use Sum|Avg|Min|Max|Count|CountNonNull|Median|StdDev|Var)."),
    };

    /// <summary>Change the AGGREGATION applied to a field projected on a visual: rewrites the matching Select
    /// node so its Column/Measure expression is wrapped in an Aggregation { Function }. aggregation =
    /// Sum|Avg|Min|Max|Count|CountNonNull|Median|StdDev|Var (index 0..8). field = "Table.Field" (the queryRef).
    /// scopedEvalBaseline=true wraps the aggregate in a context-free ScopedEval (AllRolesRef) baseline - the
    /// "evaluate ignoring the visual's groupings" trick (FLAG: confirm the ScopedEval shape in Desktop).</summary>
    public object EditVisualAggregation(string reportSessionId, string page, string visual,
        string field, string aggregation, bool scopedEvalBaseline = false)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = FindVisual(FindSection(session.Layout.Root, page), visual);
        int func = AggregationCode(aggregation);

        if (sv["prototypeQuery"] is not JsonObject pq || pq["Select"] is not JsonArray select)
            throw new InvalidOperationException($"visual '{visual}' has no prototypeQuery.Select to edit.");

        // match the Select node by its Name (= queryRef "Table.Field") or by the field's Property.
        string wantField = field.Contains('.') ? field[(field.IndexOf('.') + 1)..] : field;
        JsonObject? target = null;
        foreach (var node in select)
        {
            if (node is not JsonObject s) continue;
            string? name = (string?)s["Name"];
            string? prop = (string?)(s["Column"]?["Property"] ?? s["Measure"]?["Property"]
                                     ?? s["Aggregation"]?["Expression"]?["Column"]?["Property"]
                                     ?? s["Aggregation"]?["Expression"]?["Measure"]?["Property"]);
            if (string.Equals(name, field, StringComparison.Ordinal) || string.Equals(prop, wantField, StringComparison.Ordinal))
            { target = s; break; }
        }
        if (target == null)
            throw new InvalidOperationException($"field '{field}' is not projected on visual '{visual}'.");

        // pull out the WRAPPED Column/Measure expression so Aggregation.Expression keeps its { Column | Measure }
        // wrapper (unwrapping a prior Aggregation/ScopedEval if the field is already aggregated).
        JsonObject wrappedExpr;
        if (target["Column"] is JsonNode colNode)
            wrappedExpr = new JsonObject { ["Column"] = colNode.DeepClone() };
        else if (target["Measure"] is JsonNode measNode)
            wrappedExpr = new JsonObject { ["Measure"] = measNode.DeepClone() };
        else
        {
            // already aggregated: lift the inner Expression (which itself carries { Column | Measure }).
            var prior = target["Aggregation"]?["Expression"] ?? target["ScopedEval"]?["Expression"]?["Aggregation"]?["Expression"];
            wrappedExpr = prior is JsonObject po ? (JsonObject)po.DeepClone()
                          : throw new InvalidOperationException("the Select node has no Column/Measure expression to aggregate.");
        }
        string? nameKeep = (string?)target["Name"];

        var agg = new JsonObject { ["Expression"] = wrappedExpr, ["Function"] = func };
        JsonNode aggExpr = scopedEvalBaseline
            ? new JsonObject
              {
                  // context-free baseline: evaluate the aggregate ignoring the visual's own role groupings.
                  ["ScopedEval"] = new JsonObject
                  {
                      ["Expression"] = new JsonObject { ["Aggregation"] = agg },
                      ["Scope"] = new JsonArray { new JsonObject { ["AllRolesRef"] = new JsonObject() } },
                  },
              }
            : new JsonObject { ["Aggregation"] = agg };

        // rebuild the node cleanly: only the aggregate expression + its Name.
        target.Remove("Column"); target.Remove("Measure"); target.Remove("Aggregation"); target.Remove("ScopedEval");
        foreach (var kv in ((JsonObject)aggExpr).ToList()) { ((JsonObject)aggExpr).Remove(kv.Key); target[kv.Key] = kv.Value; }
        if (nameKeep != null) target["Name"] = nameKeep;

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], field, aggregation = aggregation.ToLowerInvariant(),
            functionIndex = func, scopedEvalBaseline,
            note = scopedEvalBaseline ? "ScopedEval/AllRolesRef context-free baseline applied (confirm shape in Desktop)." : null };
    }

    /// <summary>Add a VISUAL-LEVEL Top N: rank rankField by byMeasure, keep the top/bottom n, with the ranking
    /// applied in the visual's own query (a Top node in prototypeQuery.Where). direction = Top|Bottom. This is
    /// the visual's "Top N" filter applied at the query level (vs add_topn_filter which writes a filter card).</summary>
    public object AddVisualTopN(string reportSessionId, string page, string visual,
        string rankTable, string rankField, string byTable, string byMeasure, int n, string direction)
    {
        if (n <= 0) throw new ArgumentException("n must be a positive integer.");
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = FindVisual(FindSection(session.Layout.Root, page), visual);

        if (sv["prototypeQuery"] is not JsonObject pq)
            throw new InvalidOperationException($"visual '{visual}' has no prototypeQuery.");
        var from = (pq["From"] as JsonArray) ?? new JsonArray();
        pq["From"] = from;

        // reuse the rank-table's From alias (or add it); same for the by-measure table.
        string AliasFor(string entity)
        {
            foreach (var fo in from.OfType<JsonObject>())
                if (string.Equals((string?)fo["Entity"], entity, StringComparison.Ordinal)) return (string?)fo["Name"] ?? "f1";
            string a = "v" + (from.Count + 1);
            from.Add(new JsonObject { ["Name"] = a, ["Entity"] = entity, ["Type"] = 0 });
            return a;
        }
        string rankAlias = AliasFor(rankTable);
        string byAlias = AliasFor(byTable);

        var colExpr = FieldExpr("column", rankField, new JsonObject { ["Source"] = rankAlias });
        var measureExpr = FieldExpr("measure", byMeasure, new JsonObject { ["Source"] = byAlias });
        int dir = direction.Trim().Equals("bottom", StringComparison.OrdinalIgnoreCase) ? 1 : 2;  // 2=desc(top),1=asc(bottom)

        var top = new JsonObject
        {
            ["Top"] = new JsonObject
            {
                ["Expressions"] = new JsonArray { colExpr },
                ["Count"] = n,
                ["OrderBy"] = new JsonArray { new JsonObject { ["Direction"] = dir, ["Expression"] = measureExpr } },
            },
        };
        var where = (pq["Where"] as JsonArray) ?? new JsonArray();
        where.Add(new JsonObject { ["Condition"] = top });
        pq["Where"] = where;

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"],
            visualTopN = $"{(dir == 1 ? "bottom" : "top")} {n} {rankTable}[{rankField}] by {byTable}[{byMeasure}]" };
    }

    /// <summary>Set a filter card's RESTATEMENT (custom display label) + lock/hide flags on a matching filter at
    /// a scope. displayName overrides the card's auto-generated label (the "restatement"); isHiddenInViewMode /
    /// isLockedInViewMode toggle the card's hide/lock. Extends the Wave A/N filter object in place.</summary>
    public object SetFilterRestatement(string reportSessionId, string scope, string? page, string? visual,
        string table, string field, string? displayName, bool? isHiddenInViewMode, bool? isLockedInViewMode)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (filters, write) = ResolveFilterScope(session.Layout.Root, scope, page, visual);

        int updated = 0;
        foreach (var node in filters)
        {
            if (node is not JsonObject f || !FilterMatches(f, table, field)) continue;
            if (displayName != null) f["displayName"] = displayName;
            if (isHiddenInViewMode.HasValue) f["isHiddenInViewMode"] = isHiddenInViewMode.Value;
            if (isLockedInViewMode.HasValue) f["isLockedInViewMode"] = isLockedInViewMode.Value;
            updated++;
        }
        if (updated == 0)
            throw new InvalidOperationException($"no {scope}-level filter found on {table}[{field}] to restate.");

        write(filters);
        session.Dirty = true;
        return new { ok = true, scope, page, visual, field = $"{table}[{field}]",
            displayName, isHiddenInViewMode, isLockedInViewMode, updated };
    }

    // ---- filter helpers shared by remove / lock / hide (match a filter on table[field]) ----

    /// <summary>The Entity an existing filter object's "expression" references (Column or Measure).</summary>
    private static string? FilterEntity(JsonObject f) =>
        (string?)(f["expression"]?["Column"]?["Expression"]?["SourceRef"]?["Entity"]
                  ?? f["expression"]?["Measure"]?["Expression"]?["SourceRef"]?["Entity"]);

    /// <summary>The Property (field name) an existing filter object's "expression" references.</summary>
    private static string? FilterProperty(JsonObject f) =>
        (string?)(f["expression"]?["Column"]?["Property"] ?? f["expression"]?["Measure"]?["Property"]);

    private static bool FilterMatches(JsonObject f, string table, string field) =>
        string.Equals(FilterEntity(f), table, StringComparison.Ordinal) &&
        string.Equals(FilterProperty(f), field, StringComparison.Ordinal);

    /// <summary>Resolve the stringified filters array for a scope and return a setter to write it back.</summary>
    private (JsonArray filters, Action<JsonArray> write) ResolveFilterScope(
        JsonObject root, string scope, string? pageName, string? visualName)
    {
        switch ((scope ?? "").ToLowerInvariant())
        {
            case "report":
                return (JsonNode.Parse((string?)root["filters"] ?? "[]") as JsonArray ?? new JsonArray(),
                        arr => root["filters"] = arr.ToJsonString(JsonOpts));
            case "page":
            {
                var section = FindSection(root, pageName ?? throw new ArgumentException("page is required for scope=page."));
                return (JsonNode.Parse((string?)section["filters"] ?? "[]") as JsonArray ?? new JsonArray(),
                        arr => section["filters"] = arr.ToJsonString(JsonOpts));
            }
            case "visual":
            {
                var section = FindSection(root, pageName ?? throw new ArgumentException("page is required for scope=visual."));
                var (vc, _, _) = FindVisual(section, visualName ?? throw new ArgumentException("visual is required for scope=visual."));
                return (JsonNode.Parse((string?)vc["filters"] ?? "[]") as JsonArray ?? new JsonArray(),
                        arr => vc["filters"] = arr.ToJsonString(JsonOpts));
            }
            default:
                throw new ArgumentException($"unknown scope '{scope}' (use visual|page|report).");
        }
    }

    /// <summary>Remove a matching filter (by table[field]) at the given scope (visual|page|report).</summary>
    public object RemoveFilter(string reportSessionId, string scope, string? pageName, string? visualName, string table, string field)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (filters, write) = ResolveFilterScope(session.Layout.Root, scope, pageName, visualName);

        int removed = 0;
        for (int i = filters.Count - 1; i >= 0; i--)
            if (filters[i] is JsonObject f && FilterMatches(f, table, field)) { filters.RemoveAt(i); removed++; }

        write(filters);
        session.Dirty = true;
        return new { ok = true, scope, page = pageName, visual = visualName, field = $"{table}[{field}]", removed };
    }

    /// <summary>Set the lock / hide-in-view-mode flags on a matching filter at the given scope. Writes the
    /// PBIX filter object's "isLockedInViewMode" / "isHiddenInViewMode" booleans (verified ground-truth flags).</summary>
    public object SetFilterFlags(string reportSessionId, string scope, string? pageName, string? visualName,
        string table, string field, bool? locked, bool? hidden)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (filters, write) = ResolveFilterScope(session.Layout.Root, scope, pageName, visualName);

        int updated = 0;
        foreach (var node in filters)
        {
            if (node is not JsonObject f || !FilterMatches(f, table, field)) continue;
            if (locked.HasValue) f["isLockedInViewMode"] = locked.Value;
            if (hidden.HasValue) f["isHiddenInViewMode"] = hidden.Value;
            updated++;
        }
        if (updated == 0)
            throw new InvalidOperationException($"no {scope}-level filter found on {table}[{field}] to lock/hide.");

        write(filters);
        session.Dirty = true;
        return new { ok = true, scope, page = pageName, visual = visualName, field = $"{table}[{field}]", locked, hidden, updated };
    }

    // ---- visual interactions (edit interactions: source visual -> target visual = filter|highlight|none) ----

    // Legacy Report/Layout encodes edit-interaction overrides as a "relationships" array in the page's
    // stringified config; each entry is { source, target, type } where type is an integer:
    //   0 = Default (auto), 1 = Filter, 2 = Highlight, 3 = None.
    private static int InteractionCode(string interaction) => interaction.ToLowerInvariant() switch
    {
        "filter" => 1,
        "highlight" => 2,
        "none" or "nofilter" => 3,
        "default" or "auto" => 0,
        _ => throw new ArgumentException($"unknown interaction '{interaction}' (use filter|highlight|none)."),
    };

    /// <summary>Set how a SOURCE visual affects a TARGET visual when a data point is selected (edit
    /// interactions). Writes/updates a { source, target, type } override in the page config's "relationships"
    /// array. interaction = filter|highlight|none. source/target are visual names (ids).</summary>
    public object SetVisualInteractions(string reportSessionId, string pageName, string sourceVisual, string targetVisual, string interaction)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        // resolve both visuals exist (throws with a clear message otherwise)
        FindVisual(section, sourceVisual);
        FindVisual(section, targetVisual);

        int code = InteractionCode(interaction);
        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var rels = (cfg["relationships"] as JsonArray) ?? new JsonArray();
        cfg["relationships"] = rels;

        // update an existing override for this source+target, else add one.
        JsonObject? existing = null;
        foreach (var r in rels)
            if (r is JsonObject ro && (string?)ro["source"] == sourceVisual && (string?)ro["target"] == targetVisual) { existing = ro; break; }
        if (existing != null) existing["type"] = code;
        else rels.Add(new JsonObject { ["source"] = sourceVisual, ["target"] = targetVisual, ["type"] = code });

        section["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page = pageName, source = sourceVisual, target = targetVisual, interaction = interaction.ToLowerInvariant(), type = code };
    }

    // ---- filter pane (show/hide + collapse) ----

    /// <summary>Show/hide and expand/collapse the FILTER PANE on a page (or report-wide when page omitted).
    /// Writes the "outspacePane" formatting object (properties visible / expanded, as expr-literal booleans)
    /// into the section.config.objects (page) or layout.config.objects (report) bucket.</summary>
    public object SetFilterPane(string reportSessionId, string? pageName, bool? visible, bool? expanded)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;

        // target the page config (if a page given) else the report-level config
        JsonObject host;
        Action<JsonObject> write;
        if (string.IsNullOrWhiteSpace(pageName))
        {
            host = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
            write = cfg => root["config"] = cfg.ToJsonString(JsonOpts);
        }
        else
        {
            var section = FindSection(root, pageName!);
            host = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
            write = cfg => section["config"] = cfg.ToJsonString(JsonOpts);
        }

        var objects = (host["objects"] as JsonObject) ?? new JsonObject();
        host["objects"] = objects;
        var props = new JsonObject();
        if (visible.HasValue) props["visible"] = Lit(visible.Value ? "true" : "false");
        if (expanded.HasValue) props["expanded"] = Lit(expanded.Value ? "true" : "false");
        if (props.Count == 0) throw new ArgumentException("set at least one of visible / expanded.");
        objects["outspacePane"] = new JsonArray { new JsonObject { ["properties"] = props } };

        write(host);
        session.Dirty = true;
        return new { ok = true, scope = string.IsNullOrWhiteSpace(pageName) ? "report" : "page", page = pageName, visible, expanded };
    }

    // ---- slicer sync (keep one field's selection in sync across pages) ----

    /// <summary>Add a slicer to a SYNC GROUP so the same field stays in sync across pages. Writes the
    /// singleVisual.syncGroup { groupName, fieldChanges, filterChanges } object (verified ground-truth shape).
    /// Slicers that share a groupName sync. groupName defaults to the slicer's field. The slicer must still be
    /// placed on each target page (clone_visual) for it to appear there - this wires the sync, not placement.</summary>
    public object SyncSlicer(string reportSessionId, string pageName, string slicerVisual, string? groupName, bool fieldChanges, bool filterChanges)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = FindVisual(section, slicerVisual);

        string vt = (string?)sv["visualType"] ?? "";
        if (!vt.Equals("slicer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"visual '{slicerVisual}' is a '{vt}', not a slicer - sync only applies to slicers.");

        // default the group name to the slicer's bound field so two slicers on the same field sync by default
        string gn = groupName ?? "";
        if (string.IsNullOrWhiteSpace(gn))
        {
            var sel = sv["prototypeQuery"]?["Select"] as JsonArray;
            if (sel is { Count: > 0 } && sel[0] is JsonObject s0)
                gn = (string?)(s0["Column"]?["Property"] ?? s0["Measure"]?["Property"]) ?? "Sync";
            else gn = "Sync";
        }

        sv["syncGroup"] = new JsonObject
        {
            ["groupName"] = gn,
            ["fieldChanges"] = fieldChanges,
            ["filterChanges"] = filterChanges,
        };
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page = pageName, slicer = slicerVisual, groupName = gn, fieldChanges, filterChanges };
    }

    // ---- the one shared filter/condition LITERAL TYPE-SUFFIX encoder -----------------------------
    // Every semantic-query literal Value carries its type as a suffix or quoting. Get this wrong and the
    // query parses to a blank white visual silently. This is the single authority every filter / condition
    // / TopN / aggregation builder routes through.
    //   double   -> "0D"        | int/long -> "10L"   | decimal -> "..M"
    //   string   -> 'EUR'       (single-quoted, internal ' doubled)
    //   bool     -> true|false  (bare, no quotes)
    //   datetime -> datetime'2024-01-01T00:00:00'
    //   color    -> '#FF0000'   (single-quoted string; a colour is just a hex string literal)
    //   null     -> null        (bare keyword)
    internal static string FormatLiteral(string value, string valueType)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        switch ((valueType ?? "").Trim().ToLowerInvariant())
        {
            case "int":
            case "integer":
            case "long":
                return $"{long.Parse(value, inv)}L";
            case "decimal":
            case "currency":
            case "money":
                return $"{decimal.Parse(value, System.Globalization.NumberStyles.Number, inv).ToString(inv)}M";
            case "double":
            case "number":
            case "float":
            case "real":
                return $"{double.Parse(value, System.Globalization.NumberStyles.Float, inv).ToString(inv)}D";
            case "bool":
            case "boolean":
            {
                string b = value.Trim().ToLowerInvariant();
                if (b is "true" or "1") return "true";
                if (b is "false" or "0") return "false";
                throw new ArgumentException($"bool literal must be true/false, got '{value}'.");
            }
            case "datetime":
            case "date":
            {
                // accept "2024-01-01" or a full ISO timestamp; emit datetime'yyyy-MM-ddTHH:mm:ss'.
                var dt = DateTime.Parse(value.Trim(), inv, System.Globalization.DateTimeStyles.RoundtripKind);
                return $"datetime'{dt:yyyy-MM-ddTHH:mm:ss}'";
            }
            case "color":
            case "colour":
                return $"'{NormalizeHex(value) ?? value}'";
            case "null":
                return "null";
            case "string":
            case "text":
                return $"'{value.Replace("'", "''")}'";
            default:
                // unknown type: default to double (the historical fallback) so existing callers are unchanged.
                return $"{double.Parse(value, System.Globalization.NumberStyles.Float, inv).ToString(inv)}D";
        }
    }

    /// <summary>Replace the field bindings (role projections + prototypeQuery) of an EXISTING visual - change which
    /// columns/measures it shows without deleting it, preserving position, formatting and conditional formatting.
    /// Clears the sort (OrderBy may reference a removed field) - re-apply with SetVisualSort.</summary>
    public object SetVisualFields(string reportSessionId, string pageName, string visualName, IReadOnlyList<FieldBinding> bindings)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = FindVisual(section, visualName);

        var tables = bindings.Select(b => b.Table).Distinct().ToList();
        var alias = tables.Select((t, i) => (t, a: "t" + (i + 1))).ToDictionary(p => p.t, p => p.a);

        var from = new JsonArray();
        foreach (var t in tables) from.Add(new JsonObject { ["Name"] = alias[t], ["Entity"] = t, ["Type"] = 0 });

        var select = new JsonArray();
        foreach (var b in bindings)
        {
            var expr = new JsonObject { ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = alias[b.Table] } }, ["Property"] = b.Field };
            var sel = new JsonObject();
            sel[b.Kind.Equals("measure", StringComparison.OrdinalIgnoreCase) ? "Measure" : "Column"] = expr;
            sel["Name"] = $"{b.Table}.{b.Field}";
            select.Add(sel);
        }

        var projections = new JsonObject();
        foreach (var grp in bindings.GroupBy(b => b.Role))
        {
            var arr = new JsonArray();
            foreach (var b in grp) arr.Add(new JsonObject { ["queryRef"] = $"{b.Table}.{b.Field}" });
            projections[grp.Key] = arr;
        }

        var pq = (sv["prototypeQuery"] as JsonObject) ?? new JsonObject { ["Version"] = 2 };
        pq["From"] = from;
        pq["Select"] = select;
        pq.Remove("OrderBy");
        sv["prototypeQuery"] = pq;
        sv["projections"] = projections;

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visualName, roles = bindings.Select(b => b.Role).Distinct().ToArray(), fields = bindings.Count };
    }

    // -------------------------------------------------------------------- styling (dimensionalism)
    /// <summary>Apply a "floating card" look to one visual: background fill, rounded corners, drop shadow, header on/off.</summary>
    public object StyleVisual(string reportSessionId, string pageName, string visualName,
        string? background, double? cornerRadius, bool? shadow, bool? showHeader, string? borderColor)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var arr = (section["visualContainers"] as JsonArray)!;
        foreach (var node in arr)
        {
            if (node is not JsonObject vc) continue;
            string? cfgStr = (string?)vc["config"];
            if (cfgStr == null || JsonNode.Parse(cfgStr) is not JsonObject co || (string?)co["name"] != visualName) continue;
            var sv = (co["singleVisual"] as JsonObject) ?? throw new InvalidOperationException("visual has no singleVisual.");
            var vcObjects = (sv["vcObjects"] as JsonObject) ?? new JsonObject();
            ApplyStyle(vcObjects, background, cornerRadius, shadow, showHeader, borderColor);
            sv["vcObjects"] = vcObjects;
            vc["config"] = co.ToJsonString(JsonOpts);
            session.Dirty = true;
            return new { ok = true, styled = visualName, page = pageName };
        }
        throw new InvalidOperationException($"Visual '{visualName}' not found on page '{pageName}'.");
    }

    /// <summary>Apply one consistent card style to every data visual on a page (the "make it beautiful" pass).</summary>
    public object StylePage(string reportSessionId, string pageName,
        string? background, double? cornerRadius, bool? shadow, bool? showHeader, string? borderColor)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var arr = (section["visualContainers"] as JsonArray)!;
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "textbox", "basicShape", "image", "actionButton", "shape" };
        int n = 0;
        foreach (var node in arr)
        {
            if (node is not JsonObject vc) continue;
            string? cfgStr = (string?)vc["config"];
            if (cfgStr == null || JsonNode.Parse(cfgStr) is not JsonObject co) continue;
            var sv = co["singleVisual"] as JsonObject;
            string? vt = (string?)sv?["visualType"];
            if (sv == null || vt == null || skip.Contains(vt)) continue;
            var vcObjects = (sv["vcObjects"] as JsonObject) ?? new JsonObject();
            ApplyStyle(vcObjects, background, cornerRadius, shadow, showHeader, borderColor);
            sv["vcObjects"] = vcObjects;
            vc["config"] = co.ToJsonString(JsonOpts);
            n++;
        }
        session.Dirty = true;
        return new { ok = true, page = pageName, styledVisuals = n };
    }

    // -------------------------------------------------------------------- flatten (clear visual styling)
    // The PURELY-DECORATIVE chrome keys we ALWAYS strip when flattening to a plain "non-premium" look.
    // 'title' and 'background' are handled separately because a deliberate custom title or a transparent
    // (overlay) background is MEANINGFUL formatting we must never destroy by default.
    private static readonly string[] DecorativeChromeKeys =
        { "border", "dropShadow", "stylePreset", "visualHeader", "visualHeaderTooltip" };

    // The full decorative set (chrome + title + background) - reported to the caller for transparency.
    private static readonly string[] DecorativeVcKeys =
        { "background", "border", "dropShadow", "title", "stylePreset", "visualHeader", "visualHeaderTooltip" };

    // Action/navigation keys that live in the SAME vcObjects bucket as the decorative styling and MUST
    // survive a flatten - deleting these silently breaks view-switch / page-nav buttons (label stays, click dies).
    private static readonly HashSet<string> ActionVcKeys =
        new(StringComparer.OrdinalIgnoreCase) { "visualLink", "bookmark", "navigation", "drill", "filter", "action" };

    /// <summary>True if a 'title' object carries DELIBERATE formatting we must preserve by default:
    /// a custom text literal (text property present) OR an explicit show literal (show set, true or false -
    /// e.g. a title hidden on an overlay line chart). A bare auto-title with neither is decorative chrome.</summary>
    private static bool IsDeliberateTitle(JsonNode? titleNode)
    {
        var props = FirstProps(titleNode);
        if (props is null) return false;
        return props.ContainsKey("text") || props.ContainsKey("show");
    }

    /// <summary>True if a 'background' object is an OVERLAY we must preserve by default: it carries a
    /// transparency property (a transparent/translucent background that makes the visual float over a chart
    /// or panel). A plain opaque background with no transparency is decorative chrome and is removed.</summary>
    private static bool IsOverlayBackground(JsonNode? bgNode)
    {
        var props = FirstProps(bgNode);
        return props is not null && props.ContainsKey("transparency");
    }

    /// <summary>The first entry's "properties" object of a formatting key (array-of-{properties} shape).</summary>
    private static JsonObject? FirstProps(JsonNode? objNode)
    {
        if (objNode is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject e0) return e0["properties"] as JsonObject;
        if (objNode is JsonObject eo) return eo["properties"] as JsonObject;
        return null;
    }

    /// <summary>
    /// THE flatten primitive. Strips the decorative vcObjects chrome (border, dropShadow, stylePreset,
    /// visualHeader/visualHeaderTooltip, and plain opaque backgrounds) from each targeted visual, and NEVER
    /// touches visualLink / bookmark / navigation / action keys or singleVisual.objects (data + conditional
    /// formatting). DELIBERATE formatting is preserved by default: a 'title' with a custom text or an explicit
    /// show literal, and a 'background' carrying a transparency (overlay) survive. removeTitles=true also
    /// removes such titles (the old aggressive behaviour, opt-in). actionButton visuals are skipped entirely.
    /// Mutates the visualContainers in place and re-stringifies each touched config.
    /// Returns (visualsTouched, decorativeKeysRemoved, actionKeysPreserved).
    /// </summary>
    internal static (int visualsTouched, int decorativeKeysRemoved, int actionKeysPreserved) FlattenVisualContainers(
        JsonArray containers, string? visualName = null, bool removeTitles = false)
    {
        int touched = 0, removed = 0, preserved = 0;
        foreach (var node in containers)
        {
            if (node is not JsonObject vc) continue;
            if ((string?)vc["config"] is not string cfgStr) continue;
            if (JsonNode.Parse(cfgStr) is not JsonObject co) continue;
            if (visualName != null && (string?)co["name"] != visualName) continue;
            if (co["singleVisual"] is not JsonObject sv) continue;

            // SKIP action buttons entirely - their visualLink is the whole point of the visual.
            string vt = (string?)sv["visualType"] ?? "";
            if (vt.Equals("actionButton", StringComparison.OrdinalIgnoreCase)) continue;

            if (sv["vcObjects"] is not JsonObject vcObjects) continue;

            int removedHere = 0;

            // 1. always-decorative chrome
            foreach (var key in DecorativeChromeKeys)
                if (vcObjects.ContainsKey(key)) { vcObjects.Remove(key); removedHere++; }

            // 2. title: remove ONLY a bare auto-title - keep a custom-text/explicit-show title unless removeTitles.
            if (vcObjects.ContainsKey("title") && (removeTitles || !IsDeliberateTitle(vcObjects["title"])))
            { vcObjects.Remove("title"); removedHere++; }

            // 3. background: remove ONLY a plain opaque background - keep a transparent (overlay) background.
            if (vcObjects.ContainsKey("background") && !IsOverlayBackground(vcObjects["background"]))
            { vcObjects.Remove("background"); removedHere++; }

            // count the action/nav keys we deliberately LEFT in place (so the caller can verify buttons survived)
            foreach (var kv in vcObjects)
                if (ActionVcKeys.Contains(kv.Key)) preserved++;

            if (removedHere == 0) continue;   // nothing decorative removed - leave the config byte-identical

            // if only decorative styling lived here, drop the now-empty bucket; otherwise keep the rest
            if (vcObjects.Count == 0) sv.Remove("vcObjects");

            vc["config"] = co.ToJsonString(JsonOpts);
            touched++;
            removed += removedHere;
        }
        return (touched, removed, preserved);
    }

    /// <summary>
    /// Flatten visuals to a plain "non-premium" look the SAFE way: remove the decorative chrome (border,
    /// dropShadow, stylePreset, visualHeader[Tooltip], plain opaque backgrounds) while PRESERVING deliberate
    /// formatting by default - a custom-text/explicit-show title, and a transparent (overlay) background -
    /// plus every action/navigation key (visualLink, bookmark, ...) and singleVisual.objects (data +
    /// conditional formatting). removeTitles=true also strips such titles. pageName null = all pages;
    /// visualName null = all visuals on the page. actionButton visuals are skipped.
    /// </summary>
    public object ClearVisualStyling(string reportSessionId, string? pageName, string? visualName, bool whiteBackground, bool removeTitles = false)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;

        IEnumerable<JsonObject> sections = pageName == null
            ? Sections(root).OfType<JsonObject>()
            : new[] { FindSection(root, pageName) };

        int touched = 0, removed = 0, preserved = 0, pages = 0;
        foreach (var section in sections)
        {
            pages++;
            if (section["visualContainers"] is JsonArray containers)
            {
                var (t, r, p) = FlattenVisualContainers(containers, visualName, removeTitles);
                touched += t; removed += r; preserved += p;
            }
            if (whiteBackground)
                SetSectionWhiteBackground(section);
        }

        session.Dirty = true;
        return new
        {
            ok = true,
            pages,
            visualsTouched = touched,
            decorativeKeysRemoved = removed,
            actionKeysPreserved = preserved,
            decorativeKeys = DecorativeVcKeys,
            removeTitles,
            whiteBackground,
        };
    }

    // Set the page (section) background to a solid #FFFFFF - the canvas half of a flatten pass.
    private static void SetSectionWhiteBackground(JsonObject section)
    {
        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var objects = (cfg["objects"] as JsonObject) ?? new JsonObject();
        objects["background"] = new JsonArray
        {
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["color"] = Lit("'#FFFFFF'", wrapSolid: true),
                    ["transparency"] = Lit("0D"),
                },
            },
        };
        cfg["objects"] = objects;
        section["config"] = cfg.ToJsonString(JsonOpts);
    }

    /// <summary>Add a decorative rounded rectangle (panel) - use behind related visuals for grouping + depth.</summary>
    public object AddShape(string reportSessionId, string pageName,
        double x, double y, double width, double height, string? fill, double cornerRadius, bool shadow, string? lineColor)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var containers = (section["visualContainers"] as JsonArray)!;
        string visualName = Guid.NewGuid().ToString("N");
        var inv = System.Globalization.CultureInfo.InvariantCulture;

        var objects = new JsonObject
        {
            ["shape"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject {
                ["tileShape"] = Lit("'rectangle'"),
                ["roundEdge"] = Lit(cornerRadius.ToString(inv) + "D"),
            } } },
            ["fill"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject {
                ["show"] = Lit(fill != null ? "true" : "false"),
                ["fillColor"] = Lit($"'{fill ?? "#FFFFFF"}'", wrapSolid: true),
                ["transparency"] = Lit("0D"),
            } } },
            ["outline"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject {
                ["show"] = Lit(lineColor != null ? "true" : "false"),
                ["lineColor"] = Lit($"'{lineColor ?? "#E6E9EF"}'", wrapSolid: true),
                ["weight"] = Lit("1D"),
            } } },
        };
        var sv = new JsonObject { ["visualType"] = "basicShape", ["objects"] = objects, ["drillFilterOtherVisuals"] = true };
        if (shadow)
            sv["vcObjects"] = new JsonObject { ["dropShadow"] = new JsonArray { new JsonObject {
                ["properties"] = new JsonObject { ["show"] = Lit("true"), ["preset"] = Lit("'BottomRight'") } } } };

        AddContainer(containers, visualName, sv, x, y, 0, width, height);
        session.Dirty = true;
        return new { ok = true, visualName, page = pageName, type = "basicShape" };
    }

    // a formatting object bag entry: [{ "properties": { ... } }]
    private static JsonArray OneProps(params (string k, JsonNode v)[] props)
    {
        var pr = new JsonObject();
        foreach (var (k, v) in props) pr[k] = v;
        return new JsonArray { new JsonObject { ["properties"] = pr } };
    }

    // vcObjects that make a visual fully transparent + chrome-free (sit cleanly on a panel/banner)
    private static JsonObject FlatVc() => new()
    {
        ["background"] = OneProps(("show", Lit("false"))),
        ["border"] = OneProps(("show", Lit("false"))),
        ["dropShadow"] = OneProps(("show", Lit("false"))),
        ["visualHeader"] = OneProps(("show", Lit("false"))),
        ["title"] = OneProps(("show", Lit("false"))),   // kill the "Measure by Category" auto-title
    };

    /// <summary>
    /// A premium KPI card composed from proven primitives: a white rounded panel (shadow) with an
    /// uppercase label, a big value, an optional coloured delta ("vs LY") and an optional sparkline.
    /// Every inner visual is transparent so it floats on the one panel - reads as a single modern card.
    /// </summary>
    public object AddKpiCard(string reportSessionId, string pageName, string table, string valueMeasure, string label,
        double x, double y, double width, double height, string? deltaMeasure, string? trendTable, string? trendMeasure,
        string? dateTable, string? dateColumn, string accentColor, string valueColor)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var containers = (section["visualContainers"] as JsonArray)!;
        double pad = 14;
        bool hasDelta = !string.IsNullOrWhiteSpace(deltaMeasure);
        bool hasSpark = !string.IsNullOrWhiteSpace(trendMeasure) && !string.IsNullOrWhiteSpace(dateTable) && !string.IsNullOrWhiteSpace(dateColumn);

        // 1. panel + 2. label (proven public builders)
        AddShape(reportSessionId, pageName, x, y, width, height, "#FFFFFF", 12, true, null);
        AddTextbox(reportSessionId, pageName, label, x + pad, y + 10, width - 2 * pad, 16, 10, false, "#7A8AA0", "left");

        // 3. big value (transparent card)
        var valSv = BuildSingleVisual("card", new[] { new FieldBinding("Values", table, valueMeasure, "measure") }, null);
        valSv["objects"] = new JsonObject
        {
            ["labels"] = OneProps(("color", Lit($"'{valueColor}'", true)), ("fontSize", Lit("30D"))),
            ["categoryLabels"] = OneProps(("show", Lit("false"))),
        };
        valSv["vcObjects"] = FlatVc();
        AddContainer(containers, Guid.NewGuid().ToString("N"), valSv, x + pad - 6, y + 28, 10, width - 2 * pad + 12, 50);

        // 4. delta ("vs LY") - coloured card + caption
        if (hasDelta)
        {
            var dSv = BuildSingleVisual("card", new[] { new FieldBinding("Values", table, deltaMeasure!, "measure") }, null);
            dSv["objects"] = new JsonObject
            {
                ["labels"] = OneProps(("color", Lit($"'{accentColor}'", true)), ("fontSize", Lit("13D"))),
                ["categoryLabels"] = OneProps(("show", Lit("false"))),
            };
            dSv["vcObjects"] = FlatVc();
            AddContainer(containers, Guid.NewGuid().ToString("N"), dSv, x + pad - 6, y + 76, 11, 96, 26);
            AddTextbox(reportSessionId, pageName, "vs LY", x + pad + 74, y + 82, 70, 16, 9, false, "#9AA5B1", "left");
        }

        // 5. sparkline (transparent line, no chrome)
        if (hasSpark)
        {
            var sSv = BuildSingleVisual("lineChart", new[]
            {
                new FieldBinding("Category", dateTable!, dateColumn!, "column"),
                new FieldBinding("Y", string.IsNullOrWhiteSpace(trendTable) ? table : trendTable!, trendMeasure!, "measure"),
            }, null);
            sSv["objects"] = new JsonObject
            {
                ["categoryAxis"] = OneProps(("show", Lit("false"))),
                ["valueAxis"] = OneProps(("show", Lit("false"))),
                ["legend"] = OneProps(("show", Lit("false"))),
                ["dataPoint"] = OneProps(("defaultColor", Lit($"'{accentColor}'", true))),
            };
            sSv["vcObjects"] = FlatVc();
            AddContainer(containers, Guid.NewGuid().ToString("N"), sSv, x + pad - 8, y + height - 42, 12, width - 2 * pad + 16, 36);
        }

        session.Dirty = true;
        return new { ok = true, page = pageName, kpi = label, valueMeasure, delta = hasDelta, sparkline = hasSpark };
    }

    private static string NameOf(object r, string prop) =>
        (string)(r.GetType().GetProperty(prop)!.GetValue(r)!);

    /// <summary>
    /// RECIPE: build a complete, premium "Executive" dashboard page in ONE call from a JSON config -
    /// brand theme, navy banner + gold seam, a slicer filter-bar, a row of premium KPI cards, a hero
    /// trend, and a "by segment" bar chart whose colour encodes growth. Point it at any client model.
    /// </summary>
    public object BuildExecutiveReport(string reportSessionId, string configJson)
    {
        var cfg = JsonNode.Parse(configJson) as JsonObject ?? throw new InvalidOperationException("config is not a JSON object.");
        string S(string k, string def = "") => (string?)cfg[k] ?? def;
        string title = S("title", "Report"), subtitle = S("subtitle"), headline = S("headline"), headlineLabel = S("headlineLabel");
        string brand = S("brandColor", "#16365C"), accent = S("accentColor", "#C9A227");
        string fact = S("factTable"), dateTable = S("dateTable"), dateCol = S("dateColumn"), trend = S("trendMeasure");
        string segTable = S("segmentTable"), segCol = S("segmentColumn"), segVal = S("segmentValueMeasure"), growth = S("growthMeasure");
        string logo = S("logoPath");
        bool hasLogo = !string.IsNullOrWhiteSpace(logo) && File.Exists(logo);
        double W = Num(cfg["canvasWidth"]) ?? 1280;
        double H = Num(cfg["canvasHeight"]) ?? 720;
        const double M = 24;
        double usable = W - 2 * M, gut = 16;

        // brand theme: palette from the logo if supplied, else the brand colour
        GenerateTheme(reportSessionId, title + " Brand", brand, null, "executive", "Segoe UI", false, 10, true, false, true, hasLogo ? logo : null);
        ClearPages(reportSessionId);
        string pg = NameOf(AddPage(reportSessionId, "Executive", (int)W, (int)H), "pageName");
        SetPageBackground(reportSessionId, pg, "#EDF1F6", 0);

        // banner + gold seam, full width (logo mark + wordmark beside it; empty title = logo only)
        AddShape(reportSessionId, pg, M, 20, usable, 84, brand, 16, true, null);
        AddShape(reportSessionId, pg, M, 101, usable, 3, accent, 0, false, null);
        if (hasLogo)
        {
            AddImage(reportSessionId, pg, logo, M + 20, 26, 50, 50, "Fit");
            if (title != "") AddTextbox(reportSessionId, pg, title, M + 84, 27, 460, 42, 26, true, "#FFFFFF", "left");
        }
        else if (title != "") AddTextbox(reportSessionId, pg, title, M + 22, 27, 520, 42, 28, true, "#FFFFFF", "left");
        double textLeft = hasLogo ? M + 84 : M + 24;
        if (subtitle != "") AddTextbox(reportSessionId, pg, subtitle, textLeft, 72, 720, 20, 11, false, "#A9C2DE", "left");
        if (headlineLabel != "") AddTextbox(reportSessionId, pg, headlineLabel, W - M - 444, 28, 420, 16, 10, false, "#8FB0D6", "right");
        if (headline != "") AddTextbox(reportSessionId, pg, headline, W - M - 444, 46, 420, 34, 20, true, "#FFFFFF", "right");

        // slicer filter-bar
        var slicers = cfg["slicers"] as JsonArray ?? new JsonArray();
        int sc = Math.Max(1, slicers.Count);
        double sw = (usable - (sc - 1) * gut) / sc;   // distribute slicers across the full width
        double sxp = M;
        foreach (var sNode in slicers)
        {
            if (sNode is not JsonObject s) continue;
            var r = AddVisual(reportSessionId, pg, "slicer", sxp, 120, 0, sw, 60,
                new[] { new FieldBinding("Values", (string?)s["table"] ?? "", (string?)s["column"] ?? "", "column") },
                (string?)s["title"] ?? (string?)s["column"], "Dropdown");
            StyleVisual(reportSessionId, pg, NameOf(r, "visualName"), "#FFFFFF", 8, true, false, "#E6E9EF");
            sxp += sw + gut;
        }

        // section header + premium KPI row
        AddTextbox(reportSessionId, pg, "KEY METRICS", M, 184, 300, 14, 10, true, "#5B7494", "left");
        var kpis = cfg["kpis"] as JsonArray ?? new JsonArray();
        int n = Math.Max(1, kpis.Count);
        double kw = (usable - (n - 1) * gut) / n, kx = M, ky = 218, kh = 150;
        foreach (var kNode in kpis)
        {
            if (kNode is not JsonObject k) continue;
            string? kt = (string?)k["trend"];
            bool spark = !string.IsNullOrWhiteSpace(kt);
            AddKpiCard(reportSessionId, pg, fact, (string?)k["measure"] ?? "", (string?)k["label"] ?? "", kx, ky, kw, kh,
                (string?)k["delta"], fact, kt, spark ? dateTable : null, spark ? dateCol : null, "#1B8A4B", brand);
            kx += kw + gut;
        }

        // optional auto-narrative insight strip (bind a card to a narrative measure added via add_narrative_measure)
        double afterKpis = ky + kh + 8;
        string narrative = S("narrativeMeasure");
        if (narrative != "")
        {
            string nn = NameOf(AddVisual(reportSessionId, pg, "card", M, afterKpis, 0, usable, 40,
                new[] { new FieldBinding("Values", fact, narrative, "measure") }, null), "visualName");
            StyleVisual(reportSessionId, pg, nn, "#FFFFFF", 8, true, false, "#E6E9EF");
            SetVisualProperty(reportSessionId, pg, nn, "labels", "color", "#16365C", "color", "objects");
            SetVisualProperty(reportSessionId, pg, nn, "labels", "fontSize", "13", "number", "objects");
            SetVisualProperty(reportSessionId, pg, nn, "categoryLabels", "show", "false", "bool", "objects");
            afterKpis += 52;
        }

        double headerY = afterKpis, cy = headerY + 22, ch = H - cy - M;

        // if the config supplies VIEWS, build a button-switched multi-view content area
        var views = cfg["views"] as JsonArray;
        if (views is { Count: > 0 })
        {
            AddTextbox(reportSessionId, pg, "PERFORMANCE", M, headerY + 12, 360, 14, 10, true, "#5B7494", "left");
            double vcy = headerY + 44, vch = H - vcy - M;   // clear band above the chart for the button bar
            var viewSpecs = new JsonArray();
            foreach (var vNode in views)
            {
                if (vNode is not JsonObject vo) continue;
                var chartsArr = vo["charts"] as JsonArray ?? new JsonArray();
                var names = new JsonArray();
                int cc = chartsArr.Count;
                for (int ci = 0; ci < cc; ci++)
                {
                    if (chartsArr[ci] is not JsonObject ch2) continue;
                    double hw = Math.Round(usable * 0.62);
                    double cwid = cc == 1 ? usable : (ci == 0 ? hw : usable - hw - gut);
                    double cxx = ci == 0 ? M : M + hw + gut;
                    var binds = new[]
                    {
                        new FieldBinding("Category", (string?)ch2["categoryTable"] ?? "", (string?)ch2["categoryField"] ?? "", "column"),
                        new FieldBinding("Y", (string?)ch2["valueTable"] ?? fact, (string?)ch2["valueMeasure"] ?? "", "measure"),
                    };
                    string cn = NameOf(AddVisual(reportSessionId, pg, (string?)ch2["type"] ?? "lineChart", cxx, vcy, 0, cwid, vch, binds, (string?)ch2["title"]), "visualName");
                    StyleVisual(reportSessionId, pg, cn, "#FFFFFF", 10, true, false, "#E6E9EF");
                    SetVisualProperty(reportSessionId, pg, cn, "legend", "show", "false", "bool", "objects");
                    if ((string?)ch2["growthMeasure"] is string cg && cg != "")
                        SetConditionalFormatting(reportSessionId, pg, cn, "dataPoint", "fill", fact, cg, "#C0504D", "#1B8A4B", "#F4F4F4", -0.05, 0.05, 0, null);
                    names.Add(cn);
                }
                viewSpecs.Add(new JsonObject { ["name"] = (string?)vo["name"] ?? "View", ["visuals"] = names });
            }
            int nb = viewSpecs.Count; double btnW = 130, btnGap = 8;
            double bxx = W - M - (nb * btnW + (nb - 1) * btnGap);
            AddViewSwitcher(reportSessionId, pg, viewSpecs.ToJsonString(JsonOpts), bxx, headerY + 6, btnW, 30, btnGap, "#5B7494", brand, "#FFFFFF");
            return new { ok = true, page = pg, recipe = "executive-views", canvas = $"{(int)W}x{(int)H}", kpiCards = kpis.Count, slicers = slicers.Count, views = nb };
        }

        // default single view: hero trend + by-segment bars (fill remaining height)
        AddTextbox(reportSessionId, pg, "TRENDS & PERFORMANCE", M, headerY, 360, 14, 10, true, "#5B7494", "left");
        double heroW = Math.Round(usable * 0.62), segW = usable - heroW - gut;
        var heroR = AddVisual(reportSessionId, pg, "lineChart", M, cy, 0, heroW, ch,
            new[] { new FieldBinding("Category", dateTable, dateCol, "column"), new FieldBinding("Y", fact, trend, "measure") }, "Sales Trend");
        string hero = NameOf(heroR, "visualName");
        StyleVisual(reportSessionId, pg, hero, "#FFFFFF", 10, true, false, "#E6E9EF");
        SetVisualProperty(reportSessionId, pg, hero, "dataPoint", "defaultColor", brand, "color", "objects");
        SetVisualProperty(reportSessionId, pg, hero, "valueAxis", "labelDisplayUnits", "1000000D", "raw", "objects");
        SetVisualProperty(reportSessionId, pg, hero, "legend", "show", "false", "bool", "objects");
        var segR = AddVisual(reportSessionId, pg, "clusteredBarChart", M + heroW + gut, cy, 0, segW, ch,
            new[] { new FieldBinding("Category", segTable, segCol, "column"), new FieldBinding("Y", fact, segVal, "measure") }, "Sales by Segment   (colour = growth)");
        string seg = NameOf(segR, "visualName");
        StyleVisual(reportSessionId, pg, seg, "#FFFFFF", 10, true, false, "#E6E9EF");
        SetVisualProperty(reportSessionId, pg, seg, "legend", "show", "false", "bool", "objects");
        if (growth != "")
            SetConditionalFormatting(reportSessionId, pg, seg, "dataPoint", "fill", fact, growth, "#C0504D", "#1B8A4B", "#F4F4F4", -0.05, 0.05, 0, null);

        return new { ok = true, page = pg, recipe = "executive", canvas = $"{(int)W}x{(int)H}", kpiCards = kpis.Count, slicers = slicers.Count };
    }

    /// <summary>
    /// RECIPE: a Total-Market compare page across two retailers/panels. Pairs with conform_dimension -
    /// point it at the conformed dimension + each retailer's sales measure and it builds the banner,
    /// slicer bar, a KPI row (Total Market / Retailer A / Retailer B), a by-entity table and an
    /// A-vs-B grouped bar. clearPages:false + pagePrefix to append it beside a client's existing pages.
    /// </summary>
    public object BuildCrossRetailerCompare(string reportSessionId, string configJson)
    {
        var cfg = JsonNode.Parse(configJson) as JsonObject ?? throw new InvalidOperationException("config is not a JSON object.");
        string S(string k, string def = "") => (string?)cfg[k] ?? def;
        string title = S("title", "Total Market"), subtitle = S("subtitle"), headline = S("headline"), headlineLabel = S("headlineLabel");
        string brand = S("brandColor", "#16365C"), accent = S("accentColor", "#C9A227"), logo = S("logoPath");
        string cmpT = S("compareTable"), cmpC = S("compareColumn");
        var rA = cfg["retailerA"] as JsonObject ?? new JsonObject();
        var rB = cfg["retailerB"] as JsonObject ?? new JsonObject();
        var tot = cfg["totalMeasure"] as JsonObject;
        string aLabel = (string?)rA["label"] ?? "Retailer A", aTable = (string?)rA["table"] ?? cmpT, aMeas = (string?)rA["measure"] ?? "";
        string bLabel = (string?)rB["label"] ?? "Retailer B", bTable = (string?)rB["table"] ?? cmpT, bMeas = (string?)rB["measure"] ?? "";
        string totTable = (string?)tot?["table"] ?? aTable, totMeas = (string?)tot?["measure"] ?? "";
        bool hasTot = !string.IsNullOrWhiteSpace(totMeas);
        var slicers = cfg["slicers"] as JsonArray ?? new JsonArray();
        double W = Num(cfg["canvasWidth"]) ?? 1280, H = Num(cfg["canvasHeight"]) ?? 720;
        const double M = 24; double usable = W - 2 * M, gut = 16;
        bool clearPages = (bool?)cfg["clearPages"] ?? true;
        string pp = S("pagePrefix"), logoPath = !string.IsNullOrWhiteSpace(logo) && File.Exists(logo) ? logo : "";

        if (clearPages)
        {
            GenerateTheme(reportSessionId, title + " Brand", brand, null, "executive", "Segoe UI", false, 10, true, false, true, logoPath == "" ? null : logoPath);
            ClearPages(reportSessionId);
        }
        string pg = NameOf(AddPage(reportSessionId, pp + "Total Market", (int)W, (int)H), "pageName");
        SetPageBackground(reportSessionId, pg, "#EDF1F6", 0);
        BuildBanner(reportSessionId, pg, title, subtitle, headline, headlineLabel, brand, accent, logoPath == "" ? null : logoPath, W, M, usable);
        BuildSlicerBar(reportSessionId, pg, slicers, M, usable, gut, 156, 56);
        string Card(object r) { string n = NameOf(r, "visualName"); StyleVisual(reportSessionId, pg, n, "#FFFFFF", 10, true, false, "#E6E9EF"); return n; }

        // KPI row: Total Market (if supplied) + each retailer
        var cards = new List<(string label, string table, string meas, string color)>();
        if (hasTot) cards.Add(("TOTAL MARKET", totTable, totMeas, brand));
        cards.Add((aLabel.ToUpperInvariant(), aTable, aMeas, brand));
        cards.Add((bLabel.ToUpperInvariant(), bTable, bMeas, "#5B7494"));
        AddTextbox(reportSessionId, pg, "MARKET SIZE", M, 184, 300, 14, 10, true, "#5B7494", "left");
        int n = cards.Count; double kw = (usable - (n - 1) * gut) / n, kx = M, ky = 218, kh = 120;
        foreach (var (label, tbl, meas, color) in cards)
        {
            AddKpiCard(reportSessionId, pg, tbl, meas, label, kx, ky, kw, kh, null, null, null, null, null, accent, color);
            kx += kw + gut;
        }

        // content: by-entity table (left) + A-vs-B grouped bar (right)
        double cy = ky + kh + 26, ch = H - cy - M;
        AddTextbox(reportSessionId, pg, $"{aLabel} vs {bLabel} by {cmpC}", M, cy - 22, 700, 14, 10, true, "#5B7494", "left");
        double tW = Math.Round(usable * 0.5), barW = usable - tW - gut, barX = M + tW + gut;
        var tbinds = new List<FieldBinding> { new("Values", cmpT, cmpC, "column"), new("Values", aTable, aMeas, "measure"), new("Values", bTable, bMeas, "measure") };
        if (hasTot) tbinds.Add(new("Values", totTable, totMeas, "measure"));
        Card(AddVisual(reportSessionId, pg, "tableEx", M, cy, 0, tW, ch, tbinds, $"Total Market by {cmpC}"));
        var bbinds = new[] { new FieldBinding("Category", cmpT, cmpC, "column"), new FieldBinding("Y", aTable, aMeas, "measure"), new FieldBinding("Y", bTable, bMeas, "measure") };
        string bar = Card(AddVisual(reportSessionId, pg, "clusteredBarChart", barX, cy, 0, barW, ch, bbinds, $"{aLabel} vs {bLabel}"));
        SetVisualProperty(reportSessionId, pg, bar, "legend", "show", "true", "bool", "objects");

        try { AutoMobileLayout(reportSessionId, pg); } catch { }
        return new { ok = true, page = pg, recipe = "crossretailer", canvas = $"{(int)W}x{(int)H}", retailers = new[] { aLabel, bLabel }, totalMarket = hasTot, appended = !clearPages };
    }

    /// <summary>
    /// RECIPE: a flexible GRID dashboard - compose ANY visuals from a config so the engine can serve
    /// every category. Each entry in "visuals" is {type, title, span (1-12), rows, category, series,
    /// values:[...]}; they flow into a 12-column grid that fills the canvas. Supports the full chart
    /// palette (column/bar/line/area/pie/donut/funnel/ribbon/combo + scatter/treemap) and tables/cards.
    /// clearPages:false + pagePrefix to append; this is the open-ended composer behind the template library.
    /// </summary>
    public object BuildGridReport(string reportSessionId, string configJson)
    {
        var cfg = JsonNode.Parse(configJson) as JsonObject ?? throw new InvalidOperationException("config is not a JSON object.");
        string S(string k, string def = "") => (string?)cfg[k] ?? def;
        string title = S("title", "Dashboard"), subtitle = S("subtitle"), headline = S("headline"), headlineLabel = S("headlineLabel");
        string brand = S("brandColor", "#16365C"), accent = S("accentColor", "#C9A227"), logo = S("logoPath");
        var slicers = cfg["slicers"] as JsonArray ?? new JsonArray();
        var visuals = cfg["visuals"] as JsonArray ?? new JsonArray();
        double W = Num(cfg["canvasWidth"]) ?? 1280, H = Num(cfg["canvasHeight"]) ?? 720;
        const double M = 24; double usable = W - 2 * M, gut = 14;
        bool clearPages = (bool?)cfg["clearPages"] ?? true;
        string pp = S("pagePrefix"), pageName = S("pageName", "Dashboard");
        string logoPath = !string.IsNullOrWhiteSpace(logo) && File.Exists(logo) ? logo : "";

        if (clearPages)
        {
            GenerateTheme(reportSessionId, title + " Brand", brand, null, "executive", "Segoe UI", false, 10, true, false, true, logoPath == "" ? null : logoPath);
            ClearPages(reportSessionId);
        }
        string pg = NameOf(AddPage(reportSessionId, pp + pageName, (int)W, (int)H), "pageName");
        SetPageBackground(reportSessionId, pg, "#EDF1F6", 0);
        BuildBanner(reportSessionId, pg, title, subtitle, headline, headlineLabel, brand, accent, logoPath == "" ? null : logoPath, W, M, usable);
        bool hasSlicers = slicers.Count > 0;
        if (hasSlicers) BuildSlicerBar(reportSessionId, pg, slicers, M, usable, gut, 156, 56);
        string Card(object r) { string n = NameOf(r, "visualName"); StyleVisual(reportSessionId, pg, n, "#FFFFFF", 10, true, false, "#E6E9EF"); return n; }

        double contentY = hasSlicers ? 226 : 124;
        double contentH = H - contentY - M;
        double colW = usable / 12.0;

        // layout pass: flow each visual into a 12-column grid
        var placed = new List<(JsonObject v, int col, int rowTop, int span, int hU)>();
        int curCol = 0, curRowTop = 0, curRowMaxH = 0;
        foreach (var vn in visuals)
        {
            if (vn is not JsonObject vo) continue;
            int span = Math.Clamp((int)(Num(vo["span"]) ?? 6), 1, 12);
            int hU = Math.Clamp((int)(Num(vo["rows"]) ?? 2), 1, 8);
            if (curCol + span > 12) { curRowTop += curRowMaxH; curCol = 0; curRowMaxH = 0; }
            placed.Add((vo, curCol, curRowTop, span, hU));
            curCol += span; curRowMaxH = Math.Max(curRowMaxH, hU);
        }
        // per-row pixel heights: a card-only row gets a fixed comfortable height (so the value never
        // clips); chart rows share the remaining height proportionally to their row spans.
        const double CardRowH = 100;
        var rowKeys = placed.Select(p => p.rowTop).Distinct().OrderBy(r => r).ToList();
        var rowIsCard = new Dictionary<int, bool>(); var rowUnits = new Dictionary<int, int>();
        double sumCardH = 0; int sumChartUnits = 0;
        foreach (var rt in rowKeys)
        {
            var inRow = placed.Where(p => p.rowTop == rt).ToList();
            bool isCard = inRow.All(p => ((string?)p.v["type"] ?? "").ToLowerInvariant().Contains("card"));
            int units = inRow.Max(p => p.hU);
            rowIsCard[rt] = isCard; rowUnits[rt] = units;
            if (isCard) sumCardH += CardRowH; else sumChartUnits += units;
        }
        double remaining = Math.Max(160, contentH - sumCardH);
        double chartUnit = sumChartUnits > 0 ? remaining / sumChartUnits : remaining;
        var rowTopPx = new Dictionary<int, double>(); var rowHpx = new Dictionary<int, double>();
        double cyPx = contentY;
        foreach (var rt in rowKeys)
        {
            rowTopPx[rt] = cyPx;
            double hpx = rowIsCard[rt] ? CardRowH : rowUnits[rt] * chartUnit;
            rowHpx[rt] = hpx; cyPx += hpx;
        }

        int built = 0;
        foreach (var (vo, col, rowTop, span, hU) in placed)
        {
            string type = (string?)vo["type"] ?? "clusteredColumnChart";
            string lt = type.ToLowerInvariant();
            double x = M + col * colW, y = rowTopPx[rowTop];
            double w = span * colW - gut, h = rowHpx[rowTop] - gut;
            var binds = GridBinds(type, vo);
            if (binds.Count == 0) continue;
            string n = Card(AddVisual(reportSessionId, pg, type, x, y, 0, w, h, binds, (string?)vo["title"]));
            if (lt.Contains("card"))
                try
                {
                    SetVisualProperty(reportSessionId, pg, n, "categoryLabels", "show", "false", "bool", "objects");
                    SetVisualProperty(reportSessionId, pg, n, "labels", "fontSize", "26", "number", "objects");
                    SetVisualProperty(reportSessionId, pg, n, "labels", "color", brand, "color", "objects");
                }
                catch { }
            else if (lt.Contains("chart") && !lt.Contains("line") && !lt.Contains("combo"))
                try { SetVisualProperty(reportSessionId, pg, n, "dataPoint", "defaultColor", brand, "color", "objects"); } catch { }
            built++;
        }

        try { AutoMobileLayout(reportSessionId, pg); } catch { }
        return new { ok = true, page = pg, recipe = "grid", canvas = $"{(int)W}x{(int)H}", visuals = built, slicers = slicers.Count, appended = !clearPages };
    }

    /// <summary>Map a grid visual config to field bindings with the right role names per visual type.</summary>
    private static List<FieldBinding> GridBinds(string type, JsonObject vo)
    {
        var b = new List<FieldBinding>();
        string t = type.ToLowerInvariant();
        JsonObject? cat = vo["category"] as JsonObject;
        JsonObject? ser = vo["series"] as JsonObject;
        var vals = vo["values"] as JsonArray ?? new JsonArray();
        string? cT = (string?)cat?["table"], cC = (string?)cat?["column"];
        string? sT = (string?)ser?["table"], sC = (string?)ser?["column"];
        IEnumerable<(string tbl, string fld)> Vals() => vals.OfType<JsonObject>()
            .Select(v => ((string?)v["table"] ?? "", (string?)v["measure"] ?? (string?)v["column"] ?? ""))
            .Where(p => p.Item2.Length > 0);

        if (t is "tableex" || t.Contains("table") || t.Contains("pivot") || t.Contains("matrix"))
        {
            if (cC != null) b.Add(new(t.Contains("pivot") || t.Contains("matrix") ? "Rows" : "Values", cT!, cC, "column"));
            foreach (var (vt, vf) in Vals()) b.Add(new("Values", vt, vf, "measure"));
        }
        else if (t.Contains("card") || t == "kpi")
        {
            foreach (var (vt, vf) in Vals()) b.Add(new("Values", vt, vf, "measure"));
        }
        else if (t.Contains("scatter"))
        {
            var vl = Vals().ToList();
            if (vl.Count >= 1) b.Add(new("X", vl[0].tbl, vl[0].fld, "measure"));
            if (vl.Count >= 2) b.Add(new("Y", vl[1].tbl, vl[1].fld, "measure"));
            if (vl.Count >= 3) b.Add(new("Size", vl[2].tbl, vl[2].fld, "measure"));
            if (cC != null) b.Add(new("Details", cT!, cC, "column"));
        }
        else if (t == "treemap")
        {
            if (cC != null) b.Add(new("Group", cT!, cC, "column"));
            foreach (var (vt, vf) in Vals()) b.Add(new("Values", vt, vf, "measure"));
        }
        else
        {
            // standard charts: column / bar / line / area / pie / donut / funnel / ribbon / combo
            if (cC != null) b.Add(new("Category", cT!, cC, "column"));
            foreach (var (vt, vf) in Vals()) b.Add(new("Y", vt, vf, "measure"));
            if (sC != null) b.Add(new("Series", sT!, sC, "column"));
            if (vo["lineValues"] is JsonArray lv)
                foreach (var v in lv.OfType<JsonObject>())
                    b.Add(new("Y2", (string?)v["table"] ?? "", (string?)v["measure"] ?? "", "measure"));
        }
        return b;
    }

    // ---- shared recipe chrome (used by both recipes) ----
    private void BuildBanner(string sid, string pg, string title, string subtitle, string headline, string headlineLabel,
        string brand, string accent, string? logo, double W, double M, double usable)
    {
        bool hasLogo = !string.IsNullOrWhiteSpace(logo) && File.Exists(logo);
        AddShape(sid, pg, M, 20, usable, 84, brand, 16, true, null);
        AddShape(sid, pg, M, 101, usable, 3, accent, 0, false, null);
        if (hasLogo)
        {
            AddImage(sid, pg, logo!, M + 20, 26, 50, 50, "Fit");
            if (title != "") AddTextbox(sid, pg, title, M + 84, 27, 460, 42, 26, true, "#FFFFFF", "left");
        }
        else if (title != "") AddTextbox(sid, pg, title, M + 22, 27, 520, 42, 28, true, "#FFFFFF", "left");
        double textLeft = hasLogo ? M + 84 : M + 24;
        if (subtitle != "") AddTextbox(sid, pg, subtitle, textLeft, 72, 720, 20, 11, false, "#A9C2DE", "left");
        if (headlineLabel != "") AddTextbox(sid, pg, headlineLabel, W - M - 444, 28, 420, 16, 10, false, "#8FB0D6", "right");
        if (headline != "") AddTextbox(sid, pg, headline, W - M - 444, 46, 420, 34, 20, true, "#FFFFFF", "right");
    }

    private void BuildSlicerBar(string sid, string pg, JsonArray slicers, double M, double usable, double gut, double y, double h)
    {
        int sc = Math.Max(1, slicers.Count);
        double sw = (usable - (sc - 1) * gut) / sc, sxp = M;
        foreach (var sNode in slicers)
        {
            if (sNode is not JsonObject s) continue;
            var r = AddVisual(sid, pg, "slicer", sxp, y, 0, sw, h,
                new[] { new FieldBinding("Values", (string?)s["table"] ?? "", (string?)s["column"] ?? "", "column") },
                (string?)s["title"] ?? (string?)s["column"], "Dropdown");
            StyleVisual(sid, pg, NameOf(r, "visualName"), "#FFFFFF", 8, true, false, "#E6E9EF");
            sxp += sw + gut;
        }
    }

    private void BuildNavBar(string sid, string pg, List<(string label, string page)> pages, int current,
        double x, double y, double btnW, double btnH, double gap, string fill, string activeFill, string textColor)
    {
        double bx = x;
        for (int i = 0; i < pages.Count; i++)
        {
            AddNavButton(sid, pg, pages[i].label, pages[i].page, bx, y, btnW, btnH, i == current ? activeFill : fill, textColor);
            bx += btnW + gap;
        }
    }

    /// <summary>
    /// RECIPE 2: a beautified multi-page CATEGORY REVIEW (the standard scan template) - Performance,
    /// Price &amp; Volume, Share and Distribution pages, branded, themed and nav-linked. Conditionally
    /// formatted matrices, a price/volume combo chart and a share breakdown. One call, four pages.
    /// </summary>
    public object BuildCategoryReport(string reportSessionId, string configJson)
    {
        var cfg = JsonNode.Parse(configJson) as JsonObject ?? throw new InvalidOperationException("config is not a JSON object.");
        string S(string k, string def = "") => (string?)cfg[k] ?? def;
        string title = S("title", "Report"), subtitle = S("subtitle"), headline = S("headline"), headlineLabel = S("headlineLabel");
        string brand = S("brandColor", "#16365C"), accent = S("accentColor", "#C9A227"), logo = S("logoPath");
        string fact = S("factTable"), brandT = S("brandTable"), brandC = S("brandColumn");
        string segT = S("segmentTable"), segC = S("segmentColumn"), dateT = S("dateTable"), dateC = S("dateColumn");
        var mm = cfg["measures"] as JsonObject ?? new JsonObject();
        string Mz(string k, string def) => (string?)mm[k] ?? def;
        string mSales = Mz("sales", "Sales (Period)"), mVol = Mz("volume", "Volume (Period)"), mPrice = Mz("price", "Avg Price (Period)"),
            mGrowth = Mz("growth", "Sales Growth %"), mTrend = Mz("salesTrend", "Sales (window)");
        string? mDist = (string?)mm["distribution"];   // optional - scan exports without distribution skip that page
        string? mUsw = (string?)mm["usw"];             // optional - average unit-of-sale weight column
        bool hasDist = !string.IsNullOrWhiteSpace(mDist);
        var slicers = cfg["slicers"] as JsonArray ?? new JsonArray();
        double W = Num(cfg["canvasWidth"]) ?? 1280, H = Num(cfg["canvasHeight"]) ?? 720;
        const double M = 24; double usable = W - 2 * M, gut = 16, contentY = 224, contentH = H - contentY - M;
        // additive mode: clearPages:false appends these pages to an existing report (e.g. a benchmark
        // section beside a client's own pages); pagePrefix namespaces the page names to avoid collisions.
        bool clearPages = (bool?)cfg["clearPages"] ?? true;
        string pp = S("pagePrefix");

        if (clearPages)
        {
            GenerateTheme(reportSessionId, title + " Brand", brand, null, "executive", "Segoe UI", false, 10, true, false, true,
                !string.IsNullOrWhiteSpace(logo) && File.Exists(logo) ? logo : null);
            ClearPages(reportSessionId);
        }
        string pPerf = NameOf(AddPage(reportSessionId, pp + "Performance", (int)W, (int)H), "pageName");
        string pPV = NameOf(AddPage(reportSessionId, pp + "Price & Volume", (int)W, (int)H), "pageName");
        string pShare = NameOf(AddPage(reportSessionId, pp + "Share", (int)W, (int)H), "pageName");
        string? pDist = hasDist ? NameOf(AddPage(reportSessionId, pp + "Distribution", (int)W, (int)H), "pageName") : null;
        string pRank = NameOf(AddPage(reportSessionId, pp + "Ranking", (int)W, (int)H), "pageName");
        var nav = new List<(string, string)> { ("Performance", pPerf), ("Price & Volume", pPV), ("Share", pShare) };
        if (hasDist) nav.Add(("Distribution", pDist!));
        nav.Add(("Ranking", pRank));

        void Chrome(string pg)
        {
            int idx = nav.FindIndex(n => n.Item2 == pg);
            if (idx < 0) idx = 0;
            SetPageBackground(reportSessionId, pg, "#EDF1F6", 0);
            BuildBanner(reportSessionId, pg, title, subtitle, headline, headlineLabel, brand, accent, logo, W, M, usable);
            BuildNavBar(reportSessionId, pg, nav, idx, M, 114, 184, 32, 8, "#5B7494", brand, "#FFFFFF");
            BuildSlicerBar(reportSessionId, pg, slicers, M, usable, gut, 156, 56);
        }
        string Card(object r, string pg) { string n = NameOf(r, "visualName"); StyleVisual(reportSessionId, pg, n, "#FFFFFF", 10, true, false, "#E6E9EF"); return n; }

        // pane split for table pages - tables don't stretch their columns, so pair each with a chart to fill the canvas
        double tW = Math.Round(usable * 0.56), cW = usable - tW - gut, cX = M + tW + gut;

        // Performance: Brand x metrics matrix (left) + ranked Sales bar (right)
        Chrome(pPerf);
        var perfBinds = new List<FieldBinding> { new("Rows", brandT, brandC, "column") };
        var perfMeasures = new List<string> { mSales, mVol, mPrice };
        if (hasDist) perfMeasures.Add(mDist!);
        perfMeasures.Add(mGrowth);
        foreach (var me in perfMeasures) perfBinds.Add(new("Values", fact, me, "measure"));
        string perfM = Card(AddVisual(reportSessionId, pPerf, "pivotTable", M, contentY, 0, tW, contentH, perfBinds, "Brand Performance"), pPerf);
        SetConditionalFormatting(reportSessionId, pPerf, perfM, "values", "backColor", fact, mSales, "#EAF1F7", "#16365C", null, null, null, null, $"{fact}.{mSales}");
        SetConditionalFormatting(reportSessionId, pPerf, perfM, "values", "fontColor", fact, mGrowth, "#C0504D", "#1B8A4B", "#3A3A3A", -0.05, 0.05, 0, $"{fact}.{mGrowth}");
        string perfBar = Card(AddVisual(reportSessionId, pPerf, "clusteredBarChart", cX, contentY, 0, cW, contentH,
            new[] { new FieldBinding("Category", brandT, brandC, "column"), new FieldBinding("Y", fact, mSales, "measure") }, "Sales by Brand"), pPerf);
        SetVisualProperty(reportSessionId, pPerf, perfBar, "dataPoint", "defaultColor", brand, "color", "objects");

        // Price & Volume: combo - volume columns + avg price line, by segment (Period totals vary by segment, not over time)
        Chrome(pPV);
        var pvBinds = new[]
        {
            new FieldBinding("Category", segT, segC, "column"),
            new FieldBinding("Y", fact, mVol, "measure"),
            new FieldBinding("Y2", fact, mPrice, "measure"),
        };
        string pvC = Card(AddVisual(reportSessionId, pPV, "lineClusteredColumnComboChart", M, contentY, 0, usable, contentH, pvBinds, "Volume and Avg Price by Segment"), pPV);
        SetVisualProperty(reportSessionId, pPV, pvC, "dataPoint", "defaultColor", brand, "color", "objects");

        // Share: segment donut + segment trend
        Chrome(pShare);
        double halfW = (usable - gut) / 2;
        Card(AddVisual(reportSessionId, pShare, "donutChart", M, contentY, 0, halfW, contentH,
            new[] { new FieldBinding("Category", segT, segC, "column"), new FieldBinding("Y", fact, mSales, "measure") }, "Sales Share by Segment"), pShare);
        string st = Card(AddVisual(reportSessionId, pShare, "lineChart", M + halfW + gut, contentY, 0, halfW, contentH,
            new[] { new FieldBinding("Category", dateT, dateC, "column"), new FieldBinding("Y", fact, mTrend, "measure"), new FieldBinding("Series", segT, segC, "column") }, "Sales Trend by Segment"), pShare);
        SetVisualProperty(reportSessionId, pShare, st, "legend", "show", "true", "bool", "objects");

        // Distribution: Brand x distribution metrics matrix (left) + ranked distribution bar (right) - only when the data has it
        if (hasDist)
        {
            Chrome(pDist!);
            var distBinds = new List<FieldBinding> { new("Rows", brandT, brandC, "column"), new("Values", fact, mDist!, "measure") };
            if (!string.IsNullOrWhiteSpace(mUsw)) distBinds.Add(new("Values", fact, mUsw!, "measure"));
            string distM = Card(AddVisual(reportSessionId, pDist!, "pivotTable", M, contentY, 0, tW, contentH, distBinds, "Distribution Tracker"), pDist!);
            SetConditionalFormatting(reportSessionId, pDist!, distM, "values", "backColor", fact, mDist!, "#EAF1F7", "#16365C", null, null, null, null, $"{fact}.{mDist}");
            string distBar = Card(AddVisual(reportSessionId, pDist!, "clusteredBarChart", cX, contentY, 0, cW, contentH,
                new[] { new FieldBinding("Category", brandT, brandC, "column"), new FieldBinding("Y", fact, mDist!, "measure") }, "Distribution % by Brand"), pDist!);
            SetVisualProperty(reportSessionId, pDist!, distBar, "dataPoint", "defaultColor", accent, "color", "objects");
        }

        // Ranking: a view-switcher across By Brand / By Segment / By Item ranking tables
        Chrome(pRank);
        double rcy = 256, rch = H - rcy - M;
        string itemC = S("itemColumn", "ItemDesc");
        var rankViews = new (string name, string t, string c)[] { ("By Brand", brandT, brandC), ("By Segment", segT, segC), ("By Item", brandT, itemC) };
        var rankSpecs = new JsonArray();
        foreach (var (vname, vt, vcol) in rankViews)
        {
            var binds = new List<FieldBinding>
            {
                new("Values", vt, vcol, "column"),
                new("Values", fact, mSales, "measure"),
                new("Values", fact, mVol, "measure"),
                new("Values", fact, mGrowth, "measure"),
            };
            string rt = Card(AddVisual(reportSessionId, pRank, "tableEx", M, rcy, 0, tW, rch, binds, $"Ranking - {vname}"), pRank);
            string rb = Card(AddVisual(reportSessionId, pRank, "clusteredBarChart", cX, rcy, 0, cW, rch,
                new[] { new FieldBinding("Category", vt, vcol, "column"), new FieldBinding("Y", fact, mSales, "measure") }, $"Top {vname.Replace("By ", "")} by Sales"), pRank);
            SetVisualProperty(reportSessionId, pRank, rb, "dataPoint", "defaultColor", brand, "color", "objects");
            var arr = new JsonArray { rt, rb };
            rankSpecs.Add(new JsonObject { ["name"] = vname, ["visuals"] = arr });
        }
        int rnb = rankSpecs.Count; double rbtnW = 140, rbtnGap = 8;
        double rbx = W - M - (rnb * rbtnW + (rnb - 1) * rbtnGap);
        AddViewSwitcher(reportSessionId, pRank, rankSpecs.ToJsonString(JsonOpts), rbx, 220, rbtnW, 28, rbtnGap, "#5B7494", brand, "#FFFFFF");

        // mobile layout for every page - slicers + table + chart stack cleanly on a phone canvas
        int mobilePages = 0;
        foreach (var (_, pg) in nav) { try { AutoMobileLayout(reportSessionId, pg); mobilePages++; } catch { } }

        return new { ok = true, recipe = "category", canvas = $"{(int)W}x{(int)H}", pages = nav.Count, appended = !clearPages, mobilePages, slicers = slicers.Count };
    }

    /// <summary>The recipe catalog - the SaaS front-door. Lists each report template, what it produces,
    /// and the config fields that map it to a client model. Discover, then call the recipe's tool.</summary>
    public object ListRecipes() => new
    {
        ok = true,
        recipes = new object[]
        {
            new
            {
                name = "executive",
                tool = "build_executive_report",
                title = "Executive Dashboard",
                pages = "1 page (optionally button-switched views)",
                description = "Premium single-page executive dashboard: brand banner + logo, slicer bar, a row of KPI cards (value + coloured delta + sparkline), a hero trend and growth-coloured segment bars. Optional VIEWS (button-switched) and a narrative insight strip.",
                config = new[]
                {
                    "title* / subtitle / headline / headlineLabel  (banner text)",
                    "brandColor / accentColor / logoPath  (brand kit)",
                    "canvasWidth / canvasHeight  (default 1280x720; 1920x1080 = Full HD)",
                    "factTable* / dateTable* / dateColumn* / trendMeasure*  (hero trend)",
                    "segmentTable* / segmentColumn* / segmentValueMeasure* / growthMeasure*  (growth bars)",
                    "slicers*: [{table,column,title}]",
                    "kpis*: [{measure,label,delta,trend}]",
                    "views: [{name, charts:[{type,categoryTable,categoryField,valueTable,valueMeasure,title,growthMeasure}]}]",
                    "narrativeMeasure  (insight strip - from add_narrative_measure)",
                },
            },
            new
            {
                name = "category",
                tool = "build_category_report",
                title = "Category Review",
                pages = "5 nav-linked pages: Performance, Price & Volume, Share, Distribution, Ranking",
                description = "Beautified multi-page FMCG scan template: a Brand x metrics Performance matrix (colour-scale on Sales + red/green Growth), a Volume/Price combo by segment, a segment Share donut + trend, a Distribution matrix, and a Ranking page with a Brand/Segment/Item view-switcher.",
                config = new[]
                {
                    "title / subtitle / headline / headlineLabel  (banner text)",
                    "brandColor / accentColor / logoPath  (brand kit)",
                    "canvasWidth / canvasHeight  (default 1280x720)",
                    "factTable*",
                    "brandTable* / brandColumn*  (matrix + ranking rows)",
                    "segmentTable* / segmentColumn*  (share + combo)",
                    "dateTable* / dateColumn*  (share trend - use a chronological Date column)",
                    "itemColumn  (Ranking 'By Item', default ItemDesc)",
                    "slicers*: [{table,column,title}]",
                    "measures*: {sales,volume,price,growth,salesTrend, +optional distribution,usw}  (omit distribution to auto-skip that page)",
                    "clearPages  (default true; false = APPEND beside existing pages)",
                    "pagePrefix  (namespace page names, e.g. 'R2 ' for the second retailer - for benchmark sections)",
                },
            },
            new
            {
                name = "crossretailer",
                tool = "build_crossretailer_compare",
                title = "Total Market Compare",
                pages = "1 page (append-able)",
                description = "A total-market compare page across two retailers/panels (the Phase-2 payoff). Pairs with conform_dimension: KPI row (Total Market / Retailer A / Retailer B), a by-entity table, and an A-vs-B grouped bar - one slicer filters both retailers.",
                config = new[]
                {
                    "title / subtitle / headline / headlineLabel / brandColor / accentColor / logoPath",
                    "compareTable* / compareColumn*  (the conformed dimension from conform_dimension)",
                    "retailerA*: {label, table, measure}   retailerB*: {label, table, measure}",
                    "totalMeasure: {table, measure}  (optional Total Market KPI = A + B)",
                    "slicers*: [{table,column,title}]   (slice on the conformed column to filter both)",
                    "clearPages / pagePrefix  (append beside existing pages)",
                },
            },
        },
        // The premium pipeline is more than the report recipes - these are the supporting workflows
        // a serious build uses, in order. Each step is its own tool(s).
        workflows = new object[]
        {
            new
            {
                name = "model-prep",
                purpose = "Turn raw scan/panel data into a clean star schema.",
                steps = "stage_excel_to_csv / unpivot_weekly_csv (big files) -> add_table_from_m -> add_data_column -> refresh_table -> create_date_table -> add_relationship (auto-recalcs) -> add_measure / add_time_intelligence",
            },
            new
            {
                name = "reliability-gate",
                purpose = "Catch the 'click a value and the visuals fall over' class BEFORE delivery (the soft-drink-brand lesson).",
                steps = "audit_robustness (flags both error-on-select and blank-on-select, pinpointing dead values) -> add_coverage_flag (boolean flag so slicers/filters exclude members with no data) -> check_relationships / quality_gate for the rest",
            },
            new
            {
                name = "cross-retailer / total-market",
                purpose = "Join two side-by-side retailer/panel islands into one total-market view (the Phase-2 join).",
                steps = "conform_dimension(newTable, key, factA, colA, factB, colB) builds a shared dimension related to both -> one slicer filters both retailers -> add_measure [Total Market X] = [A] + [B] and share-of-market measures",
            },
            new
            {
                name = "premium-report",
                purpose = "Generate the report itself, then ship it clean.",
                steps = "open_report (pbix CLOSED) -> build_executive_report / build_category_report (clearPages:false + pagePrefix to append a benchmark section) -> auto_mobile_layout -> save_report -> VERIFY the render in Power BI Desktop (the engine is WebView2; the server cannot see render errors)",
            },
        },
        howTo = "Model tools need the pbix OPEN in Power BI Desktop (live engine); report tools need it CLOSED. Typical job: open -> model-prep -> reliability-gate -> (cross-retailer) -> Save + close -> premium-report -> verify in Desktop.",
    };

    private static void ApplyStyle(JsonObject vcObjects, string? background, double? cornerRadius,
        bool? shadow, bool? showHeader, string? borderColor)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (background != null)
            vcObjects["background"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject {
                ["show"] = Lit("true"),
                ["color"] = Lit($"'{background}'", wrapSolid: true),
                ["transparency"] = Lit("0D"),
            } } };
        if (borderColor != null || cornerRadius != null)
        {
            var props = new JsonObject { ["show"] = Lit("true") };
            if (borderColor != null) props["color"] = Lit($"'{borderColor}'", wrapSolid: true);
            if (cornerRadius != null) props["radius"] = Lit(cornerRadius.Value.ToString(inv) + "D");
            vcObjects["border"] = new JsonArray { new JsonObject { ["properties"] = props } };
        }
        if (shadow != null)
            vcObjects["dropShadow"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject {
                ["show"] = Lit(shadow.Value ? "true" : "false"),
                ["preset"] = Lit("'BottomRight'"),
            } } };
        if (showHeader != null)
            vcObjects["visualHeader"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject {
                ["show"] = Lit(showHeader.Value ? "true" : "false"),
            } } };
    }

    // -------------------------------------------------------------------- universal formatting + layout
    /// <summary>Set ANY visual formatting property (data labels, legend, axes, fonts, colours...).
    /// objectName/propertyName are Power BI's formatting ids; target = objects (data-level) or vcObjects (container-level).</summary>
    public object SetVisualProperty(string reportSessionId, string pageName, string visualName,
        string objectName, string propertyName, string value, string kind, string target)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = FindVisual(section, visualName);

        string bagKey = target.Equals("vcObjects", StringComparison.OrdinalIgnoreCase) ? "vcObjects" : "objects";
        var bag = sv[bagKey] as JsonObject;
        if (bag == null) { bag = new JsonObject(); sv[bagKey] = bag; }
        var arr = bag[objectName] as JsonArray;
        if (arr == null) { arr = new JsonArray(); bag[objectName] = arr; }
        JsonObject entry;
        if (arr.Count > 0 && arr[0] is JsonObject e0) entry = e0;
        else { entry = new JsonObject { ["properties"] = new JsonObject() }; arr.Add(entry); }
        var props = entry["properties"] as JsonObject;
        if (props == null) { props = new JsonObject(); entry["properties"] = props; }
        props[propertyName] = LiteralFor(value, kind);

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visualName, target = bagKey, obj = objectName, prop = propertyName, value };
    }

    /// <summary>
    /// Apply a gradient colour scale to a visual property driven by a measure (conditional formatting -
    /// the signature 'enterprise dashboard' feature). e.g. colour a table value's background from light
    /// to dark by [Total Sales]. The data min/max is auto-computed by Power BI.
    /// </summary>
    public object SetConditionalFormatting(string reportSessionId, string pageName, string visualName,
        string objectName, string propertyName, string measureTable, string measure,
        string minColor, string maxColor, string? centerColor, double? minValue, double? maxValue, double? midValue,
        string? metadata = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = FindVisual(section, visualName);

        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        var arr = (objects[objectName] as JsonArray) ?? new JsonArray(); objects[objectName] = arr;
        // a table/matrix column is targeted by a selector with that column's queryRef as metadata;
        // a chart's data points have no selector. Reuse a matching entry or append a new one.
        JsonObject? entry = null;
        foreach (var n in arr)
            if (n is JsonObject e && (string?)e["selector"]?["metadata"] == metadata) { entry = e; break; }
        if (entry == null) { entry = new JsonObject { ["properties"] = new JsonObject() }; arr.Add(entry); }
        if (metadata != null)
            entry["selector"] = new JsonObject
            {
                ["data"] = new JsonArray { new JsonObject { ["dataViewWildcard"] = new JsonObject { ["matchingOption"] = 0 } } },
                ["metadata"] = metadata,
            };
        var props = (entry["properties"] as JsonObject) ?? new JsonObject(); entry["properties"] = props;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        // gradient stop = colour (a bare Literal, NOT wrapped in expr) + optional explicit value
        JsonObject Stop(string hex, double? v)
        {
            var s = new JsonObject { ["color"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{hex}'" } } };
            if (v.HasValue) s["value"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = v.Value.ToString(inv) + "D" } };
            return s;
        }
        JsonObject nullStrat() => new() { ["strategy"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "'asZero'" } } };
        JsonObject grad = centerColor == null
            ? new JsonObject { ["linearGradient2"] = new JsonObject { ["min"] = Stop(minColor, minValue), ["max"] = Stop(maxColor, maxValue), ["nullColoringStrategy"] = nullStrat() } }
            : new JsonObject { ["linearGradient3"] = new JsonObject { ["min"] = Stop(minColor, minValue), ["mid"] = Stop(centerColor, midValue), ["max"] = Stop(maxColor, maxValue), ["nullColoringStrategy"] = nullStrat() } };
        var input = new JsonObject { ["Measure"] = new JsonObject {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = measureTable } }, ["Property"] = measure } };
        // the OUTER colour keeps its expr.FillRule wrapper - only the gradient stops drop expr
        props[propertyName] = new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonObject {
            ["expr"] = new JsonObject { ["FillRule"] = new JsonObject { ["Input"] = input, ["FillRule"] = grad } } } } };

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visualName, conditionalFormat = $"{objectName}/{propertyName}", basedOn = $"{measureTable}[{measure}]",
            gradient = centerColor == null ? "2-colour" : "3-colour" };
    }

    // ============================================================================================
    //  REPORT-BUILDING TOOLS - reproduce a dashboard end-to-end through the engine: clone pages/visuals,
    //  build report-level bookmarks, wire a bookmark to an action button, and apply discrete rule-based
    //  conditional formatting. Each writes the exact PBIR/legacy-Layout shapes the live product emits.
    // ============================================================================================

    /// <summary>A fresh, globally-unique visual id (config.name): 32 random hex characters, the same
    /// width Power BI uses. RandomNumberGenerator gives a cryptographically-strong id so a clone never
    /// collides with an existing visual anywhere in the report.</summary>
    private static string NewVisualId() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    /// <summary>A fresh report-section name: "ReportSection" + 32 random hex (matches AddPage's width).</summary>
    private static string NewSectionName() => "ReportSection" + NewVisualId();

    /// <summary>Every visual id (config.name) currently used ANYWHERE in the report - so a clone can be
    /// proven globally unique, not just unique on its own page.</summary>
    private static HashSet<string> AllVisualIds(JsonObject root)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in Sections(root))
            if (s is JsonObject so && so["visualContainers"] is JsonArray vcs)
                foreach (var node in vcs)
                    if (node is JsonObject vc && (string?)vc["config"] is string cfg &&
                        JsonNode.Parse(cfg) is JsonObject co && (string?)co["name"] is string n)
                        ids.Add(n);
        return ids;
    }

    /// <summary>Give one visualContainer a fresh, unique config.name (mutates its stringified config in
    /// place) and record the id in the used-set. Returns the new id.</summary>
    private static string ReidVisual(JsonObject vc, HashSet<string> used)
    {
        if ((string?)vc["config"] is not string cfg || JsonNode.Parse(cfg) is not JsonObject co)
            return "";
        string id;
        do { id = NewVisualId(); } while (!used.Add(id));
        co["name"] = id;
        vc["config"] = co.ToJsonString(JsonOpts);
        return id;
    }

    // -------------------------------------------------------------------- clone
    /// <summary>
    /// Deep-clone a whole page (section): a fresh unique section name, a new ordinal at the end, the given
    /// display name, and - critically - a fresh unique id for EVERY visual on it, so visual ids stay
    /// globally unique across the whole report (ids drive bookmarks, selection and cross-highlight, so a
    /// duplicated id would silently corrupt both pages). Returns the new page (section) name.
    /// </summary>
    public object ClonePage(string reportSessionId, string sourcePage, string newDisplayName)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var source = FindSection(root, sourcePage);
        var used = AllVisualIds(root);

        var clone = (JsonObject)source.DeepClone();
        clone["name"] = NewSectionName();
        clone["displayName"] = newDisplayName;
        clone["ordinal"] = Sections(root).Count == 0 ? 0 : Sections(root).Max(s => (int?)Num(s?["ordinal"]) ?? 0) + 1;

        int reided = 0;
        if (clone["visualContainers"] is JsonArray vcs)
            foreach (var node in vcs)
                if (node is JsonObject vc) { ReidVisual(vc, used); reided++; }

        Sections(root).Add(clone);
        session.Dirty = true;
        return new { ok = true, pageName = (string?)clone["name"], displayName = newDisplayName,
            sourcePage = (string?)source["name"], visuals = reided };
    }

    /// <summary>
    /// Deep-clone ONE visual (with a fresh unique config.name) onto the same page or onto targetPage.
    /// Position is preserved (nudged slightly when cloning onto the same page so the copy is visible).
    /// Returns the new visual id.
    /// </summary>
    public object CloneVisual(string reportSessionId, string page, string visual, string? targetPage)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var srcSection = FindSection(root, page);
        var (vc, _, _) = FindVisual(srcSection, visual);
        var dstSection = string.IsNullOrWhiteSpace(targetPage) ? srcSection : FindSection(root, targetPage!);
        bool samePage = ReferenceEquals(dstSection, srcSection);

        var used = AllVisualIds(root);
        var clone = (JsonObject)vc.DeepClone();
        if (samePage)
        {
            // nudge so the copy is not exactly behind the original
            void Bump(string k) { if (Num(clone[k]) is double d) clone[k] = d + 16; }
            Bump("x"); Bump("y");
            if (JsonNode.Parse((string?)clone["config"] ?? "{}") is JsonObject cco &&
                cco["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject pos)
            {
                if (Num(pos["x"]) is double px) pos["x"] = px + 16;
                if (Num(pos["y"]) is double py) pos["y"] = py + 16;
                clone["config"] = cco.ToJsonString(JsonOpts);
            }
        }
        string newId = ReidVisual(clone, used);
        (dstSection["visualContainers"] as JsonArray)!.Add(clone);
        session.Dirty = true;
        return new { ok = true, visualName = newId, page = (string?)dstSection["name"], clonedFrom = visual };
    }

    // -------------------------------------------------------------------- bookmarks (report-level)
    /// <summary>The report-level bookmarks array, parsed out of the stringified layout.config. Returns the
    /// parsed config and the (live, attached) bookmarks array so a caller can mutate then re-stringify.</summary>
    private static (JsonObject cfg, JsonArray bookmarks) ReportBookmarks(JsonObject root)
    {
        var cfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var bookmarks = (cfg["bookmarks"] as JsonArray) ?? new JsonArray();
        cfg["bookmarks"] = bookmarks;
        return (cfg, bookmarks);
    }

    /// <summary>The set of visual ids a bookmark hides: those whose recorded singleVisual.display.mode is
    /// "hidden" in its explorationState.</summary>
    private static List<string> HiddenVisualsOf(JsonObject bm)
    {
        var hidden = new List<string>();
        if (bm["explorationState"]?["sections"] is JsonObject secs)
            foreach (var (_, secNode) in secs)
                if (secNode?["visualContainers"] is JsonObject vcs)
                    foreach (var (vid, vNode) in vcs)
                        if ((string?)vNode?["singleVisual"]?["display"]?["mode"] == "hidden")
                            hidden.Add(vid);
        return hidden;
    }

    /// <summary>List the report's bookmarks: name, displayName and the visual ids each one hides.</summary>
    public object ListBookmarks(string reportSessionId)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var (_, bookmarks) = ReportBookmarks(root);
        var list = bookmarks.OfType<JsonObject>().Select(bm => new
        {
            name = (string?)bm["name"],
            displayName = (string?)bm["displayName"],
            activeSection = (string?)bm["explorationState"]?["activeSection"],
            hiddenVisuals = HiddenVisualsOf(bm).ToArray(),
        }).ToArray();
        return new { ok = true, bookmarks = list, count = list.Length };
    }

    /// <summary>Build a bookmark's explorationState for a page: record EVERY visual on the page, with the
    /// listed ones set display.mode = "hidden" and the rest "show" - the exact PBIX shape. When captureData is
    /// true ALSO snapshots the live DATA state into each visual's singleVisual config (the Wave N data-state
    /// capture): the saved SORT (orderBy), the DRILL position (activeProjections + expansionStates), the
    /// cross-HIGHLIGHT (highlight.selection on the container state), and the page + per-visual FILTER/SLICER
    /// state (the section filterConfig + each visual's filterConfig). This is what makes a bookmark restore the
    /// filter/slicer values, sort and drill - not just which visuals are shown.</summary>
    private JsonObject BuildExplorationState(JsonObject section, IReadOnlyCollection<string> hiddenVisuals, bool captureData = false)
    {
        string secName = (string?)section["name"] ?? throw new InvalidOperationException("page has no name.");
        var vcs = new JsonObject();
        foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co || (string?)co["name"] is not string vid) continue;
            bool hide = hiddenVisuals.Contains(vid);

            var vcState = new JsonObject
            {
                ["singleVisual"] = new JsonObject { ["display"] = new JsonObject { ["mode"] = hide ? "hidden" : "show" } },
            };
            if (captureData) CaptureVisualDataState(vc, co, vcState);
            vcs[vid] = vcState;
        }

        var sectionState = new JsonObject { ["visualContainers"] = vcs };
        // page-level filter/slicer values: snapshot the section's filters into the SectionState.filterConfig.
        if (captureData && SnapshotFilterConfig(section["filters"]) is JsonObject pageFc)
            sectionState["filterConfig"] = pageFc;

        return new JsonObject
        {
            ["version"] = "1.3",
            ["activeSection"] = secName,
            ["sections"] = new JsonObject { [secName] = sectionState },
        };
    }

    /// <summary>Snapshot the live DATA state of one visual into its VisualContainerState (the bookmark
    /// data-state capture): the singleVisual config carries the saved orderBy (sort), activeProjections +
    /// expansionStates (drill position), and objects (formatting) - alongside any cross-highlight on the
    /// container state and the visual's own filterConfig. Only writes the slots that exist on the visual so a
    /// chart without a sort/drill stays minimal. FLAG (Desktop validation): the singleVisual config carried
    /// inside explorationState mirrors the live visual config; the exact subset Desktop persists per bookmark
    /// should be confirmed - we capture the durable data-bearing slots (orderBy/projections/expansion/highlight
    /// /filters).</summary>
    private static void CaptureVisualDataState(JsonObject vc, JsonObject co, JsonObject vcState)
    {
        var sv = co["singleVisual"] as JsonObject;
        var svState = (vcState["singleVisual"] as JsonObject)!;

        if (sv != null)
        {
            // saved SORT - the visual's prototypeQuery OrderBy, captured verbatim.
            if (sv["prototypeQuery"]?["OrderBy"] is JsonArray orderBy && orderBy.Count > 0)
                svState["orderBy"] = orderBy.DeepClone();
            // DRILL position - the active role projections and the expanded levels.
            if (sv["projections"] is JsonObject projections)
                svState["activeProjections"] = projections.DeepClone();
            if (sv["expansionStates"] is JsonArray expansion && expansion.Count > 0)
                svState["expansionStates"] = expansion.DeepClone();
            // captured formatting (a bookmark may pin formatting changes).
            if (sv["objects"] is JsonObject svObjects)
                svState["objects"] = svObjects.DeepClone();
            // FIELD-PARAMETER selection, when present.
            if (sv["parameters"] is JsonNode svParams)
                svState["parameters"] = svParams.DeepClone();
        }

        // cross-HIGHLIGHT - a selection the visual currently highlights (highlight.selection on the state).
        if (co["highlight"] is JsonObject highlight)
            vcState["highlight"] = highlight.DeepClone();

        // per-visual FILTER/SLICER values - the visual's own filters array.
        if (SnapshotFilterConfig(vc["filters"]) is JsonObject visFc)
            vcState["filterConfig"] = visFc;
    }

    /// <summary>Lift a stringified legacy filters array into a bookmark filterConfig { filters:[...] } object,
    /// or null when there are no filters to capture. The legacy filters array is the same shape the filter
    /// builders write (FilterContainer entries), so a bookmark restores those filter/slicer values on apply.</summary>
    private static JsonObject? SnapshotFilterConfig(JsonNode? filtersNode)
    {
        if (filtersNode is null) return null;
        JsonArray? arr = filtersNode as JsonArray
            ?? (filtersNode is JsonValue v && v.TryGetValue<string>(out var s) ? JsonNode.Parse(s) as JsonArray : null);
        if (arr is null || arr.Count == 0) return null;
        return new JsonObject { ["filters"] = arr.DeepClone() };
    }

    /// <summary>
    /// Add a report-level bookmark named "Bookmark" + 16 hex whose explorationState captures the page's
    /// visualContainers, with the listed visual ids hidden and the rest shown - the live PBIX bookmark
    /// shape. Returns the new bookmark name.
    /// </summary>
    public object AddBookmark(string reportSessionId, string page, string displayName,
        IReadOnlyCollection<string> hiddenVisuals, bool captureData = false)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var section = FindSection(root, page);
        var (cfg, bookmarks) = ReportBookmarks(root);

        string name = "Bookmark" + NewVisualId()[..16];
        bookmarks.Add(new JsonObject
        {
            ["displayName"] = displayName,
            ["name"] = name,
            ["explorationState"] = BuildExplorationState(section, new HashSet<string>(hiddenVisuals, StringComparer.Ordinal), captureData),
        });
        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, name, displayName, page = (string?)section["name"], hiddenVisuals = hiddenVisuals.ToArray(),
            captureData, dataState = captureData ? "filters/slicers + sort + drill + highlight captured" : "display only" };
    }

    /// <summary>Re-snapshot the DATA state of an existing bookmark from a page's CURRENT live state (filters,
    /// slicer values, sort, drill, cross-highlight) - the "update with current state" action with Data on. Keeps
    /// the bookmark's existing hidden/shown visuals. Pass page to re-anchor which page is captured (defaults to
    /// the bookmark's activeSection).</summary>
    public object SetBookmarkDataState(string reportSessionId, string name, string? page)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var (cfg, bookmarks) = ReportBookmarks(root);
        var bm = FindBookmark(bookmarks, name);

        // preserve the existing hidden set, then rebuild the explorationState WITH data capture.
        var hidden = new HashSet<string>(HiddenVisualsOf(bm), StringComparer.Ordinal);
        string pageRef = page ?? (string?)bm["explorationState"]?["activeSection"]
            ?? throw new InvalidOperationException("bookmark has no activeSection; pass page.");
        var section = FindSection(root, pageRef);
        bm["explorationState"] = BuildExplorationState(section, hidden, captureData: true);

        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, name = (string?)bm["name"], page = (string?)section["name"],
            hiddenVisuals = hidden.ToArray(), captureData = true,
            dataState = "filters/slicers + sort + drill + highlight captured" };
    }

    private static JsonObject FindBookmark(JsonArray bookmarks, string name) =>
        bookmarks.OfType<JsonObject>().FirstOrDefault(b => (string?)b["name"] == name || (string?)b["displayName"] == name)
        ?? throw new InvalidOperationException($"Bookmark '{name}' not found.");

    /// <summary>Re-set which visuals a bookmark hides: flips each recorded visual's display.mode to
    /// "hidden" (if listed) or "show" (otherwise), in place. Visuals not yet recorded are left alone.</summary>
    public object UpdateBookmark(string reportSessionId, string name, IReadOnlyCollection<string> hiddenVisuals,
        bool captureData = false)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var (cfg, bookmarks) = ReportBookmarks(root);
        var bm = FindBookmark(bookmarks, name);
        var hide = new HashSet<string>(hiddenVisuals, StringComparer.Ordinal);

        // captureData rebuilds the whole explorationState from the live page (display + data), keeping the
        // requested hidden set; otherwise we only flip display.mode on the recorded visuals (the legacy behaviour).
        if (captureData)
        {
            string pageRef = (string?)bm["explorationState"]?["activeSection"]
                ?? throw new InvalidOperationException("bookmark has no activeSection to re-capture.");
            var section = FindSection(root, pageRef);
            bm["explorationState"] = BuildExplorationState(section, hide, captureData: true);
        }
        else if (bm["explorationState"]?["sections"] is JsonObject secs)
        {
            foreach (var (_, secNode) in secs)
                if (secNode?["visualContainers"] is JsonObject vcs)
                    foreach (var (vid, vNode) in vcs)
                        if (vNode is JsonObject vo)
                            vo["singleVisual"] = new JsonObject { ["display"] = new JsonObject { ["mode"] = hide.Contains(vid) ? "hidden" : "show" } };
        }

        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, name = (string?)bm["name"], hiddenVisuals = HiddenVisualsOf(bm).ToArray(), captureData };
    }

    /// <summary>Delete a report-level bookmark by name or displayName.</summary>
    public object DeleteBookmark(string reportSessionId, string name)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var (cfg, bookmarks) = ReportBookmarks(root);
        for (int i = 0; i < bookmarks.Count; i++)
            if (bookmarks[i] is JsonObject b && ((string?)b["name"] == name || (string?)b["displayName"] == name))
            {
                bookmarks.RemoveAt(i);
                root["config"] = cfg.ToJsonString(JsonOpts);
                session.Dirty = true;
                return new { ok = true, deleted = name };
            }
        throw new InvalidOperationException($"Bookmark '{name}' not found.");
    }

    // -------------------------------------------------------------------- action button bound to a bookmark
    /// <summary>
    /// Add an actionButton wired to a bookmark (the view-switch pattern): its singleVisual.vcObjects.visualLink
    /// carries show=true, type='Bookmark' and the target bookmark name, plus the button label text. Matches the
    /// exact shape the live Ranking view-switch buttons use. Returns the new visual id.
    /// </summary>
    public object AddActionButton(string reportSessionId, string page, string text, string bookmarkName,
        double x, double y, double width, double height)
        => AddActionButtonEx(reportSessionId, page, text, "bookmark", bookmarkName, null, x, y, width, height);

    /// <summary>
    /// Add a button with ANY action type: Bookmark | Back | PageNavigation | Drillthrough | Qna | WebUrl. The
    /// visualLink card carries type + the action's target: a Bookmark name, a PageNavigation/Drillthrough
    /// destination page (navigationSection), or a WebUrl url. Back needs no target. Reuses the same actionButton
    /// shape AddActionButton/AddNavButton write.
    /// FLAG (Desktop validation): the visualLink "type" enum values (Bookmark/Back/PageNavigation/Drillthrough/
    /// Qna/WebUrl) and their target keys are matched to the legacy shape - confirm Drillthrough/Qna/WebUrl in
    /// Desktop.
    /// </summary>
    public object AddActionButtonEx(string reportSessionId, string page, string text, string actionType,
        string? bookmarkName, string? destinationOrUrl, double x, double y, double width, double height)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var section = FindSection(root, page);
        var containers = (section["visualContainers"] as JsonArray)!;

        var linkProps = new JsonObject { ["show"] = Lit("true") };
        string at = (actionType ?? "bookmark").Trim().ToLowerInvariant();
        string typeToken;
        switch (at)
        {
            case "bookmark":
                if (string.IsNullOrWhiteSpace(bookmarkName)) throw new ArgumentException("a bookmark action needs bookmarkName.");
                typeToken = "Bookmark";
                linkProps["bookmark"] = Lit($"'{bookmarkName!.Replace("'", "''")}'");
                break;
            case "back":
                typeToken = "Back";
                break;
            case "pagenavigation":
            case "page":
                if (string.IsNullOrWhiteSpace(destinationOrUrl)) throw new ArgumentException("a page-navigation action needs a destination page.");
                typeToken = "PageNavigation";
                linkProps["navigationSection"] = Lit($"'{ResolvePageName(root, destinationOrUrl!).Replace("'", "''")}'");
                break;
            case "drillthrough":
                if (string.IsNullOrWhiteSpace(destinationOrUrl)) throw new ArgumentException("a drill-through action needs a destination page.");
                typeToken = "Drillthrough";
                linkProps["navigationSection"] = Lit($"'{ResolvePageName(root, destinationOrUrl!).Replace("'", "''")}'");
                break;
            case "qna":
            case "q&a":
                typeToken = "Qna";
                break;
            case "weburl":
            case "url":
            case "web":
                if (string.IsNullOrWhiteSpace(destinationOrUrl)) throw new ArgumentException("a web-url action needs a url.");
                typeToken = "WebUrl";
                linkProps["webUrl"] = Lit($"'{destinationOrUrl!.Replace("'", "''")}'");
                break;
            case "clearallslicers":
            case "clearall":
            case "clear":
                // acts on the page's slicers - carries no bookmark and no destination
                typeToken = "ClearAllSlicers";
                break;
            case "applyallslicers":
            case "applyall":
            case "apply":
                typeToken = "ApplyAllSlicers";
                break;
            default:
                throw new ArgumentException($"unknown actionType '{actionType}' (use bookmark|back|pageNavigation|drillthrough|qna|webUrl|clearAllSlicers|applyAllSlicers).");
        }
        linkProps["type"] = Lit($"'{typeToken}'");

        var sv = new JsonObject
        {
            ["visualType"] = "actionButton",
            ["drillFilterOtherVisuals"] = true,
            ["vcObjects"] = new JsonObject
            {
                ["visualLink"] = new JsonArray { new JsonObject { ["properties"] = linkProps } },
                ["text"] = new JsonArray
                {
                    new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("true") } },
                    Sel(new JsonObject { ["text"] = Lit($"'{text.Replace("'", "''")}'") }, "default"),
                },
            },
        };
        // a Back button gets the built-in 'back' chevron shape so it reads as Back without text.
        if (typeToken == "Back")
            sv["objects"] = new JsonObject { ["icon"] = new JsonArray { Sel(new JsonObject { ["shapeType"] = Lit("'back'") }, "default") } };

        string id = NewVisualId();
        AddContainer(containers, id, sv, x, y, 0, width, height);
        session.Dirty = true;
        return new { ok = true, visualName = id, page = (string?)section["name"], text, actionType = typeToken,
            bookmark = bookmarkName, destination = destinationOrUrl };
    }

    // -------------------------------------------------------------------- discrete rule-based conditional formatting
    /// <summary>
    /// Apply DISCRETE rule-based BACKGROUND conditional formatting to a measure column in a table/matrix:
    /// each rule { min, max, color } paints the cell that colour when the measure value is in [min, max).
    /// Writes the standard Power BI rule-based FillRule (RuleDefinition with a colour ramp of {min,max}
    /// FilterCondition stops) into the visual's objects, targeting the measure via a selector metadata
    /// queryRef. This is the true discrete-band structure (NOT a gradient).
    /// rules: each is (min, max, hexColor); min/max are the half-open band bounds.
    /// </summary>
    public object SetConditionalFormattingRules(string reportSessionId, string page, string visual,
        string measureTable, string measure, IReadOnlyList<(double min, double max, string color)> rules,
        string objectName = "values", string propertyName = "backColor")
    {
        if (rules.Count == 0) throw new ArgumentException("at least one rule { min, max, color } is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        string queryRef = $"{measureTable}.{measure}";
        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        var arr = (objects[objectName] as JsonArray) ?? new JsonArray(); objects[objectName] = arr;

        // reuse a matching entry (same column selector) or append a new one
        JsonObject? entry = null;
        foreach (var n in arr)
            if (n is JsonObject e && (string?)e["selector"]?["metadata"] == queryRef) { entry = e; break; }
        if (entry == null) { entry = new JsonObject(); arr.Add(entry); }
        entry["selector"] = new JsonObject
        {
            ["data"] = new JsonArray { new JsonObject { ["dataViewWildcard"] = new JsonObject { ["matchingOption"] = 0 } } },
            ["metadata"] = queryRef,
        };
        var props = (entry["properties"] as JsonObject) ?? new JsonObject(); entry["properties"] = props;

        // the measure that drives the colour (the FillRule Input)
        var input = new JsonObject { ["Measure"] = new JsonObject {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = measureTable } }, ["Property"] = measure } };

        // each rule -> a RuleDefinition rule: Condition = (Input >= min) AND (Input < max), Color = the band colour.
        // This is Power BI's discrete rule-based structure (RuleDefinition.Rules[].Condition + .Color).
        var ruleArr = new JsonArray();
        foreach (var (min, max, color) in rules)
            ruleArr.Add(new JsonObject
            {
                ["Condition"] = BandCondition(input, min, max),
                ["Color"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{color}'" } },
            });

        var fillRule = new JsonObject
        {
            ["Input"] = input,
            ["FillRule"] = new JsonObject { ["ruleDefinition"] = new JsonObject { ["rules"] = ruleArr } },
        };
        props[propertyName] = new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonObject {
            ["expr"] = new JsonObject { ["FillRule"] = fillRule } } } };

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visual, conditionalFormat = $"{objectName}/{propertyName}",
            basedOn = $"{measureTable}[{measure}]", rules = rules.Count, kind = "discrete-rules" };
    }

    /// <summary>A band Condition: the FillRule Input value is in the half-open interval [min, max).
    /// Encodes as a logical AND of (Input &gt;= min) and (Input &lt; max) Comparisons (ComparisonKind 2 = GTE,
    /// 3 = LT), the standard Power BI rule-condition shape.</summary>
    private static JsonObject BandCondition(JsonObject input, double min, double max)
    {
        JsonObject Cmp(int kind, double v) => new()
        {
            ["Comparison"] = new JsonObject
            {
                ["ComparisonKind"] = kind,
                ["Left"] = input.DeepClone(),
                ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = v.ToString(Inv) + "D" } },
            },
        };
        return new JsonObject
        {
            ["And"] = new JsonObject
            {
                ["Left"] = Cmp(2, min),   // value >= min
                ["Right"] = Cmp(3, max),  // value <  max
            },
        };
    }

    // ============================================================================================
    //  STRUCTURE & ANALYTICS - grouping, z-order, page type & tooltip, analytics reference lines,
    //  and the conditional-format variants (font-colour rules, data bars, icon rules). All edit the
    //  legacy Report/Layout JSON, reusing the same container/config helpers as everything above.
    // ============================================================================================

    // -------------------------------------------------------------------- grouping
    /// <summary>The parsed config of every visualContainer on a section, paired with its live container,
    /// so a caller can read config.name / parentGroupName / singleVisual and write the config back.</summary>
    private static IEnumerable<(JsonObject vc, JsonObject co)> Configs(JsonObject section)
    {
        foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            if (node is JsonObject vc && (string?)vc["config"] is string cfg && JsonNode.Parse(cfg) is JsonObject co)
                yield return (vc, co);
    }

    /// <summary>
    /// GROUP a set of visuals: creates a group visualContainer (a config WITHOUT a singleVisual, carrying
    /// "isGroup"=true) and stamps each named child's config with parentGroupName = the group's name. The
    /// group's bounds wrap the children. Matches the legacy-Layout grouping shape (group container + each
    /// child's parentGroupName).
    /// </summary>
    public object GroupVisuals(string reportSessionId, string page, IReadOnlyList<string> visualNames, string? groupName)
    {
        if (visualNames.Count < 2) throw new ArgumentException("group needs at least two visuals.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);

        // locate the named children (and their bounds for the wrapping group rectangle)
        var wanted = new HashSet<string>(visualNames, StringComparer.Ordinal);
        var children = new List<(JsonObject vc, JsonObject co)>();
        foreach (var (vc, co) in Configs(section))
            if ((string?)co["name"] is string n && wanted.Contains(n)) children.Add((vc, co));
        if (children.Count != wanted.Count)
        {
            var found = children.Select(c => (string?)c.co["name"]).ToHashSet();
            var missing = visualNames.Where(v => !found.Contains(v));
            throw new InvalidOperationException($"these visuals were not found on '{page}': {string.Join(", ", missing)}.");
        }

        string grpId = NewVisualId();
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue, minZ = double.MaxValue;
        foreach (var (vc, _) in children)
        {
            double x = Num(vc["x"]) ?? 0, y = Num(vc["y"]) ?? 0, w = Num(vc["width"]) ?? 0, h = Num(vc["height"]) ?? 0, z = Num(vc["z"]) ?? 0;
            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
            maxX = Math.Max(maxX, x + w); maxY = Math.Max(maxY, y + h);
            minZ = Math.Min(minZ, z);
        }

        // stamp each child with the group name (parentGroupName lives in the child's config)
        foreach (var (vc, co) in children)
        {
            co["parentGroupName"] = grpId;
            vc["config"] = co.ToJsonString(JsonOpts);
        }

        // the group container: a config with NO singleVisual, flagged isGroup, sitting just below its children
        double gz = minZ > 0 ? minZ - 1 : 0;
        var groupConfig = new JsonObject
        {
            ["name"] = grpId,
            ["layouts"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = 0,
                    ["position"] = new JsonObject
                    {
                        ["x"] = minX, ["y"] = minY, ["z"] = gz,
                        ["width"] = maxX - minX, ["height"] = maxY - minY, ["tabOrder"] = (int)gz,
                    },
                },
            },
            ["singleVisualGroup"] = new JsonObject
            {
                ["displayName"] = string.IsNullOrWhiteSpace(groupName) ? "Group" : groupName!,
                ["isHidden"] = false,
                ["groupMode"] = "ScaleMode",
            },
        };
        (section["visualContainers"] as JsonArray)!.Add(new JsonObject
        {
            ["x"] = minX, ["y"] = minY, ["z"] = gz, ["width"] = maxX - minX, ["height"] = maxY - minY,
            ["config"] = groupConfig.ToJsonString(JsonOpts),
            ["filters"] = "[]",
        });

        session.Dirty = true;
        return new { ok = true, page, groupName = grpId, displayName = string.IsNullOrWhiteSpace(groupName) ? "Group" : groupName, grouped = children.Count };
    }

    /// <summary>UNGROUP: clear parentGroupName from every child whose parentGroupName is the group name, and
    /// remove the group container itself. Reverses GroupVisuals.</summary>
    public object UngroupVisuals(string reportSessionId, string page, string groupName)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var containers = (section["visualContainers"] as JsonArray)!;

        int freed = 0;
        bool removedContainer = false;
        for (int i = containers.Count - 1; i >= 0; i--)
        {
            if (containers[i] is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co) continue;

            // the group container itself: has this name AND no singleVisual -> remove it
            if ((string?)co["name"] == groupName && co["singleVisual"] is null)
            {
                containers.RemoveAt(i);
                removedContainer = true;
                continue;
            }
            // a child of the group -> free it
            if ((string?)co["parentGroupName"] == groupName)
            {
                co.Remove("parentGroupName");
                vc["config"] = co.ToJsonString(JsonOpts);
                freed++;
            }
        }
        if (!removedContainer && freed == 0)
            throw new InvalidOperationException($"group '{groupName}' not found on page '{page}'.");

        session.Dirty = true;
        return new { ok = true, page, groupName, ungrouped = freed, groupRemoved = removedContainer };
    }

    // -------------------------------------------------------------------- z-order
    /// <summary>Set a visual's z (stack order). Writes BOTH the container z and the config
    /// layouts[0].position.z / tabOrder so the order sticks on open.</summary>
    public object SetVisualZOrder(string reportSessionId, string page, string visual, double z)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, _) = FindVisual(section, visual);
        ApplyZ(vc, co, z);
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, z };
    }

    /// <summary>Bring a visual to the FRONT (z = current max on the page + 1).</summary>
    public object BringToFront(string reportSessionId, string page, string visual)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, _) = FindVisual(section, visual);
        double max = PageZRange(section).max;
        double z = max + 1;
        ApplyZ(vc, co, z);
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, z, sentTo = "front" };
    }

    /// <summary>Send a visual to the BACK (z = current min on the page - 1).</summary>
    public object SendToBack(string reportSessionId, string page, string visual)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, _) = FindVisual(section, visual);
        double min = PageZRange(section).min;
        double z = min - 1;
        ApplyZ(vc, co, z);
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, z, sentTo = "back" };
    }

    /// <summary>The min and max container z across the page (0/0 when the page is empty).</summary>
    private static (double min, double max) PageZRange(JsonObject section)
    {
        double min = double.MaxValue, max = double.MinValue;
        foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
            if (node is JsonObject vc && Num(vc["z"]) is double z) { min = Math.Min(min, z); max = Math.Max(max, z); }
        if (min == double.MaxValue) return (0, 0);
        return (min, max);
    }

    /// <summary>Write z onto the container and (if present) the config layouts[0].position.z + tabOrder.</summary>
    private static void ApplyZ(JsonObject vc, JsonObject co, double z)
    {
        vc["z"] = z;
        if (co["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject pos)
        {
            pos["z"] = z;
            pos["tabOrder"] = (int)z;
        }
    }

    // -------------------------------------------------------------------- page type & tooltip
    /// <summary>
    /// Set a page's TYPE: standard | tooltip | drillthrough. A tooltip page sets the small tooltip canvas
    /// (320x240, displayOption Actual) and flags the page as a tooltip in its config (type=Tooltip). A
    /// drillthrough page is flagged Drillthrough. Writes the page-type into section.config.objects.pageInformation.
    /// </summary>
    public object SetPageType(string reportSessionId, string page, string type)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        string t = (type ?? "standard").Trim().ToLowerInvariant();
        string ptype = t switch
        {
            "tooltip" => "Tooltip",
            "drillthrough" or "drill-through" => "Drillthrough",
            "standard" or "default" => "Standard",
            _ => throw new ArgumentException($"unknown page type '{type}' (use standard|tooltip|drillthrough)."),
        };

        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var objects = (cfg["objects"] as JsonObject) ?? new JsonObject(); cfg["objects"] = objects;
        objects["pageInformation"] = new JsonArray
        {
            new JsonObject { ["properties"] = new JsonObject { ["type"] = Lit($"'{ptype}'") } },
        };
        section["config"] = cfg.ToJsonString(JsonOpts);

        // a tooltip page is a small canvas sized to "Actual" (displayOption 2)
        if (ptype == "Tooltip")
        {
            section["width"] = 320;
            section["height"] = 240;
            section["displayOption"] = 2;
        }

        session.Dirty = true;
        return new { ok = true, page, type = ptype, canvas = ptype == "Tooltip" ? "320x240" : null };
    }

    /// <summary>
    /// Point a visual's report-page TOOLTIP at a tooltip page: writes singleVisual.vcObjects.visualTooltip
    /// with type='ReportPage' (canvasTooltip) and section = the tooltip page's name. The tooltip page should
    /// already be flagged with set_page_type tooltip.
    /// </summary>
    public object SetVisualTooltipPage(string reportSessionId, string page, string visual, string tooltipPage)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var section = FindSection(root, page);
        var (vc, co, sv) = FindVisual(section, visual);
        string tipName = ResolvePageName(root, tooltipPage);

        var vcObjects = (sv["vcObjects"] as JsonObject) ?? new JsonObject(); sv["vcObjects"] = vcObjects;
        vcObjects["visualTooltip"] = new JsonArray
        {
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["show"] = Lit("true"),
                    ["type"] = Lit("'ReportPage'"),
                    ["section"] = Lit($"'{tipName}'"),
                },
            },
        };
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, tooltipPage = tipName };
    }

    /// <summary>
    /// Wire a TOOLTIP PAGE's field binding: a report-page tooltip can be bound to specific fields so it only
    /// shows when hovering those fields (the "Tooltip" page binding fields). fields = { table, field } entries.
    /// Writes the tooltip page's config.pageBinding { type=Tooltip, parameters:[ {Expression: column} ] } - the
    /// PBIR pageBinding.parameters wiring. The page should already be set_page_type tooltip.
    /// FLAG (Desktop validation): legacy Report/Layout carries the tooltip page binding on the page config; the
    /// exact parameters shape (Expression per field) should be confirmed in Desktop.
    /// </summary>
    public object SetTooltipFieldBinding(string reportSessionId, string tooltipPage, IReadOnlyList<(string table, string field)> fields)
    {
        if (fields.Count == 0) throw new ArgumentException("at least one tooltip field { table, field } is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, tooltipPage);

        var parameters = new JsonArray();
        foreach (var (table, field) in fields)
            parameters.Add(new JsonObject
            {
                ["Expression"] = new JsonObject
                {
                    ["Column"] = new JsonObject
                    {
                        ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } },
                        ["Property"] = field,
                    },
                },
            });

        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        cfg["pageBinding"] = new JsonObject { ["type"] = "Tooltip", ["parameters"] = parameters };
        section["config"] = cfg.ToJsonString(JsonOpts);

        session.Dirty = true;
        return new { ok = true, tooltipPage = (string?)section["name"],
            boundFields = fields.Select(f => $"{f.table}[{f.field}]").ToArray(), count = fields.Count,
            slot = "section.config.pageBinding.parameters", note = "Tooltip-page field binding (confirm in Desktop)." };
    }

    /// <summary>
    /// Add EXTRA FIELDS to a visual's DEFAULT (data) tooltip: each becomes a Tooltips-well projection so the
    /// value shows in the default hover tooltip. fields = { table, field, kind } (kind=measure|column). Adds the
    /// projections + the prototypeQuery Select entries so the fields participate in the visual's query. Distinct
    /// from set_visual_tooltip_page (which swaps the whole tooltip for a report page).
    /// </summary>
    public object AddTooltipFields(string reportSessionId, string page, string visual, IReadOnlyList<FieldBinding> fields)
    {
        if (fields.Count == 0) throw new ArgumentException("at least one tooltip field is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        var projections = (sv["projections"] as JsonObject) ?? new JsonObject(); sv["projections"] = projections;
        var tooltipsArr = (projections["Tooltips"] as JsonArray) ?? new JsonArray(); projections["Tooltips"] = tooltipsArr;

        var pq = (sv["prototypeQuery"] as JsonObject) ?? new JsonObject(); sv["prototypeQuery"] = pq;
        if (pq["Version"] is null) pq["Version"] = 2;
        var from = (pq["From"] as JsonArray) ?? new JsonArray(); pq["From"] = from;
        var select = (pq["Select"] as JsonArray) ?? new JsonArray(); pq["Select"] = select;

        var added = new List<string>();
        foreach (var b in fields)
        {
            string queryRef = $"{b.Table}.{b.Field}";
            // reuse the existing From alias for the table, or add one.
            string? alias = from.OfType<JsonObject>().FirstOrDefault(f => (string?)f["Entity"] == b.Table)?["Name"]?.ToString();
            if (alias is null)
            {
                alias = "t" + (from.Count + 1);
                from.Add(new JsonObject { ["Name"] = alias, ["Entity"] = b.Table, ["Type"] = 0 });
            }
            // add the Select projection if not already present.
            if (!select.OfType<JsonObject>().Any(s => (string?)s["Name"] == queryRef))
            {
                var sel = new JsonObject();
                sel[b.Kind.Equals("measure", StringComparison.OrdinalIgnoreCase) ? "Measure" : "Column"] =
                    new JsonObject { ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = alias } }, ["Property"] = b.Field };
                sel["Name"] = queryRef;
                select.Add(sel);
            }
            // add the Tooltips-well projection if not already present.
            if (!tooltipsArr.OfType<JsonObject>().Any(t => (string?)t["queryRef"] == queryRef))
            {
                tooltipsArr.Add(new JsonObject { ["queryRef"] = queryRef });
                added.Add(queryRef);
            }
        }

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, tooltipFields = added.ToArray(), count = added.Count };
    }

    // -------------------------------------------------------------------- analytics reference lines
    /// <summary>
    /// Add an ANALYTICS line to a chart: kind = constant | min | max | average | median | trend | forecast.
    /// Each lives in singleVisual.objects under its own object id (y1AxisReferenceLine for the value-driven
    /// lines, trend for a trend line, forecast for a forecast), as an array of { properties } with
    /// Encode-wrapped values. A constant line uses an explicit value; min/max/average/median compute from a
    /// measure (passed as measure). Structure matched to the legacy-Layout analytics shape - VERIFY in Desktop.
    /// </summary>
    public object AddAnalyticsLine(string reportSessionId, string page, string visual, string kind,
        double? value, string? measureTable, string? measure, string? label, string? color)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);
        string k = (kind ?? "").Trim().ToLowerInvariant();

        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        string lineColor = string.IsNullOrWhiteSpace(color) ? "#E81123" : color!;
        string selectorId = NewVisualId()[..16];   // each analytics line carries its own selector id

        string objectName;
        var props = new JsonObject { ["show"] = Encode("true", FmtKind.Bool) };

        switch (k)
        {
            case "constant":
            {
                if (value is null) throw new ArgumentException("a constant line needs a value.");
                objectName = "y1AxisReferenceLine";
                props["value"] = Encode(value.Value.ToString(Inv), FmtKind.Number);
                props["lineColor"] = Encode(lineColor, FmtKind.Color);
                break;
            }
            case "min":
            case "max":
            case "average":
            case "median":
            {
                if (string.IsNullOrWhiteSpace(measure) || string.IsNullOrWhiteSpace(measureTable))
                    throw new ArgumentException($"a {k} line needs measureTable + measure.");
                objectName = "y1AxisReferenceLine";
                // the aggregate type the line computes (Power BI's referenceLine "type" enum value)
                string aggType = k switch { "min" => "Min", "max" => "Max", "average" => "Average", _ => "Median" };
                props["type"] = Encode(aggType, FmtKind.Text);   // Encode single-quotes the enum
                props["measure"] = MeasureExpr(measureTable!, measure!);
                props["lineColor"] = Encode(lineColor, FmtKind.Color);
                break;
            }
            case "trend":
            {
                objectName = "trend";
                props["lineColor"] = Encode(lineColor, FmtKind.Color);
                break;
            }
            case "forecast":
            {
                objectName = "forecast";
                if (measure != null && measureTable != null) props["measure"] = MeasureExpr(measureTable, measure);
                props["confidenceBandStyle"] = Encode("fillAndLine", FmtKind.Text);
                break;
            }
            default:
                throw new ArgumentException($"unknown analytics kind '{kind}' (use constant|min|max|average|median|trend|forecast).");
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            props["dataLabelShow"] = Encode("true", FmtKind.Bool);
            props["text"] = Encode(label!, FmtKind.Text);
        }

        var arr = (objects[objectName] as JsonArray) ?? new JsonArray(); objects[objectName] = arr;
        arr.Add(new JsonObject
        {
            ["properties"] = props,
            ["selector"] = new JsonObject { ["id"] = selectorId },
        });

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, analyticsLine = k, objectName, color = lineColor };
    }

    /// <summary>A measure-bound expression (the same Measure/SourceRef shape the conditional-format Input uses).</summary>
    private static JsonObject MeasureExpr(string table, string measure) => new()
    {
        ["expr"] = new JsonObject
        {
            ["Measure"] = new JsonObject
            {
                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } },
                ["Property"] = measure,
            },
        },
    };

    // -------------------------------------------------------------------- CF variants (font colour, data bars, icons)
    /// <summary>FONT-COLOUR rules: the same discrete FillRule builder as set_conditional_formatting, applied to
    /// the measure's fontColor instead of backColor. Each rule { min, max, color } paints the text that colour
    /// when the value is in [min, max).</summary>
    public object SetFontColorRules(string reportSessionId, string page, string visual,
        string measureTable, string measure, IReadOnlyList<(double min, double max, string color)> rules)
        => SetConditionalFormattingRules(reportSessionId, page, visual, measureTable, measure, rules, "values", "fontColor");

    /// <summary>
    /// DATA BARS on a measure column in a table/matrix: a positive bar colour and an optional negative bar
    /// colour, driven by the measure. Writes the dataBars object onto the measure's column selector (same
    /// selector shape the FillRule builder uses). Structure matched to the legacy-Layout dataBars shape -
    /// VERIFY in Desktop.
    /// </summary>
    public object SetDataBars(string reportSessionId, string page, string visual,
        string measureTable, string measure, string positiveColor, string? negativeColor,
        bool reverseDirection = false, bool hideText = false, string? axisColor = null,
        double? minValue = null, double? maxValue = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        var (props, _) = ColumnFormatEntry(sv, $"{measureTable}.{measure}", "values");
        // min/max axis bounds: when an explicit value is supplied, write a numeric Literal stop; otherwise an
        // "Auto" rule so Power BI computes the data extent. The OLD bug wrote the SAME measure expr to both
        // minValue and maxValue (min==max -> bars never render). Default to auto-min / auto-max instead.
        JsonObject Bound(double? v) => v.HasValue
            ? new JsonObject { ["Literal"] = new JsonObject { ["Value"] = v.Value.ToString(Inv) + "D" } }
            : new JsonObject { ["Auto"] = new JsonObject() };

        props["dataBars"] = new JsonObject
        {
            ["minValue"] = Bound(minValue),
            ["maxValue"] = Bound(maxValue),
            ["positiveColor"] = ColorObj(positiveColor),
            ["negativeColor"] = ColorObj(string.IsNullOrWhiteSpace(negativeColor) ? "#FF0000" : negativeColor!),
            ["axisColor"] = ColorObj(NormalizeHex(axisColor) ?? "#000000"),
            ["reverseDirection"] = Lit(reverseDirection ? "true" : "false"),
            ["hideText"] = Lit(hideText ? "true" : "false"),
        };

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, dataBars = $"{measureTable}[{measure}]", positiveColor,
            negativeColor = string.IsNullOrWhiteSpace(negativeColor) ? "#FF0000" : negativeColor,
            reverseDirection, hideText, axisColor = NormalizeHex(axisColor) ?? "#000000",
            minValue = minValue.HasValue ? (object)minValue.Value : "auto",
            maxValue = maxValue.HasValue ? (object)maxValue.Value : "auto" };
    }

    /// <summary>
    /// ICON rules on a measure column: each rule { min, max, color } maps a value band to an icon. Reuses the
    /// same band condition as the FillRule builder; writes an icon RuleDefinition with one rule per band.
    /// Structure matched to the legacy-Layout icon-rule shape - VERIFY in Desktop.
    /// </summary>
    public object SetIconRules(string reportSessionId, string page, string visual,
        string measureTable, string measure, IReadOnlyList<(double min, double max, string color)> rules,
        IReadOnlyList<string>? glyphs = null, string? iconSet = null, string? layout = null)
    {
        if (rules.Count == 0) throw new ArgumentException("at least one rule { min, max, color } is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        var (props, _) = ColumnFormatEntry(sv, $"{measureTable}.{measure}", "values");
        var input = new JsonObject { ["Measure"] = new JsonObject {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = measureTable } }, ["Property"] = measure } };

        // each band -> an icon rule: same [min,max) Condition as backColor bands; the IconId is the picked glyph
        // (e.g. 'ArrowUp', 'ThreeFlags1') when given, else the band colour (the legacy colour-keyed shape).
        var ruleArr = new JsonArray();
        for (int i = 0; i < rules.Count; i++)
        {
            var (min, max, color) = rules[i];
            string iconId = glyphs != null && i < glyphs.Count && !string.IsNullOrWhiteSpace(glyphs[i]) ? glyphs[i] : color;
            ruleArr.Add(new JsonObject
            {
                ["Condition"] = BandCondition(input, min, max),
                ["IconId"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{iconId.Replace("'", "''")}'" } },
            });
        }

        props["icon"] = new JsonObject
        {
            ["solid"] = new JsonObject
            {
                ["color"] = new JsonObject
                {
                    ["expr"] = new JsonObject
                    {
                        ["FillRule"] = new JsonObject
                        {
                            ["Input"] = input,
                            ["FillRule"] = new JsonObject { ["ruleDefinition"] = new JsonObject { ["rules"] = ruleArr } },
                        },
                    },
                },
            },
        };
        // icon-set + layout presentation (the "icons" card): which glyph family + where the icon sits.
        if (!string.IsNullOrWhiteSpace(iconSet)) props["iconSet"] = Lit($"'{iconSet!.Replace("'", "''")}'");
        if (!string.IsNullOrWhiteSpace(layout))
        {
            string lay = layout!.Trim().ToLowerInvariant() switch
            {
                "left" => "DataAndIcon", "right" => "IconAndData", "icon-only" or "icononly" or "icon" => "IconOnly",
                _ => layout!,
            };
            props["iconLayout"] = Lit($"'{lay}'");
        }

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, icons = $"{measureTable}[{measure}]", rules = rules.Count,
            iconSet, layout, glyphs = glyphs?.ToArray() };
    }

    /// <summary>
    /// FORMAT-BY-FIELD-VALUE conditional formatting: a measure returns a hex/CSS colour string and that colour
    /// drives the cell's background / font / icon directly (the "Format by: Field value" option). target =
    /// background -> values.backColor, font -> values.fontColor, icon -> values.icon. Reuses the {measure} expr
    /// shorthand (MeasureBoundExpr) so the colour is a true Measure-bound expr (NOT a FillRule) - the colour the
    /// measure returns IS the colour. colorMeasure = "Table[Measure]" (or pass table separately).
    /// </summary>
    public object SetFieldValueCf(string reportSessionId, string page, string visual, string column,
        string target, string colorMeasure, string? colorMeasureTable = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        string prop = (target ?? "background").Trim().ToLowerInvariant() switch
        {
            "background" or "bg" or "backcolor" => "backColor",
            "font" or "fontcolor" or "text" => "fontColor",
            "icon" => "icon",
            _ => throw new ArgumentException($"unknown target '{target}' (use background|font|icon)."),
        };

        // lift the measure to a Measure-bound expr via the shared shorthand resolver (Wave M).
        var bound = MeasureBoundExpr(new JsonObject
        {
            ["measure"] = colorMeasure,
            ["table"] = colorMeasureTable,
        }) ?? throw new ArgumentException("colorMeasure must be \"Table[Measure]\" or pass colorMeasureTable.");

        var (props, _) = ColumnFormatEntry(sv, column, "values");
        // background/font are a solid colour driven by the measure-returned hex; icon is the icon colour.
        props[prop] = new JsonObject { ["solid"] = new JsonObject { ["color"] = bound.DeepClone() } };

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, column, target = prop, colorMeasure, kind = "field-value (measure-bound expr)" };
    }

    /// <summary>
    /// WEB URL conditional formatting: a measure returns a URL string and that drives the cell's web URL (the
    /// "Web URL" CF option, so a table cell becomes a clickable link). Writes values.webURL as a Measure-bound
    /// expr on the target column - reusing the {measure} expr shorthand. urlMeasure = "Table[Measure]".
    /// </summary>
    public object SetWebUrlCf(string reportSessionId, string page, string visual, string column,
        string urlMeasure, string? urlMeasureTable = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = FindVisual(section, visual);

        var bound = MeasureBoundExpr(new JsonObject
        {
            ["measure"] = urlMeasure,
            ["table"] = urlMeasureTable,
        }) ?? throw new ArgumentException("urlMeasure must be \"Table[Measure]\" or pass urlMeasureTable.");

        var (props, _) = ColumnFormatEntry(sv, column, "values");
        props["webURL"] = bound.DeepClone();   // the measure-returned string becomes the cell's web URL

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, column, urlMeasure, kind = "web-url (measure-bound expr)" };
    }

    /// <summary>Find (or create) the values-object entry whose selector targets a column's queryRef, returning
    /// its live properties bag (so a caller can add dataBars / icon / backColor onto the same column). Mirrors
    /// the selector shape SetConditionalFormattingRules writes.</summary>
    private static (JsonObject props, JsonObject entry) ColumnFormatEntry(JsonObject sv, string queryRef, string objectName)
    {
        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        var arr = (objects[objectName] as JsonArray) ?? new JsonArray(); objects[objectName] = arr;
        JsonObject? entry = null;
        foreach (var n in arr)
            if (n is JsonObject e && (string?)e["selector"]?["metadata"] == queryRef) { entry = e; break; }
        if (entry == null) { entry = new JsonObject(); arr.Add(entry); }
        entry["selector"] = new JsonObject
        {
            ["data"] = new JsonArray { new JsonObject { ["dataViewWildcard"] = new JsonObject { ["matchingOption"] = 0 } } },
            ["metadata"] = queryRef,
        };
        var props = (entry["properties"] as JsonObject) ?? new JsonObject(); entry["properties"] = props;
        return (props, entry);
    }

    /// <summary>A solid-colour object literal { solid:{ color:{ expr:{ Literal:{ Value:"'#RRGGBB'" } } } } }.</summary>
    private static JsonObject ColorObj(string hex) => (JsonObject)Lit($"'{hex.Replace("'", "''")}'", wrapSolid: true);

    /// <summary>Reposition / resize an existing visual (any subset of x,y,z,width,height).</summary>
    public object SetVisualBounds(string reportSessionId, string pageName, string visualName,
        double? x, double? y, double? z, double? width, double? height)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = FindVisual(section, visualName);
        _ = sv;
        void SetBoth(string key, double? v) { if (v.HasValue) vc[key] = v.Value; }
        SetBoth("x", x); SetBoth("y", y); SetBoth("z", z); SetBoth("width", width); SetBoth("height", height);
        if (co["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject pos)
        {
            if (x.HasValue) pos["x"] = x.Value;
            if (y.HasValue) pos["y"] = y.Value;
            if (z.HasValue) pos["z"] = z.Value;
            if (width.HasValue) pos["width"] = width.Value;
            if (height.HasValue) pos["height"] = height.Value;
        }
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visualName, x, y, z, width, height };
    }

    // ============================================================================================
    //  UNIVERSAL VISUAL FORMATTING - total customizability: read/set ANY formatting property on ANY
    //  visual. Formatting lives in two buckets on singleVisual:
    //    - vcObjects  : container-level (title, background, border, dropShadow, visualHeader, ...)
    //    - objects    : visual-type-specific (labels, legend, categoryAxis, valueAxis, dataPoint, ...)
    //  Each object is an ARRAY of { properties: {...} } (one element usually; may carry a "selector").
    //  Property values are wrapped expressions; the Encode/Decode pair below round-trips them.
    // ============================================================================================

    /// <summary>The value kinds a formatting property can take. Text/Enum/Color wrap the value in
    /// single quotes inside the literal; Bool/Number do not (Number doubles carry a trailing 'D').</summary>
    internal enum FmtKind { Bool, Number, Text, Color, Enum }

    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>Encode a plain value into a wrapped-expression JsonNode by kind. Round-trips with Decode.
    ///  - Bool   : { expr:{ Literal:{ Value:"true" } } }
    ///  - Number : { expr:{ Literal:{ Value:"100D" } } }            (doubles carry a 'D' suffix)
    ///  - Text   : { expr:{ Literal:{ Value:"'hello'" } } }         (single-quoted inside)
    ///  - Enum   : { expr:{ Literal:{ Value:"'Bookmark'" } } }      (single-quoted inside)
    ///  - Color  : { solid:{ color:{ expr:{ Literal:{ Value:"'#FFFFFF'" } } } } }
    /// </summary>
    internal static JsonNode Encode(string value, FmtKind kind)
    {
        switch (kind)
        {
            case FmtKind.Bool:
                return Lit(value.Trim().ToLowerInvariant() == "true" ? "true" : "false");
            case FmtKind.Number:
            {
                // normalise to an invariant double with the 'D' suffix Power BI uses for doubles
                double d = double.Parse(value, Inv);
                return Lit(d.ToString(Inv) + "D");
            }
            case FmtKind.Color:
                return Lit($"'{value.Replace("'", "''")}'", wrapSolid: true);
            case FmtKind.Text:
            case FmtKind.Enum:
            default:
                return Lit($"'{value.Replace("'", "''")}'");
        }
    }

    /// <summary>Decode a wrapped-expression JsonNode back to its plain value. Inverse of Encode:
    /// strips the expr/Literal (or solid/color/expr/Literal for colours) wrapper, the surrounding single
    /// quotes (text/enum/colour) and the trailing 'D' (numbers). Returns null if the shape is not a literal
    /// we understand (e.g. a measure-bound expression or a conditional-format FillRule) - the caller leaves
    /// such properties out of the simplified view rather than guessing.</summary>
    internal static string? Decode(JsonNode? expr)
    {
        if (expr is null) return null;

        // colour: { solid: { color: <expr> } }  -> recurse into the inner colour expression
        if (expr is JsonObject so && so["solid"] is JsonObject solid && solid["color"] is JsonNode innerColor)
            return Decode(innerColor);

        // the literal value string lives at expr.Literal.Value (possibly nested under a top-level "expr")
        JsonNode? node = expr;
        if (node is JsonObject eo && eo["expr"] is JsonNode e) node = e;
        string? raw = (node as JsonObject)?["Literal"]?["Value"]?.GetValue<string>();
        if (raw is null) return null;
        return Unwrap(raw);
    }

    /// <summary>Unwrap a raw literal string: drop surrounding single quotes (text/enum/colour) and a
    /// trailing numeric 'D'/'L' suffix; leave bools as-is. Used by Decode and by get_visual_format.</summary>
    private static string Unwrap(string raw)
    {
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
            return raw[1..^1].Replace("''", "'");
        if (raw is "true" or "false") return raw;
        // numbers: "100D", "12.5D", "5L"  -> strip a single trailing type suffix
        if (raw.Length > 1 && (raw[^1] is 'D' or 'L') && double.TryParse(raw[..^1], System.Globalization.NumberStyles.Any, Inv, out _))
            return raw[..^1];
        return raw;
    }

    /// <summary>The known-property registry: maps a formatting property id to its value kind so callers
    /// can pass plain values (true / "My Title" / 100 / "#FFFFFF" / "Top") and we encode them correctly.
    /// A property not listed here falls back to InferKind (by the JSON value's own type).</summary>
    private static readonly Dictionary<string, FmtKind> PropKinds = new(StringComparer.OrdinalIgnoreCase)
    {
        // booleans (show/visibility toggles)
        ["show"] = FmtKind.Bool, ["bold"] = FmtKind.Bool, ["italic"] = FmtKind.Bool,
        ["underline"] = FmtKind.Bool, ["wordWrap"] = FmtKind.Bool, ["showAll"] = FmtKind.Bool,
        ["showAxisTitle"] = FmtKind.Bool, ["gridlineShow"] = FmtKind.Bool, ["invertIfNegative"] = FmtKind.Bool,
        // numbers
        ["transparency"] = FmtKind.Number, ["fontSize"] = FmtKind.Number, ["radius"] = FmtKind.Number,
        ["weight"] = FmtKind.Number, ["start"] = FmtKind.Number, ["end"] = FmtKind.Number,
        ["labelPrecision"] = FmtKind.Number, ["labelDisplayUnits"] = FmtKind.Number,
        ["roundEdge"] = FmtKind.Number, ["x"] = FmtKind.Number, ["y"] = FmtKind.Number,
        ["width"] = FmtKind.Number, ["height"] = FmtKind.Number, ["angle"] = FmtKind.Number,
        // colours
        ["color"] = FmtKind.Color, ["fontColor"] = FmtKind.Color, ["fill"] = FmtKind.Color,
        ["fillColor"] = FmtKind.Color, ["lineColor"] = FmtKind.Color, ["defaultColor"] = FmtKind.Color,
        ["backColor"] = FmtKind.Color, ["foreground"] = FmtKind.Color, ["outlineColor"] = FmtKind.Color,
        ["borderColor"] = FmtKind.Color,
        // enums (single-quoted string ids)
        ["alignment"] = FmtKind.Enum, ["position"] = FmtKind.Enum, ["preset"] = FmtKind.Enum,
        ["fontFamily"] = FmtKind.Enum, ["type"] = FmtKind.Enum, ["mode"] = FmtKind.Enum,
        ["tileShape"] = FmtKind.Enum, ["shapeType"] = FmtKind.Enum, ["titleAlignment"] = FmtKind.Enum,
        ["labelOrientation"] = FmtKind.Enum, ["legendPosition"] = FmtKind.Enum,
        // text
        ["text"] = FmtKind.Text, ["titleText"] = FmtKind.Text, ["altText"] = FmtKind.Text,
    };

    /// <summary>Infer a value's kind from its JSON type when the property is not in the registry:
    /// boolean -> Bool, number -> Number, a "#RRGGBB" string -> Color, any other string -> Text.</summary>
    private static FmtKind InferKind(JsonNode? value)
    {
        if (value is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out _)) return FmtKind.Bool;
            if (jv.TryGetValue<double>(out _)) return FmtKind.Number;
            string s = jv.ToString();
            if (System.Text.RegularExpressions.Regex.IsMatch(s, "^#[0-9A-Fa-f]{6}$")) return FmtKind.Color;
            return FmtKind.Text;
        }
        return FmtKind.Text;
    }

    /// <summary>Pick the kind for a property: registry first, then infer from the value.</summary>
    private static FmtKind KindFor(string propName, JsonNode? value) =>
        PropKinds.TryGetValue(propName, out var k) ? k : InferKind(value);

    /// <summary>Resolve a visual on a section by its config name OR (where present) its config displayName,
    /// mirroring the page resolver. Returns the container, parsed config and singleVisual.</summary>
    private (JsonObject vc, JsonObject co, JsonObject sv) ResolveVisual(JsonObject section, string visualRef)
    {
        foreach (var node in (section["visualContainers"] as JsonArray)!)
        {
            if (node is JsonObject vc && (string?)vc["config"] is string cfg &&
                JsonNode.Parse(cfg) is JsonObject co &&
                ((string?)co["name"] == visualRef || (string?)co["displayName"] == visualRef) &&
                co["singleVisual"] is JsonObject sv)
                return (vc, co, sv);
        }
        // fall back to the strict name-only resolver (keeps its identical error message)
        return FindVisual(section, visualRef);
    }

    /// <summary>Decode a whole formatting bucket (vcObjects or objects) to a simplified
    /// { objectName: { propertyName: value } } map. Properties whose value is not a plain literal
    /// (measure-bound expressions, conditional-format FillRules, navigation actions) are skipped so the
    /// view stays a clean snapshot of the literal formatting.</summary>
    private static JsonObject DecodeBucket(JsonObject? bucket)
    {
        var outMap = new JsonObject();
        if (bucket is null) return outMap;
        foreach (var (objName, objNode) in bucket)
        {
            // take the FIRST entry's properties (the default selector); selectors are preserved on write
            JsonObject? props = null;
            if (objNode is JsonArray arr && arr.Count > 0 && arr[0] is JsonObject e0) props = e0["properties"] as JsonObject;
            else if (objNode is JsonObject eo) props = eo["properties"] as JsonObject;
            if (props is null) continue;
            var decoded = new JsonObject();
            foreach (var (propName, propVal) in props)
            {
                string? v = Decode(propVal);
                if (v != null) decoded[propName] = v;
            }
            if (decoded.Count > 0) outMap[objName] = decoded;
        }
        return outMap;
    }

    /// <summary>get_visual_format: the DECODED, simplified current formatting of one visual. Read-only.
    /// Returns its type, position, the decoded vcObjects + objects maps, and its field bindings.</summary>
    public object GetVisualFormat(string reportSessionId, string pageName, string visualName)
    {
        var section = FindSection(_sessions.GetReport(reportSessionId).Layout.Root, pageName);
        var (vc, co, sv) = ResolveVisual(section, visualName);

        var pos = (co["layouts"] as JsonArray)?.FirstOrDefault() is JsonObject l0 && l0["position"] is JsonObject p
            ? new { x = Num(p["x"]), y = Num(p["y"]), w = Num(p["width"]), h = Num(p["height"]) }
            : new { x = Num(vc["x"]), y = Num(vc["y"]), w = Num(vc["width"]), h = Num(vc["height"]) };

        // field bindings (role -> table.field), read off the projections
        var fields = new List<object>();
        if (sv["projections"] is JsonObject proj)
            foreach (var (role, refs) in proj)
                if (refs is JsonArray ra)
                    foreach (var r in ra)
                        if ((string?)r?["queryRef"] is string q) fields.Add(new { role, queryRef = q });

        return new
        {
            ok = true,
            visual = (string?)co["name"],
            type = (string?)sv["visualType"],
            position = pos,
            vcObjects = DecodeBucket(sv["vcObjects"] as JsonObject),
            objects = DecodeBucket(sv["objects"] as JsonObject),
            fields,
        };
    }

    /// <summary>Merge one decoded property into a formatting bucket, encoding by kind. Creates the object
    /// array + properties if absent, updates the property in place if present, and PRESERVES every other
    /// property/object and any existing selector. Returns the (object, property) pair touched.</summary>
    internal static (string obj, string prop) MergeProperty(JsonObject bucket, string objName, string propName, JsonNode? value)
    {
        var arr = bucket[objName] as JsonArray;
        if (arr is null) { arr = new JsonArray(); bucket[objName] = arr; }
        JsonObject entry;
        if (arr.Count > 0 && arr[0] is JsonObject e0) entry = e0;
        else { entry = new JsonObject { ["properties"] = new JsonObject() }; arr.Add(entry); }
        var props = entry["properties"] as JsonObject;
        if (props is null) { props = new JsonObject(); entry["properties"] = props; }

        props[propName] = EncodeValue(propName, value);
        return (objName, propName);
    }

    /// <summary>The single value-encoding rule shared by every structured-formatting write path
    /// (set_visual_format and the selector setter). A value that is ALREADY structured PBI JSON is preserved
    /// verbatim (deep-cloned) so callers can write any pre-shaped property - images, FillRule, dataBars,
    /// gradient stops, reference lines - without the encoder stringifying it into a broken literal:
    ///   - a JsonObject carrying expr/solid (a wrapped expression / colour)               -> taken as-is
    ///   - a JsonObject of shape { measure:"Table[Measure]" } or { column:"Table[Col]" }  -> a measure/column
    ///     -bound expr (drive ANY property by a measure, even where the UI shows no fx button)
    ///   - ANY OTHER JsonObject or JsonArray                                              -> taken as-is
    ///     (pre-shaped structured PBI JSON: plotArea image, errorBars, anomalyDetection, FillRule, ...)
    /// Only a true scalar (bool / number / string / hex colour) is routed through Encode by the property kind.</summary>
    internal static JsonNode EncodeValue(string propName, JsonNode? value)
    {
        if (value is JsonObject vo)
        {
            if (vo.ContainsKey("expr") || vo.ContainsKey("solid")) return vo.DeepClone();
            if (MeasureBoundExpr(vo) is JsonNode mbe) return mbe;
            return vo.DeepClone();                 // arbitrary structured PBI JSON - write verbatim
        }
        if (value is JsonArray va) return va.DeepClone();   // pre-shaped array (e.g. paragraphs/runs) - verbatim
        return Encode(value?.ToString() ?? "", KindFor(propName, value));
    }

    /// <summary>Recognise a measure/column-bound shorthand value and lift it to a wrapped expr node:
    /// { "measure":"Table[Measure]" } or { "measure":"Measure", "table":"Table" }  -> expr.Measure{...}
    /// { "column":"Table[Col]"     } or { "column":"Col", "table":"Table" }         -> expr.Column{...}
    /// This is the "drive any property by a measure" capability (e.g. a conditional title/colour where the
    /// Desktop UI shows no fx). Returns null when value is not a measure/column shorthand.</summary>
    private static JsonNode? MeasureBoundExpr(JsonObject vo)
    {
        bool isMeasure = vo["measure"] is JsonValue;
        bool isColumn = vo["column"] is JsonValue;
        if (!isMeasure && !isColumn) return null;
        string raw = (isMeasure ? vo["measure"] : vo["column"])!.ToString();
        string? table = (string?)vo["table"];
        string field = raw;
        // accept the "Table[Field]" shorthand and split it.
        int lb = raw.IndexOf('[');
        if (lb >= 0 && raw.EndsWith("]"))
        {
            if (string.IsNullOrEmpty(table)) table = raw[..lb];
            field = raw[(lb + 1)..^1];
        }
        if (string.IsNullOrWhiteSpace(table))
            throw new ArgumentException($"a measure/column-bound value needs a table - pass \"Table[{field}]\" or add \"table\".");
        var inner = new JsonObject
        {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } },
            ["Property"] = field,
        };
        return new JsonObject { ["expr"] = new JsonObject { [isMeasure ? "Measure" : "Column"] = inner } };
    }

    /// <summary>set_visual_format: encode each property by kind and MERGE it into the visual's
    /// vcObjects/objects buckets without clobbering untouched objects or properties. formatJson shape:
    /// { "vcObjects": { "title": {"show":true,"text":"My Title"} }, "objects": { "legend": {"show":false} } }.
    /// Returns a summary of the objects/properties changed.</summary>
    public object SetVisualFormat(string reportSessionId, string pageName, string visualName, string formatJson)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var (vc, co, sv) = ResolveVisual(section, visualName);

        var spec = JsonNode.Parse(formatJson) as JsonObject
                   ?? throw new InvalidOperationException("formatJson must be a JSON object with vcObjects and/or objects.");

        var changed = new List<string>();
        void ApplyBucket(string bucketKey)
        {
            if (spec[bucketKey] is not JsonObject objs) return;
            var bag = sv[bucketKey] as JsonObject;
            if (bag is null) { bag = new JsonObject(); sv[bucketKey] = bag; }
            foreach (var (objName, propsNode) in objs)
            {
                if (propsNode is not JsonObject propsMap) continue;
                foreach (var (propName, val) in propsMap)
                {
                    var (o, pr) = MergeProperty(bag, objName, propName, val);
                    changed.Add($"{bucketKey}.{o}.{pr}");
                }
            }
        }
        ApplyBucket("vcObjects");
        ApplyBucket("objects");

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visual = (string?)co["name"], changed = changed.ToArray(), propertiesChanged = changed.Count };
    }

    /// <summary>Generic engine entry the typed wrappers delegate to: build a formatJson from a list of
    /// (bucket, object, property, value) tuples and merge it in. value strings are encoded by registry kind.</summary>
    private object SetFormatProps(string reportSessionId, string pageName, string visualName,
        IEnumerable<(string bucket, string obj, string prop, string? value)> props)
    {
        var vcb = new JsonObject();
        var objb = new JsonObject();
        foreach (var (bucket, objName, propName, value) in props)
        {
            if (value is null) continue;
            var into = bucket == "vcObjects" ? vcb : objb;
            var o = into[objName] as JsonObject;
            if (o is null) { o = new JsonObject(); into[objName] = o; }
            // type the value so SetVisualFormat's encoder picks the right kind: bools/numbers as native
            // JSON, everything else as a string (the registry then maps color/enum/text correctly).
            FmtKind k = KindFor(propName, JsonValue.Create(value));
            o[propName] = k switch
            {
                FmtKind.Bool => JsonValue.Create(value.Trim().ToLowerInvariant() == "true"),
                FmtKind.Number => JsonValue.Create(double.Parse(value, Inv)),
                _ => JsonValue.Create(value),
            };
        }
        var spec = new JsonObject();
        if (vcb.Count > 0) spec["vcObjects"] = vcb;
        if (objb.Count > 0) spec["objects"] = objb;
        return SetVisualFormat(reportSessionId, pageName, visualName, spec.ToJsonString(JsonOpts));
    }

    // ---- typed convenience wrappers (each delegates to the generic engine) ----

    /// <summary>Set a visual's TITLE: any subset of show/text/fontColor/alignment/fontSize.</summary>
    public object SetVisualTitle(string reportSessionId, string pageName, string visualName,
        bool? show, string? text, string? fontColor, string? alignment, double? fontSize)
        => SetFormatProps(reportSessionId, pageName, visualName, new (string, string, string, string?)[]
        {
            ("vcObjects", "title", "show", show?.ToString().ToLowerInvariant()),
            ("vcObjects", "title", "text", text),
            ("vcObjects", "title", "fontColor", fontColor),
            ("vcObjects", "title", "alignment", alignment),
            ("vcObjects", "title", "fontSize", fontSize?.ToString(Inv)),
        });

    /// <summary>Set a visual's BACKGROUND: any subset of show/color/transparency (transparency = overlay).</summary>
    public object SetVisualBackground(string reportSessionId, string pageName, string visualName,
        bool? show, string? color, double? transparency)
        => SetFormatProps(reportSessionId, pageName, visualName, new (string, string, string, string?)[]
        {
            ("vcObjects", "background", "show", show?.ToString().ToLowerInvariant()),
            ("vcObjects", "background", "color", color),
            ("vcObjects", "background", "transparency", transparency?.ToString(Inv)),
        });

    /// <summary>Set a visual's BORDER: any subset of show/color/radius.</summary>
    public object SetVisualBorder(string reportSessionId, string pageName, string visualName,
        bool? show, string? color, double? radius)
        => SetFormatProps(reportSessionId, pageName, visualName, new (string, string, string, string?)[]
        {
            ("vcObjects", "border", "show", show?.ToString().ToLowerInvariant()),
            ("vcObjects", "border", "color", color),
            ("vcObjects", "border", "radius", radius?.ToString(Inv)),
        });

    /// <summary>Set a visual's POSITION/size: updates BOTH the visualContainer x/y/width/height AND the
    /// config layouts[0].position. Any subset of x/y/width/height.</summary>
    public object SetVisualPosition(string reportSessionId, string pageName, string visualName,
        double? x, double? y, double? width, double? height)
        => SetVisualBounds(reportSessionId, pageName, visualName, x, y, null, width, height);

    /// <summary>Show/hide a visual via singleVisual.display.mode. visible=false sets {mode:"hidden"};
    /// visible=true removes the hidden flag. (Wraps the existing SetVisualVisibility(hidden) primitive.)</summary>
    public object SetVisualDisplay(string reportSessionId, string pageName, string visualName, bool visible)
        => SetVisualVisibility(reportSessionId, pageName, visualName, !visible);

    /// <summary>Show/hide data labels (objects.labels.show).</summary>
    public object SetDataLabelsShow(string reportSessionId, string pageName, string visualName, bool show)
        => SetFormatProps(reportSessionId, pageName, visualName, new (string, string, string, string?)[]
        {
            ("objects", "labels", "show", show.ToString().ToLowerInvariant()),
        });

    /// <summary>Show/hide a legend and (optionally) set its position (objects.legend.show / position).</summary>
    public object SetLegendShow(string reportSessionId, string pageName, string visualName, bool? show, string? position)
        => SetFormatProps(reportSessionId, pageName, visualName, new (string, string, string, string?)[]
        {
            ("objects", "legend", "show", show?.ToString().ToLowerInvariant()),
            ("objects", "legend", "position", position),
        });

    // -------------------------------------------------------------------- layout intelligence
    private static readonly HashSet<string> DecorTypes = new(StringComparer.OrdinalIgnoreCase)
        { "basicShape", "image", "shape", "actionButton" };

    /// <summary>Every arrangeable visual on a page: its container, parsed config, name and type.</summary>
    private List<(JsonObject vc, JsonObject co, string name, string type)> EnumVisuals(JsonObject section)
    {
        var list = new List<(JsonObject, JsonObject, string, string)>();
        foreach (var node in (section["visualContainers"] as JsonArray)!)
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co) continue;
            string name = (string?)co["name"] ?? "";
            string type = (string?)co["singleVisual"]?["visualType"] ?? "";
            list.Add((vc, co, name, type));
        }
        return list;
    }

    private void SetBounds(JsonObject vc, JsonObject co, double x, double y, double w, double h)
    {
        vc["x"] = x; vc["y"] = y; vc["width"] = w; vc["height"] = h;
        if (co["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject pos)
        {
            pos["x"] = x; pos["y"] = y; pos["width"] = w; pos["height"] = h;
        }
        vc["config"] = co.ToJsonString(JsonOpts);
    }

    /// <summary>Lay items into rows of `per`, full-width cells, returning the y below the band.</summary>
    private double LayoutBand(List<(JsonObject vc, JsonObject co, string name, string type)> items,
        int per, double startY, double margin, double usable, double gutter, double itemH)
    {
        if (items.Count == 0) return startY;
        per = Math.Max(1, per);
        double cellW = (usable - (per - 1) * gutter) / per;
        double y = startY;
        for (int i = 0; i < items.Count; i++)
        {
            int c = i % per;
            if (i > 0 && c == 0) y += itemH + gutter;
            double x = margin + c * (cellW + gutter);
            SetBounds(items[i].vc, items[i].co, x, y, cellW, itemH);
        }
        return y + itemH + gutter;
    }

    /// <summary>
    /// Auto-arrange a page into a clean, professional grid: header textboxes full-width at the top,
    /// then a slicer filter-bar, then a KPI-card row, then the data visuals in a balanced grid that
    /// fills the remaining height - consistent margins, gutters and alignment throughout. The
    /// 'make this page look designed' one-shot. Decorative shapes/images are left in place.
    /// </summary>
    public object AutoArrange(string reportSessionId, string pageName, double? canvasWidth, double? canvasHeight,
        double margin, double gutter, double headerHeight, double slicerHeight, double kpiHeight, int maxPerRow)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        double W = canvasWidth ?? Num(section["width"]) ?? 1280;
        double H = canvasHeight ?? Num(section["height"]) ?? 720;
        double usable = W - 2 * margin;

        var headers = new List<(JsonObject, JsonObject, string, string)>();
        var slicers = new List<(JsonObject, JsonObject, string, string)>();
        var kpis = new List<(JsonObject, JsonObject, string, string)>();
        var data = new List<(JsonObject, JsonObject, string, string)>();
        foreach (var v in EnumVisuals(section))
        {
            string t = v.type.ToLowerInvariant();
            if (DecorTypes.Contains(v.type)) continue;             // leave panels/images where they are
            if (t == "textbox") headers.Add(v);
            else if (t == "slicer") slicers.Add(v);
            else if (t is "card" or "multirowcard" or "kpi") kpis.Add(v);
            else data.Add(v);
        }

        double y = margin;
        foreach (var h in headers) { SetBounds(h.Item1, h.Item2, margin, y, usable, headerHeight); y += headerHeight + gutter / 2; }
        if (headers.Count > 0) y += gutter / 2;
        if (slicers.Count > 0) y = LayoutBand(slicers, Math.Min(slicers.Count, 6), y, margin, usable, gutter, slicerHeight);
        if (kpis.Count > 0) y = LayoutBand(kpis, Math.Min(kpis.Count, 6), y, margin, usable, gutter, kpiHeight);

        if (data.Count > 0)
        {
            int cols = data.Count switch { 1 => 1, 2 => 2, 3 => 3, 4 => 2, _ => Math.Clamp(maxPerRow, 1, 4) };
            int rows = (int)Math.Ceiling(data.Count / (double)cols);
            double remaining = H - margin - y;
            double cellW = (usable - (cols - 1) * gutter) / cols;
            double cellH = (remaining - (rows - 1) * gutter) / rows;
            if (cellH < 140) cellH = 140;   // never crush a chart; page can scroll
            for (int i = 0; i < data.Count; i++)
            {
                int r = i / cols, c = i % cols;
                int inRow = Math.Min(cols, data.Count - r * cols);
                // stretch a short final row to fill the width
                double w = (usable - (inRow - 1) * gutter) / inRow;
                double x = margin + c * (w + gutter);
                SetBounds(data[i].Item1, data[i].Item2, x, y + r * (cellH + gutter), w, cellH);
            }
        }

        session.Dirty = true;
        return new { ok = true, page = pageName, arranged = headers.Count + slicers.Count + kpis.Count + data.Count,
            headers = headers.Count, slicers = slicers.Count, kpis = kpis.Count, dataVisuals = data.Count };
    }

    /// <summary>Align or distribute a set of visuals, or make them the same size - precise tidy-up.</summary>
    public object AlignVisuals(string reportSessionId, string pageName, IReadOnlyList<string> visualNames, string mode)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, pageName);
        var byName = EnumVisuals(section).ToDictionary(v => v.name, v => v, StringComparer.OrdinalIgnoreCase);
        var sel = visualNames.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
        if (sel.Count < 2) throw new InvalidOperationException("align needs at least 2 of the named visuals on the page.");

        double X((JsonObject vc, JsonObject co, string name, string type) v) => Num(v.vc["x"]) ?? 0;
        double Y((JsonObject vc, JsonObject co, string name, string type) v) => Num(v.vc["y"]) ?? 0;
        double Wd((JsonObject vc, JsonObject co, string name, string type) v) => Num(v.vc["width"]) ?? 0;
        double Ht((JsonObject vc, JsonObject co, string name, string type) v) => Num(v.vc["height"]) ?? 0;
        void Put((JsonObject vc, JsonObject co, string name, string type) v, double? x, double? y, double? w, double? h)
            => SetBounds(v.vc, v.co, x ?? X(v), y ?? Y(v), w ?? Wd(v), h ?? Ht(v));

        double minX = sel.Min(X), maxR = sel.Max(v => X(v) + Wd(v));
        double minY = sel.Min(Y), maxB = sel.Max(v => Y(v) + Ht(v));
        switch (mode.ToLowerInvariant())
        {
            case "left": foreach (var v in sel) Put(v, minX, null, null, null); break;
            case "right": foreach (var v in sel) Put(v, maxR - Wd(v), null, null, null); break;
            case "top": foreach (var v in sel) Put(v, null, minY, null, null); break;
            case "bottom": foreach (var v in sel) Put(v, null, maxB - Ht(v), null, null); break;
            case "centerx": { double cx = (minX + maxR) / 2; foreach (var v in sel) Put(v, cx - Wd(v) / 2, null, null, null); break; }
            case "centery": { double cy = (minY + maxB) / 2; foreach (var v in sel) Put(v, null, cy - Ht(v) / 2, null, null); break; }
            case "samewidth": { double w = sel.Max(Wd); foreach (var v in sel) Put(v, null, null, w, null); break; }
            case "sameheight": { double h = sel.Max(Ht); foreach (var v in sel) Put(v, null, null, null, h); break; }
            case "distributeh":
                {
                    var ord = sel.OrderBy(X).ToList();
                    double total = maxR - minX, sum = ord.Sum(Wd);
                    double gap = ord.Count > 1 ? (total - sum) / (ord.Count - 1) : 0;
                    double x = minX; foreach (var v in ord) { Put(v, x, null, null, null); x += Wd(v) + gap; }
                    break;
                }
            case "distributev":
                {
                    var ord = sel.OrderBy(Y).ToList();
                    double total = maxB - minY, sum = ord.Sum(Ht);
                    double gap = ord.Count > 1 ? (total - sum) / (ord.Count - 1) : 0;
                    double yy = minY; foreach (var v in ord) { Put(v, null, yy, null, null); yy += Ht(v) + gap; }
                    break;
                }
            default: throw new InvalidOperationException($"unknown align mode '{mode}'. Use left|right|top|bottom|centerx|centery|samewidth|sameheight|distributeh|distributev.");
        }
        session.Dirty = true;
        return new { ok = true, page = pageName, mode, aligned = sel.Count };
    }

    // -------------------------------------------------------------------- themes
    /// <summary>Apply a report theme (built-in preset or custom theme JSON) - controls palette, fonts and structural colours.</summary>
    public object ApplyReportTheme(string reportSessionId, string preset, string? themeJson)
    {
        var session = _sessions.GetReport(reportSessionId);
        JsonObject theme = !string.IsNullOrWhiteSpace(themeJson)
            ? (JsonNode.Parse(themeJson) as JsonObject ?? throw new InvalidOperationException("themeJson is not a JSON object."))
            : BuiltInTheme(preset);
        ApplyThemeObject(session, theme);
        return new { ok = true, theme = (string?)theme["name"] ?? preset,
            dataColors = (theme["dataColors"] as JsonArray)?.Count ?? 0 };
    }

    /// <summary>Write a theme object into the report's themeCollection.customTheme.</summary>
    private void ApplyThemeObject(ReportSession session, JsonObject theme)
    {
        var root = session.Layout.Root;
        var cfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var tc = (cfg["themeCollection"] as JsonObject) ?? new JsonObject();
        tc["customTheme"] = theme;
        tc["customThemeAdded"] = true;
        cfg["themeCollection"] = tc;
        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
    }

    /// <summary>Return the report's current custom theme (summary + full JSON to inspect or re-apply).</summary>
    public object ReadTheme(string reportSessionId)
    {
        var session = _sessions.GetReport(reportSessionId);
        var cfg = JsonNode.Parse((string?)session.Layout.Root["config"] ?? "{}") as JsonObject;
        if (cfg?["themeCollection"]?["customTheme"] is not JsonObject theme)
            return new { ok = true, hasCustomTheme = false, note = "No custom theme applied - the report uses a base/built-in theme. Use generate_theme or apply_report_theme." };
        return new
        {
            ok = true,
            hasCustomTheme = true,
            name = (string?)theme["name"],
            dataColors = (theme["dataColors"] as JsonArray)?.Select(c => (string?)c).ToArray(),
            background = (string?)theme["background"],
            foreground = (string?)theme["foreground"],
            hasVisualStyleDefaults = theme["visualStyles"] != null,
            themeJson = theme.ToJsonString(JsonOpts),
        };
    }

    /// <summary>
    /// Generate a complete, professional Power BI theme: an 8-colour palette (derived from a primary
    /// colour, an explicit list, or a named style), structural colours + fonts, AND visualStyles
    /// defaults so EVERY visual automatically gets the card look (rounded corners, drop shadow,
    /// consistent title font, header hidden) - the theme-driven 'looks pro' layer. Optionally applies it.
    /// </summary>
    public object GenerateTheme(string reportSessionId, string name, string? primaryColor, string? colorsCsv,
        string style, string fontFamily, bool dark, double cornerRadius, bool shadow, bool cardStyle, bool apply,
        string? logoPath = null)
    {
        string[] colors; string source;
        if (!string.IsNullOrWhiteSpace(logoPath) && File.Exists(logoPath) && ExtractPalette(logoPath, 8) is { Length: >= 1 } logo)
        {
            var list = logo.ToList();                                  // brand colours first, padded to 8
            foreach (var c in GeneratePalette(list[0], 8)) if (list.Count < 8 && !list.Contains(c)) list.Add(c);
            colors = list.Take(8).ToArray(); source = "logo";
        }
        else if (!string.IsNullOrWhiteSpace(colorsCsv))
        { colors = colorsCsv.Split(',').Select(s => NormalizeHex(s)).Where(s => s != null).Cast<string>().ToArray(); source = "colors"; }
        else if (!string.IsNullOrWhiteSpace(primaryColor) && NormalizeHex(primaryColor) is string p)
        { colors = GeneratePalette(p, 8); source = "primaryColor"; }
        else { colors = PresetColors(style); source = "style"; }
        if (colors.Length == 0) { colors = PresetColors("executive"); source = "style"; }

        string bg = dark ? "#1B1B2A" : "#FFFFFF";
        string fg = dark ? "#F2F4F8" : "#1A2233";
        string cardBg = dark ? "#262640" : "#FFFFFF";
        string border = dark ? "#3A3A55" : "#E6E9EF";
        string subtle = dark ? "#A9B0C0" : "#5B6B7E";
        string accent = colors[0];
        string face = string.IsNullOrWhiteSpace(fontFamily) ? "Segoe UI" : fontFamily;
        string semibold = face == "Segoe UI" ? "Segoe UI Semibold" : face;

        var theme = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(name) ? "Generated" : name };
        var dc = new JsonArray(); foreach (var c in colors) dc.Add(c);
        theme["dataColors"] = dc;
        theme["background"] = bg; theme["foreground"] = fg; theme["tableAccent"] = accent;
        theme["good"] = "#2E7D32"; theme["neutral"] = "#E8A93B"; theme["bad"] = "#D7263D";
        theme["maximum"] = colors[0]; theme["center"] = accent;
        theme["minimum"] = dark ? "#22324A" : "#EAF1F7"; theme["null"] = dark ? "#33384A" : "#F0F0F0";
        theme["textClasses"] = new JsonObject
        {
            ["title"] = TextClass(semibold, 14, fg),
            ["header"] = TextClass(semibold, 12, fg),
            ["label"] = TextClass(face, 10, subtle),
            ["callout"] = TextClass(semibold, 28, fg),
        };

        // visualStyles "*"/"*" = the default look for every visual
        var def = new JsonObject
        {
            ["title"] = new JsonArray { new JsonObject {
                ["show"] = true, ["fontColor"] = Solid(fg), ["fontFamily"] = semibold,
                ["fontSize"] = 12, ["alignment"] = "left", ["titleWrap"] = true } },
            ["visualHeader"] = new JsonArray { new JsonObject { ["show"] = false } },
        };
        if (cardStyle)
        {
            def["background"] = new JsonArray { new JsonObject { ["show"] = true, ["color"] = Solid(cardBg), ["transparency"] = 0 } };
            def["border"] = new JsonArray { new JsonObject { ["show"] = true, ["color"] = Solid(border), ["radius"] = cornerRadius } };
            if (shadow) def["dropShadow"] = new JsonArray { new JsonObject { ["show"] = true, ["preset"] = "BottomRight" } };
        }
        var visualStyles = new JsonObject { ["*"] = new JsonObject { ["*"] = def } };
        if (cardStyle)
        {
            // textboxes, shapes, images and buttons must NOT get the card treatment - they're
            // banners/titles/panels that bring their own fill. Neutralise the defaults for them.
            JsonObject Flat() => new()
            {
                ["*"] = new JsonObject
                {
                    ["background"] = new JsonArray { new JsonObject { ["show"] = false } },
                    ["border"] = new JsonArray { new JsonObject { ["show"] = false } },
                    ["dropShadow"] = new JsonArray { new JsonObject { ["show"] = false } },
                    ["visualHeader"] = new JsonArray { new JsonObject { ["show"] = false } },
                    ["title"] = new JsonArray { new JsonObject { ["show"] = false } },
                },
            };
            foreach (var vt in new[] { "textbox", "image", "shape", "actionButton", "basicShape" })
                visualStyles[vt] = Flat();
        }
        theme["visualStyles"] = visualStyles;

        string themeJson = theme.ToJsonString(JsonOpts);
        bool applied = false;
        if (apply)
        {
            ApplyThemeObject(_sessions.GetReport(reportSessionId), JsonNode.Parse(themeJson) as JsonObject ?? theme);
            applied = true;
        }
        return new { ok = true, name = (string?)theme["name"], palette = colors, source, dark, cardStyle, applied, themeJson };
    }

    /// <summary>Tweak the report's current custom theme in place (palette / structural colours / card radius+shadow / font).</summary>
    public object ModifyTheme(string reportSessionId, string? primaryColor, string? colorsCsv,
        string? background, string? foreground, double? cornerRadius, bool? shadow, string? fontFamily)
    {
        var session = _sessions.GetReport(reportSessionId);
        var cfg = JsonNode.Parse((string?)session.Layout.Root["config"] ?? "{}") as JsonObject ?? new JsonObject();
        if (cfg["themeCollection"]?["customTheme"] is not JsonObject live)
            throw new InvalidOperationException("No custom theme to modify - run generate_theme or apply_report_theme first.");
        var theme = JsonNode.Parse(live.ToJsonString()) as JsonObject ?? throw new InvalidOperationException("theme parse failed.");

        if (!string.IsNullOrWhiteSpace(colorsCsv))
        {
            var dc = new JsonArray(); foreach (var c in colorsCsv.Split(',')) if (NormalizeHex(c) is string h) dc.Add(h);
            if (dc.Count > 0) theme["dataColors"] = dc;
        }
        else if (!string.IsNullOrWhiteSpace(primaryColor) && NormalizeHex(primaryColor) is string p)
        {
            var dc = new JsonArray(); foreach (var c in GeneratePalette(p, 8)) dc.Add(c);
            theme["dataColors"] = dc; theme["tableAccent"] = p; theme["center"] = p; theme["maximum"] = p;
        }
        if (NormalizeHex(background) is string bgh) theme["background"] = bgh;
        if (NormalizeHex(foreground) is string fgh) theme["foreground"] = fgh;

        // patch the card defaults if present
        if (theme["visualStyles"]?["*"]?["*"] is JsonObject def)
        {
            if (cornerRadius.HasValue && def["border"] is JsonArray ba && ba.Count > 0 && ba[0] is JsonObject b0)
                b0["radius"] = cornerRadius.Value;
            if (shadow.HasValue)
                def["dropShadow"] = new JsonArray { new JsonObject { ["show"] = shadow.Value, ["preset"] = "BottomRight" } };
            if (!string.IsNullOrWhiteSpace(fontFamily) && def["title"] is JsonArray ta && ta.Count > 0 && ta[0] is JsonObject t0)
                t0["fontFamily"] = fontFamily;
        }

        ApplyThemeObject(session, theme);
        return new { ok = true, modified = true, dataColors = (theme["dataColors"] as JsonArray)?.Count ?? 0 };
    }

    private static (string name, string[] colors, string bg, string fg, string accent) PresetSpec(string preset) =>
        preset.ToLowerInvariant() switch
        {
            "vibrant" => ("Vibrant", new[] { "#2D6CDF", "#16C79A", "#FF6B6B", "#FFC93C", "#7B61FF", "#FF8A5B", "#00B8D9", "#E84393" }, "#FFFFFF", "#1A1A2E", "#2D6CDF"),
            "slate"   => ("Slate",   new[] { "#3A86FF", "#8338EC", "#FF006E", "#FB5607", "#FFBE0B", "#06D6A0", "#118AB2", "#737B8C" }, "#F4F6FA", "#1F2937", "#3A86FF"),
            "sunset"  => ("Sunset",  new[] { "#F25F5C", "#FFB400", "#247BA0", "#70C1B3", "#B95F89", "#FF8C42", "#50514F", "#9BC53D" }, "#FFFBF5", "#2B2118", "#F25F5C"),
            "forest"  => ("Forest",  new[] { "#2E7D32", "#1B998B", "#3D5A80", "#E9C46A", "#E76F51", "#8AB17D", "#264653", "#A8763E" }, "#FBFDF8", "#1B2A21", "#2E7D32"),
            _         => ("Executive", new[] { "#16365C", "#2E86AB", "#5BC0BE", "#9BC53D", "#E8A93B", "#D7263D", "#6A5ACD", "#5B6B7E" }, "#FFFFFF", "#16365C", "#2E86AB"),
        };

    private static string[] PresetColors(string preset) => PresetSpec(preset).colors;

    private static JsonObject TextClass(string face, int size, string color) =>
        new() { ["fontFace"] = face, ["fontSize"] = size, ["color"] = color };

    private static JsonObject Solid(string color) => new() { ["solid"] = new JsonObject { ["color"] = color } };

    private static JsonObject BuiltInTheme(string preset)
    {
        var p = PresetSpec(preset);
        var dataColors = new JsonArray(); foreach (var c in p.colors) dataColors.Add(c);
        return new JsonObject
        {
            ["name"] = p.name,
            ["dataColors"] = dataColors,
            ["background"] = p.bg,
            ["foreground"] = p.fg,
            ["tableAccent"] = p.accent,
            ["good"] = "#2E7D32", ["neutral"] = "#E8A93B", ["bad"] = "#D7263D",
            ["maximum"] = p.colors[0], ["center"] = p.accent, ["minimum"] = "#EAF1F7", ["null"] = "#F0F0F0",
            ["textClasses"] = new JsonObject
            {
                ["title"] = TextClass("Segoe UI Semibold", 14, p.fg),
                ["header"] = TextClass("Segoe UI Semibold", 12, p.fg),
                ["label"] = TextClass("Segoe UI", 10, "#5B6B7E"),
                ["callout"] = TextClass("Segoe UI Semibold", 28, p.fg),
            },
        };
    }

    // ---- colour maths for palette generation ----
    private static string? NormalizeHex(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim(); if (!s.StartsWith("#")) s = "#" + s;
        if (s.Length == 4) s = "#" + new string(new[] { s[1], s[1], s[2], s[2], s[3], s[3] });
        if (s.Length != 7) return null;
        for (int i = 1; i < 7; i++) if (!Uri.IsHexDigit(s[i])) return null;
        return s.ToUpperInvariant();
    }

    private static (double r, double g, double b) HexToRgb(string hex) => (
        Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0,
        Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0,
        Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0);

    private static string RgbToHex(double r, double g, double b) =>
        $"#{(int)Math.Round(Math.Clamp(r, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(g, 0, 1) * 255):X2}{(int)Math.Round(Math.Clamp(b, 0, 1) * 255):X2}";

    private static (double h, double s, double l) RgbToHsl(double r, double g, double b)
    {
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double h = 0, s, l = (max + min) / 2;
        double d = max - min;
        if (d == 0) { s = 0; }
        else
        {
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static (double r, double g, double b) HslToRgb(double h, double s, double l)
    {
        if (s == 0) return (l, l, l);
        double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        double p = 2 * l - q;
        double Hue(double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return (Hue(h + 1.0 / 3), Hue(h), Hue(h - 1.0 / 3));
    }

    /// <summary>Derive an n-colour data palette from one primary colour: spread distinct hues around
    /// the wheel at a pleasant, consistent saturation/lightness, keeping the exact primary first.</summary>
    private static string[] GeneratePalette(string primaryHex, int n)
    {
        var (r, g, b) = HexToRgb(primaryHex);
        var (h, s, l) = RgbToHsl(r, g, b);
        s = Math.Clamp(s, 0.45, 0.78);
        l = Math.Clamp(l, 0.42, 0.56);
        double[] offsets = { 0, 200, 45, 150, 280, 25, 110, 320, 75, 240 };
        var outc = new List<string>();
        for (int i = 0; i < n; i++)
        {
            double hh = ((h * 360 + offsets[i % offsets.Length]) % 360) / 360.0;
            double ll = Math.Clamp(l + (i % 2 == 0 ? 0 : -0.07), 0.30, 0.60);
            var (rr, gg, bb) = HslToRgb(hh, s, ll);
            outc.Add(RgbToHex(rr, gg, bb));
        }
        if (outc.Count > 0) outc[0] = primaryHex;
        return outc.ToArray();
    }

    /// <summary>Extract a brand palette from a logo image: the most prominent, distinct, saturated
    /// colours (background/near-white/near-black filtered out), most-used first. Windows GDI+ via System.Drawing.</summary>
    private static string[] ExtractPalette(string imagePath, int n)
    {
        // GDI+ only exists on Windows under net8; off Windows degrade to "no palette" (the
        // DesktopInterop convention) so GenerateTheme falls through to its other colour sources.
        // 6.1 is the floor System.Drawing.Common declares, and what the analyzer wants proven.
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 1)) return Array.Empty<string>();
        using var source = new Bitmap(imagePath);
        // fit inside 160x160 preserving aspect (ResizeMode.Max semantics: upscales small logos too)
        double scale = Math.Min(160.0 / source.Width, 160.0 / source.Height);
        int w = Math.Max(1, (int)Math.Round(source.Width * scale));
        int h = Math.Max(1, (int)Math.Round(source.Height * scale));
        using var image = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var gfx = Graphics.FromImage(image))
        {
            gfx.CompositingMode = CompositingMode.SourceCopy;         // keep source alpha; no blend with the blank canvas
            gfx.InterpolationMode = InterpolationMode.HighQualityBicubic;
            gfx.PixelOffsetMode = PixelOffsetMode.HighQuality;
            using var attrs = new ImageAttributes();
            attrs.SetWrapMode(WrapMode.TileFlipXY);                   // stop the resampler ghosting past the image edge
            gfx.DrawImage(source, new Rectangle(0, 0, w, h), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attrs);
        }
        var buckets = new Dictionary<int, (long count, long r, long g, long b)>();
        var locked = image.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[locked.Stride * h];                 // one bulk copy - never GetPixel per pixel
            Marshal.Copy(locked.Scan0, pixels, 0, pixels.Length);
            for (int y = 0; y < h; y++)
            {
                int row = y * locked.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + x * 4;                              // Format32bppArgb lays out BGRA in memory
                    byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2], a = pixels[i + 3];
                    if (a < 24) continue;                             // skip transparent
                    int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);   // 5-bit/channel bucket
                    if (buckets.TryGetValue(key, out var v)) buckets[key] = (v.count + 1, v.r + r, v.g + g, v.b + b);
                    else buckets[key] = (1, r, g, b);
                }
            }
        }
        finally { image.UnlockBits(locked); }
        (long count, double r, double g, double b) Avg(KeyValuePair<int, (long count, long r, long g, long b)> kv)
            => (kv.Value.count, (double)kv.Value.r / kv.Value.count, (double)kv.Value.g / kv.Value.count, (double)kv.Value.b / kv.Value.count);
        bool Brandy((long count, double r, double g, double b) c)
        {
            var (_, s, l) = RgbToHsl(c.r / 255, c.g / 255, c.b / 255);
            return l > 0.08 && l < 0.95 && s > 0.12;             // drop near-white/black + greys
        }
        var ranked = buckets.Select(Avg).Where(Brandy).OrderByDescending(c => c.count).ToList();
        if (ranked.Count == 0) ranked = buckets.Select(Avg).OrderByDescending(c => c.count).ToList();
        var palette = new List<(double r, double g, double b)>();
        foreach (var c in ranked)
        {
            if (palette.Count >= n) break;
            if (palette.Any(p => Math.Abs(p.r - c.r) + Math.Abs(p.g - c.g) + Math.Abs(p.b - c.b) < 64)) continue;  // distinct only
            palette.Add((c.r, c.g, c.b));
        }
        return palette.Select(p => RgbToHex(p.r / 255, p.g / 255, p.b / 255)).ToArray();
    }

    // ============================================================================================
    //  WAVE M - STRUCTURED / DATA-BOUND VISUAL BUILDERS (the genuinely structured gaps the universal
    //  encoder fix now makes writable). Each writes a pre-shaped PBI JSON object onto the right card and
    //  re-stringifies the visual config. Structures matched to ground truth - FLAG for Desktop validation.
    // ============================================================================================

    /// <summary>Get a visual's live objects bag, creating it if absent.</summary>
    private static JsonObject ObjectsBag(JsonObject sv)
    {
        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        return objects;
    }

    /// <summary>Get the first (default, selector-less) properties bag of an object card, creating the card +
    /// entry if absent. Used by the single-card structured builders.</summary>
    private static JsonObject CardProps(JsonObject bag, string objectName)
    {
        var arr = (bag[objectName] as JsonArray) ?? new JsonArray(); bag[objectName] = arr;
        JsonObject entry;
        if (arr.Count > 0 && arr[0] is JsonObject e0) entry = e0;
        else { entry = new JsonObject { ["properties"] = new JsonObject() }; arr.Add(entry); }
        var props = (entry["properties"] as JsonObject) ?? new JsonObject(); entry["properties"] = props;
        return props;
    }

    /// <summary>1. set_plot_area_image: a background image on a chart's PLOT AREA (the plotArea card image
    /// object). scaling = Fit|Fill|Normal; transparency 0-100 optional. imageUrlOrPath is a local file
    /// (embedded) or an http(s)/data URL.</summary>
    public object SetPlotAreaImage(string reportSessionId, string page, string visual,
        string imageUrlOrPath, string scaling, double? transparency)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        var props = CardProps(ObjectsBag(sv), "plotArea");
        props["image"] = ImageSourceFile(session, imageUrlOrPath, scaling);
        if (transparency.HasValue) props["transparency"] = Lit(transparency.Value.ToString(Inv) + "D");
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], plotAreaImage = true, scaling,
            source = IsImageUrl(imageUrlOrPath) ? "url" : "embedded" };
    }

    /// <summary>2. set_image_source: re-source an EXISTING image visual (rewrites its image card sourceFile).
    /// add_image only creates new image visuals; this re-points one already on the page.</summary>
    public object SetImageSource(string reportSessionId, string page, string visual, string imageUrlOrPath)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        if ((string?)sv["visualType"] != "image")
            throw new InvalidOperationException($"set_image_source targets an existing image visual; '{visual}' is a '{(string?)sv["visualType"]}'. Use add_image to create one.");
        var props = CardProps(ObjectsBag(sv), "image");
        // preserve the existing scaling if present, else default to a sensible Normal.
        string scaling = "Normal";
        if (props["sourceFile"]?["image"]?["scaling"]?["expr"]?["Literal"]?["Value"]?.GetValue<string>() is string sc && sc.Length >= 2)
            scaling = sc.Trim('\'');
        props["sourceFile"] = ImageSourceFile(session, imageUrlOrPath, scaling);
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], reSourced = true,
            source = IsImageUrl(imageUrlOrPath) ? "url" : "embedded" };
    }

    /// <summary>3. set_textbox_content: rich MULTI-run paragraphs on a textbox (add_textbox is single-run).
    /// runs = [{ text, fontFamily?, fontSize?, color?, bold?, italic?, url? }] - each becomes a textRun with
    /// its own textStyle; a url makes the run a hyperlink. bulleted wraps the runs in a bulleted list item.</summary>
    public object SetTextboxContent(string reportSessionId, string page, string visual, JsonArray runs, bool bulleted)
    {
        if (runs.Count == 0) throw new ArgumentException("at least one run { text } is required.");
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);

        var textRuns = new JsonArray();
        foreach (var rNode in runs)
        {
            if (rNode is not JsonObject r) continue;
            string text = (string?)r["text"] ?? "";
            var style = new JsonObject();
            if ((string?)r["fontFamily"] is string ff && ff.Length > 0) style["fontFamily"] = ff;
            if (Num(r["fontSize"]) is double fs) style["fontSize"] = fs.ToString(Inv) + "pt";
            if ((string?)r["color"] is string col && col.Length > 0) style["color"] = col;
            if ((bool?)r["bold"] == true) style["fontWeight"] = "bold";
            if ((bool?)r["italic"] == true) style["fontStyle"] = "italic";
            var run = new JsonObject { ["value"] = text };
            if (style.Count > 0) run["textStyle"] = style;
            if ((string?)r["url"] is string url && url.Length > 0) run["url"] = url;
            textRuns.Add(run);
        }

        var paragraph = new JsonObject { ["textRuns"] = textRuns };
        if (bulleted) paragraph["listType"] = "Bullet";   // bulleted list paragraph
        var props = CardProps(ObjectsBag(sv), "general");
        props["paragraphs"] = new JsonArray { paragraph };

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], runs = textRuns.Count, bulleted };
    }

    /// <summary>A gradient stop literal: a bare-Literal colour + optional explicit value (NOT expr-wrapped),
    /// the shape Power BI uses inside linearGradient2/3.</summary>
    private static JsonObject GradStop(string hex, double? v)
    {
        var s = new JsonObject { ["color"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{hex}'" } } };
        if (v.HasValue) s["value"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = v.Value.ToString(Inv) + "D" } };
        return s;
    }

    /// <summary>The measure-driven FillRule Input node (Measure/SourceRef), the input to a gradient/rule fill.</summary>
    private static JsonObject FillInput(string table, string measure) => new()
    {
        ["Measure"] = new JsonObject {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } }, ["Property"] = measure },
    };

    /// <summary>A measure-driven gradient FillRule colour object (linearGradient2 or linearGradient3),
    /// wrapped solid.color.expr.FillRule - the shape that drives a colour by a measure across a 2/3-stop ramp.</summary>
    private static JsonObject GradientFillColor(string table, string measure, string minColor, string maxColor,
        string? centerColor, double? min, double? center, double? max)
    {
        JsonObject nullStrat() => new() { ["strategy"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "'asZero'" } } };
        JsonObject grad = centerColor == null
            ? new JsonObject { ["linearGradient2"] = new JsonObject { ["min"] = GradStop(minColor, min), ["max"] = GradStop(maxColor, max), ["nullColoringStrategy"] = nullStrat() } }
            : new JsonObject { ["linearGradient3"] = new JsonObject { ["min"] = GradStop(minColor, min), ["mid"] = GradStop(centerColor, center), ["max"] = GradStop(maxColor, max), ["nullColoringStrategy"] = nullStrat() } };
        return new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonObject {
            ["expr"] = new JsonObject { ["FillRule"] = new JsonObject { ["Input"] = FillInput(table, measure), ["FillRule"] = grad } } } } };
    }

    /// <summary>4. set_gradient_color: gradient-stop saturation on a card set_color_scale does NOT target -
    /// treemap/funnel/map dataPoint fill. Writes a measure-driven gradient FillRule onto the chosen card's
    /// fillColor. card defaults to dataPoint; the property written is fillColor.</summary>
    public object SetGradientColor(string reportSessionId, string page, string visual, string card,
        string measureTable, string measure, string minColor, string maxColor,
        string? centerColor, double? min, double? center, double? max)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        string cardName = string.IsNullOrWhiteSpace(card) ? "dataPoint" : card;
        var props = CardProps(ObjectsBag(sv), cardName);
        props["fillColor"] = GradientFillColor(measureTable, measure, minColor, maxColor, centerColor, min, center, max);
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], card = cardName,
            basedOn = $"{measureTable}[{measure}]", gradient = centerColor == null ? "2-colour" : "3-colour" };
    }

    /// <summary>A discrete rule-based FillRule colour object (RuleDefinition of {min,max,color} bands),
    /// wrapped solid.color.expr.FillRule - the same shape SetConditionalFormattingRules writes.</summary>
    private static JsonObject RuleFillColor(string table, string measure, IReadOnlyList<(double min, double max, string color)> rules)
    {
        var input = FillInput(table, measure);
        var ruleArr = new JsonArray();
        foreach (var (mn, mx, color) in rules)
            ruleArr.Add(new JsonObject
            {
                ["Condition"] = BandCondition(input, mn, mx),
                ["Color"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{color}'" } },
            });
        return new JsonObject { ["solid"] = new JsonObject { ["color"] = new JsonObject {
            ["expr"] = new JsonObject { ["FillRule"] = new JsonObject {
                ["Input"] = input,
                ["FillRule"] = new JsonObject { ["ruleDefinition"] = new JsonObject { ["rules"] = ruleArr } } } } } } };
    }

    /// <summary>5. set_map_conditional_formatting: a FillRule on a filledMap/azureMap filled layer - either a
    /// measure-driven gradient (pass gradient colours) or discrete rules (pass rules). Writes onto the
    /// dataPoint card's fillColor (target defaults to fill).</summary>
    public object SetMapConditionalFormatting(string reportSessionId, string page, string visual,
        string measureTable, string measure, IReadOnlyList<(double min, double max, string color)>? rules,
        (string minColor, string maxColor, string? centerColor, double? min, double? center, double? max)? gradient,
        string target)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        string prop = string.IsNullOrWhiteSpace(target) || target.Equals("fill", StringComparison.OrdinalIgnoreCase) ? "fillColor" : target;
        var props = CardProps(ObjectsBag(sv), "dataPoint");
        string mode;
        if (rules is { Count: > 0 })
        {
            props[prop] = RuleFillColor(measureTable, measure, rules);
            mode = "rules";
        }
        else if (gradient is { } g)
        {
            props[prop] = GradientFillColor(measureTable, measure, g.minColor, g.maxColor, g.centerColor, g.min, g.center, g.max);
            mode = g.centerColor == null ? "gradient-2" : "gradient-3";
        }
        else throw new ArgumentException("set_map_conditional_formatting needs either rules or a gradient.");
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], mapConditionalFormat = $"dataPoint/{prop}",
            basedOn = $"{measureTable}[{measure}]", mode };
    }

    /// <summary>6. set_azuremap_layer_source: an azureMap reference-layer (file/URL/geojson) or tile-layer
    /// (URL template). layer = reference|tile. For reference, a local .json/.geojson is embedded; an http(s)
    /// URL or inline geojson string is written verbatim. For tile, source is the URL template.</summary>
    public object SetAzureMapLayerSource(string reportSessionId, string page, string visual, string layer, string source)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        string lyr = (layer ?? "").Trim().ToLowerInvariant();
        var objects = ObjectsBag(sv);
        string detail;
        if (lyr == "reference")
        {
            var props = CardProps(objects, "referenceLayer");
            props["show"] = Lit("true");
            if (IsImageUrl(source))
            {
                props["url"] = Lit($"'{source.Replace("'", "''")}'");
                props["dataLocation"] = Lit("'Url'");
                detail = "url";
            }
            else if (source.TrimStart().StartsWith("{") || source.TrimStart().StartsWith("["))
            {
                // inline geojson - stored as the layer's data literal
                props["data"] = Lit($"'{source.Replace("'", "''")}'");
                props["dataLocation"] = Lit("'Inline'");
                detail = "inline-geojson";
            }
            else
            {
                string resName = EmbedImageResource(session, source);   // any file part (geojson/json) goes via the resource package
                props["url"] = new JsonObject { ["expr"] = new JsonObject { ["ResourcePackageItem"] = new JsonObject {
                    ["PackageName"] = "RegisteredResources", ["PackageType"] = 1, ["ItemName"] = resName } } };
                props["dataLocation"] = Lit("'ResourcePackage'");
                detail = "embedded";
            }
        }
        else if (lyr == "tile")
        {
            var props = CardProps(objects, "tileLayer");
            props["show"] = Lit("true");
            props["tileUrl"] = Lit($"'{source.Replace("'", "''")}'");
            detail = "tile-url-template";
        }
        else throw new ArgumentException($"unknown layer '{layer}' (use reference|tile).");

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], layer = lyr, source = detail };
    }

    /// <summary>7. set_shapemap_custom_map: a custom topojson/geojson map on a shapeMap (the custom-map
    /// upload). A local file is embedded as a resource; inline json or an http(s) URL is written verbatim.</summary>
    public object SetShapeMapCustomMap(string reportSessionId, string page, string visual, string topojsonOrGeojson)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        var props = CardProps(ObjectsBag(sv), "shape");
        string detail;
        if (IsImageUrl(topojsonOrGeojson))
        {
            props["map"] = Lit($"'{topojsonOrGeojson.Replace("'", "''")}'");
            props["mapType"] = Lit("'Url'");
            detail = "url";
        }
        else if (topojsonOrGeojson.TrimStart().StartsWith("{"))
        {
            props["map"] = Lit($"'{topojsonOrGeojson.Replace("'", "''")}'");
            props["mapType"] = Lit("'Custom'");
            detail = "inline";
        }
        else
        {
            string resName = EmbedImageResource(session, topojsonOrGeojson);
            props["map"] = new JsonObject { ["expr"] = new JsonObject { ["ResourcePackageItem"] = new JsonObject {
                ["PackageName"] = "RegisteredResources", ["PackageType"] = 1, ["ItemName"] = resName } } };
            props["mapType"] = Lit("'Custom'");
            detail = "embedded";
        }
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], customMap = detail };
    }

    /// <summary>8. add_error_bars: an errorBars object on a chart. kind = byField (upper/lower measures) or
    /// byPercentage (a percent of the value). relation = Absolute|Relative; symmetrical optional; band =
    /// Fill|Line|Both.</summary>
    public object AddErrorBars(string reportSessionId, string page, string visual, string kind,
        string? measureTable, string? upperField, string? lowerField, double? percent,
        string relation, bool? symmetrical, string? band)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        string k = (kind ?? "").Trim().ToLowerInvariant();
        var props = CardProps(ObjectsBag(sv), "errorBars");
        props["show"] = Lit("true");
        props["relationship"] = Lit($"'{(string.IsNullOrWhiteSpace(relation) ? "Absolute" : relation)}'");
        if (symmetrical.HasValue) props["symmetry"] = Lit(symmetrical.Value ? "true" : "false");
        if (!string.IsNullOrWhiteSpace(band)) props["displayType"] = Lit($"'{band}'");

        if (k == "byfield")
        {
            if (string.IsNullOrWhiteSpace(measureTable) || string.IsNullOrWhiteSpace(upperField))
                throw new ArgumentException("byField error bars need measureTable + upperField (and lowerField unless symmetrical).");
            props["type"] = Lit("'ByField'");
            props["upperBound"] = MeasureExpr(measureTable!, upperField!);
            if (!string.IsNullOrWhiteSpace(lowerField)) props["lowerBound"] = MeasureExpr(measureTable!, lowerField!);
        }
        else if (k == "bypercentage")
        {
            if (percent is null) throw new ArgumentException("byPercentage error bars need percent.");
            props["type"] = Lit("'ByPercentage'");
            props["percentageValue"] = Lit(percent.Value.ToString(Inv) + "D");
        }
        else throw new ArgumentException($"unknown error-bar kind '{kind}' (use byField|byPercentage).");

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], errorBars = k, relation };
    }

    /// <summary>9. add_anomaly_detection: a line-chart anomalies object. sensitivity 0-100 optional;
    /// explainBy = the fields the anomaly explanation groups by.</summary>
    public object AddAnomalyDetection(string reportSessionId, string page, string visual,
        double? sensitivity, IReadOnlyList<string>? explainBy)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        var props = CardProps(ObjectsBag(sv), "anomalyDetection");
        props["show"] = Lit("true");
        if (sensitivity.HasValue) props["sensitivity"] = Lit(sensitivity.Value.ToString(Inv) + "D");
        if (explainBy is { Count: > 0 })
        {
            var arr = new JsonArray();
            foreach (var f in explainBy) arr.Add(f);
            props["explainBy"] = arr;
        }
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], anomalyDetection = true,
            explainBy = explainBy?.ToArray() ?? Array.Empty<string>() };
    }

    /// <summary>10. set_forecast: the FULL forecast object on a line chart (add_analytics_line forecast only
    /// sets the band). length + units (Day|Month|Year/Point), ignoreLast points, confidenceInterval (e.g.
    /// 0.95), seasonality optional.</summary>
    public object SetForecast(string reportSessionId, string page, string visual,
        double length, string? units, double? ignoreLast, double? confidenceInterval, double? seasonality)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        var props = CardProps(ObjectsBag(sv), "forecast");
        props["show"] = Lit("true");
        props["forecastLength"] = Lit(length.ToString(Inv) + "D");
        if (!string.IsNullOrWhiteSpace(units)) props["forecastUnits"] = Lit($"'{units}'");
        if (ignoreLast.HasValue) props["ignoreLast"] = Lit(ignoreLast.Value.ToString(Inv) + "D");
        if (confidenceInterval.HasValue) props["confidenceBand"] = Lit(confidenceInterval.Value.ToString(Inv) + "D");
        if (seasonality.HasValue) props["seasonality"] = Lit(seasonality.Value.ToString(Inv) + "D");
        props["confidenceBandStyle"] = Lit("'fillAndLine'");
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], forecast = true, length, units };
    }

    /// <summary>11. set_play_axis: bind a field to a scatter chart's Play Axis projection role (a data
    /// binding, not formatting). field = "Table.Column"; writes a Play projection with a queryRef.</summary>
    public object SetPlayAxis(string reportSessionId, string page, string visual, string field)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        if (string.IsNullOrWhiteSpace(field) || !field.Contains('.'))
            throw new ArgumentException("field must be \"Table.Column\".");
        int dot = field.IndexOf('.');
        string table = field[..dot], column = field[(dot + 1)..];

        var proj = (sv["projections"] as JsonObject) ?? new JsonObject(); sv["projections"] = proj;
        proj["Play"] = new JsonArray { new JsonObject { ["queryRef"] = $"{table}.{column}" } };

        // ensure the matching Select node exists in prototypeQuery so the role binds (best-known shape).
        var pq = (sv["prototypeQuery"] as JsonObject);
        if (pq?["Select"] is JsonArray sel && !sel.OfType<JsonObject>().Any(s => (string?)s["Name"] == $"{table}.{column}"))
        {
            sel.Add(new JsonObject
            {
                ["Column"] = new JsonObject {
                    ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } }, ["Property"] = column },
                ["Name"] = $"{table}.{column}",
            });
        }

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], playAxis = $"{table}.{column}" };
    }

    /// <summary>12. set_card_image: a hero/callout image on a cardVisual (the new card's image element).
    /// imageUrlOrPath local file (embedded) or URL; fit = Fit|Fill|Normal (defaults Fit).</summary>
    public object SetCardImage(string reportSessionId, string page, string visual, string imageUrlOrPath, string? fit)
    {
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        var props = CardProps(ObjectsBag(sv), "image");
        props["show"] = Lit("true");
        props["sourceFile"] = ImageSourceFile(session, imageUrlOrPath, string.IsNullOrWhiteSpace(fit) ? "Fit" : fit!);
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], cardImage = true,
            source = IsImageUrl(imageUrlOrPath) ? "url" : "embedded" };
    }

    /// <summary>13. set_slicer_conditional_formatting: measure-driven CF on a slicer / button-slicer. The
    /// FillRule builders are hardwired to table/matrix column selectors; this generalises them to a slicer's
    /// own cards. target = fill|font|callout -> the card + property. rules = discrete {min,max,color} bands.</summary>
    public object SetSlicerConditionalFormatting(string reportSessionId, string page, string visual,
        string target, string measureTable, string measure, IReadOnlyList<(double min, double max, string color)> rules)
    {
        if (rules.Count == 0) throw new ArgumentException("at least one rule { min, max, color } is required.");
        var session = _sessions.GetReport(reportSessionId);
        var (vc, co, sv) = ResolveVisual(FindSection(session.Layout.Root, page), visual);
        // map the friendly target to the slicer card + property that carries the colour.
        var (cardName, prop) = (target ?? "fill").Trim().ToLowerInvariant() switch
        {
            "fill" => ("items", "fill"),
            "font" => ("items", "fontColor"),
            "callout" => ("data", "fontColor"),   // button-slicer value (callout) text colour
            _ => throw new ArgumentException($"unknown target '{target}' (use fill|font|callout)."),
        };
        var props = CardProps(ObjectsBag(sv), cardName);
        props[prop] = RuleFillColor(measureTable, measure, rules);
        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], slicerConditionalFormat = $"{cardName}/{prop}",
            basedOn = $"{measureTable}[{measure}]", rules = rules.Count };
    }

    private static JsonObject LiteralFor(string value, string kind) => kind?.ToLowerInvariant() switch
    {
        "number" => Lit(value + "D"),
        "bool" => Lit(value.ToLowerInvariant()),
        "color" => Lit($"'{value}'", wrapSolid: true),
        "raw" => Lit(value),
        _ => Lit($"'{value.Replace("'", "''")}'"),
    };

    private (JsonObject vc, JsonObject co, JsonObject sv) FindVisual(JsonObject section, string visualName)
    {
        foreach (var node in (section["visualContainers"] as JsonArray)!)
        {
            if (node is JsonObject vc && (string?)vc["config"] is string cfg &&
                JsonNode.Parse(cfg) is JsonObject co && (string?)co["name"] == visualName &&
                co["singleVisual"] is JsonObject sv)
                return (vc, co, sv);
        }
        throw new InvalidOperationException($"Visual '{visualName}' not found on this page.");
    }

    // ============================================================================================
    //  REPORT-POLISH TOOLS - the final authoring surface: report-level settings, page display options,
    //  selector-scoped formatting (per-series / per-category / totals / conditional), bookmark options,
    //  visual display mode (spotlight / focus / maximize) and report-level measures.
    // ============================================================================================

    // ---- report settings (ExplorationSettings) -------------------------------------------------

    // The toggles that take an enum/string value (single-quoted in the literal) rather than a bool.
    private static readonly HashSet<string> EnumReportSettings = new(StringComparer.Ordinal)
        { "exportDataMode", "pagesPosition", "queryLimitOption" };

    /// <summary>Write report-level ExplorationSettings into layout.config.settings. Accepts a JSON object of
    /// toggle -> value: boolean toggles (useStylableVisualContainerHeader, hideVisualContainerHeader,
    /// defaultFilterActionIsDataFilter, defaultDrillFilterOtherVisuals, useCrossReportDrillthrough,
    /// allowChangeFilterTypes, allowInlineExploration, useEnhancedTooltips, useScaledTooltips, ...) and the enum
    /// toggles exportDataMode (AllowSummarized|AllowSummarizedAndUnderlying|None) and pagesPosition
    /// (PagesPane|Bottom). MERGES into any existing settings (does not clobber untouched toggles). Each value is
    /// encoded as a wrapped expr-literal (bools as true/false, enums single-quoted) like every other layout
    /// literal.</summary>
    public object SetReportSettings(string reportSessionId, string settingsJson)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var spec = JsonNode.Parse(settingsJson) as JsonObject
                   ?? throw new InvalidOperationException("settings must be a JSON object of toggle -> value.");

        var cfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var settings = (cfg["settings"] as JsonObject) ?? new JsonObject();
        cfg["settings"] = settings;

        var applied = new List<string>();
        foreach (var (key, valNode) in spec)
        {
            if (valNode is null) continue;
            FmtKind kind;
            string raw;
            if (EnumReportSettings.Contains(key))
            {
                kind = FmtKind.Enum;
                raw = valNode.ToString();
            }
            else if (valNode is JsonValue jv && jv.TryGetValue<bool>(out var b))
            {
                kind = FmtKind.Bool;
                raw = b ? "true" : "false";
            }
            else
            {
                // a string "true"/"false" is still a bool toggle; otherwise treat as an enum string id.
                string s = valNode.ToString();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(s, "false", StringComparison.OrdinalIgnoreCase))
                { kind = FmtKind.Bool; raw = s.ToLowerInvariant(); }
                else { kind = FmtKind.Enum; raw = s; }
            }
            settings[key] = Encode(raw, kind);
            applied.Add(key);
        }
        if (applied.Count == 0) throw new ArgumentException("no settings supplied.");

        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, scope = "report", settingsChanged = applied.ToArray(), count = applied.Count };
    }

    // ---- page display option + visibility ------------------------------------------------------

    // Legacy Report/Layout encodes a section's displayOption as an integer:
    //   0 = FitToPage, 1 = FitToWidth, 2 = ActualSize.
    // (Confirmed against the existing engine: AddPage defaults to FitToPage and a Tooltip page is set to
    //  ActualSize=2. FLAG: the FitToPage/FitToWidth pairing is the documented legacy enum.)
    private static int DisplayOptionCode(string opt) => opt.ToLowerInvariant() switch
    {
        "fittopage" => 0,
        "fittowidth" => 1,
        "actualsize" => 2,
        _ => throw new ArgumentException($"unknown displayOption '{opt}' (use FitToPage|FitToWidth|ActualSize)."),
    };

    /// <summary>Set a page's DISPLAY OPTION (the section.displayOption int: FitToPage|FitToWidth|ActualSize) and/or
    /// its VISIBILITY (section.config.objects.section -> visibility 0 = AlwaysVisible, 1 = HiddenInViewMode - the
    /// "hide this page" toggle). Both are optional; set at least one. Visibility merges into the page config.</summary>
    public object SetPageDisplay(string reportSessionId, string page, string? displayOption, string? visibility)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);

        string? appliedDisplay = null;
        if (!string.IsNullOrWhiteSpace(displayOption))
        {
            section["displayOption"] = DisplayOptionCode(displayOption!);
            appliedDisplay = displayOption;
        }

        string? appliedVisibility = null;
        if (!string.IsNullOrWhiteSpace(visibility))
        {
            int vis = visibility!.ToLowerInvariant() switch
            {
                "alwaysvisible" => 0,
                "hiddeninviewmode" => 1,
                _ => throw new ArgumentException($"unknown visibility '{visibility}' (use AlwaysVisible|HiddenInViewMode)."),
            };
            var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
            var objects = (cfg["objects"] as JsonObject) ?? new JsonObject(); cfg["objects"] = objects;
            // the page "section" formatting card carries the visibility property (expr-literal int).
            MergeProperty(objects, "section", "visibility", JsonValue.Create((double)vis));
            section["config"] = cfg.ToJsonString(JsonOpts);
            appliedVisibility = visibility;
        }

        if (appliedDisplay is null && appliedVisibility is null)
            throw new ArgumentException("set at least one of displayOption / visibility.");

        session.Dirty = true;
        return new { ok = true, page, displayOption = appliedDisplay, visibility = appliedVisibility };
    }

    // ---- selector-scoped visual formatting (per-series / per-category / totals / conditional) ---

    /// <summary>Build a formatting SELECTOR from a plain JSON spec. Forms:
    ///   { "data": [ { "scopeId": &lt;literal&gt; }, { "roles": ["Series"] }, { "dataViewWildcard": {"matchingOption":0} } ] }
    ///   { "metadata": "Table.Field" }
    ///   { "total": true }   (or "total":"subtotal" - encoded as a metadata token)
    /// scopeId values are taken as already-shaped literal nodes when an object, else single-quoted text.</summary>
    private static JsonObject BuildSelector(JsonObject spec)
    {
        var sel = new JsonObject();
        if (spec["data"] is JsonArray dataArr)
        {
            var outArr = new JsonArray();
            foreach (var item in dataArr)
            {
                if (item is not JsonObject io) continue;
                var entry = new JsonObject();
                if (io["scopeId"] is JsonNode sc)
                {
                    // a scopeId is a DataRepetitionSelector - pass through an object shape as-is, else wrap a literal.
                    entry["scopeId"] = sc is JsonObject sco ? sco.DeepClone()
                        : new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{sc}'" } } };
                }
                if (io["roles"] is JsonArray roles) entry["roles"] = roles.DeepClone();
                if (io["dataViewWildcard"] is JsonObject dvw) entry["dataViewWildcard"] = dvw.DeepClone();
                else if (io["dataViewWildcard"] is JsonValue) entry["dataViewWildcard"] = new JsonObject { ["matchingOption"] = 0 };
                outArr.Add(entry);
            }
            sel["data"] = outArr;
        }
        if (spec["metadata"] is JsonValue md) sel["metadata"] = md.ToString();
        if (spec["total"] is JsonNode tot)
        {
            // a total/subtotal selector targets the grand-total / subtotal scope.
            string token = tot is JsonValue tv && tv.TryGetValue<bool>(out var tb)
                ? (tb ? "Total" : "")
                : tot.ToString();
            if (!string.IsNullOrEmpty(token))
                sel["data"] = new JsonArray { new JsonObject { ["scopeId"] = new JsonObject { ["Subtotal"] = new JsonObject() }, ["kind"] = token } };
        }
        if (sel.Count == 0) throw new ArgumentException("selector must contain data / metadata / total.");
        return sel;
    }

    /// <summary>Set a SELECTOR-SCOPED formatting entry on a visual so a formatting card can target a specific data
    /// scope: a single series, a category, the grand total / subtotal, or a conditional/wildcard scope. An object
    /// card is an ARRAY of { properties, selector }; this finds the entry whose selector matches (or appends a new
    /// one) and merges the properties into it - so per-series colours and per-category formatting do NOT clobber the
    /// default (selector-less) card. bucket = objects | vcObjects. properties = { prop: value } (encoded by kind,
    /// exactly like set_visual_format). selectorJson = the selector spec (data / metadata / total).</summary>
    public object SetVisualFormatSelector(string reportSessionId, string page, string visual,
        string bucket, string objectName, string propertiesJson, string selectorJson)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        string bk = bucket?.ToLowerInvariant() == "vcobjects" ? "vcObjects" : "objects";
        var props = JsonNode.Parse(propertiesJson) as JsonObject
                    ?? throw new InvalidOperationException("properties must be a JSON object of prop -> value.");
        var selectorSpec = JsonNode.Parse(selectorJson) as JsonObject
                    ?? throw new InvalidOperationException("selector must be a JSON object (data / metadata / total).");
        var selector = BuildSelector(selectorSpec);

        var bag = (sv[bk] as JsonObject) ?? new JsonObject(); sv[bk] = bag;
        var arr = (bag[objectName] as JsonArray) ?? new JsonArray(); bag[objectName] = arr;

        // find an existing entry whose selector is structurally identical (so repeated calls update in place).
        string selKey = selector.ToJsonString(JsonOpts);
        JsonObject? entry = null;
        foreach (var n in arr)
            if (n is JsonObject e && e["selector"] is JsonObject es && es.ToJsonString(JsonOpts) == selKey) { entry = e; break; }
        if (entry == null)
        {
            entry = new JsonObject { ["properties"] = new JsonObject(), ["selector"] = selector };
            arr.Add(entry);
        }
        else entry["selector"] = selector;

        var entryProps = (entry["properties"] as JsonObject) ?? new JsonObject(); entry["properties"] = entryProps;
        var changed = new List<string>();
        foreach (var (propName, val) in props)
        {
            entryProps[propName] = EncodeValue(propName, val);
            changed.Add(propName);
        }

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, visual = (string?)co["name"], bucket = bk, @object = objectName,
            selector = (string)(selectorSpec.ToJsonString(JsonOpts)), propertiesChanged = changed.ToArray() };
    }

    // ---- bookmark options ----------------------------------------------------------------------

    /// <summary>Set a bookmark's OPTIONS - the Data / Display / Current-page / Selected-visuals toggles. Maps to the
    /// bookmark.options object: suppressData (Data off), suppressDisplay (Display off), suppressActiveSection
    /// (Current page off). Passing targetVisuals scopes the bookmark to those visuals
    /// (applyOnlyToTargetVisuals = true + targetVisualNames); passing an empty list clears the scope. Only the
    /// supplied toggles are changed; the others are left as-is.</summary>
    public object SetBookmarkOptions(string reportSessionId, string bookmark,
        bool? suppressData, bool? suppressDisplay, bool? suppressActiveSection, IReadOnlyList<string>? targetVisuals)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        var (cfg, bookmarks) = ReportBookmarks(root);
        var bm = FindBookmark(bookmarks, bookmark);

        var options = (bm["options"] as JsonObject) ?? new JsonObject();
        bm["options"] = options;

        if (suppressData.HasValue) options["suppressData"] = suppressData.Value;
        if (suppressDisplay.HasValue) options["suppressDisplay"] = suppressDisplay.Value;
        if (suppressActiveSection.HasValue) options["suppressActiveSection"] = suppressActiveSection.Value;
        if (targetVisuals != null)
        {
            if (targetVisuals.Count == 0)
            {
                options.Remove("targetVisualNames");
                options["applyOnlyToTargetVisuals"] = false;
            }
            else
            {
                var tvn = new JsonArray();
                foreach (var v in targetVisuals) tvn.Add(v);
                options["targetVisualNames"] = tvn;
                options["applyOnlyToTargetVisuals"] = true;
            }
        }

        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new
        {
            ok = true,
            bookmark = (string?)bm["name"],
            suppressData = (bool?)(options["suppressData"]),
            suppressDisplay = (bool?)(options["suppressDisplay"]),
            suppressActiveSection = (bool?)(options["suppressActiveSection"]),
            targetVisuals = (options["targetVisualNames"] as JsonArray)?.Select(n => (string?)n).ToArray(),
        };
    }

    // ---- visual display mode (spotlight / focus / maximize / hidden) ---------------------------

    /// <summary>Set a visual's DISPLAY STATE on the base layout: the singleVisual.display.mode token. modes:
    ///   normal   -> removes the display override (back to the default state)
    ///   hidden   -> mode "hidden" (the existing hide primitive)
    ///   spotlight, maximize, focus -> the VisualContainerDisplayState modes.
    /// FLAG (Desktop validation): legacy Report/Layout stores the live spotlight/focus state primarily inside a
    /// BOOKMARK's explorationState (VisualContainerDisplayState.mode), not always durably on the base visual config.
    /// We write display.mode on the visual's singleVisual (the same slot the hide/show primitive uses) as the
    /// best-known legacy location; spotlight/focus may need a bookmark to persist in Desktop.</summary>
    public object SetVisualDisplayMode(string reportSessionId, string page, string visual, string mode)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        string m = (mode ?? "").ToLowerInvariant();
        switch (m)
        {
            case "normal":
            case "default":
            case "show":
                sv.Remove("display");
                break;
            case "hidden":
            case "spotlight":
            case "maximize":
            case "focus":
                sv["display"] = new JsonObject { ["mode"] = m == "default" ? "show" : m };
                break;
            default:
                throw new ArgumentException($"unknown mode '{mode}' (use normal|spotlight|maximize|focus|hidden).");
        }

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], mode = m == "default" ? "normal" : m };
    }

    // ---- report-level measures -----------------------------------------------------------------

    /// <summary>Add a REPORT-LEVEL measure (a measure that lives in the report definition, not the model). In the
    /// modern PBIR format these live in reportExtensions.json under entities[].measures[]. In the legacy
    /// Report/Layout we write the SAME conceptual shape into layout.config.modelExtensions: an array of
    /// { name, entities:[ { name=&lt;table&gt;, extends=&lt;table&gt;, measures:[ { name, expression, ... } ] } ] }, merging
    /// into the existing modelExtensions for that table so multiple report measures accumulate.
    /// FLAG (Desktop validation): the legacy modelExtensions shape is the best-known form and should be confirmed in
    /// Power BI Desktop; the canonical home for report-level measures is reportExtensions.json (PBIR).</summary>
    public object AddReportMeasure(string reportSessionId, string table, string name,
        string daxExpression, string? formatString, string? displayFolder)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        if (string.IsNullOrWhiteSpace(table)) throw new ArgumentException("table (the host entity) is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("measure name is required.");
        if (string.IsNullOrWhiteSpace(daxExpression)) throw new ArgumentException("daxExpression is required.");

        var cfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var extensions = (cfg["modelExtensions"] as JsonArray) ?? new JsonArray();
        cfg["modelExtensions"] = extensions;

        // a single report-extension container holds all report measures (named "extension").
        JsonObject? ext = extensions.OfType<JsonObject>().FirstOrDefault(e => (string?)e["name"] == "extension");
        if (ext == null)
        {
            ext = new JsonObject { ["name"] = "extension", ["entities"] = new JsonArray() };
            extensions.Add(ext);
        }
        var entities = (ext["entities"] as JsonArray) ?? new JsonArray(); ext["entities"] = entities;

        // find (or create) the entity that extends the host table.
        JsonObject? entity = entities.OfType<JsonObject>().FirstOrDefault(en => (string?)en["name"] == table);
        if (entity == null)
        {
            entity = new JsonObject { ["name"] = table, ["extends"] = table, ["measures"] = new JsonArray() };
            entities.Add(entity);
        }
        var measures = (entity["measures"] as JsonArray) ?? new JsonArray(); entity["measures"] = measures;

        // replace any existing measure of the same name (so re-adding updates rather than duplicates).
        for (int i = measures.Count - 1; i >= 0; i--)
            if (measures[i] is JsonObject m && (string?)m["name"] == name) measures.RemoveAt(i);

        var measure = new JsonObject { ["name"] = name, ["expression"] = daxExpression };
        if (!string.IsNullOrWhiteSpace(formatString)) measure["formatString"] = formatString;
        if (!string.IsNullOrWhiteSpace(displayFolder)) measure["displayFolder"] = displayFolder;
        measures.Add(measure);

        root["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, table, measure = name, formatString, displayFolder,
            note = "Report-level measure written to layout.config.modelExtensions (best-known legacy shape; confirm in Desktop)." };
    }

    // ============================================================================================
    //  WAVE H - the remaining report-layer features: visual calculations, native sparklines,
    //  drill-through field wiring, drill-down behaviour, navigator visuals, granular theme authoring,
    //  accessibility (alt text + tab order), personalisation, custom-visual registration and the page
    //  wallpaper. All edit the legacy Report/Layout JSON, reusing the same container/config/Encode helpers.
    // ============================================================================================

    // ---- 1. visual calculations -----------------------------------------------------------------

    /// <summary>Author a VISUAL CALCULATION on a visual: an in-visual DAX expression evaluated over the
    /// visual's own result matrix (RUNNINGSUM / MOVINGAVERAGE / PERCENTOFTOTAL / COLLAPSE / EXPAND ...),
    /// distinct from a model measure. Writes a named entry into singleVisual.visualCalculations[] AND
    /// projects it as a Values-role field on the prototypeQuery (a NativeVisualCalculation reference) so it
    /// renders as a column on the visual.
    /// FLAG (Desktop validation): the legacy Report/Layout slot for visual calculations is
    /// singleVisual.visualCalculations[] (each { name, expression, ... }); we also add the projection so the
    /// calc shows. The canonical PBIR home is the same conceptual visualCalculations array. The exact legacy
    /// schema (vs. the GA visualCalculations) should be confirmed in Power BI Desktop.</summary>
    public object AddVisualCalculation(string reportSessionId, string page, string visual, string name, string daxExpression)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a visual calculation name is required.");
        if (string.IsNullOrWhiteSpace(daxExpression)) throw new ArgumentException("daxExpression is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        // FLAG: the visual calculation layer is singleVisual.visualCalculations[] - an array of named DAX calcs
        // over the visual's matrix. Re-adding the same name updates in place rather than duplicating.
        var calcs = (sv["visualCalculations"] as JsonArray) ?? new JsonArray();
        sv["visualCalculations"] = calcs;
        for (int i = calcs.Count - 1; i >= 0; i--)
            if (calcs[i] is JsonObject ex && (string?)ex["name"] == name) calcs.RemoveAt(i);
        calcs.Add(new JsonObject
        {
            ["name"] = name,
            ["expression"] = daxExpression,
            ["hidden"] = false,
        });

        // project the calc as a Values-role field so the visual actually renders the new column.
        if (sv["projections"] is not JsonObject proj) { proj = new JsonObject(); sv["projections"] = proj; }
        var values = (proj["Values"] as JsonArray) ?? new JsonArray();
        proj["Values"] = values;
        bool already = values.OfType<JsonObject>().Any(p => (string?)p["queryRef"] == name);
        if (!already)
            values.Add(new JsonObject { ["queryRef"] = name, ["nativeVisualCalculation"] = true });

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], visualCalculation = name, expression = daxExpression,
            slot = "singleVisual.visualCalculations[]",
            note = "Visual calculation written to singleVisual.visualCalculations[] + projected as a Values field (best-known legacy slot; confirm in Desktop)." };
    }

    // ---- 2. native sparklines -------------------------------------------------------------------

    /// <summary>Add a NATIVE SPARKLINE column to a table/matrix: a tiny line chart per row driven by a value
    /// measure across a category (e.g. trend over month). Writes the sparkline object card
    /// (objects.sparkline) carrying the line measure binding + the category axis, the modern in-cell
    /// sparkline the product renders inside tableEx/pivotTable. Re-adding replaces the sparkline card.
    /// FLAG (Desktop validation): native sparklines are a recent feature; the legacy Report/Layout slot is
    /// singleVisual.objects.sparkline (properties: show + the lineColor + the measure/category query bindings).
    /// The exact binding sub-shape should be confirmed in Desktop.</summary>
    public object AddSparkline(string reportSessionId, string page, string visual,
        string valueMeasureTable, string valueMeasure, string categoryTable, string categoryField, string? lineColor)
    {
        if (string.IsNullOrWhiteSpace(valueMeasure)) throw new ArgumentException("valueMeasure is required.");
        if (string.IsNullOrWhiteSpace(categoryField)) throw new ArgumentException("categoryField is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        string valueRef = $"{valueMeasureTable}.{valueMeasure}";
        string catRef = $"{categoryTable}.{categoryField}";
        string color = string.IsNullOrWhiteSpace(lineColor) ? "#118DFF" : lineColor!;

        // the sparkline object card: show + line colour + the value/category bindings (queryRefs).
        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        objects["sparkline"] = new JsonArray
        {
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["show"] = Lit("true"),
                    ["lineColor"] = Lit($"'{color}'", wrapSolid: true),
                    ["valueAxisMin"] = Lit("'Auto'"),
                    ["valueAxisMax"] = Lit("'Auto'"),
                    // FLAG: the measure/category bindings are referenced by queryRef (the projection that drives
                    // the sparkline line + axis). Confirm the exact binding property names in Desktop.
                    ["measure"] = Lit($"'{valueRef.Replace("'", "''")}'"),
                    ["categoryAxis"] = Lit($"'{catRef.Replace("'", "''")}'"),
                },
            },
        };

        // project the sparkline value measure + its category so the query carries the trend data.
        if (sv["projections"] is not JsonObject proj) { proj = new JsonObject(); sv["projections"] = proj; }
        var values = (proj["Values"] as JsonArray) ?? new JsonArray(); proj["Values"] = values;
        if (!values.OfType<JsonObject>().Any(p => (string?)p["queryRef"] == valueRef))
            values.Add(new JsonObject { ["queryRef"] = valueRef });

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], sparkline = valueRef, category = catRef, lineColor = color,
            slot = "singleVisual.objects.sparkline",
            note = "Sparkline written to objects.sparkline with line measure + category bindings (best-known legacy slot; confirm in Desktop)." };
    }

    // ---- 3. drill-through field wiring ----------------------------------------------------------

    /// <summary>Wire the CARRIED FIELDS of a DRILL-THROUGH page: each field becomes a drill-through page
    /// filter (the section.filters entry with howCreated 5) so right-clicking that field on any other page
    /// drills through to this page filtered to the value. Also sets the keep-all-filters toggle
    /// (section.config.objects.pageBinding/dropFilters) - "keep all filters" when keepAllFilters=true.
    /// fields = list of { table, field }. Replaces any existing drill-through filters (howCreated 5) so the
    /// carried-field set is exactly the supplied list.
    /// FLAG (Desktop validation): legacy Report/Layout carries drill-through fields as section.filters with
    /// howCreated=5 (matching the existing add_drillthrough). The keep-all-filters toggle is written to the
    /// page config (objects.pageInformation / dropFilters); the PBIR home is page.json pageBinding.parameters
    /// + acceptsFilterContext. Confirm the keep-all-filters slot in Desktop.</summary>
    public object SetDrillthroughFields(string reportSessionId, string page,
        IReadOnlyList<(string table, string field)> fields, bool? keepAllFilters)
    {
        if (fields.Count == 0) throw new ArgumentException("at least one drill-through field { table, field } is required.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);

        var filters = JsonNode.Parse((string?)section["filters"] ?? "[]") as JsonArray ?? new JsonArray();
        // drop the existing drill-through filters (howCreated 5) so we rewrite the carried-field set cleanly.
        for (int i = filters.Count - 1; i >= 0; i--)
            if (filters[i] is JsonObject f && (int?)Num(f["howCreated"]) == 5) filters.RemoveAt(i);

        foreach (var (table, field) in fields)
            filters.Add(new JsonObject
            {
                ["name"] = Guid.NewGuid().ToString("N")[..20],
                ["expression"] = new JsonObject
                {
                    ["Column"] = new JsonObject
                    {
                        ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = table } },
                        ["Property"] = field,
                    },
                },
                ["type"] = "Categorical",
                ["howCreated"] = 5,   // 5 = drill-through carried field
            });
        section["filters"] = filters.ToJsonString(JsonOpts);

        // keep-all-filters toggle (the "Keep all filters" switch on a drill-through page).
        if (keepAllFilters.HasValue)
        {
            var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
            var objects = (cfg["objects"] as JsonObject) ?? new JsonObject(); cfg["objects"] = objects;
            // FLAG: the keep-all-filters flag lives on the page binding card (pageInformation.dropFilters in
            // legacy); keepAllFilters=true => dropFilters=false (do NOT drop incoming filters).
            MergeProperty(objects, "pageInformation", "keepAllFilters", JsonValue.Create(keepAllFilters.Value));
            section["config"] = cfg.ToJsonString(JsonOpts);
        }

        session.Dirty = true;
        return new { ok = true, page, drillthroughFields = fields.Select(f => $"{f.table}[{f.field}]").ToArray(),
            keepAllFilters, count = fields.Count,
            note = "Drill-through carried fields written as section.filters howCreated=5; keep-all-filters on the page binding (confirm in Desktop)." };
    }

    /// <summary>
    /// Enable (or disable) CROSS-REPORT drill-through ON a drill-through page: writes the page's pageBinding with
    /// type=Drillthrough and referenceScope=CrossReport, so the page can be a drill-through target FROM ANOTHER
    /// REPORT in the same workspace. enable=false reverts to a same-report (referenceScope-less) drill-through
    /// binding. The page should already be flagged with set_page_type drillthrough and carry drill-through fields.
    /// FLAG (Desktop validation): cross-report drill-through is gated by the report-level useCrossReportDrillthrough
    /// setting (set_report_settings) AND the page pageBinding.referenceScope=CrossReport. We write both the page
    /// binding here and recommend enabling the report setting.
    /// </summary>
    public object SetCrossReportDrillthrough(string reportSessionId, string page, bool enable)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);

        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var binding = new JsonObject { ["type"] = "Drillthrough" };
        if (enable) binding["referenceScope"] = "CrossReport";
        cfg["pageBinding"] = binding;
        section["config"] = cfg.ToJsonString(JsonOpts);

        // also flip the report-level useCrossReportDrillthrough setting so the feature is on report-wide.
        var root = session.Layout.Root;
        var rcfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var settings = (rcfg["settings"] as JsonObject) ?? new JsonObject(); rcfg["settings"] = settings;
        settings["useCrossReportDrillthrough"] = Encode(enable ? "true" : "false", FmtKind.Bool);
        root["config"] = rcfg.ToJsonString(JsonOpts);

        session.Dirty = true;
        return new { ok = true, page = (string?)section["name"], crossReport = enable,
            slot = "section.config.pageBinding.referenceScope + layout.config.settings.useCrossReportDrillthrough",
            note = "Cross-report drill-through written on the page binding + report setting (confirm in Desktop)." };
    }

    // ---- 4. drill-down behaviour ----------------------------------------------------------------

    /// <summary>Set DRILL-DOWN behaviour on a visual with a drillable (hierarchy/multi-level) axis: whether a
    /// click expands to the next level (expandToNextLevel / drillOnClick) and seeds the saved drill state
    /// (singleVisual.expansionStates). Writes the drill behaviour into singleVisual.objects.general
    /// (drillFilterOtherVisuals stays as-is) + seeds an empty expansionStates so the visual opens at the
    /// requested level.
    /// FLAG (Desktop validation): expansionStates is the durable saved-drill slot
    /// (singleVisual.expansionStates), and the expand-on-click behaviour is a visual-level toggle. The exact
    /// property names (expandCollapse / drillExpandOnClick) should be confirmed in Desktop.</summary>
    public object SetDrilldown(string reportSessionId, string page, string visual, bool? expandToNextLevel, bool? drillOnClick)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        // the drill behaviour card (general.drill* toggles).
        if (expandToNextLevel.HasValue)
            MergeProperty(objects, "general", "expandToNextLevel", JsonValue.Create(expandToNextLevel.Value));
        if (drillOnClick.HasValue)
            MergeProperty(objects, "general", "drillOnClick", JsonValue.Create(drillOnClick.Value));

        // seed the saved drill state (expansionStates) so the visual opens at a defined level.
        if (sv["expansionStates"] is not JsonArray)
            sv["expansionStates"] = new JsonArray
            {
                new JsonObject
                {
                    ["roles"] = new JsonArray { "Rows", "Category" },
                    ["levels"] = new JsonArray(),
                },
            };

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], expandToNextLevel, drillOnClick,
            seededExpansionStates = true,
            note = "Drill behaviour on objects.general + expansionStates seeded (best-known legacy slot; confirm in Desktop)." };
    }

    // ---- 5. navigator visuals (page navigator / bookmark navigator) -----------------------------

    /// <summary>Add a built-in NAVIGATOR visual: a pageNavigator (auto-lists the report's pages as buttons) or
    /// a bookmarkNavigator (auto-lists the bookmarks). These are first-class visualTypes the product renders
    /// without manual buttons. type = pageNavigator | bookmarkNavigator.</summary>
    public object AddNavigator(string reportSessionId, string page, string navigatorType,
        double x, double y, double width, double height)
    {
        string vt = navigatorType.Trim().ToLowerInvariant() switch
        {
            "pagenavigator" or "page" => "pageNavigator",
            "bookmarknavigator" or "bookmark" => "bookmarkNavigator",
            _ => throw new ArgumentException($"unknown navigator type '{navigatorType}' (use pageNavigator|bookmarkNavigator)."),
        };
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var containers = (section["visualContainers"] as JsonArray)!;

        var sv = new JsonObject
        {
            ["visualType"] = vt,
            ["drillFilterOtherVisuals"] = true,
            // the navigator auto-populates from pages/bookmarks; no field bindings are required.
            ["objects"] = new JsonObject(),
        };
        string id = NewVisualId();
        AddContainer(containers, id, sv, x, y, 0, width, height);
        session.Dirty = true;
        return new { ok = true, visualName = id, page = (string?)section["name"], visualType = vt };
    }

    // ---- 6. granular theme authoring ------------------------------------------------------------

    /// <summary>Get the report's LIVE custom theme object so a granular theme tool can mutate it, throwing if
    /// none exists (run generate_theme / apply_report_theme first).</summary>
    private JsonObject LiveTheme(ReportSession session)
    {
        var cfg = JsonNode.Parse((string?)session.Layout.Root["config"] ?? "{}") as JsonObject ?? new JsonObject();
        if (cfg["themeCollection"]?["customTheme"] is not JsonObject live)
            throw new InvalidOperationException("No custom theme to edit - run generate_theme or apply_report_theme first.");
        return JsonNode.Parse(live.ToJsonString()) as JsonObject ?? throw new InvalidOperationException("theme parse failed.");
    }

    /// <summary>Like LiveTheme but seeds a minimal base theme when the report has none yet, so the
    /// theme-writing helpers (wildcard defaults / palette generation) work on a fresh report.</summary>
    private JsonObject LiveThemeOrNew(ReportSession session)
    {
        var cfg = JsonNode.Parse((string?)session.Layout.Root["config"] ?? "{}") as JsonObject ?? new JsonObject();
        if (cfg["themeCollection"]?["customTheme"] is JsonObject live)
            return JsonNode.Parse(live.ToJsonString()) as JsonObject ?? new JsonObject { ["name"] = "Generated" };
        return new JsonObject { ["name"] = "Generated" };
    }

    /// <summary>Split a "Table[Field]" reference into (table, field). Throws if not in that shape.</summary>
    private static (string table, string field) ParseFieldRef(string fieldRef)
    {
        string s = (fieldRef ?? "").Trim();
        int lb = s.IndexOf('[');
        if (lb <= 0 || !s.EndsWith("]"))
            throw new ArgumentException($"field reference must be \"Table[Field]\" (got '{fieldRef}').");
        return (s[..lb].Trim().Trim('\''), s[(lb + 1)..^1]);
    }

    /// <summary>Set the theme's DATA COLOURS array (the categorical palette).</summary>
    public object SetThemeDataColors(string reportSessionId, IReadOnlyList<string> colors)
    {
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveTheme(session);
        var dc = new JsonArray();
        foreach (var c in colors) if (NormalizeHex(c) is string h) dc.Add(h);
        if (dc.Count == 0) throw new ArgumentException("at least one valid hex colour is required.");
        theme["dataColors"] = dc;
        ApplyThemeObject(session, theme);
        return new { ok = true, dataColors = dc.Count };
    }

    /// <summary>Set the theme's SENTIMENT colours: good / neutral / bad (the KPI / sentiment ramp).</summary>
    public object SetThemeSentimentColors(string reportSessionId, string? good, string? neutral, string? bad)
    {
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveTheme(session);
        int n = 0;
        if (NormalizeHex(good) is string g) { theme["good"] = g; n++; }
        if (NormalizeHex(neutral) is string ne) { theme["neutral"] = ne; n++; }
        if (NormalizeHex(bad) is string b) { theme["bad"] = b; n++; }
        if (n == 0) throw new ArgumentException("supply at least one of good / neutral / bad as a hex colour.");
        ApplyThemeObject(session, theme);
        return new { ok = true, good = (string?)theme["good"], neutral = (string?)theme["neutral"], bad = (string?)theme["bad"] };
    }

    /// <summary>Set the theme's CONDITIONAL-FORMATTING gradient stops: minimum / center / maximum / null
    /// (maximum/center/minimum/null in the theme schema).</summary>
    public object SetThemeCfColors(string reportSessionId, string? min, string? center, string? max, string? nul)
    {
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveTheme(session);
        int n = 0;
        if (NormalizeHex(min) is string mn) { theme["minimum"] = mn; n++; }
        if (NormalizeHex(center) is string ce) { theme["center"] = ce; n++; }
        if (NormalizeHex(max) is string mx) { theme["maximum"] = mx; n++; }
        if (NormalizeHex(nul) is string nl) { theme["null"] = nl; n++; }
        if (n == 0) throw new ArgumentException("supply at least one of min / center / max / null as a hex colour.");
        ApplyThemeObject(session, theme);
        return new { ok = true, minimum = (string?)theme["minimum"], center = (string?)theme["center"],
            maximum = (string?)theme["maximum"], @null = (string?)theme["null"] };
    }

    // the canonical structural-colour keys + their Power BI aliases (we write the canonical key).
    private static readonly Dictionary<string, string> StructuralColorKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["firstLevelElements"] = "firstLevelElements",
        ["secondLevelElements"] = "secondLevelElements",
        ["thirdLevelElements"] = "thirdLevelElements",
        ["fourthLevelElements"] = "fourthLevelElements",
        ["background"] = "background",
        ["secondaryBackground"] = "secondaryBackground",
        ["tableAccent"] = "tableAccent",
        // aliases
        ["foreground"] = "firstLevelElements",
    };

    /// <summary>Set the theme's STRUCTURAL colours from a JSON object of
    /// { firstLevelElements, secondLevelElements, thirdLevelElements, fourthLevelElements, background,
    /// secondaryBackground, tableAccent } (the 6-level structural palette + table accent). Only supplied keys
    /// change. Each value is a hex colour.</summary>
    public object SetThemeStructuralColors(string reportSessionId, string colorsJson)
    {
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveTheme(session);
        var spec = JsonNode.Parse(colorsJson) as JsonObject
                   ?? throw new InvalidOperationException("structural colours must be a JSON object of key -> hex.");
        var applied = new List<string>();
        foreach (var (key, val) in spec)
        {
            if (val is null) continue;
            if (!StructuralColorKeys.TryGetValue(key, out var canon)) continue;   // ignore unknown keys
            if (NormalizeHex(val.ToString()) is string h) { theme[canon] = h; applied.Add(canon); }
        }
        if (applied.Count == 0) throw new ArgumentException("no recognised structural colour keys supplied.");
        ApplyThemeObject(session, theme);
        return new { ok = true, structuralColorsSet = applied.ToArray(), count = applied.Count };
    }

    // the theme text classes (4 primary + 8 secondary) the schema declares.
    private static readonly HashSet<string> TextClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "callout", "title", "header", "label",
        "largeTitle", "semiboldLabel", "largeLabel", "smallLabel",
        "lightLabel", "boldLabel", "largeLightLabel", "smallLightLabel",
    };

    /// <summary>Set/merge ONE theme TEXT CLASS (callout / title / header / label or a secondary class): any
    /// subset of fontFace / fontSize / color. Merges into the existing class so untouched props are kept.</summary>
    public object SetThemeTextClass(string reportSessionId, string textClass, string? fontFace, double? fontSize, string? color)
    {
        if (!TextClassNames.Contains(textClass))
            throw new ArgumentException($"unknown text class '{textClass}' (callout|title|header|label + 8 secondary).");
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveTheme(session);

        var classes = (theme["textClasses"] as JsonObject) ?? new JsonObject(); theme["textClasses"] = classes;
        // canonicalise the casing to the schema's lowerCamelCase first letter (find the existing key, else use as-is).
        string key = classes.Select(kv => kv.Key).FirstOrDefault(k => string.Equals(k, textClass, StringComparison.OrdinalIgnoreCase)) ?? textClass;
        var cls = (classes[key] as JsonObject) ?? new JsonObject(); classes[key] = cls;
        if (!string.IsNullOrWhiteSpace(fontFace)) cls["fontFace"] = fontFace;
        if (fontSize.HasValue) cls["fontSize"] = fontSize.Value;
        if (NormalizeHex(color) is string h) cls["color"] = h;
        if (cls.Count == 0) throw new ArgumentException("supply at least one of fontFace / fontSize / color.");

        ApplyThemeObject(session, theme);
        return new { ok = true, textClass = key, fontFace = (string?)cls["fontFace"],
            fontSize = Num(cls["fontSize"]), color = (string?)cls["color"] };
    }

    /// <summary>Add a NAMED VISUAL-STYLE PRESET to the theme: theme.visualStyles[visualType][presetName] = the
    /// card definitions. cardPropertiesJson is the card map { cardName: [ { prop: value } ] } (or a single
    /// object per card, normalised to an array as the schema requires). A preset name lets a visual opt into
    /// the look via its stylePreset card. Creates the visualStyles tree as needed.
    /// FLAG (Desktop validation): the theme schema models each card as an ARRAY of property objects; we
    /// accept either form and store an array. Validate the preset shows in Desktop's style gallery.</summary>
    public object AddThemeVisualStylePreset(string reportSessionId, string visualType, string presetName, string cardPropertiesJson)
    {
        if (string.IsNullOrWhiteSpace(visualType)) throw new ArgumentException("visualType is required.");
        if (string.IsNullOrWhiteSpace(presetName)) throw new ArgumentException("presetName is required.");
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveTheme(session);

        var cards = JsonNode.Parse(cardPropertiesJson) as JsonObject
                    ?? throw new InvalidOperationException("cardProperties must be a JSON object of cardName -> property map / array.");

        // normalise each card to an ARRAY of property objects (the theme schema's card body shape).
        var preset = new JsonObject();
        foreach (var (cardName, body) in cards)
        {
            JsonArray arr = body switch
            {
                JsonArray a => (JsonArray)a.DeepClone(),
                JsonObject o => new JsonArray { o.DeepClone() },
                _ => new JsonArray(),
            };
            preset[cardName] = arr;
        }

        var visualStyles = (theme["visualStyles"] as JsonObject) ?? new JsonObject(); theme["visualStyles"] = visualStyles;
        var byType = (visualStyles[visualType] as JsonObject) ?? new JsonObject(); visualStyles[visualType] = byType;
        byType[presetName] = preset;

        ApplyThemeObject(session, theme);
        return new { ok = true, visualType, preset = presetName, cards = preset.Count };
    }

    // ---- 7. accessibility: alt text -------------------------------------------------------------

    /// <summary>Set a visual's ACCESSIBILITY ALT TEXT (read by screen readers): writes
    /// singleVisual.objects.general.altText (the general formatting card's altText property), encoded as a
    /// text literal like every other formatting value.</summary>
    public object SetAltText(string reportSessionId, string page, string visual, string text)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        var objects = (sv["objects"] as JsonObject) ?? new JsonObject(); sv["objects"] = objects;
        MergeProperty(objects, "general", "altText", JsonValue.Create(text ?? ""));

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], altText = text };
    }

    // ---- 8. accessibility: tab order ------------------------------------------------------------

    /// <summary>Set the KEYBOARD TAB ORDER of a page's visuals: visualOrder is the list of visual names in the
    /// order a keyboard user should tab through them. Writes each visual's config.layouts[0].position.tabOrder
    /// (the legacy tab-order slot) in steps so the order is deterministic. Visuals not listed keep their
    /// existing tabOrder. A visual can be hidden from tab order by giving it a negative order (we encode -1).
    /// FLAG (Desktop validation): legacy Report/Layout carries tab order on layouts[].position.tabOrder; PBIR
    /// keeps it on position.tabOrder too. Confirm the hide-from-tab-order convention (-1) in Desktop.</summary>
    public object SetTabOrder(string reportSessionId, string page, IReadOnlyList<string> visualOrder)
    {
        if (visualOrder.Count == 0) throw new ArgumentException("visualOrder must list at least one visual.");
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);

        var applied = new List<string>();
        int order = 0;
        foreach (var name in visualOrder)
        {
            var (vc, co, _) = ResolveVisual(section, name);
            if (co["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject pos)
            {
                pos["tabOrder"] = order;
                vc["config"] = co.ToJsonString(JsonOpts);
                applied.Add(name);
                order += 1000;   // space them so a later insert can slot between without a renumber
            }
        }
        if (applied.Count == 0) throw new InvalidOperationException("none of the named visuals were found on the page.");

        session.Dirty = true;
        return new { ok = true, page, tabOrder = applied.ToArray(), count = applied.Count,
            note = "Tab order written to each visual's layouts[0].position.tabOrder (confirm in Desktop)." };
    }

    /// <summary>
    /// PAGE-LEVEL tab order with explicit HIDE: orderedVisuals get sequential tabOrder (0,1,2,...) in that order;
    /// any visual in hidden[] gets tabOrder -1 (removed from the keyboard tab sequence - the accessibility
    /// "hide from tab order" convention). Visuals in neither list keep their existing tabOrder. Writes each
    /// visual's layouts[0].position.tabOrder.
    /// FLAG (Desktop validation): tabOrder = -1 is the legacy convention for "not in tab order"; confirm in Desktop.
    /// </summary>
    public object SetPageTabOrder(string reportSessionId, string page, IReadOnlyList<string> orderedVisuals, IReadOnlyList<string>? hidden)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);

        void Apply(string name, int order)
        {
            var (vc, co, _) = ResolveVisual(section, name);
            if (co["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject pos)
            {
                pos["tabOrder"] = order;
                vc["config"] = co.ToJsonString(JsonOpts);
            }
        }

        var ordered = new List<string>();
        int seq = 0;
        foreach (var name in orderedVisuals) { Apply(name, seq++); ordered.Add(name); }
        var hid = new List<string>();
        if (hidden != null) foreach (var name in hidden) { Apply(name, -1); hid.Add(name); }

        if (ordered.Count == 0 && hid.Count == 0)
            throw new ArgumentException("supply orderedVisuals and/or hidden.");

        session.Dirty = true;
        return new { ok = true, page, tabOrder = ordered.ToArray(), hidden = hid.ToArray(),
            note = "Page tab order written to layouts[0].position.tabOrder; hidden visuals set to -1 (confirm in Desktop)." };
    }

    /// <summary>
    /// SHOW ITEMS WITH NO DATA on a projection: turn on "Show items with no data" for one field in a visual, so
    /// categories with no rows still appear (e.g. all months even with zero sales). Writes the field's
    /// projection "showAll" flag in the visual's projections (keyed by the field's queryRef).
    /// FLAG (Desktop validation): legacy Report/Layout carries show-items-no-data as the projection's "showAll"
    /// flag; confirm the property name in Desktop.
    /// </summary>
    public object SetShowItemsNoData(string reportSessionId, string page, string visual, string table, string field, bool show)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        string queryRef = $"{table}.{field}";
        bool found = false;
        if (sv["projections"] is JsonObject projections)
            foreach (var (_, refs) in projections)
                if (refs is JsonArray ra)
                    foreach (var r in ra)
                        if (r is JsonObject ro && (string?)ro["queryRef"] == queryRef)
                        {
                            if (show) ro["showAll"] = true; else ro.Remove("showAll");
                            found = true;
                        }
        if (!found)
            throw new InvalidOperationException($"field {table}[{field}] is not projected on visual '{visual}' - bind it first.");

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual = (string?)co["name"], field = $"{table}[{field}]", showItemsWithNoData = show };
    }

    // ---- 9. personalisation / inline exploration ------------------------------------------------

    /// <summary>Turn PERSONALISATION (inline exploration) on/off. scope = report enables the report-level
    /// allowInlineExploration setting (a viewer can re-jig visuals for themselves); scope = page sets the
    /// per-page personalizeVisual object. perVisualPersonalize, when scope=page, sets the page's
    /// personalizeVisual.show toggle. report scope writes to layout.config.settings (the ExplorationSettings).</summary>
    public object SetPersonalization(string reportSessionId, string scope, string? page,
        bool? allowInlineExploration, bool? perVisualPersonalize)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        string sc = (scope ?? "report").Trim().ToLowerInvariant();

        if (sc == "report")
        {
            if (!allowInlineExploration.HasValue)
                throw new ArgumentException("scope=report needs allowInlineExploration.");
            var cfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
            var settings = (cfg["settings"] as JsonObject) ?? new JsonObject(); cfg["settings"] = settings;
            settings["allowInlineExploration"] = Encode(allowInlineExploration.Value ? "true" : "false", FmtKind.Bool);
            root["config"] = cfg.ToJsonString(JsonOpts);
            session.Dirty = true;
            return new { ok = true, scope = "report", allowInlineExploration = allowInlineExploration.Value };
        }
        if (sc == "page")
        {
            if (string.IsNullOrWhiteSpace(page)) throw new ArgumentException("scope=page needs a page.");
            var section = FindSection(root, page!);
            var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
            var objects = (cfg["objects"] as JsonObject) ?? new JsonObject(); cfg["objects"] = objects;
            // the page personalizeVisual card carries the show toggle.
            bool show = perVisualPersonalize ?? allowInlineExploration ?? true;
            MergeProperty(objects, "personalizeVisual", "show", JsonValue.Create(show));
            section["config"] = cfg.ToJsonString(JsonOpts);
            session.Dirty = true;
            return new { ok = true, scope = "page", page, perVisualPersonalize = show };
        }
        throw new ArgumentException($"unknown scope '{scope}' (use report|page).");
    }

    // ============================================================================================
    //  WAVE O - community visual/measure design HELPERS (report side). Dynamic titles, button-state CF,
    //  house-style wildcard defaults, palette generation into the theme, the HTML-content custom visual,
    //  and reusable report templates. SVG-image measures + format helpers live on the model side.
    // ============================================================================================

    /// <summary>
    /// Bind a visual's TITLE to a text MEASURE (expression-based title): writes vcObjects.title.text as a
    /// Measure-bound expr via the {measure} shorthand, and forces title show=true. The measure must return a
    /// string (e.g. a SELECTEDVALUE / CONCATENATEX narrative with an "All/multiple" fallback). titleMeasure =
    /// "Table[Measure]" (or pass titleMeasureTable). This is the report half of add_dynamic_title - author the
    /// text measure on the model with add_dynamic_title_measure (or add_measure), then bind it here.
    /// </summary>
    public object BindDynamicTitle(string reportSessionId, string page, string visual,
        string titleMeasure, string? titleMeasureTable)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, visual);

        var bound = MeasureBoundExpr(new JsonObject { ["measure"] = titleMeasure, ["table"] = titleMeasureTable })
                    ?? throw new ArgumentException("titleMeasure must be \"Table[Measure]\" or pass titleMeasureTable.");

        var vco = (sv["vcObjects"] as JsonObject) ?? new JsonObject(); sv["vcObjects"] = vco;
        // title.text = the measure-bound expr; title.show = true. Preserve any other title props.
        MergeProperty(vco, "title", "show", JsonValue.Create(true));
        var arr = (vco["title"] as JsonArray)!;
        var entry = (JsonObject)arr[0]!;
        var props = (entry["properties"] as JsonObject)!;
        props["text"] = bound.DeepClone();

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, visual, titleMeasure, kind = "expression-based title (measure-bound)" };
    }

    /// <summary>
    /// Measure-driven per-STATE button formatting: drive a button's fill / text / icon colour by a measure
    /// (a hex/CSS colour) for a given state (default|hover|pressed|selected). Writes the colour as a
    /// measure-bound expr onto the button's card with a state selector (the same {id:state} selector the
    /// text card uses). target=fill|text|icon. button is the actionButton's visual name.
    /// </summary>
    public object SetButtonStateCf(string reportSessionId, string page, string button, string colorMeasure,
        string state, string target, string? colorMeasureTable)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisual(section, button);

        string st = (state ?? "default").Trim().ToLowerInvariant();
        if (st is not ("default" or "hover" or "pressed" or "selected"))
            throw new ArgumentException($"unknown state '{state}' (use default|hover|pressed|selected).");
        (string card, string prop) = (target ?? "fill").Trim().ToLowerInvariant() switch
        {
            "fill" => ("fill", "fillColor"),
            "text" => ("text", "fontColor"),
            "icon" => ("icon", "fill"),
            _ => throw new ArgumentException($"unknown target '{target}' (use fill|text|icon)."),
        };

        var bound = MeasureBoundExpr(new JsonObject { ["measure"] = colorMeasure, ["table"] = colorMeasureTable })
                    ?? throw new ArgumentException("colorMeasure must be \"Table[Measure]\" or pass colorMeasureTable.");

        var vco = (sv["vcObjects"] as JsonObject) ?? new JsonObject(); sv["vcObjects"] = vco;
        var arr = (vco[card] as JsonArray) ?? new JsonArray(); vco[card] = arr;
        // find (or add) the entry whose selector targets this state.
        JsonObject? entry = null;
        foreach (var n in arr)
            if (n is JsonObject e && (string?)e["selector"]?["id"] == st) { entry = e; break; }
        if (entry == null) { entry = Sel(new JsonObject(), st); arr.Add(entry); }
        var props = (entry["properties"] as JsonObject)!;
        // colour cards carry the solid wrapper around the measure-bound expr.
        props[prop] = new JsonObject { ["solid"] = new JsonObject { ["color"] = bound.DeepClone() } };

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, button, state = st, target = $"{card}/{prop}", colorMeasure };
    }

    /// <summary>
    /// Set the theme's GLOBAL WILDCARD defaults ("*":{"*":[ {...} ]}) - house-style formatting applied to
    /// every card of every visual in one shot. propsJson is a { cardName: {prop:value} | [ {prop:value} ] }
    /// map; each card is normalised to the array-of-property-objects shape the theme schema requires.
    /// FLAG: the "card" card uses a "$id":"default" quirk in the theme schema; we pass card bodies through
    /// verbatim, so to target it supply a card body that already carries the "$id":"default" object.
    /// </summary>
    public object SetGlobalWildcardDefaults(string reportSessionId, string propsJson)
    {
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveThemeOrNew(session);
        var cards = JsonNode.Parse(propsJson) as JsonObject
                    ?? throw new InvalidOperationException("props must be a JSON object of cardName -> property map / array.");

        var def = new JsonObject();
        foreach (var (cardName, body) in cards)
        {
            def[cardName] = body switch
            {
                JsonArray a => (JsonNode)a.DeepClone(),
                JsonObject o => new JsonArray { o.DeepClone() },
                _ => new JsonArray(),
            };
        }
        var visualStyles = (theme["visualStyles"] as JsonObject) ?? new JsonObject(); theme["visualStyles"] = visualStyles;
        var star = (visualStyles["*"] as JsonObject) ?? new JsonObject(); visualStyles["*"] = star;
        // merge into the existing wildcard defaults rather than clobbering them.
        var existing = (star["*"] as JsonObject) ?? new JsonObject();
        foreach (var (k, vNode) in def) existing[k] = vNode!.DeepClone();
        star["*"] = existing;

        ApplyThemeObject(session, theme);
        return new { ok = true, scope = "*/*", cards = def.Count };
    }

    /// <summary>
    /// COMPUTE a palette and write it into the theme: an n-colour dataColors[] plus good/neutral/bad and the
    /// CF min/center/max stops. mode=harmonic (hue-wheel spread off a base), monochrome (lightness ramp of a
    /// base) or gradient (interpolate between two endpoints). For gradient, pass gradientFrom + gradientTo;
    /// otherwise pass baseColor.
    /// </summary>
    public object GeneratePaletteIntoTheme(string reportSessionId, string? baseColor, string? gradientFrom,
        string? gradientTo, int n, string mode)
    {
        var session = _sessions.GetReport(reportSessionId);
        var theme = LiveThemeOrNew(session);
        string[] palette = ComputePalette(baseColor, gradientFrom, gradientTo, n, mode);
        if (palette.Length == 0) throw new ArgumentException("could not compute a palette - supply a valid baseColor or gradient endpoints.");

        var dc = new JsonArray(); foreach (var c in palette) dc.Add(c);
        theme["dataColors"] = dc;
        // sentiment + CF stops derived from the palette ends.
        theme["good"] = "#2E7D32"; theme["neutral"] = "#E8A93B"; theme["bad"] = "#D7263D";
        theme["minimum"] = palette[^1]; theme["center"] = palette[palette.Length / 2]; theme["maximum"] = palette[0];
        ApplyThemeObject(session, theme);
        return new { ok = true, mode, count = palette.Length, dataColors = palette,
            min = (string?)theme["minimum"], center = (string?)theme["center"], max = (string?)theme["maximum"] };
    }

    /// <summary>Compute an n-colour palette by mode. harmonic reuses the hue-wheel GeneratePalette; monochrome
    /// ramps a base's lightness; gradient interpolates RGB between two endpoints.</summary>
    internal static string[] ComputePalette(string? baseColor, string? gradientFrom, string? gradientTo, int n, string mode)
    {
        n = Math.Max(1, n);
        string m = (mode ?? "harmonic").Trim().ToLowerInvariant();
        if (m == "gradient")
        {
            string a = NormalizeHex(gradientFrom) ?? throw new ArgumentException("gradient mode needs gradientFrom.");
            string b = NormalizeHex(gradientTo) ?? throw new ArgumentException("gradient mode needs gradientTo.");
            var (ar, ag, ab) = HexToRgb(a); var (br, bg, bb) = HexToRgb(b);
            var outc = new string[n];
            for (int i = 0; i < n; i++)
            {
                double f = n == 1 ? 0 : (double)i / (n - 1);
                outc[i] = RgbToHex(ar + (br - ar) * f, ag + (bg - ag) * f, ab + (bb - ab) * f);
            }
            return outc;
        }
        string baseHex = NormalizeHex(baseColor) ?? throw new ArgumentException($"{m} mode needs a baseColor.");
        if (m == "monochrome")
        {
            var (r, g, bl) = HexToRgb(baseHex);
            var (h, s, _) = RgbToHsl(r, g, bl);
            var outc = new string[n];
            for (int i = 0; i < n; i++)
            {
                double l = n == 1 ? 0.5 : 0.25 + 0.5 * i / (n - 1);   // ramp lightness 0.25 -> 0.75
                var (rr, gg, bb) = HslToRgb(h, s, l);
                outc[i] = RgbToHex(rr, gg, bb);
            }
            return outc;
        }
        // harmonic (default): the existing hue-wheel generator, base colour first.
        return GeneratePalette(baseHex, n);
    }

    /// <summary>
    /// Add the HTML Content (lite) certified custom visual bound to a DAX measure that returns HTML/CSS:
    /// registers the visual type, adds the visual, and binds the measure to its "values" data role so the
    /// visual renders the measure's HTML. daxHtmlMeasure = "Table[Measure]" - the measure must already exist
    /// on the model and return an HTML string. visualGuid lets you override the visual type id.
    /// FLAG (Desktop validation): the HTML Content (lite) visualType guid + its data role name are matched to
    /// the certified visual; confirm the guid/role against the imported .pbiviz in Desktop.
    /// </summary>
    public object AddHtmlContentBlock(string reportSessionId, string page, string name, string daxHtmlMeasure,
        double x, double y, double w, double h, string? visualGuid)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (table, field) = ParseFieldRef(daxHtmlMeasure);

        string vtype = string.IsNullOrWhiteSpace(visualGuid) ? "htmlContentLite" : visualGuid!;
        // register the custom visual type so the visualType resolves.
        RegisterCustomVisual(reportSessionId, vtype, null, null);

        // bind the HTML measure to the visual's "values" role (the HTML Content visual's content slot).
        var bindings = new[] { new FieldBinding("Values", table, field, "measure") };
        var sv = BuildSingleVisual(vtype, bindings, name);
        var containers = (section["visualContainers"] as JsonArray)!;
        string id = Guid.NewGuid().ToString("N");
        AddContainer(containers, id, sv, x, y, 0, w, h);
        session.Dirty = true;
        return new { ok = true, visualName = id, page = (string?)section["name"], visualType = vtype,
            boundMeasure = $"{table}[{field}]", note = "HTML Content (lite) visual bound to the DAX HTML measure (confirm visual guid in Desktop)." };
    }

    /// <summary>
    /// Apply a reusable REPORT TEMPLATE in one call: a bundle of wallpaper + theme + canvas-preset + nav
    /// settings. templateJson = { theme?:{...}, wallpaper?:{color,transparency}, canvas?:{preset,width,height},
    /// nav?:{ <ExplorationSettings> } }. page targets the wallpaper + canvas (defaults to the first page).
    /// </summary>
    public object ApplyReportTemplate(string reportSessionId, string templateJson, string? page)
    {
        var session = _sessions.GetReport(reportSessionId);
        var spec = JsonNode.Parse(templateJson) as JsonObject
                   ?? throw new InvalidOperationException("template must be a JSON object.");
        string targetPage = page ?? (string?)(Sections(session.Layout.Root).FirstOrDefault() as JsonObject)?["name"]
                             ?? throw new InvalidOperationException("no page to apply the template to.");
        var applied = new List<string>();

        if (spec["theme"] is JsonObject theme) { ApplyThemeObject(session, (JsonObject)theme.DeepClone()); applied.Add("theme"); }
        if (spec["wallpaper"] is JsonObject wp)
        {
            SetPageWallpaper(reportSessionId, targetPage, (string?)wp["color"], Num(wp["transparency"]));
            applied.Add("wallpaper");
        }
        if (spec["canvas"] is JsonObject cv)
        {
            SetCanvasPreset(reportSessionId, targetPage, (string?)cv["preset"] ?? "custom", Num(cv["width"]), Num(cv["height"]));
            applied.Add("canvas");
        }
        if (spec["nav"] is JsonObject nav) { SetReportSettings(reportSessionId, nav.ToJsonString()); applied.Add("nav"); }

        return new { ok = true, page = targetPage, applied = applied.ToArray() };
    }

    /// <summary>
    /// Save the report's current look as a reusable TEMPLATE bundle (the inverse of apply_report_template):
    /// captures the live theme, the page's wallpaper (outspace) + canvas size, and the report nav settings
    /// into one templateJson the caller can re-apply later. page defaults to the first page.
    /// </summary>
    public object SaveReportTemplate(string reportSessionId, string name, string? page)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        string targetPage = page ?? (string?)(Sections(root).FirstOrDefault() as JsonObject)?["name"]
                            ?? throw new InvalidOperationException("no page to capture.");
        var section = FindSection(root, targetPage);

        var template = new JsonObject { ["name"] = string.IsNullOrWhiteSpace(name) ? "Template" : name };
        var rootCfg = JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject;
        if (rootCfg?["themeCollection"]?["customTheme"] is JsonObject theme) template["theme"] = theme.DeepClone();
        if (rootCfg?["settings"] is JsonObject settings) template["nav"] = settings.DeepClone();

        var secCfg = JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject;
        if (secCfg?["objects"]?["outspace"] is JsonArray ous && ous.Count > 0 && ous[0] is JsonObject ow
            && ow["properties"] is JsonObject owp)
        {
            var wp = new JsonObject();
            if (Decode(owp["color"]) is string wc) wp["color"] = wc;
            if (Decode(owp["transparency"]) is string wt && double.TryParse(wt, out var wtd)) wp["transparency"] = wtd;
            template["wallpaper"] = wp;
        }
        template["canvas"] = new JsonObject
        {
            ["preset"] = "custom",
            ["width"] = Num(section["width"]) ?? 1280,
            ["height"] = Num(section["height"]) ?? 720,
        };

        return new { ok = true, name = (string?)template["name"], page = targetPage, templateJson = template.ToJsonString(JsonOpts) };
    }

    // ---- 10. custom-visual registration ---------------------------------------------------------

    /// <summary>Register an imported CUSTOM VISUAL (.pbiviz) into the report so its visualType can be used:
    /// adds the visual's guid to the report's publicCustomVisuals list (in layout.config), and, when a .pbiviz
    /// path is supplied, embeds it as a resource package + records it in resourcePackages. name is the visual's
    /// guid/visualType (the open-string used as a visual's visualType). guid defaults to name.
    /// FLAG (Desktop validation): legacy Report/Layout lists imported custom visuals as guid strings in
    /// layout.config.publicCustomVisuals (organizationCustomVisuals for org-store visuals). The exact
    /// resourcePackage type for a .pbiviz should be confirmed in Desktop; we register the guid (which is what
    /// makes the visualType usable) and stage the file when given.</summary>
    public object RegisterCustomVisual(string reportSessionId, string name, string? guid, string? path)
        => RegisterCustomVisualCore(reportSessionId, name, guid, path, "publicCustomVisuals");

    /// <summary>Register an ORGANIZATION-store custom visual: the same as register_custom_visual but lists the
    /// guid in layout.config.organizationCustomVisuals (the org app-source store) rather than publicCustomVisuals
    /// (the public AppSource store). Org-store visuals are managed by the tenant admin, so a .pbiviz is rarely
    /// embedded; the guid registration is what makes the visualType usable.</summary>
    public object RegisterOrgCustomVisual(string reportSessionId, string name, string? guid, string? path)
        => RegisterCustomVisualCore(reportSessionId, name, guid, path, "organizationCustomVisuals");

    private object RegisterCustomVisualCore(string reportSessionId, string name, string? guid, string? path, string listKey)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("a custom visual name (visualType/guid) is required.");
        var session = _sessions.GetReport(reportSessionId);
        var root = session.Layout.Root;
        string id = string.IsNullOrWhiteSpace(guid) ? name : guid!;

        var cfg = (JsonNode.Parse((string?)root["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var pub = (cfg[listKey] as JsonArray) ?? new JsonArray();
        cfg[listKey] = pub;
        bool already = pub.OfType<JsonValue>().Any(v => (string?)v == id);
        if (!already) pub.Add(id);
        root["config"] = cfg.ToJsonString(JsonOpts);

        // when a .pbiviz file is given, stage it as a resource + register a resource package (best-effort).
        string? staged = null;
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            string resName = "cv" + Guid.NewGuid().ToString("N")[..12] + ".pbiviz";
            session.PendingResources["Report/CustomVisuals/" + resName] = File.ReadAllBytes(path);
            staged = resName;

            var packages = (root["resourcePackages"] as JsonArray) ?? new JsonArray();
            root["resourcePackages"] = packages;
            packages.Add(new JsonObject
            {
                ["resourcePackage"] = new JsonObject
                {
                    ["name"] = id,
                    ["type"] = 2,   // FLAG: custom-visual package type (confirm in Desktop)
                    ["items"] = new JsonArray { new JsonObject { ["type"] = 101, ["path"] = resName, ["name"] = resName } },
                    ["disabled"] = false,
                },
            });
        }

        session.Dirty = true;
        return new { ok = true, customVisual = id, registered = !already, staged,
            slot = "layout.config." + listKey,
            note = $"Custom visual guid registered in {listKey}; .pbiviz staged into resourcePackages when given (confirm package type in Desktop)." };
    }

    // ---- Deneb / Vega-Lite first-class visual ---------------------------------------------------

    // The Deneb custom-visual visualType GUID. FLAG: overridable - confirm against the imported Deneb .pbiviz
    // in Desktop. This is the published Deneb (deneb-powerbi-visual) marketplace guid string.
    public const string DenebDefaultGuid = "deneb1F0BC2FA97E94E48A4D85B033EC8E9D3";

    /// <summary>
    /// Encode a JSON spec (or config) for a Deneb vega/vega[0].properties slot. The spec is reduced to ONE
    /// line of JSON, then placed verbatim as a single-quoted string literal in expr.Literal.Value (internal
    /// single quotes doubled per the semantic-query string rule). This is the #1 Deneb failure mode: the JSON
    /// must round-trip - i.e. stripping the wrapping single quotes and un-doubling '' must parse back to the
    /// exact spec. We parse the supplied JSON (validates it) and re-serialise compact to guarantee one line.
    /// </summary>
    private static JsonObject DenebJsonLiteral(string json)
    {
        JsonNode? node;
        try { node = JsonNode.Parse(json); }
        catch (JsonException ex) { throw new ArgumentException($"spec/config must be valid JSON: {ex.Message}"); }
        if (node == null) throw new ArgumentException("spec/config must be valid JSON.");
        // compact, single-line; UnsafeRelaxedJsonEscaping matches PBI's literal style (keeps " as " inside the string).
        string oneLine = node.ToJsonString(JsonOpts);
        string quoted = "'" + oneLine.Replace("'", "''") + "'";
        return new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = quoted } } };
    }

    /// <summary>The inverse of DenebJsonLiteral - used by tests + diagnostics: take a wrapped expr-literal and
    /// recover the embedded JSON string (strip the single quotes, un-double '').</summary>
    public static string DenebDecodeJsonLiteral(JsonObject wrapped)
    {
        string? v = (string?)wrapped["expr"]?["Literal"]?["Value"]
                    ?? throw new ArgumentException("not a Deneb expr-literal.");
        if (v.Length < 2 || v[0] != '\'' || v[^1] != '\'') throw new ArgumentException("Deneb literal is not single-quoted.");
        return v[1..^1].Replace("''", "'");
    }

    /// <summary>
    /// Add a DENEB (Vega / Vega-Lite) custom visual to a page. Registers the Deneb custom-visual guid, adds a
    /// visual of that type, binds the supplied data fields into Deneb's "values" data role/projection, and
    /// writes visual.objects.vega[0].properties: jsonSpec (the spec one-lined + single-quoted in a literal),
    /// jsonConfig, provider (vega|vegaLite), renderMode (svg|canvas), and the enable* booleans.
    /// FLAG: the Deneb visual guid (visualGuid) defaults to the published marketplace guid - confirm/override
    /// against the imported .pbiviz in Desktop.
    /// </summary>
    public object AddDenebVisual(string reportSessionId, string page, string name, string spec,
        string provider, string renderMode, string? config,
        bool enableTooltips, bool enableSelection, bool enableHighlight,
        double x, double y, double w, double h,
        IReadOnlyList<FieldBinding>? dataRoles, string? visualGuid)
    {
        if (string.IsNullOrWhiteSpace(spec)) throw new ArgumentException("a Deneb spec (Vega/Vega-Lite JSON) is required.");
        string prov = provider.Trim().ToLowerInvariant() switch
        {
            "vega" => "vega",
            "vegalite" or "vega-lite" or "vl" => "vegaLite",
            _ => throw new ArgumentException($"unknown provider '{provider}' (use vega|vegaLite)."),
        };
        string render = renderMode.Trim().ToLowerInvariant() switch
        {
            "svg" => "svg", "canvas" => "canvas",
            _ => throw new ArgumentException($"unknown renderMode '{renderMode}' (use svg|canvas)."),
        };

        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        string vtype = string.IsNullOrWhiteSpace(visualGuid) ? DenebDefaultGuid : visualGuid!;

        // register the Deneb custom visual type so the visualType resolves.
        RegisterCustomVisual(reportSessionId, vtype, null, null);

        // bind any supplied fields into Deneb's "values" data role (its dataset). Default the role to "values".
        var bindings = (dataRoles ?? Array.Empty<FieldBinding>())
            .Select(b => string.IsNullOrWhiteSpace(b.Role) ? b with { Role = "values" } : b).ToList();
        var sv = BuildSingleVisual(vtype, bindings, name);

        // write the Deneb vega object: visual.objects.vega[0].properties.
        var props = new JsonObject
        {
            ["jsonSpec"] = DenebJsonLiteral(spec),
            ["provider"] = Lit($"'{prov}'"),
            ["renderMode"] = Lit($"'{render}'"),
            ["enableTooltips"] = Lit(enableTooltips ? "true" : "false"),
            ["enableSelection"] = Lit(enableSelection ? "true" : "false"),
            ["enableContextMenu"] = Lit("true"),
            ["enableHighlight"] = Lit(enableHighlight ? "true" : "false"),
        };
        if (!string.IsNullOrWhiteSpace(config))
            props["jsonConfig"] = DenebJsonLiteral(config!);

        var objects = (sv["objects"] as JsonObject) ?? new JsonObject();
        objects["vega"] = new JsonArray { new JsonObject { ["properties"] = props } };
        sv["objects"] = objects;

        var containers = (section["visualContainers"] as JsonArray)!;
        string id = Guid.NewGuid().ToString("N");
        AddContainer(containers, id, sv, x, y, 0, w <= 0 ? 480 : w, h <= 0 ? 320 : h);
        session.Dirty = true;
        return new
        {
            ok = true, visualName = id, page = (string?)section["name"], visualType = vtype,
            provider = prov, renderMode = render, dataFields = bindings.Count,
            enableTooltips, enableSelection, enableHighlight,
            note = "Deneb visual added; spec written as a single-line single-quoted literal in objects.vega[0].properties.jsonSpec. FLAG: confirm the Deneb visual guid in Desktop (override with visualGuid).",
        };
    }

    // ---- 11. page wallpaper ---------------------------------------------------------------------

    /// <summary>Set the page WALLPAPER (the grey margin OUTSIDE the canvas), distinct from set_page_background
    /// (which colours the canvas area). Writes the page's outspace object (section.config.objects.outspace) -
    /// colour + transparency. This is what fills the area around the report page in view mode.</summary>
    public object SetPageWallpaper(string reportSessionId, string page, string? color, double? transparency)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var cfg = (JsonNode.Parse((string?)section["config"] ?? "{}") as JsonObject) ?? new JsonObject();
        var objects = (cfg["objects"] as JsonObject) ?? new JsonObject(); cfg["objects"] = objects;

        var props = new JsonObject();
        if (NormalizeHex(color) is string h) props["color"] = Lit($"'{h}'", wrapSolid: true);
        props["transparency"] = Lit((transparency ?? 0).ToString(Inv) + "D");
        // the page wallpaper = the "outspace" object (the canvas surround), NOT background (the canvas).
        objects["outspace"] = new JsonArray { new JsonObject { ["properties"] = props } };

        cfg["objects"] = objects;
        section["config"] = cfg.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new { ok = true, page, wallpaper = NormalizeHex(color), transparency = transparency ?? 0,
            slot = "section.config.objects.outspace" };
    }

    // ============================================================================================
    //  OFFLINE REPORT-VISUAL EDIT - the general "edit a report visual" capability. update_visual_property
    //  sets ANY property at a JSON path under a visual's singleVisual config; set_slicer_selection is the
    //  targeted slicer single-select + default-selection fix (e.g. the field-parameter flat-lines bug).
    //  Both are ONE-SHOT: open the CLOSED .pbix, patch Report/Layout, Repack back (DataModel byte-preserved).
    // ============================================================================================

    /// <summary>Run a report-layer transform against a .pbix in ONE shot: open a fresh session, apply
    /// <paramref name="body"/>, Save (Repack the ZIP, DataModel preserved), then drop the session. The result
    /// object is folded into a JSON node with pbixPath + persistedToDisk stamped on. The .pbix must be CLOSED.</summary>
    private object RunOffline(string pbixPath, Func<string, object> body)
    {
        var session = LoadReportSession(pbixPath);
        try
        {
            var result = body(session.Id);
            Save(session.Id);
            var node = JsonSerializer.SerializeToNode(result) as JsonObject ?? new JsonObject();
            node["pbixPath"] = pbixPath;
            node["persistedToDisk"] = true;
            return node;
        }
        finally { _sessions.RemoveReport(session.Id); }
    }

    /// <summary>update_visual_property (ONE-SHOT): open a CLOSED .pbix, set the property, save back.</summary>
    public object UpdateVisualPropertyOffline(string pbixPath, string page, string visualRef,
        string propertyPath, string value, string valueKind = "auto")
        => RunOffline(pbixPath, sid => UpdateVisualProperty(sid, page, visualRef, propertyPath, value, valueKind));

    /// <summary>set_slicer_selection (ONE-SHOT): open a CLOSED .pbix, set single-select + default, save back.</summary>
    public object SetSlicerSelectionOffline(string pbixPath, string page, string slicerRef,
        bool singleSelect = true, string? defaultValue = null)
        => RunOffline(pbixPath, sid => SetSlicerSelection(sid, page, slicerRef, singleSelect, defaultValue));

    /// <summary>fix_slicer_single_select (ONE-SHOT): open a CLOSED .pbix, auto-find the field-parameter slicer
    /// on the page and make it single-select, save back. Needs ONLY {pbix, page} (both from the request).</summary>
    public object FixSlicerSingleSelectOffline(string pbixPath, string page, string? chart = null, string? defaultValue = null)
        => RunOffline(pbixPath, sid => FixSlicerSingleSelect(sid, page, chart, defaultValue));

    // ============================================================================================
    //  TIDY SLICER LAYOUT - conservative geometry cleanup for a page's (or the whole report's) slicers:
    //  (1) DE-OVERLAP slicer-on-slicer collisions by the SMALLEST clean nudge, and (2) SNAP-ALIGN slicers
    //  that sit just off a shared left/top edge. Positions ONLY - never resizes, never moves a non-slicer.
    //  Slicer-over-non-slicer overlaps are FLAGGED (not auto-moved) by default because they are frequently a
    //  deliberate header layering (a State slicer drawn over a wide date-label card); opt in with
    //  deOverlapNonSlicers to also nudge slicers clear of other visuals. Returns a per-move report.
    // ============================================================================================

    /// <summary>An axis-aligned box for one visual on a page while tidying. Geometry is worked purely on the
    /// doubles here; the layout JSON is only rewritten (via SetVisualBounds) for the slicers that actually moved.</summary>
    private sealed class TidyBox
    {
        public string Name = "";
        public string Label = "";
        public bool IsSlicer;
        public double X, Y, W, H;
        public double OrigX, OrigY;
        public string? Reason;               // "de-overlap", "snap-align", or "de-overlap + snap-align"
        public double Right => X + W;
        public double Bottom => Y + H;
        public void AddReason(string r) => Reason = Reason is null ? r : (Reason.Contains(r) ? Reason : Reason + " + " + r);
    }

    /// <summary>Penetration of two boxes along X (positive = they overlap by that many px on the X axis).</summary>
    private static double PenX(TidyBox a, TidyBox b) => Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
    private static double PenY(TidyBox a, TidyBox b) => Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);

    /// <summary>The minimal-translation penetration depth of two boxes: 0 (or negative) when they do not overlap,
    /// otherwise the smaller of the X/Y penetrations - the distance one must move to separate them.</summary>
    private static double Penetration(TidyBox a, TidyBox b)
    {
        double ox = PenX(a, b), oy = PenY(a, b);
        return (ox > 0 && oy > 0) ? Math.Min(ox, oy) : 0;
    }

    /// <summary>tidy_slicer_layout (ONE-SHOT): open a CLOSED .pbix, tidy its slicers, save back (DataModel byte-preserved).</summary>
    public object TidySlicerLayoutOffline(string pbixPath, string? page = null, bool deOverlapNonSlicers = false)
    {
        var session = LoadReportSession(pbixPath);
        try
        {
            var result = TidySlicerLayout(session.Id, page, deOverlapNonSlicers);
            bool changed = session.Dirty;
            if (changed) Save(session.Id);
            var node = JsonSerializer.SerializeToNode(result) as JsonObject ?? new JsonObject();
            node["pbixPath"] = pbixPath;
            node["persistedToDisk"] = changed;
            if (!changed)
                node["note"] = "No slicer moves were needed; the .pbix was left byte-for-byte unchanged.";
            return node;
        }
        finally { _sessions.RemoveReport(session.Id); }
    }

    /// <summary>
    /// TIDY the slicers on one page (when <paramref name="page"/> is given) or EVERY page (when null). For each
    /// page: read every rendered visual as a box, then (1) DE-OVERLAP - repeatedly resolve the worst slicer-on-slicer
    /// collision with the smallest clean nudge that clears it (preferring to keep the slicer near its spot, on-canvas,
    /// and without creating a NEW overlap), and (2) SNAP-ALIGN - snap a slicer whose left or top edge sits within a
    /// few px of a shared edge onto that edge. CONSERVATIVE: only SLICERS ever move, sizes are never changed, and a
    /// snap/nudge is rejected if it would create fresh overlap. Slicer-over-non-slicer overlaps are FLAGGED (see the
    /// deliberate date-label-card layering) unless <paramref name="deOverlapNonSlicers"/> is set. Returns
    /// { ok, pagesTidied, moves:[{page,slicer,reason,before{x,y,w,h},after{x,y,w,h}}], flagged:[...], summary }.</summary>
    public object TidySlicerLayout(string reportSessionId, string? page = null, bool deOverlapNonSlicers = false)
    {
        var root = _sessions.GetReport(reportSessionId).Layout.Root;

        var targets = new List<JsonObject>();
        if (!string.IsNullOrWhiteSpace(page))
            targets.Add(FindSectionLoose(root, page!));
        else
            foreach (var s in Sections(root)) if (s is JsonObject so) targets.Add(so);

        var moves = new List<object>();
        var flagged = new List<object>();
        int pagesTidied = 0;

        foreach (var section in targets)
        {
            string pageName = (string?)section["displayName"] ?? (string?)section["name"] ?? "(unnamed)";
            var (pageMoves, pageFlagged) = TidyOnePage(reportSessionId, section, pageName, deOverlapNonSlicers);
            if (pageMoves.Count > 0) pagesTidied++;
            moves.AddRange(pageMoves);
            flagged.AddRange(pageFlagged);
        }

        return new
        {
            ok = true,
            pagesScanned = targets.Count,
            pagesTidied,
            moveCount = moves.Count,
            moves,
            flaggedCount = flagged.Count,
            flagged,
            note = flagged.Count > 0 && !deOverlapNonSlicers
                ? "Slicer-over-other-visual overlaps are FLAGGED, not moved (often a deliberate header layering, e.g. a State slicer over a wide date-label card). Re-run with deOverlapNonSlicers=true to nudge those too."
                : null,
        };
    }

    /// <summary>Tidy a single page section. Returns (moves, flagged). Pure geometry on TidyBox doubles; the layout
    /// is only rewritten for the slicers that actually shifted, via SetVisualBounds (positions only, both the
    /// container x/y and the layouts[0].position mirror).</summary>
    private (List<object> moves, List<object> flagged) TidyOnePage(
        string reportSessionId, JsonObject section, string pageName, bool deOverlapNonSlicers)
    {
        const double OverlapMin = 1.0;   // ignore sub-pixel touches; fix overlaps >= 1px deep
        const double Gap        = 2.0;   // clearance left between boxes after a de-overlap nudge
        const double SnapTol    = 3.0;   // an edge within this of a shared edge is snapped onto it
        const double MaxDisp    = 80.0;  // never move a slicer more than this from its original spot
        const int    MaxIters   = 500;   // hard guard against pathological thrash

        double canvasW = Num(section["width"]) ?? 1280.0;
        double canvasH = Num(section["height"]) ?? 720.0;

        var moves = new List<object>();
        var flagged = new List<object>();

        // --- read every RENDERED visual as a box (skip hidden / malformed) --------------------------
        var boxes = new List<TidyBox>();
        foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;
            if ((string?)sv["display"]?["mode"] == "hidden") continue;

            // prefer layouts[0].position (the authored geometry), fall back to the container fields
            double x, y, w, h;
            if ((co["layouts"] as JsonArray)?.FirstOrDefault() is JsonObject l0 && l0["position"] is JsonObject p)
            { x = Num(p["x"]) ?? 0; y = Num(p["y"]) ?? 0; w = Num(p["width"]) ?? 0; h = Num(p["height"]) ?? 0; }
            else
            { x = Num(vc["x"]) ?? 0; y = Num(vc["y"]) ?? 0; w = Num(vc["width"]) ?? 0; h = Num(vc["height"]) ?? 0; }
            if (w <= 0 || h <= 0) continue;

            string? name = (string?)co["name"];
            bool isSlicer = string.Equals((string?)sv["visualType"], "slicer", StringComparison.OrdinalIgnoreCase);
            var (t, f) = TryBoundField(sv);
            string label = TitleText(sv) ?? (t != null && f != null ? $"{t}[{f}]" : null) ?? name ?? "(unnamed)";

            boxes.Add(new TidyBox
            {
                Name = name ?? "", Label = label,
                IsSlicer = isSlicer && !string.IsNullOrEmpty(name),   // only a NAMED slicer is movable
                X = x, Y = y, W = w, H = h, OrigX = x, OrigY = y,
            });
        }

        // --- (1) DE-OVERLAP --------------------------------------------------------------------------
        var skip = new HashSet<(int, int)>();
        for (int iter = 0; iter < MaxIters; iter++)
        {
            // find the worst still-overlapping pair where at least one box is a movable slicer
            int bi = -1, bj = -1; double worst = OverlapMin;
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    if (skip.Contains((i, j))) continue;
                    var a = boxes[i]; var b = boxes[j];
                    if (!a.IsSlicer && !b.IsSlicer) continue;
                    // slicer-vs-non-slicer only participates when opted in
                    if ((!a.IsSlicer || !b.IsSlicer) && !deOverlapNonSlicers) continue;
                    double pen = Penetration(a, b);
                    if (pen >= worst) { worst = pen; bi = i; bj = j; }
                }
            if (bi < 0) break;

            if (!ResolveOverlap(boxes, bi, bj, canvasW, canvasH, Gap, MaxDisp, OverlapMin))
                skip.Add((bi, bj));   // unresolvable within budget - leave it, stop looping on it
        }

        // --- flag any RESIDUAL slicer-on-slicer overlap we could not auto-resolve within budget ------
        for (int i = 0; i < boxes.Count; i++)
            for (int j = i + 1; j < boxes.Count; j++)
            {
                var a = boxes[i]; var b = boxes[j];
                if (!a.IsSlicer || !b.IsSlicer) continue;
                double pen = Penetration(a, b);
                if (pen < OverlapMin) continue;
                flagged.Add(new
                {
                    page = pageName, slicer = a.Label, over = b.Label,
                    overlapPx = Round2(pen),
                    note = "slicer-on-slicer overlap could NOT be cleared by a single conservative nudge (the row is packed); review manually.",
                });
            }

        // --- flag slicer-over-non-slicer overlaps we did NOT auto-move (default) ---------------------
        if (!deOverlapNonSlicers)
            for (int i = 0; i < boxes.Count; i++)
                for (int j = i + 1; j < boxes.Count; j++)
                {
                    var a = boxes[i]; var b = boxes[j];
                    if (a.IsSlicer == b.IsSlicer) continue;         // exactly one is a slicer
                    double pen = Penetration(a, b);
                    if (pen < 2.0) continue;                        // only visible overlaps
                    var slc = a.IsSlicer ? a : b; var oth = a.IsSlicer ? b : a;
                    flagged.Add(new
                    {
                        page = pageName, slicer = slc.Label, over = oth.Label,
                        overlapPx = Round2(pen),
                        note = "slicer overlaps a non-slicer visual; left in place (often deliberate layering). Review, or re-run with deOverlapNonSlicers=true.",
                    });
                }

        // --- (2) SNAP-ALIGN (positions only: left edges on X, top edges on Y) ------------------------
        SnapEdges(boxes, isX: true,  SnapTol, OverlapMin, canvasW, canvasH);
        SnapEdges(boxes, isX: false, SnapTol, OverlapMin, canvasW, canvasH);

        // --- apply: rewrite ONLY the slicers that actually moved -------------------------------------
        foreach (var box in boxes)
        {
            if (!box.IsSlicer) continue;
            bool moved = Math.Abs(box.X - box.OrigX) > 0.01 || Math.Abs(box.Y - box.OrigY) > 0.01;
            if (!moved) continue;
            SetVisualBounds(reportSessionId, pageName, box.Name, box.X, box.Y, null, null, null);
            moves.Add(new
            {
                page = pageName, slicer = box.Label, reason = box.Reason ?? "de-overlap",
                before = new { x = Round2(box.OrigX), y = Round2(box.OrigY), w = Round2(box.W), h = Round2(box.H) },
                after  = new { x = Round2(box.X),     y = Round2(box.Y),     w = Round2(box.W), h = Round2(box.H) },
            });
        }
        return (moves, flagged);
    }

    /// <summary>Resolve one overlapping pair by moving whichever SLICER can clear it with the smallest, cleanest
    /// nudge: candidates are push-right / push-left / push-down / push-up of each movable box to just clear the
    /// partner (plus a small gap). A candidate is valid only if it stays on-canvas and its total displacement from
    /// the slicer's original spot is within budget; among valid candidates the one that creates the least NEW
    /// overlap (then the smallest move) wins. Returns false when nothing can clear it within budget.</summary>
    private static bool ResolveOverlap(List<TidyBox> boxes, int i, int j,
        double canvasW, double canvasH, double gap, double maxDisp, double overlapMin)
    {
        var a = boxes[i]; var b = boxes[j];

        // candidate = (box to move, new X, new Y)
        var cands = new List<(TidyBox box, double nx, double ny)>();
        void AddMoves(TidyBox mv, TidyBox other)
        {
            if (!mv.IsSlicer) return;
            cands.Add((mv, other.Right + gap, mv.Y));           // push right of other
            cands.Add((mv, other.X - gap - mv.W, mv.Y));        // push left of other
            cands.Add((mv, mv.X, other.Bottom + gap));          // push down below other
            cands.Add((mv, mv.X, other.Y - gap - mv.H));        // push up above other
        }
        AddMoves(a, b);
        AddMoves(b, a);

        (TidyBox box, double nx, double ny)? best = null;
        double bestScore = double.MaxValue;
        foreach (var c in cands)
        {
            // on-canvas (a small slack tolerates geometry that was already flush to an edge)
            if (c.nx < -1 || c.ny < -1 || c.nx + c.box.W > canvasW + 1 || c.ny + c.box.H > canvasH + 1) continue;
            // within displacement budget from the ORIGINAL spot
            double disp = Math.Sqrt((c.nx - c.box.OrigX) * (c.nx - c.box.OrigX) + (c.ny - c.box.OrigY) * (c.ny - c.box.OrigY));
            if (disp > maxDisp) continue;

            // NEW overlap this move would create against every OTHER box (pre-existing overlap is not charged,
            // so a deliberate slicer-over-card layering never blocks the fix)
            double newPen = 0;
            foreach (var k in boxes)
            {
                if (ReferenceEquals(k, c.box)) continue;
                var moved = new TidyBox { X = c.nx, Y = c.ny, W = c.box.W, H = c.box.H };
                double before = Penetration(c.box, k);
                double after = Penetration(moved, k);
                if (after > before + 0.01) newPen += after - before;
            }
            double moveDist = Math.Abs(c.nx - c.box.X) + Math.Abs(c.ny - c.box.Y);
            double score = newPen * 1000 + moveDist;   // strongly prefer no new overlap, then the smallest move
            if (score < bestScore) { bestScore = score; best = c; }
        }

        if (best is null) return false;
        var pick = best.Value;
        // if the only option still leaves a real overlap with the partner, treat as unresolved
        var probe = new TidyBox { X = pick.nx, Y = pick.ny, W = pick.box.W, H = pick.box.H };
        var partner = ReferenceEquals(pick.box, a) ? b : a;
        if (Penetration(probe, partner) >= overlapMin) return false;

        pick.box.X = pick.nx; pick.box.Y = pick.ny;
        pick.box.AddReason("de-overlap");
        return true;
    }

    /// <summary>Snap slicer left edges (isX) or top edges (!isX) that sit within <paramref name="snapTol"/> of one
    /// another onto a single shared edge - the roundest member of each tight cluster. A snap is applied only when it
    /// does not push the box off-canvas and does not increase that box's overlap with any other box (so a deliberate
    /// pre-existing layering is preserved). Positions only; widths/heights are never touched.</summary>
    private static void SnapEdges(List<TidyBox> boxes, bool isX, double snapTol, double overlapMin, double canvasW, double canvasH)
    {
        var slicers = boxes.Where(b => b.IsSlicer).ToList();
        if (slicers.Count < 2) return;

        // greedy 1-D clustering of the chosen edge over the sorted values
        var ordered = slicers.OrderBy(b => isX ? b.X : b.Y).ToList();
        int n = ordered.Count, idx = 0;
        while (idx < n)
        {
            int start = idx;
            double min = isX ? ordered[idx].X : ordered[idx].Y;
            idx++;
            while (idx < n)
            {
                double v = isX ? ordered[idx].X : ordered[idx].Y;
                if (v - min > snapTol) break;   // cluster stays within a snapTol-wide band
                idx++;
            }
            int count = idx - start;
            if (count < 2) continue;

            var cluster = ordered.GetRange(start, count);
            // representative = the roundest edge in the cluster (closest to an integer), tie -> smallest
            double rep = cluster
                .Select(b => isX ? b.X : b.Y)
                .OrderBy(v => Math.Abs(v - Math.Round(v)))
                .ThenBy(v => v)
                .First();

            foreach (var b in cluster)
            {
                double cur = isX ? b.X : b.Y;
                if (Math.Abs(cur - rep) <= 0.01 || Math.Abs(cur - rep) > snapTol) continue;
                double nx = isX ? rep : b.X, ny = isX ? b.Y : rep;
                if (nx < -1 || ny < -1 || nx + b.W > canvasW + 1 || ny + b.H > canvasH + 1) continue;

                // reject if the snap would deepen this box's overlap with any other box
                var moved = new TidyBox { X = nx, Y = ny, W = b.W, H = b.H };
                bool worsens = false;
                foreach (var k in boxes)
                {
                    if (ReferenceEquals(k, b)) continue;
                    if (Penetration(moved, k) > Penetration(b, k) + 0.01 && Penetration(moved, k) >= overlapMin) { worsens = true; break; }
                }
                if (worsens) continue;

                b.X = nx; b.Y = ny;
                b.AddReason("snap-align");
            }
        }
    }

    private static double Round2(double v) => Math.Round(v, 2);

    // ============================================================================================
    //  MATCH SLICER LAYOUT + TIDY SLICER LAYOUT V2 - the ROW-STRUCTURE slicer tools.
    //  match_slicer_layout LEARNS the intended slicer row structure (row count, per-row y, height,
    //  left start, horizontal gap) from hand-fixed REFERENCE pages using medians (robust to reference
    //  noise: a slicer nudged to x=-3, a top row whose y drifts a few px between pages), then re-lays
    //  the slicers on TARGET pages onto that structure. tidy_slicer_layout_v2 is the row-aware tidy:
    //  cluster a page's slicers into rows, align each row onto one shared baseline y, pull off-canvas
    //  slicers back on, and clear within-row overlaps. BOTH move ONLY slicers - non-slicer visuals are
    //  never touched - and both save offline with the DataModel byte-preserved.
    // ============================================================================================

    /// <summary>One learned slicer row: canonical top y, height, left start x and horizontal gap - each the
    /// MEDIAN over every reference sample so a single noisy slicer cannot skew the structure.</summary>
    private sealed class LearnedSlicerRow
    {
        public double Y, H, Left, Gap;
        public int Samples;
    }

    /// <summary>Median of a list (average of the middle pair when even). 0 for an empty list.</summary>
    private static double MedianOf(List<double> values)
    {
        if (values.Count == 0) return 0;
        var s = values.OrderBy(v => v).ToList();
        int n = s.Count;
        return n % 2 == 1 ? s[n / 2] : (s[n / 2 - 1] + s[n / 2]) / 2.0;
    }

    /// <summary>Read every rendered visual on a page as a TidyBox. Unlike the v1 tidy reader, IsSlicer here
    /// means "IS a slicer" regardless of name (unnamed slicers still inform structure learning); whether a
    /// box is movable is a separate Name != "" check at the call sites.</summary>
    private static List<TidyBox> ReadPageBoxes(JsonObject section)
    {
        var boxes = new List<TidyBox>();
        foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;
            if ((string?)sv["display"]?["mode"] == "hidden") continue;

            double x, y, w, h;
            if ((co["layouts"] as JsonArray)?.FirstOrDefault() is JsonObject l0 && l0["position"] is JsonObject p)
            { x = Num(p["x"]) ?? 0; y = Num(p["y"]) ?? 0; w = Num(p["width"]) ?? 0; h = Num(p["height"]) ?? 0; }
            else
            { x = Num(vc["x"]) ?? 0; y = Num(vc["y"]) ?? 0; w = Num(vc["width"]) ?? 0; h = Num(vc["height"]) ?? 0; }
            if (w <= 0 || h <= 0) continue;

            string? name = (string?)co["name"];
            var (t, f) = TryBoundField(sv);
            string label = TitleText(sv) ?? (t != null && f != null ? $"{t}[{f}]" : null) ?? name ?? "(unnamed)";
            boxes.Add(new TidyBox
            {
                Name = name ?? "", Label = label,
                IsSlicer = string.Equals((string?)sv["visualType"], "slicer", StringComparison.OrdinalIgnoreCase),
                X = x, Y = y, W = w, H = h, OrigX = x, OrigY = y,
            });
        }
        return boxes;
    }

    /// <summary>Greedy 1-D clustering of slicers into ROWS: sorted by top y, a slicer joins the current row
    /// while its y is within <paramref name="rowTol"/> of the row's topmost member; each row is returned
    /// sorted left-to-right. Rows come back top-to-bottom.</summary>
    private static List<List<TidyBox>> ClusterSlicerRows(IEnumerable<TidyBox> slicers, double rowTol)
    {
        var rows = new List<List<TidyBox>>();
        foreach (var b in slicers.OrderBy(b => b.Y).ThenBy(b => b.X))
        {
            if (rows.Count > 0 && b.Y - rows[^1].Min(m => m.Y) <= rowTol) rows[^1].Add(b);
            else rows.Add(new List<TidyBox> { b });
        }
        foreach (var r in rows) r.Sort((a, b) => a.X.CompareTo(b.X));
        return rows;
    }

    /// <summary>Resolve a comma-separated page list: each token is an ORDINAL (matched against
    /// section.ordinal, falling back to positional index) or a page name/displayName (exact, then
    /// case-insensitive, then contains). Duplicates collapse; an unresolvable token throws.</summary>
    private static List<JsonObject> ResolvePageList(List<JsonObject> all, string list)
    {
        var result = new List<JsonObject>();
        foreach (var raw in list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            JsonObject? sec = null;
            if (int.TryParse(raw, out int ord))
                sec = all.FirstOrDefault(s => Num(s["ordinal"]) is double d && (int)d == ord)
                      ?? (ord >= 0 && ord < all.Count ? all[ord] : null);
            sec ??= all.FirstOrDefault(s => (string?)s["name"] == raw || (string?)s["displayName"] == raw);
            sec ??= all.FirstOrDefault(s => string.Equals((string?)s["displayName"], raw, StringComparison.OrdinalIgnoreCase));
            sec ??= all.FirstOrDefault(s => ((string?)s["displayName"])?.Contains(raw, StringComparison.OrdinalIgnoreCase) == true);
            if (sec is null) throw new InvalidOperationException($"Page '{raw}' not found (by ordinal, name, or displayName).");
            if (!result.Contains(sec)) result.Add(sec);
        }
        return result;
    }

    /// <summary>match_slicer_layout (ONE-SHOT): open a CLOSED .pbix, learn the slicer row structure from the
    /// reference pages, re-lay the target pages' slicers onto it, save back (DataModel byte-preserved).</summary>
    public object MatchSlicerLayoutOffline(string pbixPath, string referencePages, string? targetPages = null)
    {
        var session = LoadReportSession(pbixPath);
        try
        {
            var result = MatchSlicerLayout(session.Id, referencePages, targetPages);
            bool changed = session.Dirty;
            if (changed) Save(session.Id);
            var node = JsonSerializer.SerializeToNode(result) as JsonObject ?? new JsonObject();
            node["pbixPath"] = pbixPath;
            node["persistedToDisk"] = changed;
            if (!changed) node["note"] = "Target pages already matched the learned structure; the .pbix was left byte-for-byte unchanged.";
            return node;
        }
        finally { _sessions.RemoveReport(session.Id); }
    }

    /// <summary>
    /// LEARN the slicer ROW STRUCTURE from <paramref name="referencePages"/> and APPLY it to
    /// <paramref name="targetPages"/> (null/empty/"all" = every OTHER slicer-bearing page).
    /// Learning is median-based per row index across all reference pages: row y, row height, left start
    /// (clamped on-canvas, so a reference slicer nudged to x=-3 cannot teach an off-canvas start) and the
    /// horizontal gap between neighbours (clamped >= 0, so accidental reference overlaps teach a flush
    /// layout, not an overlapping one). Rows beyond the learned count are extrapolated by the learned row
    /// pitch. Applying re-packs each target row left-to-right in the slicers' existing order at the learned
    /// left/gap, sets the learned row y and height, and preserves widths (they only shrink, proportionally,
    /// when a row cannot physically fit the canvas). ONLY slicers move; non-slicer visuals are untouched.
    /// Returns { ok, learned{rows,rowPitch,...}, pages:[{page,rowsDetected,moves:[{slicer,row,before,after}]}] }.
    /// </summary>
    public object MatchSlicerLayout(string reportSessionId, string referencePages, string? targetPages = null)
    {
        const double RowTol = 30.0;   // slicers whose tops are within this band form one row
        const double RowGapMin = 2.0; // minimum vertical clearance kept between stacked rows

        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var all = Sections(root).OfType<JsonObject>().ToList();

        var refs = ResolvePageList(all, referencePages);
        if (refs.Count == 0) throw new ArgumentException("reference_pages resolved to no pages.");

        List<JsonObject> targets;
        string t = (targetPages ?? "").Trim();
        if (t.Length == 0 || t == "*" || string.Equals(t, "all", StringComparison.OrdinalIgnoreCase))
            targets = all.Where(s => !refs.Contains(s) && ReadPageBoxes(s).Any(b => b.IsSlicer)).ToList();
        else
            targets = ResolvePageList(all, t).Where(s => !refs.Contains(s)).ToList();

        // ---- LEARN: aggregate row samples across every reference page ---------------------------
        var samples = new List<(List<double> ys, List<double> hs, List<double> lefts, List<double> gaps)>();
        foreach (var sec in refs)
        {
            var rows = ClusterSlicerRows(ReadPageBoxes(sec).Where(b => b.IsSlicer), RowTol);
            for (int i = 0; i < rows.Count; i++)
            {
                while (samples.Count <= i)
                    samples.Add((new List<double>(), new List<double>(), new List<double>(), new List<double>()));
                var s = samples[i];
                s.ys.AddRange(rows[i].Select(b => b.Y));
                s.hs.AddRange(rows[i].Select(b => b.H));
                s.lefts.Add(Math.Max(0, rows[i].Min(b => b.X)));           // clamp: never learn an off-canvas start
                for (int k = 1; k < rows[i].Count; k++)
                    s.gaps.Add(Math.Max(0, rows[i][k].X - rows[i][k - 1].Right));   // clamp: never learn an overlap
            }
        }
        if (samples.Count == 0) throw new InvalidOperationException("The reference pages carry no slicers to learn from.");

        var learned = samples.Select(s => new LearnedSlicerRow
        {
            Y = MedianOf(s.ys), H = MedianOf(s.hs), Left = MedianOf(s.lefts),
            Gap = s.gaps.Count > 0 ? MedianOf(s.gaps) : 8.0, Samples = s.ys.Count,
        }).ToList();
        // learned rows must stack cleanly - a later row may never start above the previous row's bottom
        for (int i = 1; i < learned.Count; i++)
            learned[i].Y = Math.Max(learned[i].Y, learned[i - 1].Y + learned[i - 1].H + RowGapMin);
        double rowPitch = learned.Count >= 2 ? learned[1].Y - learned[0].Y : learned[0].H + 12.0;

        // ---- APPLY: re-lay each target page's slicer rows onto the learned structure -------------
        var pagesOut = new List<object>();
        int totalMoves = 0;
        foreach (var sec in targets)
        {
            string pageName = (string?)sec["displayName"] ?? (string?)sec["name"] ?? "(unnamed)";
            double canvasW = Num(sec["width"]) ?? 1280.0, canvasH = Num(sec["height"]) ?? 720.0;
            var movable = ReadPageBoxes(sec).Where(b => b.IsSlicer && b.Name.Length > 0).ToList();
            var rows = ClusterSlicerRows(movable, RowTol);

            var pageMoves = new List<object>();
            double prevBottom = double.MinValue;
            for (int i = 0; i < rows.Count; i++)
            {
                var l = learned[Math.Min(i, learned.Count - 1)];
                double rowY = i < learned.Count ? l.Y : learned[^1].Y + (i - learned.Count + 1) * rowPitch;
                rowY = Math.Max(rowY, prevBottom + RowGapMin);
                double rowH = l.H;
                if (rowY + rowH > canvasH) rowY = Math.Max(0, canvasH - rowH);

                var members = rows[i];   // already left-to-right; existing order is preserved
                double sumW = members.Sum(b => b.W);
                double gap = l.Gap, scale = 1.0;
                if (l.Left + sumW + gap * (members.Count - 1) > canvasW)
                {
                    gap = members.Count > 1 ? Math.Max(0, (canvasW - l.Left - sumW) / (members.Count - 1)) : 0;
                    if (l.Left + sumW > canvasW) { scale = (canvasW - l.Left) / sumW; gap = 0; }
                }

                double cursor = l.Left;
                foreach (var b in members)
                {
                    double nw = Round2(b.W * scale), nh = Round2(rowH);
                    double nx = Round2(cursor), ny = Round2(rowY);
                    bool moved = Math.Abs(nx - b.X) > 0.01 || Math.Abs(ny - b.Y) > 0.01
                              || Math.Abs(nw - b.W) > 0.01 || Math.Abs(nh - b.H) > 0.01;
                    if (moved)
                    {
                        SetVisualBounds(reportSessionId, pageName, b.Name, nx, ny, null,
                            Math.Abs(nw - b.W) > 0.01 ? nw : null,
                            Math.Abs(nh - b.H) > 0.01 ? nh : null);
                        pageMoves.Add(new
                        {
                            slicer = b.Label, row = i + 1,
                            before = new { x = Round2(b.X), y = Round2(b.Y), w = Round2(b.W), h = Round2(b.H) },
                            after  = new { x = nx, y = ny, w = nw, h = nh },
                        });
                    }
                    cursor += nw + gap;
                }
                prevBottom = rowY + rowH;
            }
            totalMoves += pageMoves.Count;
            pagesOut.Add(new
            {
                page = pageName, rowsDetected = rows.Count, slicerCount = movable.Count,
                moveCount = pageMoves.Count, moves = pageMoves,
            });
        }

        return new
        {
            ok = true,
            learned = new
            {
                referencePages = refs.Select(s => (string?)s["displayName"] ?? (string?)s["name"] ?? "(unnamed)").ToArray(),
                rowTolerance = RowTol,
                rows = learned.Select((r, i) => new
                {
                    row = i + 1, y = Round2(r.Y), height = Round2(r.H),
                    leftStart = Round2(r.Left), gap = Round2(r.Gap), samples = r.Samples,
                }).ToArray(),
                rowPitch = Round2(rowPitch),
            },
            pagesMatched = pagesOut.Count,
            totalMoves,
            pages = pagesOut,
            note = "Structure INFERRED (medians per row across the reference pages), not copied pixel-for-pixel. Slicers only: non-slicer visuals were not touched.",
        };
    }

    /// <summary>tidy_slicer_layout_v2 (ONE-SHOT): open a CLOSED .pbix, row-align its slicers, save back
    /// (DataModel byte-preserved).</summary>
    public object TidySlicerLayoutV2Offline(string pbixPath, string? page = null)
    {
        var session = LoadReportSession(pbixPath);
        try
        {
            var result = TidySlicerLayoutV2(session.Id, page);
            bool changed = session.Dirty;
            if (changed) Save(session.Id);
            var node = JsonSerializer.SerializeToNode(result) as JsonObject ?? new JsonObject();
            node["pbixPath"] = pbixPath;
            node["persistedToDisk"] = changed;
            if (!changed) node["note"] = "No slicer moves were needed; the .pbix was left byte-for-byte unchanged.";
            return node;
        }
        finally { _sessions.RemoveReport(session.Id); }
    }

    /// <summary>
    /// ROW-AWARE slicer tidy (v2). For one page (or every page when <paramref name="page"/> is null):
    /// cluster the slicers into ROWS, then per row (1) ROW-ALIGN - every member's y snaps to the row's
    /// median baseline, (2) ON-CANVAS - a row starting off the left edge is pulled back on (and pulled left
    /// when it hangs off the right edge, as far as space allows), and (3) EVEN-GAP - members are re-packed
    /// left-to-right from the row's left start with ONE uniform gap (the median of the row's existing
    /// positive gaps, shrunk only as needed to keep the row on-canvas), which also clears any within-row
    /// overlap. POSITIONS ONLY: sizes never change, non-slicer visuals never move, and a row that physically
    /// cannot fit the canvas even flush is FLAGGED (use match_slicer_layout to re-pack it against learned
    /// structure instead). Returns { ok, rows:[{page,row,y,members,aligned}],
    /// moves:[{page,slicer,reason,before,after}], flagged }.
    /// </summary>
    public object TidySlicerLayoutV2(string reportSessionId, string? page = null)
    {
        const double RowTol = 30.0;   // slicers whose tops are within this band form one row
        const double Eps = 0.01;

        var root = _sessions.GetReport(reportSessionId).Layout.Root;
        var targets = new List<JsonObject>();
        if (!string.IsNullOrWhiteSpace(page)) targets.Add(FindSectionLoose(root, page!));
        else targets.AddRange(Sections(root).OfType<JsonObject>());

        var rowsOut = new List<object>();
        var moves = new List<object>();
        var flagged = new List<object>();

        foreach (var sec in targets)
        {
            string pageName = (string?)sec["displayName"] ?? (string?)sec["name"] ?? "(unnamed)";
            double canvasW = Num(sec["width"]) ?? 1280.0;
            var movable = ReadPageBoxes(sec).Where(b => b.IsSlicer && b.Name.Length > 0).ToList();
            var rows = ClusterSlicerRows(movable, RowTol);

            for (int i = 0; i < rows.Count; i++)
            {
                var members = rows[i];   // ClusterSlicerRows already sorted them left-to-right
                double baseline = Round2(MedianOf(members.Select(b => b.Y).ToList()));

                // -- keep-on-canvas clamp for the row's LEFT START (fixes the x=-3.3 escapee) ------
                double leftStart = Math.Max(0, members[0].X);

                // -- even-gap spacing: one uniform gap per row = median of the EXISTING positive
                //    gaps (an overlapping pair contributes 0, an off-canvas start cannot skew it),
                //    shrunk only as far as needed to keep the whole row on the canvas ---------------
                var gapsNow = new List<double>();
                for (int k = 1; k < members.Count; k++)
                    gapsNow.Add(Math.Max(0, members[k].X - members[k - 1].Right));
                double gap = gapsNow.Count > 0 ? MedianOf(gapsNow) : 0;

                double sumW = members.Sum(b => b.W);
                double span = sumW + gap * (members.Count - 1);
                if (leftStart + span > canvasW)
                {
                    // first pull the row left, then squeeze the gap; NEVER resize
                    leftStart = Math.Max(0, canvasW - span);
                    if (leftStart + span > canvasW && members.Count > 1)
                        gap = Math.Max(0, (canvasW - leftStart - sumW) / (members.Count - 1));
                    if (sumW > canvasW + Eps)
                        flagged.Add(new
                        {
                            page = pageName, row = i + 1,
                            slicers = members.Select(b => b.Label).ToArray(),
                            overflowPx = Round2(sumW - canvasW),
                            note = "row is wider than the canvas even packed flush; overlap cannot be cleared without resizing. Use match_slicer_layout to re-pack this row against learned structure.",
                        });
                }

                int aligned = 0;
                double cursor = leftStart;
                foreach (var b in members)
                {
                    double nx = Round2(cursor), ny = baseline;
                    var reasons = new List<string>();
                    if (Math.Abs(ny - b.Y) > Eps) reasons.Add("row-align");
                    if (b.X < -Eps || b.X + b.W > canvasW + Eps) reasons.Add("on-canvas");
                    if (Math.Abs(nx - b.X) > Eps && !reasons.Contains("on-canvas")) reasons.Add("even-gap");
                    if (Math.Abs(nx - b.X) > Eps || Math.Abs(ny - b.Y) > Eps)
                    {
                        SetVisualBounds(reportSessionId, pageName, b.Name, nx, ny, null, null, null);
                        moves.Add(new
                        {
                            page = pageName, slicer = b.Label, row = i + 1,
                            reason = string.Join(" + ", reasons.Distinct()),
                            before = new { x = Round2(b.X), y = Round2(b.Y), w = Round2(b.W), h = Round2(b.H) },
                            after  = new { x = nx, y = ny, w = Round2(b.W), h = Round2(b.H) },
                        });
                        aligned++;
                        b.X = nx; b.Y = ny;
                    }
                    cursor += b.W + gap;
                }
                rowsOut.Add(new
                {
                    page = pageName, row = i + 1, y = baseline,
                    members = members.Select(b => b.Label).ToArray(), aligned,
                });
            }
        }

        return new
        {
            ok = true,
            pagesScanned = targets.Count,
            rowCount = rowsOut.Count,
            rows = rowsOut,
            moveCount = moves.Count,
            moves,
            flaggedCount = flagged.Count,
            flagged,
            note = "Row-aware tidy: positions only (no resizes), slicers only. Rows are clustered by top-y proximity; each row top-aligns to its median baseline, is clamped on-canvas, and is re-packed with one even gap (the median of its existing gaps).",
        };
    }

    /// <summary>
    /// GENERAL report-layer visual-config editor (session form): locate the visual on the page (by config name,
    /// displayName, its visible TITLE, or a bound field) and set ANY property at a JSON path UNDER its
    /// singleVisual config. Intermediate objects/arrays on the path are created as needed. valueKind controls how
    /// <paramref name="value"/> is interpreted:
    ///   auto (default) - JSON if it parses (object/array/bool/number/null), otherwise a plain string;
    ///   raw            - same as auto; json - strict JSON parse; string - always a string literal;
    ///   literal/bool/number/color/text - encode as a Power BI FORMATTING literal expr ({expr:{Literal:{Value}}}
    ///                    or {solid:{color:...}}), so a formatting-card property can be set by path.
    /// Returns the before/after value at that path. This is the general "edit a report visual" capability the
    /// typed setters (set_slicer_selection, set_visual_display_mode, ...) build on.
    /// </summary>
    public object UpdateVisualProperty(string reportSessionId, string page, string visualRef,
        string propertyPath, string value, string valueKind = "auto")
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisualFlexible(section, visualRef, matchBoundField: true);

        var segs = ParsePath(propertyPath);
        var leaf = segs[^1];
        var newVal = BuildUpdateValue(value, valueKind, leaf.isIndex ? "" : leaf.key);
        var before = SetAtPath(sv, segs, newVal);

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;
        return new
        {
            ok = true, page, visual = (string?)co["name"], visualType = (string?)sv["visualType"],
            propertyPath, valueKind,
            before = before?.ToJsonString(JsonOpts) ?? "(absent)",
            after = newVal?.ToJsonString(JsonOpts) ?? "null",
        };
    }

    /// <summary>
    /// TARGETED slicer fix (session form): make a slicer SINGLE-SELECT and, optionally, pre-select a default
    /// value. Single-select is written as the ground-truth <c>objects.selection[0].properties.strictSingleSelect
    /// = true</c> - the Power BI "Single select" toggle, which forces exactly one item AND auto-picks the first
    /// available option when none is selected. That alone RESOLVES a field-parameter axis (the flat-lines bug):
    /// a field-parameter-bound line chart only renders when its slicer is filtered to ONE option.
    /// When <paramref name="defaultValue"/> is given, a Categorical "is one of [value]" filter is written into
    /// the slicer's own container filters (a slicer's saved selection) so it opens on that specific value; if the
    /// bound field cannot be resolved, single-select still applies and the default is reported as auto-picked.
    /// </summary>
    public object SetSlicerSelection(string reportSessionId, string page, string slicerRef,
        bool singleSelect = true, string? defaultValue = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSection(session.Layout.Root, page);
        var (vc, co, sv) = ResolveVisualFlexible(section, slicerRef, matchBoundField: true);

        string? vtype = (string?)sv["visualType"];
        if (!string.Equals(vtype, "slicer", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"'{slicerRef}' resolved to a '{vtype}', not a slicer - set_slicer_selection only applies to slicer visuals.");

        var (table, field) = TryBoundField(sv);

        // capture BEFORE
        string beforeSelection = (sv["objects"] as JsonObject)?["selection"]?.ToJsonString(JsonOpts) ?? "(none)";
        string beforeFilters = (string?)vc["filters"] ?? "[]";

        // 1) single-select mode -> strictSingleSelect (forces one item, auto-picks the first if none).
        bool modeSet = false;
        if (singleSelect)
        {
            var objects = sv["objects"] as JsonObject;
            if (objects is null) { objects = new JsonObject(); sv["objects"] = objects; }
            MergeProperty(objects, "selection", "strictSingleSelect", JsonValue.Create(true));
            modeSet = true;
        }

        // 2) default selection -> a Categorical selection filter in the slicer's OWN container filters.
        bool defaultApplied = false;
        string? defaultNote = null;
        if (!string.IsNullOrWhiteSpace(defaultValue))
        {
            if (table is null || field is null)
            {
                defaultNote = "the slicer's bound field could not be resolved, so no explicit default filter was written; "
                            + "strictSingleSelect will still auto-pick the first available option on open.";
            }
            else
            {
                var filters = JsonNode.Parse((string?)vc["filters"] ?? "[]") as JsonArray ?? new JsonArray();
                // drop any prior Categorical selection on the SAME field so we don't stack duplicates
                for (int i = filters.Count - 1; i >= 0; i--)
                    if (filters[i] is JsonObject ef && (string?)ef["type"] == "Categorical"
                        && (string?)ef["expression"]?["Column"]?["Property"] == field)
                        filters.RemoveAt(i);
                filters.Add(BuildCategoricalFilter(table, field, new[] { defaultValue! }, "string"));
                vc["filters"] = filters.ToJsonString(JsonOpts);
                defaultApplied = true;
                defaultNote = "written as a Categorical selection filter in the slicer's container; strictSingleSelect also "
                            + "guarantees a single active option on open. Confirm the opening value once in Desktop.";
            }
        }
        else if (singleSelect)
        {
            defaultNote = "no default_value given; strictSingleSelect auto-selects the FIRST available option on open.";
        }

        vc["config"] = co.ToJsonString(JsonOpts);
        session.Dirty = true;

        return new
        {
            ok = true, page, slicer = (string?)co["name"], title = TitleText(sv),
            boundField = (table is not null && field is not null) ? $"{table}[{field}]" : null,
            singleSelect = modeSet,
            singleSelectProperty = modeSet ? "objects.selection[0].properties.strictSingleSelect = true" : null,
            defaultValue,
            defaultApplied,
            defaultNote,
            before = new { selection = beforeSelection, filters = beforeFilters },
            after = new
            {
                selection = (sv["objects"] as JsonObject)?["selection"]?.ToJsonString(JsonOpts) ?? "(none)",
                filters = (string?)vc["filters"] ?? "[]",
            },
        };
    }

    /// <summary>
    /// ONE-CALL flat-line fix (session form): on <paramref name="page"/> (loose name match) find the slicer bound
    /// to a FIELD PARAMETER and make it single-select (strictSingleSelect=true) - the WHOLE fix for a
    /// field-parameter axis that flat-lines, with NO slicer name, no boolean-vs-string, and no multi-step required
    /// from the caller. Field-parameter slicers are detected purely from the report layer: a slicer whose bound
    /// (table, field) is an auto-named field parameter (table == field, the shape Power BI and add_field_parameter
    /// generate) OR whose single-column table's field is plotted as a chart axis on the same page. Target choice:
    /// when <paramref name="chart"/> is given AND resolves, prefer the slicer feeding THAT chart's field-parameter
    /// axis; else if exactly one field-parameter slicer, use it; else prefer the one feeding a field-parameter-axis
    /// chart that is NOT already single-select (the flat-liner); else throw, listing the page's slicers so the
    /// caller can pass <paramref name="chart"/>. <paramref name="defaultValue"/> is optional - strictSingleSelect
    /// already auto-picks the field parameter's first option (e.g. MonthYear) on open.
    /// </summary>
    public object FixSlicerSingleSelect(string reportSessionId, string page, string? chart = null, string? defaultValue = null)
    {
        var session = _sessions.GetReport(reportSessionId);
        var section = FindSectionLoose(session.Layout.Root, page);
        string pageDisplay = (string?)section["displayName"] ?? (string?)section["name"] ?? page;

        // scan the page once: slicers, the (table,field) columns plotted on NON-slicer visuals, and how many
        // distinct fields each table contributes (a field-parameter table is single-column).
        var slicers = new List<PageSlicer>();
        var plotted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);                 // "table.field" on non-slicer visuals
        var fieldsByTable = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var charts = new List<(JsonObject co, string? name, string? title, List<(string t, string f)> cols)>();

        foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;

            var cols = AllColumnFields(sv);
            foreach (var (t, f) in cols)
            {
                if (!fieldsByTable.TryGetValue(t, out var set)) { set = new(StringComparer.OrdinalIgnoreCase); fieldsByTable[t] = set; }
                set.Add(f);
            }

            if (Eq("slicer", (string?)sv["visualType"]))
            {
                var (t, f) = TryBoundField(sv);
                slicers.Add(new PageSlicer { Co = co, Sv = sv, Name = (string?)co["name"], Title = TitleText(sv),
                    Table = t, Field = f, AlreadySingle = SlicerHasSingleSelect(sv) });
            }
            else
            {
                foreach (var (t, f) in cols) plotted.Add(FpKey(t, f));
                charts.Add((co, (string?)co["name"], TitleText(sv), cols));
            }
        }

        bool IsFieldParamField(string? t, string? f) =>
            t is not null && f is not null &&
            (Eq(t, f) || (plotted.Contains(FpKey(t, f)) && fieldsByTable.TryGetValue(t, out var s) && s.Count == 1));

        foreach (var sl in slicers) sl.IsFieldParam = IsFieldParamField(sl.Table, sl.Field);
        var fp = slicers.Where(s => s.IsFieldParam).ToList();

        PageSlicer? target = null;
        string? resolvedChart = null;
        string reason = "";

        // (a) chart hint (BEST-EFFORT): if it resolves to a chart carrying a field-parameter axis, prefer the
        //     slicer feeding that axis. A hint that does not resolve is IGNORED (e.g. the caller passed the page
        //     name as the chart) so the tool still succeeds on the page-level rules below.
        if (!string.IsNullOrWhiteSpace(chart))
        {
            var ch = charts.FirstOrDefault(c =>
                Eq(chart!, c.name) || Eq(chart!, c.title) ||
                c.cols.Any(cf => IsFieldParamField(cf.t, cf.f) && (Eq(chart!, cf.t) || Eq(chart!, cf.f))));
            if (ch.co is not null)
            {
                resolvedChart = ch.title ?? ch.name;
                var axis = new HashSet<string>(
                    ch.cols.Where(cf => IsFieldParamField(cf.t, cf.f)).Select(cf => FpKey(cf.t, cf.f)),
                    StringComparer.OrdinalIgnoreCase);
                target = fp.FirstOrDefault(s => axis.Contains(FpKey(s.Table!, s.Field!)) && !s.AlreadySingle)
                      ?? fp.FirstOrDefault(s => axis.Contains(FpKey(s.Table!, s.Field!)));
                if (target is not null) reason = $"slicer feeds the field-parameter axis of chart '{resolvedChart}'";
            }
        }

        // (b) exactly one field-parameter slicer on the page.
        if (target is null && fp.Count == 1) { target = fp[0]; reason = "the only field-parameter slicer on the page"; }

        // (c) several field-parameter slicers: pick the one feeding a field-parameter-axis chart that is not yet
        //     single-select (the flat-liner); else the sole not-already-single field-parameter slicer.
        if (target is null && fp.Count > 1)
        {
            var flatliners = fp.Where(s => !s.AlreadySingle && plotted.Contains(FpKey(s.Table!, s.Field!))).ToList();
            if (flatliners.Count == 1) { target = flatliners[0]; reason = "the field-parameter slicer feeding a flat/field-parameter-axis chart that is not yet single-select"; }
            else
            {
                var notSingle = fp.Where(s => !s.AlreadySingle).ToList();
                if (notSingle.Count == 1) { target = notSingle[0]; reason = "the only field-parameter slicer not already single-select"; }
            }
        }

        if (target is null)
        {
            string list = slicers.Count == 0 ? "(no slicers on this page)"
                : string.Join("; ", slicers.Select(s =>
                    $"'{s.Title ?? s.Name}'"
                    + (s.Table is not null && s.Field is not null ? $" -> {s.Table}[{s.Field}]" : "")
                    + (s.IsFieldParam ? " [field-parameter]" : "")
                    + (s.AlreadySingle ? " [already single-select]" : "")));
            string why = fp.Count == 0
                ? "No field-parameter slicer was found on this page"
                : $"{fp.Count} field-parameter slicers are candidates and the chart to fix is ambiguous";
            throw new InvalidOperationException(
                $"{why} on page '{pageDisplay}'. Pass 'chart' (a chart's title, name, or bound field) to pick which "
                + $"slicer to fix. Slicers present: {list}.");
        }

        // apply single-select (+ optional explicit default) via the proven internals, resolving the slicer by its
        // config name. strictSingleSelect is the ground-truth fix; the default just pins the opening option.
        string slicerRef = target.Name ?? target.Title ?? $"{target.Table}.{target.Field}";
        var applied = JsonSerializer.SerializeToNode(
            SetSlicerSelection(reportSessionId, pageDisplay, slicerRef, singleSelect: true, defaultValue)) as JsonObject
            ?? new JsonObject();

        return new
        {
            ok = true,
            page = pageDisplay,
            slicer = (string?)applied["slicer"],
            slicerTitle = (string?)applied["title"],
            fieldParameter = target.Table,
            boundField = (string?)applied["boundField"],
            chart = resolvedChart,
            strictSingleSelect = true,
            singleSelectProperty = (string?)applied["singleSelectProperty"],
            default_applied = (bool?)applied["defaultApplied"] ?? false,
            default_value = defaultValue,
            default_note = (string?)applied["defaultNote"],
            reason,
            candidates = fp.Select(s => s.Title ?? s.Name).ToArray(),
            before = applied["before"]?.DeepClone(),
            after = applied["after"]?.DeepClone(),
        };
    }

    // ---- offline-edit helpers: flexible visual resolver, bound-field reader, JSON-path set, value builder ----

    /// <summary>A slicer scanned off a page for the one-call field-parameter fix (fix_slicer_single_select).</summary>
    private sealed class PageSlicer
    {
        public JsonObject Co = null!;
        public JsonObject Sv = null!;
        public string? Name;
        public string? Title;
        public string? Table;
        public string? Field;
        public bool AlreadySingle;
        public bool IsFieldParam;
    }

    private static string FpKey(string table, string field) => table + "" + field;

    /// <summary>Find a page by exact name/displayName, then case-insensitively, then by a case-insensitive
    /// substring match either way (a LOOSE match so a caller can pass a partial page title).</summary>
    private static JsonObject FindSectionLoose(JsonObject root, string page)
    {
        JsonObject? ciExact = null, contains = null;
        foreach (var s in Sections(root))
        {
            if (s is not JsonObject so) continue;
            string? nm = (string?)so["name"], dn = (string?)so["displayName"];
            if (nm == page || dn == page) return so;
            if (ciExact is null && (Eq(page, nm) || Eq(page, dn))) ciExact = so;
            if (contains is null &&
                ((dn is not null && dn.Contains(page, StringComparison.OrdinalIgnoreCase))
                 || (nm is not null && nm.Contains(page, StringComparison.OrdinalIgnoreCase))
                 || (dn is not null && page.Contains(dn, StringComparison.OrdinalIgnoreCase))))
                contains = so;
        }
        return ciExact ?? contains
            ?? throw new InvalidOperationException($"Page '{page}' not found (matched by name, displayName, or loose contains).");
    }

    /// <summary>True when a slicer already carries objects.selection[*].properties.strictSingleSelect = true.</summary>
    private static bool SlicerHasSingleSelect(JsonObject sv)
    {
        if ((sv["objects"] as JsonObject)?["selection"] is not JsonArray sel) return false;
        foreach (var e in sel)
        {
            var lit = (e as JsonObject)?["properties"]?["strictSingleSelect"]?["expr"]?["Literal"]?["Value"];
            if (lit is not null && string.Equals(lit.ToString().Trim().Trim('\''), "true", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Every COLUMN (not measure) field binding of a visual as (table, field), resolved from the
    /// prototypeQuery Select/From (falling back to projection queryRefs). Field parameters are always columns, so
    /// this is the set used to spot a field-parameter axis on a chart and to count a table's distinct fields.</summary>
    private static List<(string table, string field)> AllColumnFields(JsonObject sv)
    {
        var result = new List<(string, string)>();
        if (sv["prototypeQuery"] is JsonObject pq)
        {
            var alias = new Dictionary<string, string>(StringComparer.Ordinal);
            if (pq["From"] is JsonArray from)
                foreach (var fr in from)
                    if (fr is JsonObject fo && (string?)fo["Name"] is string a && (string?)fo["Entity"] is string ent)
                        alias[a] = ent;
            if (pq["Select"] is JsonArray sel)
                foreach (var s in sel)
                    if (s is JsonObject so && so["Column"] is JsonObject col && (string?)col["Property"] is string prop)
                    {
                        string? src = (string?)col["Expression"]?["SourceRef"]?["Source"];
                        if (src is not null && alias.TryGetValue(src, out var ent)) result.Add((ent, prop));
                        else if (alias.Count == 1) result.Add((alias.Values.First(), prop));
                    }
        }
        if (result.Count == 0 && sv["projections"] is JsonObject proj)
            foreach (var (_, refs) in proj)
                if (refs is JsonArray ra)
                    foreach (var r in ra)
                        if ((string?)(r as JsonObject)?["queryRef"] is string q)
                        { int dot = q.IndexOf('.'); if (dot > 0) result.Add((q[..dot], q[(dot + 1)..])); }
        return result;
    }

    private static bool Eq(string a, string? b) => b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The visible TITLE text of a visual (decoded from vcObjects.title[0].properties.text), or null.</summary>
    private static string? TitleText(JsonObject sv)
    {
        var titleArr = sv["vcObjects"]?["title"] as JsonArray;
        var textNode = (titleArr?.FirstOrDefault() as JsonObject)?["properties"]?["text"];
        return textNode is null ? null : Decode(textNode);
    }

    /// <summary>Resolve a visual on a page by config name, config displayName, its visible TITLE text, or - when
    /// <paramref name="matchBoundField"/> - by a bound field reference (a projected "Table.Field" queryRef, or
    /// just the table or field name). Returns (container, parsed config, singleVisual). Throws listing what IS on
    /// the page so a mis-typed reference is easy to fix.</summary>
    private (JsonObject vc, JsonObject co, JsonObject sv) ResolveVisualFlexible(
        JsonObject section, string reference, bool matchBoundField = false)
    {
        var seen = new List<string>();
        foreach (var node in (section["visualContainers"] as JsonArray) ?? new JsonArray())
        {
            if (node is not JsonObject vc || (string?)vc["config"] is not string cfg) continue;
            if (JsonNode.Parse(cfg) is not JsonObject co || co["singleVisual"] is not JsonObject sv) continue;

            string? name = (string?)co["name"];
            string? disp = (string?)co["displayName"];
            string? title = TitleText(sv);
            seen.Add(title ?? disp ?? name ?? "(unnamed)");

            if (Eq(reference, name) || Eq(reference, disp) || Eq(reference, title))
                return (vc, co, sv);
            if (matchBoundField && BoundFieldMatches(sv, reference))
                return (vc, co, sv);
        }
        string by = matchBoundField ? "title, name, displayName or bound field" : "title, name or displayName";
        throw new InvalidOperationException(
            $"Visual '{reference}' not found on this page (matched by {by}). Visuals present: {string.Join(", ", seen)}.");
    }

    /// <summary>Read a visual's primary bound field as (table, field) from its prototypeQuery Select[0]
    /// (Column or Measure), falling back to a "Table.Field" projection queryRef. Returns (null, null) when the
    /// visual has no field binding (textbox / shape / image / button).</summary>
    private static (string? table, string? field) TryBoundField(JsonObject sv)
    {
        if (sv["prototypeQuery"] is JsonObject pq)
        {
            var sel0 = (pq["Select"] as JsonArray)?.FirstOrDefault() as JsonObject;
            var colOrMeas = (sel0?["Column"] ?? sel0?["Measure"]) as JsonObject;
            string? field = (string?)colOrMeas?["Property"];
            string? alias = (string?)colOrMeas?["Expression"]?["SourceRef"]?["Source"];
            string? table = null;
            if (pq["From"] is JsonArray from)
                foreach (var f in from)
                    if (f is JsonObject fo && (alias is null || (string?)fo["Name"] == alias))
                    { table = (string?)fo["Entity"]; if (alias is not null) break; }
            if (field is not null) return (table, field);
        }
        if (sv["projections"] is JsonObject proj)
            foreach (var (_, refs) in proj)
                if (refs is JsonArray ra && ra.FirstOrDefault() is JsonObject r0 && (string?)r0["queryRef"] is string q)
                {
                    int dot = q.IndexOf('.');
                    if (dot > 0) return (q[..dot], q[(dot + 1)..]);
                }
        return (null, null);
    }

    /// <summary>True when <paramref name="reference"/> matches the visual's bound field: the resolved table,
    /// the resolved field, "Table.Field", or any projection queryRef.</summary>
    private static bool BoundFieldMatches(JsonObject sv, string reference)
    {
        var (table, field) = TryBoundField(sv);
        if (Eq(reference, table) || Eq(reference, field)) return true;
        if (table is not null && field is not null && Eq(reference, $"{table}.{field}")) return true;
        if (sv["projections"] is JsonObject proj)
            foreach (var (_, refs) in proj)
                if (refs is JsonArray ra)
                    foreach (var r in ra)
                        if (Eq(reference, (string?)(r as JsonObject)?["queryRef"])) return true;
        return false;
    }

    /// <summary>Parse a JSON path like <c>objects.selection[0].properties.strictSingleSelect</c> into ordered
    /// segments: a property key (isIndex=false) or an array index (isIndex=true).</summary>
    private static List<(bool isIndex, string key, int index)> ParsePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("property_path is empty.");
        var segs = new List<(bool, string, int)>();
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            string p = part;
            int lb;
            while ((lb = p.IndexOf('[')) >= 0)
            {
                string nm = p[..lb];
                if (nm.Length > 0) segs.Add((false, nm, 0));
                int rb = p.IndexOf(']', lb);
                if (rb < 0) throw new ArgumentException($"malformed path segment '{part}' (missing ']').");
                string idxStr = p[(lb + 1)..rb];
                if (!int.TryParse(idxStr, System.Globalization.NumberStyles.Integer, Inv, out int idx) || idx < 0)
                    throw new ArgumentException($"array index '{idxStr}' is not a non-negative integer in '{part}'.");
                segs.Add((true, "", idx));
                p = p[(rb + 1)..];
            }
            if (p.Length > 0) segs.Add((false, p, 0));
        }
        if (segs.Count == 0) throw new ArgumentException("property_path did not yield any segments.");
        return segs;
    }

    /// <summary>Set <paramref name="newValue"/> at the parsed path under <paramref name="root"/>, creating any
    /// missing intermediate objects/arrays. Returns a deep clone of the prior value at that leaf (or null).</summary>
    private static JsonNode? SetAtPath(JsonObject root, List<(bool isIndex, string key, int index)> segs, JsonNode? newValue)
    {
        JsonNode current = root;
        for (int i = 0; i < segs.Count - 1; i++)
        {
            var seg = segs[i];
            bool nextIsIndex = segs[i + 1].isIndex;
            if (!seg.isIndex)
            {
                var obj = current as JsonObject
                          ?? throw new ArgumentException($"path expects an object before '.{seg.key}'.");
                if (obj[seg.key] is null) obj[seg.key] = nextIsIndex ? new JsonArray() : new JsonObject();
                current = obj[seg.key]!;
            }
            else
            {
                var arr = current as JsonArray
                          ?? throw new ArgumentException($"path expects an array before index [{seg.index}].");
                while (arr.Count <= seg.index) arr.Add(nextIsIndex ? new JsonArray() : new JsonObject());
                if (arr[seg.index] is null) arr[seg.index] = nextIsIndex ? new JsonArray() : new JsonObject();
                current = arr[seg.index]!;
            }
        }

        var leaf = segs[^1];
        JsonNode? before;
        if (!leaf.isIndex)
        {
            var obj = current as JsonObject
                      ?? throw new ArgumentException($"path expects an object at leaf '.{leaf.key}'.");
            before = obj[leaf.key]?.DeepClone();
            obj[leaf.key] = newValue;
        }
        else
        {
            var arr = current as JsonArray
                      ?? throw new ArgumentException($"path expects an array at leaf index [{leaf.index}].");
            while (arr.Count <= leaf.index) arr.Add((JsonNode?)null);
            before = arr[leaf.index]?.DeepClone();
            arr[leaf.index] = newValue;
        }
        return before;
    }

    /// <summary>Interpret an update_visual_property value string by valueKind (see UpdateVisualProperty).</summary>
    private static JsonNode? BuildUpdateValue(string value, string valueKind, string leafKey)
    {
        switch ((valueKind ?? "auto").Trim().ToLowerInvariant())
        {
            case "auto":
            case "raw":
                return ParseAuto(value);
            case "json":
                return JsonNode.Parse(value);
            case "string":
                return JsonValue.Create(value);
            case "literal":
                return Encode(value, KindFor(leafKey, ParseAuto(value)));
            case "bool":
            case "boolean":
                return Encode(value, FmtKind.Bool);
            case "number":
                return Encode(value, FmtKind.Number);
            case "color":
            case "colour":
                return Encode(value, FmtKind.Color);
            case "text":
            case "enum":
                return Encode(value, FmtKind.Text);
            default:
                throw new ArgumentException(
                    $"unknown valueKind '{valueKind}' (auto|raw|json|string|literal|bool|number|color|text).");
        }
    }

    /// <summary>auto value coercion: a JSON object/array literal, a bool, an integer/double, JSON null, else the
    /// raw string. Used by update_visual_property's default (auto/raw) valueKind.</summary>
    private static JsonNode? ParseAuto(string value)
    {
        string v = (value ?? "").Trim();
        if (v.Length == 0) return JsonValue.Create(value ?? "");
        if (v[0] is '{' or '[')
        {
            try { return JsonNode.Parse(v); } catch { return JsonValue.Create(value!); }
        }
        if (v == "true") return JsonValue.Create(true);
        if (v == "false") return JsonValue.Create(false);
        if (v == "null") return null;
        if (!v.Contains(' ') && double.TryParse(v, System.Globalization.NumberStyles.Any, Inv, out double d))
            return long.TryParse(v, System.Globalization.NumberStyles.Integer, Inv, out long l)
                ? JsonValue.Create(l) : JsonValue.Create(d);
        return JsonValue.Create(value!);
    }

    // -------------------------------------------------------------------- save
    public object Save(string reportSessionId)
    {
        var session = _sessions.GetReport(reportSessionId);
        string json = session.Layout.Root.ToJsonString(JsonOpts);
        byte[] bytes = new UnicodeEncoding(false, false).GetBytes(json);  // UTF-16-LE, NO BOM
        Repack(session.PbixPath, session.Layout.LayoutPartName, bytes, session.PendingResources, session.KeepOnlyBaseThemePart);
        session.PendingResources.Clear();
        session.KeepOnlyBaseThemePart = null;
        session.Dirty = false;
        return new
        {
            ok = true, persistedToDisk = true, pbixPath = session.PbixPath,
            note = "Saved. The .pbix must NOT be open in Power BI Desktop during save, or Desktop will overwrite it.",
        };
    }

    /// <summary>
    /// Replace the session's entire Report/Layout with a pre-generated legacy Layout JSON (e.g. from
    /// ReportLayoutBuilder.Build, tier-scaled) and stage a theme part so it is folded into the .pbix on Save.
    /// Used by the tier-accurate /build solution path: it rewrites only the report layer while the existing
    /// .pbix DataModel rides through Repack untouched. Throws if the layout JSON is not a JSON object.
    /// </summary>
    public void ReplaceLayout(string reportSessionId, string layoutJson, byte[]? themeBytes, string? themePartName)
    {
        var session = _sessions.GetReport(reportSessionId);
        var root = JsonNode.Parse(layoutJson) as JsonObject
                   ?? throw new InvalidOperationException("replacement Report/Layout is not a JSON object.");
        session.Layout = new ReportLayout { Root = root, LayoutPartName = session.Layout.LayoutPartName };
        if (themeBytes is { Length: > 0 } && !string.IsNullOrWhiteSpace(themePartName))
        {
            session.PendingResources[themePartName!] = themeBytes;
            // a tier-accurate rebuild swaps the theme: keep ONLY the chosen palette base theme part, so the
            // starter's stale SuperBiBase.json (or any other BaseThemes/*.json) is dropped on Save.
            session.KeepOnlyBaseThemePart = themePartName;
        }
        session.Dirty = true;
    }

    /// <summary>Stage an extra binary part (e.g. a brand logo image) to be written into the .pbix on Save - used
    /// by the tier-accurate /build path to fold in the RegisteredResources logo the regenerated Layout references.</summary>
    public void AddPendingResource(string reportSessionId, string partName, byte[] bytes)
    {
        if (string.IsNullOrWhiteSpace(partName) || bytes is not { Length: > 0 }) return;
        var session = _sessions.GetReport(reportSessionId);
        session.PendingResources[partName] = bytes;
        session.Dirty = true;
    }

    // ============================================================================================
    //  DataMashup / Power Query M extract + edit (MS-QDEFF)
    //
    //  The .pbix "DataMashup" part is a binary container (MS-QDEFF):
    //     [Int32 version][Int32 packagePartsLength][packageParts = an inner OPC zip]
    //     [Int32 permissionsLength][permissions XML]
    //     [Int32 metadataLength][metadata: Int32 version + Metadata XML + ... ]
    //     [Int32 permissionBindingsLength][permission bindings (SHA-256 over the formulas)]
    //  The inner OPC zip holds Formulas/Section1.m (plain-text M, "section Section1; shared Query = ...;")
    //  and Config/Package.xml + a Metadata part. We read Section1.m (the M) + the metadata XML, and on
    //  rewrite we repackage the inner zip, re-emit the QDEFF container, and CLEAR PermissionBindings (they are
    //  SHA-256 over the formulas; once the M changes they no longer match - Desktop recomputes them on open,
    //  exactly like SecurityBindings). RISKY: always work on a COPY.
    // ============================================================================================

    private const string MashupPartName = "DataMashup";

    /// <summary>Locate the DataMashup part in a .pbix (its name in the OPC zip varies by case/version).</summary>
    private static ZipArchiveEntry? FindMashupEntry(ZipArchive zip)
    {
        foreach (var e in zip.Entries)
        {
            string n = e.FullName.TrimStart('/');
            if (n.Equals("DataMashup", StringComparison.OrdinalIgnoreCase)) return e;
        }
        return null;
    }

    /// <summary>The parsed sections of a QDEFF DataMashup container.</summary>
    private sealed class MashupContainer
    {
        public int Version;
        public byte[] PackageParts = Array.Empty<byte>();   // the inner OPC zip bytes
        public byte[] Permissions = Array.Empty<byte>();
        public byte[] Metadata = Array.Empty<byte>();
        public byte[] PermissionBindings = Array.Empty<byte>();
    }

    private static MashupContainer ParseMashup(byte[] raw)
    {
        if (raw.Length < 8) throw new InvalidOperationException("DataMashup part is too small to be a QDEFF container.");
        int pos = 0;
        int ReadInt() { int v = BitConverter.ToInt32(raw, pos); pos += 4; return v; }
        byte[] ReadBlock(int len)
        {
            if (len < 0 || pos + len > raw.Length)
                throw new InvalidOperationException("DataMashup container is malformed (block length out of range).");
            var b = raw[pos..(pos + len)]; pos += len; return b;
        }
        var c = new MashupContainer();
        c.Version = ReadInt();
        c.PackageParts = ReadBlock(ReadInt());
        if (pos + 4 <= raw.Length) c.Permissions = ReadBlock(ReadInt());
        if (pos + 4 <= raw.Length) c.Metadata = ReadBlock(ReadInt());
        if (pos + 4 <= raw.Length) c.PermissionBindings = ReadBlock(ReadInt());
        return c;
    }

    private static byte[] SerialiseMashup(MashupContainer c)
    {
        using var ms = new MemoryStream();
        void WriteInt(int v) => ms.Write(BitConverter.GetBytes(v), 0, 4);
        void WriteBlock(byte[] b) { WriteInt(b.Length); ms.Write(b, 0, b.Length); }
        WriteInt(c.Version);
        WriteBlock(c.PackageParts);
        WriteBlock(c.Permissions);
        WriteBlock(c.Metadata);
        WriteBlock(c.PermissionBindings);
        return ms.ToArray();
    }

    /// <summary>Read Formulas/Section1.m from the inner OPC package bytes.</summary>
    private static string? ReadSection1M(byte[] packageZipBytes)
    {
        using var ms = new MemoryStream(packageZipBytes);
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        foreach (var e in zip.Entries)
        {
            string n = e.FullName.TrimStart('/');
            if (n.Equals("Formulas/Section1.m", StringComparison.OrdinalIgnoreCase))
            {
                using var s = e.Open(); using var sr = new StreamReader(s, Encoding.UTF8, true);
                return sr.ReadToEnd();
            }
        }
        return null;
    }

    /// <summary>Rewrite Formulas/Section1.m inside the inner OPC package, preserving every other inner part.</summary>
    private static byte[] RewriteSection1M(byte[] packageZipBytes, string newM)
    {
        using var srcMs = new MemoryStream(packageZipBytes);
        using var src = new ZipArchive(srcMs, ZipArchiveMode.Read);
        using var dstMs = new MemoryStream();
        using (var dst = new ZipArchive(dstMs, ZipArchiveMode.Create, leaveOpen: true))
        {
            bool wrote = false;
            foreach (var e in src.Entries)
            {
                string n = e.FullName.TrimStart('/');
                var ne = dst.CreateEntry(e.FullName, CompressionLevel.Optimal);
                using var os = ne.Open();
                if (n.Equals("Formulas/Section1.m", StringComparison.OrdinalIgnoreCase))
                {
                    byte[] mb = new UTF8Encoding(false).GetBytes(newM);
                    os.Write(mb, 0, mb.Length);
                    wrote = true;
                }
                else
                {
                    using var s = e.Open(); s.CopyTo(os);
                }
            }
            if (!wrote)
            {
                var ne = dst.CreateEntry("Formulas/Section1.m", CompressionLevel.Optimal);
                using var os = ne.Open();
                byte[] mb = new UTF8Encoding(false).GetBytes(newM);
                os.Write(mb, 0, mb.Length);
            }
        }
        return dstMs.ToArray();
    }

    // A query in Section1.m is "shared <Name> = <expr>;" (or "<Name> = <expr>;" for non-shared). Parse the
    // top-level query names + their M bodies. Conservative: split on the section body, track brackets/strings.
    private static List<(string name, string m)> ParseQueries(string section1M)
    {
        var queries = new List<(string, string)>();
        if (string.IsNullOrEmpty(section1M)) return queries;
        // strip the "section Section1;" header up to the first ';' at depth 0.
        int start = section1M.IndexOf("section", StringComparison.Ordinal);
        int bodyStart = 0;
        if (start >= 0)
        {
            int semi = section1M.IndexOf(';', start);
            if (semi >= 0) bodyStart = semi + 1;
        }
        var rx = new System.Text.RegularExpressions.Regex(
            @"(?:^|;)\s*(?:shared\s+)?(?:#""(?<q>[^""]+)""|(?<n>[A-Za-z_][A-Za-z0-9_]*))\s*=",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        foreach (System.Text.RegularExpressions.Match mm in rx.Matches(section1M, bodyStart))
        {
            string nm = mm.Groups["q"].Success ? mm.Groups["q"].Value : mm.Groups["n"].Value;
            if (nm == "section") continue;
            queries.Add((nm, ""));   // body extraction is best-effort; the full M is returned separately
        }
        return queries;
    }

    /// <summary>get_datamashup_info: report whether a .pbix has a DataMashup part and the query names it
    /// declares. READ-ONLY. If absent, point the caller at set_partition_m / get_table_m (enhanced-metadata
    /// PBI_V3 models store M in the DataModel/TMDL instead).</summary>
    public object GetDataMashupInfo(string pbixPath)
    {
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        using var zip = ZipFile.OpenRead(pbixPath);
        var entry = FindMashupEntry(zip);
        if (entry == null)
            return new
            {
                ok = true, hasDataMashup = false, pbixPath,
                note = "No DataMashup part. This .pbix stores Power Query M inside the DataModel/TMDL (enhanced-metadata / PBI_V3). Use get_table_m / set_partition_m to read or edit the M.",
            };

        byte[] raw; using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); raw = ms.ToArray(); }
        var c = ParseMashup(raw);
        string? m = ReadSection1M(c.PackageParts);
        var queries = m != null ? ParseQueries(m).Select(q => q.name).ToList() : new List<string>();
        return new
        {
            ok = true, hasDataMashup = true, pbixPath,
            qdeffVersion = c.Version,
            queries, queryCount = queries.Count,
            hasPermissionBindings = c.PermissionBindings.Length > 0,
            packageBytes = c.PackageParts.Length, metadataBytes = c.Metadata.Length,
        };
    }

    /// <summary>extract_power_query: return the full Section1.m (the plain-text M for every query) plus the
    /// declared query names. READ-ONLY.</summary>
    public object ExtractPowerQuery(string pbixPath)
    {
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        using var zip = ZipFile.OpenRead(pbixPath);
        var entry = FindMashupEntry(zip);
        if (entry == null)
            return new
            {
                ok = true, hasDataMashup = false, pbixPath, section1M = (string?)null,
                note = "No DataMashup part. M lives in the DataModel/TMDL here - use get_table_m to read it.",
            };

        byte[] raw; using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); raw = ms.ToArray(); }
        var c = ParseMashup(raw);
        string? m = ReadSection1M(c.PackageParts)
                    ?? throw new InvalidOperationException("DataMashup package has no Formulas/Section1.m.");
        var queries = ParseQueries(m).Select(q => q.name).ToList();
        return new { ok = true, hasDataMashup = true, pbixPath, section1M = m, queries, queryCount = queries.Count };
    }

    /// <summary>Guard: never operate on a path under a protected location. Operators can fence off
    /// directories (e.g. live client folders) via SUPERBI_PROTECTED_PATHS, a ';'-separated list.</summary>
    private static void GuardMashupWritePath(string pbixPath)
    {
        string full = Path.GetFullPath(pbixPath);
        string[] forbidden = (Environment.GetEnvironmentVariable("SUPERBI_PROTECTED_PATHS") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var bad in forbidden)
        {
            string badFull;
            try { badFull = Path.GetFullPath(bad); } catch { continue; }
            if (full.StartsWith(badFull, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"refusing to edit a DataMashup under a protected path ({bad}). Copy the .pbix elsewhere first.");
        }
    }

    /// <summary>Repackage the DataMashup container with new inner-package bytes + cleared PermissionBindings,
    /// and write it back into the .pbix as a single zip entry (preserving every other part, stripping
    /// SecurityBindings, same discipline as the Layout writer).</summary>
    private void WriteMashupBack(string pbixPath, byte[] newPackageZip, MashupContainer original, out bool clearedBindings)
    {
        clearedBindings = original.PermissionBindings.Length > 0;
        var rebuilt = new MashupContainer
        {
            Version = original.Version,
            PackageParts = newPackageZip,
            Permissions = original.Permissions,
            Metadata = original.Metadata,
            PermissionBindings = Array.Empty<byte>(),   // SHA-256 over the formulas - now stale; Desktop recomputes.
        };
        byte[] newMashup = SerialiseMashup(rebuilt);
        Repack(pbixPath, FindMashupPartNameOnDisk(pbixPath), newMashup);
    }

    private static string FindMashupPartNameOnDisk(string pbixPath)
    {
        using var zip = ZipFile.OpenRead(pbixPath);
        var e = FindMashupEntry(zip);
        return e?.FullName ?? MashupPartName;
    }

    /// <summary>update_power_query: replace the entire Section1.m with newM (the full "section Section1; ..."
    /// document) inside the DataMashup, clearing PermissionBindings. RISKY - operate on a COPY. Refuses to run
    /// on a path under the protected client locations.</summary>
    public object UpdatePowerQuery(string pbixPath, string newM)
    {
        if (string.IsNullOrWhiteSpace(newM)) throw new ArgumentException("newM (the full Section1.m) is required.");
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        GuardMashupWritePath(pbixPath);

        MashupContainer c;
        using (var zip = ZipFile.OpenRead(pbixPath))
        {
            var entry = FindMashupEntry(zip)
                        ?? throw new InvalidOperationException("This .pbix has no DataMashup part - M lives in the DataModel/TMDL; use set_partition_m.");
            byte[] raw; using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); raw = ms.ToArray(); }
            c = ParseMashup(raw);
        }
        byte[] newPkg = RewriteSection1M(c.PackageParts, newM);
        WriteMashupBack(pbixPath, newPkg, c, out bool cleared);
        return new
        {
            ok = true, pbixPath, rewroteSection1M = true, clearedPermissionBindings = cleared,
            note = "Section1.m rewritten; PermissionBindings cleared (Desktop recomputes the SHA-256 on open). FLAG: work on a COPY - the .pbix must be closed in Desktop.",
        };
    }

    /// <summary>rewrite_connection_string: find/replace a literal substring (e.g. a server/db name or file
    /// path) across Section1.m inside the DataMashup, clearing PermissionBindings. RISKY - operate on a COPY.</summary>
    public object RewriteConnectionString(string pbixPath, string find, string replace)
    {
        if (string.IsNullOrEmpty(find)) throw new ArgumentException("find is required.");
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        GuardMashupWritePath(pbixPath);

        MashupContainer c; string m;
        using (var zip = ZipFile.OpenRead(pbixPath))
        {
            var entry = FindMashupEntry(zip)
                        ?? throw new InvalidOperationException("This .pbix has no DataMashup part - M lives in the DataModel/TMDL; use set_partition_m.");
            byte[] raw; using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); raw = ms.ToArray(); }
            c = ParseMashup(raw);
            m = ReadSection1M(c.PackageParts) ?? throw new InvalidOperationException("DataMashup has no Formulas/Section1.m.");
        }
        int occurrences = 0; int idx = 0;
        while ((idx = m.IndexOf(find, idx, StringComparison.Ordinal)) >= 0) { occurrences++; idx += find.Length; }
        if (occurrences == 0)
            return new { ok = true, pbixPath, replaced = 0, note = $"'{find}' not found in Section1.m; nothing written." };

        string newM = m.Replace(find, replace);
        byte[] newPkg = RewriteSection1M(c.PackageParts, newM);
        WriteMashupBack(pbixPath, newPkg, c, out bool cleared);
        return new
        {
            ok = true, pbixPath, replaced = occurrences, clearedPermissionBindings = cleared,
            note = "Connection string rewritten in Section1.m; PermissionBindings cleared. FLAG: work on a COPY - the .pbix must be closed in Desktop.",
        };
    }

    // -- expose the QDEFF round-trip helpers for the test suite (build/parse a synthetic container) --
    internal static byte[] BuildMashupPackageForTest(string section1M)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry("Formulas/Section1.m", CompressionLevel.Optimal);
            using (var s = e.Open())
            {
                byte[] b = new UTF8Encoding(false).GetBytes(section1M);
                s.Write(b, 0, b.Length);
            }
            // a minimal Config/Package.xml so the inner package looks like a real one.
            var cfg = zip.CreateEntry("Config/Package.xml", CompressionLevel.Optimal);
            using (var cs = cfg.Open())
            {
                byte[] cb = new UTF8Encoding(false).GetBytes("<Package xmlns=\"http://schemas.microsoft.com/DataMashup\" />");
                cs.Write(cb, 0, cb.Length);
            }
        }
        return ms.ToArray();
    }

    internal static byte[] BuildMashupContainerForTest(string section1M, bool withBindings)
    {
        var c = new MashupContainer
        {
            Version = 0,
            PackageParts = BuildMashupPackageForTest(section1M),
            Permissions = new UTF8Encoding(false).GetBytes("<PermissionList />"),
            Metadata = new UTF8Encoding(false).GetBytes("meta"),
            PermissionBindings = withBindings ? new byte[] { 1, 2, 3, 4 } : Array.Empty<byte>(),
        };
        return SerialiseMashup(c);
    }

    internal static string? ReadSection1MFromContainerForTest(byte[] mashupContainer)
        => ReadSection1M(ParseMashup(mashupContainer).PackageParts);

    internal static bool MashupContainerHasPermissionBindingsForTest(byte[] mashupContainer)
        => ParseMashup(mashupContainer).PermissionBindings.Length > 0;

    /// <summary>Patch a single part in the OPC ZIP, preserving every other part, and add any new
    /// binary parts (e.g. logo images). Content types for png/jpg are ensured when images are added.</summary>
    private void Repack(string pbixPath, string partName, byte[] newBytes, IReadOnlyDictionary<string, byte[]>? extraParts = null,
        string? keepOnlyBaseThemePart = null)
    {
        string tmp = pbixPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var oic = StringComparison.OrdinalIgnoreCase;
        bool needPng = extraParts?.Keys.Any(k => k.EndsWith(".png", oic)) ?? false;
        bool needJpg = extraParts?.Keys.Any(k => k.EndsWith(".jpg", oic) || k.EndsWith(".jpeg", oic)) ?? false;
        const string baseThemeDir = "Report/StaticResources/SharedResources/BaseThemes/";
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var src = ZipFile.OpenRead(pbixPath))
        using (var dstStream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var dst = new ZipArchive(dstStream, ZipArchiveMode.Create))
        {
            foreach (var e in src.Entries)
            {
                if (string.Equals(e.FullName, "SecurityBindings", oic)) continue; // drop signature
                // drop a stale base theme part when a tier-accurate rebuild swaps in a different palette theme
                if (keepOnlyBaseThemePart != null
                    && e.FullName.StartsWith(baseThemeDir, oic)
                    && !string.Equals(e.FullName, keepOnlyBaseThemePart, oic))
                    continue;
                written.Add(e.FullName);
                if (string.Equals(e.FullName, partName, oic))
                {
                    var ne = dst.CreateEntry(e.FullName, CompressionLevel.Optimal);
                    using var s = ne.Open(); s.Write(newBytes, 0, newBytes.Length);
                }
                else if (string.Equals(e.FullName, "[Content_Types].xml", oic))
                {
                    string ct = EnsureImageDefaults(RemoveSecurityBindingsOverride(ReadEntryText(e)), needPng, needJpg);
                    var ne = dst.CreateEntry(e.FullName, CompressionLevel.Optimal);
                    using var s = ne.Open();
                    byte[] cb = new UTF8Encoding(false).GetBytes(ct);
                    s.Write(cb, 0, cb.Length);
                }
                else
                {
                    var level = string.Equals(e.FullName, "DataModel", oic) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var ne = dst.CreateEntry(e.FullName, level);
                    using var os = e.Open(); using var ns = ne.Open(); os.CopyTo(ns);
                }
            }
            if (extraParts != null)
                foreach (var kv in extraParts)
                {
                    if (written.Contains(kv.Key)) continue;
                    var ne = dst.CreateEntry(kv.Key, CompressionLevel.Optimal);
                    using var s = ne.Open(); s.Write(kv.Value, 0, kv.Value.Length);
                }
        }
        File.Delete(pbixPath);
        File.Move(tmp, pbixPath);
    }

    private static string EnsureImageDefaults(string ct, bool png, bool jpg)
    {
        void Add(string ext, string mime)
        {
            if (ct.IndexOf($"Extension=\"{ext}\"", StringComparison.OrdinalIgnoreCase) >= 0) return;
            string def = $"<Default Extension=\"{ext}\" ContentType=\"{mime}\" />";
            int at = ct.IndexOf("<Override", StringComparison.OrdinalIgnoreCase);
            if (at < 0) at = ct.IndexOf("</Types>", StringComparison.OrdinalIgnoreCase);
            if (at >= 0) ct = ct.Insert(at, def);
        }
        if (png) Add("png", "image/png");
        if (jpg) { Add("jpg", "image/jpeg"); Add("jpeg", "image/jpeg"); }
        return ct;
    }

    // ------------------------------------------------------- visual JSON builders
    internal static JsonObject BuildSingleVisual(string visualType, IReadOnlyList<FieldBinding> bindings, string? title, string? slicerMode = null)
    {
        // distinct entity -> alias
        var tables = bindings.Select(b => b.Table).Distinct().ToList();
        var alias = tables.Select((t, i) => (t, a: "t" + (i + 1))).ToDictionary(p => p.t, p => p.a);

        var from = new JsonArray();
        foreach (var t in tables)
            from.Add(new JsonObject { ["Name"] = alias[t], ["Entity"] = t, ["Type"] = 0 });

        var select = new JsonArray();
        foreach (var b in bindings)
        {
            string queryRef = $"{b.Table}.{b.Field}";
            var expr = new JsonObject { ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Source"] = alias[b.Table] } }, ["Property"] = b.Field };
            var sel = new JsonObject();
            sel[b.Kind.Equals("measure", StringComparison.OrdinalIgnoreCase) ? "Measure" : "Column"] = expr;
            sel["Name"] = queryRef;
            select.Add(sel);
        }

        var projections = new JsonObject();
        foreach (var grp in bindings.GroupBy(b => b.Role))
        {
            var arr = new JsonArray();
            foreach (var b in grp) arr.Add(new JsonObject { ["queryRef"] = $"{b.Table}.{b.Field}" });
            projections[grp.Key] = arr;
        }

        var sv = new JsonObject
        {
            ["visualType"] = visualType,
            ["projections"] = projections,
            ["prototypeQuery"] = new JsonObject { ["Version"] = 2, ["From"] = from, ["Select"] = select },
            ["drillFilterOtherVisuals"] = true,
        };

        if (!string.IsNullOrWhiteSpace(title))
        {
            sv["vcObjects"] = new JsonObject
            {
                ["title"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["properties"] = new JsonObject
                        {
                            ["text"] = new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = $"'{title.Replace("'", "''")}'" } } },
                            ["show"] = new JsonObject { ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "true" } } },
                        },
                    },
                },
            };
        }

        // slicer presentation: Dropdown (compact) + avoid the double title.
        if (visualType.Equals("slicer", StringComparison.OrdinalIgnoreCase))
        {
            var objs = new JsonObject();
            if (!string.IsNullOrWhiteSpace(slicerMode))
            {
                string modeVal = slicerMode.Equals("List", StringComparison.OrdinalIgnoreCase) ? "Basic" : slicerMode!;
                objs["data"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["mode"] = Lit($"'{modeVal}'") } } };
            }
            // The visual title (vcObjects.title) already labels the slicer; hide the slicer's
            // built-in FIELD HEADER so we don't render the label twice.
            if (!string.IsNullOrWhiteSpace(title))
                objs["header"] = new JsonArray { new JsonObject { ["properties"] = new JsonObject { ["show"] = Lit("false") } } };
            if (objs.Count > 0) sv["objects"] = objs;
        }
        return sv;
    }

    // ------------------------------------------------------------------ helpers
    private static JsonArray Sections(JsonObject root) =>
        root["sections"] as JsonArray ?? throw new InvalidOperationException("Report/Layout has no 'sections'.");

    private static JsonObject FindSection(JsonObject root, string pageName)
    {
        foreach (var s in Sections(root))
            if (s is JsonObject so &&
                ((string?)so["name"] == pageName || (string?)so["displayName"] == pageName))
                return so;
        throw new InvalidOperationException($"Page '{pageName}' not found (match by name or displayName).");
    }

    // JsonNode's explicit (double?) cast is strict about the stored numeric type;
    // GetValue<double>() converts any JSON number (int or float) safely.
    private static double? Num(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<int>(); } catch { return double.TryParse(n.ToString(), out var d) ? d : (double?)null; } }
    }

    private static byte[] StripBom(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return b[2..];
        return b;
    }

    private static string ReadEntryText(ZipArchiveEntry e)
    {
        using var s = e.Open(); using var sr = new StreamReader(s, Encoding.UTF8, true);
        return sr.ReadToEnd();
    }

    private static string RemoveSecurityBindingsOverride(string contentTypes)
    {
        int i = contentTypes.IndexOf("/SecurityBindings", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return contentTypes;
        int start = contentTypes.LastIndexOf("<Override", i, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return contentTypes;
        int end = contentTypes.IndexOf("/>", i, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return contentTypes;
        return contentTypes.Remove(start, end + 2 - start);
    }
}
