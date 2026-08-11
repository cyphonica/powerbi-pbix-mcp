namespace SuperBiMcp.Integrations;

/// <summary>
/// Resolves a <see cref="IDataSourceConnector"/> by its <see cref="IDataSourceConnector.Id"/>. Mirrors the
/// SolutionLibrary / TemplateLibrary catalog pattern: a small, cached, read-mostly registry. An ingest
/// caller resolves the requested connector here, then calls FetchAsync.
///
/// Connectors register themselves in <see cref="Default"/>: the generic-raw-data
/// <see cref="FilesConnector"/>, <see cref="GoogleSheetsConnector"/>, <see cref="XeroConnector"/>,
/// <see cref="WooConnector"/> and <see cref="ShopifyConnector"/>, among others.
/// </summary>
public sealed class ConnectorRegistry
{
    private readonly Dictionary<string, IDataSourceConnector> _byId =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Register a connector, replacing any existing one with the same id.</summary>
    public ConnectorRegistry Add(IDataSourceConnector connector)
    {
        _byId[connector.Id] = connector;
        return this;
    }

    /// <summary>Resolve a connector by id, or null if none is registered under that id.</summary>
    public IDataSourceConnector? Resolve(string? id)
        => id != null && _byId.TryGetValue(id, out var c) ? c : null;

    /// <summary>All registered connectors (for a "list available connectors" surface).</summary>
    public IEnumerable<IDataSourceConnector> All => _byId.Values;

    /// <summary>The public projection (id + display name) safe to serve to a front end.</summary>
    public IEnumerable<object> Catalog => _byId.Values
        .OrderBy(c => c.Id, StringComparer.Ordinal)
        .Select(c => new { id = c.Id, name = c.DisplayName });

    private static readonly Lazy<ConnectorRegistry> _default = new(() => new ConnectorRegistry()
        .Add(new FilesConnector())
        .Add(new GoogleSheetsConnector())
        .Add(new OneDriveConnector())
        .Add(new SharePointConnector())
        .Add(new ExcelOnlineConnector())
        .Add(new MicrosoftListsConnector())
        .Add(new DropboxConnector())
        .Add(new GoogleDriveConnector())
        .Add(new XeroConnector())
        .Add(new QuickBooksConnector())
        .Add(new SqlConnector())
        .Add(new WooConnector())
        .Add(new ShopifyConnector()));

    /// <summary>The process-wide default registry with every shipped connector registered.</summary>
    public static ConnectorRegistry Default => _default.Value;
}
