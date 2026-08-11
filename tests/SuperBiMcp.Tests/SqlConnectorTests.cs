using System.Data.Common;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SuperBiMcp.Integrations;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline coverage of the REAL <see cref="SqlConnector"/> orchestration. The connector is dialect-agnostic
/// (it speaks only the ADO.NET <see cref="DbConnection"/> / <see cref="DbDataReader"/> abstractions), so the
/// tests register a SQLite dialect through the same provider-factory seam the three shipped drivers use and
/// drive a REAL query against an in-memory database: the reader -&gt; CSV streaming, the per-column type
/// inference, the row-cap Note and the "SELECT * FROM &lt;table&gt;" path are all the system under test - none
/// of it is re-implemented. The connection-string injection guard is asserted directly against the shipped
/// driver builders.
/// </summary>
public sealed class SqlConnectorTests : IDisposable
{
    // a keep-alive connection holds the shared in-memory database open for the duration of a test (a SQLite
    // ":memory:" database vanishes when its last connection closes; "cache=shared" lets the connector open
    // its own connection onto the same database).
    private readonly SqliteConnection _keepAlive;
    private readonly string _connectionString;
    private readonly string _dialectName;

    public SqlConnectorTests()
    {
        // each test instance gets its own uniquely-named shared in-memory DB so xUnit parallelism is safe.
        _dialectName = "sqlite-test-" + Guid.NewGuid().ToString("N");
        _connectionString = $"Data Source=file:{_dialectName}?mode=memory&cache=shared";
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
    }

    public void Dispose() => _keepAlive.Dispose();

    /// <summary>Register the SQLite dialect on the connector: the connection-string builder ignores the
    /// (validated) host/user and returns this test's shared in-memory connection string, and the open factory
    /// returns a <see cref="SqliteConnection"/>. The connector's real component validation + reader streaming
    /// still run; only the transport is local.</summary>
    // SQLite has no information_schema; its user tables live in sqlite_master. The connector reads the table
    // list through the dialect's TableListQuery, so the seam supplies this SQLite variant to drive the REAL
    // DiscoverAsync path offline.
    private const string SqliteTableListQuery =
        "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";

    private IDisposable UseSqlite()
        => SqlConnector.UseDialectForTests(
            _dialectName,
            (_, _) => _connectionString,
            cs => new SqliteConnection(cs),
            tableListQuery: SqliteTableListQuery);

    /// <summary>Register the SQLite dialect with a READ ONLY preamble that emulates a DB-level read-only
    /// session via <c>PRAGMA query_only = 1</c> (SQLite's connection-scoped read-only switch). The connector
    /// issues this preamble on its own connection before the customer query, so a write in that query is
    /// rejected by SQLite exactly as a write would be rejected by a postgres / mysql READ ONLY transaction.</summary>
    private IDisposable UseReadOnlySqlite()
        => SqlConnector.UseDialectForTests(
            _dialectName,
            (_, _) => _connectionString,
            cs => new SqliteConnection(cs),
            readOnlyPreamble: "PRAGMA query_only = 1;",
            tableListQuery: SqliteTableListQuery);

    private SchemaDiscovery Discover(ConnectorRequest req)
    {
        bool prev = HostGuard.AllowPrivate;
        HostGuard.AllowPrivate = true; // the in-memory test never makes a network call; skip DNS resolution
        try
        {
            return new SqlConnector().DiscoverAsync(req, CancellationToken.None).GetAwaiter().GetResult();
        }
        finally { HostGuard.AllowPrivate = prev; }
    }

    private void Exec(string sql)
    {
        using var cmd = _keepAlive.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static ConnectorRequest Req(string dialect, string? query = null, string? table = null)
    {
        var p = new JsonObject
        {
            ["dialect"] = dialect,
            ["host"] = "db.example.com",   // a public-looking host; the SSRF guard is opted out below
            ["database"] = "appdb",
            ["user"] = "reader",
        };
        if (query != null) p["query"] = query;
        if (table != null) p["table"] = table;
        return new ConnectorRequest { AccessToken = "the-password", Params = p };
    }

    private IngestResult Fetch(ConnectorRequest req)
    {
        var work = Fixtures.NewWorkDir();
        bool prev = HostGuard.AllowPrivate;
        HostGuard.AllowPrivate = true; // the in-memory test never makes a network call; skip DNS resolution
        try
        {
            return new SqlConnector().FetchAsync(req, work.Path, CancellationToken.None)
                                     .GetAwaiter().GetResult();
        }
        finally { HostGuard.AllowPrivate = prev; }
    }

    // ---- reader -> CSV mapping ------------------------------------------------------------------

    [Fact]
    public void Query_MapsRowsAndTypes_ToCanonicalCsv()
    {
        Exec("CREATE TABLE sales (OrderId INTEGER, Amount REAL, OrderDate TEXT, Note TEXT)");
        Exec("INSERT INTO sales VALUES (1, 19.5, '2024-01-31', 'hello')");
        Exec("INSERT INTO sales VALUES (2, 4.0, '2024-02-29', 'a,comma')");   // a comma forces RFC-4180 quoting
        Exec("INSERT INTO sales VALUES (3, 7.25, '2024-03-31', NULL)");        // NULL -> empty cell

        using var _ = UseSqlite();
        var result = Fetch(Req(_dialectName, query: "SELECT OrderId, Amount, OrderDate, Note FROM sales ORDER BY OrderId", table: "sales"));

        Assert.True(result.Tables.ContainsKey("sales"), string.Join(" | ", result.Notes));
        string[] lines = File.ReadAllLines(result.Tables["sales"]);
        Assert.Equal("OrderId,Amount,OrderDate,Note", lines[0]);
        Assert.Equal("1,19.5,2024-01-31,hello", lines[1]);
        Assert.Equal("2,4,2024-02-29,\"a,comma\"", lines[2]);   // comma cell quoted; REAL 4.0 -> "4"
        Assert.Equal("3,7.25,2024-03-31,", lines[3]);            // trailing NULL -> empty field
        Assert.Equal(4, lines.Length);

        // per-column inferred spec types off the real values
        var t = result.Schema.Tables.Single(x => x.Name == "sales");
        Assert.Equal("int64", t.Columns.Single(c => c.Name == "OrderId").DataType);
        Assert.Equal("double", t.Columns.Single(c => c.Name == "Amount").DataType);
        Assert.Equal("date", t.Columns.Single(c => c.Name == "OrderDate").DataType);
        Assert.Equal("string", t.Columns.Single(c => c.Name == "Note").DataType);
        Assert.Contains(result.Notes, n => n.Contains("ingested 3 row"));
    }

    [Fact]
    public void Table_NoQuery_RunsSelectStar_OverQuotedIdentifier()
    {
        Exec("CREATE TABLE customers (id INTEGER, name TEXT)");
        Exec("INSERT INTO customers VALUES (10, 'Acme')");
        Exec("INSERT INTO customers VALUES (11, 'Globex')");

        using var _ = UseSqlite();
        var result = Fetch(Req(_dialectName, table: "customers")); // no query -> SELECT * FROM "customers"

        Assert.True(result.Tables.ContainsKey("customers"), string.Join(" | ", result.Notes));
        string[] lines = File.ReadAllLines(result.Tables["customers"]);
        Assert.Equal("id,name", lines[0]);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.Contains("10,Acme", lines);
        Assert.Contains("11,Globex", lines);
    }

    [Fact]
    public void RowCap_StopsAtCap_AndRecordsCapNote_NoSilentTruncation()
    {
        Exec("CREATE TABLE big (n INTEGER)");
        for (int i = 1; i <= 10; i++) Exec($"INSERT INTO big VALUES ({i})");

        using var _ = UseSqlite();
        using var __ = SqlConnector.UseRowCapForTests(4); // lower the cap so the branch bites on a tiny table
        var result = Fetch(Req(_dialectName, query: "SELECT n FROM big ORDER BY n"));

        string[] lines = File.ReadAllLines(result.Tables.Values.Single());
        Assert.Equal(5, lines.Length); // header + exactly 4 capped rows (no silent extra rows)
        Assert.Contains(result.Notes, n => n.Contains("row CAP reached") && n.Contains("4"));
        Assert.Contains(result.Notes, n => n.Contains("ingested 4 row"));
    }

    // ---- validation -----------------------------------------------------------------------------

    [Fact]
    public void MissingDialect_RecordsGuidanceNote()
    {
        var p = new JsonObject { ["host"] = "db.example.com", ["database"] = "d", ["user"] = "u" };
        var result = Fetch(new ConnectorRequest { AccessToken = "pw", Params = p });
        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("dialect"));
    }

    [Fact]
    public void NoQueryNoTable_RecordsGuidanceNote()
    {
        using var _ = UseSqlite();
        var result = Fetch(Req(_dialectName)); // valid components, but nothing to read
        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("query or a table"));
    }

    [Fact]
    public void BadPort_RecordsGuidanceNote()
    {
        var p = new JsonObject
        {
            ["dialect"] = "postgres",
            ["host"] = "db.example.com",
            ["database"] = "d",
            ["user"] = "u",
            ["port"] = "70000", // out of range
        };
        var result = Fetch(new ConnectorRequest { AccessToken = "pw", Params = p });
        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("port"));
    }

    // ---- SSRF guard is enforced by the connector (not opted out) -------------------------------

    [Fact]
    public void Fetch_RejectsLoopbackHost_ViaSsrfGuard()
    {
        // here the guard is NOT opted out, so a loopback host must be rejected before any connection is opened.
        using var _ = UseSqlite();
        var p = new JsonObject
        {
            ["dialect"] = _dialectName,
            ["host"] = "127.0.0.1",
            ["database"] = "appdb",
            ["user"] = "reader",
            ["table"] = "sales",
        };
        var req = new ConnectorRequest { AccessToken = "pw", Params = p };
        var work = Fixtures.NewWorkDir();
        bool prev = HostGuard.AllowPrivate;
        HostGuard.AllowPrivate = false;
        try
        {
            Assert.Throws<InvalidOperationException>(() =>
                new SqlConnector().FetchAsync(req, work.Path, CancellationToken.None).GetAwaiter().GetResult());
        }
        finally { HostGuard.AllowPrivate = prev; }
    }

    // ---- connection-string injection guard (shipped driver builders) ---------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mysql")]
    [InlineData("sqlserver")]
    public void ConnectionStringBuilder_NeutralisesHostInjection(string dialect)
    {
        // a hostile host string that tries to smuggle extra connection parameters must NOT become extra
        // key/value pairs in the built connection string - the driver's builder escapes/quotes the whole value
        // so it stays a single Host/Server token. We assert the injected keywords did not appear as parameters.
        const string hostile = "evil.example.com;Trusted_Connection=true;Encrypt=false";
        string cs = SqlConnector.BuildConnectionStringForTests(dialect, hostile, 5432, "db", "user", "s3cr3t");

        // round-trip the built string through a neutral DbConnectionStringBuilder and inspect the PARSED
        // key/value pairs: the smuggled "Trusted_Connection" / "Encrypt=false" must NOT have become standalone
        // parameters - the whole hostile string must come back as the single Host/Server/Data-Source value.
        var rt = new DbConnectionStringBuilder { ConnectionString = cs };

        // the injected keyword never appears as its own parsed key (that would be a successful injection).
        Assert.False(rt.ContainsKey("Trusted_Connection"),
            "host injection leaked a Trusted_Connection parameter: " + cs);
        // sqlserver legitimately sets Encrypt=true; the injection tried Encrypt=false - assert it did not win.
        if (rt.TryGetValue("Encrypt", out var enc))
            Assert.False(string.Equals(enc?.ToString(), "false", StringComparison.OrdinalIgnoreCase),
                "host injection flipped Encrypt to false");

        string hostValue = dialect switch
        {
            "postgres" => (string)rt["Host"],
            "mysql" => (string)rt["Server"],
            _ => (string)rt["Data Source"],
        };
        // the entire hostile string survived AS the host value (one opaque token), proving it was quoted, not
        // parsed into extra parameters.
        Assert.Contains("Trusted_Connection=true", hostValue);
        Assert.Contains("Encrypt=false", hostValue);
    }

    // ---- DiscoverAsync: lists tables + surfaces the validation error as a Note --------------------

    [Fact]
    public void Discover_ListsUserTables()
    {
        Exec("CREATE TABLE orders (id INTEGER, total REAL)");
        Exec("CREATE TABLE products (sku TEXT, name TEXT)");

        using var _ = UseSqlite();
        var schema = Discover(Req(_dialectName));

        var names = schema.Tables.Select(t => t.Name).ToList();
        Assert.Contains("orders", names);
        Assert.Contains("products", names);
        Assert.Empty(schema.Notes); // a clean discovery records no error Note
    }

    [Fact]
    public void Discover_InvalidTarget_ReturnsEmptySchema_AndSurfacesTheErrorAsANote()
    {
        // no dialect chosen -> validation fails. Discovery must NOT silently return an empty schema with no
        // explanation: it surfaces the same guidance FetchAsync would, as a Note.
        var p = new JsonObject { ["host"] = "db.example.com", ["database"] = "d", ["user"] = "u" };
        var schema = Discover(new ConnectorRequest { AccessToken = "pw", Params = p });

        Assert.Empty(schema.Tables);
        Assert.NotEmpty(schema.Notes);
        Assert.Contains(schema.Notes, n => n.Contains("dialect"));
    }

    // ---- zero rows / zero columns ---------------------------------------------------------------

    [Fact]
    public void EmptyTable_WritesHeaderOnlyCsv_AndRecordsZeroRowNote()
    {
        Exec("CREATE TABLE empties (id INTEGER, label TEXT)");
        // no rows inserted

        using var _ = UseSqlite();
        var result = Fetch(Req(_dialectName, table: "empties"));

        Assert.True(result.Tables.ContainsKey("empties"), string.Join(" | ", result.Notes));
        string[] lines = File.ReadAllLines(result.Tables["empties"]);
        Assert.Single(lines);                 // header only - no data rows
        Assert.Equal("id,label", lines[0]);
        Assert.Contains(result.Notes, n => n.Contains("ingested 0 row"));
    }

    [Fact]
    public void ZeroColumnStatement_RecordsNoColumnsNote_AndIngestsNothing()
    {
        // a statement that yields a reader with no columns (a setter PRAGMA returns no result set) exercises the
        // "no columns" branch: nothing is written and the guidance Note is recorded.
        using var _ = UseSqlite();
        var result = Fetch(Req(_dialectName, query: "PRAGMA cache_size = 2000"));

        Assert.Empty(result.Tables);
        Assert.Contains(result.Notes, n => n.Contains("no columns"));
    }

    // ---- read-only enforcement ------------------------------------------------------------------

    [Fact]
    public void ReadOnlyPreamble_RejectsAWrite_ButStillAllowsAread()
    {
        Exec("CREATE TABLE ledger (n INTEGER)");
        Exec("INSERT INTO ledger VALUES (1)");

        using var _ = UseReadOnlySqlite(); // the connector issues PRAGMA query_only = 1 before the customer SQL

        // a read works under the read-only session
        var read = Fetch(Req(_dialectName, query: "SELECT n FROM ledger"));
        Assert.True(read.Tables.ContainsKey("query"), string.Join(" | ", read.Notes));
        Assert.Contains(read.Notes, n => n.Contains("ingested 1 row"));

        // a write in the customer's verbatim SQL is rejected by the DATABASE (the read-only session), not by the
        // engine parsing it - exactly the postgres / mysql READ ONLY transaction behaviour, emulated in SQLite.
        var ex = Record.Exception(() => Fetch(Req(_dialectName, query: "INSERT INTO ledger VALUES (2)")));
        Assert.NotNull(ex);

        // the write never landed: the table still holds exactly the one seeded row.
        var after = Fetch(Req(_dialectName, query: "SELECT COUNT(*) AS c FROM ledger"));
        Assert.Equal("1", File.ReadAllLines(after.Tables["query"])[1]);
    }

    [Theory]
    [InlineData("postgres", "READ ONLY")]
    [InlineData("mysql", "READ ONLY")]
    public void ReadOnlyPreamble_IsIssuedFor_PostgresAndMysql(string dialect, string expected)
    {
        // the shipped postgres / mysql dialects each carry a DB-level READ ONLY preamble so a write is rejected
        // by the database, not merely claimed read-only in the docs.
        string? preamble = SqlConnector.ReadOnlyPreambleForTests(dialect);
        Assert.False(string.IsNullOrEmpty(preamble), $"{dialect} must carry a read-only preamble");
        Assert.Contains(expected, preamble);
    }

    [Fact]
    public void ReadOnlyPreamble_IsNotIssuedFor_SqlServer()
    {
        // SQL Server has no read-only transaction flag, so it must NOT claim enforced read-only: it issues no
        // preamble (the doc directs the operator to use a read-only DB user instead).
        Assert.True(string.IsNullOrEmpty(SqlConnector.ReadOnlyPreambleForTests("sqlserver")));
    }
}
