using System.Text.Json.Nodes;

namespace SuperBiMcp.Integrations;

/// <summary>
/// A "bring your data" connector - the thin front door for the DaxOps integrations. A connector is NOT a
/// new subsystem: its entire job is to produce a folder of CSVs (plus an inferred schema) in the exact
/// canonical shape the existing report pipeline consumes (the model.spec.json format that
/// <see cref="Headless.GenerateProject"/> reads, where each table's M does
/// <c>Csv.Document(File.Contents(DataFolder &amp; "/&lt;file&gt;.csv"), ...)</c> - see
/// <c>solutions/retail-fmcg/model.spec.json</c>). From the returned CSVs the pipeline scaffolds and bakes a
/// .pbix exactly as it does for an uploaded one, so everything downstream (build, AI prompt, download) is
/// unchanged.
///
/// Two operations:
///   1. <see cref="DiscoverAsync"/> - inspect the source and report the tables/columns it would emit,
///      cheaply, without a full pull (powers the dataset picker and a pre-flight schema preview).
///   2. <see cref="FetchAsync"/> - pull the data into <c>workDir</c> as CSVs and return the table-&gt;csv map
///      plus the inferred schema (and any truncation notes).
///
/// Data residency: every fetch runs on the engine host, the data is staged into <c>workDir</c>, baked, then
/// the folder is wiped (the data is deleted, never shared with third parties). Any access token on the
/// request is short-lived, used per job, never persisted and never logged.
/// </summary>
public interface IDataSourceConnector
{
    /// <summary>Stable connector id used to resolve it from the registry, e.g. "files", "rest",
    /// "google-sheets", "xero", "shopify", "woo".</summary>
    string Id { get; }

    /// <summary>Customer-facing label for the connector (never names the internal engine).</summary>
    string DisplayName { get; }

    /// <summary>Inspect the source and report the tables/columns this connector would emit, without a full
    /// data pull. Used for the dataset picker and a pre-flight schema preview. Implementations should read
    /// only headers and a small sample.</summary>
    Task<SchemaDiscovery> DiscoverAsync(ConnectorRequest req, CancellationToken ct);

    /// <summary>Fetch the source data into <paramref name="workDir"/> as CSVs in the canonical shape and
    /// return the logical-table-&gt;csv-path map, the inferred schema (used to synthesise a model spec when
    /// there is no Solution), and any truncation / pagination notes. The connector must write ONLY into
    /// <paramref name="workDir"/>; the caller wipes it after the bake.</summary>
    Task<IngestResult> FetchAsync(ConnectorRequest req, string workDir, CancellationToken ct);
}

/// <summary>
/// The per-job request handed to a connector. Carries the source parameters, an optional short-lived access
/// token (passed per job from the WordPress OAuth broker - never persisted, never logged), and an optional
/// Solution id when the data maps onto a known Solution schema.
/// </summary>
public sealed class ConnectorRequest
{
    /// <summary>Short-lived access token for the provider (OAuth bearer, Woo key, etc.). Used per job only;
    /// never written to the job store and never logged.</summary>
    public string? AccessToken { get; init; }

    /// <summary>Free-form connector parameters: sheetId/range, xeroTenantId, shopDomain, url, jsonPath,
    /// staged file paths, and so on. Each connector documents the keys it reads.</summary>
    public JsonObject Params { get; init; } = new();

    /// <summary>When the data maps to a known Solution schema, its id (e.g. "finance"); otherwise null and
    /// the pipeline AI-auto-shapes from <see cref="IngestResult.Schema"/>.</summary>
    public string? SolutionId { get; init; }

    /// <summary>Convenience reader for a parameter as text, or null if absent/blank. Tolerates ANY JSON scalar -
    /// a number, bool or string all read back as their text form. This matters: a browser
    /// &lt;input type="number"&gt; (the SQL connector's port) serialises as 5433, NOT "5433", and an explicit
    /// (string?) cast on a non-string JsonValue throws "An element of type 'Number' cannot be converted to a
    /// 'System.String'" - which failed the whole ingest instead of simply reading the port.</summary>
    public string? Param(string key)
    {
        JsonNode? n = Params.TryGetPropertyValue(key, out var v) ? v : null;
        if (n is null) return null;
        string? s = n is JsonValue jv && jv.TryGetValue<string>(out var str) ? str : n.ToString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}

/// <summary>
/// The result of a fetch: where each logical table's CSV landed in <c>workDir</c>, the inferred schema (for
/// the no-Solution AI-auto-shape path), and human-readable notes about any cap or truncation that was
/// applied (no silent truncation - every cap is surfaced and later feeds the verify moat).
/// </summary>
public sealed class IngestResult
{
    /// <summary>Logical table name -&gt; CSV path inside <c>workDir</c>.</summary>
    public Dictionary<string, string> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The inferred schema (tables + columns + types), used to synthesise a flat-table model spec
    /// when no Solution is supplied.</summary>
    public SchemaDiscovery Schema { get; } = new();

    /// <summary>Truncation / pagination / cap notes. Never silently drop rows: record the cap here.</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>A discovered/inferred schema: the set of tables a connector would emit and their columns.</summary>
public sealed class SchemaDiscovery
{
    public List<TableSchema> Tables { get; } = new();

    /// <summary>Discovery-time notes - a validation failure or a partial-preview caveat is surfaced here so a
    /// caller never sees a silently empty schema with no explanation (mirrors <see cref="IngestResult.Notes"/>).</summary>
    public List<string> Notes { get; } = new();
}

/// <summary>One logical table in a discovered schema (becomes one CSV and one model.spec table).</summary>
public sealed class TableSchema
{
    public TableSchema(string name) => Name = name;

    /// <summary>Logical table name (also the CSV file stem, e.g. "sales" -&gt; sales.csv).</summary>
    public string Name { get; }

    public List<ColumnSchema> Columns { get; } = new();
}

/// <summary>One column in a discovered schema.</summary>
public sealed class ColumnSchema
{
    public ColumnSchema(string name, string dataType)
    {
        Name = name;
        DataType = dataType;
    }

    /// <summary>Column name as it appears in the CSV header.</summary>
    public string Name { get; }

    /// <summary>One of the spec data-type tokens <see cref="Headless"/> understands:
    /// int64 | double | decimal | date | boolean | string.</summary>
    public string DataType { get; set; }
}
