using System.IO.Compression;
using System.Text;

namespace SuperBiMcp;

/// <summary>
/// Assembles a SINGLE-FILE legacy .pbix (OPC / ZIP package with an embedded data model) from the pieces the
/// rest of the pipeline already produces: the populated model's Analysis Services backup (.abf, from
/// Bake.BakeProject) becomes the binary <c>DataModel</c> part, and the dashboard built by
/// ReportLayoutBuilder becomes <c>Report/Layout</c>. No pbi-tools, no Power BI Desktop, no template.
///
/// Part list and encodings (see CompilePbix for the why):
///   [Content_Types].xml  - UTF-8 WITH BOM   (the only OPC-required manifest)
///   Version              - UTF-16 LE        ("1.28")
///   DataModel            - raw .abf bytes   (stored, never re-compressed)
///   Report/Layout        - UTF-16 LE, NO BOM (the legacy Layout JSON; wrong encoding = "corrupt file")
///
/// SecurityBindings is deliberately OMITTED (and so is its content-type override): the existing
/// ReportService.Repack path strips SecurityBindings on every legacy-pbix rewrite and Desktop reopens the
/// file, so it is provably not required here. Settings / Metadata are legacy .NET binary-serialised blobs
/// that cannot be hand-authored and are tolerated absent; Connections is for live-connection reports only and
/// is not shipped for an embedded model.
/// </summary>
public static class PbixCompiler
{
    // legacy text parts: UTF-16 LE. Layout is written WITHOUT a BOM (UnicodeEncoding(bigEndian:false,
    // byteOrderMark:false)); Version carries its BOM, matching what real pbix files store.
    private static readonly Encoding Utf16NoBom = new UnicodeEncoding(false, false);
    private static readonly Encoding Utf16Bom = new UnicodeEncoding(false, true);
    private static readonly Encoding Utf8Bom = new UTF8Encoding(true);

    private const string PbixVersion = "1.28";

    /// <summary>
    /// Compile a populated single-file .pbix at <paramref name="outPbixPath"/>:
    /// DataModel = the raw bytes of <paramref name="abfPath"/>, Report/Layout = <paramref name="layoutJson"/>,
    /// plus the minimal required OPC scaffold. <paramref name="pbipFolder"/> is accepted for parity with the
    /// caller and so report-level static resources (a theme under
    /// <c>*.Report/StaticResources/</c>) can be folded in when present; it is otherwise unused.
    /// Throws on any failure so the caller can fall back to the PBIP-zip deliverable.
    /// </summary>
    public static void CompilePbix(string pbipFolder, string abfPath, string layoutJson, string outPbixPath)
    {
        if (string.IsNullOrWhiteSpace(abfPath) || !File.Exists(abfPath))
            throw new FileNotFoundException($"DataModel source .abf not found: {abfPath}");
        if (string.IsNullOrWhiteSpace(layoutJson))
            throw new ArgumentException("layoutJson is empty.", nameof(layoutJson));

        // any static resources (e.g. a custom theme) the report folder carries, folded in verbatim so their
        // Default content-type ("json"/"png"/...) covers them - kept optional and best-effort.
        var staticResources = CollectStaticResources(pbipFolder);
        bool needPng = staticResources.Keys.Any(k => k.EndsWith(".png", StringComparison.OrdinalIgnoreCase));
        bool needJpg = staticResources.Keys.Any(k => k.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || k.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

        string contentTypes = BuildContentTypes(staticResources.Keys, needPng, needJpg);

        string tmp = outPbixPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                WriteText(zip, "[Content_Types].xml", contentTypes, Utf8Bom);
                WriteText(zip, "Version", PbixVersion, Utf16Bom);

                // DataModel: the raw .abf, stored uncompressed (it is already compact; the Repack path also
                // writes DataModel with NoCompression). Streamed so a large model never loads wholly into RAM.
                var dm = zip.CreateEntry("DataModel", CompressionLevel.NoCompression);
                using (var dst = dm.Open())
                using (var src = new FileStream(abfPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    src.CopyTo(dst);

                // Report/Layout: UTF-16 LE, NO BOM.
                WriteText(zip, "Report/Layout", layoutJson, Utf16NoBom);

                foreach (var kv in staticResources)
                {
                    var e = zip.CreateEntry(kv.Key, CompressionLevel.Optimal);
                    using var s = e.Open();
                    s.Write(kv.Value, 0, kv.Value.Length);
                }
            }

            if (File.Exists(outPbixPath)) File.Delete(outPbixPath);
            File.Move(tmp, outPbixPath);
        }
        catch
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            throw;
        }
    }

    private static void WriteText(ZipArchive zip, string partName, string text, Encoding enc)
    {
        var e = zip.CreateEntry(partName, CompressionLevel.Optimal);
        using var s = e.Open();
        byte[] b = enc.GetBytes(text);
        s.Write(b, 0, b.Length);
    }

    /// <summary>The OPC content-type manifest. Empty ContentType strings are intentional for a pbix (Power BI
    /// does not use real MIME types here - this matches the known-good legacy [Content_Types].xml). Overrides
    /// are emitted only for the parts actually present; Default extensions cover the static resources.</summary>
    private static string BuildContentTypes(IEnumerable<string> staticParts, bool needPng, bool needJpg = false)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">");
        sb.Append("<Default Extension=\"json\" ContentType=\"\" />");
        if (needPng) sb.Append("<Default Extension=\"png\" ContentType=\"\" />");
        if (needJpg) { sb.Append("<Default Extension=\"jpg\" ContentType=\"\" />"); sb.Append("<Default Extension=\"jpeg\" ContentType=\"\" />"); }
        sb.Append("<Override PartName=\"/Version\" ContentType=\"\" />");
        sb.Append("<Override PartName=\"/DataModel\" ContentType=\"\" />");
        sb.Append("<Override PartName=\"/Report/Layout\" ContentType=\"\" />");
        sb.Append("</Types>");
        _ = staticParts;   // covered by the Default extensions above
        return sb.ToString();
    }

    /// <summary>Any files under a <c>*.Report/StaticResources/</c> folder in the PBIP, keyed by their pbix
    /// part name (<c>Report/StaticResources/...</c>). Empty when the project has none. Best-effort - a missing
    /// or unreadable resources folder simply yields no extra parts.</summary>
    private static Dictionary<string, byte[]> CollectStaticResources(string pbipFolder)
    {
        var map = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(pbipFolder) || !Directory.Exists(pbipFolder)) return map;
        try
        {
            foreach (var reportDir in Directory.GetDirectories(pbipFolder, "*.Report", SearchOption.AllDirectories))
            {
                string sr = Path.Combine(reportDir, "StaticResources");
                if (!Directory.Exists(sr)) continue;
                foreach (var f in Directory.GetFiles(sr, "*", SearchOption.AllDirectories))
                {
                    string rel = Path.GetRelativePath(sr, f).Replace('\\', '/');
                    string part = "Report/StaticResources/" + rel;
                    if (!map.ContainsKey(part)) map[part] = File.ReadAllBytes(f);
                }
            }
        }
        catch { /* static resources are optional - never fail the compile over them */ }
        return map;
    }
}
