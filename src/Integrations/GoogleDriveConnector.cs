using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The Google Drive connector (id "google-drive") - pulls ONE CSV or Excel file the customer picked from their
/// Google Drive into the canonical CSV shape the report pipeline consumes, so everything downstream (scaffold,
/// bake, AI prompt, download) is unchanged. It is deliberately thin: an OAuth Bearer token plus a Drive file
/// id, download the file's bytes, then run the SAME CSV / Excel staging an uploaded file would get
/// (<see cref="CloudFileStaging"/>) - a .csv passes through; an .xlsx / .xls workbook is expanded one CSV per
/// worksheet, the sheet name becoming the table name.
///
/// A Google-native Sheet has no downloadable bytes, so it is EXPORTED as an .xlsx
/// (GET /files/{id}/export?mimeType=...spreadsheetml.sheet) and then staged exactly like an uploaded workbook
/// (one CSV per tab). A regular uploaded file (a .csv or .xlsx stored in Drive) is fetched verbatim with
/// ?alt=media. The connector picks the right path from the file's mimeType.
///
/// Two operations against the Google Drive REST API v3 (raw HTTP, no SDK / no NuGet):
///   - <see cref="DiscoverAsync"/> lists the customer's CSV / Excel / native-Sheet files (files.list) so the
///     dataset picker can offer them; it does not download anything.
///   - <see cref="FetchAsync"/> downloads (or exports) the chosen file and stages it through the shared file
///     staging.
///
/// Default shape is AI auto-shape: an arbitrary file is an arbitrary schema, so <see cref="FlatSpecBuilder"/>
/// builds one flat table per CSV unless a Solution is supplied.
///
/// Auth: the short-lived Google OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/> and
/// is sent as <c>Authorization: Bearer &lt;token&gt;</c>. It is used per job only - never persisted, never
/// logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. The download size cap that
/// bites is recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   fileId   : the Drive file id of the picked file (required for a fetch).
///   mimeType : the file's mime type, used to decide download-verbatim vs export-as-xlsx and to detect csv vs
///              xlsx (optional; if omitted the file's metadata is read to find it).
///   fileName : the file's name, used to detect csv vs xlsx for an uploaded file (optional).
/// </summary>
public sealed class GoogleDriveConnector : IDataSourceConnector
{
    public string Id => "google-drive";
    public string DisplayName => "Google Drive";

    // Google Drive API v3 base. https://developers.google.com/drive/api/reference/rest/v3/files
    private const string ApiBase = "https://www.googleapis.com/drive/v3/files/";

    // mime type of a Google-native spreadsheet (no bytes; must be exported) and the xlsx export target.
    private const string GoogleSheetMime = "application/vnd.google-apps.spreadsheet";
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    // cap on the discovery / metadata response; the download path enforces its own byte cap.
    private const long MaxResponseBytes = 16L * 1024 * 1024;

    // most files the picker would offer to list before the listing is truncated.
    private const int MaxListed = 200;

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static readonly HttpClient _http = HttpDefaults.New();

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discovery here is a file PICKER feed, not a schema pull: list the CSV / Excel / native-Sheet items
        // the token can see so a front end can offer them. The real schema is inferred by Fetch once chosen.
        var schema = new SchemaDiscovery();

        // files.list filtered to the mime types we can stage (csv, xlsx, native sheet), names only.
        string q = "trashed=false and (mimeType='text/csv' or mimeType='" + XlsxMime + "' or mimeType='" + GoogleSheetMime + "')";
        string url = ApiBase + "?q=" + Uri.EscapeDataString(q)
                     + "&pageSize=" + MaxListed + "&fields=" + Uri.EscapeDataString("files(id,name,mimeType)");
        JsonNode? root = await GetJsonAsync(url, req.AccessToken, ct).ConfigureAwait(false);

        if (root?["files"] is JsonArray files)
            foreach (var f in files)
            {
                string name = Str(f?["name"]);
                if (name.Length == 0) continue;
                schema.Tables.Add(new TableSchema(CloudFileStaging.SafeTableName(Path.GetFileNameWithoutExtension(name))));
            }
        return schema;
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? fileId = req.Param("fileId");
        if (string.IsNullOrWhiteSpace(fileId))
        {
            result.Notes.Add("No fileId was provided; pick a file to ingest.");
            return result;
        }

        string? mimeType = req.Param("mimeType");
        string? fileName = req.Param("fileName");

        // if we were not told the mime type, read the file's metadata so we know whether to export it.
        if (string.IsNullOrWhiteSpace(mimeType))
        {
            string metaUrl = ApiBase + Uri.EscapeDataString(fileId) + "?fields=" + Uri.EscapeDataString("name,mimeType");
            JsonNode? meta = await GetJsonAsync(metaUrl, req.AccessToken, ct).ConfigureAwait(false);
            mimeType = Str(meta?["mimeType"]);
            if (string.IsNullOrWhiteSpace(fileName)) fileName = Str(meta?["name"]);
        }

        string url;
        string? contentType;
        string effectiveName;
        if (string.Equals(mimeType, GoogleSheetMime, StringComparison.OrdinalIgnoreCase))
        {
            // a Google-native Sheet has no stored bytes; export it as an .xlsx workbook, then stage per tab.
            url = ApiBase + Uri.EscapeDataString(fileId) + "/export?mimeType=" + Uri.EscapeDataString(XlsxMime);
            contentType = XlsxMime;
            effectiveName = string.IsNullOrWhiteSpace(fileName) ? null! : EnsureExtension(fileName!, ".xlsx");
        }
        else
        {
            // a regular uploaded file (.csv / .xlsx stored in Drive): download its bytes verbatim.
            url = ApiBase + Uri.EscapeDataString(fileId) + "?alt=media";
            contentType = string.IsNullOrWhiteSpace(mimeType) ? null : mimeType;
            effectiveName = fileName!;
        }

        long bytes = CloudFileStaging.DownloadAndStage(
            HttpMethod.Get, url, req.AccessToken,
            extraHeaders: Array.Empty<CloudFileStaging.Header>(),
            fileName: effectiveName, contentType: contentType, tableHint: fileName,
            workDir, result, ct);

        if (bytes >= 0 && result.Tables.Count == 0)
            result.Notes.Add("The file was downloaded but no table could be staged from it.");
        return result;
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    /// <summary>GET a Drive API URL with the per-job Bearer token and return the parsed JSON root. The token is
    /// applied to the request header only; it is never written to a Note or an exception message.</summary>
    private static async Task<JsonNode?> GetJsonAsync(string url, string? accessToken, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(accessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Drive API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Ensure a file name carries the given extension (so an exported native Sheet stages as a
    /// workbook even when its Drive name has no extension).</summary>
    private static string EnsureExtension(string name, string ext)
        => string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase) ? name : name + ext;

    /// <summary>Read a JSON node as its literal text (string/number/bool); null/missing -&gt; empty.</summary>
    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };
}
