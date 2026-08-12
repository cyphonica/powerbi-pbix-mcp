using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Services;

/// <summary>
/// pbix_doctor: a READ-ONLY file-level container scan of a CLOSED .pbix - the OPC zip part inventory
/// against the expected parts, stale signature parts (SecurityBindings / DataMashup PermissionBindings),
/// Version / DataModel / DataMashup presence and sizes, sensitivity-label parts, zero-byte and truncated
/// parts. Never writes; the file is only ever opened for read. Mirrors the zip discipline the writers in
/// <see cref="ReportService"/> / PbixCompiler already follow (they strip SecurityBindings on rewrite -
/// this is the diagnostic that tells you whether a stale one is still present).
/// </summary>
public static class PbixDoctor
{
    private sealed record Part(string Name, long Length, long CompressedLength, bool Readable);

    // top-level parts a healthy Desktop-authored .pbix is expected/allowed to carry.
    private static readonly string[] KnownTopLevel =
    {
        "[Content_Types].xml", "Version", "DataModel", "DataModelSchema", "DataMashup", "Report",
        "DiagramLayout", "DiagramState", "Settings", "Metadata", "SecurityBindings", "Connections",
        "docProps", "docMetadata", "CustomVisuals", "RegisteredResources", "StaticResources",
    };

    public static object Run(string pbixPath)
    {
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");

        var parts = new List<Part>();
        var duplicates = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[]? versionBytes = null, mashupBytes = null, layoutBytes = null;
        bool hasDefinitionTree = false, hasPbirPointer = false;

        using (var zip = ZipFile.OpenRead(pbixPath))
        {
            foreach (var e in zip.Entries)
            {
                string n = e.FullName.Replace('\\', '/').TrimStart('/');
                if (n.EndsWith("/")) continue;                     // directory marker
                if (!seen.Add(n)) duplicates.Add(n);

                // full read proves the entry inflates end to end (a truncated/corrupt part throws here)
                bool readable = true;
                byte[]? bytes = null;
                try
                {
                    using var s = e.Open(); using var ms = new MemoryStream();
                    s.CopyTo(ms); bytes = ms.ToArray();
                }
                catch { readable = false; }

                parts.Add(new Part(n, e.Length, e.CompressedLength, readable));

                if (readable && bytes != null)
                {
                    if (n.Equals("Version", StringComparison.OrdinalIgnoreCase)) versionBytes = bytes;
                    else if (n.Equals("DataMashup", StringComparison.OrdinalIgnoreCase)) mashupBytes = bytes;
                    else if (n.Equals("Report/Layout", StringComparison.OrdinalIgnoreCase)) layoutBytes = bytes;
                }
                if (n.StartsWith("Report/definition/", StringComparison.OrdinalIgnoreCase)) hasDefinitionTree = true;
                if (n.Equals("Report/definition.pbir", StringComparison.OrdinalIgnoreCase)) hasPbirPointer = true;
            }
        }

        var checks = new List<object>();
        int failCount = 0, warnCount = 0;
        void Check(string id, string name, string status, string detail)
        {
            if (status == "fail") failCount++;
            else if (status == "warn") warnCount++;
            checks.Add(new { id, name, status, detail });
        }
        Part? Find(string name) => parts.FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

        // 1. [Content_Types].xml - the OPC manifest every valid package needs
        var ct = Find("[Content_Types].xml");
        Check("content-types", "[Content_Types].xml present", ct != null ? "pass" : "fail",
            ct != null ? $"{ct.Length} bytes" : "missing - not a valid OPC package; Desktop will refuse it.");

        // 2 + 3. Version part present and decodable
        var ver = Find("Version");
        string? versionText = null;
        if (versionBytes != null)
        {
            try { versionText = new UnicodeEncoding(false, true).GetString(StripBom(versionBytes)).Trim('\0').Trim(); }
            catch { versionText = null; }
        }
        Check("version-present", "Version part present", ver != null ? "pass" : "fail",
            ver != null ? $"{ver.Length} bytes" : "missing - Desktop uses it to pick the load path.");
        Check("version-readable", "Version decodes (UTF-16)", versionText != null ? "pass" : (ver == null ? "warn" : "fail"),
            versionText ?? "not decodable as UTF-16 text.");

        // 4 + 5. model part - DataModel (pbix), DataModelSchema (pbit), or a live connection (Connections)
        var dm = Find("DataModel"); var dms = Find("DataModelSchema"); var conn = Find("Connections");
        if (dm != null)
            Check("data-model", "DataModel present", dm.Length > 0 ? "pass" : "fail",
                dm.Length > 0 ? $"{dm.Length} bytes (compressed {dm.CompressedLength})" : "zero-byte DataModel - the model is gone.");
        else if (dms != null)
            Check("data-model", "DataModelSchema present (template)", "pass", $"{dms.Length} bytes - a .pbit-style schema-only model.");
        else if (conn != null)
            Check("data-model", "No local model - live connection", "info", "Connections part present; the model lives in the Service/AS.");
        else
            Check("data-model", "Model part present", "fail", "no DataModel, DataModelSchema or Connections part - the file has no model at all.");

        // 6. report part - legacy Layout vs PBIR definition tree
        string reportFormat = layoutBytes != null ? "legacy" : (hasDefinitionTree || hasPbirPointer) ? "pbir" : "none";
        Check("report-part", "Report part present", reportFormat == "none" ? "fail" : "pass",
            reportFormat == "legacy" ? "legacy Report/Layout (single UTF-16 JSON blob)"
            : reportFormat == "pbir" ? "PBIR Report/definition tree"
            : "no Report/Layout and no Report/definition tree.");

        // 7. legacy layout parses
        if (layoutBytes != null)
        {
            int? sections = null;
            try
            {
                string text = new UnicodeEncoding(false, true).GetString(StripBom(layoutBytes));
                sections = (JsonNode.Parse(text) as JsonObject)?["sections"] is JsonArray sa ? sa.Count : null;
            }
            catch { /* parse failed */ }
            Check("layout-parses", "Report/Layout parses", sections != null ? "pass" : "fail",
                sections != null ? $"{sections} page(s)" : "Report/Layout is not parseable UTF-16 JSON.");
        }

        // 8 + 9. DataMashup container - QDEFF parse + stale PermissionBindings
        if (mashupBytes != null)
        {
            var (qdeffOk, permissionBindings, packageBytes) = ProbeQdeff(mashupBytes);
            Check("datamashup", "DataMashup parses (QDEFF)", qdeffOk ? "pass" : "fail",
                qdeffOk ? $"inner package {packageBytes} bytes" : "DataMashup is not a well-formed QDEFF container.");
            Check("permission-bindings", "DataMashup PermissionBindings", permissionBindings ? "warn" : "pass",
                permissionBindings
                    ? "present - a SHA-256 over the formulas; STALE if the M was edited outside Desktop (Desktop recomputes on open)."
                    : "absent/cleared - Desktop recomputes on open.");
        }
        else
            Check("datamashup", "DataMashup present", "info",
                "absent - Power Query M lives in the DataModel/TMDL (enhanced-metadata PBI_V3) or there is no import M.");

        // 10. stale SecurityBindings signature
        var sb = Find("SecurityBindings");
        Check("security-bindings", "SecurityBindings part", sb != null ? "warn" : "pass",
            sb != null
                ? $"present ({sb.Length} bytes) - a signature over the package; STALE if any part was rewritten outside Desktop. Our writers strip it; Desktop re-signs on save."
                : "absent - normal for an externally-edited file; Desktop re-signs on save.");

        // 11. sensitivity-label (MSIP) parts
        var labels = parts.Where(p => p.Name.Contains("LabelInfo", StringComparison.OrdinalIgnoreCase)
                                      || p.Name.Contains("SensitivityLabel", StringComparison.OrdinalIgnoreCase)).ToList();
        Check("sensitivity-labels", "Sensitivity-label parts", labels.Count > 0 ? "info" : "pass",
            labels.Count > 0
                ? $"present: {string.Join(", ", labels.Select(l => l.Name))} - an MSIP label travels with the file; external rewrites can invalidate it."
                : "none.");

        // 12. zero-byte parts (excluding the label/signature parts already reported)
        var zero = parts.Where(p => p.Length == 0).Select(p => p.Name).ToList();
        Check("zero-byte-parts", "Zero-byte parts", zero.Count == 0 ? "pass" : "warn",
            zero.Count == 0 ? "none" : string.Join(", ", zero));

        // 13. truncated/unreadable parts (the full-inflate probe failed)
        var bad = parts.Where(p => !p.Readable).Select(p => p.Name).ToList();
        Check("truncated-parts", "Truncated/corrupt parts", bad.Count == 0 ? "pass" : "fail",
            bad.Count == 0 ? "none - every entry inflates end to end" : string.Join(", ", bad));

        // 14. duplicate entry names
        Check("duplicate-parts", "Duplicate entries", duplicates.Count == 0 ? "pass" : "fail",
            duplicates.Count == 0 ? "none" : string.Join(", ", duplicates.Distinct()));

        // 15. Metadata + Settings presence
        Check("metadata", "Metadata part", Find("Metadata") != null ? "pass" : "info",
            Find("Metadata") != null ? $"{Find("Metadata")!.Length} bytes" : "absent.");
        Check("settings", "Settings part", Find("Settings") != null ? "pass" : "info",
            Find("Settings") != null ? $"{Find("Settings")!.Length} bytes" : "absent.");

        // 16. diagram parts (the model-view diagram state)
        bool diagram = Find("DiagramLayout") != null || Find("DiagramState") != null;
        Check("diagram", "Diagram layout/state", diagram ? "pass" : "info", diagram ? "present" : "absent.");

        // 17. unexpected top-level parts
        var unexpected = parts
            .Select(p => p.Name.Split('/')[0])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(top => !KnownTopLevel.Contains(top, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Check("unexpected-parts", "Unexpected top-level parts", unexpected.Count == 0 ? "pass" : "info",
            unexpected.Count == 0 ? "none" : string.Join(", ", unexpected));

        return new
        {
            ok = true,
            pbixPath,
            reportFormat,
            version = versionText,
            partCount = parts.Count,
            parts = parts.Select(p => new { name = p.Name, size = p.Length, compressed = p.CompressedLength }).ToList(),
            checks,
            passCount = checks.Count - failCount - warnCount,
            warnCount,
            failCount,
            verdict = failCount > 0 ? "fail" : warnCount > 0 ? "review" : "pass",
        };
    }

    /// <summary>Minimal QDEFF probe: version int + length-prefixed blocks that must fit inside the part.
    /// Returns whether it parses, whether a PermissionBindings block is present, and the inner package size.</summary>
    private static (bool ok, bool permissionBindings, int packageBytes) ProbeQdeff(byte[] raw)
    {
        try
        {
            if (raw.Length < 8) return (false, false, 0);
            int pos = 4;                                            // skip the version int
            int Read() { int v = BitConverter.ToInt32(raw, pos); pos += 4; return v; }
            int pkg = Read();
            if (pkg < 0 || pos + pkg > raw.Length) return (false, false, 0);
            pos += pkg;
            int bindings = 0;
            // permissions, metadata, permissionBindings - each optional at the tail
            for (int block = 0; block < 3 && pos + 4 <= raw.Length; block++)
            {
                int len = Read();
                if (len < 0 || pos + len > raw.Length) return (false, false, pkg);
                if (block == 2) bindings = len;
                pos += len;
            }
            return (true, bindings > 0, pkg);
        }
        catch { return (false, false, 0); }
    }

    private static byte[] StripBom(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return b[2..];
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return b[3..];
        return b;
    }
}
