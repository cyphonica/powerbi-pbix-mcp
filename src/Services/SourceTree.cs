using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>
/// unpack_to_source / pbix_diff - the "source control" pair. Unpack turns a model + its .pbix report
/// into a DETERMINISTIC, text-only tree (Model/definition TMDL via the official serializer +
/// canonicalised Report files) that commits and diffs cleanly under git: same inputs, byte-identical
/// output, every run. Diff is the matching semantic comparison (files, TMDL tables/measures, report
/// pages, M queries) between two .pbix files or two unpacked trees. Pure file/model logic - no live
/// engine beyond the TOM model handed in.
/// </summary>
public static class SourceTree
{
    private static readonly UTF8Encoding Utf8 = new(false);   // git-friendly: UTF-8, no BOM

    // fixed serializer options so the same JSON always canonicalises to the same bytes
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    // marker proving a folder is OUR previous unpack (fixed content - no timestamps anywhere in outputs)
    private const string Marker = ".superbi-source";
    private const string MarkerContent = "superbi unpack v1";

    // ============================================================================ unpack

    /// <summary>Unpack <paramref name="model"/> (TMDL) + the report half of <paramref name="pbixPath"/>
    /// into a deterministic text source tree at <paramref name="outputFolder"/>.</summary>
    public static object Unpack(TOM.Model model, string pbixPath, string outputFolder)
    {
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        PrepareOutputFolder(outputFolder);

        // ----- model half: official TMDL serializer (stable output for a given model) -----
        string modelDir = Path.Combine(outputFolder, "Model", "definition");
        TOM.TmdlSerializer.SerializeModelToFolder(model, modelDir);
        var modelFiles = Directory.GetFiles(modelDir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(outputFolder, f).Replace('\\', '/'))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        // ----- report half: canonicalised text from the .pbix zip -----
        string format = DetectPbixReportFormat(pbixPath);
        string reportDir = Path.Combine(outputFolder, "Report");
        var reportFiles = format == "legacy"
            ? UnpackLegacyReport(pbixPath, reportDir)
            : UnpackPbirReport(pbixPath, reportDir);
        reportFiles.Sort(StringComparer.Ordinal);

        File.WriteAllText(Path.Combine(outputFolder, Marker), MarkerContent, Utf8);

        return new
        {
            ok = true,
            outputFolder,
            format,
            modelFiles = modelFiles.Count,
            reportFiles = reportFiles.Count,
            modelSample = modelFiles.Take(20).ToArray(),
            reportSample = reportFiles.Take(20).ToArray(),
        };
    }

    /// <summary>Never recursively delete a folder we did not create: wipe only when the target is empty
    /// or carries the marker file from a previous unpack.</summary>
    private static void PrepareOutputFolder(string outputFolder)
    {
        if (Directory.Exists(outputFolder))
        {
            bool empty = !Directory.EnumerateFileSystemEntries(outputFolder).Any();
            bool ours = File.Exists(Path.Combine(outputFolder, Marker));
            if (!empty && !ours)
                throw new InvalidOperationException(
                    $"output folder exists and is not a previous unpack: {outputFolder}");
            Directory.Delete(outputFolder, true);
        }
        Directory.CreateDirectory(outputFolder);
    }

    /// <summary>Classify the report half of a .pbix zip - same evidence as PbirService.DetectReportFormat
    /// (which is instance-bound; this static replica keeps SourceTree service-free).</summary>
    private static string DetectPbixReportFormat(string pbixPath)
    {
        using var zip = ZipFile.OpenRead(pbixPath);
        bool hasLayout = false, hasDefinition = false, hasPointer = false;
        foreach (var e in zip.Entries)
        {
            string n = e.FullName.Replace('\\', '/');
            if (n.Equals("Report/Layout", StringComparison.OrdinalIgnoreCase)) hasLayout = true;
            if (n.StartsWith("Report/definition/", StringComparison.OrdinalIgnoreCase)) hasDefinition = true;
            if (n.Equals("Report/definition.pbir", StringComparison.OrdinalIgnoreCase)) hasPointer = true;
        }
        if (hasDefinition || hasPointer) return "pbir";
        if (hasLayout) return "legacy";
        throw new InvalidOperationException("This .pbix has neither Report/Layout nor Report/definition/ - not a recognised report part.");
    }

    /// <summary>Legacy Report/Layout (one UTF-16-LE blob) -&gt; Report/Layout.json (pretty UTF-8, no BOM)
    /// plus every StaticResources entry byte-verbatim.</summary>
    private static List<string> UnpackLegacyReport(string pbixPath, string reportDir)
    {
        var written = new List<string>();
        using var zip = ZipFile.OpenRead(pbixPath);

        var layout = zip.GetEntry("Report/Layout")
                     ?? throw new InvalidOperationException("legacy pbix has no Report/Layout entry.");
        byte[] lb = ReadEntry(layout);
        int off = (lb.Length >= 2 && lb[0] == 0xFF && lb[1] == 0xFE) ? 2 : 0;   // strip UTF-16 BOM
        string text = new UnicodeEncoding(false, false).GetString(lb, off, lb.Length - off);
        var node = JsonNode.Parse(text) ?? throw new InvalidOperationException("Report/Layout is not valid JSON.");
        WriteText(reportDir, "Layout.json", node.ToJsonString(JsonOpts), written);

        var statics = zip.Entries
            .Where(e => e.FullName.Replace('\\', '/').StartsWith("Report/StaticResources/", StringComparison.OrdinalIgnoreCase)
                        && !e.FullName.EndsWith("/", StringComparison.Ordinal))
            .OrderBy(e => e.FullName.Replace('\\', '/'), StringComparer.Ordinal);
        foreach (var e in statics)
        {
            string rel = e.FullName.Replace('\\', '/').Substring("Report/".Length);
            WriteBytes(reportDir, rel, ReadEntry(e), written);
        }
        return written;
    }

    /// <summary>PBIR: every Report/** zip entry - .json/.pbir canonicalised (JsonNode preserves property
    /// order), everything else byte-verbatim. Entries written in ordinal path order.</summary>
    private static List<string> UnpackPbirReport(string pbixPath, string reportDir)
    {
        var written = new List<string>();
        using var zip = ZipFile.OpenRead(pbixPath);
        var entries = zip.Entries
            .Where(e => e.FullName.Replace('\\', '/').StartsWith("Report/", StringComparison.OrdinalIgnoreCase)
                        && !e.FullName.EndsWith("/", StringComparison.Ordinal))
            .OrderBy(e => e.FullName.Replace('\\', '/'), StringComparer.Ordinal);
        foreach (var e in entries)
        {
            string rel = e.FullName.Replace('\\', '/').Substring("Report/".Length);
            byte[] bytes = ReadEntry(e);
            if (rel.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                rel.EndsWith(".pbir", StringComparison.OrdinalIgnoreCase))
            {
                JsonNode? node = null;
                try { node = JsonNode.Parse(Utf8.GetString(StripBom(bytes))); }
                catch { /* not parseable JSON - keep verbatim */ }
                if (node != null) { WriteText(reportDir, rel, node.ToJsonString(JsonOpts), written); continue; }
            }
            WriteBytes(reportDir, rel, bytes, written);
        }
        return written;
    }

    private static void WriteText(string reportDir, string rel, string text, List<string> written)
        => WriteBytes(reportDir, rel, Utf8.GetBytes(text), written);

    private static void WriteBytes(string reportDir, string rel, byte[] bytes, List<string> written)
    {
        string dest = Path.Combine(reportDir, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, bytes);
        written.Add("Report/" + rel);
    }

    private static byte[] ReadEntry(ZipArchiveEntry e)
    {
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static byte[] StripBom(byte[] b)
    {
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return b[3..];
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return b[2..];
        return b;
    }

    // ============================================================================ diff

    /// <summary>Semantic diff between two sides, each a .pbix file or an unpacked source tree folder.</summary>
    public static object Diff(string pathA, string pathB, ReportService report)
    {
        var a = LoadSide(pathA, report);
        var b = LoadSide(pathB, report);
        bool sameKind = a.IsTree == b.IsTree;

        // file layer only compares like layouts (tree rel-paths vs zip entry names are different worlds)
        string[] filesAdded = Array.Empty<string>(), filesRemoved = Array.Empty<string>(), filesChanged = Array.Empty<string>();
        if (sameKind)
        {
            filesAdded = Sorted(b.Files.Keys.Where(k => !a.Files.ContainsKey(k)));
            filesRemoved = Sorted(a.Files.Keys.Where(k => !b.Files.ContainsKey(k)));
            filesChanged = Sorted(a.Files.Keys.Where(k => b.Files.ContainsKey(k) && a.Files[k] != b.Files[k]));
        }

        // model layer: TMDL is only present in unpacked trees
        bool modelDiff = a.IsTree && b.IsTree;
        var tablesAdded = modelDiff ? Sorted(b.Tables.Where(t => !a.Tables.Contains(t))) : Array.Empty<string>();
        var tablesRemoved = modelDiff ? Sorted(a.Tables.Where(t => !b.Tables.Contains(t))) : Array.Empty<string>();
        var measuresAdded = modelDiff ? Sorted(b.Measures.Keys.Where(m => !a.Measures.ContainsKey(m))) : Array.Empty<string>();
        var measuresRemoved = modelDiff ? Sorted(a.Measures.Keys.Where(m => !b.Measures.ContainsKey(m))) : Array.Empty<string>();
        var measuresChanged = modelDiff
            ? Sorted(a.Measures.Keys.Where(m => b.Measures.ContainsKey(m) && a.Measures[m] != b.Measures[m]))
            : Array.Empty<string>();

        // report layer: pages keyed by displayName (fallback name), value = visual count
        var pagesAdded = Sorted(b.Pages.Keys.Where(p => !a.Pages.ContainsKey(p)));
        var pagesRemoved = Sorted(a.Pages.Keys.Where(p => !b.Pages.ContainsKey(p)));
        var visualCountChanges = a.Pages.Keys
            .Where(p => b.Pages.ContainsKey(p) && a.Pages[p] != b.Pages[p])
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => (object)new { page = p, a = a.Pages[p], b = b.Pages[p] })
            .ToArray();

        // query layer: M is only readable from a .pbix DataMashup
        bool queryDiff = a.HasQueries && b.HasQueries;
        var queriesAdded = queryDiff ? Sorted(b.Queries.Keys.Where(q => !a.Queries.ContainsKey(q))) : Array.Empty<string>();
        var queriesRemoved = queryDiff ? Sorted(a.Queries.Keys.Where(q => !b.Queries.ContainsKey(q))) : Array.Empty<string>();
        var queriesChanged = queryDiff
            ? Sorted(a.Queries.Keys.Where(q => b.Queries.ContainsKey(q) && a.Queries[q] != b.Queries[q]))
            : Array.Empty<string>();

        string? note = null;
        if (!a.IsTree && !b.IsTree)
            note = "model (TMDL) diff is not possible from cold .pbix files - unpack both sides with unpack_to_source first for the model layer.";
        else if (!sameKind)
            note = "sides are different kinds (unpacked tree vs .pbix) - file, model and query layers compare like-for-like only; page diff still applies.";

        return new
        {
            ok = true,
            a = new { path = pathA, kind = a.IsTree ? "tree" : "pbix" },
            b = new { path = pathB, kind = b.IsTree ? "tree" : "pbix" },
            files = new { added = filesAdded, removed = filesRemoved, changed = filesChanged },
            model = new { tablesAdded, tablesRemoved, measuresAdded, measuresRemoved, measuresChanged },
            report = new { pagesAdded, pagesRemoved, visualCountChanges },
            queries = new { added = queriesAdded, removed = queriesRemoved, changed = queriesChanged },
            note,
        };
    }

    private static string[] Sorted(IEnumerable<string> xs) => xs.OrderBy(x => x, StringComparer.Ordinal).ToArray();

    // ---- one side of the diff, normalised ----

    private sealed class Side
    {
        public required bool IsTree { get; init; }
        public bool HasQueries;
        public readonly Dictionary<string, string> Files = new(StringComparer.Ordinal);       // rel path -> content hash
        public readonly HashSet<string> Tables = new(StringComparer.OrdinalIgnoreCase);       // tables/*.tmdl file names
        public readonly Dictionary<string, string> Measures = new(StringComparer.OrdinalIgnoreCase);   // name -> DAX text
        public readonly Dictionary<string, int> Pages = new(StringComparer.Ordinal);          // page key -> visual count
        public readonly Dictionary<string, string> Queries = new(StringComparer.Ordinal);     // M query name -> text
    }

    private static Side LoadSide(string path, ReportService report)
    {
        if (Directory.Exists(path)) return LoadTree(path);
        if (!File.Exists(path)) throw new FileNotFoundException($"diff side not found: {path}");
        return LoadPbix(path, report);
    }

    private static Side LoadTree(string root)
    {
        var side = new Side { IsTree = true };
        var rels = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(r => !r.Equals(Marker, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        foreach (var rel in rels)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            using var fs = File.OpenRead(full);
            side.Files[rel] = Hash(fs);
        }

        foreach (var rel in rels.Where(r =>
                     r.StartsWith("Model/definition/tables/", StringComparison.OrdinalIgnoreCase) &&
                     r.EndsWith(".tmdl", StringComparison.OrdinalIgnoreCase)))
        {
            side.Tables.Add(Path.GetFileNameWithoutExtension(rel));
            ParseMeasures(File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))), side.Measures);
        }

        string layoutPath = Path.Combine(root, "Report", "Layout.json");
        if (File.Exists(layoutPath))
            LoadLegacyPages(JsonNode.Parse(File.ReadAllText(layoutPath)), side.Pages);
        else
            LoadPbirPages(
                rel => JsonNode.Parse(File.ReadAllText(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)))),
                rels, side.Pages);
        return side;
    }

    private static Side LoadPbix(string pbixPath, ReportService report)
    {
        var side = new Side { IsTree = false };
        var jsonBytes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);   // small report parts we parse
        using (var zip = ZipFile.OpenRead(pbixPath))
        {
            foreach (var e in zip.Entries)
            {
                string rel = e.FullName.Replace('\\', '/');
                if (rel.EndsWith("/", StringComparison.Ordinal)) continue;
                using (var s = e.Open()) side.Files[rel] = Hash(s);
                bool keep = rel.Equals("Report/Layout", StringComparison.OrdinalIgnoreCase) ||
                            (rel.StartsWith("Report/definition/pages/", StringComparison.OrdinalIgnoreCase) &&
                             rel.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase));
                if (keep) jsonBytes[rel] = ReadEntry(e);
            }
        }

        if (jsonBytes.TryGetValue("Report/Layout", out var lb))
        {
            int off = (lb.Length >= 2 && lb[0] == 0xFF && lb[1] == 0xFE) ? 2 : 0;   // strip UTF-16 BOM
            string text = new UnicodeEncoding(false, false).GetString(lb, off, lb.Length - off);
            LoadLegacyPages(JsonNode.Parse(text), side.Pages);
        }
        else
        {
            LoadPbirPages(rel => JsonNode.Parse(Utf8.GetString(StripBom(jsonBytes[rel]))), side.Files.Keys, side.Pages);
        }

        // M queries - the DataMashup part may be absent (enhanced-metadata models keep M in the DataModel)
        try
        {
            var pq = JsonSerializer.SerializeToNode(report.ExtractPowerQuery(pbixPath)) as JsonObject;
            if ((bool?)pq?["hasDataMashup"] == true && (string?)pq["section1M"] is string m && m.Length > 0)
            {
                side.HasQueries = true;
                ParseQueries(m, side.Queries);
            }
        }
        catch { /* no mashup / unreadable - the query layer just stays empty */ }
        return side;
    }

    private static string Hash(Stream s)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(s));
    }

    // ---- report pages (legacy sections[] or PBIR page.json files) ----

    private static void LoadLegacyPages(JsonNode? layout, Dictionary<string, int> pages)
    {
        if (layout?["sections"] is not JsonArray sections) return;
        foreach (var s in sections)
        {
            if (s is not JsonObject so) continue;
            string key = (string?)so["displayName"] ?? (string?)so["name"] ?? "";
            if (key.Length == 0) continue;
            pages[key] = (so["visualContainers"] as JsonArray)?.Count ?? 0;
        }
    }

    private static void LoadPbirPages(Func<string, JsonNode?> readJson, IEnumerable<string> relPaths, Dictionary<string, int> pages)
    {
        var rels = relPaths.ToList();
        foreach (var rel in rels.Where(r =>
                     r.StartsWith("Report/definition/pages/", StringComparison.OrdinalIgnoreCase) &&
                     r.EndsWith("/page.json", StringComparison.OrdinalIgnoreCase)))
        {
            JsonObject? pj = null;
            try { pj = readJson(rel) as JsonObject; } catch { }
            if (pj == null) continue;
            string folder = rel[..^"/page.json".Length];   // Report/definition/pages/<name>
            string key = (string?)pj["displayName"] ?? (string?)pj["name"] ?? folder;
            pages[key] = rels.Count(r =>
                r.StartsWith(folder + "/visuals/", StringComparison.OrdinalIgnoreCase) &&
                r.EndsWith("/visual.json", StringComparison.OrdinalIgnoreCase));
        }
    }

    // ---- TMDL measures (name + DAX text, heuristic line parse - same parser both sides) ----

    private static readonly Regex MeasureRx = new(
        @"^\s*measure\s+(?:'(?<q>[^']+)'|(?<n>[A-Za-z0-9_ ]+?))\s*=(?<rest>.*)$", RegexOptions.Compiled);
    private static readonly Regex ObjectRx = new(
        @"^\s*(measure|column|partition|hierarchy|level|annotation|changedProperty|variation|extendedProperty|calculationGroup|calculationItem|role|table)\b",
        RegexOptions.Compiled);
    private static readonly Regex PropertyRx = new(@"^\s*[A-Za-z][A-Za-z0-9]*\s*:", RegexOptions.Compiled);

    private static void ParseMeasures(string tmdl, Dictionary<string, string> into)
    {
        var lines = tmdl.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var m = MeasureRx.Match(lines[i]);
            if (!m.Success) continue;
            string name = m.Groups["q"].Success ? m.Groups["q"].Value : m.Groups["n"].Value.Trim();
            var dax = new StringBuilder(m.Groups["rest"].Value.Trim());
            int j = i + 1;
            // the expression continues until the next object or property line (formatString: etc.)
            for (; j < lines.Length; j++)
            {
                if (ObjectRx.IsMatch(lines[j]) || PropertyRx.IsMatch(lines[j])) break;
                dax.Append('\n').Append(lines[j].Trim());
            }
            into[name] = dax.ToString().Trim();
            i = j - 1;
        }
    }

    // ---- M queries (split Section1.m into per-query text - same parser both sides) ----

    private static readonly Regex QueryRx = new(
        @"(?:^|;)\s*shared\s+(?:#""(?<q>[^""]+)""|(?<n>[A-Za-z_][A-Za-z0-9_.]*))\s*=",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static void ParseQueries(string section1M, Dictionary<string, string> into)
    {
        var matches = QueryRx.Matches(section1M);
        for (int i = 0; i < matches.Count; i++)
        {
            string name = matches[i].Groups["q"].Success ? matches[i].Groups["q"].Value : matches[i].Groups["n"].Value;
            int start = matches[i].Index;
            int end = i + 1 < matches.Count ? matches[i + 1].Index : section1M.Length;
            into[name] = section1M[start..end].Trim().TrimStart(';').Trim();
        }
    }
}
