using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The generic raw-data connector (id "files") - the foundation that proves the ingest spine. No OAuth, no
/// third-party review: it turns files the customer already has into the canonical CSV shape. Three
/// sub-modes, one connector:
///   - File upload (.csv / .xlsx / .xls): a .csv passes through (validated, copied); an Excel workbook is
///     expanded with <see cref="ExcelService.XlsxToCsv"/> (one CSV per sheet, sheet name -&gt; table name).
///   - CSV / Excel by URL: fetch the file onto the VM (HttpClient, size-capped), then the same staging.
///   - REST / JSON: an endpoint URL, optional auth headers, a JSON path to the record array; flatten the
///     records to a CSV, follow the paging param up to a logged cap (no silent truncation).
///
/// Default shape is AI auto-shape: <see cref="FlatSpecBuilder"/> builds one flat table per CSV. Advanced
/// users can map onto a Solution by setting <see cref="ConnectorRequest.SolutionId"/>, in which case the
/// pipeline uses the Solution's hand-authored model.spec.json instead.
///
/// HTTP fetches use the BCL <see cref="HttpClient"/> only (no NuGet). The connector never logs the URL's
/// query string or any auth header; an access token (or a header) is used per job and never persisted.
///
/// Params it reads:
///   files : JSON array of absolute paths already staged into the tenant dir (.csv / .xlsx / .xls).
///   urls  : JSON array of { url, kind:"csv"|"xlsx", table? } to fetch on the VM.
///   rest  : { url, headers?, jsonPath?, table?, paging? } for the JSON sub-mode, where paging is
///           { mode:"page"|"none", param?, start?, pageSize?, sizeParam?, maxPages? }.
/// </summary>
public sealed class FilesConnector : IDataSourceConnector
{
    public string Id => "files";
    public string DisplayName => "File or URL (CSV, Excel, REST)";

    // hard caps for VM-side fetches so a hostile or runaway source cannot exhaust the box. Every time a cap
    // bites it is recorded in IngestResult.Notes - no silent truncation.
    private const long MaxDownloadBytes = 256L * 1024 * 1024;   // 256 MB per fetched file
    private const int MaxRestRows = 200_000;                    // rows flattened from a REST pull
    private const int MaxRestPages = 1_000;                     // pages followed before the cap bites
    private const int DefaultRestPageSize = 100;

    // one shared HttpClient (BCL guidance: never one-per-request). 100s default timeout is fine for a VM pull.
    private static HttpClient _http = HttpDefaults.New();

    /// <summary>Test-only seam: replace the shared HttpClient's transport with a stub handler so the REST /
    /// URL sub-modes (paging, the row / page caps, the JSON flattener) can be driven offline. Returns an
    /// <see cref="IDisposable"/> that restores the real transport when disposed. Never used in production -
    /// only the test assembly (via InternalsVisibleTo) reaches this.</summary>
    internal static IDisposable UseTransportForTests(HttpMessageHandler handler)
    {
        HttpClient previous = _http;
        _http = HttpDefaults.New(handler);
        return new TransportSwap(() => _http = previous);
    }

    private sealed class TransportSwap : IDisposable
    {
        private readonly Action _restore;
        public TransportSwap(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }

    public Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discover by staging into a per-invocation probe dir (concurrent discovers must never share
        // staging), reading headers + a sample, then discarding exactly this dir - never the parent.
        string probe = Path.Combine(Path.GetTempPath(), "daxops-discover", Guid.NewGuid().ToString("N"));
        try
        {
            var result = StageAll(req, probe, ct);
            return Task.FromResult(result.Schema);
        }
        finally
        {
            TryWipe(probe);
        }
    }

    public Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        return Task.FromResult(StageAll(req, workDir, ct));
    }

    /// <summary>Stage every configured source into <paramref name="workDir"/> as CSVs, then infer the
    /// schema from each. Shared by Discover and Fetch.</summary>
    private IngestResult StageAll(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        // 1. local files already staged into the tenant dir
        if (req.Params["files"] is JsonArray files)
            foreach (var f in files)
                if ((string?)f is { Length: > 0 } path)
                    CloudFileStaging.StageLocalFile(path, workDir, result, ct);

        // 2. CSV/Excel by URL - fetch onto the VM, then the same local staging
        if (req.Params["urls"] is JsonArray urls)
            foreach (var u in urls)
                if (u is JsonObject uo)
                    StageUrl(uo, workDir, req, result, ct);

        // 3. REST / JSON - GET the endpoint, walk to the record array, flatten to a CSV (paged, capped)
        if (req.Params["rest"] is JsonObject rest)
            StageRest(rest, workDir, req, result, ct);

        if (result.Tables.Count == 0 && result.Schema.Tables.Count == 0)
            result.Notes.Add("No usable source was provided (expected files[], urls[] or rest).");

        return result;
    }

    // ---- URL sub-mode (CSV / Excel fetched onto the VM) ----------------------------------------

    /// <summary>Fetch one { url, kind:"csv"|"xlsx", table? } onto the VM (size-capped), then stage it through
    /// the same local path as an uploaded file. The download lands in <paramref name="workDir"/> so the
    /// caller's post-bake wipe removes it; nothing is left elsewhere.</summary>
    private void StageUrl(JsonObject spec, string workDir, ConnectorRequest req, IngestResult result, CancellationToken ct)
    {
        string? url = (string?)spec["url"];
        if (string.IsNullOrWhiteSpace(url) || !IsHttpUrl(url))
        {
            result.Notes.Add("url skipped (missing or not http/https).");
            return;
        }

        // kind: explicit, else inferred from the path extension, else csv
        string kind = ((string?)spec["kind"])?.Trim().ToLowerInvariant() is { Length: > 0 } k
            ? k
            : GuessKindFromUrl(url);
        string ext = kind switch { "xlsx" or "xls" or "xlsm" or "excel" => ".xlsx", _ => ".csv" };
        string stem = (string?)spec["table"] is { Length: > 0 } tn
            ? CloudFileStaging.SafeTableName(tn)
            : CloudFileStaging.SafeTableName(FileStemFromUrl(url));

        // download into workDir under a temp name; StageLocalFile then copies/expands it into the table CSV(s)
        string downloaded = Path.Combine(workDir, "_dl_" + Guid.NewGuid().ToString("N") + ext);
        try
        {
            long bytes = Download(url, BuildHeaders(spec["headers"], req.AccessToken), downloaded, result, ct);
            if (bytes < 0) return; // a note was already recorded (cap hit or fetch error)

            // for a .csv we want the table named from `table`/the url stem, not the temp file name, so copy it
            if (ext == ".csv")
            {
                string dest = Path.Combine(workDir, stem + ".csv");
                if (!string.Equals(Path.GetFullPath(downloaded), Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                    File.Copy(downloaded, dest, overwrite: true);
                CloudFileStaging.RegisterCsv(stem, dest, result);
            }
            else
            {
                CloudFileStaging.StageLocalFile(downloaded, workDir, result, ct);
            }
        }
        catch (Exception ex)
        {
            result.Notes.Add("url fetch failed: " + ex.Message);
        }
        finally
        {
            TryDeleteFile(downloaded);
        }
    }

    // ---- REST / JSON sub-mode ------------------------------------------------------------------

    /// <summary>GET a JSON endpoint, walk <c>rest.jsonPath</c> to the record array, flatten the records to one
    /// CSV (union of keys -&gt; header, nested objects/arrays serialised), following the paging param up to a
    /// cap. Every cap that bites is recorded in <see cref="IngestResult.Notes"/> - never a silent truncation.
    ///
    /// rest = { url, headers?, jsonPath?, table?, paging? }
    ///   paging = { mode:"page"|"none", param:"page", start:1, pageSize:100, sizeParam:"per_page", maxPages:N }
    /// </summary>
    private void StageRest(JsonObject rest, string workDir, ConnectorRequest req, IngestResult result, CancellationToken ct)
    {
        string? url = (string?)rest["url"];
        if (string.IsNullOrWhiteSpace(url) || !IsHttpUrl(url))
        {
            result.Notes.Add("rest skipped (missing or not http/https url).");
            return;
        }

        string table = (string?)rest["table"] is { Length: > 0 } tn ? CloudFileStaging.SafeTableName(tn) : "rest_data";
        string? jsonPath = (string?)rest["jsonPath"];
        var headers = BuildHeaders(rest["headers"], req.AccessToken);

        // paging config (best-effort, capped). mode:"none" pulls a single page.
        var paging = rest["paging"] as JsonObject;
        string pagingMode = ((string?)paging?["mode"])?.Trim().ToLowerInvariant() ?? "none";
        string pageParam = (string?)paging?["param"] is { Length: > 0 } pp ? pp : "page";
        string? sizeParam = (string?)paging?["sizeParam"] is { Length: > 0 } sp ? sp : null;
        int pageNum = paging?["start"] is { } st && int.TryParse(st.ToString(), out var s0) ? s0 : 1;
        int pageSize = paging?["pageSize"] is { } ps && int.TryParse(ps.ToString(), out var ps0) && ps0 > 0 ? ps0 : DefaultRestPageSize;
        int maxPages = paging?["maxPages"] is { } mp && int.TryParse(mp.ToString(), out var mp0) && mp0 > 0
            ? Math.Min(mp0, MaxRestPages) : MaxRestPages;

        var rows = new List<JsonObject>();
        var header = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int pagesPulled = 0;
        bool capHit = false;
        string capReason = "";

        try
        {
            for (int page = 0; page < (pagingMode == "page" ? maxPages : 1); page++)
            {
                ct.ThrowIfCancellationRequested();

                string pageUrl = pagingMode == "page"
                    ? AppendQuery(url, pageParam, (pageNum + page).ToString(), sizeParam, pageSize)
                    : url;

                JsonNode? root = GetJson(pageUrl, headers, ct);
                if (root == null) { result.Notes.Add($"rest: page {page + 1} returned no JSON; stopping."); break; }

                var records = SelectArray(root, jsonPath);
                if (records == null)
                {
                    if (page == 0) result.Notes.Add($"rest: jsonPath '{jsonPath ?? "(root)"}' did not resolve to an array; no rows ingested.");
                    break;
                }
                if (records.Count == 0) break; // a short/empty page ends pagination

                pagesPulled++;
                foreach (var rec in records)
                {
                    if (rec is not JsonObject ro)
                    {
                        // a scalar/array element becomes a single-column "value" row
                        ro = new JsonObject { ["value"] = rec?.DeepClone() };
                    }
                    foreach (var kv in ro)
                        if (seen.Add(kv.Key)) header.Add(kv.Key);

                    rows.Add(ro);
                    if (rows.Count >= MaxRestRows) { capHit = true; capReason = $"row cap {MaxRestRows:N0}"; break; }
                }
                if (capHit) break;
                if (records.Count < pageSize && pagingMode == "page") break; // last page (short page)
                if (pagingMode == "page" && page + 1 >= maxPages) { capHit = true; capReason = $"page cap {maxPages:N0}"; }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            result.Notes.Add("rest fetch failed: " + ex.Message);
            if (rows.Count == 0) return; // nothing usable
        }

        if (rows.Count == 0)
        {
            if (header.Count == 0) result.Notes.Add("rest: no records flattened (empty result).");
            return;
        }
        if (header.Count == 0) header.Add("value");

        // flatten to a CSV through the shared sink (canonical shape)
        string dest = Path.Combine(workDir, table + ".csv");
        using (var sink = new CsvSink(dest, header))
            foreach (var ro in rows)
                sink.WriteRow(header.Select(h => CellToString(ro[h])).ToList());

        CloudFileStaging.RegisterCsv(table, dest, result);
        result.Notes.Add($"rest: ingested {rows.Count:N0} record(s) across {pagesPulled} page(s) into '{table}'.");
        if (capHit)
            result.Notes.Add($"rest: pagination CAP reached ({capReason}) - the pull may be incomplete; not all source rows were ingested.");
    }

    // ---- HTTP helpers --------------------------------------------------------------------------

    /// <summary>GET a URL with optional headers and return the parsed JSON root (or null on a non-success /
    /// non-JSON response). Reads the body through a size-capped stream so a huge response cannot exhaust the
    /// box.</summary>
    private static JsonNode? GetJson(string url, IReadOnlyList<(string name, string value)> headers, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        ApplyHeaders(msg, headers);
        // async: HttpDefaults uses WinHttpHandler on Windows, which rejects the synchronous Send/ReadAsStream.
        using var resp = _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"HTTP {(int)resp.StatusCode} from the endpoint.");

        using var stream = resp.Content.ReadAsStreamAsync(ct).GetAwaiter().GetResult();
        using var capped = new CappedReadStream(stream, MaxDownloadBytes);
        try { return JsonNode.Parse(capped); }
        catch (JsonException) { return null; }
    }

    /// <summary>Download a URL to <paramref name="dest"/> with a hard byte cap. Returns the byte count, or -1
    /// if the fetch failed or the cap was exceeded (a note is recorded in either case).</summary>
    private static long Download(string url, IReadOnlyList<(string name, string value)> headers, string dest, IngestResult result, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        ApplyHeaders(msg, headers);
        // async: HttpDefaults uses WinHttpHandler on Windows, which rejects the synchronous Send/ReadAsStream.
        using var resp = _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).GetAwaiter().GetResult();
        if (!resp.IsSuccessStatusCode)
        {
            result.Notes.Add($"url fetch returned HTTP {(int)resp.StatusCode}; skipped.");
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
                result.Notes.Add($"url fetch exceeded the {MaxDownloadBytes / (1024 * 1024)} MB cap; skipped (no partial file ingested).");
                return -1;
            }
            fs.Write(buf, 0, n);
        }
        return total;
    }

    /// <summary>Build the per-request header list: any caller-supplied headers, plus a Bearer Authorization
    /// from the job's access token when one is present and no explicit Authorization was given. Header values
    /// are transient - never persisted, never logged.</summary>
    private static List<(string name, string value)> BuildHeaders(JsonNode? headersNode, string? accessToken)
    {
        var list = new List<(string, string)>();
        bool hasAuth = false;
        if (headersNode is JsonObject ho)
            foreach (var kv in ho)
                if (kv.Value is { } v)
                {
                    string name = kv.Key;
                    list.Add((name, v.ToString()));
                    if (name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) hasAuth = true;
                }
        if (!hasAuth && !string.IsNullOrWhiteSpace(accessToken))
            list.Add(("Authorization", "Bearer " + accessToken));
        return list;
    }

    private static void ApplyHeaders(HttpRequestMessage msg, IReadOnlyList<(string name, string value)> headers)
    {
        foreach (var (name, value) in headers)
            if (!msg.Headers.TryAddWithoutValidation(name, value))
                msg.Content?.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>Append a paging query param (and an optional page-size param) to a URL, preserving any
    /// existing query string.</summary>
    private static string AppendQuery(string url, string param, string value, string? sizeParam, int pageSize)
    {
        var sb = new StringBuilder(url);
        sb.Append(url.Contains('?') ? '&' : '?');
        sb.Append(Uri.EscapeDataString(param)).Append('=').Append(Uri.EscapeDataString(value));
        if (sizeParam != null)
            sb.Append('&').Append(Uri.EscapeDataString(sizeParam)).Append('=').Append(pageSize);
        return sb.ToString();
    }

    /// <summary>Resolve a dotted JSON path (e.g. "data.items") to an array node, or treat the root as the
    /// array when no path is given. Returns null when the path does not resolve to an array.</summary>
    private static JsonArray? SelectArray(JsonNode root, string? jsonPath)
    {
        JsonNode? node = root;
        if (!string.IsNullOrWhiteSpace(jsonPath))
            foreach (var seg in jsonPath.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (node is JsonObject o && o.TryGetPropertyValue(seg, out var next)) node = next;
                else return null;
            }
        return node as JsonArray;
    }

    /// <summary>Render a JSON cell value for a CSV: a string/number/bool becomes its literal text; a nested
    /// object/array is serialised as compact JSON (so the column is never silently lost). Null -&gt; empty.</summary>
    private static string CellToString(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };

    private static bool IsHttpUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string GuessKindFromUrl(string url)
    {
        string ext = Path.GetExtension(PathPartOfUrl(url)).ToLowerInvariant();
        return ext is ".xlsx" or ".xls" or ".xlsm" ? "xlsx" : "csv";
    }

    private static string FileStemFromUrl(string url)
    {
        string stem = Path.GetFileNameWithoutExtension(PathPartOfUrl(url));
        return string.IsNullOrWhiteSpace(stem) ? "url_data" : stem;
    }

    private static string PathPartOfUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.AbsolutePath : url;

    private static void TryDeleteFile(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ } }

    private static void TryWipe(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>
/// A read-only stream wrapper that throws once more than <c>cap</c> bytes have been read from the inner
/// stream. Used to bound a REST/JSON response so a hostile or runaway endpoint cannot exhaust the box while
/// the body is being parsed (the download path enforces its own cap inline). Read-forward only.
/// </summary>
internal sealed class CappedReadStream : Stream
{
    private readonly Stream _inner;
    private readonly long _cap;
    private long _read;

    public CappedReadStream(Stream inner, long cap) { _inner = inner; _cap = cap; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int n = _inner.Read(buffer, offset, count);
        _read += n;
        if (_read > _cap)
            throw new InvalidOperationException($"response exceeded the {_cap / (1024 * 1024)} MB cap.");
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _read; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
