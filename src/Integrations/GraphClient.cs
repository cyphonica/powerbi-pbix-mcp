using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// A small shared helper for the Microsoft Graph connectors (SharePoint, Excel Online, Microsoft Lists, and
/// the OneDrive folder browse). It owns the ONE copy of the "Bearer GET a Graph URL, parse the JSON under a
/// hard byte cap, follow <c>@odata.nextLink</c> paging" logic so each Graph connector stays thin and the
/// token-handling rules are enforced in one place.
///
/// The short-lived OAuth access token is applied to the request <c>Authorization</c> header only - it is
/// never persisted, never logged, and never written to a Note or an exception message. Every response body is
/// read through a <see cref="CappedReadStream"/> so a hostile or runaway source cannot exhaust the box.
///
/// All calls go against the Microsoft Graph v1.0 endpoint (<c>https://graph.microsoft.com/v1.0</c>). The
/// helper builds absolute URLs from a relative Graph path; an already-absolute URL (e.g. an
/// <c>@odata.nextLink</c>) is used verbatim.
/// </summary>
internal static class GraphClient
{
    /// <summary>The Microsoft Graph v1.0 base. https://learn.microsoft.com/graph/use-the-api</summary>
    public const string Base = "https://graph.microsoft.com/v1.0";

    /// <summary>Hard cap on any single Graph response body (a metadata / value page, not a file download).</summary>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static HttpClient _http = HttpDefaults.New();

    /// <summary>Test-only seam: replace the shared HttpClient's transport with a stub handler so the Graph
    /// connectors can be driven end to end offline (no live token, no network). Returns an
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

    /// <summary>Turn a Graph path or absolute URL into the absolute URL to call. A path beginning with '/'
    /// (or anything that is not already an http(s) URL) is appended to <see cref="Base"/>; an absolute URL
    /// (e.g. an <c>@odata.nextLink</c>) is returned unchanged.</summary>
    public static string Url(string pathOrUrl)
    {
        if (pathOrUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || pathOrUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return pathOrUrl;
        return Base + (pathOrUrl.StartsWith('/') ? pathOrUrl : "/" + pathOrUrl);
    }

    /// <summary>GET a Graph path/URL with the per-job Bearer token and return the parsed JSON root, read under
    /// the response byte cap. The token is applied to the request header only; it is never written to a Note
    /// or an exception message.</summary>
    /// <param name="provider">A neutral, customer-facing label for the source (e.g. "SharePoint") used in any
    /// error message - never the internal engine name.</param>
    public static async Task<JsonNode?> GetJsonAsync(string pathOrUrl, string? accessToken, string provider, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, Url(pathOrUrl));
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(accessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"{provider} (Microsoft Graph) returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Follow <c>@odata.nextLink</c> paging from a starting Graph path/URL, invoking <paramref name="onPage"/>
    /// with each page's <c>value</c> array (empty array when a page carries no <c>value</c>). The callback
    /// returns <c>false</c> to stop paging early (e.g. a cap bit); <paramref name="maxPages"/> bounds the walk
    /// so a runaway feed cannot loop forever. Returns the number of pages consumed.
    /// </summary>
    public static async Task<int> ForEachPageAsync(
        string startPathOrUrl, string? accessToken, string provider,
        Func<JsonArray, bool> onPage, int maxPages, CancellationToken ct)
    {
        string? next = startPathOrUrl;
        int pages = 0;
        while (next is not null && pages < maxPages)
        {
            ct.ThrowIfCancellationRequested();
            JsonNode? root = await GetJsonAsync(next, accessToken, provider, ct).ConfigureAwait(false);
            pages++;

            var value = root?["value"] as JsonArray ?? new JsonArray();
            bool keepGoing = onPage(value);
            if (!keepGoing) break;

            next = (string?)root?["@odata.nextLink"];
        }
        return pages;
    }

    /// <summary>Read a JSON node as its literal text (string/number/bool); null/missing -&gt; empty. A nested
    /// object/array is rendered as compact JSON so a Lists field that holds a structured value still flattens
    /// to a single cell rather than being dropped.</summary>
    public static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };
}
