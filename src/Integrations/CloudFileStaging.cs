using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using SuperBiMcp.Services;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The shared "a file the customer picked -&gt; canonical CSVs" staging used by every cloud-storage connector
/// (and by <see cref="FilesConnector"/> for its local-file and URL sub-modes). It owns the ONE copy of the
/// file-to-canonical logic so the connectors that fetch a file stay thin:
///   - a .csv passes through (validated, copied into the work dir under its table name);
///   - an Excel workbook (.xlsx / .xlsm / .xls) is expanded with <see cref="ExcelService.XlsxToCsv"/>, one
///     CSV per worksheet, the sheet name becoming the table name;
/// then each staged CSV's header + a leading sample are read to infer a type per column for the no-Solution
/// AI-auto-shape path. The cloud connectors add only an OAuth Bearer download in front of this.
///
/// HTTP fetches use the BCL <see cref="HttpClient"/> only (no NuGet). A download is bounded by a hard byte
/// cap so a hostile or runaway source cannot exhaust the box; every cap that bites is recorded in
/// <see cref="IngestResult.Notes"/> - no silent truncation. An access token is applied to the request header
/// only and is never persisted, never logged, and never written to a Note or an exception message.
/// </summary>
internal static class CloudFileStaging
{
    // how many leading rows to sample when inferring column types (matches FilesConnector / GoogleSheets).
    private const int SampleRows = 200;

    /// <summary>Hard cap on a single downloaded file so a hostile or runaway source cannot exhaust the box.</summary>
    public const long MaxDownloadBytes = 256L * 1024 * 1024;   // 256 MB per fetched file

    private static readonly ExcelService _excel = new(NullLogger<ExcelService>.Instance);

    // one shared HttpClient (BCL guidance: never one-per-request). 100s default timeout is fine for a VM pull.
    private static readonly HttpClient _http = HttpDefaults.New();

    /// <summary>A header to apply to an outgoing request (e.g. a Dropbox-API-Arg). Content headers fall back
    /// when a header is not valid on the request line.</summary>
    public readonly record struct Header(string Name, string Value);

    /// <summary>
    /// Download a file with an OAuth Bearer token (and any extra headers) into <paramref name="workDir"/>,
    /// then stage it into the canonical CSV shape exactly as an uploaded file would be. The extension is
    /// taken from <paramref name="fileName"/> when supplied, else from <paramref name="contentType"/>, else
    /// it falls back to .csv. The download lands in <paramref name="workDir"/> under a temp name and is
    /// deleted after staging so nothing is left behind. Returns the bytes downloaded, or -1 if the fetch
    /// failed or the byte cap bit (a note is recorded in either case).
    /// </summary>
    /// <param name="method">HTTP method for the download (GET for most APIs, POST for Dropbox).</param>
    /// <param name="url">The provider's file-content URL.</param>
    /// <param name="accessToken">The short-lived OAuth Bearer token (never logged / persisted).</param>
    /// <param name="extraHeaders">Provider-specific headers (e.g. Dropbox-API-Arg); may be empty.</param>
    /// <param name="fileName">The picked file's name, used to detect the extension (csv vs xlsx).</param>
    /// <param name="contentType">A fallback content-type/mime when the name carries no usable extension.</param>
    /// <param name="tableHint">Preferred table/CSV stem for a .csv (e.g. the file name without extension);
    /// ignored for an Excel workbook, whose sheet names drive the table names.</param>
    public static long DownloadAndStage(
        HttpMethod method, string url, string? accessToken, IReadOnlyList<Header> extraHeaders,
        string? fileName, string? contentType, string? tableHint,
        string workDir, IngestResult result, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);

        string ext = DetectExtension(fileName, contentType);
        string stem = !string.IsNullOrWhiteSpace(tableHint)
            ? SafeTableName(tableHint!)
            : SafeTableName(StemFromName(fileName));

        string downloaded = Path.Combine(workDir, "_dl_" + Guid.NewGuid().ToString("N") + ext);
        try
        {
            long bytes = Download(method, url, accessToken, extraHeaders, downloaded, result, ct);
            if (bytes < 0) return -1; // a note was already recorded (cap hit or fetch error)

            if (ext == ".csv")
            {
                // name the table from the picked file (not the temp name): copy onto the stem CSV.
                string dest = Path.Combine(workDir, stem + ".csv");
                if (!string.Equals(Path.GetFullPath(downloaded), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(downloaded, dest, overwrite: true);
                RegisterCsv(stem, dest, result);
            }
            else
            {
                StageLocalFile(downloaded, workDir, result, ct);
            }
            return bytes;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Notes.Add("file fetch failed: " + ex.Message);
            return -1;
        }
        finally
        {
            TryDeleteFile(downloaded);
        }
    }

    /// <summary>Stage one already-local file (.csv passes through, .xlsx/.xlsm/.xls is expanded one CSV per
    /// sheet) into <paramref name="workDir"/>, then infer its schema and register the table(s). This is the
    /// single canonical implementation shared by the file and URL sub-modes.</summary>
    public static void StageLocalFile(string path, string workDir, IngestResult result, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        if (!File.Exists(path))
        {
            result.Notes.Add("file not found, skipped: " + Path.GetFileName(path));
            return;
        }

        string ext = Path.GetExtension(path).ToLowerInvariant();
        switch (ext)
        {
            case ".csv":
            {
                string table = SafeTableName(Path.GetFileNameWithoutExtension(path));
                string dest = Path.Combine(workDir, table + ".csv");
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(path, dest, overwrite: true);
                RegisterCsv(table, dest, result);
                break;
            }
            case ".xlsx":
            case ".xlsm":
            case ".xls":
            {
                // one CSV per worksheet; the sheet name becomes the table name
                foreach (var sheet in SheetNames(path))
                {
                    ct.ThrowIfCancellationRequested();
                    string table = SafeTableName(sheet);
                    string dest = Path.Combine(workDir, table + ".csv");
                    try
                    {
                        _excel.XlsxToCsv(path, sheet, dest);
                        RegisterCsv(table, dest, result);
                    }
                    catch (Exception ex)
                    {
                        result.Notes.Add($"sheet '{sheet}' could not be staged: {ex.Message}");
                    }
                }
                break;
            }
            default:
                result.Notes.Add("unsupported file type, skipped: " + Path.GetFileName(path));
                break;
        }
    }

    /// <summary>Register a staged CSV in the result: record the table-&gt;path mapping and infer its schema
    /// (header + sampled types) for the no-Solution auto-shape path.</summary>
    public static void RegisterCsv(string table, string csvPath, IngestResult result)
    {
        result.Tables[table] = csvPath;
        var ts = InferCsvSchema(table, csvPath);
        if (ts != null) result.Schema.Tables.Add(ts);
    }

    /// <summary>A logical table / CSV stem safe for an M file reference: keep letters, digits, spaces,
    /// underscores and hyphens; collapse the rest to underscores.</summary>
    public static string SafeTableName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Table";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' ? c : '_');
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "Table" : s;
    }

    // ---- download ------------------------------------------------------------------------------

    /// <summary>Download a URL to <paramref name="dest"/> with the per-job Bearer token (and any extra
    /// headers) under a hard byte cap. Returns the byte count, or -1 if the fetch failed or the cap was
    /// exceeded (a note is recorded in either case). The token is applied to the request header only - never
    /// written to a Note or an exception message.</summary>
    private static long Download(HttpMethod method, string url, string? accessToken, IReadOnlyList<Header> extraHeaders,
        string dest, IngestResult result, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(method, url);
        if (!string.IsNullOrWhiteSpace(accessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        foreach (var h in extraHeaders)
            if (!msg.Headers.TryAddWithoutValidation(h.Name, h.Value))
                msg.Content?.Headers.TryAddWithoutValidation(h.Name, h.Value);

        // Must be async: HttpDefaults uses WinHttpHandler on Windows (its TLS fingerprint isn't WAF-flagged),
        // and WinHttpHandler does NOT support the synchronous HttpClient.Send / HttpContent.ReadAsStream - they
        // throw "The synchronous method is not supported by 'WinHttpHandler'". Bridge async->sync with
        // GetAwaiter().GetResult() (this runs on the engine's background job thread; no sync-context deadlock),
        // matching how the connectors already invoke FetchAsync. Before this every cloud-storage FILE download
        // (Google Drive, OneDrive, Dropbox, SharePoint) failed on the box with a masked "no tables".
        using var resp = _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
        {
            result.Notes.Add($"file fetch returned HTTP {(int)resp.StatusCode}; skipped.");
            return -1;
        }

        using var input = resp.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
        using var fs = File.Create(dest);
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            total += n;
            if (total > MaxDownloadBytes)
            {
                result.Notes.Add($"file fetch exceeded the {MaxDownloadBytes / (1024 * 1024)} MB cap; skipped (no partial file ingested).");
                return -1;
            }
            fs.Write(buf, 0, n);
        }
        return total;
    }

    // ---- extension / mime detection ------------------------------------------------------------

    /// <summary>Decide whether the fetched bytes are an Excel workbook or a CSV from the picked file name
    /// first (its extension is authoritative), then a content-type / mime fallback, else default to .csv.
    /// Returns a normalised extension (".csv" or ".xlsx").</summary>
    public static string DetectExtension(string? fileName, string? contentType)
    {
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            if (ext is ".csv") return ".csv";
            if (ext is ".xlsx" or ".xlsm" or ".xls") return ".xlsx";
        }
        if (!string.IsNullOrWhiteSpace(contentType))
        {
            string ct = contentType.ToLowerInvariant();
            if (ct.Contains("spreadsheetml") || ct.Contains("ms-excel") || ct.Contains("officedocument.spreadsheet"))
                return ".xlsx";
            if (ct.Contains("csv")) return ".csv";
        }
        return ".csv";
    }

    private static string StemFromName(string? fileName)
    {
        string stem = string.IsNullOrWhiteSpace(fileName) ? "" : Path.GetFileNameWithoutExtension(fileName);
        return string.IsNullOrWhiteSpace(stem) ? "cloud_file" : stem;
    }

    // ---- schema inference / sheet listing ------------------------------------------------------

    /// <summary>List worksheet names in a workbook via ExcelService.ListSheets; fall back to the workbook
    /// stem as a single sheet name if the listing shape is unexpected.</summary>
    private static IEnumerable<string> SheetNames(string xlsxPath)
    {
        try
        {
            // ListSheets returns an anonymous object { ok, xlsxPath, sheets:[{name, sheetId}] }; read names
            // out of it via JSON so we do not depend on the anonymous type.
            var node = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(_excel.ListSheets(xlsxPath)));
            if (node?["sheets"] is JsonArray arr)
            {
                var names = arr.Select(n => (string?)n?["name"]).Where(n => n is { Length: > 0 }).Select(n => n!).ToList();
                if (names.Count > 0) return names;
            }
        }
        catch { /* fall through to the workbook stem as a single sheet name */ }
        return new[] { SafeTableName(Path.GetFileNameWithoutExtension(xlsxPath)) };
    }

    /// <summary>Read a CSV header and a leading sample of rows, infer a type per column, and return the table
    /// schema. Returns null for an empty file.</summary>
    private static TableSchema? InferCsvSchema(string table, string csvPath)
    {
        using var reader = new StreamReader(csvPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? headerLine = reader.ReadLine();
        if (string.IsNullOrEmpty(headerLine)) return null;

        string[] header = SplitCsvLine(headerLine);
        var samples = new List<string?>[header.Length];
        for (int i = 0; i < header.Length; i++) samples[i] = new List<string?>();

        int rows = 0;
        string? line;
        while (rows < SampleRows && (line = reader.ReadLine()) != null)
        {
            var fields = SplitCsvLine(line);
            for (int i = 0; i < header.Length; i++)
                samples[i].Add(i < fields.Length ? fields[i] : null);
            rows++;
        }

        var ts = new TableSchema(table);
        for (int i = 0; i < header.Length; i++)
        {
            string name = string.IsNullOrWhiteSpace(header[i]) ? $"Column{i + 1}" : header[i];
            ts.Columns.Add(new ColumnSchema(name, ColumnTypeInference.Infer(samples[i])));
        }
        return ts;
    }

    // minimal RFC-4180 CSV line splitter (handles quoted fields with embedded commas/quotes), matching the
    // splitter ExcelService uses for the unpivot path.
    private static string[] SplitCsvLine(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (inQ)
            {
                if (c == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQ = false; }
                else sb.Append(c);
            }
            else
            {
                if (c == ',') { outp.Add(sb.ToString()); sb.Clear(); }
                else if (c == '"') inQ = true;
                else sb.Append(c);
            }
        }
        outp.Add(sb.ToString());
        return outp.ToArray();
    }

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }
}
