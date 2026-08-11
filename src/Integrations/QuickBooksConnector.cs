using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The QuickBooks Online connector (id "quickbooks") - the second finance source after Xero. A company's
/// accounting data is pulled into the engine host via the Intuit QuickBooks v3 API and turned into the same
/// canonical CSV shape the finance Solution consumes, so everything downstream (scaffold, bake, AI prompt,
/// download) is unchanged. The emitted files match <c>solutions/finance/schema.json</c> EXACTLY - invoices.csv,
/// contacts.csv, accounts.csv, bank.csv and a generated calendar.csv - the very same shape the Xero connector
/// targets, so either accounting source drops onto the finance model.spec.json with no field mapping.
///
/// Entities pulled (all read-only, paged through the query API's STARTPOSITION / MAXRESULTS):
///   - Account  -&gt; accounts.csv (chart of accounts; pulled first so invoices can reference its keys).
///   - Customer -&gt; contacts.csv (the customer dimension; Contact Type = "Customer").
///   - Invoice  -&gt; invoices.csv (revenue + AR facts; Balance drives AmountDue, days past due drive aging)
///     AND, in the same walk, the paid portion of each invoice (TotalAmt - Balance) becomes a cash inflow row
///     in bank.csv, so the finance cash facts are populated from the one entity.
///   - a generated calendar over the months the facts span -&gt; calendar.csv.
///
/// Two operations against the QuickBooks v3 query endpoint (raw HTTP, no SDK / no NuGet):
///   - <see cref="DiscoverAsync"/> issues a cheap count query so the token + realm are confirmed, then reports
///     the fixed finance tables/columns it would emit.
///   - <see cref="FetchAsync"/> pages each entity, writes each through <see cref="CsvSink"/>, and returns the
///     table-&gt;csv map + the (finance) schema. Pagination caps are recorded in
///     <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Auth: the short-lived Intuit OAuth2 access token arrives on <see cref="ConnectorRequest.AccessToken"/>
/// (it rides the X-Connector-Token header into the worker) and is sent as
/// <c>Authorization: Bearer &lt;token&gt;</c>; the company id arrives in <c>Params.realmId</c> and forms the
/// company path segment. Neither the token nor the realm id is ever persisted or logged (not in an error
/// message or a Note).
///
/// Params it reads:
///   realmId     : the QuickBooks company (realm) id (required for a fetch; forms the /v3/company/{realmId} path).
///   environment : "production" (default) or "sandbox" - selects the API base host.
/// </summary>
public sealed class QuickBooksConnector : IDataSourceConnector
{
    public string Id => "quickbooks";
    public string DisplayName => "QuickBooks";

    // QuickBooks v3 API bases. https://developer.intuit.com/app/developer/qbo/docs/api/accounting/all-entities
    private const string ProductionBase = "https://quickbooks.api.intuit.com";
    private const string SandboxBase = "https://sandbox-quickbooks.api.intuit.com";

    // the query API returns at most this many rows per page; we page with STARTPOSITION / MAXRESULTS.
    private const int PageSize = 1_000;

    // hard caps so a very large company cannot exhaust the box. Every cap that bites is recorded in Notes -
    // no silent truncation. MaxPages is mutable only so the test suite can lower it to exercise the cap branch
    // against a tiny stub; production never writes it.
    private const int DefaultMaxPages = 1_000;
    private static int _maxPages = DefaultMaxPages;
    private const long MaxResponseBytes = 256L * 1024 * 1024;

    /// <summary>Test-only seam: temporarily lower the hard page cap so the cap branch can be exercised against a
    /// tiny stub. Returns an <see cref="IDisposable"/> that restores the production cap. Reached only by the
    /// test assembly (InternalsVisibleTo); never used in production.</summary>
    internal static IDisposable UsePageCapForTests(int cap)
    {
        int previous = _maxPages;
        _maxPages = cap;
        return new TransportSwap(() => _maxPages = previous);
    }

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static HttpClient _http = HttpDefaults.New();

    /// <summary>Test-only seam: replace the shared HttpClient's transport with a stub handler so the
    /// connector's real paging walk, cap branches and finance-schema mapping run end to end offline (no live
    /// token, no network). Returns an <see cref="IDisposable"/> that restores the real transport. Reached only
    /// by the test assembly (InternalsVisibleTo); never used in production.</summary>
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

    /// <summary>Shared state for one entity's paging walk: the number of pages fetched and whether the walk
    /// was stopped by the <see cref="_maxPages"/> cap (as opposed to a natural short final page). The cap is
    /// only flagged when the loop actually exhausts at the cap on a FULL final page - a full page that lands
    /// exactly on the cap means there may be more rows the cap prevented us reaching; a short final page (even
    /// one that lands on the cap) is a natural end and is NOT a cap hit.</summary>
    private sealed class PageWalk
    {
        public int Pages;
        public bool Capped;
    }

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // confirm the token + realm can see the company (a cheap count query), then report the fixed tables.
        string? realmId = req.Param("realmId");
        if (!string.IsNullOrWhiteSpace(realmId))
            _ = await QueryAsync(req, realmId, "SELECT COUNT(*) FROM Account", ct).ConfigureAwait(false);
        return FinanceSchema();
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? realmId = req.Param("realmId");
        if (string.IsNullOrWhiteSpace(realmId))
        {
            result.Notes.Add("No realmId was provided; pick a QuickBooks company to ingest.");
            return result;
        }

        var monthEnds = new SortedSet<DateTime>();

        // accounts first so the account-key map is populated before invoices reference it.
        var accountKeys = await FetchAccountsAsync(req, realmId, workDir, result, ct).ConfigureAwait(false);
        var contactKeys = await FetchContactsAsync(req, realmId, workDir, result, ct).ConfigureAwait(false);
        await FetchInvoicesAsync(req, realmId, workDir, result, monthEnds, contactKeys, accountKeys, ct).ConfigureAwait(false);

        WriteCalendar(workDir, result, monthEnds);

        if (result.Tables.Count == 0)
            result.Notes.Add("No data was returned for the chosen company.");
        return result;
    }

    // ---- Account -> accounts.csv ---------------------------------------------------------------

    /// <summary>Pull the chart of accounts into accounts.csv and return a first-seen Id -&gt; small-integer
    /// AccountKey map so invoices reference accounts by the same key. A synthetic catch-all (200, "Sales") is
    /// emitted so an invoice whose account is unknown still has a valid AccountKey.</summary>
    private async Task<Dictionary<string, int>> FetchAccountsAsync(
        ConnectorRequest req, string realmId, string workDir, IngestResult result, CancellationToken ct)
    {
        string[] header = { "AccountKey", "Account Name", "Account Class" };
        string dest = Path.Combine(workDir, "accounts.csv");
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long rows = 0;
        int nextKey = 201; // 200 reserved for the catch-all
        var walk = new PageWalk();

        using (var sink = new CsvSink(dest, header))
        {
            sink.WriteRow(new[] { "200", "Sales", "Revenue" });
            rows++;

            await foreach (var (account, _) in PageAsync(req, realmId, "Account", walk, ct))
            {
                if (account is not JsonObject o) continue;
                string id = Str(o["Id"]);
                if (id.Length == 0) continue;
                int key = nextKey++;
                keys[id] = key;
                sink.WriteRow(new[]
                {
                    key.ToString(),
                    Str(o["Name"]),
                    Str(o["AccountType"] ?? o["Classification"]),
                });
                rows++;
            }
        }

        Register("accounts", dest, result, AccountsSchema());
        result.Notes.Add($"accounts: ingested {rows:N0} account(s) across {walk.Pages} page(s).");
        if (walk.Capped)
            result.Notes.Add($"accounts: page CAP reached ({_maxPages:N0} pages) - the pull is incomplete; not all accounts were ingested.");
        return keys;
    }

    // ---- Customer -> contacts.csv --------------------------------------------------------------

    /// <summary>Pull Customers into contacts.csv and return a first-seen Id -&gt; small-integer ContactKey map
    /// so invoices reference customers by the same key.</summary>
    private async Task<Dictionary<string, int>> FetchContactsAsync(
        ConnectorRequest req, string realmId, string workDir, IngestResult result, CancellationToken ct)
    {
        string[] header = { "ContactKey", "Contact Name", "Contact Type", "Region" };
        string dest = Path.Combine(workDir, "contacts.csv");
        var keys = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        long rows = 0;
        int nextKey = 1;
        var walk = new PageWalk();

        using (var sink = new CsvSink(dest, header))
        {
            await foreach (var (customer, _) in PageAsync(req, realmId, "Customer", walk, ct))
            {
                if (customer is not JsonObject o) continue;
                string id = Str(o["Id"]);
                if (id.Length == 0) continue;
                int key = nextKey++;
                keys[id] = key;
                string name = Str(o["DisplayName"] ?? o["CompanyName"] ?? o["FullyQualifiedName"]);
                sink.WriteRow(new[]
                {
                    key.ToString(),
                    name,
                    "Customer",
                    Str(o["BillAddr"]?["City"] ?? o["BillAddr"]?["CountrySubDivisionCode"]),
                });
                rows++;
            }
        }

        Register("contacts", dest, result, ContactsSchema());
        result.Notes.Add($"contacts: ingested {rows:N0} contact(s) across {walk.Pages} page(s).");
        if (walk.Capped)
            result.Notes.Add($"contacts: page CAP reached ({_maxPages:N0} pages) - the pull is incomplete; not all contacts were ingested.");
        return keys;
    }

    // ---- Invoice -> invoices.csv + bank.csv ----------------------------------------------------

    /// <summary>Pull Invoices into invoices.csv (revenue + AR; Balance drives AmountDue, the days past the due
    /// date drive aging) AND, in the same walk, derive a bank.csv inflow row for the paid portion of each
    /// invoice (TotalAmt - Balance) so the finance cash facts are populated from one entity.</summary>
    private async Task FetchInvoicesAsync(
        ConnectorRequest req, string realmId, string workDir, IngestResult result, SortedSet<DateTime> monthEnds,
        Dictionary<string, int> contactKeys, Dictionary<string, int> accountKeys, CancellationToken ct)
    {
        string[] invHeader =
        {
            "InvoiceKey", "ContactKey", "AccountKey", "InvoiceDate", "DueDate", "Type", "Status",
            "Total", "CostAmount", "AmountPaid", "AmountDue", "DaysOutstanding",
        };
        string[] bankHeader = { "BankTxnKey", "TxnDate", "Bank Account", "Direction", "NetMovement" };

        string invDest = Path.Combine(workDir, "invoices.csv");
        string bankDest = Path.Combine(workDir, "bank.csv");
        long invRows = 0, bankRows = 0;
        int invoiceKey = 0, bankKey = 0;
        DateTime today = DateTime.UtcNow.Date;
        var walk = new PageWalk();

        using (var invSink = new CsvSink(invDest, invHeader))
        using (var bankSink = new CsvSink(bankDest, bankHeader))
        {
            await foreach (var (inv, _) in PageAsync(req, realmId, "Invoice", walk, ct))
            {
                if (inv is not JsonObject o) continue;
                invoiceKey++;

                DateTime invDate = ParseDate(o["TxnDate"]) ?? today;
                DateTime dueDate = ParseDate(o["DueDate"]) ?? invDate;
                decimal total = Dec(o["TotalAmt"]);
                decimal due = Dec(o["Balance"]);
                decimal paid = total - due;
                if (paid < 0) paid = 0;
                int daysOut = due > 0 ? (int)(today - dueDate).TotalDays : 0;
                string status = due <= 0 ? "PAID" : "AUTHORISED";

                int contactKey = LookupKey(o["CustomerRef"]?["value"], contactKeys);
                int accountKey = ResolveAccountKey(o["Line"], accountKeys);

                DateTime monthEnd = MonthEnd(invDate);
                monthEnds.Add(monthEnd);

                invSink.WriteRow(new[]
                {
                    invoiceKey.ToString(),
                    contactKey.ToString(),
                    accountKey.ToString(),
                    Iso(monthEnd),            // InvoiceDate joins calendar.MonthEnding (month-end grain)
                    Iso(dueDate),
                    "ACCREC",                 // QuickBooks Invoice is always a sales (receivable) document
                    status,
                    Num(total),
                    "",                        // CostAmount - not on the invoice header
                    Num(paid),
                    Num(due),
                    daysOut.ToString(),
                });
                invRows++;

                // the paid portion becomes a cash inflow on the invoice's month-end (bank.csv).
                if (paid > 0)
                {
                    bankKey++;
                    bankSink.WriteRow(new[]
                    {
                        bankKey.ToString(),
                        Iso(monthEnd),
                        "Accounts Receivable",
                        "Inflow",
                        Num(paid),
                    });
                    bankRows++;
                }
            }
        }

        Register("invoices", invDest, result, InvoicesSchema());
        Register("bank", bankDest, result, BankSchema());
        result.Notes.Add($"invoices: ingested {invRows:N0} invoice(s) across {walk.Pages} page(s).");
        result.Notes.Add($"bank: derived {bankRows:N0} cash inflow(s) from paid invoices.");
        if (walk.Capped)
            result.Notes.Add($"invoices: page CAP reached ({_maxPages:N0} pages) - the pull is incomplete; not all invoices were ingested.");
    }

    // ---- generated Calendar -> calendar.csv ----------------------------------------------------

    /// <summary>Generate calendar.csv over the span of month-ends touched by the facts; the most recent 12
    /// months are "This Year" and the prior months "Last Year" (matching the finance Solution's growth split).
    /// Falls back to the last 24 months ending this month when no dated facts were ingested.</summary>
    private static void WriteCalendar(string workDir, IngestResult result, SortedSet<DateTime> monthEnds)
    {
        string[] header = { "MonthEnding", "Period", "Month Name", "Year" };
        string dest = Path.Combine(workDir, "calendar.csv");

        var months = BuildMonthSpan(monthEnds);
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

    // ---- query API + paging --------------------------------------------------------------------

    /// <summary>Page an entity through the v3 query API with STARTPOSITION / MAXRESULTS, yielding each row of
    /// every page paired with the 1-based page number, and recording the walk's outcome on
    /// <paramref name="walk"/>. A short page (fewer than <see cref="PageSize"/> rows) is a natural end and is
    /// NOT a cap. The cap is flagged ONLY when the loop runs the <see cref="_maxPages"/>th page and that page
    /// is FULL (so there may be more rows the cap prevented us reaching) - a full page landing exactly on the
    /// cap is the only way capping bites; a short final page (even one that happens to land on the cap) is a
    /// clean exhaustion, not a truncation.</summary>
    private async IAsyncEnumerable<(JsonNode? row, int page)> PageAsync(
        ConnectorRequest req, string realmId, string entity, PageWalk walk,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        for (int page = 1; page <= _maxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            int start = (page - 1) * PageSize + 1; // QuickBooks STARTPOSITION is 1-based
            string query = $"SELECT * FROM {entity} STARTPOSITION {start} MAXRESULTS {PageSize}";

            JsonNode? root = await QueryAsync(req, realmId, query, ct).ConfigureAwait(false);
            if (root?["QueryResponse"]?[entity] is not JsonArray rowsArray || rowsArray.Count == 0)
                yield break; // an empty page ends the walk cleanly (no cap)

            walk.Pages = page;
            foreach (var row in rowsArray)
                yield return (row, page);

            if (rowsArray.Count < PageSize) yield break; // a short page ends pagination - a natural end, no cap

            // the page was FULL. If it was the cap'th page, the for-loop is about to exit at the cap with more
            // rows likely remaining - that, and only that, is a genuine cap hit.
            if (page == _maxPages) walk.Capped = true;
        }
    }

    /// <summary>Issue one query against /v3/company/{realmId}/query and return the parsed JSON root. The
    /// Bearer token and realm id are applied to the request only; neither is written to a Note or an exception
    /// message.</summary>
    private static async Task<JsonNode?> QueryAsync(ConnectorRequest req, string realmId, string query, CancellationToken ct)
    {
        string baseUrl = Environment(req);
        string url = baseUrl + "/v3/company/" + Uri.EscapeDataString(realmId)
            + "/query?minorversion=70&query=" + Uri.EscapeDataString(query);

        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(req.AccessToken))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", req.AccessToken);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"QuickBooks API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>The API base for the chosen environment: sandbox host for "sandbox", production otherwise.</summary>
    private static string Environment(ConnectorRequest req)
        => string.Equals(req.Param("environment"), "sandbox", StringComparison.OrdinalIgnoreCase)
            ? SandboxBase : ProductionBase;

    // ---- key resolution ------------------------------------------------------------------------

    /// <summary>Look up a referenced entity id in a first-seen key map; an unknown / blank id maps to 0.</summary>
    private static int LookupKey(JsonNode? idNode, Dictionary<string, int> map)
    {
        string id = Str(idNode);
        return id.Length > 0 && map.TryGetValue(id, out var k) ? k : 0;
    }

    /// <summary>Derive an AccountKey for an invoice from the income account of its first SalesItemLineDetail;
    /// defaults to the catch-all 200 ("Sales") when no account reference is present.</summary>
    private static int ResolveAccountKey(JsonNode? lines, Dictionary<string, int> accountKeys)
    {
        if (lines is JsonArray arr)
            foreach (var line in arr)
            {
                var accRef = line?["SalesItemLineDetail"]?["ItemAccountRef"]?["value"]
                             ?? line?["AccountBasedExpenseLineDetail"]?["AccountRef"]?["value"];
                string id = Str(accRef);
                if (id.Length > 0 && accountKeys.TryGetValue(id, out var k)) return k;
            }
        return 200;
    }

    // ---- value helpers -------------------------------------------------------------------------

    /// <summary>Parse a QuickBooks date (ISO yyyy-MM-dd, possibly with a time component). Returns the date
    /// component only, or null when blank / unparseable.</summary>
    private static DateTime? ParseDate(JsonNode? node)
    {
        string s = Str(node);
        if (s.Length == 0) return null;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.Date : null;
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
