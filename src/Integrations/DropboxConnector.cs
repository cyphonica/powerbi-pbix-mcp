using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The Dropbox connector (id "dropbox") - pulls ONE CSV or Excel file the customer picked from their Dropbox
/// into the canonical CSV shape the report pipeline consumes, so everything downstream (scaffold, bake, AI
/// prompt, download) is unchanged. It is deliberately thin: an OAuth Bearer token plus the file's Dropbox
/// path, download the file's bytes, then run the SAME CSV / Excel staging an uploaded file would get
/// (<see cref="CloudFileStaging"/>) - a .csv passes through; an .xlsx / .xls workbook is expanded one CSV per
/// worksheet, the sheet name becoming the table name.
///
/// Two operations against the Dropbox HTTP API (raw HTTP, no SDK / no NuGet):
///   - <see cref="DiscoverAsync"/> lists the customer's .csv / .xlsx files (files/list_folder) so the dataset
///     picker can offer them; it does not download anything.
///   - <see cref="FetchAsync"/> downloads the chosen file (POST content.dropboxapi.com/2/files/download with
///     the path carried in the <c>Dropbox-API-Arg</c> header) and stages it through the shared file staging.
///
/// Default shape is AI auto-shape: an arbitrary file is an arbitrary schema, so <see cref="FlatSpecBuilder"/>
/// builds one flat table per CSV unless a Solution is supplied.
///
/// Auth: the short-lived Dropbox OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/> and
/// is sent as <c>Authorization: Bearer &lt;token&gt;</c>. It is used per job only - never persisted, never
/// logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. The download size cap that
/// bites is recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   path : the file's Dropbox path (e.g. "/reports/sales.xlsx") - required for a fetch. The file name at the
///          end of the path is used to detect csv vs xlsx.
/// </summary>
public sealed class DropboxConnector : IDataSourceConnector
{
    public string Id => "dropbox";
    public string DisplayName => "Dropbox";

    // Dropbox API hosts. https://www.dropbox.com/developers/documentation/http/documentation
    private const string ContentDownloadUrl = "https://content.dropboxapi.com/2/files/download";
    private const string ListFolderUrl = "https://api.dropboxapi.com/2/files/list_folder";

    // cap on the discovery listing response; the download path enforces its own byte cap.
    private const long MaxResponseBytes = 16L * 1024 * 1024;

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static readonly HttpClient _http = HttpDefaults.New();

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discovery here is a file PICKER feed, not a schema pull: list the .csv/.xlsx items the token can see
        // so a front end can offer them. The real per-column schema is inferred by Fetch once a file is chosen.
        var schema = new SchemaDiscovery();

        // list a folder (the param's path if given, else the app/root folder ""). Names only - no download.
        string folder = req.Param("path") ?? "";
        var body = new JsonObject
        {
            ["path"] = folder,
            ["recursive"] = true,
            ["limit"] = 1000,
        };
        JsonNode? root = await PostJsonAsync(ListFolderUrl, req.AccessToken, body, ct).ConfigureAwait(false);

        if (root?["entries"] is JsonArray entries)
            foreach (var e in entries)
            {
                if (Str(e?[".tag"]) != "file") continue;
                string name = Str(e?["name"]);
                if (name.Length == 0) continue;
                string ext = Path.GetExtension(name).ToLowerInvariant();
                if (ext is ".csv" or ".xlsx" or ".xlsm" or ".xls")
                    schema.Tables.Add(new TableSchema(CloudFileStaging.SafeTableName(Path.GetFileNameWithoutExtension(name))));
            }
        return schema;
    }

    public Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? path = req.Param("path");
        if (string.IsNullOrWhiteSpace(path))
        {
            result.Notes.Add("No path was provided; pick a file to ingest.");
            return Task.FromResult(result);
        }

        // Dropbox carries the request parameters in a header, not the body: Dropbox-API-Arg: {"path":"..."}.
        // The path is JSON-encoded so a name with a quote or unicode is escaped safely.
        string apiArg = JsonSerializer.Serialize(new { path });
        var headers = new[] { new CloudFileStaging.Header("Dropbox-API-Arg", apiArg) };

        // detect csv vs xlsx from the file name at the end of the path.
        string fileName = LastSegment(path);

        long bytes = CloudFileStaging.DownloadAndStage(
            HttpMethod.Post, ContentDownloadUrl, req.AccessToken,
            extraHeaders: headers,
            fileName: fileName, contentType: null, tableHint: fileName,
            workDir, result, ct);

        if (bytes >= 0 && result.Tables.Count == 0)
            result.Notes.Add("The file was downloaded but no table could be staged from it.");
        return Task.FromResult(result);
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    /// <summary>POST a JSON body to a Dropbox RPC URL with the per-job Bearer token and return the parsed JSON
    /// root. The token is applied to the request header only; it is never written to a Note or an exception
    /// message.</summary>
    private static async Task<JsonNode?> PostJsonAsync(string url, string? accessToken, JsonObject body, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Post, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(accessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        msg.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Dropbox API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>The file name at the end of a Dropbox path (after the last '/'); the whole value if there is
    /// no separator.</summary>
    private static string LastSegment(string path)
    {
        int slash = path.TrimEnd('/').LastIndexOf('/');
        return slash >= 0 ? path[(slash + 1)..] : path;
    }

    /// <summary>Read a JSON node as its literal text (string/number/bool); null/missing -&gt; empty.</summary>
    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };
}
