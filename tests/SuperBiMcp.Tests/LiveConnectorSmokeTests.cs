using System.Text.Json.Nodes;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// LIVE one-round-trip-per-connector smoke tests. Each test is SKIPPED (not failed) unless its credentials
/// and source ids are supplied via environment variables, so <c>dotnet test</c> is green with ZERO
/// credentials. When the env vars ARE set, the test resolves the real connector from the default registry,
/// runs a real <see cref="IDataSourceConnector.FetchAsync"/> against the live source, and asserts that at
/// least one table's CSV landed on disk with a data row.
///
/// Every env var is documented in README.md. See <see cref="Env"/> for the exact names.
/// </summary>
public sealed class LiveConnectorSmokeTests
{
    /// <summary>Run a connector's FetchAsync and assert a CSV with a header + at least one data row landed.</summary>
    private static void AssertRoundTrip(string connectorId, ConnectorRequest req)
    {
        var connector = ConnectorRegistry.Default.Resolve(connectorId);
        Assert.NotNull(connector); // a registered connector for this id must exist

        using var work = Fixtures.NewWorkDir();
        IngestResult result = connector!.FetchAsync(req, work.Path, CancellationToken.None)
                                        .GetAwaiter().GetResult();

        // surface notes in the failure message so a live failure is diagnosable without leaking the token.
        string notes = string.Join(" | ", result.Notes);
        Assert.True(result.Tables.Count > 0, $"no tables landed. notes: {notes}");

        bool anyRow = false;
        foreach (var (table, path) in result.Tables)
        {
            Assert.True(File.Exists(path), $"table '{table}' has no CSV at {path}");
            // a non-empty CSV has a header line plus at least one data line for a real round-trip.
            var lines = File.ReadAllLines(path);
            if (lines.Length >= 2) anyRow = true;
        }
        Assert.True(anyRow, $"every landed CSV was header-only. notes: {notes}");
    }

    private static ConnectorRequest Req(string? token, params (string key, string? val)[] @params)
    {
        var p = new JsonObject();
        foreach (var (k, v) in @params)
            if (!string.IsNullOrWhiteSpace(v)) p[k] = v;
        return new ConnectorRequest { AccessToken = token, Params = p };
    }

    // ---- Microsoft Graph: Excel Online --------------------------------------------------------

    [SkippableFact]
    public void ExcelOnline_LiveRoundTrip()
    {
        string? token = Env.Get(Env.GraphToken);
        Skip.If(token is null, $"set {Env.GraphToken} (+ {Env.ExcelDriveId}/{Env.ExcelItemId}) to run the Excel Online live smoke.");
        string? driveId = Env.Require(Env.ExcelDriveId);
        string? itemId = Env.Require(Env.ExcelItemId);

        AssertRoundTrip("excel-online", Req(token,
            ("driveId", driveId), ("itemId", itemId),
            ("worksheet", Env.Get(Env.ExcelWorksheet)), ("table", Env.Get(Env.ExcelTable))));
    }

    // ---- Microsoft Graph: SharePoint document (download + stage) -------------------------------

    [SkippableFact]
    public void SharePoint_LiveRoundTrip()
    {
        string? token = Env.Get(Env.GraphToken);
        Skip.If(token is null, $"set {Env.GraphToken} (+ {Env.SpDriveId}/{Env.SpItemId}) to run the SharePoint live smoke.");
        AssertRoundTrip("sharepoint", Req(token,
            ("driveId", Env.Require(Env.SpDriveId)), ("itemId", Env.Require(Env.SpItemId))));
    }

    // ---- Microsoft Graph: Microsoft Lists ------------------------------------------------------

    [SkippableFact]
    public void MicrosoftLists_LiveRoundTrip()
    {
        string? token = Env.Get(Env.GraphToken);
        Skip.If(token is null, $"set {Env.GraphToken} (+ {Env.ListsSiteId}/{Env.ListsListId}) to run the Lists live smoke.");
        AssertRoundTrip("microsoft-lists", Req(token,
            ("siteId", Env.Require(Env.ListsSiteId)), ("listId", Env.Require(Env.ListsListId))));
    }

    // ---- Google Sheets -------------------------------------------------------------------------

    [SkippableFact]
    public void GoogleSheets_LiveRoundTrip()
    {
        string? token = Env.Get(Env.GoogleToken);
        Skip.If(token is null, $"set {Env.GoogleToken} (+ {Env.SheetsSpreadsheetId}) to run the Google Sheets live smoke.");
        AssertRoundTrip("google-sheets", Req(token,
            ("spreadsheetId", Env.Require(Env.SheetsSpreadsheetId)), ("range", Env.Get(Env.SheetsRange))));
    }

    // ---- Xero ----------------------------------------------------------------------------------

    [SkippableFact]
    public void Xero_LiveRoundTrip()
    {
        string? token = Env.Get(Env.XeroToken);
        Skip.If(token is null, $"set {Env.XeroToken} (+ {Env.XeroTenantId}) to run the Xero live smoke.");
        AssertRoundTrip("xero", Req(token,
            ("xeroTenantId", Env.Require(Env.XeroTenantId)), ("fromDate", Env.Get(Env.XeroFromDate))));
    }

    // ---- QuickBooks ----------------------------------------------------------------------------

    [SkippableFact]
    public void QuickBooks_LiveRoundTrip()
    {
        string? token = Env.Get(Env.QboToken);
        Skip.If(token is null, $"set {Env.QboToken} (+ {Env.QboRealmId}) to run the QuickBooks live smoke.");
        AssertRoundTrip("quickbooks", Req(token,
            ("realmId", Env.Require(Env.QboRealmId)),
            ("environment", Env.Get(Env.QboEnvironment) ?? "production")));
    }

    // ---- SQL database --------------------------------------------------------------------------

    [SkippableFact]
    public void Sql_LiveRoundTrip()
    {
        // the DB password is the credential and rides the AccessToken (X-Connector-Token) - never a param.
        string? password = Env.Get(Env.SqlPassword);
        string? dialect = Env.Get(Env.SqlDialect);
        string? host = Env.Get(Env.SqlHost);
        Skip.If(password is null || dialect is null || host is null,
            $"set {Env.SqlDialect}/{Env.SqlHost}/{Env.SqlDatabase}/{Env.SqlUser}/{Env.SqlPassword} (+ {Env.SqlQuery} or {Env.SqlTable}) to run the SQL live smoke.");
        AssertRoundTrip("sql", Req(password,
            ("dialect", dialect), ("host", host), ("port", Env.Get(Env.SqlPort)),
            ("database", Env.Require(Env.SqlDatabase)), ("user", Env.Require(Env.SqlUser)),
            ("query", Env.Get(Env.SqlQuery)), ("table", Env.Get(Env.SqlTable))));
    }

    // ---- Shopify -------------------------------------------------------------------------------

    [SkippableFact]
    public void Shopify_LiveRoundTrip()
    {
        string? token = Env.Get(Env.ShopifyToken);
        Skip.If(token is null, $"set {Env.ShopifyToken} (+ {Env.ShopifyShopDomain}) to run the Shopify live smoke.");
        AssertRoundTrip("shopify", Req(token, ("shopDomain", Env.Require(Env.ShopifyShopDomain))));
    }

    // ---- WooCommerce ---------------------------------------------------------------------------

    [SkippableFact]
    public void Woo_LiveRoundTrip()
    {
        string? store = Env.Get(Env.WooStoreUrl);
        string? key = Env.Get(Env.WooConsumerKey);
        string? secret = Env.Get(Env.WooConsumerSecret);
        Skip.If(store is null || key is null || secret is null,
            $"set {Env.WooStoreUrl}/{Env.WooConsumerKey}/{Env.WooConsumerSecret} to run the WooCommerce live smoke.");
        AssertRoundTrip("woo", Req(null,
            ("storeUrl", store), ("consumerKey", key), ("consumerSecret", secret)));
    }
}

/// <summary>
/// The environment-variable contract for the live connector smoke tests. Every name is documented in
/// README.md. <see cref="Get"/> returns null for an unset / blank var; <see cref="Require"/> reads one that
/// the caller has already gated a Skip on.
/// </summary>
internal static class Env
{
    public const string GraphToken = "DAXTEST_GRAPH_TOKEN";
    public const string ExcelDriveId = "DAXTEST_EXCEL_DRIVE_ID";
    public const string ExcelItemId = "DAXTEST_EXCEL_ITEM_ID";
    public const string ExcelWorksheet = "DAXTEST_EXCEL_WORKSHEET";
    public const string ExcelTable = "DAXTEST_EXCEL_TABLE";

    public const string SpDriveId = "DAXTEST_SP_DRIVE_ID";
    public const string SpItemId = "DAXTEST_SP_ITEM_ID";

    public const string ListsSiteId = "DAXTEST_LISTS_SITE_ID";
    public const string ListsListId = "DAXTEST_LISTS_LIST_ID";

    public const string GoogleToken = "DAXTEST_GOOGLE_TOKEN";
    public const string SheetsSpreadsheetId = "DAXTEST_SHEETS_SPREADSHEET_ID";
    public const string SheetsRange = "DAXTEST_SHEETS_RANGE";

    public const string XeroToken = "DAXTEST_XERO_TOKEN";
    public const string XeroTenantId = "DAXTEST_XERO_TENANT_ID";
    public const string XeroFromDate = "DAXTEST_XERO_FROM_DATE";

    public const string QboToken = "DAXTEST_QBO_TOKEN";
    public const string QboRealmId = "DAXTEST_QBO_REALM_ID";
    public const string QboEnvironment = "DAXTEST_QBO_ENVIRONMENT";

    public const string SqlDialect = "DAXTEST_SQL_DIALECT";
    public const string SqlHost = "DAXTEST_SQL_HOST";
    public const string SqlPort = "DAXTEST_SQL_PORT";
    public const string SqlDatabase = "DAXTEST_SQL_DATABASE";
    public const string SqlUser = "DAXTEST_SQL_USER";
    public const string SqlPassword = "DAXTEST_SQL_PASSWORD";
    public const string SqlQuery = "DAXTEST_SQL_QUERY";
    public const string SqlTable = "DAXTEST_SQL_TABLE";

    public const string ShopifyToken = "DAXTEST_SHOPIFY_TOKEN";
    public const string ShopifyShopDomain = "DAXTEST_SHOPIFY_SHOP_DOMAIN";

    public const string WooStoreUrl = "DAXTEST_WOO_STORE_URL";
    public const string WooConsumerKey = "DAXTEST_WOO_CONSUMER_KEY";
    public const string WooConsumerSecret = "DAXTEST_WOO_CONSUMER_SECRET";

    public static string? Get(string name)
    {
        string? v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public static string Require(string name)
        => Get(name) ?? throw new Xunit.Sdk.XunitException(
            $"live smoke gated on but missing required env var {name}.");
}
