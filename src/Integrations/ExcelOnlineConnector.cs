using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The Excel Online connector (id "excel-online") - reads a LIVE workbook stored in OneDrive / SharePoint via
/// the Microsoft Graph workbook API, with no full-file download, and turns each worksheet's used range (or a
/// named table) into one CSV in the canonical shape the report pipeline consumes, so everything downstream
/// (scaffold, bake, AI prompt, download) is unchanged. This is the "always reads what is in the cloud right
/// now" path (as opposed to <see cref="SharePointConnector"/> / <see cref="OneDriveConnector"/>, which
/// download the stored bytes).
///
/// Operations against the Microsoft Graph workbook API (raw HTTP, no SDK / no NuGet, via
/// <see cref="GraphClient"/>):
///   - /drives/{driveId}/items/{itemId}/workbook/worksheets                 - list worksheets
///   - /drives/{driveId}/items/{itemId}/workbook/tables                     - list named tables
///   - .../workbook/worksheets/{id}/usedRange(valuesOnly=true)              - a worksheet's used-range values
///   - .../workbook/tables/{id}/headerRowRange and .../tables/{id}/rows     - a named table's header + rows
/// Each returned value matrix becomes a CSV with the first row promoted to headers, via <see cref="CsvSink"/>;
/// a type is inferred per column with <see cref="ColumnTypeInference"/>.
///
/// Selection (params worksheet / table are both optional):
///   - table given     : emit just that named table (one CSV).
///   - worksheet given : emit just that worksheet's used range (one CSV).
///   - neither given   : emit every worksheet's used range (one CSV per worksheet).
///
/// Default shape is AI auto-shape: an arbitrary workbook is an arbitrary schema, so
/// <see cref="FlatSpecBuilder"/> builds one flat table per CSV unless a Solution is supplied.
///
/// Auth: the short-lived Microsoft OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/>
/// and is sent as <c>Authorization: Bearer &lt;token&gt;</c>. It is used per job only - never persisted,
/// never logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. Any cell / row cap that
/// bites is recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   driveId   : the drive id of the workbook's drive-item (required).
///   itemId    : the workbook's drive-item id (required).
///   worksheet : an optional worksheet name; when present only that worksheet's used range is emitted.
///   table     : an optional named-table name; when present only that table is emitted (wins over worksheet).
/// </summary>
public sealed class ExcelOnlineConnector : IDataSourceConnector
{
    public string Id => "excel-online";
    public string DisplayName => "Excel Online";

    // how many leading data rows to sample when inferring a column's type (matches the other connectors).
    private const int SampleRows = 200;

    // hard caps so a huge workbook cannot exhaust the box. Every cap that bites is recorded in Notes.
    private const int MaxWorksheets = 100;             // worksheets emitted in one job
    private const long MaxCellsPerSheet = 5_000_000;   // cells from a single used range before the cap bites
    private const int TableRowPageSize = 5_000;        // rows pulled per page from a named table
    private const long MaxTableRows = 2_000_000;       // rows pulled from a single named table before the cap bites
    private const int MaxTablePages = 1_000;           // page-walk guard for a named table's rows

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discovery reports the worksheets (and named tables) the workbook would emit, reading only the
        // header / first row cheaply; Fetch samples real values to infer the precise types.
        var schema = new SchemaDiscovery();

        string? driveId = req.Param("driveId");
        string? itemId = req.Param("itemId");
        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(itemId))
            return schema; // nothing to inspect - the caller surfaces the missing ids

        string itemBase = WorkbookBase(driveId!, itemId!);
        string? wantWorksheet = req.Param("worksheet");
        string? wantTable = req.Param("table");

        if (!string.IsNullOrWhiteSpace(wantTable))
        {
            var (header, _) = await ReadTableHeaderAsync(itemBase, wantTable!, req, ct).ConfigureAwait(false);
            schema.Tables.Add(HeaderToSchema(wantTable!, header));
            return schema;
        }

        var worksheets = await ListWorksheetsAsync(itemBase, req, ct).ConfigureAwait(false);
        foreach (var ws in worksheets)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrWhiteSpace(wantWorksheet)
                && !string.Equals(ws, wantWorksheet, StringComparison.OrdinalIgnoreCase))
                continue;
            var header = await ReadUsedRangeHeaderAsync(itemBase, ws, req, ct).ConfigureAwait(false);
            schema.Tables.Add(HeaderToSchema(ws, header));
        }
        return schema;
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? driveId = req.Param("driveId");
        string? itemId = req.Param("itemId");
        if (string.IsNullOrWhiteSpace(driveId) || string.IsNullOrWhiteSpace(itemId))
        {
            result.Notes.Add("Both driveId and itemId are required; nothing to ingest.");
            return result;
        }

        string itemBase = WorkbookBase(driveId!, itemId!);
        string? wantTable = req.Param("table");
        string? wantWorksheet = req.Param("worksheet");

        if (!string.IsNullOrWhiteSpace(wantTable))
        {
            // a single named table wins over worksheet selection.
            await FetchTableAsync(itemBase, wantTable!, req, workDir, result, ct).ConfigureAwait(false);
        }
        else
        {
            var worksheets = await ListWorksheetsAsync(itemBase, req, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(wantWorksheet))
                worksheets = worksheets
                    .Where(w => string.Equals(w, wantWorksheet, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            if (worksheets.Count == 0)
                result.Notes.Add("The workbook has no readable worksheets (or none matched the requested worksheet).");

            if (worksheets.Count > MaxWorksheets)
            {
                result.Notes.Add($"worksheet CAP reached: {worksheets.Count:N0} worksheets found, only the first {MaxWorksheets:N0} were ingested.");
                worksheets = worksheets.Take(MaxWorksheets).ToList();
            }

            foreach (var ws in worksheets)
            {
                ct.ThrowIfCancellationRequested();
                await FetchWorksheetAsync(itemBase, ws, req, workDir, result, ct).ConfigureAwait(false);
            }
        }

        if (result.Tables.Count == 0)
            result.Notes.Add("No rows were returned from the workbook.");
        return result;
    }

    // ---- worksheets (used range) ---------------------------------------------------------------

    /// <summary>List the workbook's worksheet names (/workbook/worksheets).</summary>
    private async Task<List<string>> ListWorksheetsAsync(string itemBase, ConnectorRequest req, CancellationToken ct)
    {
        string url = itemBase + "/workbook/worksheets?$select=name&$top=" + MaxWorksheets;
        JsonNode? root = await GraphClient.GetJsonAsync(url, req.AccessToken, DisplayName, ct).ConfigureAwait(false);

        var names = new List<string>();
        if (root?["value"] is JsonArray sheets)
            foreach (var s in sheets)
                if (GraphClient.Str(s?["name"]) is { Length: > 0 } name)
                    names.Add(name);
        return names;
    }

    /// <summary>Read just the first row of a worksheet's used range (its header) for the discovery preview.</summary>
    private async Task<List<string>> ReadUsedRangeHeaderAsync(string itemBase, string worksheet, ConnectorRequest req, CancellationToken ct)
    {
        // usedRange(valuesOnly=true) returns the whole used range; we read only its first row for the preview.
        var values = await ReadUsedRangeValuesAsync(itemBase, worksheet, req, ct).ConfigureAwait(false);
        return values is { Count: > 0 } ? RowToStrings(values[0] as JsonArray) : new List<string>();
    }

    /// <summary>GET a worksheet's used-range value matrix (.../worksheets/{name}/usedRange(valuesOnly=true)).
    /// Returns the rows array (each a JsonArray of cells), or null when the worksheet is empty.</summary>
    private async Task<JsonArray?> ReadUsedRangeValuesAsync(string itemBase, string worksheet, ConnectorRequest req, CancellationToken ct)
    {
        string url = itemBase + "/workbook/worksheets/" + Uri.EscapeDataString(worksheet)
                     + "/usedRange(valuesOnly=true)?$select=values";
        JsonNode? root = await GraphClient.GetJsonAsync(url, req.AccessToken, DisplayName, ct).ConfigureAwait(false);
        return root?["values"] as JsonArray;
    }

    /// <summary>Fetch one worksheet's used range to <c>workDir/&lt;worksheet&gt;.csv</c> (first row = header),
    /// infer a type per column and register the table; records a cell cap in Notes if one bit.</summary>
    private async Task FetchWorksheetAsync(string itemBase, string worksheet, ConnectorRequest req, string workDir, IngestResult result, CancellationToken ct)
    {
        var values = await ReadUsedRangeValuesAsync(itemBase, worksheet, req, ct).ConfigureAwait(false);
        WriteMatrix(worksheet, values, $"worksheet '{worksheet}'", workDir, result);
    }

    // ---- named tables --------------------------------------------------------------------------

    /// <summary>Read a named table's header row (.../tables/{name}/headerRowRange) for the discovery preview,
    /// plus the table's name as confirmed by Graph.</summary>
    private async Task<(List<string> header, string name)> ReadTableHeaderAsync(string itemBase, string table, ConnectorRequest req, CancellationToken ct)
    {
        string url = itemBase + "/workbook/tables/" + Uri.EscapeDataString(table)
                     + "/headerRowRange?$select=values";
        JsonNode? root = await GraphClient.GetJsonAsync(url, req.AccessToken, DisplayName, ct).ConfigureAwait(false);
        var values = root?["values"] as JsonArray;
        var header = values is { Count: > 0 } ? RowToStrings(values[0] as JsonArray) : new List<string>();
        return (header, table);
    }

    /// <summary>Fetch one named table to <c>workDir/&lt;table&gt;.csv</c>: the header row first, then the data
    /// rows paged via /tables/{name}/rows with $top/$skip; infer a type per column and register the table.
    /// Records the row cap in Notes if it bit.</summary>
    private async Task FetchTableAsync(string itemBase, string table, ConnectorRequest req, string workDir, IngestResult result, CancellationToken ct)
    {
        var (header, _) = await ReadTableHeaderAsync(itemBase, table, req, ct).ConfigureAwait(false);
        if (header.Count == 0)
        {
            result.Notes.Add($"table '{table}' has no header row; skipped.");
            return;
        }
        for (int i = 0; i < header.Count; i++)
            if (string.IsNullOrWhiteSpace(header[i])) header[i] = $"Column{i + 1}";

        string tableName = CloudFileStaging.SafeTableName(table);
        string dest = Path.Combine(workDir, tableName + ".csv");

        var samples = new List<string?>[header.Count];
        for (int i = 0; i < header.Count; i++) samples[i] = new List<string?>();

        long dataRows = 0;
        bool capHit = false;
        int skip = 0;

        using (var sink = new CsvSink(dest, header))
        {
            for (int page = 0; page < MaxTablePages; page++)
            {
                ct.ThrowIfCancellationRequested();
                string url = itemBase + "/workbook/tables/" + Uri.EscapeDataString(table)
                             + $"/rows?$select=values&$top={TableRowPageSize}&$skip={skip}";
                JsonNode? root = await GraphClient.GetJsonAsync(url, req.AccessToken, DisplayName, ct).ConfigureAwait(false);
                var rowsPage = root?["value"] as JsonArray;
                int inPage = rowsPage?.Count ?? 0;
                if (inPage == 0) break; // no more rows

                foreach (var rowNode in rowsPage!)
                {
                    if (dataRows >= MaxTableRows) { capHit = true; break; }

                    // each row's "values" is a 1-row matrix [[ cells... ]]; take its single row.
                    var matrix = rowNode?["values"] as JsonArray;
                    var cells = matrix is { Count: > 0 } ? RowToStrings(matrix[0] as JsonArray) : new List<string>();

                    var fields = new string?[header.Count];
                    for (int c = 0; c < header.Count; c++)
                    {
                        string? v = c < cells.Count ? cells[c] : null;
                        fields[c] = v;
                        if (dataRows < SampleRows) samples[c].Add(v);
                    }
                    sink.WriteRow(fields);
                    dataRows++;
                }

                if (capHit) break;
                skip += inPage;
                if (inPage < TableRowPageSize) break; // last (partial) page
            }
        }

        var ts = new TableSchema(tableName);
        for (int i = 0; i < header.Count; i++)
            ts.Columns.Add(new ColumnSchema(header[i], ColumnTypeInference.Infer(samples[i])));
        result.Schema.Tables.Add(ts);
        result.Tables[tableName] = dest;
        result.Notes.Add($"table '{table}': ingested {dataRows:N0} row(s) into '{tableName}'.");
        if (capHit)
            result.Notes.Add($"table '{table}': row CAP reached ({MaxTableRows:N0} rows) - the pull is incomplete; not all rows were ingested.");
    }

    // ---- value-matrix -> CSV (shared by worksheet used ranges) ---------------------------------

    /// <summary>Write a value matrix to <c>workDir/&lt;label&gt;.csv</c> (first row = header), infer a type per
    /// column and register the table. Records a cell cap in Notes if one bit. An empty matrix yields a note
    /// and no CSV.</summary>
    private void WriteMatrix(string label, JsonArray? values, string what, string workDir, IngestResult result)
    {
        string table = CloudFileStaging.SafeTableName(label);
        if (values is not { Count: > 0 })
        {
            result.Notes.Add($"{what} is empty; no CSV written.");
            return;
        }

        var header = RowToStrings(values[0] as JsonArray);
        if (header.Count == 0)
        {
            result.Notes.Add($"{what} has no header row; skipped.");
            return;
        }
        for (int i = 0; i < header.Count; i++)
            if (string.IsNullOrWhiteSpace(header[i])) header[i] = $"Column{i + 1}";

        var samples = new List<string?>[header.Count];
        for (int i = 0; i < header.Count; i++) samples[i] = new List<string?>();

        string dest = Path.Combine(workDir, table + ".csv");
        long cells = header.Count;
        bool capHit = false;
        long dataRows = 0;

        using (var sink = new CsvSink(dest, header))
        {
            for (int r = 1; r < values.Count; r++)
            {
                var row = RowToStrings(values[r] as JsonArray);
                cells += row.Count;
                if (cells > MaxCellsPerSheet) { capHit = true; break; }

                var fields = new string?[header.Count];
                for (int c = 0; c < header.Count; c++)
                {
                    string? v = c < row.Count ? row[c] : null;
                    fields[c] = v;
                    if (dataRows < SampleRows) samples[c].Add(v);
                }
                sink.WriteRow(fields);
                dataRows++;
            }
        }

        var ts = new TableSchema(table);
        for (int i = 0; i < header.Count; i++)
            ts.Columns.Add(new ColumnSchema(header[i], ColumnTypeInference.Infer(samples[i])));
        result.Schema.Tables.Add(ts);
        result.Tables[table] = dest;
        result.Notes.Add($"{what}: ingested {dataRows:N0} row(s) into '{table}'.");
        if (capHit)
            result.Notes.Add($"{what}: cell CAP reached ({MaxCellsPerSheet:N0} cells) - the pull is incomplete; not all rows were ingested.");
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>The workbook item base path for a drive-item: <c>/drives/{driveId}/items/{itemId}</c>.</summary>
    private static string WorkbookBase(string driveId, string itemId)
        => $"/drives/{Uri.EscapeDataString(driveId)}/items/{Uri.EscapeDataString(itemId)}";

    /// <summary>Build a discovery TableSchema from a worksheet/table label and its header row (string columns;
    /// Fetch infers precise types). Blank header cells become Column1, Column2, ...</summary>
    private static TableSchema HeaderToSchema(string label, IReadOnlyList<string> header)
    {
        var ts = new TableSchema(CloudFileStaging.SafeTableName(label));
        for (int i = 0; i < header.Count; i++)
        {
            string name = string.IsNullOrWhiteSpace(header[i]) ? $"Column{i + 1}" : header[i];
            ts.Columns.Add(new ColumnSchema(name, "string"));
        }
        return ts;
    }

    /// <summary>Convert a workbook value row (a JsonArray of cells) to a list of strings. A null cell becomes
    /// an empty string; a number / bool becomes its literal text; a structured cell its compact JSON.</summary>
    private static List<string> RowToStrings(JsonArray? row)
    {
        var list = new List<string>();
        if (row == null) return list;
        foreach (var cell in row)
            list.Add(GraphClient.Str(cell));
        return list;
    }
}
