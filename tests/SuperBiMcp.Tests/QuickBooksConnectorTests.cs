using System.Net;
using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline coverage of the REAL <see cref="QuickBooksConnector"/> orchestration, driven against an injected
/// <see cref="StubHttpHandler"/> so the connector's own query-API paging walk (STARTPOSITION / MAXRESULTS),
/// the Account -&gt; Customer -&gt; Invoice key wiring, the paid-invoice -&gt; bank-inflow derivation and the
/// finance-schema CSV mapping all run end to end with no network and no token. The emitted files must match
/// <c>solutions/finance/schema.json</c> exactly (the same shape the Xero connector targets).
/// </summary>
public sealed class QuickBooksConnectorTests
{
    private static ConnectorRequest Req(string realmId = "123", string env = "production")
        => new()
        {
            AccessToken = "intuit-bearer-token",
            Params = new JsonObject { ["realmId"] = realmId, ["environment"] = env },
        };

    private static IngestResult Fetch(ConnectorRequest req, StubHttpHandler handler)
    {
        using var _ = QuickBooksConnector.UseTransportForTests(handler);
        var work = Fixtures.NewWorkDir();
        return new QuickBooksConnector().FetchAsync(req, work.Path, CancellationToken.None)
                                        .GetAwaiter().GetResult();
    }

    /// <summary>The entity name in a /query?query=... request (Account / Customer / Invoice), read from the
    /// SELECT * FROM &lt;entity&gt; in the (URL-encoded) query string.</summary>
    private static string EntityOf(HttpRequestMessage req)
    {
        string url = Uri.UnescapeDataString(req.RequestUri!.ToString());
        foreach (var e in new[] { "Account", "Customer", "Invoice" })
            if (url.Contains("FROM " + e)) return e;
        return "";
    }

    private static int StartOf(HttpRequestMessage req)
    {
        string url = Uri.UnescapeDataString(req.RequestUri!.ToString());
        int i = url.IndexOf("STARTPOSITION ", StringComparison.Ordinal);
        if (i < 0) return 1;
        i += "STARTPOSITION ".Length;
        int j = i;
        while (j < url.Length && char.IsDigit(url[j])) j++;
        return int.Parse(url.Substring(i, j - i));
    }

    private static (HttpStatusCode, string?) Resp(string entity, JsonArray rows)
        => (HttpStatusCode.OK,
            new JsonObject { ["QueryResponse"] = new JsonObject { [entity] = rows } }.ToJsonString());

    // ---- the happy path: finance CSVs in the canonical shape -----------------------------------

    [Fact]
    public void Fetch_EmitsAllFinanceCsvs_InCanonicalShape()
    {
        var handler = new StubHttpHandler(req =>
        {
            string entity = EntityOf(req);
            return entity switch
            {
                "Account" => Resp("Account", new JsonArray
                {
                    new JsonObject { ["Id"] = "33", ["Name"] = "Sales of Product Income", ["AccountType"] = "Income" },
                }),
                "Customer" => Resp("Customer", new JsonArray
                {
                    new JsonObject { ["Id"] = "1", ["DisplayName"] = "Amy's Bird Sanctuary",
                        ["BillAddr"] = new JsonObject { ["City"] = "Bayshore" } },
                    new JsonObject { ["Id"] = "2", ["DisplayName"] = "Bill's Windsurf Shop" },
                }),
                "Invoice" => Resp("Invoice", new JsonArray
                {
                    // a fully-paid invoice (Balance 0) -> Status PAID, a bank inflow of TotalAmt.
                    new JsonObject
                    {
                        ["Id"] = "101", ["TxnDate"] = "2024-01-15", ["DueDate"] = "2024-02-15",
                        ["TotalAmt"] = 150.0, ["Balance"] = 0.0,
                        ["CustomerRef"] = new JsonObject { ["value"] = "1" },
                        ["Line"] = new JsonArray
                        {
                            new JsonObject { ["SalesItemLineDetail"] = new JsonObject
                                { ["ItemAccountRef"] = new JsonObject { ["value"] = "33" } } },
                        },
                    },
                    // a partly-paid invoice (Balance 40 of 100) -> Status AUTHORISED, inflow of 60.
                    new JsonObject
                    {
                        ["Id"] = "102", ["TxnDate"] = "2024-02-20", ["DueDate"] = "2024-03-20",
                        ["TotalAmt"] = 100.0, ["Balance"] = 40.0,
                        ["CustomerRef"] = new JsonObject { ["value"] = "2" },
                    },
                }),
                _ => (HttpStatusCode.OK, new JsonObject { ["QueryResponse"] = new JsonObject() }.ToJsonString()),
            };
        });

        var result = Fetch(Req(), handler);

        // every finance table is present and matches the schema header EXACTLY.
        foreach (var name in new[] { "invoices", "contacts", "accounts", "bank", "calendar" })
            Assert.True(result.Tables.ContainsKey(name), $"missing {name}: " + string.Join(" | ", result.Notes));

        Assert.Equal("InvoiceKey,ContactKey,AccountKey,InvoiceDate,DueDate,Type,Status,Total,CostAmount,AmountPaid,AmountDue,DaysOutstanding",
            File.ReadAllLines(result.Tables["invoices"])[0]);
        Assert.Equal("ContactKey,Contact Name,Contact Type,Region", File.ReadAllLines(result.Tables["contacts"])[0]);
        Assert.Equal("AccountKey,Account Name,Account Class", File.ReadAllLines(result.Tables["accounts"])[0]);
        Assert.Equal("BankTxnKey,TxnDate,Bank Account,Direction,NetMovement", File.ReadAllLines(result.Tables["bank"])[0]);
        Assert.Equal("MonthEnding,Period,Month Name,Year", File.ReadAllLines(result.Tables["calendar"])[0]);

        // invoices: two rows, the right statuses and AR balance, joined to the customer/account keys.
        string[] inv = File.ReadAllLines(result.Tables["invoices"]);
        Assert.Equal(3, inv.Length); // header + 2
        Assert.StartsWith("1,1,", inv[1]);                 // InvoiceKey 1 -> ContactKey 1 (customer "1")
        Assert.Contains("2024-01-31", inv[1]);             // InvoiceDate snapped to the month-end of Jan 2024
        Assert.Contains("ACCREC", inv[1]);
        Assert.Contains("PAID", inv[1]);
        Assert.Contains("150", inv[1]);                    // Total
        Assert.EndsWith(",0", inv[1].TrimEnd());           // DaysOutstanding 0 for a paid invoice
        Assert.Contains("AUTHORISED", inv[2]);             // the partly-paid invoice
        Assert.Contains(",40", inv[2]);                    // AmountDue 40

        // contacts: both customers, Contact Type fixed to "Customer", region from BillAddr.City.
        string[] con = File.ReadAllLines(result.Tables["contacts"]);
        Assert.Equal(3, con.Length);
        Assert.Equal("1,Amy's Bird Sanctuary,Customer,Bayshore", con[1]);
        Assert.Equal("2,Bill's Windsurf Shop,Customer,", con[2]);

        // accounts: the synthetic catch-all (200) plus the one real income account.
        string[] acc = File.ReadAllLines(result.Tables["accounts"]);
        Assert.Equal("200,Sales,Revenue", acc[1]);
        Assert.Contains(acc, l => l.Contains("Sales of Product Income"));

        // bank: a derived inflow for each paid portion (150 fully paid + 60 partly paid).
        string[] bank = File.ReadAllLines(result.Tables["bank"]);
        Assert.Equal(3, bank.Length); // header + 2 inflows
        Assert.Contains(bank, l => l.Contains("Inflow") && l.EndsWith(",150"));
        Assert.Contains(bank, l => l.Contains("Inflow") && l.EndsWith(",60"));

        // the invoice's first line referenced account "33" -> the real account key (not the catch-all 200).
        int accountKeyOf33 = int.Parse(acc.First(l => l.Contains("Sales of Product Income")).Split(',')[0]);
        Assert.Contains(accountKeyOf33.ToString(), inv[1].Split(',')[2]);
    }

    // ---- paging: STARTPOSITION / MAXRESULTS walk -----------------------------------------------

    [Fact]
    public void Fetch_PagesInvoices_UntilAShortPage()
    {
        // 1500 invoices across two pages (page size 1000): page 1 is full, page 2 is short (500) -> stop.
        var handler = new StubHttpHandler(req =>
        {
            string entity = EntityOf(req);
            if (entity is "Account" or "Customer")
                return Resp(entity, new JsonArray()); // no accounts/customers needed for this paging assertion

            int start = StartOf(req);
            int count = start == 1 ? 1000 : 500; // page 1 full, page 2 short
            var rows = new JsonArray();
            for (int i = 0; i < count; i++)
                rows.Add(new JsonObject
                {
                    ["Id"] = (start + i).ToString(),
                    ["TxnDate"] = "2024-05-31",
                    ["TotalAmt"] = 10.0,
                    ["Balance"] = 10.0, // unpaid -> no bank inflow, keeps the assertion about invoice count clean
                });
            return Resp("Invoice", rows);
        });

        var result = Fetch(Req(), handler);

        string[] inv = File.ReadAllLines(result.Tables["invoices"]);
        Assert.Equal(1501, inv.Length); // header + 1500 invoices
        Assert.Contains(result.Notes, n => n.Contains("invoices: ingested 1,500 invoice(s) across 2 page"));

        // the connector issued STARTPOSITION 1 then 1001 for the two invoice pages.
        var invoiceStarts = handler.Requests
            .Where(u => Uri.UnescapeDataString(u).Contains("FROM Invoice"))
            .Select(u => { var r = new HttpRequestMessage(HttpMethod.Get, u); return StartOf(r); })
            .ToList();
        Assert.Contains(1, invoiceStarts);
        Assert.Contains(1001, invoiceStarts);
        Assert.DoesNotContain(2001, invoiceStarts); // stopped on the short page, did not fetch a third page
    }

    // ---- environment selection + empty company -------------------------------------------------

    [Fact]
    public void Fetch_Sandbox_UsesSandboxHost()
    {
        var handler = new StubHttpHandler(req => Resp(EntityOf(req), new JsonArray()));
        _ = Fetch(Req(env: "sandbox"), handler);
        Assert.All(handler.Requests, u => Assert.StartsWith("https://sandbox-quickbooks.api.intuit.com/", u));
    }

    [Fact]
    public void Fetch_Production_UsesProductionHost()
    {
        var handler = new StubHttpHandler(req => Resp(EntityOf(req), new JsonArray()));
        _ = Fetch(Req(env: "production"), handler);
        Assert.All(handler.Requests, u => Assert.StartsWith("https://quickbooks.api.intuit.com/", u));
    }

    [Fact]
    public void Fetch_MissingRealm_RecordsGuidanceNote()
    {
        var handler = new StubHttpHandler(_ => (HttpStatusCode.OK, "{}"));
        var result = Fetch(new ConnectorRequest { AccessToken = "t", Params = new JsonObject() }, handler);
        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("realmId"));
        Assert.Empty(handler.Requests); // no call made without a realm
    }

    [Fact]
    public void Fetch_NeverLeaksTokenOrRealm_InNotes()
    {
        var handler = new StubHttpHandler(req => Resp(EntityOf(req), new JsonArray()));
        var result = Fetch(Req(realmId: "SECRET-REALM-999"), handler);
        Assert.All(result.Notes, n =>
        {
            Assert.DoesNotContain("intuit-bearer-token", n);
            Assert.DoesNotContain("SECRET-REALM-999", n);
        });
    }

    // ---- page cap: fires on a full final page, NOT on a natural short end -----------------------

    /// <summary>A stub that returns FULL pages of <paramref name="full"/> invoices for the first
    /// <paramref name="fullPages"/> pages, then a final page of <paramref name="lastCount"/> invoices (which may
    /// itself be full or short). Accounts / Customers are empty so the assertion is purely about the invoice
    /// paging walk and its cap detection.</summary>
    private static StubHttpHandler InvoicePager(int fullPages, int full, int lastCount)
        => new(req =>
        {
            string entity = EntityOf(req);
            if (entity is "Account" or "Customer")
                return Resp(entity, new JsonArray());

            int start = StartOf(req);
            int page = (start - 1) / full + 1; // 1-based page from STARTPOSITION
            int count = page <= fullPages ? full : lastCount;
            var rows = new JsonArray();
            for (int i = 0; i < count; i++)
                rows.Add(new JsonObject
                {
                    ["Id"] = (start + i).ToString(),
                    ["TxnDate"] = "2024-05-31",
                    ["TotalAmt"] = 10.0,
                    ["Balance"] = 10.0, // unpaid -> no bank inflow, keeps the count clean
                });
            return Resp("Invoice", rows);
        });

    [Fact]
    public void Fetch_PageCap_FiresWhenCapIsHit_WithAFullFinalPage()
    {
        // cap lowered to 2 pages. Every page is FULL (1000), so the walk runs page 1 then page 2 (the cap) and
        // page 2 is full -> there may be more rows the cap stopped us reaching -> the cap Note must fire.
        using var _ = QuickBooksConnector.UsePageCapForTests(2);
        var handler = InvoicePager(fullPages: 2, full: 1000, lastCount: 1000);

        var result = Fetch(Req(), handler);

        Assert.Contains(result.Notes, n => n.Contains("invoices: page CAP reached") && n.Contains("2"));
        // exactly the cap'th page was the last fetched - it did NOT page past the cap.
        var invoiceStarts = handler.Requests
            .Where(u => Uri.UnescapeDataString(u).Contains("FROM Invoice"))
            .Select(u => StartOf(new HttpRequestMessage(HttpMethod.Get, u)))
            .ToList();
        Assert.Contains(1, invoiceStarts);
        Assert.Contains(1001, invoiceStarts);
        Assert.DoesNotContain(2001, invoiceStarts); // stopped at the 2-page cap
    }

    [Fact]
    public void Fetch_PageCap_DoesNotFire_WhenTheCapthPageIsANaturalShortEnd()
    {
        // cap lowered to 2 pages. Page 1 is full (1000); page 2 lands exactly on the cap BUT is short (500), so
        // it is a natural exhaustion - all rows were read - and the cap Note must NOT fire (the old code's
        // false-positive set capped=true just because the last row was on the cap'th page).
        using var _ = QuickBooksConnector.UsePageCapForTests(2);
        var handler = InvoicePager(fullPages: 1, full: 1000, lastCount: 500);

        var result = Fetch(Req(), handler);

        Assert.DoesNotContain(result.Notes, n => n.Contains("page CAP reached"));
        Assert.Contains(result.Notes, n => n.Contains("invoices: ingested 1,500 invoice(s) across 2 page"));
    }
}
