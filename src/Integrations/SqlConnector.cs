using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace SuperBiMcp.Integrations;

/// <summary>
/// The SQL database connector (id "sql") - point it at a customer's relational database and a query (or a
/// table) becomes one CSV in the canonical shape the report pipeline consumes, so everything downstream
/// (scaffold, bake, AI prompt, download) is unchanged. The result set is an arbitrary tabular shape (not a
/// fixed Solution), so the inferred schema feeds <see cref="FlatSpecBuilder"/> for the AI-auto-shape path.
///
/// Built entirely on the ADO.NET abstractions (<see cref="DbConnection"/> / <see cref="DbCommand"/> /
/// <see cref="DbDataReader"/>); the concrete driver is chosen by the dialect -> provider-factory map:
///   postgres  -&gt; Npgsql
///   mysql     -&gt; MySqlConnector
///   sqlserver -&gt; Microsoft.Data.SqlClient
/// (the test suite injects a fourth factory - SQLite - through the same path to drive the real row -&gt; CSV
/// mapping offline.)
///
/// Security and safety:
///   - The DB PASSWORD is the credential and arrives ONLY on <see cref="ConnectorRequest.AccessToken"/>
///     (it rides the X-Connector-Token header into the worker); it is never read from Params, never written
///     to a Note, and never logged. The full connection string is never logged either.
///   - The customer-supplied host runs through <see cref="HostGuard"/> (SSRF): it must resolve to a public,
///     internet-reachable address - loopback / private / link-local / unique-local / cloud-metadata are all
///     rejected. The cloud worker targets an internet-reachable DB; the desktop opts the guard out locally.
///   - The connection string is built ONLY through the dialect's ConnectionStringBuilder from the validated
///     components, so a hostile host / user string can never inject extra connection parameters.
///   - Read-only safety. The customer owns the SQL text, so the engine cannot parse-and-block a write; instead
///     it asks the DATABASE to refuse writes for the session where the dialect supports it:
///       postgres -&gt; BEGIN; SET TRANSACTION READ ONLY  (a write raises "cannot execute ... in a read-only transaction")
///       mysql    -&gt; START TRANSACTION READ ONLY         (a write raises ER_CANT_EXECUTE_IN_READ_ONLY_TRANSACTION)
///     SQL Server has no read-only transaction flag, so it is NOT claimed as enforced read-only there: the
///     statement runs with the supplied credential's privileges - use a read-only DB user. A CommandTimeout
///     (statement timeout) bounds the query and a hard row cap bounds the output (the cap is surfaced as a
///     Note - never a silent truncation).
///
/// Params it reads:
///   dialect  : "postgres" | "mysql" | "sqlserver" (required).
///   host     : the database host (required; SSRF-guarded).
///   port     : the database port (optional; the dialect default is used when omitted).
///   database : the database / catalogue name (required).
///   user     : the database user (required).
///   query    : an optional SQL statement; when present it is run verbatim (the customer owns it). On postgres
///              and mysql it runs inside a DB-level READ ONLY transaction so a write is rejected by the
///              database; SQL Server has no such flag, so there the statement runs with the supplied
///              credential's privileges - use a read-only DB user.
///   table    : an optional table name; when no query is given, "SELECT * FROM &lt;validated table&gt;" is run.
/// </summary>
public sealed class SqlConnector : IDataSourceConnector
{
    public string Id => "sql";
    public string DisplayName => "SQL Database";

    // statement timeout (seconds) so a runaway query cannot pin the worker.
    private const int CommandTimeoutSeconds = 60;

    // hard row cap so a huge result cannot exhaust the box. Every time it bites it is surfaced in Notes -
    // no silent truncation. The field is mutable only so the test suite can lower it to exercise the cap
    // branch against a tiny in-memory table; production never writes it.
    private const int DefaultMaxRows = 1_000_000;
    private static int _maxRows = DefaultMaxRows;

    /// <summary>Test-only seam: temporarily lower the hard row cap so the cap branch can be exercised against a
    /// tiny in-memory table. Returns an <see cref="IDisposable"/> that restores the production cap. Reached
    /// only by the test assembly (InternalsVisibleTo); never used in production.</summary>
    internal static IDisposable UseRowCapForTests(int cap)
    {
        int previous = _maxRows;
        _maxRows = cap;
        return new Remover(() => _maxRows = previous);
    }

    // connect timeout (seconds) so an unreachable host fails fast.
    private const int ConnectTimeoutSeconds = 15;

    /// <summary>A dialect's ADO.NET adapter: its default port, a builder that produces a safe connection string
    /// from validated components (the host / user / database can never inject extra parameters because each
    /// goes through the driver's own ConnectionStringBuilder), the open factory, and an optional READ ONLY
    /// preamble. The preamble is the DB-level statement that asks the database to refuse writes for the session
    /// (postgres / mysql); a dialect with no such flag (sqlserver) declares null and is NOT claimed read-only.</summary>
    private sealed record Dialect(
        int DefaultPort,
        Func<SqlTarget, string, string> BuildConnectionString,
        Func<string, DbConnection> OpenConnection,
        string? ReadOnlyPreamble,
        string TableListQuery);

    // the table-listing query used by DiscoverAsync. information_schema is ANSI and present on all three
    // shipped engines; it is held per-dialect so the SQLite test seam (which has no information_schema) can
    // supply its own sqlite_master variant and still drive the REAL DiscoverAsync path offline.
    private const string AnsiTableListQuery =
        "SELECT table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' ORDER BY table_name";

    /// <summary>The validated connection target (never carries the password - that is passed separately so it
    /// is never captured in a log or a Note). Internal (not public) so the test seam can build one; it never
    /// leaves the assembly.</summary>
    internal readonly record struct SqlTarget(string Host, int Port, string Database, string User);

    // dialect -> adapter. The desktop / cloud builds use these three; the test suite swaps in a SQLite
    // adapter via UseDialectForTests so the REAL reader -> CSV path is driven offline.
    // The READ ONLY preamble per dialect. postgres begins a transaction then sets it read only; mysql starts a
    // read-only transaction in one statement; SQL Server has no read-only transaction flag so it declares null
    // (it is NOT claimed enforced read-only - the doc says "use a read-only DB user").
    private const string PostgresReadOnlyPreamble = "BEGIN; SET TRANSACTION READ ONLY;";
    private const string MySqlReadOnlyPreamble = "START TRANSACTION READ ONLY;";

    private static readonly Dictionary<string, Dialect> _dialects =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["postgres"] = new Dialect(5432, BuildPostgres, cs => new NpgsqlConnection(cs), PostgresReadOnlyPreamble, AnsiTableListQuery),
            ["mysql"] = new Dialect(3306, BuildMySql, cs => new MySqlConnection(cs), MySqlReadOnlyPreamble, AnsiTableListQuery),
            ["sqlserver"] = new Dialect(1433, BuildSqlServer, cs => new SqlConnection(cs), ReadOnlyPreamble: null, AnsiTableListQuery),
        };

    /// <summary>Test-only seam: register an extra dialect (the suite uses SQLite) so the connector's real
    /// component validation, reader -&gt; CSV streaming and row-cap branch run offline against an in-memory
    /// database through the same <see cref="DbConnection"/> path. Returns an <see cref="IDisposable"/> that
    /// removes it again. Reached only by the test assembly (InternalsVisibleTo); never used in production.</summary>
    internal static IDisposable UseDialectForTests(
        string name, Func<SqlTarget, string, string> buildConnectionString, Func<string, DbConnection> open,
        string? readOnlyPreamble = null, string? tableListQuery = null)
    {
        var dialect = new Dialect(
            0, buildConnectionString, open, readOnlyPreamble, tableListQuery ?? AnsiTableListQuery);
        _dialects[name] = dialect;
        return new Remover(() => _dialects.Remove(name));
    }

    /// <summary>Test-only access to a dialect's READ ONLY preamble (so the test can assert postgres / mysql each
    /// issue their DB-level read-only statement and SQL Server issues none). Not used in production.</summary>
    internal static string? ReadOnlyPreambleForTests(string dialect)
        => _dialects.TryGetValue(dialect, out var d) ? d.ReadOnlyPreamble : null;

    /// <summary>Test-only access to a dialect's connection-string builder (so the injection guard can be
    /// asserted directly): build the string from explicit components + password and return it. Not used in
    /// production.</summary>
    internal static string BuildConnectionStringForTests(
        string dialect, string host, int port, string database, string user, string password)
        => _dialects[dialect].BuildConnectionString(new SqlTarget(host, port, database, user), password);

    private sealed class Remover : IDisposable
    {
        private readonly Action _remove;
        public Remover(Action remove) => _remove = remove;
        public void Dispose() => _remove();
    }

    public async Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct)
    {
        var schema = new SchemaDiscovery();
        var (dialect, target, error) = ValidateTarget(req);
        if (dialect == null || error != null)
        {
            // surface the validation failure the way FetchAsync does, so a caller never gets a silently empty
            // schema with no explanation of why nothing was discovered.
            schema.Notes.Add(error ?? "The SQL source is not configured correctly.");
            return schema;
        }

        HostGuard.EnsurePublicHost(target.Host, ct);
        string connectionString = dialect.BuildConnectionString(target, req.AccessToken ?? "");

        await using var conn = dialect.OpenConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        await ApplyReadOnlyPreambleAsync(conn, dialect, ct).ConfigureAwait(false);

        // list the user tables for the dataset picker (the ANSI information_schema query on the shipped engines;
        // the SQLite test seam supplies its own sqlite_master variant through the same path).
        await using var cmd = conn.CreateCommand();
        cmd.CommandTimeout = CommandTimeoutSeconds;
        cmd.CommandText = dialect.TableListQuery;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string name = reader.IsDBNull(0) ? "" : reader.GetValue(0)?.ToString() ?? "";
            if (name.Length > 0) schema.Tables.Add(new TableSchema(name));
        }
        return schema;
    }

    public async Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct)
    {
        Directory.CreateDirectory(workDir);
        var result = new IngestResult();

        var (dialect, target, error) = ValidateTarget(req);
        if (dialect == null || error != null)
        {
            result.Notes.Add(error ?? "The SQL source is not configured correctly.");
            return result;
        }

        // SSRF guard: the host must resolve only to public addresses (the desktop opts out for a local DB).
        // TOCTOU note: the guard resolves the host, then the driver below resolves it again to connect, so a
        // hostile resolver could in theory return a public address to the guard and a private one to the
        // driver. We deliberately do NOT pin the guard-validated IP for the connection here: pinning the IP
        // while still presenting the original hostname for TLS SNI / certificate validation behaves differently
        // across Npgsql, MySqlConnector and SqlClient (SqlClient validates the server cert against the
        // DataSource), and getting it wrong silently breaks TLS - too high a price for a low-likelihood finding.
        // Instead the gap is mitigated by defence-in-depth: both the cloud file-staging guard and this engine
        // resolve-and-block, and the cloud worker only runs against internet-reachable databases.
        HostGuard.EnsurePublicHost(target.Host, ct);

        // the statement: the customer's query verbatim, else SELECT * over a validated table identifier.
        string? rawQuery = req.Param("query");
        string? rawTable = req.Param("table");
        string sql;
        string tableName;
        if (!string.IsNullOrWhiteSpace(rawQuery))
        {
            sql = rawQuery;
            tableName = SafeTableName(rawTable ?? "query");
        }
        else if (!string.IsNullOrWhiteSpace(rawTable))
        {
            sql = "SELECT * FROM " + QuoteIdentifier(req.Param("dialect")!, rawTable);
            tableName = SafeTableName(rawTable);
        }
        else
        {
            result.Notes.Add("Provide a query or a table to read from the database.");
            return result;
        }

        // build the connection string ONLY via the driver's builder from validated parts + the password
        // (which arrives on AccessToken and is never logged or echoed into a Note).
        string connectionString = dialect.BuildConnectionString(target, req.AccessToken ?? "");

        await using var conn = dialect.OpenConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        // read-only enforcement: on postgres / mysql ask the DATABASE to refuse writes for this session (the
        // customer's verbatim SQL then runs inside that READ ONLY transaction, so a write is rejected by the
        // engine). SQL Server has no such flag and issues no preamble; it runs with the credential's privileges.
        await ApplyReadOnlyPreambleAsync(conn, dialect, ct).ConfigureAwait(false);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeoutSeconds; // statement timeout - read-only safety
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleResult, ct).ConfigureAwait(false);

        await WriteReaderAsync(reader, tableName, workDir, result, ct).ConfigureAwait(false);

        if (result.Tables.Count == 0)
            result.Notes.Add("The query returned no columns; nothing was ingested.");
        return result;
    }

    /// <summary>Issue the dialect's READ ONLY preamble on the open connection so the customer's verbatim SQL
    /// runs in a session the DATABASE will refuse writes for (postgres: BEGIN; SET TRANSACTION READ ONLY; mysql:
    /// START TRANSACTION READ ONLY). The preamble is dialect-gated - a dialect that declares none (SQL Server,
    /// which has no read-only transaction flag) is a no-op, so the SQLite test path is unaffected unless the
    /// test deliberately registers a preamble. The statement timeout bounds it so a wedged session fails fast.</summary>
    private static async Task ApplyReadOnlyPreambleAsync(DbConnection conn, Dialect dialect, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(dialect.ReadOnlyPreamble)) return;
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = dialect.ReadOnlyPreamble;
        cmd.CommandTimeout = CommandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stream a <see cref="DbDataReader"/> result set to <c>workDir/&lt;table&gt;.csv</c> through the
    /// shared <see cref="CsvSink"/>, inferring each column's spec type from a sample while writing, and
    /// enforcing the hard row cap (surfaced as a Note - never a silent truncation). The real engine and the
    /// test SQLite path both run through here, so the reader -&gt; CSV mapping is proven by the offline test.</summary>
    private static async Task WriteReaderAsync(
        DbDataReader reader, string table, string workDir, IngestResult result, CancellationToken ct)
    {
        int columns = reader.FieldCount;
        if (columns == 0) return;

        var header = new string[columns];
        for (int i = 0; i < columns; i++)
        {
            string name = reader.GetName(i);
            header[i] = string.IsNullOrWhiteSpace(name) ? $"Column{i + 1}" : name;
        }

        const int SampleRows = 200;
        var samples = new List<string?>[columns];
        for (int i = 0; i < columns; i++) samples[i] = new List<string?>();

        string dest = Path.Combine(workDir, table + ".csv");
        long rows = 0;
        bool capHit = false;
        var buffer = new string?[columns];

        using (var sink = new CsvSink(dest, header))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (rows >= _maxRows) { capHit = true; break; }
                for (int i = 0; i < columns; i++)
                {
                    string? v = reader.IsDBNull(i) ? null : FormatValue(reader.GetValue(i));
                    buffer[i] = v;
                    if (rows < SampleRows) samples[i].Add(v);
                }
                sink.WriteRow(buffer);
                rows++;
            }
        }

        var ts = new TableSchema(table);
        for (int i = 0; i < columns; i++)
            ts.Columns.Add(new ColumnSchema(header[i], ColumnTypeInference.Infer(samples[i])));
        result.Schema.Tables.Add(ts);
        result.Tables[table] = dest;
        result.Notes.Add($"sql: ingested {rows:N0} row(s) into '{table}'.");
        if (capHit)
            result.Notes.Add($"sql: row CAP reached ({_maxRows:N0} rows) - the pull is incomplete; not all rows were ingested.");
    }

    // ---- validation ----------------------------------------------------------------------------

    /// <summary>Validate the dialect + connection components from Params. Returns the resolved dialect adapter
    /// and a populated <see cref="SqlTarget"/>, or an error message (never echoing the password) when a
    /// required part is missing or out of range.</summary>
    private static (Dialect? dialect, SqlTarget target, string? error) ValidateTarget(ConnectorRequest req)
    {
        string? dialectName = req.Param("dialect");
        if (string.IsNullOrWhiteSpace(dialectName) || !_dialects.TryGetValue(dialectName, out var dialect))
            return (null, default, "Choose a database dialect: postgres, mysql or sqlserver.");

        string? host = req.Param("host");
        if (string.IsNullOrWhiteSpace(host))
            return (null, default, "A database host is required.");

        string? database = req.Param("database");
        if (string.IsNullOrWhiteSpace(database))
            return (null, default, "A database name is required.");

        string? user = req.Param("user");
        if (string.IsNullOrWhiteSpace(user))
            return (null, default, "A database user is required.");

        int port = dialect.DefaultPort;
        if (req.Param("port") is { Length: > 0 } portText)
        {
            if (!int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out port)
                || port is < 1 or > 65535)
                return (null, default, "The database port must be a number between 1 and 65535.");
        }

        return (dialect, new SqlTarget(host.Trim(), port, database.Trim(), user.Trim()), null);
    }

    // ---- connection-string builders (one per dialect; NEVER string-concatenate the host/user) ---

    private static string BuildPostgres(SqlTarget t, string password)
        => new NpgsqlConnectionStringBuilder
        {
            Host = t.Host,
            Port = t.Port,
            Database = t.Database,
            Username = t.User,
            Password = password,
            Timeout = ConnectTimeoutSeconds,
            CommandTimeout = CommandTimeoutSeconds,
            Pooling = false,
        }.ConnectionString;

    private static string BuildMySql(SqlTarget t, string password)
        => new MySqlConnectionStringBuilder
        {
            Server = t.Host,
            Port = (uint)t.Port,
            Database = t.Database,
            UserID = t.User,
            Password = password,
            ConnectionTimeout = ConnectTimeoutSeconds,
            DefaultCommandTimeout = CommandTimeoutSeconds,
            Pooling = false,
        }.ConnectionString;

    private static string BuildSqlServer(SqlTarget t, string password)
        => new SqlConnectionStringBuilder
        {
            DataSource = t.Host + "," + t.Port.ToString(CultureInfo.InvariantCulture),
            InitialCatalog = t.Database,
            UserID = t.User,
            Password = password,
            ConnectTimeout = ConnectTimeoutSeconds,
            // The connection stays ENCRYPTED; only the certificate CHAIN check is relaxed. Microsoft.Data.SqlClient
            // encrypts by default and validates the chain, and the vast majority of SQL Servers present a
            // self-signed certificate - so every such customer failed with "The certificate chain was issued by an
            // authority that is not trusted" and could never connect at all. Encrypt stays true (this is NOT
            // plaintext); we only stop requiring a publicly-trusted chain a customer's own server cannot be expected
            // to have. TODO: surface a per-connection "my server uses a self-signed certificate" opt-in so a customer
            // WITH a trusted chain keeps full MITM protection instead of this being on for everyone.
            Encrypt = true,
            TrustServerCertificate = true,
            Pooling = false,
        }.ConnectionString;

    // ---- helpers -------------------------------------------------------------------------------

    /// <summary>Quote a table identifier for the dialect when building "SELECT * FROM &lt;table&gt;". A
    /// schema-qualified name (schema.table) has each part quoted. The double-quote / backtick is doubled so a
    /// hostile identifier cannot break out of the quoting.</summary>
    private static string QuoteIdentifier(string dialect, string identifier)
    {
        var parts = identifier.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) parts = new[] { identifier };

        bool backtick = dialect.Equals("mysql", StringComparison.OrdinalIgnoreCase);
        var quoted = new List<string>(parts.Length);
        foreach (var part in parts)
            quoted.Add(backtick
                ? "`" + part.Replace("`", "``") + "`"
                : "\"" + part.Replace("\"", "\"\"") + "\"");
        return string.Join('.', quoted);
    }

    /// <summary>Render a DB value as CSV text: numbers / dates in invariant, deterministic forms (so the
    /// generated CSV is culture-stable and re-typeable by the M layer), everything else as its string.</summary>
    private static string FormatValue(object value) => value switch
    {
        null or DBNull => "",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero
            ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        DateTimeOffset dto => dto.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        byte[] bytes => Convert.ToBase64String(bytes),
        decimal dec => dec.ToString(CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable fmt => fmt.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>A logical table / CSV stem safe for an M file reference: keep letters, digits, spaces,
    /// underscores and hyphens; collapse the rest to underscores. Matches the other connectors.</summary>
    private static string SafeTableName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "query";
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is ' ' or '_' or '-' ? c : '_');
        string s = sb.ToString().Trim();
        return s.Length == 0 ? "query" : s;
    }
}
