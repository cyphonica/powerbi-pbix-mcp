using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The OneDrive connector (id "onedrive") - pulls ONE CSV or Excel file the customer picked from their
/// Microsoft OneDrive into the canonical CSV shape the report pipeline consumes, so everything downstream
/// (scaffold, bake, AI prompt, download) is unchanged. It is deliberately thin: an OAuth Bearer token plus a
/// Graph file id, download the file's bytes, then run the SAME CSV / Excel staging an uploaded file would get
/// (<see cref="CloudFileStaging"/>) - a .csv passes through; an .xlsx / .xls workbook is expanded one CSV per
/// worksheet, the sheet name becoming the table name.
///
/// Two operations against the Microsoft Graph REST API (raw HTTP, no SDK / no NuGet):
///   - <see cref="DiscoverAsync"/> offers the dataset picker its file list. By default it searches the drive
///     for spreadsheet-ish files (a Graph search over the drive). When a <c>folderId</c> param is supplied it
///     BROWSES that folder instead - listing its children (/me/drive/items/{folderId}/children) - so a
///     front end can drill into folders rather than only seeing search hits. It downloads nothing.
///   - <see cref="FetchAsync"/> downloads the chosen file
///     (GET /me/drive/items/{fileId}/content) and stages it through the shared file staging.
///
/// Default shape is AI auto-shape: an arbitrary file is an arbitrary schema, so <see cref="FlatSpecBuilder"/>
/// builds one flat table per CSV unless a Solution is supplied.
///
/// Auth: the short-lived Microsoft OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/>
/// and is sent as <c>Authorization: Bearer &lt;token&gt;</c>. It is used per job only - never persisted, never
/// logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. The download size cap that
/// bites is recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   fileId   : the Graph drive-item id of the picked file (required for a fetch).
///   fileName : the file's name, used to detect csv vs xlsx (optional but recommended).
///   folderId : (discovery only) a drive-item id of a folder to BROWSE - its children are listed instead of a
///              drive-wide search. Omit to keep the default search-from-root behaviour.
/// </summary>
public sealed class OneDriveConnector : IDataSourceConnector
{
    public string Id => "onedrive";
    public string DisplayName => "OneDrive";

    // Microsoft Graph base. https://learn.microsoft.com/graph/api/driveitem-get-content
    private const string GraphBase = "https://graph.microsoft.com/v1.0/me/drive/";

    // cap on the discovery search response (the picker listing); the download path enforces its own byte cap.
    private const long MaxResponseBytes = 16L * 1024 * 1024;

    // most files the picker would offer to list before the listing is truncated (a note is added if it bites).
    private const int MaxListed = 200;

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static readonly HttpClient _http = HttpDefaults.New();

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discovery here is a file PICKER feed, not a schema pull: list the items the token can see so a
        // front end can offer them. The real per-column schema is inferred by Fetch once a file is chosen.
        var schema = new SchemaDiscovery();

        string? folderId = req.Param("folderId");
        bool browsing = !string.IsNullOrWhiteSpace(folderId);

        // Browse mode lists a folder's children (so a front end can drill into folders); the default lists a
        // drive-wide search for spreadsheet-ish files. We only read names here - nothing is downloaded.
        string url = browsing
            ? GraphBase + "items/" + Uri.EscapeDataString(folderId!) + "/children?$top=" + MaxListed
              + "&$select=id,name,file,folder&$orderby=name"
            : GraphBase + "root/search(q='.csv')?$top=" + MaxListed
              + "&$select=id,name,file&$orderby=name";
        JsonNode? root = await GetJsonAsync(url, req.AccessToken, ct).ConfigureAwait(false);

        if (root?["value"] is JsonArray items)
            foreach (var it in items)
            {
                string name = Str(it?["name"]);
                if (name.Length == 0) continue;

                // when browsing, surface sub-folders too so a front end can drill into them; a folder has a
                // "folder" facet and no "file" facet.
                if (browsing && it?["folder"] is not null && it?["file"] is null)
                {
                    schema.Tables.Add(new TableSchema(CloudFileStaging.SafeTableName(name)));
                    continue;
                }

                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is ".csv" or ".xlsx" or ".xlsm" or ".xls")
                    // one "table" per offered file (the picker label); columns are filled in on fetch.
                    schema.Tables.Add(new TableSchema(CloudFileStaging.SafeTableName(Path.GetFileNameWithoutExtension(name))));
            }
        return schema;
    }

    public Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? fileId = req.Param("fileId");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            result.Notes.Add("No fileId was provided; pick a file to ingest.");
            return Task.FromResult(result);
        }

        string? fileName = req.Param("fileName");
        // GET /me/drive/items/{fileId}/content streams the raw bytes of the picked file.
        string url = GraphBase + "items/" + Uri.EscapeDataString(fileId) + "/content";

        long bytes = CloudFileStaging.DownloadAndStage(
            HttpMethod.Get, url, req.AccessToken,
            extraHeaders: Array.Empty<CloudFileStaging.Header>(),
            fileName: fileName, contentType: null, tableHint: fileName,
            workDir, result, ct);

        if (bytes >= 0 && result.Tables.Count == 0)
            result.Notes.Add("The file was downloaded but no table could be staged from it.");
        return Task.FromResult(result);
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    /// <summary>GET a Graph URL with the per-job Bearer token and return the parsed JSON root. The token is
    /// applied to the request header only; it is never written to a Note or an exception message.</summary>
    private static async Task<JsonNode?> GetJsonAsync(string url, string? accessToken, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(accessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"OneDrive (Microsoft Graph) returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Read a JSON node as its literal text (string/number/bool); null/missing -&gt; empty.</summary>
    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };
}
