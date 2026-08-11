using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SuperBiMcp.Services;

/// <summary>
/// The strategic enhanced-report (PBIR) read/write path. Where <see cref="ReportService"/> drives the
/// LEGACY Report/Layout (one UTF-16-LE JSON blob), PBIR stores a report as a FOLDER TREE of per-file UTF-8
/// JSON:
///   definition.pbir                                  (version + datasetReference)
///   report.json                                      (theme ref, settings, report-level filters, resourcePackages)
///   definition/pages/pages.json                      (pageOrder[] + activePageName)
///   definition/pages/&lt;pageName&gt;/page.json            (name = GUID, displayName, displayOption, width/height, filters)
///   definition/pages/&lt;pageName&gt;/visuals/&lt;id&gt;/visual.json  (ONE per visual: position + visual{...} + filters)
///   definition/bookmarks/&lt;name&gt;.bookmark.json + bookmarks.json index
/// Every page/visual/bookmark name is a unique GUID. PBIR is the only report format at GA, so this is the
/// real future-proofing of the engine.
///
/// The VISUAL CONFIG is the SAME semantic-query/objects/projections JSON the legacy engine already builds -
/// just relocated into per-visual files - so every authoring path here ROUTES THROUGH the existing
/// ReportService engine helpers (BuildSingleVisual / MergeProperty+EncodeValue / BuildScopeFilter /
/// FormatLiteral). We do NOT duplicate the encoder.
///
/// FLAG (Desktop validation): the per-file PBIR shapes below are synthesised from the MS PBIR json-schemas
/// (the de-facto spec). Power BI Desktop is the authority - open a round-tripped report in Desktop to
/// validate before shipping to a client. Schema specifics flagged inline with FLAG.
/// </summary>
public sealed class PbirService
{
    private readonly SessionStore _sessions;
    private readonly ILogger<PbirService> _log;

    public PbirService(SessionStore sessions, ILogger<PbirService> log)
    {
        _sessions = sessions;
        _log = log;
    }

    // PBIR per-file JSON is UTF-8 (no BOM) and pretty-printed by Desktop; we keep it readable + relaxed-escaped
    // so a round-tripped file diffs cleanly against the original style.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    // the zip prefix a PBIR report lives under inside a .pbix. PBIP stores the same tree at the folder root.
    internal const string PbixReportPrefix = "Report/";
    internal const string DefinitionDir = "definition";
    internal const string PbirFile = "definition.pbir";

    // ============================================================================ format detection

    /// <summary>detect_report_format: classify a report SOURCE as legacy | pbir | pbip, robust on both a
    /// .pbix (a ZIP) and a PBIP project (a folder). The distinguishing artefacts:
    ///   - legacy .pbix : a "Report/Layout" zip entry (single JSON blob), no Report/definition/.
    ///   - pbir   .pbix : a "Report/definition/" tree (definition.pbir + report.json) inside the zip.
    ///   - pbip         : a folder containing a *.pbir pointer and/or a definition/ folder with report.json.
    /// Returns the classification plus the evidence so the caller can branch.</summary>
    public object DetectReportFormat(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required.");

        // ---- folder source: a PBIP project (or an exploded PBIR definition folder) ----
        if (Directory.Exists(path))
        {
            bool hasPbir = Directory.EnumerateFiles(path, "*.pbir", SearchOption.TopDirectoryOnly).Any();
            string defDir = Path.Combine(path, DefinitionDir);
            bool hasDefDir = Directory.Exists(defDir) && File.Exists(Path.Combine(defDir, "report.json"));
            // a *.Report folder inside a PBIP often holds the definition/ directly
            if (!hasDefDir)
            {
                foreach (var sub in Directory.EnumerateDirectories(path))
                    if (File.Exists(Path.Combine(sub, DefinitionDir, "report.json")) ||
                        Directory.EnumerateFiles(sub, "*.pbir", SearchOption.TopDirectoryOnly).Any())
                    { hasDefDir = true; break; }
            }
            if (hasPbir || hasDefDir)
                return new { format = "pbip", source = "folder", path, evidence = new { hasPbir, hasDefinitionFolder = hasDefDir } };
            // a folder with a legacy report.json+layout? not a PBIP - report unknown but lean folder.
            return new { format = "pbip", source = "folder", path, evidence = new { hasPbir = false, hasDefinitionFolder = false },
                note = "Folder has no *.pbir or definition/report.json; treated as a PBIP root but it may be empty - validate." };
        }

        if (!File.Exists(path)) throw new FileNotFoundException($"report source not found: {path}");

        // ---- .pbix source: a ZIP. Look for a Report/definition/ tree vs Report/Layout. ----
        using var zip = ZipFile.OpenRead(path);
        bool hasDefinition = false, hasLayout = false, hasPbirPointer = false;
        foreach (var e in zip.Entries)
        {
            string n = e.FullName.Replace('\\', '/');
            if (n.Equals("Report/Layout", StringComparison.OrdinalIgnoreCase)) hasLayout = true;
            if (n.StartsWith("Report/definition/", StringComparison.OrdinalIgnoreCase)) hasDefinition = true;
            if (n.Equals("Report/definition.pbir", StringComparison.OrdinalIgnoreCase)) hasPbirPointer = true;
        }
        if (hasDefinition || hasPbirPointer)
            return new { format = "pbir", source = "pbix", path, evidence = new { hasDefinitionFolder = hasDefinition, hasPbirPointer, hasLayout } };
        if (hasLayout)
            return new { format = "legacy", source = "pbix", path, evidence = new { hasLayout = true, hasDefinitionFolder = false } };
        throw new InvalidOperationException("This .pbix has neither Report/Layout nor Report/definition/ - not a recognised report part.");
    }

    // ============================================================================ in-memory model

    /// <summary>One parsed PBIR file: its logical path (relative to the report root, forward-slashed) and its
    /// parsed JSON. Files that arrive as raw bytes (rare in a report definition - images etc.) are kept as bytes
    /// so they round-trip byte-for-byte; JSON files are kept as a JsonObject and only re-serialised if dirtied.</summary>
    public sealed class PbirEntry
    {
        public required string RelPath { get; init; }     // e.g. "definition/pages/pages.json"
        public JsonObject? Json { get; set; }             // parsed JSON, when the file is JSON
        public byte[]? Raw { get; set; }                  // raw bytes, when the file is not JSON (preserve verbatim)
        public bool Dirty { get; set; }                   // re-emit only when dirtied
        public bool IsJson => Json != null;
    }

    /// <summary>The whole parsed PBIR tree in memory, indexed by relative path. Knows whether it came from a
    /// .pbix (so Save repacks the zip, stripping SecurityBindings) or a PBIP folder (so Save writes files).</summary>
    public sealed class PbirModel
    {
        public required string SourcePath { get; init; }
        public required bool FromPbix { get; init; }      // true: SourcePath is a .pbix zip; false: a folder
        public required string ReportRootInZip { get; init; }   // "Report/" for a pbix, "" for a folder
        // ordered dictionary of relPath -> entry (insertion order preserved for stable re-emit)
        public readonly Dictionary<string, PbirEntry> Entries = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<string> Order = new();

        public PbirEntry Add(string relPath, PbirEntry e)
        {
            relPath = relPath.Replace('\\', '/');
            if (!Entries.ContainsKey(relPath)) Order.Add(relPath);
            Entries[relPath] = e;
            return e;
        }

        public PbirEntry? Get(string relPath) =>
            Entries.TryGetValue(relPath.Replace('\\', '/'), out var e) ? e : null;

        public JsonObject Require(string relPath) =>
            (Get(relPath)?.Json) ?? throw new InvalidOperationException($"PBIR file '{relPath}' is missing or not JSON.");
    }

    // ============================================================================ open / read

    /// <summary>read_pbir: open a PBIR report (from a .pbix or a PBIP folder), parse the WHOLE definition tree
    /// into an in-memory model, register a session, and return a structured summary (pages + visual counts,
    /// bookmarks, dataset reference). The session id is reused by every mutate/save tool below.</summary>
    public object ReadPbir(string path)
    {
        var model = OpenModel(path);
        var session = new PbirSession
        {
            Id = _sessions.NewId("pbir"),
            SourcePath = path,
            Model = model,
        };
        _sessions.AddPbir(session);

        var summary = Summarise(model);
        return new
        {
            pbirSessionId = session.Id,
            path,
            format = model.FromPbix ? "pbir (pbix)" : "pbir (pbip folder)",
            summary.datasetReference,
            summary.activePageName,
            pages = summary.Pages,
            bookmarks = summary.Bookmarks,
            fileCount = model.Order.Count,
        };
    }

    /// <summary>Parse a PBIR report at <paramref name="path"/> (a .pbix or a PBIP folder) into a PbirModel.</summary>
    internal PbirModel OpenModel(string path)
    {
        var fmt = DetectReportFormat(path) as object;
        // surface a clear error if a legacy report was handed to the PBIR path
        var fmtNode = JsonSerializer.SerializeToNode(fmt) as JsonObject;
        string detected = (string?)fmtNode?["format"] ?? "";
        if (detected == "legacy")
            throw new InvalidOperationException("This is a legacy Report/Layout .pbix, not PBIR. Use the legacy report tools, or convert_legacy_to_pbir first.");

        if (Directory.Exists(path)) return OpenFolderModel(path);
        return OpenPbixModel(path);
    }

    private static PbirModel OpenFolderModel(string folder)
    {
        // the report root is the folder that CONTAINS definition/ (and the *.pbir pointer). Could be the
        // given folder, or a *.Report subfolder of a PBIP project.
        string root = folder;
        if (!Directory.Exists(Path.Combine(folder, DefinitionDir)))
        {
            foreach (var sub in Directory.EnumerateDirectories(folder))
                if (Directory.Exists(Path.Combine(sub, DefinitionDir))) { root = sub; break; }
        }

        var model = new PbirModel { SourcePath = folder, FromPbix = false, ReportRootInZip = "" };
        // the *.pbir pointer lives at the report root (not under definition/)
        foreach (var pbir in Directory.EnumerateFiles(root, "*.pbir", SearchOption.TopDirectoryOnly))
            AddFileToModel(model, root, pbir);
        // report.json sits at the report root in newer layouts, OR inside definition/; capture both.
        string reportJsonAtRoot = Path.Combine(root, "report.json");
        if (File.Exists(reportJsonAtRoot)) AddFileToModel(model, root, reportJsonAtRoot);

        string defDir = Path.Combine(root, DefinitionDir);
        if (Directory.Exists(defDir))
            foreach (var f in Directory.EnumerateFiles(defDir, "*", SearchOption.AllDirectories))
                AddFileToModel(model, root, f);

        if (model.Order.Count == 0)
            throw new InvalidOperationException($"No PBIR definition files found under '{root}'.");
        return model;
    }

    private static void AddFileToModel(PbirModel model, string root, string fullPath)
    {
        string rel = Path.GetRelativePath(root, fullPath).Replace('\\', '/');
        byte[] bytes = File.ReadAllBytes(fullPath);
        AddBytesToModel(model, rel, bytes);
    }

    private static PbirModel OpenPbixModel(string pbixPath)
    {
        var model = new PbirModel { SourcePath = pbixPath, FromPbix = true, ReportRootInZip = PbixReportPrefix };
        using var zip = ZipFile.OpenRead(pbixPath);
        foreach (var e in zip.Entries)
        {
            string full = e.FullName.Replace('\\', '/');
            if (!full.StartsWith(PbixReportPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (full.EndsWith("/")) continue;   // directory entry
            // logical (root-relative) path is the part after "Report/"
            string rel = full.Substring(PbixReportPrefix.Length);
            // only the report definition (definition/, definition.pbir, report.json, StaticResources) is PBIR;
            // everything else under Report/ (none, normally) we still carry so Save can re-emit it verbatim.
            byte[] bytes;
            using (var s = e.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
            AddBytesToModel(model, rel, bytes);
        }
        if (model.Order.Count == 0)
            throw new InvalidOperationException("No Report/ definition files found in this .pbix.");
        return model;
    }

    private static void AddBytesToModel(PbirModel model, string rel, byte[] bytes)
    {
        bool looksJson = rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                         || rel.EndsWith(".pbir", StringComparison.OrdinalIgnoreCase);
        if (looksJson)
        {
            try
            {
                string text = Utf8.GetString(StripBom(bytes));
                if (JsonNode.Parse(text) is JsonObject jo)
                {
                    model.Add(rel, new PbirEntry { RelPath = rel, Json = jo });
                    return;
                }
            }
            catch { /* not parseable JSON - fall through and keep raw */ }
        }
        model.Add(rel, new PbirEntry { RelPath = rel, Raw = bytes });
    }

    // ---- structured summary ----

    private sealed class PbirSummary
    {
        public JsonNode? datasetReference;
        public string? activePageName;
        public List<object> Pages = new();
        public List<object> Bookmarks = new();
    }

    private static PbirSummary Summarise(PbirModel model)
    {
        var s = new PbirSummary();
        // dataset reference from the *.pbir pointer
        var pbir = FindPbirPointer(model);
        s.datasetReference = pbir?["datasetReference"]?.DeepClone();

        var pagesIndex = model.Get("definition/pages/pages.json")?.Json;
        s.activePageName = (string?)pagesIndex?["activePageName"];
        var order = (pagesIndex?["pageOrder"] as JsonArray)?.Select(n => (string?)n).Where(n => n != null).Select(n => n!).ToList()
                    ?? new List<string>();

        // pages = every definition/pages/<name>/page.json, ordered by pageOrder where possible
        var pageEntries = model.Order
            .Where(p => p.StartsWith("definition/pages/", StringComparison.OrdinalIgnoreCase) && p.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var byName = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pageEntries)
        {
            var pj = model.Get(p)?.Json;
            if (pj == null) continue;
            string name = (string?)pj["name"] ?? PageNameFromPath(p);
            byName[name] = pj;
        }
        IEnumerable<string> ordered = order.Where(byName.ContainsKey).Concat(byName.Keys.Where(k => !order.Contains(k)));
        foreach (var name in ordered)
        {
            var pj = byName[name];
            int visualCount = model.Order.Count(e =>
                e.StartsWith($"definition/pages/{name}/visuals/", StringComparison.OrdinalIgnoreCase) &&
                e.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase));
            s.Pages.Add(new
            {
                name,
                displayName = (string?)pj["displayName"],
                width = NumOf(pj["width"]),
                height = NumOf(pj["height"]),
                displayOption = (string?)pj["displayOption"],
                visuals = visualCount,
            });
        }

        // bookmarks index
        var bmIndex = model.Get("definition/bookmarks/bookmarks.json")?.Json;
        if (bmIndex?["items"] is JsonArray items)
            foreach (var it in items)
                if (it is JsonObject io)
                    s.Bookmarks.Add(new { name = (string?)io["name"], displayName = (string?)io["displayName"] });
        return s;
    }

    /// <summary>list_pbir_pages: the ordered pages (name=GUID, displayName, size, visual count).</summary>
    public object ListPages(string pbirSessionId)
    {
        var model = _sessions.GetPbir(pbirSessionId).Model;
        return new { pages = Summarise(model).Pages };
    }

    /// <summary>get_pbir_page: a page's page.json plus the names of its visuals.</summary>
    public object GetPage(string pbirSessionId, string page)
    {
        var model = _sessions.GetPbir(pbirSessionId).Model;
        string name = ResolvePageName(model, page);
        var pj = model.Require($"definition/pages/{name}/page.json");
        var visuals = VisualNamesOnPage(model, name)
            .Select(v => new { name = v, visualType = VisualTypeOf(model, name, v) }).ToList();
        return new { name, page = pj.DeepClone(), visuals };
    }

    /// <summary>get_pbir_visual: one visual's visual.json (verbatim).</summary>
    public object GetVisual(string pbirSessionId, string page, string visual)
    {
        var model = _sessions.GetPbir(pbirSessionId).Model;
        string pname = ResolvePageName(model, page);
        string vname = ResolveVisualName(model, pname, visual);
        var vj = model.Require($"definition/pages/{pname}/visuals/{vname}/visual.json");
        return new { page = pname, visual = vname, visualJson = vj.DeepClone() };
    }

    // ============================================================================ save / round-trip

    /// <summary>save_pbir: re-emit ONLY the changed files, preserving every other entry byte-for-byte, GUID
    /// names intact, the DataModel untouched, and SecurityBindings stripped (same discipline as the legacy
    /// writer). For a .pbix this repacks the zip; for a PBIP folder it writes the dirtied files in place.</summary>
    public object Save(string pbirSessionId)
    {
        var session = _sessions.GetPbir(pbirSessionId);
        var model = session.Model;
        ValidateModel(model);   // catch a malformed save before we touch disk

        var changed = model.Order.Where(p => model.Entries[p].Dirty).ToList();
        if (model.FromPbix) SavePbix(model);
        else SaveFolder(model);

        foreach (var p in changed) model.Entries[p].Dirty = false;
        return new
        {
            ok = true,
            persistedToDisk = true,
            path = model.SourcePath,
            changedFiles = changed.ToArray(),
            changedCount = changed.Count,
            securityBindingsStripped = model.FromPbix,
            note = "Saved PBIR. The .pbix/PBIP must NOT be open in Power BI Desktop during save. FLAG: validate the round-tripped report in Desktop.",
        };
    }

    /// <summary>Serialise one entry to its on-disk bytes: dirtied JSON is re-serialised UTF-8 (no BOM);
    /// everything else is the original bytes (Raw, or a one-off serialisation of an untouched JsonObject).</summary>
    private static byte[] EntryBytes(PbirEntry e)
    {
        if (e.Raw != null && e.Json == null) return e.Raw;
        if (e.Json != null) return Utf8.GetBytes(e.Json.ToJsonString(JsonOpts));
        return e.Raw ?? Array.Empty<byte>();
    }

    private static void SaveFolder(PbirModel model)
    {
        // the report root is where definition/ lives. We resolved it on open into rel paths off that root.
        string root = model.SourcePath;
        if (!Directory.Exists(Path.Combine(root, DefinitionDir)))
            foreach (var sub in Directory.EnumerateDirectories(root))
                if (Directory.Exists(Path.Combine(sub, DefinitionDir))) { root = sub; break; }

        foreach (var rel in model.Order)
        {
            var e = model.Entries[rel];
            if (!e.Dirty) continue;   // re-emit only changed files
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, EntryBytes(e));
        }
    }

    private static void SavePbix(PbirModel model)
    {
        string pbixPath = model.SourcePath;
        string tmp = pbixPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var oic = StringComparison.OrdinalIgnoreCase;

        // logical-rel -> bytes for every Report/ entry we own (changed ones re-serialised, rest verbatim)
        var ownNew = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var ownExisting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in model.Order)
        {
            ownExisting.Add(PbixReportPrefix + rel);
            ownNew[PbixReportPrefix + rel] = EntryBytes(model.Entries[rel]);
        }

        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var src = ZipFile.OpenRead(pbixPath))
        using (var dstStream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var dst = new ZipArchive(dstStream, ZipArchiveMode.Create))
        {
            foreach (var entry in src.Entries)
            {
                string full = entry.FullName;
                if (string.Equals(full, "SecurityBindings", oic)) continue;   // drop the signature
                if (full.EndsWith("/")) continue;
                written.Add(full);

                if (ownNew.TryGetValue(full, out var nb))
                {
                    var ne = dst.CreateEntry(full, CompressionLevel.Optimal);
                    using var s = ne.Open(); s.Write(nb, 0, nb.Length);
                }
                else if (string.Equals(full, "[Content_Types].xml", oic))
                {
                    string ct = RemoveSecurityBindingsOverride(ReadEntryText(entry));
                    var ne = dst.CreateEntry(full, CompressionLevel.Optimal);
                    using var s = ne.Open();
                    byte[] cb = Utf8.GetBytes(ct);
                    s.Write(cb, 0, cb.Length);
                }
                else
                {
                    // DataModel and every other part: copied through UNTOUCHED (NoCompression for DataModel).
                    var level = string.Equals(full, "DataModel", oic) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                    var ne = dst.CreateEntry(full, level);
                    using var os = entry.Open(); using var ns = ne.Open(); os.CopyTo(ns);
                }
            }
            // any NEW Report/ files we created (e.g. a fresh page folder) that weren't already in the zip
            foreach (var kv in ownNew)
                if (!written.Contains(kv.Key))
                {
                    var ne = dst.CreateEntry(kv.Key, CompressionLevel.Optimal);
                    using var s = ne.Open(); s.Write(kv.Value, 0, kv.Value.Length);
                }
        }
        File.Delete(pbixPath);
        File.Move(tmp, pbixPath);
    }

    /// <summary>Fault check: a saveable PBIR model must have a *.pbir pointer, a report.json, a pages index,
    /// and every page folder must carry a page.json with a name. Catches a malformed tree BEFORE we overwrite
    /// the source.</summary>
    private static void ValidateModel(PbirModel model)
    {
        if (FindPbirPointer(model) is null)
            throw new InvalidOperationException("PBIR model is malformed: no *.pbir pointer (definition.pbir).");
        if (FindReportJson(model) is null)
            throw new InvalidOperationException("PBIR model is malformed: no report.json.");
        var pagesIdx = model.Get("definition/pages/pages.json")?.Json;
        if (pagesIdx is null)
            throw new InvalidOperationException("PBIR model is malformed: no definition/pages/pages.json.");
        if (pagesIdx["pageOrder"] is not JsonArray)
            throw new InvalidOperationException("PBIR model is malformed: pages.json has no pageOrder array.");
        foreach (var rel in model.Order)
            if (rel.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase))
            {
                var pj = model.Entries[rel].Json;
                if (pj is null || string.IsNullOrWhiteSpace((string?)pj["name"]))
                    throw new InvalidOperationException($"PBIR model is malformed: {rel} has no page name.");
            }
    }

    // ============================================================================ authoring (reuses engine)

    /// <summary>create_pbir_page: a NEW page = a new GUID folder definition/pages/&lt;guid&gt;/page.json plus an
    /// update to pages.json (append the GUID to pageOrder). width/height default to the 16:9 1280x720 canvas.</summary>
    public object CreatePage(string pbirSessionId, string displayName, int? width = null, int? height = null)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("displayName is required.");
        var session = _sessions.GetPbir(pbirSessionId);
        var model = session.Model;

        string name = NewGuid();
        int w = width ?? 1280, h = height ?? 720;
        var page = new JsonObject
        {
            // FLAG (Desktop validation): the page.json schema key for the page background/displayOption set has
            // shifted across versions; "name" (GUID), "displayName", "displayOption", "width"/"height" are stable.
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/page/1.0.0/schema.json",
            ["name"] = name,
            ["displayName"] = displayName,
            ["displayOption"] = "FitToPage",
            ["height"] = h,
            ["width"] = w,
        };
        model.Add($"definition/pages/{name}/page.json", new PbirEntry
        { RelPath = $"definition/pages/{name}/page.json", Json = page, Dirty = true });

        // update the pages index (create one if the report somehow lacks it)
        var idxEntry = model.Get("definition/pages/pages.json");
        if (idxEntry?.Json is null)
        {
            var fresh = new JsonObject { ["pageOrder"] = new JsonArray(), ["activePageName"] = name };
            idxEntry = model.Add("definition/pages/pages.json", new PbirEntry
            { RelPath = "definition/pages/pages.json", Json = fresh });
        }
        var idx = idxEntry.Json!;
        var order = idx["pageOrder"] as JsonArray ?? (JsonArray)(idx["pageOrder"] = new JsonArray());
        order.Add(name);
        if (string.IsNullOrWhiteSpace((string?)idx["activePageName"])) idx["activePageName"] = name;
        idxEntry.Dirty = true;

        session.MarkDirty();
        return new { ok = true, pageName = name, displayName, width = w, height = h, pageOrderCount = order.Count };
    }

    /// <summary>add_pbir_visual: a NEW visual = a new GUID folder definition/pages/&lt;page&gt;/visuals/&lt;guid&gt;/visual.json.
    /// The visual's query (projections + prototypeQuery) is built by the SHARED engine builder (BuildSingleVisual),
    /// so a PBIR visual carries the exact same semantic-query JSON as a legacy one - just relocated under
    /// visual.visualType / visual.query. fields = the role bindings (Category/Y/Values/Rows/...).</summary>
    public object AddVisual(string pbirSessionId, string page, string visualType,
        IReadOnlyList<FieldBinding> fields, double x, double y, double width, double height, string? title = null)
    {
        var session = _sessions.GetPbir(pbirSessionId);
        var model = session.Model;
        string pname = ResolvePageName(model, page);

        // reuse the legacy single-visual builder for projections + prototypeQuery (the format-agnostic query JSON).
        var sv = ReportService.BuildSingleVisual(visualType, fields, title);

        string vname = NewGuid();
        // FLAG (Desktop validation): PBIR puts the query under visual.query.queryState (role -> projections) and
        // visual.query.sortDefinition; the legacy projections/prototypeQuery shape is accepted by Desktop as the
        // visual's query container. We carry BOTH the projections (queryState source) and the prototypeQuery so a
        // round-trip is lossless; Desktop re-materialises queryState from these on open.
        var visual = new JsonObject
        {
            ["visualType"] = visualType,
            ["query"] = new JsonObject
            {
                ["queryState"] = ProjectionsToQueryState(sv["projections"] as JsonObject),
                ["projections"] = (sv["projections"] as JsonObject)?.DeepClone() ?? new JsonObject(),
                ["prototypeQuery"] = (sv["prototypeQuery"] as JsonObject)?.DeepClone() ?? new JsonObject(),
            },
            ["drillFilterOtherVisuals"] = true,
        };
        // hoist any formatting the builder produced (slicer mode/header, title) into the PBIR slots:
        //   singleVisual.objects        -> visual.objects (data-formatting cards)
        //   singleVisual.vcObjects      -> visual.visualContainerObjects (chrome: title/background/border)
        if (sv["objects"] is JsonObject objs) visual["objects"] = objs.DeepClone();
        if (sv["vcObjects"] is JsonObject vco) visual["visualContainerObjects"] = vco.DeepClone();

        var visualJson = new JsonObject
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/visualContainer/1.0.0/schema.json",
            ["name"] = vname,
            ["position"] = new JsonObject
            {
                ["x"] = x, ["y"] = y, ["z"] = 0, ["width"] = width, ["height"] = height, ["tabOrder"] = 0,
            },
            ["visual"] = visual,
        };

        model.Add($"definition/pages/{pname}/visuals/{vname}/visual.json", new PbirEntry
        { RelPath = $"definition/pages/{pname}/visuals/{vname}/visual.json", Json = visualJson, Dirty = true });

        session.MarkDirty();
        return new { ok = true, page = pname, visualName = vname, visualType, fields = fields.Count };
    }

    /// <summary>set_pbir_visual_format: merge one formatting property into a PBIR visual's objects tree, REUSING
    /// the legacy MergeProperty + EncodeValue engine. bucket = objects (data cards) | visualContainerObjects
    /// (chrome). A value that is already structured PBI JSON (a nested object/array, an expr/solid, a measure-bound
    /// shorthand) is written VERBATIM - the Wave M behaviour - while a scalar is encoded by property kind.</summary>
    public object SetVisualFormat(string pbirSessionId, string page, string visual,
        string card, string property, JsonNode? value, string bucket = "objects")
    {
        var session = _sessions.GetPbir(pbirSessionId);
        var model = session.Model;
        string pname = ResolvePageName(model, page);
        string vname = ResolveVisualName(model, pname, visual);
        var entry = model.Get($"definition/pages/{pname}/visuals/{vname}/visual.json")
                    ?? throw new InvalidOperationException($"visual '{visual}' not found on page '{page}'.");
        var vj = entry.Json ?? throw new InvalidOperationException("visual.json is not JSON.");

        string bk = bucket?.ToLowerInvariant() == "visualcontainerobjects" ? "visualContainerObjects" : "objects";
        var visualNode = vj["visual"] as JsonObject ?? (JsonObject)(vj["visual"] = new JsonObject());
        var bag = visualNode[bk] as JsonObject ?? (JsonObject)(visualNode[bk] = new JsonObject());

        // REUSE the exact legacy merge+encode engine against the PBIR objects tree (same { card:[{properties}] } shape).
        var (o, pr) = ReportService.MergeProperty(bag, card, property, value);
        entry.Dirty = true;
        session.MarkDirty();
        return new { ok = true, page = pname, visual = vname, bucket = bk, changed = $"{bk}.{o}.{pr}" };
    }

    /// <summary>add_pbir_filter: write a filter into the PBIR filters array at report | page | visual scope,
    /// REUSING the legacy BuildScopeFilter + FormatLiteral engine (the exact FilterContainer shape: name +
    /// expression + filter{From/Where} + type). kind=categorical|in|values -> a values list; else a
    /// comparison/blank filter (op=gt|gte|lt|lte|eq|ne|isblank|isnotblank).</summary>
    public object AddFilter(string pbirSessionId, string scope, string? page, string? visual,
        string table, string field, string kind, string fieldKind, string? op, IReadOnlyList<string>? values, string valueType)
    {
        var session = _sessions.GetPbir(pbirSessionId);
        var model = session.Model;

        // build the SAME filter object the legacy engine writes (format-agnostic).
        var filterObj = ReportService.BuildScopeFilter(table, field, kind, fieldKind, op, values, valueType);

        var (filtersArr, entry) = ResolveFilterScope(model, scope, page, visual);
        filtersArr.Add(filterObj);
        entry.Dirty = true;
        session.MarkDirty();
        return new { ok = true, scope, page, visual, filter = $"{table}[{field}]", type = (string?)filterObj["type"] };
    }

    /// <summary>Locate (and create if absent) the filters JsonArray for a scope, plus the model entry that
    /// owns it (so we can mark it dirty). report -> report.json.filterConfig.filters; page -> page.json.filterConfig
    /// .filters; visual -> visual.json.filterConfig.filters. FLAG (Desktop validation): a fresh visual may carry
    /// no filterConfig until the filter pane is expanded; we create one - Desktop accepts an authored filterConfig.</summary>
    private (JsonArray filters, PbirEntry entry) ResolveFilterScope(PbirModel model, string scope, string? page, string? visual)
    {
        PbirEntry entry;
        JsonObject host;
        switch ((scope ?? "").ToLowerInvariant())
        {
            case "report":
                entry = FindReportJsonEntry(model) ?? throw new InvalidOperationException("report.json not found.");
                host = entry.Json!;
                break;
            case "page":
            {
                string pname = ResolvePageName(model, page ?? throw new ArgumentException("page is required for a page-scope filter."));
                entry = model.Get($"definition/pages/{pname}/page.json")!;
                host = entry.Json!;
                break;
            }
            case "visual":
            {
                string pname = ResolvePageName(model, page ?? throw new ArgumentException("page is required for a visual-scope filter."));
                string vname = ResolveVisualName(model, pname, visual ?? throw new ArgumentException("visual is required for a visual-scope filter."));
                entry = model.Get($"definition/pages/{pname}/visuals/{vname}/visual.json")!;
                host = entry.Json!;
                break;
            }
            default:
                throw new ArgumentException($"unknown scope '{scope}' (use report|page|visual).");
        }
        var fc = host["filterConfig"] as JsonObject ?? (JsonObject)(host["filterConfig"] = new JsonObject());
        var arr = fc["filters"] as JsonArray ?? (JsonArray)(fc["filters"] = new JsonArray());
        return (arr, entry);
    }

    /// <summary>add_pbir_bookmark / set_pbir_bookmark: write a bookmark file definition/bookmarks/&lt;guid&gt;.bookmark
    /// .json holding an explorationState (which page is active + each visual's display.mode), and add/refresh its
    /// entry in the bookmarks.json index. When the named bookmark already exists it is overwritten (set). hidden =
    /// the visuals to hide; everything else on the page is shown.</summary>
    public object SetBookmark(string pbirSessionId, string displayName, string page,
        IReadOnlyCollection<string> hiddenVisuals, string? existingName = null)
    {
        if (string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("displayName is required.");
        var session = _sessions.GetPbir(pbirSessionId);
        var model = session.Model;
        string pname = ResolvePageName(model, page);

        // resolve/allocate the bookmark GUID (set overwrites, add allocates)
        string name = existingName ?? NewGuid();
        var hide = new HashSet<string>(hiddenVisuals, StringComparer.Ordinal);

        var visualsState = new JsonObject();
        foreach (var v in VisualNamesOnPage(model, pname))
            visualsState[v] = new JsonObject
            {
                ["singleVisual"] = new JsonObject
                {
                    ["display"] = new JsonObject { ["mode"] = hide.Contains(v) ? "hidden" : "show" },
                },
            };

        // FLAG (Desktop validation): the bookmark explorationState shape (activeSection + sections{<page>{visualContainers}})
        // mirrors the legacy bookmark explorationState; PBIR stores one bookmark per file with a pageBinding/page name.
        var bookmark = new JsonObject
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/bookmark/1.0.0/schema.json",
            ["name"] = name,
            ["displayName"] = displayName,
            ["explorationState"] = new JsonObject
            {
                ["version"] = "1.0",
                ["activeSection"] = pname,
                ["sections"] = new JsonObject
                {
                    [pname] = new JsonObject { ["visualContainers"] = visualsState },
                },
            },
        };
        string rel = $"definition/bookmarks/{name}.bookmark.json";
        var e = model.Get(rel);
        if (e?.Json != null) { e.Json = bookmark; e.Dirty = true; }
        else model.Add(rel, new PbirEntry { RelPath = rel, Json = bookmark, Dirty = true });

        // bookmarks.json index
        var idxEntry = model.Get("definition/bookmarks/bookmarks.json");
        if (idxEntry?.Json is null)
            idxEntry = model.Add("definition/bookmarks/bookmarks.json", new PbirEntry
            { RelPath = "definition/bookmarks/bookmarks.json", Json = new JsonObject { ["items"] = new JsonArray() } });
        var items = idxEntry.Json!["items"] as JsonArray ?? (JsonArray)(idxEntry.Json!["items"] = new JsonArray());
        JsonObject? indexItem = items.OfType<JsonObject>().FirstOrDefault(i => (string?)i["name"] == name);
        if (indexItem == null) { indexItem = new JsonObject { ["name"] = name }; items.Add(indexItem); }
        indexItem["displayName"] = displayName;
        idxEntry.Dirty = true;

        session.MarkDirty();
        return new { ok = true, name, displayName, page = pname,
            hiddenVisuals = hide.ToArray(), action = existingName == null ? "added" : "set" };
    }

    // ============================================================================ legacy -> PBIR

    /// <summary>convert_legacy_to_pbir: BEST-EFFORT explode a legacy Report/Layout (a single JSON blob) into a
    /// PBIR definition tree - pages -> page.json, visualContainers -> visual.json, generating GUID page/visual
    /// names. Writes the tree to a target folder (default: alongside the .pbix as &lt;name&gt;.Report). FLAG: this is
    /// a best-effort structural conversion; Power BI Desktop MUST open and validate/upgrade the result. We do not
    /// attempt pbir-&gt;legacy (deferred).</summary>
    public object ConvertLegacyToPbir(string legacyPbixOrLayoutPath, string? targetFolder = null)
    {
        // accept a .pbix (read Report/Layout from the zip) or a raw Layout JSON file (test-friendly).
        JsonObject layout = LoadLegacyLayout(legacyPbixOrLayoutPath);
        string target = targetFolder ?? DefaultConversionTarget(legacyPbixOrLayoutPath);
        Directory.CreateDirectory(target);

        var model = new PbirModel { SourcePath = target, FromPbix = false, ReportRootInZip = "" };

        // ---- definition.pbir pointer (FLAG: datasetReference left as a byPath placeholder; Desktop rebinds) ----
        model.Add(PbirFile, new PbirEntry
        {
            RelPath = PbirFile, Dirty = true,
            Json = new JsonObject
            {
                ["version"] = "1.0",
                ["datasetReference"] = new JsonObject
                {
                    ["byPath"] = new JsonObject { ["path"] = "../Model" },
                },
            },
        });

        // ---- report.json (theme/settings/report-level filters/resourcePackages carried over best-effort) ----
        var reportJson = new JsonObject
        {
            ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/report/1.0.0/schema.json",
            ["themeCollection"] = new JsonObject { ["baseTheme"] = new JsonObject { ["name"] = "CY24SU10" } },
        };
        if (TryParseStringJsonArray(layout["filters"]) is JsonArray reportFilters && reportFilters.Count > 0)
            reportJson["filterConfig"] = new JsonObject { ["filters"] = reportFilters };
        if (layout["resourcePackages"] is JsonArray rp) reportJson["resourcePackages"] = rp.DeepClone();
        model.Add("report.json", new PbirEntry { RelPath = "report.json", Json = reportJson, Dirty = true });

        // ---- pages ----
        var pageOrder = new JsonArray();
        var sections = layout["sections"] as JsonArray ?? new JsonArray();
        // preserve the author's ordinal order
        var orderedSections = sections.OfType<JsonObject>()
            .OrderBy(s => IntOf(s["ordinal"]) ?? 0).ToList();
        string? activePage = null;
        foreach (var sec in orderedSections)
        {
            string pname = NewGuid();
            pageOrder.Add(pname);
            activePage ??= pname;

            var page = new JsonObject
            {
                ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/page/1.0.0/schema.json",
                ["name"] = pname,
                ["displayName"] = (string?)sec["displayName"] ?? (string?)sec["name"] ?? "Page",
                ["displayOption"] = DisplayOptionName(IntOf(sec["displayOption"]) ?? 0),
                ["height"] = NumOf(sec["height"]) ?? 720,
                ["width"] = NumOf(sec["width"]) ?? 1280,
            };
            if (TryParseStringJsonArray(sec["filters"]) is JsonArray pageFilters && pageFilters.Count > 0)
                page["filterConfig"] = new JsonObject { ["filters"] = pageFilters };
            model.Add($"definition/pages/{pname}/page.json", new PbirEntry
            { RelPath = $"definition/pages/{pname}/page.json", Json = page, Dirty = true });

            // ---- visuals (each legacy visualContainer.config is a STRINGIFIED { name, layouts, singleVisual }) ----
            var containers = sec["visualContainers"] as JsonArray ?? new JsonArray();
            foreach (var vcNode in containers.OfType<JsonObject>())
            {
                JsonObject? co = TryParseStringObject(vcNode["config"]);
                var sv = co?["singleVisual"] as JsonObject;
                string vname = NewGuid();

                var pos = ExtractPosition(co, vcNode);
                var visual = new JsonObject
                {
                    ["visualType"] = (string?)sv?["visualType"] ?? "card",
                    ["drillFilterOtherVisuals"] = (bool?)sv?["drillFilterOtherVisuals"] ?? true,
                };
                // carry the query (projections + prototypeQuery) and formatting trees verbatim.
                if (sv?["projections"] is JsonObject || sv?["prototypeQuery"] is JsonObject)
                {
                    var query = new JsonObject();
                    if (sv?["projections"] is JsonObject projs)
                    {
                        query["queryState"] = ProjectionsToQueryState(projs);
                        query["projections"] = projs.DeepClone();
                    }
                    if (sv?["prototypeQuery"] is JsonObject proto) query["prototypeQuery"] = proto.DeepClone();
                    visual["query"] = query;
                }
                if (sv?["objects"] is JsonObject vObjs) visual["objects"] = vObjs.DeepClone();
                if (sv?["vcObjects"] is JsonObject vVco) visual["visualContainerObjects"] = vVco.DeepClone();

                var visualJson = new JsonObject
                {
                    ["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/visualContainer/1.0.0/schema.json",
                    ["name"] = vname,
                    ["position"] = pos,
                    ["visual"] = visual,
                };
                if (TryParseStringJsonArray(vcNode["filters"]) is JsonArray vFilters && vFilters.Count > 0)
                    visualJson["filterConfig"] = new JsonObject { ["filters"] = vFilters };

                model.Add($"definition/pages/{pname}/visuals/{vname}/visual.json", new PbirEntry
                { RelPath = $"definition/pages/{pname}/visuals/{vname}/visual.json", Json = visualJson, Dirty = true });
            }
        }
        model.Add("definition/pages/pages.json", new PbirEntry
        {
            RelPath = "definition/pages/pages.json", Dirty = true,
            Json = new JsonObject { ["pageOrder"] = pageOrder, ["activePageName"] = activePage ?? "" },
        });

        SaveFolder(model);
        foreach (var p in model.Order) model.Entries[p].Dirty = false;

        // register a session over the freshly-written folder so the caller can immediately read/mutate it
        var openModel = OpenFolderModel(target);
        var session = new PbirSession { Id = _sessions.NewId("pbir"), SourcePath = target, Model = openModel };
        _sessions.AddPbir(session);

        return new
        {
            ok = true,
            pbirSessionId = session.Id,
            targetFolder = target,
            pages = pageOrder.Count,
            files = model.Order.Count,
            bestEffort = true,
            note = "BEST-EFFORT legacy->PBIR explode. FLAG: open in Power BI Desktop to validate/upgrade - GUID names are fresh, the datasetReference is a placeholder, and queryState is re-materialised by Desktop. pbir->legacy is not implemented (deferred).",
        };
    }

    // ============================================================================ small helpers

    private static readonly UTF8Encoding Utf8 = new(false);

    private static string NewGuid() => Guid.NewGuid().ToString();

    /// <summary>Read any JSON number (int OR double) as a double safely - the explicit (double?) cast on a
    /// JsonNode is strict about the stored numeric kind and throws on an int, so route every numeric read here.</summary>
    private static double? NumOf(JsonNode? n)
    {
        if (n is null) return null;
        try { return n.GetValue<double>(); }
        catch { try { return n.GetValue<int>(); } catch { return double.TryParse(n.ToString(), out var d) ? d : (double?)null; } }
    }

    private static int? IntOf(JsonNode? n)
    {
        var d = NumOf(n);
        return d.HasValue ? (int)d.Value : (int?)null;
    }

    private static byte[] StripBom(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return b[3..];
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

    private static JsonObject? FindPbirPointer(PbirModel model) =>
        model.Order.Where(p => p.EndsWith(".pbir", StringComparison.OrdinalIgnoreCase))
            .Select(p => model.Entries[p].Json).FirstOrDefault(j => j != null);

    private static JsonObject? FindReportJson(PbirModel model) => FindReportJsonEntry(model)?.Json;

    private static PbirEntry? FindReportJsonEntry(PbirModel model) =>
        model.Get("report.json") ?? model.Get("definition/report.json")
        ?? model.Order.Where(p => p.EndsWith("/report.json", StringComparison.OrdinalIgnoreCase) || p.Equals("report.json", StringComparison.OrdinalIgnoreCase))
            .Select(p => model.Entries[p]).FirstOrDefault(e => e.Json != null);

    private static string PageNameFromPath(string rel)
    {
        // definition/pages/<name>/page.json -> <name>
        var parts = rel.Split('/');
        int i = Array.FindIndex(parts, p => p.Equals("pages", StringComparison.OrdinalIgnoreCase));
        return (i >= 0 && i + 1 < parts.Length) ? parts[i + 1] : rel;
    }

    private static string ResolvePageName(PbirModel model, string pageRef)
    {
        // accept the GUID name OR the displayName
        foreach (var rel in model.Order)
        {
            if (!rel.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase)) continue;
            var pj = model.Entries[rel].Json;
            if (pj == null) continue;
            string name = (string?)pj["name"] ?? PageNameFromPath(rel);
            if (name == pageRef || (string?)pj["displayName"] == pageRef) return name;
        }
        throw new InvalidOperationException($"Page '{pageRef}' not found (match by GUID name or displayName).");
    }

    private static IEnumerable<string> VisualNamesOnPage(PbirModel model, string pageName)
    {
        string prefix = $"definition/pages/{pageName}/visuals/";
        foreach (var rel in model.Order)
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && rel.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase))
            {
                var vj = model.Entries[rel].Json;
                yield return (string?)vj?["name"] ?? rel.Substring(prefix.Length).Split('/')[0];
            }
    }

    private static string ResolveVisualName(PbirModel model, string pageName, string visualRef)
    {
        string prefix = $"definition/pages/{pageName}/visuals/";
        foreach (var rel in model.Order)
            if (rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && rel.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase))
            {
                var vj = model.Entries[rel].Json;
                string name = (string?)vj?["name"] ?? rel.Substring(prefix.Length).Split('/')[0];
                if (name == visualRef) return name;
            }
        throw new InvalidOperationException($"Visual '{visualRef}' not found on page '{pageName}'.");
    }

    private static string? VisualTypeOf(PbirModel model, string pageName, string visualName)
    {
        var vj = model.Get($"definition/pages/{pageName}/visuals/{visualName}/visual.json")?.Json;
        return (string?)(vj?["visual"]?["visualType"]);
    }

    /// <summary>Convert a legacy projections object { role: [{queryRef}] } into the PBIR queryState shape
    /// { role: { projections: [{ field:{...}, queryRef }] } }. FLAG (Desktop validation): the exact queryState
    /// projection shape is version-sensitive; we keep the queryRef so Desktop can rebind, and also carry the
    /// original projections + prototypeQuery alongside for a lossless round-trip.</summary>
    private static JsonObject ProjectionsToQueryState(JsonObject? projections)
    {
        var qs = new JsonObject();
        if (projections == null) return qs;
        foreach (var (role, refs) in projections)
        {
            var arr = new JsonArray();
            if (refs is JsonArray ra)
                foreach (var r in ra)
                    if (r is JsonObject ro && (string?)ro["queryRef"] is string q)
                        arr.Add(new JsonObject { ["queryRef"] = q });
            qs[role] = new JsonObject { ["projections"] = arr };
        }
        return qs;
    }

    private static string DisplayOptionName(int code) => code switch
    {
        0 => "FitToPage",
        1 => "FitToWidth",
        2 => "ActualSize",
        _ => "FitToPage",
    };

    private static JsonObject ExtractPosition(JsonObject? co, JsonObject vcNode)
    {
        // legacy position lives at config.layouts[0].position; fall back to the container's own x/y/w/h.
        if (co?["layouts"] is JsonArray la && la.Count > 0 && la[0] is JsonObject l0 && l0["position"] is JsonObject p)
            return new JsonObject
            {
                ["x"] = NumOf(p["x"]) ?? 0, ["y"] = NumOf(p["y"]) ?? 0, ["z"] = NumOf(p["z"]) ?? 0,
                ["width"] = NumOf(p["width"]) ?? 100, ["height"] = NumOf(p["height"]) ?? 100,
                ["tabOrder"] = IntOf(p["tabOrder"]) ?? 0,
            };
        return new JsonObject
        {
            ["x"] = NumOf(vcNode["x"]) ?? 0, ["y"] = NumOf(vcNode["y"]) ?? 0, ["z"] = NumOf(vcNode["z"]) ?? 0,
            ["width"] = NumOf(vcNode["width"]) ?? 100, ["height"] = NumOf(vcNode["height"]) ?? 100,
            ["tabOrder"] = 0,
        };
    }

    private static JsonObject? TryParseStringObject(JsonNode? node)
    {
        if (node is JsonObject jo) return jo;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            return JsonNode.Parse(s) as JsonObject;
        return null;
    }

    private static JsonArray? TryParseStringJsonArray(JsonNode? node)
    {
        if (node is JsonArray ja) return ja.DeepClone() as JsonArray;
        if (node is JsonValue jv && jv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
            return JsonNode.Parse(s) as JsonArray;
        return null;
    }

    private static JsonObject LoadLegacyLayout(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"legacy report source not found: {path}");
        if (path.EndsWith(".pbix", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("Report/Layout")
                        ?? throw new InvalidOperationException("This .pbix has no Report/Layout - it may already be PBIR.");
            byte[] bytes;
            using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
            string text = new UnicodeEncoding(false, true).GetString(StripBom(bytes));
            return JsonNode.Parse(text) as JsonObject
                   ?? throw new InvalidOperationException("Report/Layout root is not a JSON object.");
        }
        // a raw Layout JSON file (UTF-8 or UTF-16) - test-friendly
        byte[] raw = File.ReadAllBytes(path);
        string txt = raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE
            ? new UnicodeEncoding(false, true).GetString(StripBom(raw))
            : Utf8.GetString(StripBom(raw));
        return JsonNode.Parse(txt) as JsonObject
               ?? throw new InvalidOperationException("Layout file root is not a JSON object.");
    }

    private static string DefaultConversionTarget(string legacyPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(legacyPath)) ?? ".";
        string baseName = Path.GetFileNameWithoutExtension(legacyPath);
        return Path.Combine(dir, baseName + ".Report");
    }

    // ---- test-only seams: build/parse a synthetic PBIR model without touching disk ----

    internal PbirModel OpenModelForTest(string path) => OpenModel(path);
    internal static PbirModel OpenFolderModelForTest(string folder) => OpenFolderModel(folder);
    internal static PbirModel OpenPbixModelForTest(string pbix) => OpenPbixModel(pbix);
    internal static byte[] EntryBytesForTest(PbirEntry e) => EntryBytes(e);
    internal static JsonObject ProjectionsToQueryStateForTest(JsonObject? p) => ProjectionsToQueryState(p);
}
