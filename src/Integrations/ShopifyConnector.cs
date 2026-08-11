using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The Shopify connector (id "shopify") - the OAuth e-commerce source. A store's sales data is pulled straight
/// into the engine host and turned into the canonical CSV shape the e-commerce Solution consumes, so
/// everything downstream (scaffold, bake, AI prompt, download) is unchanged. The emitted files match
/// <c>solutions/ecommerce/schema.json</c> EXACTLY - orders.csv, order_lines.csv, products.csv, customers.csv
/// and a generated calendar.csv - so the output drops onto the e-commerce model.spec.json with no field
/// mapping. It shares the schema and calendar shaping with the WooCommerce connector via
/// <see cref="EcommerceCalendar"/>.
///
/// Entities pulled (all read-only, cursor-paginated via the Link header):
///   - orders (Admin REST /orders.json)        -&gt; orders.csv (header: total, refund, customer type) + the
///                                                line items within each order -&gt; order_lines.csv.
///   - products (Admin REST /products.json)    -&gt; products.csv (the item dimension).
///   - customers (Admin REST /customers.json)  -&gt; customers.csv (Acquisition Date = first-order date).
///   - a generated Calendar over the months the orders span -&gt; calendar.csv.
///
/// Auth: OAuth2 per shop. The short-lived access token arrives on <see cref="ConnectorRequest.AccessToken"/>
/// and is sent as the <c>X-Shopify-Access-Token</c> header; the shop domain (e.g. acme.myshopify.com) arrives
/// in <c>Params.shopDomain</c>. Neither the token nor the shop domain is ever persisted or logged (not even in
/// an error message or a Note).
///
/// Data residency: the fetch runs on the engine host, the CSVs are staged into <c>workDir</c> (which the
/// caller wipes after the bake), and the data is never shared with third parties. Per-store cursor-pagination
/// caps are recorded in <see cref="IngestResult.Notes"/> - no silent truncation.
///
/// Params it reads:
///   shopDomain : the store's myshopify domain, e.g. acme.myshopify.com (required).
/// </summary>
public sealed class ShopifyConnector : IDataSourceConnector
{
    public string Id => "shopify";
    public string DisplayName => "Shopify";

    // Admin REST version. https://shopify.dev/docs/api/admin-rest
    private const string ApiVersion = "2024-07";
    private const string TokenHeader = "X-Shopify-Access-Token";

    private const int PageSize = 250; // Shopify caps limit at 250 for these list endpoints

    // hard caps so a very large store cannot exhaust the box. Every cap that bites is recorded in Notes -
    // no silent truncation.
    private const int MaxPages = 1_000;                        // cursor pages followed per entity before the cap bites
    private const long MaxResponseBytes = 256L * 1024 * 1024;  // cap on any single API response body

    // one shared HttpClient (BCL guidance: never one-per-request).
    private static readonly HttpClient _http = HttpDefaults.New();

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        // Confirm the shop + token resolve by reading a single page of products (cheap), then report the fixed
        // e-commerce tables this connector emits.
        string? shop = NormaliseShop(req.Param("shopDomain"));
        if (shop != null)
            _ = await GetPageAsync(FirstUrl(shop, "products"), req.AccessToken, ct).ConfigureAwait(false);
        return EcommerceSchema();
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        string? shop = NormaliseShop(req.Param("shopDomain"));
        if (shop == null)
        {
            result.Notes.Add("No shopDomain was provided; nothing to ingest.");
            return result;
        }

        var monthEnds = new SortedSet<DateTime>();
        var firstOrderByCustomer = new Dictionary<long, DateTime>();
        var customerNames = new Dictionary<long, (string name, string channel, DateTime? created)>();

        await FetchProductsAsync(shop, req.AccessToken, workDir, result, ct).ConfigureAwait(false);
        await FetchCustomersBaseAsync(shop, req.AccessToken, result, customerNames, ct).ConfigureAwait(false);
        await FetchOrdersAsync(shop, req.AccessToken, workDir, result, monthEnds, firstOrderByCustomer, ct).ConfigureAwait(false);

        WriteCustomers(workDir, result, customerNames, firstOrderByCustomer);
        WriteCalendar(workDir, result, monthEnds);

        if (result.Tables.Count == 0)
            result.Notes.Add("No data was returned from the store.");
        return result;
    }

    // ---- orders -> orders.csv + order_lines.csv ------------------------------------------------

    /// <summary>Pull orders, cursor-paginated, writing the order header to orders.csv and each line item to
    /// order_lines.csv. Customer Type is New on a customer's first order in this pull and Returning thereafter;
    /// the first-order date per customer is captured for the customers dimension. OrderDate is snapped to
    /// month-end so it joins calendar.OrderDate.</summary>
    private async Task FetchOrdersAsync(string shop, string? token, string workDir, IngestResult result,
        SortedSet<DateTime> monthEnds, Dictionary<long, DateTime> firstOrderByCustomer, CancellationToken ct)
    {
        string[] ordersHeader = { "OrderKey", "CustomerKey", "OrderDate", "Order Total", "Order Refund", "Customer Type" };
        string[] linesHeader = { "OrderKey", "ProductKey", "CustomerKey", "OrderDate", "Line Sales", "Line Units", "Line Refund" };

        string ordersDest = Path.Combine(workDir, "orders.csv");
        string linesDest = Path.Combine(workDir, "order_lines.csv");

        long orderRows = 0, lineRows = 0;
        var seenCustomers = new HashSet<long>();
        var (capped, pages) = (false, 0);

        using var ordersSink = new CsvSink(ordersDest, ordersHeader);
        using var linesSink = new CsvSink(linesDest, linesHeader);

        // status=any so cancelled / archived orders are included in the history.
        string? url = FirstUrl(shop, "orders") + "&status=any";
        while (url != null && pages < MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            pages++;
            var (root, next) = await GetPageAsync(url, token, ct).ConfigureAwait(false);
            if (root?["orders"] is not JsonArray orders || orders.Count == 0) break;

            foreach (var ord in orders)
            {
                if (ord is not JsonObject o) continue;
                long orderKey = Long(o["id"]);
                long customerKey = Long(o["customer"]?["id"]);
                DateTime orderDate = OrderDate(o);
                DateTime monthEnd = MonthEnd(orderDate);
                monthEnds.Add(monthEnd);
                decimal total = Dec(o["total_price"]);
                decimal refund = SumRefunds(o["refunds"]);

                bool isNew = customerKey != 0 ? seenCustomers.Add(customerKey) : true;
                if (customerKey != 0)
                {
                    if (!firstOrderByCustomer.TryGetValue(customerKey, out var prev) || orderDate < prev)
                        firstOrderByCustomer[customerKey] = orderDate;
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

                // Shopify carries the refund at the ORDER level (refunds[]). To make refund-by-product /
                // refund-by-category analysis possible, pro-rate the order's refund across its lines by line
                // sales, so the line refunds sum back to the order's Order Refund.
                if (o["line_items"] is JsonArray items)
                {
                    var lines = new List<(long productId, decimal lineTotal, int qty)>();
                    decimal lineSalesSum = 0m;
                    foreach (var it in items)
                    {
                        if (it is not JsonObject li) continue;
                        decimal price = Dec(li["price"]);
                        int qty = Int(li["quantity"]);
                        decimal lt = price * qty;
                        lines.Add((Long(li["product_id"]), lt, qty));
                        lineSalesSum += lt;
                    }
                    decimal allocated = 0m;
                    for (int li = 0; li < lines.Count; li++)
                    {
                        var (productId, lineTotal, qty) = lines[li];
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
            url = next;
            if (pages == MaxPages && next != null) capped = true;
        }

        ordersSink.Dispose();
        linesSink.Dispose();

        Register("orders", ordersDest, result, EcommerceCalendar.OrdersSchema());
        Register("order_lines", linesDest, result, EcommerceCalendar.OrderLinesSchema());
        result.Notes.Add($"orders: ingested {orderRows:N0} order(s) and {lineRows:N0} line(s) across {pages} page(s).");
        if (capped)
            result.Notes.Add($"orders: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all orders were ingested.");
    }

    // ---- products -> products.csv --------------------------------------------------------------

    /// <summary>Pull products, cursor-paginated, into products.csv (the item dimension). Category is the
    /// product_type. The DISTINCT set of categories is also written to category.csv (the one-side of
    /// Product[Category] -&gt; Category[Category]); it must be row-distinct or the bake fails.</summary>
    private async Task FetchProductsAsync(string shop, string? token, string workDir, IngestResult result, CancellationToken ct)
    {
        string[] header = { "ProductKey", "Product Name", "Category" };
        string dest = Path.Combine(workDir, "products.csv");
        long rows = 0;
        var (capped, pages) = (false, 0);
        string? url = FirstUrl(shop, "products");
        var categories = new SortedSet<string>(StringComparer.Ordinal);

        using (var sink = new CsvSink(dest, header))
            while (url != null && pages < MaxPages)
            {
                ct.ThrowIfCancellationRequested();
                pages++;
                var (root, next) = await GetPageAsync(url, token, ct).ConfigureAwait(false);
                if (root?["products"] is not JsonArray products || products.Count == 0) break;

                foreach (var p in products)
                {
                    if (p is not JsonObject o) continue;
                    string category = Str(o["product_type"]);
                    if (category.Length > 0) categories.Add(category);
                    sink.WriteRow(new[]
                    {
                        Long(o["id"]).ToString(),
                        Str(o["title"]),
                        category,
                    });
                    rows++;
                }

                if (products.Count < PageSize) break;
                url = next;
                if (pages == MaxPages && next != null) capped = true;
            }

        Register("products", dest, result, EcommerceCalendar.ProductsSchema());
        WriteDistinctDim(workDir, result, "category", "Category", categories, EcommerceCalendar.CategorySchema());
        result.Notes.Add($"products: ingested {rows:N0} product(s) across {pages} page(s).");
        if (capped)
            result.Notes.Add($"products: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all products were ingested.");
    }

    // ---- customers (base names) ----------------------------------------------------------------

    /// <summary>Pull customers, cursor-paginated, capturing each customer's key -&gt; (name, channel, created).
    /// The acquisition (first-order) date is filled in from the orders pass, falling back to the customer's
    /// created_at when they have no order in this pull.</summary>
    private async Task FetchCustomersBaseAsync(string shop, string? token, IngestResult result,
        Dictionary<long, (string name, string channel, DateTime? created)> names, CancellationToken ct)
    {
        var (capped, pages) = (false, 0);
        string? url = FirstUrl(shop, "customers");

        while (url != null && pages < MaxPages)
        {
            ct.ThrowIfCancellationRequested();
            pages++;
            var (root, next) = await GetPageAsync(url, token, ct).ConfigureAwait(false);
            if (root?["customers"] is not JsonArray customers || customers.Count == 0) break;

            foreach (var c in customers)
            {
                if (c is not JsonObject o) continue;
                long key = Long(o["id"]);
                string name = $"{Str(o["first_name"])} {Str(o["last_name"])}".Trim();
                if (name.Length == 0) name = Str(o["email"]);
                names[key] = (name, "Online Store", ParseDate(o["created_at"]));
            }

            if (customers.Count < PageSize) break;
            url = next;
            if (pages == MaxPages && next != null) capped = true;
        }

        if (capped)
            result.Notes.Add($"customers: page CAP reached ({MaxPages:N0} pages) - the pull is incomplete; not all customers were ingested.");
    }

    /// <summary>Write customers.csv, joining the descriptive fields with the first-order (acquisition) date
    /// derived from the orders pass (falling back to created_at). A guest order's customer (key 0) is written
    /// once so its orders still resolve a CustomerKey.</summary>
    private static void WriteCustomers(string workDir, IngestResult result,
        Dictionary<long, (string name, string channel, DateTime? created)> names, Dictionary<long, DateTime> firstOrder)
    {
        string[] header = { "CustomerKey", "Customer Name", "Channel", "Acquisition Date" };
        string dest = Path.Combine(workDir, "customers.csv");
        long rows = 0;

        var keys = new SortedSet<long>(names.Keys);
        foreach (var k in firstOrder.Keys) keys.Add(k);
        if (firstOrder.ContainsKey(0) || names.ContainsKey(0)) keys.Add(0);

        var channels = new SortedSet<string>(StringComparer.Ordinal);
        using (var sink = new CsvSink(dest, header))
            foreach (var key in keys)
            {
                names.TryGetValue(key, out var d);
                string name = d.name is { Length: > 0 } ? d.name : key == 0 ? "Guest" : $"Customer {key}";
                string channel = d.channel ?? "";
                if (channel.Length > 0) channels.Add(channel);
                DateTime? acq = firstOrder.TryGetValue(key, out var dt) ? dt : d.created;
                sink.WriteRow(new[]
                {
                    key.ToString(),
                    name,
                    channel,
                    acq.HasValue ? Iso(MonthEnd(acq.Value)) : "",
                });
                rows++;
            }

        Register("customers", dest, result, EcommerceCalendar.CustomersSchema());
        result.Notes.Add($"customers: ingested {rows:N0} customer(s).");

        WriteDistinctDim(workDir, result, "channel", "Channel", channels, EcommerceCalendar.ChannelSchema());
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

        Register("calendar", dest, result, EcommerceCalendar.CalendarSchema());
        result.Notes.Add($"calendar: generated {rows:N0} month-ending row(s).");
    }

    // ---- HTTP ----------------------------------------------------------------------------------

    /// <summary>GET an Admin REST page and return its parsed JSON root plus the cursor URL for the next page
    /// (parsed from the Link header's rel="next"), or null when there is no next page. The access token is
    /// applied to the request header only; it is never written to a Note or an exception message.</summary>
    private static async Task<(JsonNode? root, string? next)> GetPageAsync(string url, string? token, CancellationToken ct)
    {
        using var msg = new HttpRequestMessage(HttpMethod.Get, url);
        msg.Headers.Accept.ParseAdd("application/json");
        if (!string.IsNullOrWhiteSpace(token))
            msg.Headers.TryAddWithoutValidation(TokenHeader, token);

        using var resp = await _http.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"Shopify API returned HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");

        string? next = null;
        if (resp.Headers.TryGetValues("Link", out var links))
            next = NextFromLink(string.Join(",", links));

        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var capped = new CappedReadStream(stream, MaxResponseBytes);
        var root = await JsonNode.ParseAsync(capped, cancellationToken: ct).ConfigureAwait(false);
        return (root, next);
    }

    /// <summary>Extract the rel="next" URL from a Shopify Link header value (cursor pagination), or null.</summary>
    private static string? NextFromLink(string linkHeader)
    {
        foreach (var part in linkHeader.Split(','))
        {
            int lt = part.IndexOf('<'), gt = part.IndexOf('>');
            if (lt < 0 || gt <= lt) continue;
            string href = part.Substring(lt + 1, gt - lt - 1);
            if (part.Contains("rel=\"next\"", StringComparison.OrdinalIgnoreCase)
                || part.Contains("rel=next", StringComparison.OrdinalIgnoreCase))
                return href;
        }
        return null;
    }

    private static string FirstUrl(string shop, string resource)
        => $"https://{shop}/admin/api/{ApiVersion}/{resource}.json?limit={PageSize}";

    // ---- value helpers -------------------------------------------------------------------------

    /// <summary>Normalise a shop domain to its bare host (strips a scheme / path if one was supplied) and
    /// validates it is a *.myshopify.com host. Returns null when missing or not a valid shop domain.</summary>
    private static string? NormaliseShop(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim();
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            s = u.Host;
        s = s.TrimEnd('/');
        // basic host shape; the strict *.myshopify.com check keeps the call bound to a Shopify store
        return s.EndsWith(".myshopify.com", StringComparison.OrdinalIgnoreCase) && !s.Contains('/') ? s : null;
    }

    /// <summary>Sum the refunds on an order (each refund's transactions carry the refunded amounts); returned
    /// as a non-negative amount to match the schema's ">= 0" rule.</summary>
    private static decimal SumRefunds(JsonNode? refunds)
    {
        if (refunds is not JsonArray arr) return 0m;
        decimal sum = 0m;
        foreach (var r in arr)
        {
            if (r is not JsonObject ro) continue;
            if (ro["transactions"] is JsonArray txns)
                foreach (var t in txns)
                    if (t is JsonObject to) sum += Math.Abs(Dec(to["amount"]));
            else if (ro["refund_line_items"] is JsonArray rli)
                foreach (var l in rli)
                    if (l is JsonObject lo) sum += Math.Abs(Dec(lo["subtotal"]));
        }
        return sum;
    }

    private static DateTime MonthEnd(DateTime d)
        => new DateTime(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));

    /// <summary>The order's sale date: processed_at (the transaction date Shopify Analytics reports on) when
    /// present, else created_at, else today. created_at is the record's creation time - for a store migrated
    /// into Shopify that is the IMPORT date, so keying off it alone collapses the whole history onto the
    /// migration day and flattens every trend. Preferring processed_at keeps the true sales timeline.</summary>
    internal static DateTime OrderDate(JsonObject o)
        => ParseDate(o["processed_at"]) ?? ParseDate(o["created_at"]) ?? DateTime.UtcNow.Date;

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

    private static long Long(JsonNode? node)
        => long.TryParse(Str(node), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static decimal Dec(JsonNode? node)
        => decimal.TryParse(Str(node), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0m;

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
        s.Tables.Add(EcommerceCalendar.OrdersSchema());
        s.Tables.Add(EcommerceCalendar.OrderLinesSchema());
        s.Tables.Add(EcommerceCalendar.ProductsSchema());
        s.Tables.Add(EcommerceCalendar.CategorySchema());
        s.Tables.Add(EcommerceCalendar.CustomersSchema());
        s.Tables.Add(EcommerceCalendar.ChannelSchema());
        s.Tables.Add(EcommerceCalendar.CalendarSchema());
        return s;
    }
}
