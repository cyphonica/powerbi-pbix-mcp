using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The WooCommerce connector (id "woo") - the key-based e-commerce source (ships before the OAuth one). A
/// store's sales data is pulled straight into the engine host and turned into the canonical CSV shape the
/// e-commerce Solution consumes, so everything downstream (scaffold, bake, AI prompt, download) is unchanged.
/// The emitted files match <c>solutions/ecommerce/schema.json</c> EXACTLY - orders.csv, order_lines.csv,
/// products.csv, customers.csv and a generated calendar.csv - so the output drops onto the e-commerce
/// model.spec.json with no field mapping.
///
/// Entities pulled (all read-only, paginated via the per_page / page params):
///   - orders (wc/v3/orders)        -&gt; orders.csv (header: total, refund, customer type) + the line items
///                                     within each order -&gt; order_lines.csv.
///   - products (wc/v3/products)    -&gt; products.csv (the item dimension).
///   - customers (wc/v3/customers)  -&gt; customers.csv (Acquisition Date = first-order date for cohorts).
///   - a generated Calendar over the months the orders span -&gt; calendar.csv.
///
/// Auth: WooCommerce REST is KEY-based, not OAuth. A read-only consumer key/secret pair is supplied per job;
/// it is read from <c>Params.consumerKey</c> / <c>Params.consumerSecret</c> (or, when the broker passes the
/// pair packed onto <see cref="ConnectorRequest.AccessToken"/> as "key:secret", from there). Over HTTPS the
/// pair is sent as HTTP Basic auth. The key and secret are used per job only - never persisted, never logged
/// (not even in an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. Per-store pagination caps
/// are recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   storeUrl       : the store's base URL, e.g. https://shop.example.com (required).
///   consumerKey    : the read-only WooCommerce consumer key (or packed on AccessToken as "key:secret").
///   consumerSecret : the matching consumer secret.
/// </summary>
public sealed class WooConnector : IDataSourceConnector
{
    public string Id => "woo";
    public string DisplayName => "WooCommerce";

    // WooCommerce REST namespace. https://woocommerce.github.io/woocommerce-rest-api-docs/
    private const string ApiPath = "/wp-json/wc/v3/";

    private const int PageSize = 100; // Woo caps per_page at 100

    // hard caps so a very large store cannot exhaust the box. Every cap that bites is recorded in Notes -
    // no silent truncation.
    private const int MaxPages = 1_000;                        // pages followed per entity before the cap bites
    private const long MaxResponseBytes = 256L * 1024 * 1024;  // cap on any single API response body

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static readonly HttpClient _http = HttpDefaults.New();

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Confirm the store + credentials resolve by reading a single page of products (cheap), then report
        // the fixed e-commerce tables this connector emits.
        var (storeUrl, auth) = Resolve(req);
        if (storeUrl != null)
            _ = await GetArrayAsync(BuildUrl(storeUrl, "products", 1, PageSize), auth, ct).ConfigureAwait(false);
        return EcommerceSchema();
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        var (storeUrl, auth) = Resolve(req);
        if (storeUrl == null)
        {
            result.Notes.Add("No storeUrl was provided; nothing to ingest.");
            return result;
        }

        var monthEnds = new SortedSet<DateTime>();
        var firstOrderByCustomer = new Dictionary<int, DateTime>();

        await FetchProductsAsync(storeUrl, auth, workDir, result, ct).ConfigureAwait(false);
        var customerNames = await FetchCustomersBaseAsync(storeUrl, auth, result, ct).ConfigureAwait(false);
        await FetchOrdersAsync(storeUrl, auth, workDir, result, monthEnds, firstOrderByCustomer, ct).ConfigureAwait(false);

        // customers.csv needs the first-order (acquisition) date, derived from the orders pass above.
        WriteCustomers(workDir, result, customerNames, firstOrderByCustomer);
        WriteCalendar(workDir, result, monthEnds);

        if (result.Tables.Count == 0)
            result.Notes.Add("No data was returned from the store.");
        return result;
    }

    // ---- orders -> orders.csv + order_lines.csv ------------------------------------------------

    /// <summary>Pull orders, paginated, writing the order header to orders.csv and each line item to
    /// order_lines.csv. Customer Type is New on a customer's first order in this pull and Returning
    /// thereafter; the first-order date per customer is captured for the customers dimension. OrderDate is
    /// snapped to month-end so it joins calendar.OrderDate.</summary>
    private async Task FetchOrdersAsync(string storeUrl, string? auth, string workDir, IngestResult result,
        SortedSet<DateTime> monthEnds, Dictionary<int, DateTime> firstOrderByCustomer, CancellationToken ct)
    {
        string[] ordersHeader = { "OrderKey", "CustomerKey", "OrderDate", "Order Total", "Order Refund", "Customer Type" };
        string[] linesHeader = { "OrderKey", "ProductKey", "CustomerKey", "OrderDate", "Line Sales", "Line Units", "Line Refund" };

        string ordersDest = Path.Combine(workDir, "orders.csv");
        string linesDest = Path.Combine(workDir, "order_lines.csv");

        long orderRows = 0, lineRows = 0;
        var seenCustomers = new HashSet<int>();
        var (capped, pages) = (false, 0);

        using var ordersSink = new CsvSink(ordersDest, ordersHeader);
        using var linesSink = new CsvSink(linesDest, linesHeader);

        for (int page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            pages = page;
            var orders = await GetArrayAsync(BuildUrl(storeUrl, "orders", page, PageSize), auth, ct).ConfigureAwait(false);
            if (orders is not { Count: > 0 }) break;

            foreach (var ord in orders)
            {
                if (ord is not JsonObject o) continue;
                int orderKey = Int(o["id"]);
                int customerKey = Int(o["customer_id"]);
                DateTime created = ParseDate(o["date_created"]) ?? DateTime.UtcNow.Date;
                DateTime monthEnd = MonthEnd(created);
                monthEnds.Add(monthEnd);
                decimal total = Dec(o["total"]);
                decimal refund = SumRefunds(o["refunds"]);

                bool isNew = customerKey != 0 ? seenCustomers.Add(customerKey) : true;
                if (customerKey != 0)
                {
                    if (!firstOrderByCustomer.TryGetValue(customerKey, out var prev) || created < prev)
                        firstOrderByCustomer[customerKey] = created;
                }

                ordersSink.WriteRow(new[]
                {
                    orderKey.ToString(),
                    customerKey.ToString(),
                    Iso(monthEnd),
                    Num(total),
                    Num(refund),
                    isNew ? "New" : "Returning",
                });
                orderRows++;

                // Woo carries the refund at the ORDER level (refunds[]), not on each line item. To make
                // refund-by-product / refund-by-category analysis possible, pro-rate the order's refund across
                // its lines by line sales, so the line refunds sum back to the order's Order Refund.
                if (o["line_items"] is JsonArray items)
                {
                    var lines = new List<(int productId, decimal lineTotal, int qty)>();
                    decimal lineSalesSum = 0m;
                    foreach (var it in items)
                    {
                        if (it is not JsonObject li) continue;
                        decimal lt = Dec(li["total"]);
                        lines.Add((Int(li["product_id"]), lt, Int(li["quantity"])));
                        lineSalesSum += lt;
                    }
                    decimal allocated = 0m;
                    for (int li = 0; li < lines.Count; li++)
                    {
                        var (productId, lineTotal, qty) = lines[li];
                        // allocate by line-sales share; the LAST line absorbs the rounding remainder so the
                        // line refunds sum EXACTLY to the order refund.
                        decimal lineRefund = li == lines.Count - 1
                            ? Math.Max(0m, refund - allocated)
                            : (lineSalesSum > 0m ? Math.Round(refund * (lineTotal / lineSalesSum), 2) : 0m);
                        allocated += lineRefund;
                        linesSink.WriteRow(new[]
                        {
                            orderKey.ToString(),
                            productId.ToString(),
                            customerKey.ToString(),
                            Iso(monthEnd),
                            Num(lineTotal),
                            qty.ToString(),
                            Num(lineRefund),
                        });
                        lineRows++;
                    }
                }
            }

            if (orders.Count < PageSize) break;
            if (page == MaxPages) capped = true;
        }

        ordersSink.Dispose();
        linesSink.Dispose();

        Register("orders", ordersDest, result, OrdersSchema());
        Register("order_lines", linesDest, result, OrderLinesSchema());
        result.Notes.Add($"orders: ingested {orderRows:N0} order(s) and {lineRows:N0} line(s) across {pages} page(s).");
        if (capped)
            result.Notes.Add($"orders: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all orders were ingested.");
    }

    // ---- products -> products.csv --------------------------------------------------------------

    /// <summary>Pull products, paginated, into products.csv (the item dimension). Category is the first
    /// assigned product category name. The DISTINCT set of categories is also written to category.csv (the
    /// one-side of Product[Category] -&gt; Category[Category]); it must be row-distinct or the bake fails.</summary>
    private async Task FetchProductsAsync(string storeUrl, string? auth, string workDir, IngestResult result, CancellationToken ct)
    {
        string[] header = { "ProductKey", "Product Name", "Category" };
        string dest = Path.Combine(workDir, "products.csv");
        long rows = 0;
        var (capped, pages) = (false, 0);
        var categories = new SortedSet<string>(StringComparer.Ordinal);

        using (var sink = new CsvSink(dest, header))
            for (int page = 1; page <= MaxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                pages = page;
                var products = await GetArrayAsync(BuildUrl(storeUrl, "products", page, PageSize), auth, ct).ConfigureAwait(false);
                if (products is not { Count: > 0 }) break;

                foreach (var p in products)
                {
                    if (p is not JsonObject o) continue;
                    string category = Str(FirstOf(o["categories"])?["name"]);
                    if (category.Length > 0) categories.Add(category);
                    sink.WriteRow(new[]
                    {
                        Int(o["id"]).ToString(),
                        Str(o["name"]),
                        category,
                    });
                    rows++;
                }

                if (products.Count < PageSize) break;
                if (page == MaxPages) capped = true;
            }

        Register("products", dest, result, ProductsSchema());
        result.Notes.Add($"products: ingested {rows:N0} product(s) across {pages} page(s).");
        if (capped)
            result.Notes.Add($"products: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all products were ingested.");

        WriteDistinctDim(workDir, result, "category", "Category", categories, CategorySchema());
    }

    /// <summary>Write a single-column dimension CSV of DISTINCT values (e.g. category.csv / channel.csv). The
    /// rows are already deduped by the SortedSet, so the file is the unique one-side a many-to-one relationship
    /// requires - this is what makes the AS bake (which materialises each table row-for-row from its CSV, with no
    /// Power Query dedup) load without a duplicate-key error.</summary>
    private static void WriteDistinctDim(string workDir, IngestResult result, string table, string column,
        SortedSet<string> values, TableSchema schema)
    {
        string dest = Path.Combine(workDir, table + ".csv");
        using (var sink = new CsvSink(dest, new[] { column }))
            foreach (var v in values) sink.WriteRow(new[] { v });
        Register(table, dest, result, schema);
        result.Notes.Add($"{table}: {values.Count:N0} distinct {column.ToLowerInvariant()} value(s).");
    }

    // ---- customers (base names) ----------------------------------------------------------------

    /// <summary>Pull customers, paginated, capturing each customer's key -&gt; (name, channel). The acquisition
    /// (first-order) date is filled in later from the orders pass, so this only collects the dimension's
    /// descriptive fields here.</summary>
    private async Task<Dictionary<int, (string name, string channel)>> FetchCustomersBaseAsync(
        string storeUrl, string? auth, IngestResult result, CancellationToken ct)
    {
        var names = new Dictionary<int, (string, string)>();
        var (capped, pages) = (false, 0);

        for (int page = 1; page <= MaxPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            pages = page;
            var customers = await GetArrayAsync(BuildUrl(storeUrl, "customers", page, PageSize), auth, ct).ConfigureAwait(false);
            if (customers is not { Count: > 0 }) break;

            foreach (var c in customers)
            {
                if (c is not JsonObject o) continue;
                int key = Int(o["id"]);
                string name = $"{Str(o["first_name"])} {Str(o["last_name"])}".Trim();
                if (name.Length == 0) name = Str(o["username"] ?? o["email"]);
                names[key] = (name, Str(o["role"]));
            }

            if (customers.Count < PageSize) break;
            if (page == MaxPages) capped = true;
        }

        if (capped)
            result.Notes.Add($"customers: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all customers were ingested.");

        return names;
    }

    /// <summary>Write customers.csv, joining the descriptive fields collected above with the first-order
    /// (acquisition) date derived from the orders pass. A guest order's customer (key 0) is written once so its
    /// orders still resolve a CustomerKey.</summary>
    private static void WriteCustomers(string workDir, IngestResult result,
        Dictionary<int, (string name, string channel)> names, Dictionary<int, DateTime> firstOrder)
    {
        string[] header = { "CustomerKey", "Customer Name", "Channel", "Acquisition Date" };
        string dest = Path.Combine(workDir, "customers.csv");
        long rows = 0;

        // every customer key that appears in either the customer list or an order gets a row
        var keys = new SortedSet<int>(names.Keys);
        foreach (var k in firstOrder.Keys) keys.Add(k);
        if (firstOrder.ContainsKey(0) || names.ContainsKey(0)) keys.Add(0);

        var channels = new SortedSet<string>(StringComparer.Ordinal);
        using (var sink = new CsvSink(dest, header))
            foreach (var key in keys)
            {
                string name = names.TryGetValue(key, out var d) && d.name.Length > 0 ? d.name
                    : key == 0 ? "Guest" : $"Customer {key}";
                string channel = names.TryGetValue(key, out var d2) ? d2.channel : "";
                if (channel.Length > 0) channels.Add(channel);
                string acq = firstOrder.TryGetValue(key, out var dt) ? Iso(MonthEnd(dt)) : "";
                sink.WriteRow(new[] { key.ToString(), name, channel, acq });
                rows++;
            }

        Register("customers", dest, result, CustomersSchema());
        result.Notes.Add($"customers: ingested {rows:N0} customer(s).");

        WriteDistinctDim(workDir, result, "channel", "Channel", channels, ChannelSchema());
    }

    // ---- generated Calendar -> calendar.csv ----------------------------------------------------

    /// <summary>Generate calendar.csv over the span of month-ends the orders touched (one row per month-end;
    /// the most recent 12 months are "This Year", earlier months "Last Year"). The calendar's date column is
    /// "OrderDate" to match the e-commerce schema's join key.</summary>
    private static void WriteCalendar(string workDir, IngestResult result, SortedSet<DateTime> monthEnds)
    {
        string[] header = { "OrderDate", "Period", "Month Name", "Year" };
        string dest = Path.Combine(workDir, "calendar.csv");

        var months = EcommerceCalendar.BuildMonthSpan(monthEnds);
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

    // ---- HTTP ----------------------------------------------------------------------------------

    /// <summary>GET a Woo endpoint page and return its JSON array body (Woo list endpoints return a bare array).
    /// The consumer key/secret are applied as HTTP Basic auth only; they are never written to a Note or an
    /// exception message.</summary>
    private static async Task<JsonArray?> GetArrayAsync(string url, string? basicAuth, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(basicAuth))
            msg.Headers.Authorization = new AuthenticationHeaderValue("Basic", basicAuth);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            int code = (int)resp.StatusCode;
            // A Cloudflare/WAF bot challenge returns an HTML "Just a moment" interstitial (or a cf-mitigated
            // header) instead of a WooCommerce JSON error. Detect it so we can tell the user how to let DaxOps
            // through, rather than surfacing a bare 403 that looks like a key problem.
            string snippet = "";
            try { var raw = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false); snippet = raw.Length > 600 ? raw[..600] : raw; } catch { }
            bool challenge = resp.Headers.Contains("cf-mitigated")
                || snippet.Contains("Just a moment", System.StringComparison.OrdinalIgnoreCase)
                || snippet.Contains("challenge-platform", System.StringComparison.OrdinalIgnoreCase)
                || snippet.Contains("cf-browser-verification", System.StringComparison.OrdinalIgnoreCase)
                || (snippet.Contains("Cloudflare", System.StringComparison.OrdinalIgnoreCase) && (code == 403 || code == 503));
            if (challenge)
                throw new InvalidOperationException(
                    "The store is behind Cloudflare/WAF bot protection, which blocked the request (HTTP " + code + "). " +
                    "Ask the store owner to allow the connector through in Cloudflare (any one of): allowlist the user-agent " +
                    "\"DaxOps/1.0\", allowlist the IP of the machine running this engine, or add a WAF rule to skip /wp-json/. " +
                    "This is a store-side setting, not a key problem.");
            throw new InvalidOperationException($"WooCommerce API returned HTTP {code} {resp.ReasonPhrase}.");
        }

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        return await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false) as JsonArray;
    }

    /// <summary>Build a list-endpoint URL: {storeUrl}/wp-json/wc/v3/{resource}?per_page=&amp;page=&amp;orderby=id.</summary>
    private static string BuildUrl(string storeUrl, string resource, int page, int perPage)
        => storeUrl.TrimEnd('/') + ApiPath + resource + "?per_page=" + perPage + "&page=" + page + "&orderby=id&order=asc";

    /// <summary>Resolve the store URL and the Basic-auth credential. The key/secret may arrive as explicit
    /// params or packed on the access token as "key:secret". Returns (null, null) when the store URL is
    /// missing or not http/https.</summary>
    private static (string? storeUrl, string? basicAuth) Resolve(ConnectorRequest req)
    {
        string? storeUrl = req.Param("storeUrl");
        if (string.IsNullOrWhiteSpace(storeUrl)
            || !Uri.TryCreate(storeUrl, UriKind.Absolute, out var u)
            || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            return (null, null);

        string? key = req.Param("consumerKey");
        string? secret = req.Param("consumerSecret");
        if ((string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            && req.AccessToken is { Length: > 0 } packed && packed.Contains(':'))
        {
            int i = packed.IndexOf(':');
            key = packed[..i];
            secret = packed[(i + 1)..];
        }
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(secret))
            return (storeUrl, null);

        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(key + ":" + secret));
        return (storeUrl, basic);
    }

    // ---- value helpers -------------------------------------------------------------------------

    /// <summary>Sum the refund totals on an order's refunds[] (each carries a negative total in Woo); returned
    /// as a non-negative amount to match the schema's ">= 0" rule.</summary>
    private static decimal SumRefunds(JsonNode? refunds)
    {
        if (refunds is not JsonArray arr) return 0m;
        decimal sum = 0m;
        foreach (var r in arr)
            if (r is JsonObject o) sum += Math.Abs(Dec(o["total"]));
        return sum;
    }

    private static DateTime MonthEnd(DateTime d)
        => new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    private static DateTime? ParseDate(JsonNode? node)
    {
        string s = Str(node);
        if (s.Length == 0) return null;
        return DateTime.TryParse(s, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var dt)
            ? dt.Date : null;
    }

    private static string Iso(DateTime d) => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private static string Num(decimal d) => d.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static int Int(JsonNode? node)
        => int.TryParse(Str(node), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static decimal Dec(JsonNode? node)
        => decimal.TryParse(Str(node), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;

    /// <summary>The first element of a node when it is a non-empty array, else null (safe on a null /
    /// non-array node so a malformed payload never throws).</summary>
    private static JsonNode? FirstOf(JsonNode? node) => node is JsonArray { Count: > 0 } a ? a[0] : null;

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v => v.ToString(),
        _ => node.ToJsonString(),
    };

    // ---- schema (matches solutions/ecommerce/schema.json exactly) ------------------------------

    private static void Register(string table, string csvPath, IngestResult result, TableSchema schema)
    {
        result.Tables[table] = csvPath;
        result.Schema.Tables.Add(schema);
    }

    private static SchemaDiscovery EcommerceSchema()
    {
        var s = new SchemaDiscovery();
        s.Tables.Add(OrdersSchema());
        s.Tables.Add(OrderLinesSchema());
        s.Tables.Add(ProductsSchema());
        s.Tables.Add(CategorySchema());
        s.Tables.Add(CustomersSchema());
        s.Tables.Add(ChannelSchema());
        s.Tables.Add(CalendarSchema());
        return s;
    }

    private static TableSchema OrdersSchema() => EcommerceCalendar.OrdersSchema();
    private static TableSchema OrderLinesSchema() => EcommerceCalendar.OrderLinesSchema();
    private static TableSchema ProductsSchema() => EcommerceCalendar.ProductsSchema();
    private static TableSchema CategorySchema() => EcommerceCalendar.CategorySchema();
    private static TableSchema CustomersSchema() => EcommerceCalendar.CustomersSchema();
    private static TableSchema ChannelSchema() => EcommerceCalendar.ChannelSchema();
    private static TableSchema CalendarSchema() => EcommerceCalendar.CalendarSchema();
}
