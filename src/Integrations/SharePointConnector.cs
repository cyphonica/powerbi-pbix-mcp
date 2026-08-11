using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The SharePoint connector (id "sharepoint") - pulls ONE CSV or Excel file the customer picked from a
/// SharePoint document library into the canonical CSV shape the report pipeline consumes, so everything
/// downstream (scaffold, bake, AI prompt, download) is unchanged. It is deliberately thin: an OAuth Bearer
/// token plus a drive id and a drive-item id, download the file's bytes, then run the SAME CSV / Excel
/// staging an uploaded file would get (<see cref="CloudFileStaging"/>) - a .csv passes through; an
/// .xlsx / .xls workbook is expanded one CSV per worksheet, the sheet name becoming the table name.
///
/// Two operations against the Microsoft Graph REST API (raw HTTP, no SDK / no NuGet, via
/// <see cref="GraphClient"/>):
///   - <see cref="DiscoverAsync"/> is a three-level picker feed - sites (/sites?search=), then a site's
///     document libraries (/sites/{siteId}/drives), then a folder's children
///     (/drives/{driveId}/items/{itemId}/children, or /drives/{driveId}/root/children for the library root) -
///     returning names + ids so a front end can browse. It downloads nothing.
///   - <see cref="FetchAsync"/> downloads the chosen file (GET /drives/{driveId}/items/{itemId}/content) and
///     stages it through the shared file staging.
///
/// Default shape is AI auto-shape: an arbitrary file is an arbitrary schema, so <see cref="FlatSpecBuilder"/>
/// builds one flat table per CSV unless a Solution is supplied.
///
/// Auth: the short-lived Microsoft OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/>
/// and is sent as <c>Authorization: Bearer &lt;token&gt;</c>. It is used per job only - never persisted,
/// never logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. The download size cap that
/// bites is recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   For discovery (each level is optional; absent means the level above):
///     search   : a site search term; when present the picker lists matching sites (/sites?search=).
///     siteId   : a site id; when present (and no driveId) the picker lists that site's drives.
///     driveId  : a document-library drive id; when present the picker lists that drive's items.
///     itemId   : a folder drive-item id; when present (with driveId) the picker lists that folder's children,
///                else the library root is listed.
///   For a fetch:
///     driveId  : the document-library drive id (required).
///     itemId   : the file's drive-item id (required).
///     fileName : the file's name, used to detect csv vs xlsx (optional but recommended).
/// </summary>
public sealed class SharePointConnector : IDataSourceConnector
{
    public string Id => "sharepoint";
    public string DisplayName => "SharePoint";

    // most rows the picker would offer per level before the listing is truncated.
    private const int MaxListed = 200;

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discovery here is a PICKER feed across three levels (site -> drive -> folder), not a schema pull. The
        // chosen level is the deepest param supplied. Each emitted "table" carries a name + the ids needed to
        // drill in or fetch (encoded into the table name so a front end can read them back). Nothing is
        // downloaded; the real per-column schema is inferred by Fetch once a file is chosen.
        var schema = new SchemaDiscovery();

        string? driveId = req.Param("driveId");
        string? itemId = req.Param("itemId");
        string? siteId = req.Param("siteId");
        string? search = req.Param("search");

        string url;
        if (!string.IsNullOrWhiteSpace(driveId))
        {
            // a chosen drive (document library): list a folder's children, or the library root by default.
            url = string.IsNullOrWhiteSpace(itemId)
                ? $"/drives/{Esc(driveId!)}/root/children?$top={MaxListed}&$select=id,name,folder,file"
                : $"/drives/{Esc(driveId!)}/items/{Esc(itemId!)}/children?$top={MaxListed}&$select=id,name,folder,file";
        }
        else if (!string.IsNullOrWhiteSpace(siteId))
        {
            // a chosen site: list its document-library drives.
            url = $"/sites/{Esc(siteId!)}/drives?$top={MaxListed}&$select=id,name";
        }
        else
        {
            // top level: search sites (a blank search term lists what the token can see).
            url = $"/sites?search={Uri.EscapeDataString(search ?? string.Empty)}&$top={MaxListed}&$select=id,name,displayName";
        }

        JsonNode? root = await GraphClient.GetJsonAsync(url, req.AccessToken, DisplayName, ct).ConfigureAwait(false);
        if (root?["value"] is JsonArray items)
            foreach (var it in items)
            {
                string name = GraphClient.Str(it?["displayName"]);
                if (name.Length == 0) name = GraphClient.Str(it?["name"]);
                if (name.Length == 0) continue;
                schema.Tables.Add(new TableSchema(CloudFileStaging.SafeTableName(name)));
            }
        return schema;
    }

    public Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? driveId = req.Param("driveId");
        string? itemId = req.Param("itemId");
        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(itemId))
        {
            result.Notes.Add("Both driveId and itemId are required; pick a file to ingest.");
            return Task.FromResult(result);
        }

        string? fileName = req.Param("fileName");
        // GET /drives/{driveId}/items/{itemId}/content streams the raw bytes of the picked file.
        string url = GraphClient.Url($"/drives/{Esc(driveId!)}/items/{Esc(itemId!)}/content");

        long bytes = CloudFileStaging.DownloadAndStage(
            HttpMethod.Get, url, req.AccessToken,
            extraHeaders: Array.Empty<CloudFileStaging.Header>(),
            fileName: fileName, contentType: null, tableHint: fileName,
            workDir, result, ct);

        if (bytes >= 0 && result.Tables.Count == 0)
            result.Notes.Add("The file was downloaded but no table could be staged from it.");
        return Task.FromResult(result);
    }

    /// <summary>URL-escape a Graph path segment (a drive / item / site id).</summary>
    private static string Esc(string s) => Uri.EscapeDataString(s);
}
