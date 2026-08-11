using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The Microsoft Lists connector (id "microsoft-lists") - reads a SharePoint / Microsoft list as ONE table in
/// the canonical CSV shape the report pipeline consumes, so everything downstream (scaffold, bake, AI prompt,
/// download) is unchanged. A list's items are pulled with their expanded <c>fields</c>
/// (/sites/{siteId}/lists/{listId}/items?expand=fields), each item's <c>fields</c> object is flattened to a
/// row, and the union of field names across the items becomes the column set (so a list where not every item
/// fills every field still produces a rectangular CSV).
///
/// Operations against the Microsoft Graph REST API (raw HTTP, no SDK / no NuGet, via
/// <see cref="GraphClient"/>):
///   - <see cref="DiscoverAsync"/> reads the list's column definitions (/lists/{listId}/columns) cheaply to
///     report the table + its columns for the picker; it pulls no items.
///   - <see cref="FetchAsync"/> pages the list's items (?expand=fields, following <c>@odata.nextLink</c>),
///     collects the column union on a first pass, then emits one CSV.
///
/// Default shape is AI auto-shape: a list is an arbitrary schema, so <see cref="FlatSpecBuilder"/> builds one
/// flat table unless a Solution is supplied.
///
/// Auth: the short-lived Microsoft OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/>
/// and is sent as <c>Authorization: Bearer &lt;token&gt;</c>. It is used per job only - never persisted,
/// never logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSV is staged into <c>workDir</c> (which the caller
/// wipes after the bake), and the data is never shared with third parties. Any item cap that bites is
/// recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   siteId : the SharePoint site id that owns the list (required).
///   listId : the list id (required).
/// </summary>
public sealed class MicrosoftListsConnector : IDataSourceConnector
{
    public string Id => "microsoft-lists";
    public string DisplayName => "Microsoft Lists";

    // how many leading data rows to sample when inferring a column's type (matches the other connectors).
    private const int SampleRows = 200;

    // page size requested per items call (Graph caps it; nextLink paging covers the rest).
    private const int PageSize = 200;

    // hard caps so a huge list cannot exhaust the box. Every cap that bites is recorded in Notes.
    private const long MaxItems = 1_000_000;   // items pulled from a single list before the cap bites
    private const int MaxPages = 20_000;        // page-walk guard so a runaway feed cannot loop forever

    // internal Lists/SharePoint bookkeeping fields that add noise to a flat table; dropped from the column set.
    private static readonly HashSet<string> NoiseFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "@odata.etag",
    };

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discovery reads the list's column DEFINITIONS (cheap) to report the table + its columns; Fetch
        // samples real item values to infer the precise types.
        var schema = new SchemaDiscovery();

        string? siteId = req.Param("siteId");
        string? listId = req.Param("listId");
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(listId))
            return schema; // nothing to inspect - the caller surfaces the missing ids

        string url = $"/sites/{Esc(siteId!)}/lists/{Esc(listId!)}/columns?$select=name,displayName,hidden,readOnly";
        JsonNode? root = await GraphClient.GetJsonAsync(url, req.AccessToken, DisplayName, ct).ConfigureAwait(false);

        var ts = new TableSchema(CloudFileStaging.SafeTableName(listId!));
        if (root?["value"] is JsonArray cols)
            foreach (var c in cols)
            {
                // skip hidden system columns; prefer the field's internal name (what the items' fields use).
                if ((bool?)c?["hidden"] == true) continue;
                string name = GraphClient.Str(c?["name"]);
                if (name.Length == 0) name = GraphClient.Str(c?["displayName"]);
                if (name.Length == 0 || NoiseFields.Contains(name)) continue;
                ts.Columns.Add(new ColumnSchema(name, "string"));
            }
        schema.Tables.Add(ts);
        return schema;
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? siteId = req.Param("siteId");
        string? listId = req.Param("listId");
        if (string.IsNullOrWhiteSpace(siteId) || string.IsNullOrWhiteSpace(listId))
        {
            result.Notes.Add("Both siteId and listId are required; nothing to ingest.");
            return result;
        }

        // first pass: page every item's fields object, collecting the column union (insertion order preserved)
        // and the per-item flattened rows.
        var columns = new List<string>();
        var columnSet = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<Dictionary<string, string>>();
        bool capHit = false;

        string start = $"/sites/{Esc(siteId!)}/lists/{Esc(listId!)}/items?expand=fields&$top={PageSize}";
        await GraphClient.ForEachPageAsync(start, req.AccessToken, DisplayName, page =>
        {
            foreach (var item in page)
            {
                if (rows.Count >= MaxItems) { capHit = true; return false; }
                if (item?["fields"] is not JsonObject fields) continue;

                var row = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in fields)
                {
                    if (NoiseFields.Contains(kv.Key)) continue;
                    if (columnSet.Add(kv.Key)) columns.Add(kv.Key);
                    row[kv.Key] = GraphClient.Str(kv.Value);
                }
                rows.Add(row);
            }
            return true; // keep paging
        }, MaxPages, ct).ConfigureAwait(false);

        if (columns.Count == 0)
        {
            result.Notes.Add("The list returned no fields; nothing was ingested.");
            return result;
        }

        // second pass: emit one CSV over the column union, padding any field a given item did not carry.
        string table = CloudFileStaging.SafeTableName(listId!);
        string dest = Path.Combine(workDir, table + ".csv");

        var samples = new List<string?>[columns.Count];
        for (int i = 0; i < columns.Count; i++) samples[i] = new List<string?>();

        long dataRows = 0;
        using (var sink = new CsvSink(dest, columns))
        {
            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var fields = new string?[columns.Count];
                for (int c = 0; c < columns.Count; c++)
                {
                    string? v = row.TryGetValue(columns[c], out var val) ? val : null;
                    fields[c] = v;
                    if (dataRows < SampleRows) samples[c].Add(v);
                }
                sink.WriteRow(fields);
                dataRows++;
            }
        }

        var ts = new TableSchema(table);
        for (int i = 0; i < columns.Count; i++)
            ts.Columns.Add(new ColumnSchema(columns[i], ColumnTypeInference.Infer(samples[i])));
        result.Schema.Tables.Add(ts);
        result.Tables[table] = dest;
        result.Notes.Add($"list ingested {dataRows:N0} item(s) into '{table}' ({columns.Count:N0} column(s)).");
        if (capHit)
            result.Notes.Add($"item CAP reached ({MaxItems:N0} items) - the pull is incomplete; not all items were ingested.");
        return result;
    }

    /// <summary>URL-escape a Graph path segment (a site / list id).</summary>
    private static string Esc(string s) => Uri.EscapeDataString(s);
}
