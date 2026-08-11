using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The Google Sheets connector (id "google-sheets") - the first OAuth source and the "connect -&gt; dashboard
/// in 60 seconds" demo hook. A spreadsheet's tabs become one CSV per tab (first row = headers) in the
/// canonical shape the report pipeline consumes, so everything downstream (scaffold, bake, AI prompt,
/// download) is unchanged.
///
/// Two operations against the Google Sheets REST API v4 (raw HTTP, no SDK / no NuGet):
///   - <see cref="DiscoverAsync"/> lists the spreadsheet's tabs via the metadata call
///     (GET spreadsheets/{id}?fields=sheets.properties) and reports the tables/columns it would emit. The
///     column list is read cheaply from each tab's first row only.
///   - <see cref="FetchAsync"/> calls spreadsheets.values:batchGet for the chosen tabs/ranges, writes one
///     CSV per tab through <see cref="CsvSink"/> (first row promoted to headers), infers a type per column
///     with <see cref="ColumnTypeInference"/>, and returns the table-&gt;csv map + inferred schema.
///
/// Default shape is AI auto-shape: arbitrary sheets are arbitrary schemas, so <see cref="FlatSpecBuilder"/>
/// builds one flat table per CSV unless a Solution is supplied.
///
/// Auth: the short-lived Google OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/> and
/// is sent as <c>Authorization: Bearer &lt;token&gt;</c>. It is used per job only - never persisted, never
/// logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. Any cell-count / range cap
/// that bites is recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   spreadsheetId : the Google spreadsheet id (required).
///   tabs          : JSON array of tab (sheet) names to pull; omit to pull every tab in the spreadsheet.
///   range         : an optional A1 range (e.g. "A1:H1000" or "Sheet1!A1:H") applied to each pulled tab.
///   solution      : an optional Solution id (mirrors ConnectorRequest.SolutionId) when the columns map onto
///                   a known Solution schema.
/// </summary>
public sealed class GoogleSheetsConnector : IDataSourceConnector
{
    public string Id => "google-sheets";
    public string DisplayName => "Google Sheets";

    // Sheets API v4 base. https://developers.google.com/sheets/api/reference/rest
    private const string ApiBase = "https://sheets.googleapis.com/v4/spreadsheets/";

    // how many leading data rows to sample when inferring a column's type
    private const int SampleRows = 200;

    // hard caps so a huge spreadsheet cannot exhaust the box. Every cap that bites is recorded in Notes -
    // no silent truncation.
    private const int MaxTabs = 100;            // tabs pulled in one job
    private const long MaxCellsPerTab = 5_000_000;  // values returned for a single tab before the cap bites
    private const long MaxResponseBytes = 256L * 1024 * 1024; // cap on any single API response body

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static readonly HttpClient _http = HttpDefaults.New();

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        var schema = new SchemaDiscovery();
        string? spreadsheetId = req.Param("spreadsheetId");
        if (string.IsNullOrWhiteSpace(spreadsheetId))
            return schema; // nothing to inspect - the caller surfaces the missing id

        var tabs = await ResolveTabsAsync(req, spreadsheetId, ct).ConfigureAwait(false);
        if (tabs.Count == 0) return schema;

        // read only the first row of each tab to report its columns; the type is left as string for the
        // cheap pre-flight preview (Fetch samples real values to infer the precise type).
        foreach (var tab in tabs)
        {
            ct.ThrowIfCancellationRequested();
            var header = await ReadHeaderAsync(spreadsheetId, tab, req, ct).ConfigureAwait(false);
            var ts = new TableSchema(SafeTableName(tab));
            for (int i = 0; i < header.Count; i++)
            {
                string name = string.IsNullOrWhiteSpace(header[i]) ? $"Column{i + 1}" : header[i];
                ts.Columns.Add(new ColumnSchema(name, "string"));
            }
            schema.Tables.Add(ts);
        }
        return schema;
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? spreadsheetId = req.Param("spreadsheetId");
        if (string.IsNullOrWhiteSpace(spreadsheetId))
        {
            result.Notes.Add("No spreadsheetId was provided; nothing to ingest.");
            return result;
        }

        var tabs = await ResolveTabsAsync(req, spreadsheetId, ct).ConfigureAwait(false);
        if (tabs.Count == 0)
        {
            result.Notes.Add("The spreadsheet has no readable tabs (or none matched the requested tabs).");
            return result;
        }
        if (tabs.Count > MaxTabs)
        {
            result.Notes.Add($"tab CAP reached: {tabs.Count:N0} tabs found, only the first {MaxTabs:N0} were ingested.");
            tabs = tabs.Take(MaxTabs).ToList();
        }

        // batchGet every chosen tab/range in one call (ranges are tab name, optionally narrowed by `range`).
        string? a1 = req.Param("range");
        var ranges = tabs.Select(t => RangeForTab(t, a1)).ToList();
        var valuesByRange = await BatchGetAsync(spreadsheetId, ranges, req, ct).ConfigureAwait(false);

        for (int i = 0; i < tabs.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            string tab = tabs[i];
            JsonArray? values = i < valuesByRange.Count ? valuesByRange[i] : null;
            WriteTab(tab, values, workDir, result);
        }

        if (result.Tables.Count == 0)
            result.Notes.Add("No rows were returned from any tab.");
        return result;
    }

    // ---- tab resolution + metadata -------------------------------------------------------------

    /// <summary>The tabs to pull: the explicit <c>tabs[]</c> param if present, otherwise every tab in the
    /// spreadsheet (read from the metadata call). Order is preserved.</summary>
    private async Task<List<string>> ResolveTabsAsync(ConnectorRequest req, string spreadsheetId, CancellationToken ct)
    {
        if (req.Params["tabs"] is JsonArray tabsParam)
        {
            var chosen = tabsParam
                .Select(t => (string?)t)
                .Where(t => t is { Length: > 0 })
                .Select(t => t!)
                .ToList();
            if (chosen.Count > 0) return chosen;
        }
        return await ListTabsAsync(spreadsheetId, req, ct).ConfigureAwait(false);
    }

    /// <summary>List the spreadsheet's tab (sheet) titles via the metadata call
    /// (GET spreadsheets/{id}?fields=sheets.properties).</summary>
    private async Task<List<string>> ListTabsAsync(string spreadsheetId, ConnectorRequest req, CancellationToken ct)
    {
        string url = ApiBase + Uri.EscapeDataString(spreadsheetId) + "?fields=sheets.properties";
        JsonNode? root = await GetJsonAsync(url, req.AccessToken, ct).ConfigureAwait(false);

        var names = new List<string>();
        if (root?["sheets"] is JsonArray sheets)
            foreach (var s in sheets)
                if ((string?)s?["properties"]?["title"] is { Length: > 0 } title)
                    names.Add(title);
        return names;
    }

    // ---- value fetch ---------------------------------------------------------------------------

    /// <summary>Read just the first row of a tab (its header) for the cheap discovery preview.</summary>
    private async Task<List<string>> ReadHeaderAsync(string spreadsheetId, string tab, ConnectorRequest req, CancellationToken ct)
    {
        // a single-range values.get, narrowed to the first row of the tab
        string range = RangeForTab(tab, "1:1");
        string url = ApiBase + Uri.EscapeDataString(spreadsheetId) + "/values/" + Uri.EscapeDataString(range)
                     + "?majorDimension=ROWS";
        JsonNode? root = await GetJsonAsync(url, req.AccessToken, ct).ConfigureAwait(false);
        var rows = root?["values"] as JsonArray;
        return RowToStrings(rows is { Count: > 0 } ? rows[0] as JsonArray : null);
    }

    /// <summary>spreadsheets.values:batchGet for the given ranges (one per chosen tab). Returns the rows
    /// (each a JsonArray of cells) per range, in request order; a range with no data yields null.</summary>
    private async Task<List<JsonArray?>> BatchGetAsync(string spreadsheetId, IReadOnlyList<string> ranges, ConnectorRequest req, CancellationToken ct)
    {
        var sb = new StringBuilder(ApiBase);
        sb.Append(Uri.EscapeDataString(spreadsheetId)).Append("/values:batchGet?majorDimension=ROWS");
        foreach (var r in ranges)
            sb.Append("&ranges=").Append(Uri.EscapeDataString(r));

        JsonNode? root = await GetJsonAsync(sb.ToString(), req.AccessToken, ct).ConfigureAwait(false);

        var result = new List<JsonArray?>();
        if (root?["valueRanges"] is JsonArray valueRanges)
            foreach (var vr in valueRanges)
                result.Add(vr?["values"] as JsonArray);

        // pad to the requested count so the caller can index by tab even when a tab returned nothing
        while (result.Count < ranges.Count) result.Add(null);
        return result;
    }

    /// <summary>Write one tab's values to <c>workDir/&lt;tab&gt;.csv</c> (first row = header), infer a type per
    /// column, and register the table + its schema. Records a cell cap in Notes if one bit.</summary>
    private void WriteTab(string tab, JsonArray? values, string workDir, IngestResult result)
    {
        string table = SafeTableName(tab);
        if (values is not { Count: > 0 })
        {
            result.Notes.Add($"tab '{tab}' is empty; no CSV written.");
            return;
        }

        var header = RowToStrings(values[0] as JsonArray);
        if (header.Count == 0)
        {
            result.Notes.Add($"tab '{tab}' has no header row; skipped.");
            return;
        }
        for (int i = 0; i < header.Count; i++)
            if (string.IsNullOrWhiteSpace(header[i])) header[i] = $"Column{i + 1}";

        // sample columns for type inference while writing the body rows
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
                if (cells > MaxCellsPerTab) { capHit = true; break; }

                // pad/truncate to the header width; CsvSink also enforces this, sampling needs the same shape
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
        result.Notes.Add($"tab '{tab}': ingested {dataRows:N0} row(s) into '{table}'.");
        if (capHit)
            result.Notes.Add($"tab '{tab}': cell CAP reached ({MaxCellsPerTab:N0} cells) - the pull is incomplete; not all rows were ingested.");
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    /// <summary>GET a Sheets API URL with the per-job Bearer token and return the parsed JSON root. The token
    /// is applied to the request header only; it is never written to a Note or an exception message.</summary>
    private static async Task<JsonNode?> GetJsonAsync(string url, string? accessToken, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(accessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Sheets API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
    }

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Build the A1 range string for a tab. When a job-level A1 range is given (e.g. "A1:H1000" or a
    /// bare "1:1"), it is applied to the tab; otherwise the whole tab is read by its bare name. A range that
    /// already carries a "Tab!" prefix is honoured as-is.</summary>
    private static string RangeForTab(string tab, string? a1)
    {
        if (string.IsNullOrWhiteSpace(a1)) return QuoteTab(tab);
        a1 = a1.Trim();
        return a1.Contains('!') ? a1 : QuoteTab(tab) + "!" + a1;
    }

    /// <summary>Quote a tab name for an A1 reference. Sheets uses single quotes around titles that contain
    /// spaces or punctuation, with embedded single quotes doubled.</summary>
    private static string QuoteTab(string tab)
    {
        bool needsQuote = tab.Length == 0 || tab.Any(c => !char.IsLetterOrDigit(c) && c != '_');
        return needsQuote ? "'" + tab.Replace("'", "''") + "'" : tab;
    }

    /// <summary>Convert a Sheets values row (a JsonArray of cells) to a list of strings. A null/number/bool
    /// cell becomes its literal text; a missing cell is an empty string.</summary>
    private static List<string> RowToStrings(JsonArray? row)
    {
        var list = new List<string>();
        if (row == null) return list;
        foreach (var cell in row)
            list.Add(cell switch
            {
                null => "",
                JsonValue v => v.ToString(),
                _ => cell.ToJsonString(),
            });
        return list;
    }

    /// <summary>A logical table / CSV stem safe for an M file reference: keep letters, digits, spaces,
    /// underscores and hyphens; collapse the rest to underscores. Matches FilesConnector.SafeTableName.</summary>
    private static string SafeTableName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Table";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' ? c : '_');
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "Table" : s;
    }
}
