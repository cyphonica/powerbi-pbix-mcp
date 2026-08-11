using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;

namespace SuperBiMcp.Services;

/// <summary>
/// The Power BI Service / Fabric REST client (Wave F, the cloud tier): publish an already-built artifact
/// (.pbix via the Import API, or a PBIP project at definition level via the Fabric items API), trigger and
/// schedule dataset refreshes, mint embed tokens and run DAX - all over pure REST, so it works from any OS
/// with no Power BI Desktop.
///
/// Token custody is absolute: every method takes the short-lived AAD access token as a parameter and applies
/// it to the request <c>Authorization: Bearer</c> header ONLY - it is never stored, never logged, and never
/// written to an exception message or a result payload. Every response body is read through a
/// <see cref="CappedReadStream"/> so a hostile or runaway endpoint cannot exhaust the box.
///
/// Built in here (the Graph helper deliberately lacks them): 429 handling (Retry-After honoured, bounded
/// retries) and long-running-operation support (202 + Location / x-ms-operation-id polling, bounded).
/// </summary>
internal static class FabricRest
{
    /// <summary>The Power BI REST base. https://learn.microsoft.com/rest/api/power-bi/</summary>
    public const string PbiBase = "https://api.powerbi.com/v1.0/myorg";

    /// <summary>The Fabric REST base (definition-level item publish). https://learn.microsoft.com/rest/api/fabric/</summary>
    public const string FabricBase = "https://api.fabric.microsoft.com/v1";

    /// <summary>Hard cap on any single response body (metadata / query results, not a file download).</summary>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    private const int MaxAttempts = 4;       // per call: one try + up to three 429 retries
    private const int MaxPollAttempts = 60;  // import / LRO polling bound

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static HttpClient _http = HttpDefaults.New();

    // test seam: the suite zeroes these so the 429-retry and import/LRO poll loops run instantly offline.
    internal static TimeSpan RetryFloor = TimeSpan.FromSeconds(1);
    internal static TimeSpan PollDelay = TimeSpan.FromSeconds(2);

    /// <summary>Test-only seam: replace the shared HttpClient's transport with a stub handler so the service
    /// client can be driven end to end offline (no live token, no network). Returns an
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

    // ---- URL builders --------------------------------------------------------------------------

    /// <summary>Turn a Power BI path or absolute URL into the absolute URL to call. A relative path is
    /// appended to <see cref="PbiBase"/>; an absolute URL (e.g. an <c>@odata.nextLink</c>) is unchanged.</summary>
    public static string Url(string pathOrUrl) => Rebase(PbiBase, pathOrUrl);

    /// <summary>Same as <see cref="Url"/> but against the Fabric base (items / operations).</summary>
    public static string FabricUrl(string pathOrUrl) => Rebase(FabricBase, pathOrUrl);

    private static string Rebase(string baseUrl, string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return pathOrUrl;
        return baseUrl + (pathOrUrl.StartsWith('/') ? pathOrUrl : "/" + pathOrUrl);
    }

    private static string Esc(string s) => Uri.EscapeDataString(s);

    // ---- transport core: Bearer + bounded 429 retry + capped JSON parse -------------------------

    /// <summary>Send a request built by <paramref name="make"/> (a factory, so a 429 retry gets a FRESH
    /// message + content). The token goes on the Authorization header only. 429s are retried up to the
    /// bound honouring Retry-After; every other status is returned for the caller to interpret.</summary>
    private static async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, string accessToken, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            using var msg = make();
            msg.Headers.Accept.ParseAdd("application/json");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)resp.StatusCode != 429 || attempt >= MaxAttempts)
                return resp;
            TimeSpan wait = RetryAfter(resp) ?? RetryFloor;
            resp.Dispose();
            await Task.Delay(wait, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The server-instructed backoff, clamped to [0, 60] s so a hostile header cannot hang the box.</summary>
    private static TimeSpan? RetryAfter(HttpResponseMessage resp)
    {
        var ra = resp.Headers.RetryAfter;
        TimeSpan? wait = ra?.Delta ?? (ra?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
        if (wait is null) return null;
        if (wait < TimeSpan.Zero) return TimeSpan.Zero;
        return wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
    }

    /// <summary>The uniform failure: status + reason ONLY - never the token, never the raw body (it can
    /// carry request context).</summary>
    private static InvalidOperationException Fail(HttpResponseMessage resp)
        => new($"Power BI service returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

    /// <summary>Parse a response body under the byte cap; an empty body (legal on several 200/202 replies)
    /// is null rather than a parse error.</summary>
    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        using var ms = new MemoryStream();
        await capped.CopyToAsync(ms, ct).ConfigureAwait(false);
        if (ms.Length == 0) return null;
        ms.Position = 0;
        return await JsonNode.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
    }

    private static StringContent JsonBody(JsonObject body)
        => new(body.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>GET an (already absolute or relative-to-PbiBase) URL and return the parsed JSON root.</summary>
    public static async Task<JsonNode?> GetJsonAsync(string pathOrUrl, string accessToken, CancellationToken ct)
    {
        using var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(pathOrUrl)), accessToken, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp);
        return await ReadJsonAsync(resp, ct).ConfigureAwait(false);
    }

    /// <summary>Collect every item of a paged <c>value</c> envelope, following <c>@odata.nextLink</c>
    /// (Power BI) or <c>continuationUri</c> (Fabric) up to <paramref name="maxPages"/>.</summary>
    public static async Task<JsonArray> GetAllPagesAsync(string startUrl, string accessToken, int maxPages, CancellationToken ct)
    {
        var all = new JsonArray();
        string? next = startUrl;
        int pages = 0;
        while (next is not null && pages < maxPages)
        {
            ct.ThrowIfCancellationRequested();
            JsonNode? root = await GetJsonAsync(next, accessToken, ct).ConfigureAwait(false);
            pages++;
            if (root?["value"] is JsonArray value)
                foreach (var item in value)
                    all.Add(item?.DeepClone());
            next = (string?)root?["@odata.nextLink"] ?? (string?)root?["continuationUri"];
        }
        return all;
    }

    // ---- workspaces / datasets / reports ---------------------------------------------------------

    public static async Task<object> ListWorkspacesAsync(string accessToken, CancellationToken ct)
    {
        var ws = await GetAllPagesAsync(Url("/groups"), accessToken, maxPages: 20, ct).ConfigureAwait(false);
        return new { ok = true, count = ws.Count, workspaces = ws };
    }

    public static async Task<object> ListDatasetsAsync(string workspaceId, string accessToken, CancellationToken ct)
    {
        var ds = await GetAllPagesAsync(Url($"/groups/{Esc(workspaceId)}/datasets"), accessToken, maxPages: 20, ct).ConfigureAwait(false);
        return new { ok = true, count = ds.Count, datasets = ds };
    }

    public static async Task<object> ListReportsAsync(string workspaceId, string accessToken, CancellationToken ct)
    {
        var rp = await GetAllPagesAsync(Url($"/groups/{Esc(workspaceId)}/reports"), accessToken, maxPages: 20, ct).ConfigureAwait(false);
        return new { ok = true, count = rp.Count, reports = rp };
    }

    // ---- publish: .pbix import (Import API) ------------------------------------------------------

    /// <summary>Upload a .pbix into a workspace via the Import API (multipart), then poll the import until it
    /// leaves Publishing (bounded). Returns the settled state plus the created report/dataset ids.</summary>
    public static async Task<object> ImportPbixAsync(
        string workspaceId, string pbixPath, string datasetDisplayName, string nameConflict, string accessToken, CancellationToken ct)
    {
        if (!File.Exists(pbixPath))
            throw new InvalidOperationException($"pbix not found: {pbixPath}");
        string name = string.IsNullOrWhiteSpace(datasetDisplayName) ? Path.GetFileNameWithoutExtension(pbixPath) : datasetDisplayName;
        string conflict = string.IsNullOrWhiteSpace(nameConflict) ? "CreateOrOverwrite" : nameConflict;
        string url = Url($"/groups/{Esc(workspaceId)}/imports?datasetDisplayName={Esc(name)}&nameConflict={Esc(conflict)}");

        JsonNode? posted;
        using (var resp = await SendAsync(() =>
        {
            // the factory opens the file per attempt so a 429 retry never re-sends a consumed stream
            var msg = new HttpRequestMessage(HttpMethod.Post, url);
            var file = new StreamContent(File.OpenRead(pbixPath));
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var mp = new MultipartFormDataContent();
            mp.Add(file, "file", name + ".pbix");
            msg.Content = mp;
            return msg;
        }, accessToken, ct).ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode) throw Fail(resp);
            posted = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        }

        string importId = (string?)posted?["id"] ?? "";
        if (importId.Length == 0)
            throw new InvalidOperationException("Power BI accepted the import but returned no import id.");

        string state = (string?)posted?["importState"] ?? "";
        JsonNode? last = posted;
        for (int i = 0; i < MaxPollAttempts && !ImportSettled(state); i++)
        {
            await Task.Delay(PollDelay, ct).ConfigureAwait(false);
            last = await GetJsonAsync($"/groups/{Esc(workspaceId)}/imports/{Esc(importId)}", accessToken, ct).ConfigureAwait(false);
            state = (string?)last?["importState"] ?? "";
        }

        return new
        {
            ok = string.Equals(state, "Succeeded", StringComparison.OrdinalIgnoreCase),
            importId,
            state = state.Length > 0 ? state : "unknown",
            reports = last?["reports"]?.DeepClone(),
            datasets = last?["datasets"]?.DeepClone(),
        };
    }

    private static bool ImportSettled(string importState)
        => importState.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
        || importState.Equals("Failed", StringComparison.OrdinalIgnoreCase);

    // ---- publish: PBIP definition (Fabric items API) ---------------------------------------------

    /// <summary>Create a Fabric item (SemanticModel / Report) from definition parts, or update the existing
    /// item of the same type + display name in place. Handles the 202 long-running-operation reply by polling
    /// Location / x-ms-operation-id until it settles (bounded).</summary>
    public static async Task<JsonObject> CreateOrUpdateItemAsync(
        string workspaceId, string displayName, string itemType,
        IReadOnlyList<(string path, string payloadB64)> parts, string accessToken, CancellationToken ct)
    {
        var partsArr = new JsonArray();
        foreach (var (path, payload) in parts)
            partsArr.Add(new JsonObject { ["path"] = path, ["payload"] = payload, ["payloadType"] = "InlineBase64" });

        string? itemId = await FindItemIdAsync(workspaceId, displayName, itemType, accessToken, ct).ConfigureAwait(false);
        bool updating = itemId != null;

        string url = updating
            ? FabricUrl($"/workspaces/{Esc(workspaceId)}/items/{Esc(itemId!)}/updateDefinition")
            : FabricUrl($"/workspaces/{Esc(workspaceId)}/items");
        var body = updating
            ? new JsonObject { ["definition"] = new JsonObject { ["parts"] = partsArr } }
            : new JsonObject { ["displayName"] = displayName, ["type"] = itemType, ["definition"] = new JsonObject { ["parts"] = partsArr } };

        JsonNode? result;
        string? operationUrl = null;
        using (var resp = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonBody(body) }, accessToken, ct).ConfigureAwait(false))
        {
            if (!resp.IsSuccessStatusCode) throw Fail(resp);
            if (resp.StatusCode == HttpStatusCode.Accepted)
                operationUrl = OperationUrl(resp);
            result = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        }

        string opStatus = operationUrl != null
            ? await PollOperationAsync(operationUrl, accessToken, ct).ConfigureAwait(false)
            : "Succeeded";
        itemId ??= (string?)result?["id"];

        return new JsonObject
        {
            ["ok"] = string.Equals(opStatus, "Succeeded", StringComparison.OrdinalIgnoreCase),
            ["itemId"] = itemId,
            ["type"] = itemType,
            ["displayName"] = displayName,
            ["updated"] = updating,
            ["operation"] = opStatus,
        };
    }

    private static async Task<string?> FindItemIdAsync(string workspaceId, string displayName, string itemType, string accessToken, CancellationToken ct)
    {
        var items = await GetAllPagesAsync(
            FabricUrl($"/workspaces/{Esc(workspaceId)}/items?type={Esc(itemType)}"), accessToken, maxPages: 20, ct).ConfigureAwait(false);
        foreach (var it in items)
            if (it is JsonObject o && string.Equals((string?)o["displayName"], displayName, StringComparison.OrdinalIgnoreCase))
                return (string?)o["id"];
        return null;
    }

    /// <summary>Resolve the poll URL for a 202: prefer the Location header; fall back to x-ms-operation-id.</summary>
    private static string? OperationUrl(HttpResponseMessage resp)
    {
        string? loc = resp.Headers.Location?.ToString();
        if (!string.IsNullOrWhiteSpace(loc)) return loc;
        if (resp.Headers.TryGetValues("x-ms-operation-id", out var vals) && vals.FirstOrDefault() is { Length: > 0 } opId)
            return FabricUrl($"/operations/{Esc(opId)}");
        return null;
    }

    /// <summary>Poll a Fabric long-running operation until it settles or the bounded attempts run out.</summary>
    private static async Task<string> PollOperationAsync(string operationUrl, string accessToken, CancellationToken ct)
    {
        string status = "Running";
        for (int i = 0; i < MaxPollAttempts; i++)
        {
            await Task.Delay(PollDelay, ct).ConfigureAwait(false);
            var op = await GetJsonAsync(operationUrl, accessToken, ct).ConfigureAwait(false);
            status = (string?)op?["status"] ?? "Unknown";
            if (status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                return status;
        }
        return status;
    }

    /// <summary>Publish an on-disk PBIP project at definition level: the semantic model first, then the
    /// report when present, each created or updated in place by display name. <paramref name="pbipFolder"/>
    /// may be the project root (containing &lt;x&gt;.SemanticModel / &lt;x&gt;.Report) or either folder itself -
    /// exactly the tree generate_pbip / scaffold emit.</summary>
    public static async Task<JsonObject> PublishPbipAsync(
        string workspaceId, string pbipFolder, string? displayName, string accessToken, CancellationToken ct)
    {
        string root = pbipFolder.TrimEnd('\\', '/');
        if (root.EndsWith(".SemanticModel", StringComparison.OrdinalIgnoreCase)
            || root.EndsWith(".Report", StringComparison.OrdinalIgnoreCase))
            root = Path.GetDirectoryName(root) ?? root;

        string? smDir = FindPbipFolder(root, ".SemanticModel");
        if (smDir == null)
            throw new InvalidOperationException("no <name>.SemanticModel folder found under the PBIP path.");
        string name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileName(smDir)[..^".SemanticModel".Length]
            : displayName!;

        JsonObject model = await CreateOrUpdateItemAsync(
            workspaceId, name, "SemanticModel", SemanticModelParts(smDir), accessToken, ct).ConfigureAwait(false);

        JsonObject? report = null;
        string? rpDir = FindPbipFolder(root, ".Report");
        if (rpDir != null)
            report = await CreateOrUpdateItemAsync(
                workspaceId, name, "Report", ReportParts(rpDir), accessToken, ct).ConfigureAwait(false);

        bool ok = (bool?)model["ok"] == true && (report == null || (bool?)report["ok"] == true);
        return new JsonObject
        {
            ["ok"] = ok,
            ["workspaceId"] = workspaceId,
            ["displayName"] = name,
            ["semanticModel"] = model,
            ["report"] = report,
        };
    }

    private static string? FindPbipFolder(string root, string suffix)
    {
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root, "*" + suffix, SearchOption.TopDirectoryOnly).FirstOrDefault()
            ?? Directory.GetDirectories(root, "*" + suffix, SearchOption.AllDirectories).FirstOrDefault();
    }

    /// <summary>Map an on-disk PBIP semantic-model folder (&lt;x&gt;.SemanticModel) to Fabric definition parts:
    /// definition.pbism plus every file under definition/** (TMDL), paths relative to the folder with '/'
    /// separators. The .platform / diagram files at the folder root are metadata, not definition - excluded.</summary>
    internal static List<(string path, string payloadB64)> SemanticModelParts(string smFolder)
    {
        if (!Directory.Exists(smFolder))
            throw new InvalidOperationException($"semantic-model folder not found: {smFolder}");
        var parts = new List<(string, string)>();
        string pbism = Path.Combine(smFolder, "definition.pbism");
        if (File.Exists(pbism)) parts.Add(("definition.pbism", B64(pbism)));
        string defDir = Path.Combine(smFolder, "definition");
        if (Directory.Exists(defDir))
            foreach (var f in Directory.GetFiles(defDir, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                parts.Add((RelPath(smFolder, f), B64(f)));
        if (parts.Count == 0)
            throw new InvalidOperationException("no semantic-model definition parts found (definition.pbism / definition/**).");
        return parts;
    }

    /// <summary>Map an on-disk PBIP report folder (&lt;x&gt;.Report) to Fabric definition parts: every file
    /// under it (definition.pbir + report.json or the definition/ tree + StaticResources). The .platform file
    /// and the local .pbi cache are git/Desktop metadata, not definition - excluded.</summary>
    internal static List<(string path, string payloadB64)> ReportParts(string reportFolder)
    {
        if (!Directory.Exists(reportFolder))
            throw new InvalidOperationException($"report folder not found: {reportFolder}");
        var parts = new List<(string, string)>();
        foreach (var f in Directory.GetFiles(reportFolder, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            string rel = RelPath(reportFolder, f);
            if (rel.Equals(".platform", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(".pbi/", StringComparison.OrdinalIgnoreCase))
                continue;
            parts.Add((rel, B64(f)));
        }
        if (parts.Count == 0)
            throw new InvalidOperationException("no report definition parts found under the .Report folder.");
        return parts;
    }

    private static string RelPath(string root, string file)
        => Path.GetRelativePath(root, file).Replace('\\', '/');

    private static string B64(string file) => Convert.ToBase64String(File.ReadAllBytes(file));

    // ---- refresh ---------------------------------------------------------------------------------

    /// <summary>Trigger a dataset refresh. Returns the requestId parsed from the Location header so the
    /// caller can correlate it in get_refresh_status.</summary>
    public static async Task<object> RefreshDatasetAsync(
        string workspaceId, string datasetId, string type, string accessToken, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["type"] = string.IsNullOrWhiteSpace(type) ? "full" : type,
            ["notifyOption"] = "NoNotification",
        };
        using var resp = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url($"/groups/{Esc(workspaceId)}/datasets/{Esc(datasetId)}/refreshes")) { Content = JsonBody(body) },
            accessToken, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp);

        string? loc = resp.Headers.Location?.ToString();
        string requestId = loc is { Length: > 0 } ? loc.TrimEnd('/').Split('/')[^1] : "";
        if (requestId.Length == 0 && resp.Headers.TryGetValues("RequestId", out var vals))
            requestId = vals.FirstOrDefault() ?? "";
        return new { ok = true, requestId, note = "refresh accepted - poll get_refresh_status until it settles." };
    }

    public static async Task<object> GetRefreshStatusAsync(
        string workspaceId, string datasetId, int top, string accessToken, CancellationToken ct)
    {
        if (top <= 0) top = 5;
        var root = await GetJsonAsync($"/groups/{Esc(workspaceId)}/datasets/{Esc(datasetId)}/refreshes?$top={top}", accessToken, ct).ConfigureAwait(false);
        var value = root?["value"] as JsonArray ?? new JsonArray();
        return new { ok = true, count = value.Count, refreshes = value.DeepClone() };
    }

    public static async Task<object> SetRefreshScheduleAsync(
        string workspaceId, string datasetId, bool enabled,
        IReadOnlyList<string> days, IReadOnlyList<string> times, string? localTimeZoneId,
        string accessToken, CancellationToken ct)
    {
        var value = new JsonObject { ["enabled"] = enabled };
        if (days.Count > 0) value["days"] = new JsonArray(days.Select(d => (JsonNode?)d).ToArray());
        if (times.Count > 0) value["times"] = new JsonArray(times.Select(t => (JsonNode?)t).ToArray());
        if (!string.IsNullOrWhiteSpace(localTimeZoneId)) value["localTimeZoneId"] = localTimeZoneId;
        var body = new JsonObject { ["value"] = value };
        using var resp = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Patch, Url($"/groups/{Esc(workspaceId)}/datasets/{Esc(datasetId)}/refreshSchedule")) { Content = JsonBody(body) },
            accessToken, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp);
        return new { ok = true, enabled, days, times, localTimeZoneId };
    }

    // ---- embed -----------------------------------------------------------------------------------

    /// <summary>Mint an embed token for the given datasets / reports / target workspaces. The EMBED token is
    /// the product here (scoped + short-lived, not the AAD token) - returning it is the point.</summary>
    public static async Task<object> GenerateEmbedTokenAsync(
        IReadOnlyList<string> datasetIds, IReadOnlyList<string> reportIds, IReadOnlyList<string> workspaceIds,
        string accessToken, CancellationToken ct)
    {
        var body = new JsonObject();
        if (datasetIds.Count > 0)
            body["datasets"] = new JsonArray(datasetIds.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray());
        if (reportIds.Count > 0)
            body["reports"] = new JsonArray(reportIds.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray());
        if (workspaceIds.Count > 0)
            body["targetWorkspaces"] = new JsonArray(workspaceIds.Select(id => (JsonNode?)new JsonObject { ["id"] = id }).ToArray());
        if (body.Count == 0)
            throw new InvalidOperationException("generate_embed_token needs at least one dataset or report id.");

        using var resp = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url("/GenerateToken")) { Content = JsonBody(body) },
            accessToken, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp);
        var root = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        return new
        {
            ok = true,
            token = (string?)root?["token"],
            tokenId = (string?)root?["tokenId"],
            expiration = (string?)root?["expiration"],
        };
    }

    // ---- DAX over REST (the later S4 seam) --------------------------------------------------------

    public static async Task<object> ExecuteQueriesAsync(
        string workspaceId, string datasetId, string dax, string accessToken, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["queries"] = new JsonArray(new JsonObject { ["query"] = dax }),
            ["serializerSettings"] = new JsonObject { ["includeNulls"] = true },
        };
        using var resp = await SendAsync(
            () => new HttpRequestMessage(HttpMethod.Post, Url($"/groups/{Esc(workspaceId)}/datasets/{Esc(datasetId)}/executeQueries")) { Content = JsonBody(body) },
            accessToken, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) throw Fail(resp);
        var root = await ReadJsonAsync(resp, ct).ConfigureAwait(false);
        return new { ok = true, results = root?["results"]?.DeepClone() };
    }
}
