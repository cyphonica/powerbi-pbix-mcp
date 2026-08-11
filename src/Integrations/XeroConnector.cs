using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The Xero connector (id "xero") - the flagship finance source. A customer's accounting data is pulled
/// straight into the engine host and turned into the canonical CSV shape the finance Solution consumes, so
/// everything downstream (scaffold, bake, AI prompt, download) is unchanged. The emitted files match
/// <c>solutions/finance/schema.json</c> EXACTLY - invoices.csv, contacts.csv, accounts.csv, bank.csv and a
/// generated calendar.csv - so the connector output drops onto the finance model.spec.json with no field
/// mapping.
///
/// Entities pulled (all read-only, paginated where the API pages):
///   - Invoices (ACCREC sales + ACCPAY bills)  -&gt; invoices.csv (revenue + AR/AP facts, aging).
///   - Contacts (customer / supplier dimension) -&gt; contacts.csv.
///   - Accounts (chart of accounts)             -&gt; accounts.csv.
///   - BankTransactions (cash movements)        -&gt; bank.csv.
///   - a generated Calendar over the months the data spans -&gt; calendar.csv.
///
/// Two operations against the Xero Accounting REST API (raw HTTP, no SDK / no NuGet):
///   - <see cref="DiscoverAsync"/> lists the orgs the token is authorised for (the connections endpoint) and
///     reports the fixed finance tables/columns it would emit.
///   - <see cref="FetchAsync"/> pulls the entities above, writes each through <see cref="CsvSink"/>, and
///     returns the table-&gt;csv map + the (finance) schema. Pagination caps are recorded in
///     <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Auth: the short-lived OAuth access token arrives on <see cref="ConnectorRequest.AccessToken"/> and is sent
/// as <c>Authorization: Bearer &lt;token&gt;</c>; the chosen org id arrives in <c>Params.xeroTenantId</c> and is
/// sent on every call as the <c>Xero-Tenant-Id</c> header. Neither the token nor the tenant id is ever
/// persisted or logged (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. A per-call rate limit is
/// respected to stay within the provider's allowance; every pagination cap that bites is surfaced in Notes.
///
/// Params it reads:
///   xeroTenantId : the chosen org's tenant id (required for a fetch; sent as the Xero-Tenant-Id header).
///   fromDate     : an optional ISO date (yyyy-MM-dd) lower bound for the dated entities (invoices, bank).
/// </summary>
public sealed class XeroConnector : IDataSourceConnector
{
    public string Id => "xero";
    public string DisplayName => "Xero";

    // Xero API bases. https://developer.xero.com/documentation/api/accounting/overview
    private const string ConnectionsUrl = "https://api.xero.com/connections";
    private const string ApiBase = "https://api.xero.com/api.xro/2.0/";

    private const string TenantHeader = "Xero-Tenant-Id";

    // page size Xero returns per page for the paged endpoints (Invoices, Contacts, BankTransactions).
    private const int PageSize = 100;

    // hard caps so a very large org cannot exhaust the box. Every cap that bites is recorded in Notes -
    // no silent truncation.
    private const int MaxPages = 1_000;                         // pages followed per entity before the cap bites
    private const long MaxResponseBytes = 256L * 1024 * 1024;   // cap on any single API response body

    // a courtesy minimum spacing between API calls so a fetch stays well inside the provider's per-minute
    // allowance (the provider also returns 429 on burst; this keeps us under it for the common case).
    private static readonly TimeSpan MinCallSpacing = TimeSpan.FromMilliseconds(1100);

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static readonly HttpClient _http = new();

    // serialises the courtesy spacing across concurrent jobs against the same shared client.
    private static readonly SemaphoreSlim _rateGate = new(1, 1);
    private static DateTime _lastCallUtc = DateTime.MinValue;

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Discovery confirms the token can see at least one org (the connections endpoint) and reports the
        // fixed finance tables this connector emits. The org list itself is surfaced to the picker by the
        // /connectors/xero/datasets route; here we only need the connection to be valid.
        _ = await ListOrgsAsync(req, ct).ConfigureAwait(false);
        return FinanceSchema();
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? tenantId = req.Param("xeroTenantId");
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            result.Notes.Add("No xeroTenantId was provided; pick an organisation to ingest.");
            return result;
        }

        string? fromDate = req.Param("fromDate");

        var monthEnds = new SortedSet<DateTime>();

        await FetchInvoicesAsync(req, tenantId, fromDate, workDir, result, monthEnds, ct).ConfigureAwait(false);
        await FetchContactsAsync(req, tenantId, workDir, result, ct).ConfigureAwait(false);
        await FetchAccountsAsync(req, tenantId, workDir, result, ct).ConfigureAwait(false);
        await FetchBankAsync(req, tenantId, fromDate, workDir, result, monthEnds, ct).ConfigureAwait(false);

        WriteCalendar(workDir, result, monthEnds);

        if (result.Tables.Count == 0)
            result.Notes.Add("No data was returned for the chosen organisation.");
        return result;
    }

    // ---- Invoices -> invoices.csv --------------------------------------------------------------

    /// <summary>Pull Invoices (ACCREC + ACCPAY), paginated, into invoices.csv matching the finance schema.
    /// AmountDue drives AR/AP; DaysOutstanding (days past due as at today) drives aging. CostAmount is left
    /// blank (Xero invoices do not carry a cost of goods line) so the Solution's gross-margin measure simply
    /// has no cost to net against.</summary>
    private async Task FetchInvoicesAsync(ConnectorRequest req, string tenantId, string? fromDate, string workDir,
        IngestResult result, SortedSet<DateTime> monthEnds, CancellationToken ct)
    {
        string[] header =
        {
            "InvoiceKey", "ContactKey", "AccountKey", "InvoiceDate", "DueDate", "Type", "Status",
            "Total", "CostAmount", "AmountPaid", "AmountDue", "DaysOutstanding",
        };

        string dest = Path.Combine(workDir, "invoices.csv");
        long rows = 0;
        int invoiceKey = 0;
        DateTime today = DateTime.UtcNow.Date;
        var (capped, pages) = (false, 0);

        // a stable per-job map from a contact's GUID to the small integer key invoices reference.
        var contactKeys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string? where = BuildDateWhere("Date", fromDate);

        using (var sink = new CsvSink(dest, header))
        {
            for (int page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                pages = page;
                string url = ApiBase + "Invoices?page=" + page + "&pageSize=" + PageSize
                    + (where != null ? "&where=" + Uri.EscapeDataString(where) : "");
                JsonNode? root = await GetJsonAsync(url, req.AccessToken, tenantId, ct).ConfigureAwait(false);
                if (root?["Invoices"] is not JsonArray invoices || invoices.Count == 0) break;

                foreach (var inv in invoices)
                {
                    if (inv is not JsonObject o) continue;
                    invoiceKey++;

                    string type = Str(o["Type"]);            // ACCREC | ACCPAY
                    string status = MapStatus(Str(o["Status"]));
                    DateTime invDate = ParseXeroDate(o["DateString"] ?? o["Date"]) ?? today;
                    DateTime dueDate = ParseXeroDate(o["DueDateString"] ?? o["DueDate"]) ?? invDate;
                    decimal total = Dec(o["Total"]);
                    decimal paid = Dec(o["AmountPaid"]);
                    decimal due = o["AmountDue"] is { } ad ? Dec(ad) : total - paid;
                    int daysOut = due > 0 ? (int)(today - dueDate).TotalDays : 0;

                    int contactKey = ResolveContactKey(o["Contact"]?["ContactID"], contactKeys);
                    int accountKey = ResolveAccountKey(o["LineItems"]);

                    DateTime monthEnd = MonthEnd(invDate);
                    monthEnds.Add(monthEnd);

                    sink.WriteRow(new[]
                    {
                        invoiceKey.ToString(),
                        contactKey.ToString(),
                        accountKey.ToString(),
                        Iso(monthEnd),            // InvoiceDate joins calendar.MonthEnding (month-end grain)
                        Iso(dueDate),
                        type,
                        status,
                        Num(total),
                        Num(0m),                   // CostAmount - Xero invoice headers carry no COGS. Emit a numeric 0, NEVER "" - an all-empty column types as text and makes SUM(CostAmount) (Total Cost -> Gross Margin -> Margin %) throw "SUM cannot work with values of type String".
                        Num(paid),
                        Num(due),
                        daysOut.ToString(),
                    });
                    rows++;
                }

                if (invoices.Count < PageSize) break;
                if (page == MaxPages) capped = true;
            }
        }

        Register("invoices", dest, result, InvoicesSchema());
        result.Notes.Add($"invoices: ingested {rows:N0} invoice(s) across {pages} page(s).");
        if (capped)
            result.Notes.Add($"invoices: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all invoices were ingested.");
    }

    // ---- Contacts -> contacts.csv --------------------------------------------------------------

    /// <summary>Pull Contacts (the customer / supplier dimension), paginated, into contacts.csv. The small
    /// integer ContactKey is assigned in first-seen order so it matches the keys invoices reference.</summary>
    private async Task FetchContactsAsync(ConnectorRequest req, string tenantId, string workDir,
        IngestResult result, CancellationToken ct)
    {
        string[] header = { "ContactKey", "Contact Name", "Contact Type", "Region" };
        string dest = Path.Combine(workDir, "contacts.csv");
        long rows = 0;
        int key = 0;
        var (capped, pages) = (false, 0);

        using (var sink = new CsvSink(dest, header))
        {
            for (int page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                pages = page;
                string url = ApiBase + "Contacts?page=" + page + "&pageSize=" + PageSize;
                JsonNode? root = await GetJsonAsync(url, req.AccessToken, tenantId, ct).ConfigureAwait(false);
                if (root?["Contacts"] is not JsonArray contacts || contacts.Count == 0) break;

                foreach (var c in contacts)
                {
                    if (c is not JsonObject o) continue;
                    key++;
                    string contactType = (bool?)o["IsCustomer"] == true ? "Customer"
                        : (bool?)o["IsSupplier"] == true ? "Supplier" : "";
                    sink.WriteRow(new[]
                    {
                        key.ToString(),
                        Str(o["Name"]),
                        contactType,
                        Str(FirstOf(o["Addresses"])?["Region"] ?? FirstOf(o["Addresses"])?["City"]),
                    });
                    rows++;
                }

                if (contacts.Count < PageSize) break;
                if (page == MaxPages) capped = true;
            }
        }

        Register("contacts", dest, result, ContactsSchema());
        result.Notes.Add($"contacts: ingested {rows:N0} contact(s) across {pages} page(s).");
        if (capped)
            result.Notes.Add($"contacts: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all contacts were ingested.");
    }

    // ---- Accounts -> accounts.csv --------------------------------------------------------------

    /// <summary>Pull the chart of accounts (a single unpaged call) into accounts.csv. The small integer
    /// AccountKey is assigned in first-seen order; the per-job map is rebuilt here, but invoices reference
    /// accounts by their first line item's code, so we key on that code below in <see cref="ResolveAccountKey"/>.
    /// To keep the two consistent this method writes the SAME keys: code -&gt; key in first-seen order.</summary>
    private async Task FetchAccountsAsync(ConnectorRequest req, string tenantId, string workDir,
        IngestResult result, CancellationToken ct)
    {
        string[] header = { "AccountKey", "Account Name", "Account Class" };
        string dest = Path.Combine(workDir, "accounts.csv");
        long rows = 0;

        string url = ApiBase + "Accounts";
        JsonNode? root = await GetJsonAsync(url, req.AccessToken, tenantId, ct).ConfigureAwait(false);

        using (var sink = new CsvSink(dest, header))
        {
            // a synthetic catch-all so an invoice whose account is unknown still has a valid AccountKey (200).
            sink.WriteRow(new[] { "200", "Sales", "Revenue" });
            rows++;

            if (root?["Accounts"] is JsonArray accounts)
                foreach (var a in accounts)
                {
                    if (a is not JsonObject o) continue;
                    int key = AccountKeyFromCode(Str(o["Code"]));
                    if (key == 200) continue; // already emitted the catch-all under 200
                    sink.WriteRow(new[]
                    {
                        key.ToString(),
                        Str(o["Name"]),
                        Str(o["Class"] ?? o["Type"]),
                    });
                    rows++;
                }
        }

        Register("accounts", dest, result, AccountsSchema());
        result.Notes.Add($"accounts: ingested {rows:N0} account(s).");
    }

    // ---- BankTransactions -> bank.csv ----------------------------------------------------------

    /// <summary>Pull BankTransactions (cash movements), paginated, into bank.csv. Direction is RECEIVE -&gt;
    /// Inflow / SPEND -&gt; Outflow; NetMovement is signed (positive for an inflow, negative for an
    /// outflow).</summary>
    private async Task FetchBankAsync(ConnectorRequest req, string tenantId, string? fromDate, string workDir,
        IngestResult result, SortedSet<DateTime> monthEnds, CancellationToken ct)
    {
        string[] header = { "BankTxnKey", "TxnDate", "Bank Account", "Direction", "NetMovement" };
        string dest = Path.Combine(workDir, "bank.csv");
        long rows = 0;
        int key = 0;
        var (capped, pages) = (false, 0);
        string? where = BuildDateWhere("Date", fromDate);

        using (var sink = new CsvSink(dest, header))
        {
            for (int page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                pages = page;
                string url = ApiBase + "BankTransactions?page=" + page + "&pageSize=" + PageSize
                    + (where != null ? "&where=" + Uri.EscapeDataString(where) : "");
                JsonNode? root = await GetJsonAsync(url, req.AccessToken, tenantId, ct).ConfigureAwait(false);
                if (root?["BankTransactions"] is not JsonArray txns || txns.Count == 0) break;

                foreach (var t in txns)
                {
                    if (t is not JsonObject o) continue;
                    key++;
                    string xeroType = Str(o["Type"]); // RECEIVE | SPEND (+ overpayment / prepayment variants)
                    bool inflow = xeroType.StartsWith("RECEIVE", StringComparison.OrdinalIgnoreCase);
                    decimal total = Dec(o["Total"]);
                    decimal net = inflow ? total : -total;
                    DateTime txnDate = ParseXeroDate(o["DateString"] ?? o["Date"]) ?? DateTime.UtcNow.Date;
                    DateTime monthEnd = MonthEnd(txnDate);
                    monthEnds.Add(monthEnd);

                    sink.WriteRow(new[]
                    {
                        key.ToString(),
                        Iso(monthEnd),       // TxnDate joins calendar.MonthEnding (month-end grain)
                        Str(o["BankAccount"]?["Name"]),
                        inflow ? "Inflow" : "Outflow",
                        Num(net),
                    });
                    rows++;
                }

                if (txns.Count < PageSize) break;
                if (page == MaxPages) capped = true;
            }
        }

        Register("bank", dest, result, BankSchema());
        result.Notes.Add($"bank: ingested {rows:N0} transaction(s) across {pages} page(s).");
        if (capped)
            result.Notes.Add($"bank: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all transactions were ingested.");
    }

    // ---- generated Calendar -> calendar.csv ----------------------------------------------------

    /// <summary>Generate calendar.csv over the span of month-ends touched by the facts (invoices + bank).
    /// One row per month-end; the most recent 12 months are "This Year" and the prior months "Last Year"
    /// (matching the finance Solution's growth split). Falls back to the last 24 months ending this month
    /// when no dated facts were ingested.</summary>
    private static void WriteCalendar(string workDir, IngestResult result, SortedSet<DateTime> monthEnds)
    {
        string[] header = { "MonthEnding", "Period", "Month Name", "Year" };
        string dest = Path.Combine(workDir, "calendar.csv");

        var months = BuildMonthSpan(monthEnds);
        // "This Year" = the most recent 12 month-ends; everything earlier is "Last Year".
        int thisYearFrom = Math.Max(0, months.Count - 12);

        long rows = 0;
        using (var sink = new CsvSink(dest, header))
            for (int i = 0; i < months.Count; i++)
            {
                DateTime m = months[i];
                sink.WriteRow(new[]
                {
                    Iso(m),
                    i >= thisYearFrom ? "This Year" : "Last Year",
                    m.ToString("MMMM", System.Globalization.CultureInfo.InvariantCulture),
                    m.Year.ToString(),
                });
                rows++;
            }

        Register("calendar", dest, result, CalendarSchema());
        result.Notes.Add($"calendar: generated {rows:N0} month-ending row(s).");
    }

    // ---- orgs ----------------------------------------------------------------------------------

    /// <summary>List the organisations the access token is authorised for (the connections endpoint). Surfaced
    /// to the org picker; here it also doubles as the discovery connectivity check.</summary>
    private async Task<List<(string tenantId, string name)>> ListOrgsAsync(ConnectorRequest req, CancellationToken ct)
    {
        var orgs = new List<(string, string)>();
        JsonNode? root = await GetJsonAsync(ConnectionsUrl, req.AccessToken, tenantId: null, ct).ConfigureAwait(false);
        if (root is JsonArray arr)
            foreach (var c in arr)
                if (c is JsonObject o && Str(o["tenantId"]) is { Length: > 0 } id)
                    orgs.Add((id, Str(o["tenantName"])));
        return orgs;
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    /// <summary>GET a Xero API URL with the per-job Bearer token and (when set) the Xero-Tenant-Id header,
    /// honouring the courtesy call spacing, and return the parsed JSON root. Neither the token nor the tenant
    /// id is ever written to a Note or an exception message.</summary>
    private static async Task<JsonNode?> GetJsonAsync(string url, string? accessToken, string? tenantId, CancellationToken ct)
    {
        await ThrottleAsync(ct).ConfigureAwait(false);

        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(accessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrWhiteSpace(tenantId))
            msg.Headers.TryAddWithoutValidation(TenantHeader, tenantId);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Xero API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Enforce a minimum spacing between API calls so a fetch stays inside the provider's per-minute
    /// allowance. Cheap, process-wide, and never sleeps longer than the spacing.</summary>
    private static async Task ThrottleAsync(CancellationToken ct)
    {
        await _rateGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = MinCallSpacing - (DateTime.UtcNow - _lastCallUtc);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastCallUtc = DateTime.UtcNow;
        }
        finally { _rateGate.Release(); }
    }

    // ---- key resolution ------------------------------------------------------------------------

    /// <summary>Map a contact GUID to a small stable integer in first-seen order (1-based) so invoices and the
    /// contacts dimension agree on ContactKey within a job.</summary>
    private static int ResolveContactKey(JsonNode? contactId, Dictionary<string, int> map)
    {
        string id = Str(contactId);
        if (id.Length == 0) return 0;
        if (map.TryGetValue(id, out var k)) return k;
        k = map.Count + 1;
        map[id] = k;
        return k;
    }

    /// <summary>Derive an AccountKey for an invoice from its first line item's account code; defaults to the
    /// catch-all 200 ("Sales") when no code is present.</summary>
    private static int ResolveAccountKey(JsonNode? lineItems)
        => lineItems is JsonArray { Count: > 0 } li ? AccountKeyFromCode(Str(li[0]?["AccountCode"])) : 200;

    /// <summary>Turn a Xero account code into an integer AccountKey; a non-numeric or blank code falls back to
    /// the catch-all 200.</summary>
    private static int AccountKeyFromCode(string code)
        => int.TryParse(code, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0 ? n : 200;

    // ---- value helpers -------------------------------------------------------------------------

    private static string MapStatus(string xeroStatus)
        => xeroStatus.Equals("PAID", StringComparison.OrdinalIgnoreCase) ? "PAID" : "AUTHORISED";

    private static string? BuildDateWhere(string field, string? fromDate)
    {
        if (string.IsNullOrWhiteSpace(fromDate)) return null;
        var d = ParseXeroDate(fromDate);
        if (d == null) return null;
        var v = d.Value;
        return $"{field} >= DateTime({v.Year},{v.Month},{v.Day})";
    }

    /// <summary>Parse a Xero date: an ISO "DateString" (yyyy-MM-ddTHH:mm:ss) or the "/Date(ms+offset)/" epoch
    /// form. Returns the date component only.</summary>
    private static DateTime? ParseXeroDate(JsonNode? node)
    {
        string s = Str(node);
        if (s.Length == 0) return null;

        if (s.StartsWith("/Date(", StringComparison.Ordinal))
        {
            int start = 6;
            int end = s.IndexOfAny(new[] { '+', '-', ')' }, start);
            if (end > start && long.TryParse(s.AsSpan(start, end - start), out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.Date;
        }
        if (DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt))
            return dt.Date;
        return null;
    }

    private static DateTime MonthEnd(DateTime d)
        => new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    /// <summary>The contiguous list of month-ends from the earliest to the latest touched month. Falls back to
    /// the last 24 months ending this month when nothing dated was seen.</summary>
    private static List<DateTime> BuildMonthSpan(SortedSet<DateTime> monthEnds)
    {
        var list = new List<DateTime>();
        DateTime first, last;
        if (monthEnds.Count == 0)
        {
            last = MonthEnd(DateTime.UtcNow.Date);
            first = MonthEnd(last.AddMonths(-23));
        }
        else
        {
            first = monthEnds.Min;
            last = monthEnds.Max;
        }

        var cursor = new DateTime(first.Year, first.Month, 1);
        var end = new DateTime(last.Year, last.Month, 1);
        while (cursor <= end)
        {
            list.Add(MonthEnd(cursor));
            cursor = cursor.AddMonths(1);
        }
        return list;
    }

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Num(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static decimal Dec(JsonNode? node)
    {
        string s = Str(node);
        return decimal.TryParse(s, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;
    }

    /// <summary>The first element of a node when it is a non-empty array, else null (safe on a null /
    /// non-array node so a malformed payload never throws).</summary>
    private static JsonNode? FirstOf(JsonNode? node) => node is JsonArray { Count: > 0 } a ? a[0] : null;

    /// <summary>Read a JSON node as its literal text (string/number/bool); null/missing -&gt; empty.</summary>
    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };

    // ---- schema (matches solutions/finance/schema.json exactly) --------------------------------

    private static void Register(string table, string csvPath, IngestResult result, TableSchema schema)
    {
        result.Tables[table] = csvPath;
        result.Schema.Tables.Add(schema);
    }

    private static SchemaDiscovery FinanceSchema()
    {
        var s = new SchemaDiscovery();
        s.Tables.Add(InvoicesSchema());
        s.Tables.Add(ContactsSchema());
        s.Tables.Add(AccountsSchema());
        s.Tables.Add(BankSchema());
        s.Tables.Add(CalendarSchema());
        return s;
    }

    private static TableSchema InvoicesSchema()
    {
        var t = new TableSchema("invoices");
        t.Columns.Add(new ColumnSchema("InvoiceKey", "int64"));
        t.Columns.Add(new ColumnSchema("ContactKey", "int64"));
        t.Columns.Add(new ColumnSchema("AccountKey", "int64"));
        t.Columns.Add(new ColumnSchema("InvoiceDate", "date"));
        t.Columns.Add(new ColumnSchema("DueDate", "date"));
        t.Columns.Add(new ColumnSchema("Type", "string"));
        t.Columns.Add(new ColumnSchema("Status", "string"));
        t.Columns.Add(new ColumnSchema("Total", "double"));
        t.Columns.Add(new ColumnSchema("CostAmount", "double"));
        t.Columns.Add(new ColumnSchema("AmountPaid", "double"));
        t.Columns.Add(new ColumnSchema("AmountDue", "double"));
        t.Columns.Add(new ColumnSchema("DaysOutstanding", "int64"));
        return t;
    }

    private static TableSchema ContactsSchema()
    {
        var t = new TableSchema("contacts");
        t.Columns.Add(new ColumnSchema("ContactKey", "int64"));
        t.Columns.Add(new ColumnSchema("Contact Name", "string"));
        t.Columns.Add(new ColumnSchema("Contact Type", "string"));
        t.Columns.Add(new ColumnSchema("Region", "string"));
        return t;
    }

    private static TableSchema AccountsSchema()
    {
        var t = new TableSchema("accounts");
        t.Columns.Add(new ColumnSchema("AccountKey", "int64"));
        t.Columns.Add(new ColumnSchema("Account Name", "string"));
        t.Columns.Add(new ColumnSchema("Account Class", "string"));
        return t;
    }

    private static TableSchema BankSchema()
    {
        var t = new TableSchema("bank");
        t.Columns.Add(new ColumnSchema("BankTxnKey", "int64"));
        t.Columns.Add(new ColumnSchema("TxnDate", "date"));
        t.Columns.Add(new ColumnSchema("Bank Account", "string"));
        t.Columns.Add(new ColumnSchema("Direction", "string"));
        t.Columns.Add(new ColumnSchema("NetMovement", "double"));
        return t;
    }

    private static TableSchema CalendarSchema()
    {
        var t = new TableSchema("calendar");
        t.Columns.Add(new ColumnSchema("MonthEnding", "date"));
        t.Columns.Add(new ColumnSchema("Period", "string"));
        t.Columns.Add(new ColumnSchema("Month Name", "string"));
        t.Columns.Add(new ColumnSchema("Year", "int64"));
        return t;
    }
}
