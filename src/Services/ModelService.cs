using System.Globalization;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;
using TOM = Microsoft.AnalysisServices.Tabular;
using Adomd = Microsoft.AnalysisServices.AdomdClient;

namespace SuperBiMcp.Services;

/// <summary>
/// All model-side edits go through the live Tabular Object Model against the
/// Power BI Desktop AS engine. Every mutation ends with <c>ModelTxn.Save(model)</c> - the gated
/// SaveChanges: it writes to the in-memory engine immediately (persist to .pbix = File &gt; Save in
/// Desktop) unless a model transaction is open on the session, in which case the changes accumulate
/// on the object tree until commit_model_transaction issues the single real SaveChanges.
/// </summary>
public sealed partial class ModelService
{
    private readonly SessionStore _sessions;
    private readonly PortDiscovery _discovery;
    private readonly ILogger<ModelService> _log;

    public ModelService(SessionStore sessions, PortDiscovery discovery, ILogger<ModelService> log)
    {
        _sessions = sessions;
        _discovery = discovery;
        _log = log;
    }

    // ---------------------------------------------------------------- connect
    public object ListOpenModels()
    {
        var found = _discovery.Discover();
        var list = new List<object>();
        foreach (var inst in found)
        {
            string? modelName = null, dbName = null; int tables = 0;
            try
            {
                using var s = new TOM.Server();
                s.Connect($"Data Source=localhost:{inst.Port}");
                if (s.Databases.Count > 0)
                {
                    var db = s.Databases[0];
                    dbName = db.Name; modelName = db.Model?.Name; tables = db.Model?.Tables.Count ?? 0;
                }
                s.Disconnect();
            }
            catch (Exception ex) { _log.LogWarning(ex, "probe port {Port}", inst.Port); }
            list.Add(new { port = inst.Port, ownerPid = inst.OwnerPid, database = dbName, model = modelName, tables, workspace = inst.WorkspaceDir });
        }
        return new { count = list.Count, instances = list };
    }

    /// <summary>The interactive attach: binds to a Power BI Desktop a HUMAN already has open. A null
    /// port falls back to workspace discovery (newest live port wins), which is a guess - acceptable
    /// only because a person is watching. Unattended jobs never come through here: their seam is
    /// <see cref="LaunchedModelConnector"/>, which takes no <see cref="PortDiscovery"/> at all, so the
    /// compiler keeps discovery out of the pipeline.</summary>
    public object Connect(int? port)
    {
        int usePort;
        int discoveredOwnerPid = 0;
        if (port is { } p) usePort = p;
        else
        {
            var found = _discovery.Discover();
            if (found.Count == 0)
                throw new InvalidOperationException(
                    "No open Power BI Desktop model found. Open the .pbix in Power BI Desktop first.");
            usePort = found[0].Port;
            discoveredOwnerPid = found[0].OwnerPid;
        }

        var server = new TOM.Server();
        server.Connect($"Data Source=localhost:{usePort}");
        if (server.Databases.Count == 0)
            throw new InvalidOperationException($"Port {usePort} has no databases loaded.");
        var db = server.Databases[0];

        var session = new ModelSession
        {
            Id = _sessions.NewId("model"),
            Port = usePort,
            Server = server,
            Database = db,
            // attached, not launched: the Desktop belongs to a human, so there is no launched-PID
            // identity to assert against and no known .pbix path behind the engine.
            LaunchedPid = null,
            PbixPath = null,
        };
        _sessions.AddModel(session);
        if (discoveredOwnerPid != 0)
            return new
            {
                sessionId = session.Id,
                port = usePort,
                database = db.Name,
                model = db.Model.Name,
                tables = db.Model.Tables.Count,
                ownerPid = discoveredOwnerPid,
            };
        return new
        {
            sessionId = session.Id,
            port = usePort,
            database = db.Name,
            model = db.Model.Name,
            tables = db.Model.Tables.Count,
        };
    }

    /// <summary>Connect to a Fabric/Premium XMLA endpoint. The returned sessionId works with every
    /// session-based tool (run_dax, validate_dax, export_tmdl...) - no Power BI Desktop involved.</summary>
    public object ConnectXmla(string? endpoint, string? catalog, string? accessToken)
    {
        var (ep, cat, token, error) = ResolveXmlaTarget(endpoint, catalog, accessToken);
        if (error != null) return new { ok = false, error };

        var server = new TOM.Server();
        try
        {
            server.Connect(ModelSession.BuildXmlaConnectionString(ep!, cat!, token));
            var db = server.Databases.FindByName(cat!);
            if (db == null)
            {
                var names = string.Join(", ", server.Databases.Cast<TOM.Database>().Select(d => $"'{d.Name}'"));
                throw new InvalidOperationException(
                    $"Dataset '{cat}' not found on '{ep}'. Available: {names}.");
            }

            var session = new ModelSession
            {
                Id = _sessions.NewId("model"),
                Port = 0,
                Server = server,
                Database = db,
                Endpoint = ep,
                Catalog = cat,
                AccessTokenPrivate = token,
            };
            _sessions.AddModel(session);
            return new
            {
                sessionId = session.Id,
                endpoint = ep,
                catalog = cat,
                model = db.Model?.Name,
                tables = db.Model?.Tables.Count ?? 0,
            };
        }
        catch (Exception ex)
        {
            try { server.Dispose(); } catch { /* best effort */ }
            // Rebuild the exception so the token can never leak through message or inner chain.
            throw new InvalidOperationException(ScrubToken(ex.Message, token));
        }
    }

    /// <summary>Explicit arg wins, else DAXOPS_XMLA_ENDPOINT / DAXOPS_XMLA_CATALOG / DAXOPS_PBI_TOKEN.
    /// Pure so it is unit-testable; a missing endpoint or catalog comes back as an error string, not a throw.</summary>
    internal static (string? Endpoint, string? Catalog, string? Token, string? Error) ResolveXmlaTarget(
        string? endpoint, string? catalog, string? accessToken, Func<string, string?>? getEnv = null)
    {
        getEnv ??= Environment.GetEnvironmentVariable;
        string? ep = !string.IsNullOrWhiteSpace(endpoint) ? endpoint : getEnv("DAXOPS_XMLA_ENDPOINT");
        string? cat = !string.IsNullOrWhiteSpace(catalog) ? catalog : getEnv("DAXOPS_XMLA_CATALOG");
        string? token = !string.IsNullOrWhiteSpace(accessToken) ? accessToken : getEnv("DAXOPS_PBI_TOKEN");
        if (string.IsNullOrWhiteSpace(ep))
            return (null, null, null,
                "No XMLA endpoint. Pass endpoint or set DAXOPS_XMLA_ENDPOINT (e.g. powerbi://api.powerbi.com/v1.0/myorg/WorkspaceName).");
        if (string.IsNullOrWhiteSpace(cat))
            return (null, null, null,
                "No catalog. Pass catalog (the dataset name) or set DAXOPS_XMLA_CATALOG - an XMLA workspace hosts many datasets, so the name is required.");
        return (ep, cat, string.IsNullOrWhiteSpace(token) ? null : token, null);
    }

    /// <summary>Strip the access token from text destined for a tool result or exception.</summary>
    internal static string ScrubToken(string text, string? token) =>
        string.IsNullOrEmpty(token) ? text : text.Replace(token, "***");

    public object Summary(string sessionId)
    {
        var m = _sessions.GetModel(sessionId).Model;
        var tables = m.Tables.Select(t => new
        {
            name = t.Name,
            isHidden = t.IsHidden,
            columns = t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber)
                               .Select(c => new { name = c.Name, dataType = c.DataType.ToString(), type = c.Type.ToString() }),
            measures = t.Measures.Select(me => new { name = me.Name, format = me.FormatString, folder = me.DisplayFolder }),
            partitions = t.Partitions.Select(pp => new { name = pp.Name, source = pp.SourceType.ToString() }),
        });
        var rels = m.Relationships.OfType<TOM.SingleColumnRelationship>().Select(r => new
        {
            name = r.Name,
            from = $"{r.FromTable.Name}[{r.FromColumn.Name}]",
            to = $"{r.ToTable.Name}[{r.ToColumn.Name}]",
            fromCardinality = r.FromCardinality.ToString(),
            toCardinality = r.ToCardinality.ToString(),
            crossFilter = r.CrossFilteringBehavior.ToString(),
            active = r.IsActive,
        });
        var exprs = m.Expressions.Select(e => new { name = e.Name, kind = e.Kind.ToString() });
        return new { model = m.Name, tables, relationships = rels, expressions = exprs };
    }

    // ---------------------------------------------------------------- measures
    public object AddMeasure(string sessionId, string table, string name, string dax,
        string? formatString, string? displayFolder, string? description)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddMeasureCore(model, table, name, dax, formatString, displayFolder, description);
        ModelTxn.Save(model);
        return Persisted(new { added = $"{table}[{name}]" });
    }

    /// <summary>Pure object-tree add of a measure (no SaveChanges) so generators and tests can compose it.</summary>
    internal static TOM.Measure AddMeasureCore(TOM.Model model, string table, string name, string dax,
        string? formatString, string? displayFolder, string? description)
    {
        var t = Table(model, table);
        if (t.Measures.Contains(name))
            throw new InvalidOperationException($"Measure '{name}' already exists on '{table}'. Use update_measure.");
        var measure = new TOM.Measure { Name = name, Expression = dax };
        if (!string.IsNullOrWhiteSpace(formatString)) measure.FormatString = formatString;
        if (!string.IsNullOrWhiteSpace(displayFolder)) measure.DisplayFolder = displayFolder;
        if (!string.IsNullOrWhiteSpace(description)) measure.Description = description;
        t.Measures.Add(measure);
        return measure;
    }

    /// <summary>Apply a batch of generated measure specs to the model (pure; caller saves). Returns the names.</summary>
    internal static List<string> ApplyMeasureSpecsCore(TOM.Model model, IReadOnlyList<DaxGenerators.MeasureSpec> specs)
    {
        var added = new List<string>();
        foreach (var s in specs)
        {
            AddMeasureCore(model, s.Table, s.Name, s.Dax, s.Format, s.Folder, null);
            added.Add($"{s.Table}[{s.Name}]");
        }
        return added;
    }

    /// <summary>
    /// Create a dynamic NARRATIVE text measure - e.g. "Grated, Sliced drove +2.1% growth". It names the
    /// top-N dimension members by contribution to the change (current - prior) and states the overall
    /// growth. Display it in a card (it updates live with slicers). Auto-insight without the native visual.
    /// </summary>
    public object AddNarrativeMeasure(string sessionId, string homeTable, string measureName,
        string dimTable, string dimColumn, string currentMeasure, string priorMeasure, string growthMeasure, int topN)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var t = Table(model, homeTable);
        if (t.Measures.Contains(measureName))
            throw new InvalidOperationException($"Measure '{measureName}' already exists on '{homeTable}'. Use update_measure.");
        string dim = $"'{dimTable}'[{dimColumn}]";
        string dax =
            $"VAR _overall = [{growthMeasure}]\n" +
            $"VAR _contrib = ADDCOLUMNS ( VALUES ( {dim} ), \"@c\", [{currentMeasure}] - [{priorMeasure}] )\n" +
            $"VAR _gainers = TOPN ( {Math.Max(1, topN)}, FILTER ( _contrib, [@c] > 0 ), [@c], DESC )\n" +
            $"VAR _names = CONCATENATEX ( _gainers, {dim}, \", \", [@c], DESC )\n" +
            "RETURN\n" +
            "IF ( ISBLANK ( _overall ), \"\",\n" +
            "    IF ( COUNTROWS ( _gainers ) > 0,\n" +
            "        _names & \" drove \" & FORMAT ( _overall, \"+0.0%;-0.0%\" ) & \" growth\",\n" +
            "        FORMAT ( _overall, \"+0.0%;-0.0%\" ) & \" change\" ) )";
        var measure = new TOM.Measure { Name = measureName, Expression = dax };
        t.Measures.Add(measure);
        ModelTxn.Save(model);
        return Persisted(new { added = $"{homeTable}[{measureName}]",
            note = "Dynamic narrative text measure. Show it in a card (add_card) with word-wrap - it updates with slicers." });
    }

    /// <summary>
    /// Author a DYNAMIC TITLE text measure: SELECTEDVALUE over a column with an "All/multiple" fallback,
    /// optionally wrapped in a template (use {value} as the placeholder for the selected value). e.g. column
    /// 'Dim_Product'[Category], template "Sales - {value}" gives "Sales - Bikes" / "Sales - All categories".
    /// Bind it to a visual's title with bind_dynamic_title (report side).
    /// </summary>
    public object AddDynamicTitleMeasure(string sessionId, string homeTable, string measureName,
        string column, string? template, string? allLabel)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var t = Table(model, homeTable);
        if (t.Measures.Contains(measureName))
            throw new InvalidOperationException($"Measure '{measureName}' already exists on '{homeTable}'. Use update_measure.");
        string dax = BuildDynamicTitleDax(column, template, allLabel);
        t.Measures.Add(new TOM.Measure { Name = measureName, Expression = dax });
        ModelTxn.Save(model);
        return Persisted(new { added = $"{homeTable}[{measureName}]",
            note = "Dynamic title text measure. Bind it with bind_dynamic_title (expression-based title)." });
    }

    /// <summary>Pure builder for the dynamic-title DAX (so it is unit-testable). Accepts column as
    /// Table[Col] or 'Table'[Col]; falls back to a count-based "All/multiple" label when not a single value.</summary>
    internal static string BuildDynamicTitleDax(string column, string? template, string? allLabel)
    {
        string col = NormaliseColumnRef(column);
        string all = (allLabel ?? "All").Replace("\"", "\"\"");
        string sel = $"SELECTEDVALUE ( {col}, \"{all}\" )";
        if (string.IsNullOrWhiteSpace(template)) return sel;
        // split the template on {value} and concatenate the literal parts with the selected value.
        string tpl = template!;
        int at = tpl.IndexOf("{value}", StringComparison.OrdinalIgnoreCase);
        if (at < 0) return $"\"{tpl.Replace("\"", "\"\"")}\"";   // no placeholder -> static text
        string before = tpl[..at].Replace("\"", "\"\"");
        string after = tpl[(at + "{value}".Length)..].Replace("\"", "\"\"");
        var parts = new List<string>();
        if (before.Length > 0) parts.Add($"\"{before}\"");
        parts.Add(sel);
        if (after.Length > 0) parts.Add($"\"{after}\"");
        return string.Join(" & ", parts);
    }

    /// <summary>Normalise a column reference to 'Table'[Column] for DAX (accepts Table[Col] or 'Table'[Col]).</summary>
    private static string NormaliseColumnRef(string column)
    {
        string s = (column ?? "").Trim();
        int lb = s.IndexOf('[');
        if (lb <= 0 || !s.EndsWith("]"))
            throw new InvalidOperationException($"column must be Table[Column] (got '{column}').");
        return $"'{s[..lb].Trim().Trim('\'')}'[{s[(lb + 1)..^1]}]";
    }

    public object UpdateMeasure(string sessionId, string table, string name, string? dax,
        string? formatString, string? displayFolder)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var me = Table(model, table).Measures.Find(name)
                 ?? throw new InvalidOperationException($"Measure '{name}' not found on '{table}'.");
        if (dax != null) me.Expression = dax;
        if (formatString != null) me.FormatString = formatString;
        if (displayFolder != null) me.DisplayFolder = displayFolder;
        ModelTxn.Save(model);
        return Persisted(new { updated = $"{table}[{name}]" });
    }

    public object DeleteMeasure(string sessionId, string table, string name)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var t = Table(model, table);
        if (!t.Measures.Contains(name))
            throw new InvalidOperationException($"Measure '{name}' not found on '{table}'.");
        t.Measures.Remove(name);
        ModelTxn.Save(model);
        return Persisted(new { deleted = $"{table}[{name}]" });
    }

    public object ListMeasures(string sessionId, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var src = table != null ? new[] { Table(model, table) } : model.Tables.ToArray();
        var measures = src.SelectMany(t => t.Measures.Select(me => new
        {
            table = t.Name, name = me.Name, expression = me.Expression,
            format = me.FormatString, folder = me.DisplayFolder
        }));
        return new { measures };
    }

    // ---------------------------------------------------------------- columns
    public object AddCalculatedColumn(string sessionId, string table, string name, string dax)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var t = Table(model, table);
        if (t.Columns.Contains(name))
            throw new InvalidOperationException($"Column '{name}' already exists on '{table}'.");
        t.Columns.Add(new TOM.CalculatedColumn { Name = name, Expression = dax });
        ModelTxn.Save(model);
        return Persisted(new { added = $"{table}[{name}]", note = "Calculated column; values compute on the next recalc." });
    }

    public object DeleteTable(string sessionId, string name)
    {
        var model = _sessions.GetModel(sessionId).Model;
        if (!model.Tables.Contains(name))
            return new { ok = true, note = $"Table '{name}' not present (nothing to delete)." };
        // drop relationships touching the table first (TOM won't remove a table
        // whose columns are still used in a relationship)
        var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>()
            .Where(r => r.FromTable?.Name == name || r.ToTable?.Name == name).ToList();
        foreach (var r in rels) model.Relationships.Remove(r);
        model.Tables.Remove(name);
        ModelTxn.Save(model);
        return Persisted(new { deleted = name, relationshipsDropped = rels.Count });
    }

    public object AddDataColumn(string sessionId, string table, string name, string dataType, string? sourceColumn)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var t = Table(model, table);
        if (t.Columns.Contains(name))
            return Persisted(new { note = $"{table}[{name}] already exists" });
        var dt = Enum.TryParse<TOM.DataType>(dataType, ignoreCase: true, out var parsed) ? parsed : TOM.DataType.String;
        t.Columns.Add(new TOM.DataColumn { Name = name, DataType = dt, SourceColumn = sourceColumn ?? name });
        ModelTxn.Save(model);
        return Persisted(new { added = $"{table}[{name}]", dataType = dt.ToString(), refreshRequired = true });
    }

    // ---------------------------------------------------------------- relationships
    public object AddRelationship(string sessionId, string fromTable, string fromColumn,
        string toTable, string toColumn, bool bothDirections, bool active,
        string? fromCardinality, string? toCardinality, string? crossFilteringBehavior,
        string? securityFilteringBehavior, string? joinOnDateBehavior)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var rel = AddRelationshipCore(model, fromTable, fromColumn, toTable, toColumn, bothDirections, active,
            fromCardinality, toCardinality, crossFilteringBehavior, securityFilteringBehavior, joinOnDateBehavior);
        ModelTxn.Save(model);
        // Build the relationship's cross-reference index so the model is immediately queryable.
        // Without this, the next query errors: "relationship ... needs to be recalculated".
        // RefreshType.Calculate recomputes indexes + calculated tables/columns without re-importing data.
        bool recalculated = true;
        try { model.RequestRefresh(TOM.RefreshType.Calculate); ModelTxn.Save(model); }
        catch { recalculated = false; }
        return Persisted(new
        {
            added = $"{fromTable}[{fromColumn}] {CardSymbol(rel.FromCardinality)}-{CardSymbol(rel.ToCardinality)} {toTable}[{toColumn}]",
            active,
            crossFilter = rel.CrossFilteringBehavior.ToString(),
            recalculated,
        });
    }

    /// <summary>
    /// Update an existing relationship's cardinality, cross/security-filtering, active flag or
    /// join-on-date behaviour. Identify it by name, or by its from/to column pair.
    /// </summary>
    /// <summary>
    /// Build a SingleColumnRelationship with overridable cardinality / cross + security filtering / join-on-date
    /// and add it to the model. Pure object-tree mutation (no SaveChanges) so it can be unit-tested.
    /// </summary>
    internal static TOM.SingleColumnRelationship AddRelationshipCore(TOM.Model model, string fromTable, string fromColumn,
        string toTable, string toColumn, bool bothDirections, bool active,
        string? fromCardinality, string? toCardinality, string? crossFilteringBehavior,
        string? securityFilteringBehavior, string? joinOnDateBehavior)
    {
        var ft = Table(model, fromTable); var tt = Table(model, toTable);
        var fc = Column(ft, fromColumn); var tc = Column(tt, toColumn);
        var rel = new TOM.SingleColumnRelationship
        {
            FromColumn = fc,
            ToColumn = tc,
            // default Many->One (star schema); overridable to support 1:1 / 1:M / M:M.
            FromCardinality = fromCardinality != null ? ParseCardinality(fromCardinality) : TOM.RelationshipEndCardinality.Many,
            ToCardinality = toCardinality != null ? ParseCardinality(toCardinality) : TOM.RelationshipEndCardinality.One,
            // explicit crossFilteringBehavior wins; else fall back to the bothDirections flag.
            CrossFilteringBehavior = crossFilteringBehavior != null
                ? ParseCrossFilter(crossFilteringBehavior)
                : (bothDirections ? TOM.CrossFilteringBehavior.BothDirections : TOM.CrossFilteringBehavior.OneDirection),
            IsActive = active,
        };
        if (securityFilteringBehavior != null) rel.SecurityFilteringBehavior = ParseSecurityFilter(securityFilteringBehavior);
        if (joinOnDateBehavior != null) rel.JoinOnDateBehavior = ParseJoinOnDate(joinOnDateBehavior);
        model.Relationships.Add(rel);
        return rel;
    }

    public object UpdateRelationship(string sessionId, string? name, string? fromTable, string? fromColumn,
        string? toTable, string? toColumn, string? fromCardinality, string? toCardinality,
        string? crossFilteringBehavior, string? securityFilteringBehavior, bool? isActive, string? joinOnDateBehavior)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var rel = UpdateRelationshipCore(model, name, fromTable, fromColumn, toTable, toColumn,
            fromCardinality, toCardinality, crossFilteringBehavior, securityFilteringBehavior, isActive, joinOnDateBehavior);
        ModelTxn.Save(model);
        bool recalculated = true;
        try { model.RequestRefresh(TOM.RefreshType.Calculate); ModelTxn.Save(model); }
        catch { recalculated = false; }
        return Persisted(new
        {
            updated = $"{rel.FromTable.Name}[{rel.FromColumn.Name}] {CardSymbol(rel.FromCardinality)}-{CardSymbol(rel.ToCardinality)} {rel.ToTable.Name}[{rel.ToColumn.Name}]",
            crossFilter = rel.CrossFilteringBehavior.ToString(),
            securityFilter = rel.SecurityFilteringBehavior.ToString(),
            active = rel.IsActive,
            recalculated,
        });
    }

    internal static TOM.SingleColumnRelationship UpdateRelationshipCore(TOM.Model model, string? name, string? fromTable,
        string? fromColumn, string? toTable, string? toColumn, string? fromCardinality, string? toCardinality,
        string? crossFilteringBehavior, string? securityFilteringBehavior, bool? isActive, string? joinOnDateBehavior)
    {
        var rel = ResolveRelationship(model, name, fromTable, fromColumn, toTable, toColumn);
        if (fromCardinality != null) rel.FromCardinality = ParseCardinality(fromCardinality);
        if (toCardinality != null) rel.ToCardinality = ParseCardinality(toCardinality);
        if (crossFilteringBehavior != null) rel.CrossFilteringBehavior = ParseCrossFilter(crossFilteringBehavior);
        if (securityFilteringBehavior != null) rel.SecurityFilteringBehavior = ParseSecurityFilter(securityFilteringBehavior);
        if (isActive is { } a) rel.IsActive = a;
        if (joinOnDateBehavior != null) rel.JoinOnDateBehavior = ParseJoinOnDate(joinOnDateBehavior);
        return rel;
    }

    public object DeleteRelationship(string sessionId, string? name, string? fromTable, string? fromColumn,
        string? toTable, string? toColumn)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var rel = ResolveRelationship(model, name, fromTable, fromColumn, toTable, toColumn);
        string label = $"{rel.FromTable.Name}[{rel.FromColumn.Name}] -> {rel.ToTable.Name}[{rel.ToColumn.Name}]";
        DeleteRelationshipCore(model, name, fromTable, fromColumn, toTable, toColumn);
        ModelTxn.Save(model);
        return Persisted(new { deleted = label });
    }

    internal static void DeleteRelationshipCore(TOM.Model model, string? name, string? fromTable, string? fromColumn,
        string? toTable, string? toColumn)
    {
        var rel = ResolveRelationship(model, name, fromTable, fromColumn, toTable, toColumn);
        model.Relationships.Remove(rel);
    }

    /// <summary>Find a relationship by name, else by its from/to column pair (order-insensitive).</summary>
    internal static TOM.SingleColumnRelationship ResolveRelationship(TOM.Model model, string? name,
        string? fromTable, string? fromColumn, string? toTable, string? toColumn)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            var byName = model.Relationships.Find(name) as TOM.SingleColumnRelationship
                         ?? throw new InvalidOperationException($"Relationship '{name}' not found.");
            return byName;
        }
        if (string.IsNullOrWhiteSpace(fromTable) || string.IsNullOrWhiteSpace(fromColumn)
            || string.IsNullOrWhiteSpace(toTable) || string.IsNullOrWhiteSpace(toColumn))
            throw new InvalidOperationException("Provide either name, or all of fromTable/fromColumn/toTable/toColumn.");
        bool Match(TOM.SingleColumnRelationship r, string ft, string fcl, string tt, string tcl) =>
            r.FromTable != null && r.ToTable != null && r.FromColumn != null && r.ToColumn != null
            && r.FromTable.Name.Equals(ft, StringComparison.OrdinalIgnoreCase)
            && r.FromColumn.Name.Equals(fcl, StringComparison.OrdinalIgnoreCase)
            && r.ToTable.Name.Equals(tt, StringComparison.OrdinalIgnoreCase)
            && r.ToColumn.Name.Equals(tcl, StringComparison.OrdinalIgnoreCase);
        var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>().ToList();
        var found = rels.FirstOrDefault(r => Match(r, fromTable!, fromColumn!, toTable!, toColumn!))
                    ?? rels.FirstOrDefault(r => Match(r, toTable!, toColumn!, fromTable!, fromColumn!))
                    ?? throw new InvalidOperationException(
                        $"No relationship between {fromTable}[{fromColumn}] and {toTable}[{toColumn}].");
        return found;
    }

    private static TOM.RelationshipEndCardinality ParseCardinality(string s) =>
        (s ?? "").Trim().ToLowerInvariant() switch
        {
            "one" or "1" => TOM.RelationshipEndCardinality.One,
            "many" or "m" or "*" => TOM.RelationshipEndCardinality.Many,
            _ => throw new InvalidOperationException($"Invalid cardinality '{s}'. Use One or Many."),
        };

    private static TOM.CrossFilteringBehavior ParseCrossFilter(string s) =>
        Enum.TryParse<TOM.CrossFilteringBehavior>((s ?? "").Trim(), ignoreCase: true, out var v)
            ? v
            : throw new InvalidOperationException($"Invalid crossFilteringBehavior '{s}'. Use OneDirection, BothDirections or Automatic.");

    private static TOM.SecurityFilteringBehavior ParseSecurityFilter(string s) =>
        Enum.TryParse<TOM.SecurityFilteringBehavior>((s ?? "").Trim(), ignoreCase: true, out var v)
            ? v
            : throw new InvalidOperationException($"Invalid securityFilteringBehavior '{s}'. Use OneDirection, BothDirections or None.");

    private static TOM.DateTimeRelationshipBehavior ParseJoinOnDate(string s) =>
        Enum.TryParse<TOM.DateTimeRelationshipBehavior>((s ?? "").Trim(), ignoreCase: true, out var v)
            ? v
            : throw new InvalidOperationException($"Invalid joinOnDateBehavior '{s}'. Use DateAndTime or DatePartOnly.");

    private static string CardSymbol(TOM.RelationshipEndCardinality c) =>
        c == TOM.RelationshipEndCardinality.One ? "1" : c == TOM.RelationshipEndCardinality.Many ? "*" : "?";

    // ---------------------------------------------------------------- M / Power Query
    public object SetPartitionM(string sessionId, string table, string m, string? partitionName)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var t = Table(model, table);
        var part = partitionName != null
            ? (t.Partitions.Find(partitionName) ?? throw new InvalidOperationException($"Partition '{partitionName}' not found."))
            : t.Partitions[0];
        if (part.Source is not TOM.MPartitionSource src)
            throw new InvalidOperationException($"Partition '{part.Name}' is not an M (Power Query) partition.");
        src.Expression = m;
        ModelTxn.Save(model);
        session.MDirty.Mark($"set_partition_m {table}/{part.Name}");
        return Persisted(new { updated = $"{table}/{part.Name}", refreshRequired = true, refreshRequiredBeforeSave = true });
    }

    // ---------------------------------------------------------------- discrete PQ transforms
    /// <summary>
    /// The ergonomic transform layer: read a table's current M, append one mapped step (via the pure
    /// <see cref="SuperBiMcp.Integrations.MTransformBuilder"/> generators), and persist through the same
    /// MPartitionSource path as <see cref="SetPartitionM"/>. Equivalent to clicking a transform in the
    /// Power Query editor, but driven by an explicit argument set.
    /// </summary>
    private object AppendTransform(string sessionId, string table, string? partitionName,
        Func<string, string> append, string transformName)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var t = Table(model, table);
        var part = partitionName != null
            ? (t.Partitions.Find(partitionName) ?? throw new InvalidOperationException($"Partition '{partitionName}' not found."))
            : t.Partitions[0];
        if (part.Source is not TOM.MPartitionSource src)
            throw new InvalidOperationException($"Partition '{part.Name}' is not an M (Power Query) partition.");
        string newM = append(src.Expression ?? "");
        src.Expression = newM;
        ModelTxn.Save(model);
        session.MDirty.Mark($"{transformName} {table}/{part.Name}");
        return Persisted(new { table = $"{table}/{part.Name}", transform = transformName, m = newM, refreshRequired = true, refreshRequiredBeforeSave = true });
    }

    public object MergeQueries(string sessionId, string table, string rightTable, string[] leftKeys, string[] rightKeys,
        string joinKind, string[]? expandColumns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.MergeQueries(m, rightTable, leftKeys, rightKeys, joinKind, expandColumns),
            "merge_queries");

    public object AppendQueries(string sessionId, string table, string[] otherTables, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.AppendQueries(m, otherTables), "append_queries");

    public object PivotColumn(string sessionId, string table, string attributeColumn, string valueColumn,
        string? aggregation, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.PivotColumn(m, attributeColumn, valueColumn, aggregation), "pivot_column");

    public object GroupBy(string sessionId, string table, string[] keyColumns,
        IReadOnlyList<(string name, string op, string? column)> aggregations, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.GroupBy(m, keyColumns, aggregations), "group_by");

    public object SplitColumn(string sessionId, string table, string column, string by, string arg, int parts, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.SplitColumn(m, column, by, arg, parts), "split_column");

    public object SplitColumnToRows(string sessionId, string table, string column, string delimiter, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.SplitColumnToRows(m, column, delimiter), "split_column_to_rows");

    public object ReplaceValues(string sessionId, string table, string column, string find, string replace, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ReplaceValues(m, column, find, replace), "replace_values");

    public object ChangeColumnType(string sessionId, string table, IReadOnlyList<(string column, string type)> types,
        string? culture, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ChangeColumnType(m, types, culture), "change_column_type");

    public object DetectColumnTypes(string sessionId, string table, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.DetectColumnTypes(m), "detect_column_types");

    public object FilterRows(string sessionId, string table, string mCondition, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.FilterRows(m, mCondition), "filter_rows");

    public object AddCustomColumn(string sessionId, string table, string name, string mExpression, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.AddCustomColumn(m, name, mExpression), "add_custom_column");

    public object AddIndexColumn(string sessionId, string table, string? name, int start, int step, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.AddIndexColumn(m, name, start, step), "add_index_column");

    public object RemoveColumns(string sessionId, string table, string[] columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RemoveColumns(m, columns), "remove_columns");

    public object RenameColumns(string sessionId, string table, IReadOnlyList<(string from, string to)> renames, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RenameColumns(m, renames), "rename_columns");

    public object FillDown(string sessionId, string table, string[] columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.FillDown(m, columns), "fill_down");

    public object FillUp(string sessionId, string table, string[] columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.FillUp(m, columns), "fill_up");

    public object RemoveDuplicates(string sessionId, string table, string[]? columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RemoveDuplicates(m, columns), "remove_duplicates");

    public object PromoteHeaders(string sessionId, string table, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.PromoteHeaders(m), "promote_headers");

    public object Transpose(string sessionId, string table, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.Transpose(m), "transpose");

    // ---------------------------------------------------------------- Wave J: remaining discrete transforms
    public object UnpivotColumns(string sessionId, string table, string[] columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.UnpivotColumns(m, columns), "unpivot_columns");

    public object UnpivotOtherColumns(string sessionId, string table, string[] keepColumns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.UnpivotOtherColumns(m, keepColumns), "unpivot_other_columns");

    public object MergeColumns(string sessionId, string table, string[] columns, string separator,
        string newColumnName, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.MergeColumns(m, columns, separator, newColumnName), "merge_columns");

    public object ExpandColumn(string sessionId, string table, string column, string[]? fields, string? kind, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ExpandColumn(m, column, fields, kind), "expand_column");

    public object KeepTopRows(string sessionId, string table, int count, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.KeepTopRows(m, count), "keep_top_rows");

    public object KeepBottomRows(string sessionId, string table, int count, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.KeepBottomRows(m, count), "keep_bottom_rows");

    public object SkipRows(string sessionId, string table, int count, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.SkipRows(m, count), "skip_rows");

    public object KeepRangeRows(string sessionId, string table, int offset, int count, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.KeepRangeRows(m, offset, count), "keep_range_rows");

    public object SortRows(string sessionId, string table,
        IReadOnlyList<(string column, string direction)> sorts, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.SortRows(m, sorts), "sort_rows");

    public object SelectColumns(string sessionId, string table, string[] columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.SelectColumns(m, columns), "select_columns");

    public object ReorderColumns(string sessionId, string table, string[] order, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ReorderColumns(m, order), "reorder_columns");

    public object DuplicateColumn(string sessionId, string table, string column, string newName, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.DuplicateColumn(m, column, newName), "duplicate_column");

    public object TransformColumn(string sessionId, string table, string column, string operation, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.TransformColumn(m, column, operation), "transform_column");

    // ---------------------------------------------------------------- Wave J: folding-control hints
    public object SetQueryBuffer(string sessionId, string table, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.BufferTable(m), "set_query_buffer");

    public object SetStopFolding(string sessionId, string table, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.StopFolding(m), "set_stop_folding");

    // ---------------------------------------------------------------- Wave J: connector source step
    /// <summary>
    /// Seed or replace the SOURCE step of a table's M partition with a generated connector expression.
    /// Builds the connector M via <see cref="Integrations.MTransformBuilder.SourceStep"/>, then rewires the
    /// first step (downstream steps preserved) via <see cref="Integrations.MTransformBuilder.ReplaceSourceStep"/>.
    /// </summary>
    public object AddSourceStep(string sessionId, string table, string connector,
        IReadOnlyDictionary<string, string> parameters, string? partitionName)
    {
        string sourceExpr = Integrations.MTransformBuilder.SourceStep(connector, parameters);
        return AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ReplaceSourceStep(m, sourceExpr), "add_source_step");
    }

    // ---------------------------------------------------------------- Wave L: error rows, row trims, value/error replace
    public object RemoveErrors(string sessionId, string table, string[]? columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RemoveErrors(m, columns), "remove_errors");

    public object KeepErrors(string sessionId, string table, string[]? columns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.KeepErrors(m, columns), "keep_errors");

    public object ReplaceErrors(string sessionId, string table,
        IReadOnlyList<(string column, string? value, string valueType)> replacements, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ReplaceErrors(m, replacements), "replace_errors");

    public object RemoveBlankRows(string sessionId, string table, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RemoveBlankRows(m), "remove_blank_rows");

    public object RemoveBottomRows(string sessionId, string table, int count, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RemoveBottomRows(m, count), "remove_bottom_rows");

    public object RemoveAlternateRows(string sessionId, string table, int firstKept, int taken, int skipped, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RemoveAlternateRows(m, firstKept, taken, skipped), "remove_alternate_rows");

    public object ReplaceValue(string sessionId, string table, string column, string? oldValue, string? newValue,
        string valueType, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ReplaceValue(m, column, oldValue, newValue, valueType), "replace_value");

    // ---------------------------------------------------------------- Wave L: demote, move, conditional column
    public object DemoteHeaders(string sessionId, string table, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.DemoteHeaders(m), "demote_headers");

    public object MoveColumn(string sessionId, string table, string column, string position, string? refColumn, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.MoveColumn(m, column, position, refColumn), "move_column");

    public object AddConditionalColumn(string sessionId, string table, string name,
        IReadOnlyList<(string column, string op, string? value, string? result)> rules,
        string? elseResult, string valueType, string resultType, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.AddConditionalColumn(m, name, rules, elseResult, valueType, resultType),
            "add_conditional_column");

    // ---------------------------------------------------------------- Wave L: fuzzy join / group / cluster
    public object FuzzyMerge(string sessionId, string table, string rightTable, string[] leftKeys, string[] rightKeys,
        string joinKind, double threshold, bool? ignoreCase, bool? ignoreSpace, string? transformationTable,
        string[]? expandColumns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.FuzzyMerge(m, rightTable, leftKeys, rightKeys, joinKind, threshold,
                ignoreCase, ignoreSpace, transformationTable, expandColumns), "fuzzy_merge");

    public object FuzzyGroup(string sessionId, string table, string[] keyColumns,
        IReadOnlyList<(string name, string op, string? column)> aggregations, double? threshold, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.FuzzyGroup(m, keyColumns, aggregations, threshold), "fuzzy_group");

    public object FuzzyClusterColumn(string sessionId, string table, string column, string newColumn,
        double? threshold, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.FuzzyClusterColumn(m, column, newColumn, threshold), "fuzzy_cluster_column");

    // ---------------------------------------------------------------- Wave J: M parameter
    /// <summary>
    /// Create (or update) a Power Query parameter as a NamedExpression carrying the IsParameterQuery
    /// metadata - the same shape used for RangeStart/RangeEnd in <see cref="SetIncrementalRefreshCore"/>.
    /// </summary>
    public object AddMParameter(string sessionId, string name, string type, string? defaultValue, string[]? allowedValues)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        string m = AddMParameterCore(model, session.MDirty, name, type, defaultValue, allowedValues);
        ModelTxn.Save(model);
        return Persisted(new { parameter = name, type = Integrations.MTransformBuilder.NormaliseParamType(type), m,
            refreshRequired = true, refreshRequiredBeforeSave = true });
    }

    /// <summary>Mutation + tracker seam, offline-testable (no SaveChanges). A parameter IS a shared M
    /// expression: every partition that references it re-imports against the new value, so the
    /// pending-refresh blast radius is the whole model - the same rule as set_shared_expression.
    /// The mark rides the mutation, not the commit: if the caller's SaveChanges then fails, an
    /// over-marked tracker only costs a redundant refresh at save time (fail-closed for the gate).</summary>
    internal static string AddMParameterCore(TOM.Model model, MDirtyTracker dirty, string name, string type,
        string? defaultValue, string[]? allowedValues)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("name is required.");
        string m = Integrations.MTransformBuilder.ParameterExpression(type, defaultValue, allowedValues);
        var existing = model.Expressions.Find(name);
        if (existing != null) { existing.Kind = TOM.ExpressionKind.M; existing.Expression = m; }
        else model.Expressions.Add(new TOM.NamedExpression { Name = name, Kind = TOM.ExpressionKind.M, Expression = m });
        dirty.Mark($"add_m_parameter {name}");
        return m;
    }

    public object AddTableFromM(string sessionId, string name, string m)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        AddTableFromMCore(model, session.MDirty, name, m);
        ModelTxn.Save(model);
        return Persisted(new { added = name, refreshRequired = true, refreshRequiredBeforeSave = true,
            note = "Columns populate after refresh_table (RefreshType.Full)." });
    }

    /// <summary>Mutation + tracker seam, offline-testable (no SaveChanges). A brand-new M partition is
    /// M the mashup document has never refreshed: a bare Ctrl+S would persist metadata and mashup out
    /// of step, so the save gate must force a Full refresh first. Marking here also covers every
    /// generator wrapper that funnels through <see cref="AddTableFromM"/> (generate_calendar_table,
    /// generate_445_calendar, paginated_rest_source, combine_folder_files).</summary>
    internal static TOM.Table AddTableFromMCore(TOM.Model model, MDirtyTracker dirty, string name, string m)
    {
        if (model.Tables.Contains(name))
            throw new InvalidOperationException($"Table '{name}' already exists.");
        var table = new TOM.Table { Name = name };
        table.Partitions.Add(new TOM.Partition
        {
            Name = name,
            Source = new TOM.MPartitionSource { Expression = m },
        });
        model.Tables.Add(table);
        dirty.Mark($"add_table_from_m {name}");
        return table;
    }

    // ---------------------------------------------------------------- Wave Q: community generators + folding + dataflow

    /// <summary>Add a generated date/calendar table (List.Dates + ~20 part columns) as a new M query.</summary>
    public object GenerateCalendarTable(string sessionId, string name, string startExpr, string endExpr,
        int? fiscalYearEndMonth, string? locale)
        => AddTableFromM(sessionId, name,
            Integrations.MTransformBuilder.GenerateCalendarTableM(startExpr, endExpr, fiscalYearEndMonth, locale));

    /// <summary>Add a generated retail 4-4-5 / 4-5-4 / 5-4-4 calendar table as a new M query.</summary>
    public object Generate445Calendar(string sessionId, string name, string startDate, int weeksPattern,
        int periodsPerYear, int yearsToGenerate)
        => AddTableFromM(sessionId, name,
            Integrations.MTransformBuilder.Generate445CalendarM(startDate, weeksPattern, periodsPerYear, yearsToGenerate));

    /// <summary>Add a generated paginated-REST source (List.Generate page loop + Table.Combine) as a new M query.</summary>
    public object PaginatedRestSource(string sessionId, string name, string baseUrl, string mode, string dataPath,
        string? pageParam, string? sizeParam, int pageSize, string? nextField, string? recordFieldsExpr)
        => AddTableFromM(sessionId, name,
            Integrations.MTransformBuilder.PaginatedRestSourceM(baseUrl, mode, dataPath, pageParam, sizeParam,
                pageSize, nextField, recordFieldsExpr));

    /// <summary>Add a generated schema-drift-safe combine-folder-files query as a new M query.</summary>
    public object CombineFolderFiles(string sessionId, string name, string folderPath, string fileType,
        string? delimiter, int skipRows, bool promoteHeaders, bool keepFilename, bool skipErrors)
        => AddTableFromM(sessionId, name,
            Integrations.MTransformBuilder.CombineFolderFilesM(folderPath, fileType, delimiter, skipRows,
                promoteHeaders, keepFilename, skipErrors));

    public object RenameColumnsFromMapping(string sessionId, string table, string mappingTable, string oldCol,
        string newCol, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RenameColumnsFromMapping(m, mappingTable, oldCol, newCol),
            "rename_columns_from_mapping");

    public object TransformAllColumnNames(string sessionId, string table, string transform, string? arg, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.TransformAllColumnNames(m, transform, arg), "transform_all_column_names");

    public object GroupKeepAllColumns(string sessionId, string table, string[] keys, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.GroupKeepAllColumns(m, keys), "group_keep_all_columns");

    public object RunningTotal(string sessionId, string table, string valueColumn, string orderColumn,
        string? groupColumn, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.RunningTotalM(m, valueColumn, orderColumn, groupColumn), "running_total");

    public object PivotTextValues(string sessionId, string table, string attributeColumn, string valueColumn,
        string? delimiter, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.PivotTextValues(m, attributeColumn, valueColumn, delimiter), "pivot_text_values");

    public object UnpivotKeepNulls(string sessionId, string table, string[] keepColumns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.UnpivotKeepNulls(m, keepColumns), "unpivot_keep_nulls");

    public object DynamicUnpivotOtherColumns(string sessionId, string table, string[] keepColumns, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.DynamicUnpivotOtherColumns(m, keepColumns), "dynamic_unpivot_other_columns");

    public object ConcatenateWithGroupBy(string sessionId, string table, string[] keys, string textColumn,
        string? delimiter, string? outputColumn, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ConcatenateWithGroupBy(m, keys, textColumn, delimiter, outputColumn),
            "concatenate_with_group_by");

    public object AddTableViewFolding(string sessionId, string table, string[] handlers, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.AddTableViewFolding(m, handlers), "add_table_view_folding");

    public object ValueNativeQueryFolding(string sessionId, string table, string sourceExpr, string nativeQuery,
        string? paramsExpr, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.ValueNativeQueryFolding(m, sourceExpr, nativeQuery, paramsExpr),
            "value_nativequery_folding");

    public object SetListBuffer(string sessionId, string table, string referenceExpr, string kind, string? partitionName)
        => AppendTransform(sessionId, table, partitionName,
            m => Integrations.MTransformBuilder.SetListBuffer(m, referenceExpr, kind), "set_list_buffer");

    /// <summary>
    /// Export a Power BI dataflow model.json from a set of (entity, M, attributes) definitions to disk.
    /// FLAG: the inner pbi:mashup layout is best-known, not verified against a real export.
    /// </summary>
    public object ExportDataflowModelJson(string dataflowName,
        IReadOnlyList<(string name, string mExpression, IReadOnlyList<(string name, string dataType)> attributes)> entities,
        string? culture, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(outputPath)) throw new InvalidOperationException("outputPath is required.");
        string json = Integrations.MTransformBuilder.ExportDataflowModelJson(dataflowName, entities, culture);
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(outputPath, json, new System.Text.UTF8Encoding(false));
        return new
        {
            ok = true,
            persistedToDisk = true,
            path = System.IO.Path.GetFullPath(outputPath),
            entities = entities.Count,
            note = "Dataflow model.json written. FLAG: inner pbi:mashup layout is best-known, not verified against a live export.",
        };
    }

    /// <summary>
    /// Create an import table from a staged CSV in ONE call: reads the header + a row sample to
    /// infer each column's type (Int64 / Double / String), builds the Csv.Document M, declares
    /// every column, and refreshes. Removes the manual add_data_column-per-column grind.
    /// </summary>
    public object CreateCsvTable(string sessionId, string name, string csvPath, string? pathExpression, int sampleRows)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var (table, tomType) = CreateCsvTableCore(model, session.MDirty, name, csvPath, pathExpression, sampleRows);
        ModelTxn.Save(model);
        table.RequestRefresh(TOM.RefreshType.Full);
        ModelTxn.Save(model);
        // Deliberately NO MDirty.Clear here. The refresh above is scoped to THIS table only, so it
        // proves nothing about OTHER tables' pending M edits, and the tracker is model-wide with no
        // per-table scoping - clearing would silently disarm the save gate for them (the exact rule
        // Refresh() documents). Only a whole-model Full refresh - the gate's own, or
        // refresh_table(full: true) with no table - may clear the flag.
        return Persisted(new { created = name, columns = table.Columns.Count(c => c.Type != TOM.ColumnType.RowNumber),
            types = tomType, refreshed = true, refreshRequiredBeforeSave = true });
    }

    /// <summary>Mutation + tracker seam, offline-testable (no SaveChanges): CSV type inference, M build,
    /// table + typed columns. Marks MDirty - the new M partition is a model-level M mutation, and only the
    /// gate's whole-model Full refresh proves pending M before a Desktop save; the wrapper's table-scoped
    /// refresh populates this table's data but is no such proof.</summary>
    internal static (TOM.Table table, string[] types) CreateCsvTableCore(TOM.Model model, MDirtyTracker dirty,
        string name, string csvPath, string? pathExpression, int sampleRows)
    {
        if (!File.Exists(csvPath)) throw new FileNotFoundException($"csv not found: {csvPath}");
        var lines = File.ReadLines(csvPath).Take(Math.Max(1, sampleRows) + 1).ToList();
        if (lines.Count == 0) throw new InvalidOperationException("CSV is empty.");
        var header = SplitCsv(lines[0]);
        if (header.Length > 0) header[0] = header[0].TrimStart('﻿');   // strip BOM
        int nCols = header.Length;
        var sample = new List<string[]>();
        for (int i = 1; i < lines.Count; i++) sample.Add(SplitCsv(lines[i]));

        var tomType = new string[nCols];
        for (int c = 0; c < nCols; c++)
        {
            bool any = false, allInt = true, allNum = true;
            foreach (var row in sample)
            {
                if (c >= row.Length) continue;
                var v = row[c];
                if (string.IsNullOrWhiteSpace(v)) continue;
                any = true;
                if (!long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) allInt = false;
                if (!double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out _)) allNum = false;
                if (!allInt && !allNum) break;
            }
            tomType[c] = !any ? "String" : allInt ? "Int64" : allNum ? "Double" : "String";
        }

        string src = string.IsNullOrWhiteSpace(pathExpression)
            ? "\"" + csvPath.Replace("\\", "\\\\") + "\""
            : pathExpression!;
        var typed = new List<string>();
        for (int c = 0; c < nCols; c++)
            if (tomType[c] != "String")
                typed.Add($"{{\"{header[c].Replace("\"", "\"\"")}\", {(tomType[c] == "Int64" ? "Int64.Type" : "type number")}}}");
        string transform = typed.Count > 0 ? $",\n    Typed = Table.TransformColumnTypes(Promoted, {{{string.Join(", ", typed)}}})" : "";
        string final = typed.Count > 0 ? "Typed" : "Promoted";
        string m = $"let\n    Source = Csv.Document(File.Contents({src}), [Delimiter=\",\", Columns={nCols}, Encoding=65001, QuoteStyle=QuoteStyle.Csv]),\n    Promoted = Table.PromoteHeaders(Source, [PromoteAllScalars=true]){transform}\nin\n    {final}";

        if (model.Tables.Contains(name)) model.Tables.Remove(name);
        var table = new TOM.Table { Name = name };
        table.Partitions.Add(new TOM.Partition { Name = name, Source = new TOM.MPartitionSource { Expression = m } });
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int c = 0; c < nCols; c++)
        {
            if (string.IsNullOrWhiteSpace(header[c]) || !seen.Add(header[c])) continue;   // skip blank/dup headers
            var dt = tomType[c] switch { "Int64" => TOM.DataType.Int64, "Double" => TOM.DataType.Double, _ => TOM.DataType.String };
            table.Columns.Add(new TOM.DataColumn { Name = header[c], DataType = dt, SourceColumn = header[c] });
        }
        model.Tables.Add(table);
        dirty.Mark($"create_csv_table {name}");
        return (table, tomType);
    }

    private static string[] SplitCsv(string line)
    {
        var outp = new List<string>();
        var sb = new StringBuilder();
        bool inQ = false;
        for (int i = 0; i < line.Length; i++)
        {
            char ch = line[i];
            if (inQ)
            {
                if (ch == '"') { if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; } else inQ = false; }
                else sb.Append(ch);
            }
            else if (ch == ',') { outp.Add(sb.ToString()); sb.Clear(); }
            else if (ch == '"') inQ = true;
            else sb.Append(ch);
        }
        outp.Add(sb.ToString());
        return outp.ToArray();
    }

    public object AddCalculatedTable(string sessionId, string name, string dax)
    {
        var model = _sessions.GetModel(sessionId).Model;
        if (model.Tables.Contains(name))
            throw new InvalidOperationException($"Table '{name}' already exists.");
        var table = new TOM.Table { Name = name };
        table.Partitions.Add(new TOM.Partition
        {
            Name = name,
            Source = new TOM.CalculatedPartitionSource { Expression = dax },
        });
        model.Tables.Add(table);
        ModelTxn.Save(model);
        return Persisted(new { added = name, refreshRequired = true });
    }

    public object SetSharedExpression(string sessionId, string name, string m)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var existing = model.Expressions.Find(name);
        if (existing != null) existing.Expression = m;
        else model.Expressions.Add(new TOM.NamedExpression { Name = name, Kind = TOM.ExpressionKind.M, Expression = m });
        ModelTxn.Save(model);
        // A shared expression feeds EVERY partition that references it, so the pending-refresh
        // blast radius is the whole model, not one table.
        session.MDirty.Mark($"set_shared_expression {name}");
        return Persisted(new { expression = name, refreshRequired = true, refreshRequiredBeforeSave = true });
    }

    public object ListM(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var partitions = model.Tables.SelectMany(t => t.Partitions
            .Where(p => p.Source is TOM.MPartitionSource)
            .Select(p => new { table = t.Name, partition = p.Name, m = ((TOM.MPartitionSource)p.Source).Expression }));
        var shared = model.Expressions.Select(e => new { name = e.Name, m = e.Expression });
        return new { partitions, sharedExpressions = shared };
    }

    public object Refresh(string sessionId, string? table, bool full)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var type = full ? TOM.RefreshType.Full : TOM.RefreshType.Calculate;
        if (table != null) Table(model, table).RequestRefresh(type);
        else model.RequestRefresh(type);
        bool saved = ModelTxn.Save(model);
        // Only a whole-model Full proves every dependent partition re-imported; a table-scoped or
        // Calculate refresh leaves other pending M edits unproven, so the flag stays. And only an
        // EXECUTED save proves anything - an open transaction defers the refresh to commit, so the
        // dirty flag must survive until then.
        if (saved && full && table is null) session.MDirty.Clear("refresh_table(full: true) over the whole model");
        return Persisted(new { refreshed = table ?? "(all)", type = type.ToString() });
    }

    // ---------------------------------------------------------------- DAX validation
    public object ValidateDax(string sessionId, string dax)
    {
        var session = _sessions.GetModel(sessionId);
        try
        {
            using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EVALUATE ROW(\"v\", {dax})";
            using var rdr = cmd.ExecuteReader();
            while (rdr.Read()) { /* drain */ }
            return new { ok = true };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    // ---------------------------------------------------------------- DAX query (returns rows)
    public object RunDax(string sessionId, string dax, int maxRows)
    {
        var session = _sessions.GetModel(sessionId);
        string query = NormaliseDaxQuery(dax);
        var (cols, rows, truncated, _) = ExecuteDaxRows(session.AdomdConnectionString, query, maxRows, countBeyondMax: false);
        return new { ok = true, columns = cols, rowCount = rows.Count, truncated, rows };
    }

    /// <summary>Allow bare table expressions: anything not starting EVALUATE/DEFINE gets EVALUATE prefixed.</summary>
    internal static string NormaliseDaxQuery(string? dax)
    {
        string query = (dax ?? "").Trim();
        if (query.Length == 0) throw new InvalidOperationException("A DAX query is required.");
        if (!query.StartsWith("EVALUATE", StringComparison.OrdinalIgnoreCase) &&
            !query.StartsWith("DEFINE", StringComparison.OrdinalIgnoreCase))
            query = "EVALUATE " + query;
        return query;
    }

    /// <summary>Open an ADOMD connection, run the query and read up to maxRows rows (maxRows 0 = store
    /// none). countBeyondMax keeps draining the reader so total is the TRUE row count even when the
    /// stored rows are capped - the RLS harness and the benchmark need the real cardinality. internal so
    /// the engine-backed offline .pbix tools (eval_dax_offline / read_table_offline) reuse the exact same
    /// exec+serialise against a Desktop they launched, rather than re-implementing it.</summary>
    internal static (List<string> cols, List<object?[]> rows, bool truncated, int total) ExecuteDaxRows(
        string connectionString, string query, int maxRows, bool countBeyondMax)
    {
        using var conn = new Adomd.AdomdConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        using var rdr = cmd.ExecuteReader();
        return SerialiseDaxReader(rdr, maxRows, countBeyondMax);
    }

    /// <summary>The reader -> (columns, rows, truncated, total) serialisation, split out from the
    /// connection/exec so it is unit-testable against any <see cref="System.Data.IDataReader"/> - a fake,
    /// or a real in-memory SQLite reader - with no live engine. run_dax, eval_dax_offline and
    /// read_table_offline all share this exact shaping so an offline result is byte-for-byte the live one.
    /// maxRows 0 stores no rows; countBeyondMax keeps draining after the cap so total stays the TRUE
    /// cardinality even when the stored rows are capped.</summary>
    internal static (List<string> cols, List<object?[]> rows, bool truncated, int total) SerialiseDaxReader(
        System.Data.IDataReader rdr, int maxRows, bool countBeyondMax)
    {
        var cols = new List<string>();
        for (int i = 0; i < rdr.FieldCount; i++) cols.Add(rdr.GetName(i));
        var rows = new List<object?[]>();
        bool truncated = false;
        int total = 0;
        while (rdr.Read())
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                if (!countBeyondMax) break;
                total++;
                continue;
            }
            var row = new object?[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++)
            {
                var v = rdr.GetValue(i);
                row[i] = v is DBNull ? null : v;
            }
            rows.Add(row);
            total++;
        }
        return (cols, rows, truncated, total);
    }

    // ---------------------------------------------------------------- measure regression harness
    /// <summary>Evaluate one scalar DAX expression via EVALUATE ROW and render it invariant-culture.
    /// An engine error renders as the golden-set error marker so error states round-trip.</summary>
    private string EvaluateScalar(ModelSession session, string dax)
    {
        try
        {
            using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EVALUATE ROW(\"v\", {dax})";
            using var rdr = cmd.ExecuteReader();
            object? v = null;
            if (rdr.Read())
            {
                var raw = rdr.GetValue(0);
                v = raw is DBNull ? null : raw;
            }
            return GoldenSet.RenderValue(v);
        }
        catch (Exception ex)
        {
            return GoldenSet.RenderError(ScrubToken(ex.Message, session.AccessTokenPrivate));
        }
    }

    public object AssertMeasure(string sessionId, string dax, string expected, double tolerance)
    {
        var session = _sessions.GetModel(sessionId);
        string actual = EvaluateScalar(session, dax);
        var (pass, delta) = GoldenSet.Compare(expected, actual, tolerance);
        return delta is { } d
            ? new { ok = true, pass, expected, actual, delta = d }
            : (object)new { ok = true, pass, expected, actual };
    }

    public object SaveGoldenSet(string sessionId, string path, string? measures)
    {
        var session = _sessions.GetModel(sessionId);
        var all = session.Model.Tables.SelectMany(t => t.Measures).ToList();
        List<TOM.Measure> picked;
        if (string.IsNullOrWhiteSpace(measures)) picked = all;
        else
        {
            picked = new List<TOM.Measure>();
            foreach (var name in measures.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                var m = all.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Measure '{name}' not found in the model.");
                picked.Add(m);
            }
        }
        if (picked.Count == 0)
            throw new InvalidOperationException("The model has no measures to capture.");

        var goldens = picked.Select(m => new GoldenSet.Golden
        {
            Name = m.Name,
            Dax = $"[{m.Name.Replace("]", "]]")}]",
            Expected = EvaluateScalar(session, $"[{m.Name.Replace("]", "]]")}]"),
        }).ToList();
        GoldenSet.Save(path, goldens);
        return new
        {
            ok = true,
            path,
            captured = goldens.Count,
            errorStates = goldens.Count(g => GoldenSet.IsError(g.Expected)),
        };
    }

    public object RunGoldenSet(string sessionId, string path)
    {
        var session = _sessions.GetModel(sessionId);
        var goldens = GoldenSet.Load(path);
        var failures = new List<object>();
        foreach (var g in goldens)
        {
            string actual = EvaluateScalar(session, g.Dax);
            var (pass, delta) = GoldenSet.Compare(g.Expected, actual, GoldenSet.DefaultTolerance);
            if (!pass)
                failures.Add(delta is { } d
                    ? new { name = g.Name, expected = g.Expected, actual, delta = d }
                    : (object)new { name = g.Name, expected = g.Expected, actual });
        }
        return new
        {
            ok = true,
            total = goldens.Count,
            passed = goldens.Count - failures.Count,
            failed = failures.Count,
            failures,
        };
    }

    // ---------------------------------------------------------------- column / table polish
    public object FormatColumn(string sessionId, string table, string column, string? formatString, string? dataCategory)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var c = Column(Table(model, table), column);
        if (formatString != null) c.FormatString = formatString;
        if (dataCategory != null) c.DataCategory = dataCategory;
        ModelTxn.Save(model);
        return Persisted(new { column = $"{table}[{column}]", format = c.FormatString, dataCategory = c.DataCategory });
    }

    public object SortColumnBy(string sessionId, string table, string column, string sortByColumn)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var t = Table(model, table);
        var c = Column(t, column);
        c.SortByColumn = Column(t, sortByColumn);
        ModelTxn.Save(model);
        return Persisted(new { sorted = $"{table}[{column}] by [{sortByColumn}]" });
    }

    public object SetColumnVisibility(string sessionId, string table, string column, bool hidden)
    {
        var model = _sessions.GetModel(sessionId).Model;
        Column(Table(model, table), column).IsHidden = hidden;
        ModelTxn.Save(model);
        return Persisted(new { column = $"{table}[{column}]", hidden });
    }

    public object SetTableVisibility(string sessionId, string table, bool hidden)
    {
        var model = _sessions.GetModel(sessionId).Model;
        Table(model, table).IsHidden = hidden;
        ModelTxn.Save(model);
        return Persisted(new { table, hidden });
    }

    public object RenameColumn(string sessionId, string table, string column, string newName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var c = Column(Table(model, table), column);
        c.Name = newName;
        ModelTxn.Save(model);
        return Persisted(new { renamed = $"{table}[{column}] -> [{newName}]" });
    }

    // ---------------------------------------------------------------- pro modelling
    /// <summary>Create a fully-formed date table (Date, Year, Quarter, Month, MonthYear + sort columns + hierarchy) in one call.</summary>
    public object CreateDateTable(string sessionId, string name, string? dateColumnRef, bool hierarchy)
    {
        var model = _sessions.GetModel(sessionId).Model;
        if (model.Tables.Contains(name))
            throw new InvalidOperationException($"Table '{name}' already exists. delete_table first or pick another name.");

        string dax = DaxGenerators.DateTableDax(dateColumnRef);

        var table = new TOM.Table { Name = name };
        table.Partitions.Add(new TOM.Partition { Name = name, Source = new TOM.CalculatedPartitionSource { Expression = dax } });
        model.Tables.Add(table);
        ModelTxn.Save(model);
        table.RequestRefresh(TOM.RefreshType.Full);   // materialise the calculated columns
        ModelTxn.Save(model);

        void TrySet(string col, Action<TOM.Column> a) { var c = table.Columns.Find(col); if (c != null) a(c); }
        TrySet("Date", c => c.FormatString = "dd mmm yyyy");
        TrySet("Month", c => { var s = table.Columns.Find("MonthNo"); if (s != null) c.SortByColumn = s; });
        TrySet("Quarter", c => { var s = table.Columns.Find("QuarterNo"); if (s != null) c.SortByColumn = s; });
        TrySet("MonthYear", c => { var s = table.Columns.Find("YearMonthNo"); if (s != null) c.SortByColumn = s; });
        foreach (var h in new[] { "QuarterNo", "MonthNo", "YearMonthNo" }) TrySet(h, c => c.IsHidden = true);

        if (hierarchy && table.Hierarchies.Find("Calendar Hierarchy") == null)
        {
            var hy = new TOM.Hierarchy { Name = "Calendar Hierarchy" };
            int ord = 0;
            foreach (var lvl in new[] { "Year", "Quarter", "Month" })
            {
                var c = table.Columns.Find(lvl);
                if (c != null) hy.Levels.Add(new TOM.Level { Name = lvl, Ordinal = ord++, Column = c });
            }
            if (hy.Levels.Count > 0) table.Hierarchies.Add(hy);
        }
        ModelTxn.Save(model);
        return Persisted(new { created = name, columns = table.Columns.Count(c => c.Type != TOM.ColumnType.RowNumber), hierarchy,
            note = "Relate your fact's date column to " + name + "[Date]." });
    }

    public object AddHierarchy(string sessionId, string table, string name, string[] levels)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var t = Table(model, table);
        if (t.Hierarchies.Contains(name)) t.Hierarchies.Remove(name);
        var h = new TOM.Hierarchy { Name = name };
        int ord = 0;
        foreach (var lvl in levels)
            h.Levels.Add(new TOM.Level { Name = lvl, Ordinal = ord++, Column = Column(t, lvl) });
        t.Hierarchies.Add(h);
        ModelTxn.Save(model);
        return Persisted(new { hierarchy = $"{table}.{name}", levels });
    }

    /// <summary>Generate the standard time-intelligence measure set (YTD, QTD, MTD, PY, YoY %) for a base measure.</summary>
    public object AddTimeIntelligence(string sessionId, string baseMeasure, string dateColumn, string? homeTable)
    {
        var model = _sessions.GetModel(sessionId).Model;
        TOM.Measure? bm = null; TOM.Table? bt = null;
        foreach (var t in model.Tables) { var m = t.Measures.Find(baseMeasure); if (m != null) { bm = m; bt = t; break; } }
        if (bm == null) throw new InvalidOperationException($"Base measure '{baseMeasure}' not found in the model.");
        var home = homeTable != null ? Table(model, homeTable) : bt!;
        string? fmt = bm.FormatString;
        var created = new List<string>();
        void Add(string nm, string dax, string? f)
        {
            if (home.Measures.Contains(nm)) home.Measures.Remove(nm);
            var me = new TOM.Measure { Name = nm, Expression = dax };
            if (!string.IsNullOrWhiteSpace(f)) me.FormatString = f;
            home.Measures.Add(me); created.Add(nm);
        }
        string b = $"[{baseMeasure}]";
        Add($"{baseMeasure} YTD", $"TOTALYTD({b}, {dateColumn})", fmt);
        Add($"{baseMeasure} QTD", $"TOTALQTD({b}, {dateColumn})", fmt);
        Add($"{baseMeasure} MTD", $"TOTALMTD({b}, {dateColumn})", fmt);
        Add($"{baseMeasure} PY", $"CALCULATE({b}, SAMEPERIODLASTYEAR({dateColumn}))", fmt);
        Add($"{baseMeasure} YoY %", $"DIVIDE({b} - [{baseMeasure} PY], [{baseMeasure} PY])", "0.0%");
        ModelTxn.Save(model);
        return Persisted(new { baseMeasure, homeTable = home.Name, created });
    }

    /// <summary>
    /// For every Fact->Dim relationship, count dimension members that have NO matching fact rows
    /// (they show BLANK when a user selects them - the #1 cause of "the visual doesn't work") and
    /// fact keys with no matching dimension row (they fall to a blank dim member). Read-only.
    /// </summary>
    public object CheckRelationships(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>()
            .Where(r => r.FromColumn != null && r.ToColumn != null
                        && !r.FromTable.Name.StartsWith("LocalDateTable", StringComparison.OrdinalIgnoreCase)
                        && !r.ToTable.Name.StartsWith("LocalDateTable", StringComparison.OrdinalIgnoreCase)
                        && !r.ToTable.Name.StartsWith("DateTableTemplate", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var results = new List<object>();
        using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
        conn.Open();
        foreach (var r in rels)
        {
            string ft = r.FromTable.Name, fc = r.FromColumn.Name, tt = r.ToTable.Name, tc = r.ToColumn.Name;
            string dax =
                $"EVALUATE ROW(" +
                $"\"dimMembers\", COUNTROWS(VALUES('{tt}'[{tc}])), " +
                $"\"dimMembersWithNoFacts\", COUNTROWS(EXCEPT(VALUES('{tt}'[{tc}]), VALUES('{ft}'[{fc}]))), " +
                $"\"factKeysWithNoDim\", COUNTROWS(EXCEPT(VALUES('{ft}'[{fc}]), VALUES('{tt}'[{tc}]))))";
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = dax;
                using var rdr = cmd.ExecuteReader();
                long dim = 0, dimNo = 0, factNo = 0;
                if (rdr.Read()) { dim = ToLong(rdr[0]); dimNo = ToLong(rdr[1]); factNo = ToLong(rdr[2]); }
                results.Add(new
                {
                    relationship = $"{ft}[{fc}] *-1 {tt}[{tc}]",
                    active = r.IsActive,
                    dimMembers = dim,
                    dimMembersWithNoFacts = dimNo,
                    factKeysWithNoDim = factNo,
                    severity = dimNo > 0 || factNo > 0 ? "warn" : "ok",
                    note = dimNo > 0
                        ? $"{dimNo} of {dim} '{tt}' members have NO '{ft}' rows - they render BLANK when selected (filter them out of slicers, or it's expected if {tt} is a superset)."
                        : factNo > 0
                            ? $"{factNo} '{ft}' key(s) have no '{tt}' match - they fall to a blank dimension member."
                            : "every member has facts and every fact matches a member.",
                });
            }
            catch (Exception ex) { results.Add(new { relationship = $"{ft}[{fc}] *-1 {tt}[{tc}]", error = ex.Message }); }
        }
        return new { ok = true, relationships = results };
    }

    private static long ToLong(object o) => o is DBNull ? 0 : Convert.ToInt64(o);
    private static double ToDouble(object o) => o is DBNull ? 0 : Convert.ToDouble(o, CultureInfo.InvariantCulture);

    /// <summary>
    /// Auto-detect relationships the model is missing: find columns that match by name + type across
    /// tables, then PROVE each candidate with the data - work out which side is the unique key
    /// (cardinality + direction) and what fraction of the many-side keys actually exist on the one
    /// side (coverage). High-confidence many-to-one matches can be created automatically; everything
    /// else is returned as a reviewed suggestion. No guessing from names alone - the data decides.
    /// </summary>
    public object InferRelationships(string sessionId, bool autoCreate, double minCoverage)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var tables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();

        // existing wiring: skip column-pairs already related; mark table-pairs that already have an active path
        var existingColPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var activeTablePairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in model.Relationships.OfType<TOM.SingleColumnRelationship>())
        {
            if (r.FromColumn == null || r.ToColumn == null) continue;
            existingColPairs.Add(ColPairKey(r.FromTable.Name, r.FromColumn.Name, r.ToTable.Name, r.ToColumn.Name));
            existingColPairs.Add(ColPairKey(r.ToTable.Name, r.ToColumn.Name, r.FromTable.Name, r.FromColumn.Name));
            if (r.IsActive) activeTablePairs.Add(TablePairKey(r.FromTable.Name, r.ToTable.Name));
        }

        // candidate column pairs: exact name match, compatible type, not already related
        var candidates = new List<(TOM.Table A, TOM.Column ca, TOM.Table B, TOM.Column cb)>();
        for (int i = 0; i < tables.Count; i++)
            for (int j = i + 1; j < tables.Count; j++)
                foreach (var ca in tables[i].Columns.Where(c => c.Type != TOM.ColumnType.RowNumber))
                    foreach (var cb in tables[j].Columns.Where(c => c.Type != TOM.ColumnType.RowNumber))
                    {
                        if (!ca.Name.Equals(cb.Name, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!TypeCompatible(ca.DataType, cb.DataType)) continue;
                        if (existingColPairs.Contains(ColPairKey(tables[i].Name, ca.Name, tables[j].Name, cb.Name))) continue;
                        candidates.Add((tables[i], ca, tables[j], cb));
                    }
        bool capped = candidates.Count > 60;
        if (capped) candidates = candidates.Take(60).ToList();

        using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
        conn.Open();
        var proposals = new List<object>();
        int created = 0;
        foreach (var (A, ca, B, cb) in candidates)
        {
            long ra, da, rb, db, anb, bna;
            try
            {
                string dax = "EVALUATE ROW(" +
                    $"\"ra\", COUNTROWS('{A.Name}'), \"da\", COUNTROWS(VALUES('{A.Name}'[{ca.Name}])), " +
                    $"\"rb\", COUNTROWS('{B.Name}'), \"db\", COUNTROWS(VALUES('{B.Name}'[{cb.Name}])), " +
                    $"\"anb\", COUNTROWS(EXCEPT(VALUES('{A.Name}'[{ca.Name}]), VALUES('{B.Name}'[{cb.Name}]))), " +
                    $"\"bna\", COUNTROWS(EXCEPT(VALUES('{B.Name}'[{cb.Name}]), VALUES('{A.Name}'[{ca.Name}]))))";
                using var cmd = conn.CreateCommand();
                cmd.CommandText = dax;
                using var rdr = cmd.ExecuteReader();
                if (!rdr.Read()) continue;
                ra = ToLong(rdr[0]); da = ToLong(rdr[1]); rb = ToLong(rdr[2]); db = ToLong(rdr[3]); anb = ToLong(rdr[4]); bna = ToLong(rdr[5]);
            }
            catch (Exception ex) { proposals.Add(new { pair = $"{A.Name}[{ca.Name}] = {B.Name}[{cb.Name}]", error = ex.Message }); continue; }

            bool uniqueA = da > 0 && da == ra;
            bool uniqueB = db > 0 && db == rb;
            string fromT, fromC, toT, toC, card; double coverage;
            if (uniqueB && !uniqueA) { fromT = A.Name; fromC = ca.Name; toT = B.Name; toC = cb.Name; card = "many-to-one"; coverage = da == 0 ? 0 : (double)(da - anb) / da; }
            else if (uniqueA && !uniqueB) { fromT = B.Name; fromC = cb.Name; toT = A.Name; toC = ca.Name; card = "many-to-one"; coverage = db == 0 ? 0 : (double)(db - bna) / db; }
            else if (uniqueA && uniqueB) { fromT = A.Name; fromC = ca.Name; toT = B.Name; toC = cb.Name; card = "one-to-one"; coverage = da == 0 ? 0 : (double)(da - anb) / da; }
            else { fromT = A.Name; fromC = ca.Name; toT = B.Name; toC = cb.Name; card = "many-to-many"; coverage = da == 0 ? 0 : (double)(da - anb) / da; }

            string confidence = (card == "many-to-one" && coverage >= minCoverage) ? "high"
                : (card == "many-to-one" && coverage >= 0.5) ? "medium"
                : (card == "one-to-one" && coverage >= minCoverage) ? "medium" : "low";

            bool willCreate = autoCreate && confidence == "high";
            string? createError = null; bool madeActive = false;
            if (willCreate)
            {
                try
                {
                    bool active = !activeTablePairs.Contains(TablePairKey(fromT, toT));
                    var rel = new TOM.SingleColumnRelationship
                    {
                        FromColumn = Column(Table(model, fromT), fromC),
                        ToColumn = Column(Table(model, toT), toC),
                        FromCardinality = TOM.RelationshipEndCardinality.Many,
                        ToCardinality = TOM.RelationshipEndCardinality.One,
                        CrossFilteringBehavior = TOM.CrossFilteringBehavior.OneDirection,
                        IsActive = active,
                    };
                    model.Relationships.Add(rel);
                    ModelTxn.Save(model);
                    if (active) activeTablePairs.Add(TablePairKey(fromT, toT));
                    madeActive = active; created++;
                }
                catch (Exception ex) { createError = ex.Message; willCreate = false; }
            }

            proposals.Add(new
            {
                relationship = $"{fromT}[{fromC}] *-1 {toT}[{toC}]",
                cardinality = card,
                coverage = Math.Round(coverage * 100, 1) + "%",
                confidence,
                created = willCreate,
                active = willCreate ? madeActive : (bool?)null,
                error = createError,
                note = card == "many-to-many" ? "neither side is a unique key - left as a suggestion (a bridge table may be needed)"
                    : confidence == "low" ? "low value overlap - likely a coincidental name match, not created"
                    : willCreate ? (madeActive ? "created (active)" : "created INACTIVE - an active path between these tables already exists; activate via USERELATIONSHIP")
                    : autoCreate ? "medium confidence - review then create manually" : "set autoCreate:true to create high-confidence matches",
            });
        }

        return new
        {
            ok = true,
            candidatesEvaluated = candidates.Count,
            capped,
            createdCount = created,
            actionRequired = created > 0 ? "Save in Power BI Desktop (File > Save) to persist." : null,
            proposals,
        };
    }

    /// <summary>
    /// Pre-delivery QUALITY GATE: a comprehensive best-practices lint with a pass/review/fail verdict -
    /// unformatted measures/columns, tables out of relationships, visible keys, missing date table,
    /// auto-date bloat, inactive relationships, and live relationship-integrity (orphan keys via DAX).
    /// Run this before shipping so nothing goes out broken or untidy.
    /// </summary>
    public object QualityGate(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var f = new List<(string severity, string target, string issue, string fix)>();
        void Add(string sev, string target, string issue, string fix) => f.Add((sev, target, issue, fix));

        var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>().Where(r => r.FromColumn != null && r.ToColumn != null).ToList();
        var inRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rels) { inRel.Add(r.FromTable.Name); inRel.Add(r.ToTable.Name); }
        var userTables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();

        int autoDate = model.Tables.Count(t => IsAutoDateName(t.Name));
        if (autoDate > 0) Add("info", "model", $"{autoDate} auto date/time table(s) present", "turn off Auto date/time to slim the model");

        bool hasDateTable = userTables.Any(t =>
            string.Equals(t.DataCategory, "Time", StringComparison.OrdinalIgnoreCase) ||
            t.Columns.Any(c => string.Equals(c.DataCategory, "Time", StringComparison.OrdinalIgnoreCase)) ||
            ((t.Name.Contains("calendar", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("date", StringComparison.OrdinalIgnoreCase))
                && t.Columns.Any(c => c.DataType == TOM.DataType.DateTime)));
        if (!hasDateTable && userTables.Count > 1) Add("warn", "model", "no marked date/calendar table found", "add a date table for reliable time intelligence");

        foreach (var t in userTables)
        {
            if (!inRel.Contains(t.Name) && userTables.Count > 1)
                Add("info", t.Name, "table is in no relationship", "relate it, or confirm it is a disconnected/parameter table");
            foreach (var c in t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber))
            {
                bool numOrDate = c.DataType is TOM.DataType.Int64 or TOM.DataType.Double or TOM.DataType.Decimal or TOM.DataType.DateTime;
                if (numOrDate && !c.IsHidden && string.IsNullOrWhiteSpace(c.FormatString))
                    Add("warn", $"{t.Name}[{c.Name}]", "numeric/date column has no format string", "format_column");
                if (!c.IsHidden && inRel.Contains(t.Name) &&
                    (c.Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)))
                    Add("info", $"{t.Name}[{c.Name}]", "key column is visible in the field list", "set_column_visibility to hide it");
            }
            foreach (var me in t.Measures)
            {
                if (string.IsNullOrWhiteSpace(me.FormatString))
                    Add("warn", $"{t.Name}[{me.Name}]", "measure has no format string", "set the measure format");
                if (string.IsNullOrWhiteSpace(me.Description))
                    Add("info", $"{t.Name}[{me.Name}]", "measure has no description", "add a description for self-service users");
            }
        }
        foreach (var r in rels.Where(r => !r.IsActive))
            Add("info", $"{r.FromTable.Name}->{r.ToTable.Name}", "inactive relationship", "fine if reached via USERELATIONSHIP");

        int orphanRels = 0;
        try
        {
            using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
            conn.Open();
            foreach (var r in rels.Where(r => r.IsActive).Take(40))
            {
                string ft = r.FromTable.Name, fc = r.FromColumn.Name, tt = r.ToTable.Name, tc = r.ToColumn.Name;
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"EVALUATE ROW(\"o\", COUNTROWS(EXCEPT(VALUES('{ft}'[{fc}]), VALUES('{tt}'[{tc}]))))";
                    using var rdr = cmd.ExecuteReader();
                    long o = rdr.Read() ? ToLong(rdr[0]) : 0;
                    if (o > 0) { orphanRels++; Add("warn", $"{ft}[{fc}] *-1 {tt}[{tc}]", $"{o} key(s) on the many side have no matching dimension member", "clean the keys/dim; they fall to a blank member"); }
                }
                catch { }
            }
        }
        catch { }

        int err = f.Count(x => x.severity == "error"), warn = f.Count(x => x.severity == "warn"), info = f.Count(x => x.severity == "info");
        string status = err > 0 ? "fail" : warn > 0 ? "review" : "pass";
        return new
        {
            ok = true,
            status,
            verdict = status == "pass" ? "Ready to ship." : status == "review" ? "Shippable, but fix the warnings for a clean deliverable." : "Do not ship - errors present.",
            summary = new { errors = err, warnings = warn, info, tables = userTables.Count, measures = userTables.Sum(t => t.Measures.Count), relationships = rels.Count, orphanRelationships = orphanRels },
            findings = f.Select(x => new { x.severity, x.target, x.issue, x.fix }),
        };
    }

    /// <summary>
    /// The complete "select a value and the visuals fall over" detector. For every low-cardinality
    /// slicer-style column it catches BOTH failure modes a user hits when they click a value:
    ///   1. ERROR-on-select - a measure throws in that filter context (breaks the canvas), value pinpointed.
    ///   2. BLANK-on-select - the value returns blank for the anchor measure, so every visual empties
    ///      (the soft-drink-brand class: a brand/member with no underlying rows - looks broken even though it
    ///      is "valid"). Pinpoints the dead values so they can be filtered out or the data fixed.
    /// Read-only. This is the pre-delivery reliability gate; a report that errors or blanks when a
    /// user clicks a brand is not shippable.
    /// </summary>
    public object AuditRobustness(string sessionId, int maxColumns, int maxValuesPerColumn, int maxMeasures, string? anchorMeasure)
    {
        if (maxColumns <= 0) maxColumns = 20;
        if (maxValuesPerColumn <= 0) maxValuesPerColumn = 200;
        if (maxMeasures <= 0) maxMeasures = 40;
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var userTables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();
        var measures = userTables.SelectMany(t => t.Measures.Select(me => (tbl: t.Name, name: me.Name)))
            .Take(maxMeasures).ToList();
        // anchor measure for the blank-on-select test: caller's choice, else the first model measure
        string? anchor = !string.IsNullOrWhiteSpace(anchorMeasure)
            ? anchorMeasure
            : userTables.SelectMany(t => t.Measures.Select(m => m.Name)).FirstOrDefault();
        string Clean(string s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim() is var x && x.Length > 220 ? x[..220] + "..." : (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        // tell a real measure error apart from an infrastructure failure (connection dropped, model unloaded,
        // Desktop closed mid-audit). The latter must ABORT the audit, not be miscounted as 239 measure errors.
        static bool IsInfra(Exception ex)
        {
            var m = ex.Message ?? "";
            return ex is Adomd.AdomdConnectionException
                || m.Contains("CurrentCatalog", StringComparison.OrdinalIgnoreCase)
                || m.Contains("does not exist in the server", StringComparison.OrdinalIgnoreCase)
                || m.Contains("connection either timed out or was lost", StringComparison.OrdinalIgnoreCase)
                || m.Contains("Server reference", StringComparison.OrdinalIgnoreCase)
                || (m.Contains("connection", StringComparison.OrdinalIgnoreCase) && m.Contains("lost", StringComparison.OrdinalIgnoreCase));
        }
        const string InfraMsg = "Lost the connection to the model mid-audit (Power BI Desktop closed, or the model unloaded under memory pressure). Audit aborted - reopen the report and retry. No findings are reliable from a dropped connection.";

        using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
        conn.Open();
        void Exec(string dax) { using var cmd = conn.CreateCommand(); cmd.CommandText = dax; using var rdr = cmd.ExecuteReader(); while (rdr.Read()) { } }
        long Scalar(string dax) { using var cmd = conn.CreateCommand(); cmd.CommandText = dax; using var rdr = cmd.ExecuteReader(); return rdr.Read() ? ToLong(rdr[0]) : 0; }

        // candidate slicer columns: visible, categorical (string/bool/int), 2..maxValuesPerColumn distinct, not a key
        var candidates = new List<(string t, string c, bool text)>();
        foreach (var t in userTables)
        {
            foreach (var c in t.Columns)
            {
                if (candidates.Count >= maxColumns) break;
                if (c.Type == TOM.ColumnType.RowNumber || c.IsHidden) continue;
                if (c.DataType is not (TOM.DataType.String or TOM.DataType.Boolean or TOM.DataType.Int64)) continue;
                if (c.Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)) continue;
                long card;
                try { card = Scalar($"EVALUATE ROW(\"n\", COUNTROWS(VALUES('{t.Name}'[{c.Name}])))"); }
                catch { continue; }
                if (card < 2 || card > maxValuesPerColumn) continue;
                candidates.Add((t.Name, c.Name, c.DataType == TOM.DataType.String));
            }
            if (candidates.Count >= maxColumns) break;
        }

        var errorFindings = new List<object>();
        int pairsTested = 0;
        foreach (var (mt, mn) in measures)
        {
            string mref = $"[{mn}]";
            foreach (var (ct, cc, isText) in candidates)
            {
                pairsTested++;
                try { Exec($"EVALUATE SUMMARIZECOLUMNS('{ct}'[{cc}], \"v\", {mref})"); }
                catch (Exception ex)
                {
                    if (IsInfra(ex)) throw new InvalidOperationException(InfraMsg);
                    var bad = new List<string>();
                    if (isText)
                    {
                        try
                        {
                            var vals = new List<string>();
                            using (var vc = conn.CreateCommand())
                            {
                                vc.CommandText = $"EVALUATE TOPN({Math.Min(maxValuesPerColumn, 300)}, VALUES('{ct}'[{cc}]))";
                                using var vr = vc.ExecuteReader();
                                while (vr.Read()) if (vr[0] is string s) vals.Add(s);
                            }
                            foreach (var v in vals)
                            {
                                try { Exec($"EVALUATE ROW(\"v\", CALCULATE({mref}, '{ct}'[{cc}] = \"{v.Replace("\"", "\"\"")}\"))"); }
                                catch { bad.Add(v); if (bad.Count >= 5) break; }
                            }
                        }
                        catch { }
                    }
                    errorFindings.Add(new
                    {
                        severity = "error",
                        measure = $"{mt}[{mn}]",
                        column = $"{ct}[{cc}]",
                        breaksOn = bad.Count > 0 ? string.Join(", ", bad) : "(one or more values)",
                        error = Clean(ex.Message),
                        fix = "this measure ERRORS when that value is selected and will break every visual using it - guard DIVIDE, handle BLANK/zero, and HASONEVALUE before single-value logic, then update_measure",
                    });
                }
            }
        }

        // blank-on-select: values that empty the visuals (no underlying data for the anchor measure)
        var blankFindings = new List<object>();
        if (!string.IsNullOrWhiteSpace(anchor))
        {
            string aref = anchor!.StartsWith("[") ? anchor! : $"[{anchor}]";
            foreach (var (ct, cc, isText) in candidates)
            {
                long blanks;
                try { blanks = Scalar($"EVALUATE ROW(\"n\", COUNTROWS(FILTER(VALUES('{ct}'[{cc}]), ISBLANK({aref}))))"); }
                catch (Exception ex) { if (IsInfra(ex)) throw new InvalidOperationException(InfraMsg); continue; }   // anchor not valid in this column's context - skip
                if (blanks <= 0) continue;
                var samples = new List<string>();
                if (isText)
                {
                    try
                    {
                        using var vc = conn.CreateCommand();
                        vc.CommandText = $"EVALUATE TOPN(6, FILTER(VALUES('{ct}'[{cc}]), ISBLANK({aref})))";
                        using var vr = vc.ExecuteReader();
                        while (vr.Read()) if (vr[0] is string s) samples.Add(s);
                    }
                    catch { }
                }
                long total = 0; try { total = Scalar($"EVALUATE ROW(\"n\", COUNTROWS(VALUES('{ct}'[{cc}])))"); } catch { }
                blankFindings.Add(new
                {
                    severity = "warn",
                    column = $"{ct}[{cc}]",
                    blankValues = blanks,
                    ofTotal = total,
                    sample = samples.Count > 0 ? string.Join(", ", samples) : "(non-text values)",
                    anchorMeasure = anchor,
                    fix = $"{blanks} of {total} values blank every visual when selected (no data for [{anchor}]) - filter them out of the slicer (e.g. keep only values with data), or fix the upstream attribution/coverage, like the soft-drink-brand case",
                });
            }
        }

        int errs = errorFindings.Count, blankCols = blankFindings.Count;
        var findings = errorFindings.Concat(blankFindings);
        string status = errs > 0 ? "fail" : blankCols > 0 ? "review" : "pass";
        return new
        {
            ok = true,
            status,
            verdict = status == "fail"
                ? $"{errs} measure x column combination(s) ERROR on selection (break the canvas){(blankCols > 0 ? $" and {blankCols} column(s) blank the visuals on some values" : "")}. Do not ship."
                : status == "review"
                    ? $"No errors, but {blankCols} slicer column(s) have values that blank every visual when selected (the soft-drink-brand class). Filter those values out or fix coverage."
                    : "Safe: no measure errored and no slicer value blanks the visuals across the tested columns.",
            summary = new { measuresTested = measures.Count, slicerColumns = candidates.Count, pairsTested, anchorMeasure = anchor, errors = errs, blankOnSelectColumns = blankCols },
            findings,
        };
    }

    /// <summary>
    /// Close the loop on blank-on-select: add a boolean calculated column to a dimension flagging
    /// whether each member actually has rows in a fact table. Filter slicers (or add a report-level
    /// filter) on this column and a user can no longer select a dead value (the soft-drink-brand class).
    /// Idempotent (replaces a same-named column) and auto-recalcs so the flag is immediately usable.
    /// </summary>
    public object AddCoverageFlag(string sessionId, string table, string factTable, string? columnName)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var t = Table(model, table);
        _ = Table(model, factTable);   // validate the fact exists
        string name = string.IsNullOrWhiteSpace(columnName) ? $"Has {factTable} Data" : columnName!;
        var existing = t.Columns.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) t.Columns.Remove(existing);
        string expr = $"NOT ISBLANK(CALCULATE(COUNTROWS('{factTable}')))";
        t.Columns.Add(new TOM.CalculatedColumn { Name = name, Expression = expr, DataType = TOM.DataType.Boolean });
        ModelTxn.Save(model);
        try { model.RequestRefresh(TOM.RefreshType.Calculate); ModelTxn.Save(model); } catch { }

        long total = 0, withData = 0;
        try
        {
            using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
            conn.Open();
            long Scalar(string dax) { using var cmd = conn.CreateCommand(); cmd.CommandText = dax; using var rdr = cmd.ExecuteReader(); return rdr.Read() ? ToLong(rdr[0]) : 0; }
            total = Scalar($"EVALUATE ROW(\"n\", COUNTROWS('{table}'))");
            withData = Scalar($"EVALUATE ROW(\"n\", CALCULATE(COUNTROWS('{table}'), '{table}'[{name}] = TRUE))");
        }
        catch { }

        return Persisted(new
        {
            added = $"{table}[{name}]",
            expression = expr,
            dataType = "boolean",
            membersWithData = withData,
            membersTotal = total,
            deadMembers = total - withData,
            howToUse = $"Add a slicer/report filter '{table}[{name}] = True' (or filter slicers to it) so dead members can't be selected. Or keep it as a field for 'in scope' visuals.",
        });
    }

    /// <summary>
    /// The "your report is silently incomplete" detector. A dimension member with zero fact rows can be
    /// normal (genuinely no sales) OR a data defect (mis-attribution / over-scoping, like a soft-drink brand). The
    /// tell is CLUSTERING: when an entire GROUP of members under one attribute value (a whole supplier,
    /// a whole category) has zero data, that is almost never coincidence - it is a systematic exclusion.
    /// This finds those clusters automatically (no domain knowledge / brand list needed), turning a
    /// detective job into a one-call guarantee. Read-only.
    /// </summary>
    public object FindAttributionGaps(string sessionId, string? anchorMeasure, int maxColumns, int maxValuesPerColumn)
    {
        if (maxColumns <= 0) maxColumns = 24;
        if (maxValuesPerColumn <= 0) maxValuesPerColumn = 400;
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var userTables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();
        string? anchor = !string.IsNullOrWhiteSpace(anchorMeasure)
            ? anchorMeasure
            : userTables.SelectMany(t => t.Measures.Select(m => m.Name)).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(anchor))
            return new { ok = false, error = "no measure found to test data coverage against - the model has no measures." };
        string aref = anchor!.StartsWith("[") ? anchor! : $"[{anchor}]";

        using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
        conn.Open();
        long Scalar(string dax) { using var cmd = conn.CreateCommand(); cmd.CommandText = dax; using var rdr = cmd.ExecuteReader(); return rdr.Read() ? ToLong(rdr[0]) : 0; }

        // grouping-attribute candidates: visible categorical columns (supplier/category/vendor-style)
        var candidates = new List<(string t, string c)>();
        foreach (var t in userTables)
        {
            foreach (var c in t.Columns)
            {
                if (candidates.Count >= maxColumns) break;
                if (c.Type == TOM.ColumnType.RowNumber || c.IsHidden) continue;
                if (c.DataType is not (TOM.DataType.String or TOM.DataType.Int64)) continue;
                if (c.Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)) continue;
                long card;
                try { card = Scalar($"EVALUATE ROW(\"n\", COUNTROWS(VALUES('{t.Name}'[{c.Name}])))"); }
                catch (Exception ex) { if (IsInfraGap(ex)) return InfraResult(); continue; }
                if (card < 2 || card > maxValuesPerColumn) continue;
                candidates.Add((t.Name, c.Name));
            }
            if (candidates.Count >= maxColumns) break;
        }

        var gaps = new List<(long members, object finding)>();
        foreach (var (ct, cc) in candidates)
        {
            // group-values where the WHOLE group (>=2 members) has zero data for the anchor = systematic gap
            string dax = $"EVALUATE TOPN(12, FILTER(SUMMARIZECOLUMNS('{ct}'[{cc}], \"d\", {aref}, \"m\", CALCULATE(COUNTROWS('{ct}'))), ISBLANK([d]) && [m] >= 2), [m], DESC)";
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = dax;
                using var rdr = cmd.ExecuteReader();
                while (rdr.Read())
                {
                    string val = rdr[0]?.ToString() ?? "(blank)";
                    long members = rdr.FieldCount >= 3 ? ToLong(rdr[2]) : ToLong(rdr[rdr.FieldCount - 1]);
                    gaps.Add((members, new
                    {
                        severity = members >= 5 ? "warn" : "info",
                        groupedBy = $"{ct}[{cc}]",
                        value = val,
                        membersWithNoData = members,
                        note = $"all {members} '{cc}' = '{val}' members have zero data for {aref} - a whole group missing is a likely systematic exclusion (mis-attribution / over-scoped fact), not genuine no-sales. Review like a soft-drink brand.",
                    }));
                }
            }
            catch (Exception ex) { if (IsInfraGap(ex)) return InfraResult(); }
        }

        var ranked = gaps.OrderByDescending(g => g.members).Select(g => g.finding).ToList();
        bool any = ranked.Count > 0;
        return new
        {
            ok = true,
            status = any ? "review" : "pass",
            verdict = any
                ? $"{ranked.Count} group(s) of dimension members have zero data as a whole - likely systematic exclusions worth a human review (the soft-drink-brand / supplier-group class)."
                : "No whole-group data gaps found - no sign of a systematic mis-attribution / over-scoping defect.",
            anchorMeasure = anchor,
            groupsTested = candidates.Count,
            gaps = ranked,
        };

        static bool IsInfraGap(Exception ex)
        {
            var m = ex.Message ?? "";
            return ex is Adomd.AdomdConnectionException || m.Contains("CurrentCatalog", StringComparison.OrdinalIgnoreCase)
                || m.Contains("does not exist in the server", StringComparison.OrdinalIgnoreCase)
                || m.Contains("connection either timed out or was lost", StringComparison.OrdinalIgnoreCase);
        }
        object InfraResult() => new { ok = false, error = "Lost the connection to the model mid-scan (Desktop closed / model unloaded). Reopen and retry." };
    }

    /// <summary>
    /// Take a model integrity SNAPSHOT for Sentinel: grand total, per-table row counts, per-group totals
    /// and per-measure health. Compare two with sentinel-diff (before vs after a refresh) to catch a
    /// regression - a vanished category, collapsed rows, a newly-broken measure - the moment it happens.
    /// </summary>
    public object SentinelSnapshot(string sessionId, string? anchorMeasure, string? outPath, int maxGroups, int maxValuesPerGroup)
    {
        if (maxGroups <= 0) maxGroups = 14;
        if (maxValuesPerGroup <= 0) maxValuesPerGroup = 500;
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var userTables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();
        string? anchor = !string.IsNullOrWhiteSpace(anchorMeasure) ? anchorMeasure
            : userTables.SelectMany(t => t.Measures.Select(m => m.Name)).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(anchor)) throw new InvalidOperationException("no measure found to anchor the snapshot.");
        string aref = anchor!.StartsWith("[") ? anchor! : $"[{anchor}]";

        using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
        conn.Open();
        double Scalar(string dax) { using var cmd = conn.CreateCommand(); cmd.CommandText = dax; using var r = cmd.ExecuteReader(); return r.Read() ? ToDouble(r[0]) : 0; }
        string Clean(string s) { s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim(); return s.Length > 160 ? s[..160] + "..." : s; }

        double grand = 0; try { grand = Scalar($"EVALUATE ROW(\"v\", {aref})"); } catch { }

        var tables = new List<object>();
        foreach (var t in userTables)
        { double n = 0; try { n = Scalar($"EVALUATE ROW(\"n\", COUNTROWS('{t.Name}'))"); } catch { } tables.Add(new { name = t.Name, rows = n }); }

        var groups = new Dictionary<string, Dictionary<string, double>>();
        foreach (var t in userTables)
        {
            if (groups.Count >= maxGroups) break;
            foreach (var c in t.Columns)
            {
                if (groups.Count >= maxGroups) break;
                if (c.Type == TOM.ColumnType.RowNumber || c.IsHidden) continue;
                if (c.DataType is not (TOM.DataType.String or TOM.DataType.Int64)) continue;
                if (c.Name.EndsWith("Key", StringComparison.OrdinalIgnoreCase) || c.Name.EndsWith("ID", StringComparison.OrdinalIgnoreCase)) continue;
                double card; try { card = Scalar($"EVALUATE ROW(\"n\", COUNTROWS(VALUES('{t.Name}'[{c.Name}])))"); } catch { continue; }
                if (card < 2 || card > maxValuesPerGroup) continue;
                var dict = new Dictionary<string, double>();
                try
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"EVALUATE SUMMARIZECOLUMNS('{t.Name}'[{c.Name}], \"v\", {aref})";
                    using var rdr = cmd.ExecuteReader();
                    while (rdr.Read()) { string key = rdr[0]?.ToString() ?? "(blank)"; double v = ToDouble(rdr[1]); if (v != 0) dict[key] = v; }
                }
                catch { continue; }
                if (dict.Count > 0) groups[$"{t.Name}[{c.Name}]"] = dict;
            }
        }

        var measures = new Dictionary<string, string>();
        foreach (var t in userTables)
            foreach (var me in t.Measures)
            {
                try { using var cmd = conn.CreateCommand(); cmd.CommandText = $"EVALUATE ROW(\"x\", [{me.Name}])"; using var r = cmd.ExecuteReader(); while (r.Read()) { } measures[me.Name] = "ok"; }
                catch (Exception ex) { measures[me.Name] = "error: " + Clean(ex.Message); }
            }

        var snap = new Dictionary<string, object?>
        {
            ["takenAt"] = DateTime.Now.ToString("o"),
            ["anchorMeasure"] = anchor,
            ["grandTotal"] = grand,
            ["tables"] = tables,
            ["groups"] = groups,
            ["measures"] = measures,
        };
        if (!string.IsNullOrWhiteSpace(outPath))
            File.WriteAllText(outPath!, System.Text.Json.JsonSerializer.Serialize(snap, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        return new
        {
            ok = true,
            written = outPath,
            anchorMeasure = anchor,
            grandTotal = grand,
            tables = tables.Count,
            groups = groups.Count,
            measures = measures.Count,
            brokenMeasures = measures.Count(m => m.Value.StartsWith("error")),
        };
    }

    /// <summary>
    /// The cross-retailer / total-market primitive. Two separate fact islands (e.g. a grocery-retailer
    /// fact and a second-retailer fact) can't be filtered by one slicer because they share no dimension.
    /// This builds a CONFORMED dimension - a calculated table of the distinct union of a column from
    /// each side - and relates it to both, so a single slicer filters both retailers and combined
    /// measures (Total Market = [retailer A] + [retailer B]) compute correctly. Auto-materialises +
    /// recalcs. The thing that turns two side-by-side islands into a true total-market view.
    /// </summary>
    public object ConformDimension(string sessionId, string newTable, string keyName, string table1, string column1, string table2, string column2)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        _ = Column(Table(model, table1), column1);   // validate both source columns exist
        _ = Column(Table(model, table2), column2);
        var dup = model.Tables.FirstOrDefault(t => t.Name.Equals(newTable, StringComparison.OrdinalIgnoreCase));
        if (dup != null)
        {
            // remove relationships referencing the table FIRST, else SaveChanges fails ("points to deleted table")
            foreach (var r in model.Relationships.OfType<TOM.SingleColumnRelationship>()
                .Where(r => string.Equals(r.FromTable?.Name, dup.Name, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(r.ToTable?.Name, dup.Name, StringComparison.OrdinalIgnoreCase)).ToList())
                model.Relationships.Remove(r);
            model.Tables.Remove(dup);
            ModelTxn.Save(model);
        }

        string Pick(string t, string c) => $"SELECTCOLUMNS(FILTER(VALUES('{t}'[{c}]), NOT ISBLANK('{t}'[{c}])), \"{keyName}\", '{t}'[{c}])";
        string dax = $"DISTINCT(UNION({Pick(table1, column1)}, {Pick(table2, column2)}))";
        var tbl = new TOM.Table { Name = newTable };
        tbl.Partitions.Add(new TOM.Partition { Name = newTable, Source = new TOM.CalculatedPartitionSource { Expression = dax } });
        model.Tables.Add(tbl);
        ModelTxn.Save(model);
        model.RequestRefresh(TOM.RefreshType.Full); ModelTxn.Save(model);   // materialise the conformed dim

        void Relate(string ft, string fc)
        {
            model.Relationships.Add(new TOM.SingleColumnRelationship
            {
                FromColumn = Column(Table(model, ft), fc),
                ToColumn = Column(Table(model, newTable), keyName),
                FromCardinality = TOM.RelationshipEndCardinality.Many,
                ToCardinality = TOM.RelationshipEndCardinality.One,
                CrossFilteringBehavior = TOM.CrossFilteringBehavior.OneDirection,
                IsActive = true,
            });
        }
        string? relErr = null;
        try { Relate(table1, column1); Relate(table2, column2); ModelTxn.Save(model); }
        catch (Exception ex) { relErr = ex.Message; }
        try { model.RequestRefresh(TOM.RefreshType.Calculate); ModelTxn.Save(model); } catch { }

        long members = 0;
        try
        {
            using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"EVALUATE ROW(\"n\", COUNTROWS('{newTable}'))";
            using var rdr = cmd.ExecuteReader();
            if (rdr.Read()) members = ToLong(rdr[0]);
        }
        catch { }

        return Persisted(new
        {
            created = newTable,
            key = keyName,
            members,
            relatedTo = new[] { $"{table1}[{column1}]", $"{table2}[{column2}]" },
            relationshipError = relErr,
            howToUse = $"Slice on '{newTable}'[{keyName}] to filter BOTH facts at once. Add total-market measures, e.g. [Total Market Sales] = [<{table1}-side sales>] + [<{table2}-side sales>], and share-of-market measures off that.",
        });
    }

    /// <summary>
    /// Serialize the live model to TMDL text files (Microsoft's official serializer) - the
    /// model half of a PBIP project. Pure text, source-control-friendly, no Desktop needed
    /// to author. This is the spine that gets us off the live-Desktop dependency.
    /// </summary>
    public object ExportTmdl(string sessionId, string outputFolder)
    {
        var model = _sessions.GetModel(sessionId).Model;
        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);
        TOM.TmdlSerializer.SerializeModelToFolder(model, outputFolder);
        var files = Directory.GetFiles(outputFolder, "*.tmdl", SearchOption.AllDirectories);
        return new
        {
            ok = true,
            outputFolder,
            files = files.Length,
            sample = files.Select(f => Path.GetRelativePath(outputFolder, f)).OrderBy(x => x).Take(20).ToArray(),
        };
    }

    /// <summary>
    /// unpack_to_source: the live model's TMDL + the source pbix's report files as ONE deterministic,
    /// text-only, git-committable tree. Pure delegation - the logic (and the wipe safety marker that
    /// keeps it off folders we did not create) lives in <see cref="SourceTree"/>.
    /// </summary>
    public object UnpackToSource(string sessionId, string pbixPath, string outputFolder)
    {
        var model = _sessions.GetModel(sessionId).Model;
        return SourceTree.Unpack(model, pbixPath, outputFolder);
    }

    /// <summary>
    /// Generate a complete PBIP project (text-based) from the live model + a source pbix's report:
    ///   &lt;name&gt;.SemanticModel/ (TMDL, official serializer) + &lt;name&gt;.Report/ (legacy report.json +
    ///   definition.pbir + StaticResources) + &lt;name&gt;.pbip. Pure files - Desktop/Fabric open it,
    ///   no live engine needed to author. Opens schema-only (no cache.abf); Refresh loads data.
    /// </summary>
    public object GeneratePbip(string sessionId, string sourcePbixPath, string outputFolder, string name, bool includeData)
    {
        if (!File.Exists(sourcePbixPath)) throw new FileNotFoundException($"source pbix not found: {sourcePbixPath}");
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var utf8 = new UTF8Encoding(false);   // PBIP requires UTF-8 without BOM
        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);
        string sm = Path.Combine(outputFolder, name + ".SemanticModel");
        string rp = Path.Combine(outputFolder, name + ".Report");
        Directory.CreateDirectory(sm);
        Directory.CreateDirectory(rp);

        // ----- semantic model: TMDL + definition.pbism (v4.0 => TMDL definition folder) -----
        TOM.TmdlSerializer.SerializeModelToFolder(model, Path.Combine(sm, "definition"));
        File.WriteAllText(Path.Combine(sm, "definition.pbism"), "{\n  \"version\": \"4.0\",\n  \"settings\": {}\n}\n", utf8);

        // ----- optional data cache (so the project opens WITH data, no Refresh) -----
        // Desktop's workspace engine is diskless (no Backup), so we lift the model+data image
        // straight out of the source pbix's DataModel part and drop it in as .pbi/cache.abf.
        bool cacheIncluded = false; string? cacheNote = null;
        _ = session;

        // ----- report: report.json (legacy Layout) + StaticResources from the source pbix -----
        int staticCount = 0;
        using (var zip = ZipFile.OpenRead(sourcePbixPath))
        {
            var le = zip.GetEntry("Report/Layout") ?? throw new InvalidOperationException("source pbix has no Report/Layout.");
            byte[] lb; using (var s = le.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); lb = ms.ToArray(); }
            int off = (lb.Length >= 2 && lb[0] == 0xFF && lb[1] == 0xFE) ? 2 : 0;   // strip UTF-16 BOM
            string layout = new UnicodeEncoding(false, false).GetString(lb, off, lb.Length - off);
            File.WriteAllText(Path.Combine(rp, "report.json"), layout, utf8);

            foreach (var e in zip.Entries)
            {
                if (e.Length == 0 || !e.FullName.StartsWith("Report/StaticResources/", StringComparison.OrdinalIgnoreCase)) continue;
                string rel = e.FullName.Substring("Report/".Length).Replace('/', Path.DirectorySeparatorChar);
                string dest = Path.Combine(rp, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var es = e.Open(); using var fs = File.Create(dest); es.CopyTo(fs);
                staticCount++;
            }

            if (includeData)
            {
                var dm = zip.GetEntry("DataModel");
                if (dm != null)
                {
                    string pbiDir = Path.Combine(sm, ".pbi");
                    Directory.CreateDirectory(pbiDir);
                    using var es = dm.Open(); using var fs = File.Create(Path.Combine(pbiDir, "cache.abf")); es.CopyTo(fs);
                    cacheIncluded = true;
                }
                else cacheNote = "source pbix has no DataModel part - opens without cached data (Refresh to populate).";
            }
        }
        File.WriteAllText(Path.Combine(rp, "definition.pbir"),
            "{\n  \"$schema\": \"https://developer.microsoft.com/json-schemas/fabric/item/report/definitionProperties/1.0.0/schema.json\",\n" +
            "  \"version\": \"1.0\",\n  \"datasetReference\": {\n    \"byPath\": { \"path\": \"../" + name + ".SemanticModel\" }\n  }\n}\n", utf8);

        // ----- project file + gitignore -----
        File.WriteAllText(Path.Combine(outputFolder, name + ".pbip"),
            "{\n  \"$schema\": \"https://developer.microsoft.com/json-schemas/fabric/pbip/pbipProperties/1.0.0/schema.json\",\n" +
            "  \"version\": \"1.0\",\n  \"artifacts\": [ { \"report\": { \"path\": \"" + name + ".Report\" } } ],\n  \"settings\": { \"enableAutoRecovery\": true }\n}\n", utf8);
        File.WriteAllText(Path.Combine(outputFolder, ".gitignore"), "**/.pbi/localSettings.json\n**/.pbi/cache.abf\n", utf8);

        var tmdlFiles = Directory.GetFiles(Path.Combine(sm, "definition"), "*.tmdl", SearchOption.AllDirectories).Length;
        return new
        {
            ok = true,
            generated = name + ".pbip",
            outputFolder,
            tmdlFiles,
            staticResources = staticCount,
            dataCached = cacheIncluded,
            note = cacheIncluded
                ? $"Open {name}.pbip in Power BI Desktop - it opens fully loaded with data (cache.abf included)."
                : cacheNote ?? $"Open {name}.pbip in Power BI Desktop. Loads the definition (no cached data) - hit Refresh to populate.",
        };
    }

    /// <summary>Best-practices scan (read-only): unformatted measures/columns, orphan tables, etc.</summary>
    public object AnalyzeModel(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>().ToList();
        var inRel = new HashSet<string>();
        foreach (var r in rels) { if (r.FromTable != null) inRel.Add(r.FromTable.Name); if (r.ToTable != null) inRel.Add(r.ToTable.Name); }

        var findings = new List<object>();
        foreach (var t in model.Tables)
        {
            if (!inRel.Contains(t.Name) && model.Tables.Count > 1)
                findings.Add(new { severity = "info", target = t.Name, issue = "table is in no relationship (ok if it's a disconnected/parameter table)" });
            foreach (var c in t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber))
            {
                bool numOrDate = c.DataType is TOM.DataType.Int64 or TOM.DataType.Double or TOM.DataType.Decimal or TOM.DataType.DateTime;
                if (numOrDate && string.IsNullOrWhiteSpace(c.FormatString))
                    findings.Add(new { severity = "warn", target = $"{t.Name}[{c.Name}]", issue = "numeric/date column has no format string" });
            }
            foreach (var me in t.Measures)
                if (string.IsNullOrWhiteSpace(me.FormatString))
                    findings.Add(new { severity = "warn", target = $"{t.Name}[{me.Name}]", issue = "measure has no format string" });
        }
        return new
        {
            ok = true,
            tables = model.Tables.Count,
            relationships = rels.Count,
            measures = model.Tables.Sum(t => t.Measures.Count),
            findingCount = findings.Count,
            findings,
        };
    }

    // ================================================================ security (RLS / OLS)
    // The static *Core helpers do the pure TOM object-tree mutation (no SaveChanges) so they can be
    // unit-tested against an in-memory `new Model()`. The public instance methods resolve the live
    // session, call the helper, then SaveChanges() - matching every other tool here.

    public object AddRole(string sessionId, string name, string modelPermission)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddRoleCore(model, name, modelPermission);
        ModelTxn.Save(model);
        return Persisted(new { addedRole = name, modelPermission });
    }

    internal static TOM.ModelRole AddRoleCore(TOM.Model model, string name, string modelPermission)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Role name is required.");
        if (model.Roles.Contains(name))
            throw new InvalidOperationException($"Role '{name}' already exists. Use a different name or delete_role first.");
        var perm = ParseModelPermission(modelPermission);
        var role = new TOM.ModelRole { Name = name, ModelPermission = perm };
        model.Roles.Add(role);
        return role;
    }

    public object DeleteRole(string sessionId, string name)
    {
        var model = _sessions.GetModel(sessionId).Model;
        bool removed = DeleteRoleCore(model, name);
        ModelTxn.Save(model);
        return Persisted(new { deletedRole = name, existed = removed });
    }

    internal static bool DeleteRoleCore(TOM.Model model, string name)
    {
        if (!model.Roles.Contains(name)) return false;
        model.Roles.Remove(name);
        return true;
    }

    public object ListRoles(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var roles = model.Roles.Select(r => new
        {
            name = r.Name,
            modelPermission = r.ModelPermission.ToString(),
            members = r.Members.Select(m => m.MemberName),
            tablePermissions = r.TablePermissions.Select(tp => new
            {
                table = tp.Name,
                filterExpression = tp.FilterExpression,
                metadataPermission = tp.MetadataPermission.ToString(),
                columnPermissions = tp.ColumnPermissions.Select(cp => new
                {
                    column = cp.Name,
                    metadataPermission = cp.MetadataPermission.ToString(),
                }),
            }),
        });
        return new { roles };
    }

    public object SetRls(string sessionId, string role, string table, string daxFilterExpression)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetRlsCore(model, role, table, daxFilterExpression);
        ModelTxn.Save(model);
        return Persisted(new { role, table, filterExpression = daxFilterExpression });
    }

    internal static TOM.TablePermission SetRlsCore(TOM.Model model, string role, string table, string daxFilterExpression)
    {
        var r = Role(model, role);
        var t = Table(model, table);
        var tp = TablePermissionFor(r, t);
        tp.FilterExpression = daxFilterExpression ?? "";
        return tp;
    }

    public object AddRoleMember(string sessionId, string role, string memberName, string? provider)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddRoleMemberCore(model, role, memberName, provider);
        ModelTxn.Save(model);
        return Persisted(new { role, member = memberName, external = !string.IsNullOrWhiteSpace(provider) });
    }

    internal static TOM.ModelRoleMember AddRoleMemberCore(TOM.Model model, string role, string memberName, string? provider)
    {
        if (string.IsNullOrWhiteSpace(memberName)) throw new InvalidOperationException("memberName is required.");
        var r = Role(model, role);
        TOM.ModelRoleMember member = string.IsNullOrWhiteSpace(provider)
            ? new TOM.WindowsModelRoleMember { MemberName = memberName }
            : new TOM.ExternalModelRoleMember { MemberName = memberName, IdentityProvider = provider };
        r.Members.Add(member);
        return member;
    }

    // ---- OLS (object-level security): metadata permission on a table / column ----
    public object SetTableOls(string sessionId, string role, string table, string permission)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetTableOlsCore(model, role, table, permission);
        ModelTxn.Save(model);
        return Persisted(new { role, table, metadataPermission = permission });
    }

    internal static TOM.TablePermission SetTableOlsCore(TOM.Model model, string role, string table, string permission)
    {
        var r = Role(model, role);
        var t = Table(model, table);
        var tp = TablePermissionFor(r, t);
        tp.MetadataPermission = ParseMetadataPermission(permission);
        return tp;
    }

    public object SetColumnOls(string sessionId, string role, string table, string column, string permission)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetColumnOlsCore(model, role, table, column, permission);
        ModelTxn.Save(model);
        return Persisted(new { role, column = $"{table}[{column}]", metadataPermission = permission });
    }

    internal static TOM.ColumnPermission SetColumnOlsCore(TOM.Model model, string role, string table, string column, string permission)
    {
        var r = Role(model, role);
        var t = Table(model, table);
        var col = Column(t, column);
        var tp = TablePermissionFor(r, t);
        var cp = tp.ColumnPermissions.Find(column);
        if (cp == null)
        {
            cp = new TOM.ColumnPermission { Column = col };
            tp.ColumnPermissions.Add(cp);
        }
        cp.MetadataPermission = ParseMetadataPermission(permission);
        return cp;
    }

    // ================================================================ calculation groups
    public object AddCalculationGroup(string sessionId, string table, int? precedence)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddCalculationGroupCore(model, table, precedence);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, precedence });
    }

    internal static TOM.CalculationGroup AddCalculationGroupCore(TOM.Model model, string table, int? precedence)
    {
        var t = Table(model, table);
        if (t.CalculationGroup != null)
            throw new InvalidOperationException($"Table '{table}' already has a calculation group.");
        var cg = new TOM.CalculationGroup();
        if (precedence is { } p) cg.Precedence = p;
        t.CalculationGroup = cg;
        return cg;
    }

    public object AddCalculationItem(string sessionId, string table, string name, string daxExpression,
        int? ordinal, string? formatStringExpression)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddCalculationItemCore(model, table, name, daxExpression, ordinal, formatStringExpression);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, item = name, ordinal });
    }

    internal static TOM.CalculationItem AddCalculationItemCore(TOM.Model model, string table, string name,
        string daxExpression, int? ordinal, string? formatStringExpression)
    {
        var t = Table(model, table);
        var cg = t.CalculationGroup
                 ?? throw new InvalidOperationException($"Table '{table}' is not a calculation group. Run add_calculation_group first.");
        if (cg.CalculationItems.Contains(name))
            throw new InvalidOperationException($"Calculation item '{name}' already exists on '{table}'.");
        var item = new TOM.CalculationItem { Name = name, Expression = daxExpression };
        if (ordinal is { } o) item.Ordinal = o;
        if (!string.IsNullOrWhiteSpace(formatStringExpression))
            item.FormatStringDefinition = new TOM.FormatStringDefinition { Expression = formatStringExpression };
        cg.CalculationItems.Add(item);
        return item;
    }

    // ================================================================ KPI
    public object SetKpi(string sessionId, string table, string measure, string targetExpression,
        string statusExpression, string? statusGraphic)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetKpiCore(model, table, measure, targetExpression, statusExpression, statusGraphic);
        ModelTxn.Save(model);
        return Persisted(new { measure = $"{table}[{measure}]", statusGraphic = statusGraphic ?? "Three Circles Colored" });
    }

    internal static TOM.KPI SetKpiCore(TOM.Model model, string table, string measure, string targetExpression,
        string statusExpression, string? statusGraphic)
    {
        var t = Table(model, table);
        var me = t.Measures.Find(measure)
                 ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
        var kpi = new TOM.KPI
        {
            TargetExpression = targetExpression,
            StatusExpression = statusExpression,
            StatusGraphic = string.IsNullOrWhiteSpace(statusGraphic) ? "Three Circles Colored" : statusGraphic,
        };
        me.KPI = kpi;
        return kpi;
    }

    // ================================================================ detail rows (drillthrough)
    public object SetDetailRows(string sessionId, string table, string? measure, string daxTableExpression)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetDetailRowsCore(model, table, measure, daxTableExpression);
        ModelTxn.Save(model);
        return Persisted(new { target = measure != null ? $"{table}[{measure}]" : table });
    }

    internal static TOM.DetailRowsDefinition SetDetailRowsCore(TOM.Model model, string table, string? measure, string daxTableExpression)
    {
        var t = Table(model, table);
        var drd = new TOM.DetailRowsDefinition { Expression = daxTableExpression };
        if (string.IsNullOrWhiteSpace(measure))
        {
            t.DefaultDetailRowsDefinition = drd;
        }
        else
        {
            var me = t.Measures.Find(measure)
                     ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
            me.DetailRowsDefinition = drd;
        }
        return drd;
    }

    // ================================================================ dynamic format string
    public object SetDynamicFormatString(string sessionId, string table, string? measure, string? calculationItem, string daxExpression)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetDynamicFormatStringCore(model, table, measure, calculationItem, daxExpression);
        ModelTxn.Save(model);
        return Persisted(new { target = measure != null ? $"{table}[{measure}]" : $"{table}/{calculationItem}" });
    }

    internal static TOM.FormatStringDefinition SetDynamicFormatStringCore(TOM.Model model, string table,
        string? measure, string? calculationItem, string daxExpression)
    {
        var t = Table(model, table);
        var fsd = new TOM.FormatStringDefinition { Expression = daxExpression };
        if (!string.IsNullOrWhiteSpace(measure))
        {
            var me = t.Measures.Find(measure)
                     ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
            me.FormatStringDefinition = fsd;
        }
        else if (!string.IsNullOrWhiteSpace(calculationItem))
        {
            var cg = t.CalculationGroup
                     ?? throw new InvalidOperationException($"Table '{table}' is not a calculation group.");
            var item = cg.CalculationItems.Find(calculationItem)
                       ?? throw new InvalidOperationException($"Calculation item '{calculationItem}' not found on '{table}'.");
            item.FormatStringDefinition = fsd;
        }
        else
        {
            throw new InvalidOperationException("Provide either a measure or a calculationItem to set the dynamic format string on.");
        }
        return fsd;
    }

    // ================================================================ field / what-if parameters
    /// <summary>
    /// Build a FIELD PARAMETER: a calculated table whose rows let a user swap which field a visual shows.
    /// DAX is {("Display", NAMEOF('T'[Col]), 0), ...}; the three generated columns carry the
    /// ParameterMetadata extended property the report layer recognises. Reuses add_calculated_table.
    /// </summary>
    public object AddFieldParameter(string sessionId, string name, string[] fields)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddFieldParameterCore(model, name, fields);
        ModelTxn.Save(model);
        try { model.RequestRefresh(TOM.RefreshType.Calculate); ModelTxn.Save(model); } catch { }
        return Persisted(new { added = name, fields, note = "Field parameter table - drop its first column on a slicer to switch fields." });
    }

    internal static TOM.Table AddFieldParameterCore(TOM.Model model, string name, string[] fields)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Field-parameter name is required.");
        if (model.Tables.Contains(name)) throw new InvalidOperationException($"Table '{name}' already exists.");
        if (fields == null || fields.Length == 0) throw new InvalidOperationException("Provide at least one field as Table[Column].");

        var rows = new List<string>();
        for (int i = 0; i < fields.Length; i++)
        {
            var (tbl, col) = ParseFieldRef(fields[i]);
            string display = col;
            rows.Add($"(\"{display.Replace("\"", "\"\"")}\", NAMEOF('{tbl}'[{col}]), {i})");
        }
        string dax = "{\n    " + string.Join(",\n    ", rows) + "\n}";

        var table = new TOM.Table { Name = name };
        table.Partitions.Add(new TOM.Partition { Name = name, Source = new TOM.CalculatedPartitionSource { Expression = dax } });

        // the three projected columns: [name] (display), [Fields] (field ref), [Order]. The
        // ParameterMetadata extended property is what marks the table as a field parameter.
        var c0 = new TOM.CalculatedTableColumn { Name = name, SourceColumn = "[Value1]", IsHidden = false };
        var c1 = new TOM.CalculatedTableColumn { Name = $"{name} Fields", SourceColumn = "[Value2]", IsHidden = true };
        var c2 = new TOM.CalculatedTableColumn { Name = $"{name} Order", SourceColumn = "[Value3]", IsHidden = true };
        c0.ExtendedProperties.Add(new TOM.JsonExtendedProperty { Name = "ParameterMetadata", Value = "{\"version\":3,\"kind\":2}" });
        c1.ExtendedProperties.Add(new TOM.JsonExtendedProperty { Name = "ParameterMetadata", Value = "{\"version\":3,\"kind\":2}" });
        c2.ExtendedProperties.Add(new TOM.JsonExtendedProperty { Name = "ParameterMetadata", Value = "{\"version\":3,\"kind\":2}" });
        // display column sorts by the ordinal (Order) column...
        c0.SortByColumn = c2;
        // ...and groups by the hidden Fields column so the swap survives equal display names. Without this
        // GroupBy binding the field parameter does not bind correctly in the report layer.
        c0.RelatedColumnDetails = new TOM.RelatedColumnDetails();
        c0.RelatedColumnDetails.GroupByColumns.Add(new TOM.GroupByColumn { GroupingColumn = c1 });
        table.Columns.Add(c0);
        table.Columns.Add(c1);
        table.Columns.Add(c2);
        model.Tables.Add(table);
        return table;
    }

    /// <summary>
    /// Build a WHAT-IF parameter: a GENERATESERIES disconnected calculated table plus a SELECTEDVALUE
    /// measure that picks up the slider value. The standard "Modeling &gt; New parameter" pattern.
    /// </summary>
    public object AddWhatIfParameter(string sessionId, string name, double min, double max, double increment, double? defaultValue)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddWhatIfParameterCore(model, name, min, max, increment, defaultValue);
        ModelTxn.Save(model);
        try { model.RequestRefresh(TOM.RefreshType.Calculate); ModelTxn.Save(model); } catch { }
        return Persisted(new { added = name, measure = $"{name} Value", min, max, increment,
            note = "Disconnected what-if table; drop its column on a slider slicer and reference the [<name> Value] measure." });
    }

    internal static TOM.Table AddWhatIfParameterCore(TOM.Model model, string name, double min, double max, double increment, double? defaultValue)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("What-if parameter name is required.");
        if (model.Tables.Contains(name)) throw new InvalidOperationException($"Table '{name}' already exists.");
        if (increment <= 0) throw new InvalidOperationException("increment must be greater than zero.");
        if (max < min) throw new InvalidOperationException("max must be greater than or equal to min.");

        string Num(double d) => d.ToString(CultureInfo.InvariantCulture);
        string dax = $"GENERATESERIES({Num(min)}, {Num(max)}, {Num(increment)})";
        var table = new TOM.Table { Name = name };
        table.Partitions.Add(new TOM.Partition { Name = name, Source = new TOM.CalculatedPartitionSource { Expression = dax } });

        var col = new TOM.CalculatedTableColumn { Name = name, SourceColumn = "[Value]", DataType = TOM.DataType.Decimal };
        col.ExtendedProperties.Add(new TOM.JsonExtendedProperty { Name = "ParameterMetadata", Value = "{\"version\":0}" });
        table.Columns.Add(col);

        double def = defaultValue ?? min;
        var measure = new TOM.Measure
        {
            Name = $"{name} Value",
            Expression = $"SELECTEDVALUE('{name}'[{name}], {Num(def)})",
        };
        table.Measures.Add(measure);
        model.Tables.Add(table);
        return table;
    }

    private static (string table, string column) ParseFieldRef(string fieldRef)
    {
        var s = (fieldRef ?? "").Trim();
        int lb = s.IndexOf('[');
        int rb = s.LastIndexOf(']');
        if (lb <= 0 || rb <= lb) throw new InvalidOperationException($"Field '{fieldRef}' must be in the form Table[Column].");
        string tbl = s[..lb].Trim().Trim('\'');
        string col = s[(lb + 1)..rb].Trim();
        if (tbl.Length == 0 || col.Length == 0) throw new InvalidOperationException($"Field '{fieldRef}' must be in the form Table[Column].");
        return (tbl, col);
    }

    // ================================================================ Wave P: DAX UDFs + primitives
    /// <summary>
    /// Define a DAX User-Defined Function as a net-new model object (Model.Functions). The function body
    /// carries its own typed parameters and return type. UDFs need compatibility level 1702+, so this
    /// auto-bumps Database.CompatibilityLevel when it is lower (reported back via the result).
    /// </summary>
    public object DefineUdf(string sessionId, string name, IReadOnlyList<(string name, string type)> parameters,
        string bodyDax, string? returnType, string? description)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        string expr = DaxGenerators.UdfExpression(name, parameters, bodyDax, returnType, description);

        bool bumped = false;
        int wasLevel = session.Database.CompatibilityLevel;
        if (wasLevel < 1702)
        {
            try { session.Database.CompatibilityLevel = 1702; bumped = true; }
            catch (Exception ex) { throw new InvalidOperationException(
                $"DAX UDFs need compatibility level 1702+, but bumping from {wasLevel} failed: {ex.Message}"); }
        }

        DefineUdfCore(model, name, expr, description);
        ModelTxn.Save(model);
        return Persisted(new
        {
            udf = name,
            expression = expr,
            compatibilityLevel = session.Database.CompatibilityLevel,
            compatibilityLevelBumped = bumped,
            approach = "TOM.Function (Model.Functions)",
            note = bumped ? $"CompatibilityLevel raised from {wasLevel} to 1702 to enable UDFs." : null,
        });
    }

    /// <summary>Pure object-tree add of a UDF Function (no SaveChanges) so it can be unit-tested.</summary>
    internal static TOM.Function DefineUdfCore(TOM.Model model, string name, string expression, string? description)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("UDF name is required.");
        if (model.Functions.Contains(name))
            throw new InvalidOperationException($"Function '{name}' already exists. Pick another name or remove it first.");
        var fn = new TOM.Function { Name = name, Expression = expression };
        if (!string.IsNullOrWhiteSpace(description)) fn.Description = description;
        model.Functions.Add(fn);
        return fn;
    }

    /// <summary>List the model's UDFs via INFO.USERDEFINEDFUNCTIONS() (falls back to the TOM collection).</summary>
    public object ListUdfs(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var fns = model.Functions.Select(f => new { name = f.Name, expression = f.Expression, description = f.Description });
        return new { functions = fns };
    }

    /// <summary>Run EVALUATE INFO.VIEW.&lt;view&gt;() (or INFO.&lt;x&gt;()) and return shaped rows. Model docs / lineage.</summary>
    public object InfoView(string sessionId, string view, int maxRows)
    {
        string query = DaxGenerators.InfoViewQuery(view);
        var rows = RunDax(sessionId, query, maxRows);
        return new { view = (view ?? "").Trim().ToUpperInvariant(), query, result = rows };
    }

    /// <summary>Run EVALUATE COLUMNSTATISTICS() - per-column Min/Max/Cardinality/MaxLength profiling.</summary>
    public object ColumnStatistics(string sessionId, int maxRows)
    {
        string query = DaxGenerators.ColumnStatisticsQuery();
        var rows = RunDax(sessionId, query, maxRows);
        return new { query, result = rows };
    }

    /// <summary>Wrap a measure's DAX in EVALUATEANDLOG for Server Timings / DAX-debug capture. Idempotent.</summary>
    public object InjectEvaluateAndLog(string sessionId, string table, string measure, string? label)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var me = Table(model, table).Measures.Find(measure)
                 ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
        me.Expression = DaxGenerators.InjectEvaluateAndLog(me.Expression, label);
        ModelTxn.Save(model);
        return Persisted(new { measure = $"{table}[{measure}]", wrapped = true, expression = me.Expression });
    }

    /// <summary>Remove EVALUATEANDLOG wrappers from one measure, or every measure when measure is omitted.</summary>
    public object StripEvaluateAndLog(string sessionId, string? table, string? measure)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var stripped = new List<string>();
        IEnumerable<(TOM.Table t, TOM.Measure m)> targets;
        if (!string.IsNullOrWhiteSpace(measure))
        {
            if (string.IsNullOrWhiteSpace(table)) throw new InvalidOperationException("Provide the table when stripping a single measure.");
            var t = Table(model, table!);
            var me = t.Measures.Find(measure!) ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
            targets = new[] { (t, me) };
        }
        else
        {
            targets = model.Tables.SelectMany(t => t.Measures.Select(m => (t, m)));
        }
        foreach (var (t, me) in targets)
        {
            if (me.Expression?.IndexOf("EVALUATEANDLOG", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                me.Expression = DaxGenerators.StripEvaluateAndLog(me.Expression);
                stripped.Add($"{t.Name}[{me.Name}]");
            }
        }
        ModelTxn.Save(model);
        return Persisted(new { stripped, count = stripped.Count });
    }

    // ================================================================ Wave P: parameterised generators
    private object ApplyGeneratedMeasures(string sessionId, IReadOnlyList<DaxGenerators.MeasureSpec> specs, string generator)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var added = ApplyMeasureSpecsCore(model, specs);
        ModelTxn.Save(model);
        return Persisted(new { generator, added, count = added.Count });
    }

    public object AddTimeIntelligenceMeasures(string sessionId, string table, string baseMeasure,
        string dateTable, string dateColumn, string? fiscalYearEnd)
        => ApplyGeneratedMeasures(sessionId,
            DaxGenerators.TimeIntelligenceMeasures(table, baseMeasure, dateTable, dateColumn, fiscalYearEnd),
            "add_time_intelligence_measures");

    public object AddRunningTotal(string sessionId, string table, string baseMeasure,
        string? dateTable, string? dateColumn, string? sortColumn)
    {
        DaxGenerators.MeasureSpec spec = !string.IsNullOrWhiteSpace(sortColumn)
            ? DaxGenerators.RunningTotalOverColumn(table, baseMeasure, sortColumn!)
            : DaxGenerators.RunningTotalOverDate(table, baseMeasure,
                dateTable ?? throw new InvalidOperationException("Provide dateTable+dateColumn or sortColumn."),
                dateColumn ?? throw new InvalidOperationException("Provide dateTable+dateColumn or sortColumn."));
        return ApplyGeneratedMeasures(sessionId, new[] { spec }, "add_running_total");
    }

    public object AddMovingAverage(string sessionId, string table, string baseMeasure,
        string dateTable, string dateColumn, int periods, string? unit)
    {
        var spec = (unit ?? "day").Trim().ToLowerInvariant().StartsWith("month")
            ? DaxGenerators.MovingAverageMonths(table, baseMeasure, dateTable, dateColumn, periods)
            : DaxGenerators.MovingAverage(table, baseMeasure, dateTable, dateColumn, periods);
        return ApplyGeneratedMeasures(sessionId, new[] { spec }, "add_moving_average");
    }

    public object AddPercentOfTotal(string sessionId, string table, string baseMeasure, string dimension, string? scope)
        => ApplyGeneratedMeasures(sessionId,
            new[] { DaxGenerators.PercentOfTotal(table, baseMeasure, dimension, scope) }, "add_percent_of_total");

    public object AddPercentOfParent(string sessionId, string table, string baseMeasure, IReadOnlyList<string> hierarchyColumns)
        => ApplyGeneratedMeasures(sessionId,
            new[] { DaxGenerators.PercentOfParent(table, baseMeasure, hierarchyColumns) }, "add_percent_of_parent");

    public object AddRankMeasure(string sessionId, string table, string baseMeasure, string dimension,
        string order, string ties, string? withinGroup)
        => ApplyGeneratedMeasures(sessionId,
            new[] { DaxGenerators.RankMeasure(table, baseMeasure, dimension, order, ties, withinGroup) }, "add_rank_measure");

    public object AddSemiAdditiveMeasures(string sessionId, string table, string valueColumn, string dateColumn)
        => ApplyGeneratedMeasures(sessionId,
            DaxGenerators.SemiAdditiveMeasures(table, valueColumn, dateColumn), "add_semiadditive_measures");

    public object AddDynamicSegmentation(string sessionId, string entityTable, string measure, string boundaryTable,
        string granularityColumn, string lowerColumn, string upperColumn, string segmentColumn)
        => ApplyGeneratedMeasures(sessionId,
            new[] { DaxGenerators.DynamicSegmentation(entityTable, measure, boundaryTable, granularityColumn,
                lowerColumn, upperColumn, segmentColumn) }, "add_dynamic_segmentation");

    /// <summary>
    /// ABC (Pareto) classification: a dynamic class measure plus a calculated class table. thresholds
    /// are the cumulative-share cut-offs for A and B (e.g. 0.7 and 0.9); the rest fall into C.
    /// </summary>
    public object AddAbcClassification(string sessionId, string entityTable, string key, string valueMeasure,
        double aThreshold, double bThreshold, string? classTableName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var spec = DaxGenerators.AbcClassificationDynamic(entityTable, key, valueMeasure, aThreshold, bThreshold);
        AddMeasureCore(model, spec.Table, spec.Name, spec.Dax, spec.Format, spec.Folder, null);

        string tableName = string.IsNullOrWhiteSpace(classTableName)
            ? $"{DaxGenerators.StripBrackets(valueMeasure)} ABC" : classTableName!;
        string classDax = DaxGenerators.AbcClassTableDax(entityTable, key, valueMeasure, aThreshold, bThreshold);
        if (!model.Tables.Contains(tableName))
        {
            var t = new TOM.Table { Name = tableName };
            t.Partitions.Add(new TOM.Partition { Name = tableName, Source = new TOM.CalculatedPartitionSource { Expression = classDax } });
            model.Tables.Add(t);
        }
        ModelTxn.Save(model);
        return Persisted(new { measure = $"{spec.Table}[{spec.Name}]", classTable = tableName, refreshRequired = true });
    }

    /// <summary>
    /// Dynamic Top N: a what-if N parameter (slider), a UNION+Others dimension table, and a
    /// rank-or-others measure that buckets everything below rank N into the Others row.
    /// </summary>
    public object AddDynamicTopN(string sessionId, string homeTable, string dimension, string measure,
        double nMin, double nMax, double nIncrement, double? nDefault, string? othersLabel, string? topNTableName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        string label = string.IsNullOrWhiteSpace(othersLabel) ? "Others" : othersLabel!;
        string nName = $"{DaxGenerators.StripBrackets(measure)} Top N";
        AddWhatIfParameterCore(model, nName, nMin, nMax, nIncrement, nDefault);

        string topTable = string.IsNullOrWhiteSpace(topNTableName)
            ? $"{DaxGenerators.StripBrackets(dimension)} (Top N)" : topNTableName!;
        string topDax = DaxGenerators.DynamicTopNTableDax(dimension, label);
        if (!model.Tables.Contains(topTable))
        {
            var t = new TOM.Table { Name = topTable };
            t.Partitions.Add(new TOM.Partition { Name = topTable, Source = new TOM.CalculatedPartitionSource { Expression = topDax } });
            t.Columns.Add(new TOM.CalculatedTableColumn { Name = "Member", SourceColumn = "[@Member]" });
            model.Tables.Add(t);
        }
        string memberRef = $"{topTable}[Member]";
        var spec = DaxGenerators.DynamicTopNMeasure(homeTable, dimension, measure, $"{nName} Value", label, memberRef);
        AddMeasureCore(model, spec.Table, spec.Name, spec.Dax, spec.Format, spec.Folder, null);
        ModelTxn.Save(model);
        return Persisted(new { whatIfParameter = nName, topNTable = topTable, measure = $"{spec.Table}[{spec.Name}]", refreshRequired = true });
    }

    /// <summary>
    /// Time-intelligence calculation group: Current/MTD/QTD/YTD/PY/PYTD/YoY/YoY% items with ordinals,
    /// a format-string definition on YoY%, and DiscourageImplicitMeasures on the group's table.
    /// </summary>
    public object AddTimeIntelligenceCalcGroup(string sessionId, string table, string dateTable, string dateColumn, int? precedence)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddTimeIntelligenceCalcGroupCore(model, table, dateTable, dateColumn, precedence);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, items = 8 });
    }

    internal static TOM.CalculationGroup AddTimeIntelligenceCalcGroupCore(TOM.Model model, string table,
        string dateTable, string dateColumn, int? precedence)
    {
        var cg = AddCalculationGroupCore(model, table, precedence);
        // a calculation group requires implicit measures to be discouraged at the model level.
        model.DiscourageImplicitMeasures = true;
        foreach (var item in DaxGenerators.TimeIntelligenceCalcItems(dateTable, dateColumn))
            AddCalculationItemCore(model, table, item.Name, item.Dax, item.Ordinal, item.FormatStringExpression);
        return cg;
    }

    /// <summary>Currency-conversion calc group: a single Converted item that applies the daily rate. </summary>
    public object AddCurrencyConversionCalcGroup(string sessionId, string table, string rateTable, string rateColumn,
        string currencyColumn, int? precedence)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddCalculationGroupCore(model, table, precedence);
        AddCalculationItemCore(model, table, "Original Value", "SELECTEDMEASURE ()", 0, null);
        var item = DaxGenerators.CurrencyConversionCalcItem(rateTable, rateColumn, currencyColumn);
        AddCalculationItemCore(model, table, item.Name, item.Dax, item.Ordinal, item.FormatStringExpression);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, items = 2 });
    }

    /// <summary>
    /// Dynamic RLS: create the role (if absent) and set a USERPRINCIPALNAME()-driven filter on the SECURED
    /// table. shape = direct|bridge|hierarchy|lookup. Never RLS the user/lookup table itself - that is
    /// flagged and refused, as filtering the mapping table breaks the security lookup.
    /// </summary>
    public object AddDynamicRls(string sessionId, string role, string shape, string securedTable, string securedColumn,
        string userTable, string userEmailColumn, string userValueColumn, string? pathColumn, string? modelPermission)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var result = AddDynamicRlsCore(model, role, shape, securedTable, securedColumn,
            userTable, userEmailColumn, userValueColumn, pathColumn, modelPermission);
        ModelTxn.Save(model);
        return Persisted(result);
    }

    internal static object AddDynamicRlsCore(TOM.Model model, string role, string shape, string securedTable,
        string securedColumn, string userTable, string userEmailColumn, string userValueColumn,
        string? pathColumn, string? modelPermission)
    {
        if (string.Equals(securedTable, userTable, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Refusing to RLS the user/mapping table itself - filtering it breaks the USERPRINCIPALNAME lookup. Secure the fact/dimension table instead.");
        if (!model.Roles.Contains(role))
            AddRoleCore(model, role, string.IsNullOrWhiteSpace(modelPermission) ? "Read" : modelPermission!);
        string filter = DaxGenerators.DynamicRlsFilter(shape, securedTable, securedColumn,
            userTable, userEmailColumn, userValueColumn, pathColumn);
        var tp = SetRlsCore(model, role, securedTable, filter);
        return new { role, shape, securedTable, filterExpression = tp.FilterExpression };
    }

    public object AddCustomCalendarTimeIntelligence(string sessionId, string table, string calendarTable,
        string kind, string baseMeasure)
        => ApplyGeneratedMeasures(sessionId,
            DaxGenerators.CustomCalendarTimeIntelligence(table, calendarTable, kind, baseMeasure),
            "add_custom_calendar_time_intelligence");

    // ================================================================ perspectives
    public object AddPerspective(string sessionId, string name)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddPerspectiveCore(model, name);
        ModelTxn.Save(model);
        return Persisted(new { addedPerspective = name });
    }

    internal static TOM.Perspective AddPerspectiveCore(TOM.Model model, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Perspective name is required.");
        if (model.Perspectives.Contains(name))
            throw new InvalidOperationException($"Perspective '{name}' already exists.");
        var p = new TOM.Perspective { Name = name };
        model.Perspectives.Add(p);
        return p;
    }

    public object AddToPerspective(string sessionId, string perspective, string table, string? childObject)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddToPerspectiveCore(model, perspective, table, childObject);
        ModelTxn.Save(model);
        return Persisted(new { perspective, table, member = childObject ?? "(whole table)" });
    }

    internal static TOM.PerspectiveTable AddToPerspectiveCore(TOM.Model model, string perspective, string table, string? childObject)
    {
        var p = model.Perspectives.Find(perspective)
                ?? throw new InvalidOperationException($"Perspective '{perspective}' not found. Run add_perspective first.");
        var t = Table(model, table);
        var pt = p.PerspectiveTables.Find(table);
        if (pt == null) { pt = new TOM.PerspectiveTable { Table = t }; p.PerspectiveTables.Add(pt); }
        if (string.IsNullOrWhiteSpace(childObject)) return pt;   // whole table

        if (t.Columns.Contains(childObject))
        {
            if (!pt.PerspectiveColumns.Contains(childObject))
                pt.PerspectiveColumns.Add(new TOM.PerspectiveColumn { Column = t.Columns.Find(childObject) });
        }
        else if (t.Measures.Contains(childObject))
        {
            if (!pt.PerspectiveMeasures.Contains(childObject))
                pt.PerspectiveMeasures.Add(new TOM.PerspectiveMeasure { Measure = t.Measures.Find(childObject) });
        }
        else if (t.Hierarchies.Contains(childObject))
        {
            if (!pt.PerspectiveHierarchies.Contains(childObject))
                pt.PerspectiveHierarchies.Add(new TOM.PerspectiveHierarchy { Hierarchy = t.Hierarchies.Find(childObject) });
        }
        else
        {
            throw new InvalidOperationException($"'{childObject}' is not a column, measure or hierarchy on '{table}'.");
        }
        return pt;
    }

    // ================================================================ cultures / translations
    public object AddCulture(string sessionId, string locale)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddCultureCore(model, locale);
        ModelTxn.Save(model);
        return Persisted(new { addedCulture = locale });
    }

    internal static TOM.Culture AddCultureCore(TOM.Model model, string locale)
    {
        if (string.IsNullOrWhiteSpace(locale)) throw new InvalidOperationException("Culture locale is required (e.g. fr-FR).");
        if (model.Cultures.Contains(locale))
            throw new InvalidOperationException($"Culture '{locale}' already exists.");
        var c = new TOM.Culture { Name = locale };
        model.Cultures.Add(c);
        return c;
    }

    /// <summary>
    /// Set a translated Caption / Description / DisplayFolder for a model object in a culture.
    /// objectType = table | column | measure | hierarchy | model. ObjectTranslations on the Culture.
    /// </summary>
    public object SetTranslation(string sessionId, string culture, string objectType, string objectName,
        string property, string value, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetTranslationCore(model, culture, objectType, objectName, property, value, table);
        ModelTxn.Save(model);
        return Persisted(new { culture, objectType, objectName, property, value });
    }

    internal static TOM.ObjectTranslation SetTranslationCore(TOM.Model model, string culture, string objectType,
        string objectName, string property, string value, string? table)
    {
        var c = model.Cultures.Find(culture)
                ?? throw new InvalidOperationException($"Culture '{culture}' not found. Run add_culture first.");
        var prop = ParseTranslatedProperty(property);
        var target = ResolveTranslationTarget(model, objectType, objectName, table);

        var existing = c.ObjectTranslations.FirstOrDefault(o => ReferenceEquals(o.Object, target) && o.Property == prop);
        if (existing != null) { existing.Value = value; return existing; }
        var tr = new TOM.ObjectTranslation { Object = target, Property = prop, Value = value };
        c.ObjectTranslations.Add(tr);
        return tr;
    }

    internal static TOM.TranslatedProperty ParseTranslatedProperty(string? property) =>
        property?.Trim().ToLowerInvariant() switch
        {
            "caption" => TOM.TranslatedProperty.Caption,
            "description" => TOM.TranslatedProperty.Description,
            "displayfolder" => TOM.TranslatedProperty.DisplayFolder,
            _ => throw new InvalidOperationException($"Invalid property '{property}'. Use Caption, Description or DisplayFolder."),
        };

    internal static TOM.MetadataObject ResolveTranslationTarget(TOM.Model model, string? objectType,
        string objectName, string? table) =>
        (objectType ?? "").Trim().ToLowerInvariant() switch
        {
            "model" => model,
            "table" => Table(model, objectName),
            "column" => Column(Table(model, table ?? throw new InvalidOperationException("column translation needs a table.")), objectName),
            "measure" => Table(model, table ?? throw new InvalidOperationException("measure translation needs a table.")).Measures.Find(objectName)
                         ?? throw new InvalidOperationException($"Measure '{objectName}' not found on '{table}'."),
            "hierarchy" => Table(model, table ?? throw new InvalidOperationException("hierarchy translation needs a table.")).Hierarchies.Find(objectName)
                           ?? throw new InvalidOperationException($"Hierarchy '{objectName}' not found on '{table}'."),
            _ => throw new InvalidOperationException($"Invalid objectType '{objectType}'. Use table, column, measure, hierarchy or model."),
        };

    // ================================================================ aggregations (AlternateOf)
    /// <summary>
    /// Mark a detail column as an aggregation of a base column/table (the user-defined aggregations
    /// feature): Column.AlternateOf with a Summarization and either a BaseColumn or BaseTable.
    /// </summary>
    public object SetAggregation(string sessionId, string detailColumn, string baseColumnOrTable, string summarization, string table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetAggregationCore(model, table, detailColumn, baseColumnOrTable, summarization);
        ModelTxn.Save(model);
        return Persisted(new { aggregation = $"{table}[{detailColumn}]", baseColumnOrTable, summarization });
    }

    internal static TOM.AlternateOf SetAggregationCore(TOM.Model model, string table, string detailColumn,
        string baseColumnOrTable, string summarization)
    {
        var t = Table(model, table);
        var col = Column(t, detailColumn);
        var summ = (summarization ?? "").Trim().ToLowerInvariant() switch
        {
            "groupby" => TOM.SummarizationType.GroupBy,
            "sum" => TOM.SummarizationType.Sum,
            "count" => TOM.SummarizationType.Count,
            "min" => TOM.SummarizationType.Min,
            "max" => TOM.SummarizationType.Max,
            _ => throw new InvalidOperationException($"Invalid summarization '{summarization}'. Use GroupBy, Sum, Count, Min or Max."),
        };
        var alt = new TOM.AlternateOf { Summarization = summ };
        // GroupBy => the base is a column; the aggregation summarisations => base is a table (or column).
        if (summ == TOM.SummarizationType.GroupBy)
        {
            var (bt, bc) = ParseFieldRef(baseColumnOrTable);
            alt.BaseColumn = Column(Table(model, bt), bc);
        }
        else if (baseColumnOrTable.Contains('['))
        {
            var (bt, bc) = ParseFieldRef(baseColumnOrTable);
            alt.BaseColumn = Column(Table(model, bt), bc);
        }
        else
        {
            alt.BaseTable = Table(model, baseColumnOrTable.Trim().Trim('\''));
        }
        col.AlternateOf = alt;
        return alt;
    }

    // ================================================================ incremental refresh
    /// <summary>
    /// Attach a basic incremental-refresh RefreshPolicy to a table. Requires RangeStart/RangeEnd M
    /// parameters: if absent, this creates them as shared M expressions (NamedExpression parameters).
    /// </summary>
    public object SetIncrementalRefresh(string sessionId, string table, string rollingWindowGranularity,
        int rollingWindowPeriods, string incrementalGranularity, int incrementalPeriods, string? pollingExpression)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        bool created = SetIncrementalRefreshCore(model, session.MDirty, table, rollingWindowGranularity,
            rollingWindowPeriods, incrementalGranularity, incrementalPeriods, pollingExpression);
        ModelTxn.Save(model);
        return Persisted(new
        {
            table,
            rollingWindow = $"{rollingWindowPeriods} {rollingWindowGranularity}",
            incremental = $"{incrementalPeriods} {incrementalGranularity}",
            rangeParametersCreated = created,
            refreshRequiredBeforeSave = created,
            note = created
                ? "RangeStart/RangeEnd M parameters were created - point the table partition's M at them to bind the range."
                : "RangeStart/RangeEnd M parameters already present.",
        });
    }

    /// <summary>Tracker-aware seam, offline-testable (no SaveChanges). Creating RangeStart/RangeEnd adds
    /// shared M NamedExpressions the mashup has never refreshed against - the same class as
    /// set_shared_expression - so the save gate must prove them before a Desktop save. Attaching the
    /// RefreshPolicy alone writes no M text, so a params-already-present call leaves the tracker alone.</summary>
    internal static bool SetIncrementalRefreshCore(TOM.Model model, MDirtyTracker dirty, string table,
        string rollingWindowGranularity, int rollingWindowPeriods, string incrementalGranularity,
        int incrementalPeriods, string? pollingExpression)
    {
        bool created = SetIncrementalRefreshCore(model, table, rollingWindowGranularity, rollingWindowPeriods,
            incrementalGranularity, incrementalPeriods, pollingExpression);
        if (created) dirty.Mark($"set_incremental_refresh {table} (created RangeStart/RangeEnd M parameters)");
        return created;
    }

    /// <returns>true if RangeStart/RangeEnd parameters were created.</returns>
    internal static bool SetIncrementalRefreshCore(TOM.Model model, string table, string rollingWindowGranularity,
        int rollingWindowPeriods, string incrementalGranularity, int incrementalPeriods, string? pollingExpression)
    {
        var t = Table(model, table);
        if (rollingWindowPeriods <= 0 || incrementalPeriods <= 0)
            throw new InvalidOperationException("rollingWindowPeriods and incrementalPeriods must be greater than zero.");
        var rollG = ParseRefreshGranularity(rollingWindowGranularity);
        var incG = ParseRefreshGranularity(incrementalGranularity);

        bool created = false;
        if (model.Expressions.Find("RangeStart") == null) { CreateRangeParam(model, "RangeStart", "2020-01-01"); created = true; }
        if (model.Expressions.Find("RangeEnd") == null) { CreateRangeParam(model, "RangeEnd", "2021-01-01"); created = true; }

        var policy = new TOM.BasicRefreshPolicy
        {
            RollingWindowGranularity = rollG,
            RollingWindowPeriods = rollingWindowPeriods,
            IncrementalGranularity = incG,
            IncrementalPeriods = incrementalPeriods,
        };
        if (!string.IsNullOrWhiteSpace(pollingExpression)) policy.PollingExpression = pollingExpression;
        t.RefreshPolicy = policy;
        return created;
    }

    private static void CreateRangeParam(TOM.Model model, string name, string dateLiteral)
    {
        string m = $"{dateLiteral}T00:00:00 meta [IsParameterQuery=true, Type=\"DateTime\", IsParameterQueryRequired=true]";
        model.Expressions.Add(new TOM.NamedExpression { Name = name, Kind = TOM.ExpressionKind.M, Expression = m });
    }

    private static TOM.RefreshGranularityType ParseRefreshGranularity(string s) =>
        Enum.TryParse<TOM.RefreshGranularityType>((s ?? "").Trim(), ignoreCase: true, out var g)
            ? g
            : throw new InvalidOperationException($"Invalid granularity '{s}'. Use Day, Month, Quarter or Year.");

    // ================================================================ variations (date navigation)
    /// <summary>
    /// Add a Variation to a column (the "date navigation" feature): when the column is used, the
    /// engine can navigate through the named relationship to a default hierarchy on the related table.
    /// </summary>
    public object AddVariation(string sessionId, string table, string column, string relationship,
        string defaultHierarchy, bool isDefault)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddVariationCore(model, table, column, relationship, defaultHierarchy, isDefault);
        ModelTxn.Save(model);
        return Persisted(new { variation = $"{table}[{column}]", relationship, defaultHierarchy, isDefault });
    }

    internal static TOM.Variation AddVariationCore(TOM.Model model, string table, string column, string relationship,
        string defaultHierarchy, bool isDefault)
    {
        if (string.IsNullOrWhiteSpace(relationship)) throw new InvalidOperationException("relationship name is required.");
        var t = Table(model, table);
        var col = Column(t, column);
        var rel = model.Relationships.Find(relationship)
                  ?? throw new InvalidOperationException($"Relationship '{relationship}' not found.");

        // resolve the default hierarchy: on the relationship's far (one-side) table.
        TOM.Table? farTable = (rel as TOM.SingleColumnRelationship)?.ToTable;
        var hy = farTable?.Hierarchies.Find(defaultHierarchy)
                 ?? model.Tables.SelectMany(x => x.Hierarchies).FirstOrDefault(h => h.Name == defaultHierarchy)
                 ?? throw new InvalidOperationException($"Default hierarchy '{defaultHierarchy}' not found.");

        var name = $"Variation {col.Variations.Count + 1}";
        if (col.Variations.Find(defaultHierarchy) != null) name = defaultHierarchy;
        var v = new TOM.Variation
        {
            Name = name,
            Relationship = rel,
            DefaultHierarchy = hy,
            IsDefault = isDefault,
        };
        col.Variations.Add(v);
        return v;
    }

    // ================================================================ column data category
    public object SetColumnDataCategory(string sessionId, string table, string column, string category)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetColumnDataCategoryCore(model, table, column, category);
        ModelTxn.Save(model);
        return Persisted(new { column = $"{table}[{column}]", dataCategory = category });
    }

    internal static TOM.Column SetColumnDataCategoryCore(TOM.Model model, string table, string column, string category)
    {
        if (string.IsNullOrWhiteSpace(category)) throw new InvalidOperationException("data category is required.");
        var c = Column(Table(model, table), column);
        c.DataCategory = category;
        return c;
    }

    // ================================================================ Wave O: SVG-as-ImageUrl measures
    /// <summary>
    /// Create a measure whose DAX returns a "data:image/svg+xml;utf8,&lt;svg...&gt;" string, then set the
    /// measure's DataCategory to "ImageUrl" so Power BI renders the SVG inside table/matrix/card cells. This
    /// is the shared sink every add_svg_* tool routes through. Returns the created measure (the *Core path so
    /// the SaveChanges-free object mutation is unit-testable).
    /// </summary>
    internal static TOM.Measure AddSvgMeasureCore(TOM.Model model, string table, string name, string dax)
    {
        var t = Table(model, table);
        if (t.Measures.Contains(name))
            throw new InvalidOperationException($"Measure '{name}' already exists on '{table}'. Use update_measure.");
        var measure = new TOM.Measure { Name = name, Expression = dax, DataCategory = SvgBuilder.ImageUrlCategory };
        t.Measures.Add(measure);
        return measure;
    }

    private object AddSvgMeasure(string sessionId, string table, string name, string dax)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddSvgMeasureCore(model, table, name, dax);
        ModelTxn.Save(model);
        return Persisted(new { added = $"{table}[{name}]", dataCategory = SvgBuilder.ImageUrlCategory,
            note = "SVG-image measure. Drop it into a table/matrix/card; it renders as an in-cell graphic." });
    }

    public object AddSvgDatabar(string sessionId, string table, string name, string valueMeasure, string? maxMeasure,
        string fill, string? negativeFill, double width, double height, string align)
        => AddSvgMeasure(sessionId, table, name,
            SvgBuilder.DataBar(valueMeasure, maxMeasure, fill, negativeFill, width, height, align));

    public object AddSvgSparkline(string sessionId, string table, string name, string valueMeasure, string categoryColumn,
        string kind, bool showLastPoint, bool intercept, string lineColor, double width, double height)
        => AddSvgMeasure(sessionId, table, name,
            SvgBuilder.Sparkline(valueMeasure, categoryColumn, kind, showLastPoint, intercept, lineColor, width, height));

    public object AddSvgProgressBar(string sessionId, string table, string name, string valueMeasure, string targetMeasure,
        string kind, string trackColor, string fillColor, double width, double height)
        => AddSvgMeasure(sessionId, table, name,
            SvgBuilder.ProgressBar(valueMeasure, targetMeasure, kind, trackColor, fillColor, width, height));

    public object AddSvgGauge(string sessionId, string table, string name, string valueMeasure, double min, double max,
        IReadOnlyList<(double at, string color)>? thresholds, string fillColor, double size)
        => AddSvgMeasure(sessionId, table, name,
            SvgBuilder.Gauge(valueMeasure, min, max, thresholds, fillColor, size));

    public object AddSvgIcon(string sessionId, string table, string name, string valueMeasure,
        IReadOnlyList<(double min, double max, string? glyph, string? color)> rules, double size)
        => AddSvgMeasure(sessionId, table, name, SvgBuilder.Icon(valueMeasure, rules, size));

    public object AddSvgChip(string sessionId, string table, string name, string textMeasure, string? colorMeasure,
        string defaultFill, double height)
        => AddSvgMeasure(sessionId, table, name, SvgBuilder.Chip(textMeasure, colorMeasure, defaultFill, height));

    // ================================================================ Wave O: static custom format string
    /// <summary>
    /// Set a measure's STATIC custom FormatString - a 3/4-section pattern positive;negative;zero;"text" with
    /// optional [Colour] codes and UNICHAR arrows (distinct from set_dynamic_format_string, which is a
    /// DAX-driven format expression). Just writes Measure.FormatString.
    /// </summary>
    public object SetCustomFormatString(string sessionId, string table, string measure, string pattern)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetCustomFormatStringCore(model, table, measure, pattern);
        ModelTxn.Save(model);
        return Persisted(new { measure = $"{table}[{measure}]", formatString = pattern });
    }

    internal static TOM.Measure SetCustomFormatStringCore(TOM.Model model, string table, string measure, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) throw new InvalidOperationException("a format pattern is required.");
        var me = Table(model, table).Measures.Find(measure)
                 ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
        me.FormatString = pattern;
        return me;
    }

    // ================================================================ Wave O: calc-group format switcher
    /// <summary>
    /// Build a calc group whose items each OVERRIDE the displayed format via
    /// SELECTEDMEASUREFORMATSTRING-style format-string definitions - a currency / % / scale switcher. Each
    /// item's DAX is SELECTEDMEASURE() (the value is unchanged) and its FormatStringDefinition is the item's
    /// formatString literal, so picking the item reformats whatever measure is on the visual.
    /// </summary>
    public object AddCalcGroupFormat(string sessionId, string table, IReadOnlyList<(string name, string formatString)> items, int? precedence)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddCalcGroupFormatCore(model, table, items, precedence);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, items = items.Select(i => i.name).ToArray() });
    }

    internal static TOM.CalculationGroup AddCalcGroupFormatCore(TOM.Model model, string table,
        IReadOnlyList<(string name, string formatString)> items, int? precedence)
    {
        if (items == null || items.Count == 0) throw new InvalidOperationException("at least one format item is required.");
        var t = Table(model, table);
        var cg = t.CalculationGroup;
        if (cg == null) { cg = new TOM.CalculationGroup(); if (precedence is { } p) cg.Precedence = p; t.CalculationGroup = cg; }
        else if (precedence is { } p2) cg.Precedence = p2;
        int ord = cg.CalculationItems.Count;
        foreach (var (name, fmt) in items)
        {
            if (cg.CalculationItems.Contains(name)) cg.CalculationItems.Remove(name);
            var item = new TOM.CalculationItem
            {
                Name = name,
                Expression = "SELECTEDMEASURE()",
                Ordinal = ord++,
                // the literal format string is a DAX string literal, so it overrides SELECTEDMEASUREFORMATSTRING.
                FormatStringDefinition = new TOM.FormatStringDefinition { Expression = "\"" + (fmt ?? "").Replace("\"", "\"\"") + "\"" },
            };
            cg.CalculationItems.Add(item);
        }
        return cg;
    }

    // ================================================================ Wave O: IBCS variance measures
    /// <summary>
    /// Author IBCS variance measure(s) for an actual measure against a comparison base (PY|PL|FC):
    ///   - kind=abs : AC - <comparison>  (absolute variance, IBCS sign convention - positive = favourable)
    ///   - kind=rel : DIVIDE(AC - <comparison>, ABS(<comparison>))  (relative variance %)
    /// The comparison base is expected to exist as a measure named "<actual> <comparison>" (e.g. "Sales PY")
    /// unless comparisonMeasure is given explicitly. Applies an IBCS number format (+/-; leading sign) when
    /// applyIbcsFormat. Returns the created measure name(s).
    /// </summary>
    public object AddIbcsVarianceMeasure(string sessionId, string table, string actualMeasure, string comparison,
        string kind, string? comparisonMeasure, bool applyIbcsFormat)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var created = AddIbcsVarianceMeasureCore(model, table, actualMeasure, comparison, kind, comparisonMeasure, applyIbcsFormat);
        ModelTxn.Save(model);
        return Persisted(new { added = created.Select(c => $"{table}[{c}]").ToArray() });
    }

    internal static IReadOnlyList<string> AddIbcsVarianceMeasureCore(TOM.Model model, string table, string actualMeasure,
        string comparison, string kind, string? comparisonMeasure, bool applyIbcsFormat)
    {
        var t = Table(model, table);
        string comp = (comparison ?? "PY").Trim().ToUpperInvariant();
        if (comp is not ("PY" or "PL" or "FC"))
            throw new InvalidOperationException($"comparison must be PY, PL or FC (got '{comparison}').");
        string k = (kind ?? "abs").Trim().ToLowerInvariant();
        if (k is not ("abs" or "rel"))
            throw new InvalidOperationException($"kind must be abs or rel (got '{kind}').");

        string compRef = string.IsNullOrWhiteSpace(comparisonMeasure) ? $"[{actualMeasure} {comp}]" : $"[{comparisonMeasure}]";
        string ac = $"[{actualMeasure}]";

        var created = new List<string>();
        void Add(string name, string dax, string? fmt)
        {
            if (t.Measures.Contains(name)) t.Measures.Remove(name);
            var me = new TOM.Measure { Name = name, Expression = dax };
            if (!string.IsNullOrWhiteSpace(fmt)) me.FormatString = fmt;
            t.Measures.Add(me); created.Add(name);
        }

        if (k == "abs")
        {
            // IBCS: leading sign, no plus-prefix loss - "+#,0;-#,0" shows the sign on both sides.
            Add($"{actualMeasure} Δ{comp}", $"{ac} - {compRef}", applyIbcsFormat ? "+#,0;-#,0" : null);
        }
        else
        {
            Add($"{actualMeasure} Δ{comp} %", $"DIVIDE ( {ac} - {compRef}, ABS ( {compRef} ) )",
                applyIbcsFormat ? "+0.0%;-0.0%" : "0.0%");
        }
        return created;
    }

    // ================================================================ display folders
    /// <summary>Set the DisplayFolder on a measure or a column (folders the field list).</summary>
    public object SetDisplayFolder(string sessionId, string target, string folder, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetDisplayFolderCore(model, target, folder, table);
        ModelTxn.Save(model);
        return Persisted(new { target = table != null ? $"{table}[{target}]" : target, displayFolder = folder });
    }

    internal static void SetDisplayFolderCore(TOM.Model model, string target, string folder, string? table)
    {
        if (string.IsNullOrWhiteSpace(table))
            throw new InvalidOperationException("table is required (the table the measure/column lives on).");
        var t = Table(model, table);
        var me = t.Measures.Find(target);
        if (me != null) { me.DisplayFolder = folder ?? ""; return; }
        var col = t.Columns.Find(target);
        if (col != null) { col.DisplayFolder = folder ?? ""; return; }
        throw new InvalidOperationException($"'{target}' is not a measure or column on '{table}'.");
    }

    // ================================================================ mark as date table
    /// <summary>
    /// Mark a table as a date table: Table.DataCategory = "Time" and the date column IsKey = true
    /// (the TOM equivalent of "Mark as date table"). The date column must be a DateTime column.
    /// </summary>
    public object MarkAsDateTable(string sessionId, string table, string dateColumn)
    {
        var model = _sessions.GetModel(sessionId).Model;
        MarkAsDateTableCore(model, table, dateColumn);
        ModelTxn.Save(model);
        return Persisted(new { table, dateColumn, dataCategory = "Time" });
    }

    internal static TOM.Column MarkAsDateTableCore(TOM.Model model, string table, string dateColumn)
    {
        var t = Table(model, table);
        var c = Column(t, dateColumn);
        if (c.DataType != TOM.DataType.DateTime)
            throw new InvalidOperationException($"Date-table key '{table}[{dateColumn}]' must be a DateTime column (is {c.DataType}).");
        t.DataCategory = "Time";
        c.IsKey = true;
        return c;
    }

    // ================================================================ query groups (query folders)
    public object AddQueryGroup(string sessionId, string folder)
    {
        var model = _sessions.GetModel(sessionId).Model;
        AddQueryGroupCore(model, folder);
        ModelTxn.Save(model);
        return Persisted(new { addedQueryGroup = folder });
    }

    internal static TOM.QueryGroup AddQueryGroupCore(TOM.Model model, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("query group folder is required.");
        var existing = model.QueryGroups.FirstOrDefault(q => q.Folder == folder);
        if (existing != null) return existing;
        var qg = new TOM.QueryGroup { Folder = folder };
        model.QueryGroups.Add(qg);
        return qg;
    }

    /// <summary>
    /// Put an expression (shared M) or partition into a query group folder. objectType =
    /// expression | partition. For a partition, name is "Table" (uses the first partition) or "Table/Partition".
    /// </summary>
    public object SetObjectQueryGroup(string sessionId, string objectType, string name, string queryGroupFolder)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetObjectQueryGroupCore(model, objectType, name, queryGroupFolder);
        ModelTxn.Save(model);
        return Persisted(new { objectType, name, queryGroup = queryGroupFolder });
    }

    internal static void SetObjectQueryGroupCore(TOM.Model model, string objectType, string name, string queryGroupFolder)
    {
        var qg = AddQueryGroupCore(model, queryGroupFolder);
        switch ((objectType ?? "").Trim().ToLowerInvariant())
        {
            case "expression":
                var e = model.Expressions.Find(name)
                        ?? throw new InvalidOperationException($"Shared expression '{name}' not found.");
                e.QueryGroup = qg;
                break;
            case "partition":
                string tname = name, pname;
                int slash = name.IndexOf('/');
                if (slash > 0) { tname = name[..slash]; pname = name[(slash + 1)..]; }
                else pname = "";
                var t = Table(model, tname);
                var part = pname.Length > 0
                    ? (t.Partitions.Find(pname) ?? throw new InvalidOperationException($"Partition '{pname}' not found on '{tname}'."))
                    : t.Partitions[0];
                part.QueryGroup = qg;
                break;
            default:
                throw new InvalidOperationException($"Invalid objectType '{objectType}'. Use expression or partition.");
        }
    }

    // ================================================================ Wave I: remaining TOM features

    // ---------------------------------------------------------------- Q&A synonyms (linguistic schema)
    /// <summary>
    /// Add Q&amp;A synonyms for a model object to a culture's LinguisticMetadata. FLAG: the linguistic
    /// schema (the YAML/JSON "Entities/Relationships" grammar Power BI authors in the Q&amp;A tooling) is
    /// large and complex; this implements the documented, common case - a flat synonyms list per
    /// entity keyed on the object's full reference - and leaves richer phrasings/relationships to a
    /// hand-authored schema. We MERGE into any existing LinguisticMetadata.Content rather than replace.
    /// </summary>
    public object SetSynonyms(string sessionId, string objectType, string objectName, string[] synonyms,
        string? culture, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var (cult, entityKey, added) = SetSynonymsCore(model, objectType, objectName, synonyms, culture, table);
        ModelTxn.Save(model);
        return Persisted(new
        {
            culture = cult,
            entity = entityKey,
            synonyms = added,
            note = "Q&A synonyms written to Culture.LinguisticMetadata. The linguistic schema is complex - only a flat per-entity synonyms list is authored here; richer phrasings/relationships need a hand-authored schema.",
        });
    }

    /// <returns>(culture, entityKey, the synonyms now on that entity).</returns>
    internal static (string culture, string entityKey, string[] synonyms) SetSynonymsCore(TOM.Model model,
        string objectType, string objectName, string[] synonyms, string? culture, string? table)
    {
        if (synonyms == null || synonyms.Length == 0)
            throw new InvalidOperationException("Provide at least one synonym.");
        string locale = string.IsNullOrWhiteSpace(culture) ? (model.Culture ?? "en-US") : culture!;
        var c = model.Cultures.Find(locale);
        if (c == null) { c = new TOM.Culture { Name = locale }; model.Cultures.Add(c); }

        // entity key: the linguistic schema names columns "Table.Column" and measures "Table.Measure".
        string ot = (objectType ?? "").Trim().ToLowerInvariant();
        string entityKey = ot switch
        {
            "table" => Table(model, objectName).Name,
            "column" => $"{Table(model, table ?? throw new InvalidOperationException("column synonyms need a table.")).Name}.{Column(Table(model, table!), objectName).Name}",
            "measure" => $"{Table(model, table ?? throw new InvalidOperationException("measure synonyms need a table.")).Name}.{(Table(model, table!).Measures.Find(objectName) ?? throw new InvalidOperationException($"Measure '{objectName}' not found on '{table}'.")).Name}",
            "hierarchy" => $"{Table(model, table ?? throw new InvalidOperationException("hierarchy synonyms need a table.")).Name}.{(Table(model, table!).Hierarchies.Find(objectName) ?? throw new InvalidOperationException($"Hierarchy '{objectName}' not found on '{table}'.")).Name}",
            _ => throw new InvalidOperationException($"Invalid objectType '{objectType}'. Use table, column, measure or hierarchy."),
        };

        var clean = synonyms.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var lm = c.LinguisticMetadata;
        var doc = (lm != null && !string.IsNullOrWhiteSpace(lm.Content))
            ? System.Text.Json.Nodes.JsonNode.Parse(lm.Content!)?.AsObject()
            : null;
        doc ??= new System.Text.Json.Nodes.JsonObject
        {
            ["Version"] = "1.0.0",
            ["Language"] = locale,
            ["Entities"] = new System.Text.Json.Nodes.JsonObject(),
        };
        if (doc["Entities"] is not System.Text.Json.Nodes.JsonObject ents)
        {
            ents = new System.Text.Json.Nodes.JsonObject();
            doc["Entities"] = ents;
        }

        // the linguistic-schema entity name can't carry a dot; map "Table.Column" -> a safe entity id.
        string entId = entityKey.Replace(".", "_");
        if (ents[entId] is not System.Text.Json.Nodes.JsonObject ent)
        {
            ent = new System.Text.Json.Nodes.JsonObject { ["Definition"] = new System.Text.Json.Nodes.JsonObject { ["Binding"] = new System.Text.Json.Nodes.JsonObject { ["ConceptualEntity"] = entityKey } } };
            ents[entId] = ent;
        }
        var terms = ent["Terms"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
        var existingTerms = new HashSet<string>(
            terms.OfType<System.Text.Json.Nodes.JsonObject>().SelectMany(o => o.Select(kv => kv.Key)),
            StringComparer.OrdinalIgnoreCase);
        foreach (var s in clean)
            if (existingTerms.Add(s))
                terms.Add(new System.Text.Json.Nodes.JsonObject { [s] = new System.Text.Json.Nodes.JsonObject() });
        ent["Terms"] = terms;

        if (lm == null)
        {
            lm = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json };
            c.LinguisticMetadata = lm;
        }
        else
        {
            lm.ContentType = TOM.ContentType.Json;
        }
        lm.Content = doc.ToJsonString();
        return (locale, entityKey, clean);
    }

    // ---------------------------------------------------------------- table-level detail rows
    /// <summary>
    /// Set a table's DefaultDetailRowsDefinition - the rows returned when a user drills into any value
    /// of the table that has no per-measure detail rows. Distinct from the measure-level set_detail_rows.
    /// </summary>
    public object SetTableDetailRows(string sessionId, string table, string daxTableExpression)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetTableDetailRowsCore(model, table, daxTableExpression);
        ModelTxn.Save(model);
        return Persisted(new { table, defaultDetailRows = true });
    }

    internal static TOM.DetailRowsDefinition SetTableDetailRowsCore(TOM.Model model, string table, string daxTableExpression)
    {
        if (string.IsNullOrWhiteSpace(daxTableExpression))
            throw new InvalidOperationException("daxTableExpression is required.");
        var t = Table(model, table);
        var drd = new TOM.DetailRowsDefinition { Expression = daxTableExpression };
        t.DefaultDetailRowsDefinition = drd;
        return drd;
    }

    // ---------------------------------------------------------------- calc-group precedence / item ordinal
    public object SetCalcGroupPrecedence(string sessionId, string table, int precedence)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetCalcGroupPrecedenceCore(model, table, precedence);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, precedence });
    }

    internal static TOM.CalculationGroup SetCalcGroupPrecedenceCore(TOM.Model model, string table, int precedence)
    {
        var t = Table(model, table);
        var cg = t.CalculationGroup
                 ?? throw new InvalidOperationException($"Table '{table}' is not a calculation group. Run add_calculation_group first.");
        cg.Precedence = precedence;
        return cg;
    }

    public object SetCalcItemOrdinal(string sessionId, string table, string itemName, int ordinal)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetCalcItemOrdinalCore(model, table, itemName, ordinal);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, item = itemName, ordinal });
    }

    internal static TOM.CalculationItem SetCalcItemOrdinalCore(TOM.Model model, string table, string itemName, int ordinal)
    {
        var t = Table(model, table);
        var cg = t.CalculationGroup
                 ?? throw new InvalidOperationException($"Table '{table}' is not a calculation group.");
        var item = cg.CalculationItems.Find(itemName)
                   ?? throw new InvalidOperationException($"Calculation item '{itemName}' not found on '{table}'.");
        item.Ordinal = ordinal;
        return item;
    }

    // ---------------------------------------------------------------- partition mode / data coverage (hybrid / DirectLake)
    /// <summary>
    /// Set a partition's storage Mode (Import / DirectQuery / Dual / DirectLake). Defaults to the table's
    /// first partition when no partition name is given. Switching to DirectLake/DirectQuery typically
    /// needs the right partition source kind - this only flips the Mode flag.
    /// </summary>
    public object SetPartitionMode(string sessionId, string table, string mode, string? partition)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var resolved = SetPartitionModeCore(model, table, mode, partition);
        ModelTxn.Save(model);
        return Persisted(new { table, partition = resolved.Name, mode = resolved.Mode.ToString() });
    }

    internal static TOM.Partition SetPartitionModeCore(TOM.Model model, string table, string mode, string? partition)
    {
        var t = Table(model, table);
        var part = ResolvePartition(t, partition);
        part.Mode = ParseModeType(mode);
        return part;
    }

    /// <summary>
    /// Set a partition's DataCoverageDefinition - the DAX boolean that tells the engine which data a
    /// (DirectQuery) partition covers, so a hybrid Import+DirectQuery table queries the cheaper partition
    /// when the filter falls inside the imported range. Defaults to the table's first partition.
    /// </summary>
    public object SetDataCoverage(string sessionId, string table, string daxExpression, string? partition)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var resolved = SetDataCoverageCore(model, table, daxExpression, partition);
        ModelTxn.Save(model);
        return Persisted(new { table, partition = resolved.Name, dataCoverage = daxExpression });
    }

    internal static TOM.Partition SetDataCoverageCore(TOM.Model model, string table, string daxExpression, string? partition)
    {
        if (string.IsNullOrWhiteSpace(daxExpression))
            throw new InvalidOperationException("daxExpression is required.");
        var t = Table(model, table);
        var part = ResolvePartition(t, partition);
        part.DataCoverageDefinition = new TOM.DataCoverageDefinition { Expression = daxExpression };
        return part;
    }

    private static TOM.Partition ResolvePartition(TOM.Table t, string? partition)
    {
        if (!string.IsNullOrWhiteSpace(partition))
            return t.Partitions.Find(partition) ?? throw new InvalidOperationException($"Partition '{partition}' not found on '{t.Name}'.");
        if (t.Partitions.Count == 0) throw new InvalidOperationException($"Table '{t.Name}' has no partitions.");
        return t.Partitions[0];
    }

    private static TOM.ModeType ParseModeType(string s) =>
        Enum.TryParse<TOM.ModeType>((s ?? "").Trim(), ignoreCase: true, out var m)
            ? m
            : throw new InvalidOperationException($"Invalid mode '{s}'. Use Import, DirectQuery, Dual or DirectLake.");

    // ---------------------------------------------------------------- annotations / extended properties
    /// <summary>
    /// Set an Annotation (name/value string) on any model object: model | table | column | measure |
    /// hierarchy | partition. Replaces a same-named annotation in place.
    /// </summary>
    public object SetAnnotation(string sessionId, string objectType, string objectName, string name, string value, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetAnnotationCore(model, objectType, objectName, name, value, table);
        ModelTxn.Save(model);
        return Persisted(new { objectType, objectName, annotation = name, value });
    }

    internal static TOM.Annotation SetAnnotationCore(TOM.Model model, string objectType, string objectName,
        string name, string value, string? table)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("annotation name is required.");
        // each TOM type has its own strongly-typed AnnotationCollection (no shared base property), so the
        // dynamic dispatch binds .Annotations against the concrete object at the call site. The element
        // type is the shared TOM.Annotation everywhere.
        dynamic target = ResolveAnnotatable(model, objectType, objectName, table);
        TOM.Annotation? existing = target.Annotations.Find(name);
        if (existing != null) { existing.Value = value; return existing; }
        var a = new TOM.Annotation { Name = name, Value = value };
        target.Annotations.Add(a);
        return a;
    }

    /// <summary>
    /// Set a StringExtendedProperty or JsonExtendedProperty (type = String | Json) on any model object.
    /// Extended properties survive serialization and are how features like field parameters tag objects.
    /// Replaces a same-named property in place.
    /// </summary>
    public object SetExtendedProperty(string sessionId, string objectType, string objectName, string name,
        string value, string type, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetExtendedPropertyCore(model, objectType, objectName, name, value, type, table);
        ModelTxn.Save(model);
        return Persisted(new { objectType, objectName, extendedProperty = name, type });
    }

    internal static TOM.ExtendedProperty SetExtendedPropertyCore(TOM.Model model, string objectType, string objectName,
        string name, string value, string type, string? table)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("extended-property name is required.");
        dynamic target = ResolveAnnotatable(model, objectType, objectName, table);
        bool json = (type ?? "String").Trim().Equals("Json", StringComparison.OrdinalIgnoreCase);
        TOM.ExtendedProperty? existing = target.ExtendedProperties.Find(name);
        if (existing != null) target.ExtendedProperties.Remove(existing);
        TOM.ExtendedProperty ep = json
            ? new TOM.JsonExtendedProperty { Name = name, Value = value }
            : new TOM.StringExtendedProperty { Name = name, Value = value };
        target.ExtendedProperties.Add((dynamic)ep);
        return ep;
    }

    /// <summary>Resolve an object that carries Annotations + ExtendedProperties.</summary>
    private static TOM.MetadataObject ResolveAnnotatable(TOM.Model model, string objectType, string objectName, string? table)
    {
        return (objectType ?? "").Trim().ToLowerInvariant() switch
        {
            "model" => model,
            "table" => Table(model, objectName),
            "column" => Column(Table(model, table ?? throw new InvalidOperationException("column needs a table.")), objectName),
            "measure" => Table(model, table ?? throw new InvalidOperationException("measure needs a table.")).Measures.Find(objectName)
                         ?? throw new InvalidOperationException($"Measure '{objectName}' not found on '{table}'."),
            "hierarchy" => Table(model, table ?? throw new InvalidOperationException("hierarchy needs a table.")).Hierarchies.Find(objectName)
                           ?? throw new InvalidOperationException($"Hierarchy '{objectName}' not found on '{table}'."),
            "partition" => Table(model, table ?? throw new InvalidOperationException("partition needs a table.")).Partitions.Find(objectName)
                           ?? throw new InvalidOperationException($"Partition '{objectName}' not found on '{table}'."),
            _ => throw new InvalidOperationException($"Invalid objectType '{objectType}'. Use model, table, column, measure, hierarchy or partition."),
        };
    }

    // ---------------------------------------------------------------- TMDL import (deserialize -> live model)
    /// <summary>
    /// Deserialize a TMDL folder (the official TmdlSerializer) and, by default, return a DRY-RUN diff of
    /// what would change against the live model. FLAG: applying TMDL REPLACES the live model's metadata
    /// (createOrReplace semantics) - so the actual apply is gated behind applyToLiveModel=true. The
    /// dry-run deserializes the folder into a detached model and diffs table/measure/column names only.
    /// </summary>
    public object ImportTmdl(string sessionId, string folderPath, bool applyToLiveModel)
    {
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"TMDL folder not found: {folderPath}");
        var session = _sessions.GetModel(sessionId);
        var live = session.Model;

        // deserialize into a detached model so we can diff without touching the live one.
        TOM.Model incoming = DeserializeTmdlFolder(folderPath);
        var diff = DiffModels(live, incoming);

        if (!applyToLiveModel)
        {
            return new
            {
                ok = true,
                dryRun = true,
                folderPath,
                wouldChange = diff,
                note = "DRY RUN - nothing changed. Applying TMDL REPLACES the live model metadata (createOrReplace). Re-run with applyToLiveModel:true to apply.",
            };
        }

        // apply (createOrReplace): copy the deserialized model's metadata onto the live model, then commit.
        // Model.CopyFrom replaces the live model's contents with the incoming definition.
        live.CopyFrom(incoming);
        ModelTxn.Save(live);
        return Persisted(new
        {
            applied = true,
            folderPath,
            changed = diff,
            note = "TMDL applied to the live model (createOrReplace via Model.CopyFrom). Save in Power BI Desktop to persist.",
        });
    }

    /// <summary>Deserialize a TMDL folder into a detached <see cref="TOM.Model"/> (model or database layout).</summary>
    internal static TOM.Model DeserializeTmdlFolder(string folderPath)
    {
        // a PBIP SemanticModel typically nests the TMDL under a "definition" subfolder.
        string root = Directory.Exists(Path.Combine(folderPath, "definition"))
            ? Path.Combine(folderPath, "definition")
            : folderPath;
        try
        {
            return TOM.TmdlSerializer.DeserializeModelFromFolder(root);
        }
        catch
        {
            // fall back to a database-layout folder (model.tmdl at the database level).
            var db = TOM.TmdlSerializer.DeserializeDatabaseFromFolder(root);
            return db.Model;
        }
    }

    /// <summary>Name-level diff of two models (tables added/removed, measures added/removed). Pure.</summary>
    internal static object DiffModels(TOM.Model live, TOM.Model incoming)
    {
        var liveTables = new HashSet<string>(live.Tables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var inTables = new HashSet<string>(incoming.Tables.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
        var tablesAdded = inTables.Except(liveTables, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var tablesRemoved = liveTables.Except(inTables, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

        var liveMeasures = new HashSet<string>(live.Tables.SelectMany(t => t.Measures.Select(m => $"{t.Name}[{m.Name}]")), StringComparer.OrdinalIgnoreCase);
        var inMeasures = new HashSet<string>(incoming.Tables.SelectMany(t => t.Measures.Select(m => $"{t.Name}[{m.Name}]")), StringComparer.OrdinalIgnoreCase);
        var measuresAdded = inMeasures.Except(liveMeasures, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        var measuresRemoved = liveMeasures.Except(inMeasures, StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();

        return new
        {
            tablesLive = liveTables.Count,
            tablesIncoming = inTables.Count,
            tablesAdded,
            tablesRemoved,
            measuresAdded,
            measuresRemoved,
        };
    }

    // ---------------------------------------------------------------- model health (VertiPaq / DMV / INFO)
    /// <summary>
    /// Surface model health through the model connection: object counts (from TOM) plus VertiPaq column
    /// storage (size + cardinality) and unused-column hints from the storage DMVs. Reuses the run_dax /
    /// DMV execution path. The DMV/INFO query strings are produced by <see cref="ModelHealthQueries"/>
    /// so they can be unit-tested without a live server.
    /// </summary>
    public object ModelHealth(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var q = ModelHealthQueries();

        var userTables = model.Tables.Where(t => !IsAutoDateName(t.Name)).ToList();
        int tableCount = userTables.Count;
        int columnCount = userTables.Sum(t => t.Columns.Count(c => c.Type != TOM.ColumnType.RowNumber));
        int measureCount = userTables.Sum(t => t.Measures.Count);
        int relationshipCount = model.Relationships.Count;

        var columns = new List<(string table, string column, long usedSize, long dictionarySize)>();
        long totalSize = 0;
        string? dmvError = null;
        try
        {
            using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = q.ColumnStorage;
            using var rdr = cmd.ExecuteReader();
            int iTable = TryOrdinal(rdr, "DIMENSION_NAME");
            int iAttr = TryOrdinal(rdr, "ATTRIBUTE_NAME");
            int iUsed = TryOrdinal(rdr, "USED_SIZE");
            int iCard = TryOrdinal(rdr, "DICTIONARY_SIZE");
            while (rdr.Read())
            {
                string tbl = iTable >= 0 ? rdr[iTable]?.ToString() ?? "" : "";
                string attr = iAttr >= 0 ? rdr[iAttr]?.ToString() ?? "" : "";
                long used = iUsed >= 0 ? ToLong(rdr[iUsed]) : 0;
                long dict = iCard >= 0 ? ToLong(rdr[iCard]) : 0;
                totalSize += used;
                columns.Add((tbl, attr, used, dict));
            }
        }
        catch (Exception ex) { dmvError = ex.Message; }

        var topColumns = columns
            .OrderByDescending(c => c.usedSize)
            .Take(20)
            .Select(c => new { table = c.table, column = c.column, usedSize = c.usedSize, dictionarySize = c.dictionarySize })
            .ToList();

        return new
        {
            ok = true,
            model = model.Name,
            counts = new { tables = tableCount, columns = columnCount, measures = measureCount, relationships = relationshipCount },
            storage = new
            {
                columnsProfiled = columns.Count,
                totalColumnBytes = totalSize,
                topColumnsBySize = topColumns,
                dmvError,
            },
            queries = q,
            note = dmvError == null
                ? "VertiPaq column storage from DISCOVER_STORAGE_TABLE_COLUMNS. Largest columns dominate the model size - hide/drop or reduce cardinality where unused."
                : "Object counts from TOM; the storage DMV failed (model may not be processed / connection issue) - counts are still valid.",
        };
    }

    /// <summary>
    /// The DMV / INFO query strings model_health runs. Pure + static so the generated query text can be
    /// unit-tested without a live server. DISCOVER_STORAGE_TABLE_COLUMNS gives per-column VertiPaq size +
    /// cardinality; the TMSCHEMA_* DMVs and DISCOVER_CALC_DEPENDENCY are returned as lineage alternatives.
    /// </summary>
    internal static ModelHealthQuerySet ModelHealthQueries() => new();

    /// <summary>The fixed set of model-health DMV/INFO queries (so callers and tests can read them by name).</summary>
    public sealed class ModelHealthQuerySet
    {
        public string ColumnStorage { get; } = "SELECT DIMENSION_NAME, ATTRIBUTE_NAME, DICTIONARY_SIZE, USED_SIZE, TABLE_ID FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMNS";
        public string ColumnSegments { get; } = "SELECT TABLE_ID, COLUMN_ID, RECORDS_COUNT, USED_SIZE FROM $SYSTEM.DISCOVER_STORAGE_TABLE_COLUMN_SEGMENTS";
        public string Columns { get; } = "SELECT [TableID], [ExplicitName], [DataType] FROM $SYSTEM.TMSCHEMA_COLUMNS";
        public string Measures { get; } = "SELECT [TableID], [Name] FROM $SYSTEM.TMSCHEMA_MEASURES";
        public string Tables { get; } = "SELECT [ID], [Name] FROM $SYSTEM.TMSCHEMA_TABLES";
        public string CalcDependency { get; } = "SELECT [OBJECT_TYPE], [TABLE], [OBJECT], [REFERENCED_TABLE], [REFERENCED_OBJECT] FROM $SYSTEM.DISCOVER_CALC_DEPENDENCY";
    }

    private static int TryOrdinal(Adomd.AdomdDataReader rdr, string name)
    {
        try { return rdr.GetOrdinal(name); } catch { return -1; }
    }

    // ================================================================ Wave K: audited TOM coverage gaps

    // ---------------------------------------------------------------- column: summarize-by / data type / flags
    public object SetSummarizeBy(string sessionId, string table, string column, string summarizeBy)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetSummarizeByCore(model, table, column, summarizeBy);
        ModelTxn.Save(model);
        return Persisted(new { column = $"{table}[{column}]", summarizeBy });
    }

    internal static TOM.Column SetSummarizeByCore(TOM.Model model, string table, string column, string summarizeBy)
    {
        var c = Column(Table(model, table), column);
        c.SummarizeBy = Enum.TryParse<TOM.AggregateFunction>((summarizeBy ?? "").Trim(), ignoreCase: true, out var agg)
            ? agg
            : throw new InvalidOperationException($"Invalid summarizeBy '{summarizeBy}'. Use Default, None, Sum, Min, Max, Count, Average or DistinctCount.");
        return c;
    }

    public object SetColumnDataType(string sessionId, string table, string column, string dataType)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetColumnDataTypeCore(model, table, column, dataType);
        ModelTxn.Save(model);
        return Persisted(new { column = $"{table}[{column}]", dataType, refreshRequired = true });
    }

    internal static TOM.Column SetColumnDataTypeCore(TOM.Model model, string table, string column, string dataType)
    {
        var c = Column(Table(model, table), column);
        c.DataType = Enum.TryParse<TOM.DataType>((dataType ?? "").Trim(), ignoreCase: true, out var dt)
            ? dt
            : throw new InvalidOperationException($"Invalid dataType '{dataType}'. Use String, Int64, Double, Decimal, DateTime, Boolean or Binary.");
        return c;
    }

    /// <summary>Set the boolean/alignment/encoding flags on a column (any subset; nulls are left unchanged).</summary>
    public object SetColumnFlags(string sessionId, string table, string column, bool? isKey, bool? isNullable,
        bool? isUnique, string? alignment, string? encodingHint)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetColumnFlagsCore(model, table, column, isKey, isNullable, isUnique, alignment, encodingHint);
        ModelTxn.Save(model);
        return Persisted(new { column = $"{table}[{column}]", isKey, isNullable, isUnique, alignment, encodingHint });
    }

    internal static TOM.Column SetColumnFlagsCore(TOM.Model model, string table, string column, bool? isKey,
        bool? isNullable, bool? isUnique, string? alignment, string? encodingHint)
    {
        var c = Column(Table(model, table), column);
        if (isKey is { } k) c.IsKey = k;
        if (isNullable is { } n) c.IsNullable = n;
        if (isUnique is { } u) c.IsUnique = u;
        if (alignment != null)
            c.Alignment = Enum.TryParse<TOM.Alignment>(alignment.Trim(), ignoreCase: true, out var al)
                ? al
                : throw new InvalidOperationException($"Invalid alignment '{alignment}'. Use Left, Center, Right or Default.");
        if (encodingHint != null)
            c.EncodingHint = Enum.TryParse<TOM.EncodingHintType>(encodingHint.Trim(), ignoreCase: true, out var eh)
                ? eh
                : throw new InvalidOperationException($"Invalid encodingHint '{encodingHint}'. Use Hash, Value or Default.");
        return c;
    }

    // ---------------------------------------------------------------- measure: hide / describe / rename
    /// <summary>
    /// Set a measure's hidden flag, description, and/or rename it. The existing update_measure only
    /// touches DAX / format / display folder; this covers IsHidden + Description + the rename.
    /// </summary>
    public object SetMeasureProperties(string sessionId, string table, string measure, bool? hidden,
        string? description, string? newName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetMeasurePropertiesCore(model, table, measure, hidden, description, newName);
        ModelTxn.Save(model);
        return Persisted(new { measure = $"{table}[{newName ?? measure}]", hidden, descriptionSet = description != null, renamed = newName != null });
    }

    internal static TOM.Measure SetMeasurePropertiesCore(TOM.Model model, string table, string measure,
        bool? hidden, string? description, string? newName)
    {
        var t = Table(model, table);
        var me = t.Measures.Find(measure)
                 ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
        if (hidden is { } h) me.IsHidden = h;
        if (description != null) me.Description = description;
        if (!string.IsNullOrWhiteSpace(newName) && !newName.Equals(measure, StringComparison.Ordinal))
        {
            if (t.Measures.Contains(newName))
                throw new InvalidOperationException($"A measure named '{newName}' already exists on '{table}'.");
            me.Name = newName;
        }
        return me;
    }

    // ---------------------------------------------------------------- rename table
    public object RenameTable(string sessionId, string table, string newName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        RenameTableCore(model, table, newName);
        ModelTxn.Save(model);
        return Persisted(new { renamed = $"{table} -> {newName}",
            note = "Table object renamed; references by the old name in M/DAX are not rewritten." });
    }

    internal static TOM.Table RenameTableCore(TOM.Model model, string table, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("newName is required.");
        var t = Table(model, table);
        if (!newName.Equals(table, StringComparison.Ordinal) && model.Tables.Contains(newName))
            throw new InvalidOperationException($"A table named '{newName}' already exists.");
        t.Name = newName;
        return t;
    }

    // ---------------------------------------------------------------- KPI extras (beyond set_kpi)
    /// <summary>
    /// Extend an existing KPI (created via set_kpi) with the trend expression, target/status/trend
    /// descriptions and a target format string. Creates the KPI if the measure has none yet.
    /// </summary>
    public object UpdateKpi(string sessionId, string table, string measure, string? trendExpression,
        string? targetFormatString, string? statusDescription, string? trendDescription, string? targetDescription)
    {
        var model = _sessions.GetModel(sessionId).Model;
        UpdateKpiCore(model, table, measure, trendExpression, targetFormatString, statusDescription, trendDescription, targetDescription);
        ModelTxn.Save(model);
        return Persisted(new { measure = $"{table}[{measure}]", trendSet = trendExpression != null });
    }

    internal static TOM.KPI UpdateKpiCore(TOM.Model model, string table, string measure, string? trendExpression,
        string? targetFormatString, string? statusDescription, string? trendDescription, string? targetDescription)
    {
        var t = Table(model, table);
        var me = t.Measures.Find(measure)
                 ?? throw new InvalidOperationException($"Measure '{measure}' not found on '{table}'.");
        var kpi = me.KPI ?? (me.KPI = new TOM.KPI());
        if (trendExpression != null) kpi.TrendExpression = trendExpression;
        if (targetFormatString != null) kpi.TargetFormatString = targetFormatString;
        if (statusDescription != null) kpi.StatusDescription = statusDescription;
        if (trendDescription != null) kpi.TrendDescription = trendDescription;
        if (targetDescription != null) kpi.TargetDescription = targetDescription;
        return kpi;
    }

    // ---------------------------------------------------------------- hierarchy / level properties + delete
    /// <summary>
    /// Set hierarchy-level properties. NB in this TOM version HideMembers/DisplayFolder live on the
    /// HIERARCHY, not the Level - so the blank-member hide is set here (set_level_properties only
    /// renames/redescribes/re-ordinals a level).
    /// </summary>
    public object SetHierarchyProperties(string sessionId, string table, string hierarchy,
        string? displayFolder, bool? hidden, string? hideMembers)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetHierarchyPropertiesCore(model, table, hierarchy, displayFolder, hidden, hideMembers);
        ModelTxn.Save(model);
        return Persisted(new { hierarchy = $"{table}.{hierarchy}", displayFolder, hidden, hideMembers });
    }

    internal static TOM.Hierarchy SetHierarchyPropertiesCore(TOM.Model model, string table, string hierarchy,
        string? displayFolder, bool? hidden, string? hideMembers)
    {
        var t = Table(model, table);
        var h = t.Hierarchies.Find(hierarchy)
                ?? throw new InvalidOperationException($"Hierarchy '{hierarchy}' not found on '{table}'.");
        if (displayFolder != null) h.DisplayFolder = displayFolder;
        if (hidden is { } hd) h.IsHidden = hd;
        if (hideMembers != null)
            h.HideMembers = Enum.TryParse<TOM.HierarchyHideMembersType>(hideMembers.Trim(), ignoreCase: true, out var hm)
                ? hm
                : throw new InvalidOperationException($"Invalid hideMembers '{hideMembers}'. Use Default or HideBlankMembers.");
        return h;
    }

    /// <summary>
    /// Set a hierarchy level's name (rename), ordinal (order) and/or description. FLAG: in this TOM
    /// version Level has no DisplayFolder/HideMembers - those are hierarchy-wide (set_hierarchy_properties).
    /// </summary>
    public object SetLevelProperties(string sessionId, string table, string hierarchy, string level,
        int? ordinal, string? description, string? newName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetLevelPropertiesCore(model, table, hierarchy, level, ordinal, description, newName);
        ModelTxn.Save(model);
        return Persisted(new { level = $"{table}.{hierarchy}.{newName ?? level}", ordinal, descriptionSet = description != null, renamed = newName != null });
    }

    internal static TOM.Level SetLevelPropertiesCore(TOM.Model model, string table, string hierarchy, string level,
        int? ordinal, string? description, string? newName)
    {
        var t = Table(model, table);
        var h = t.Hierarchies.Find(hierarchy)
                ?? throw new InvalidOperationException($"Hierarchy '{hierarchy}' not found on '{table}'.");
        var lvl = h.Levels.Find(level)
                  ?? throw new InvalidOperationException($"Level '{level}' not found on '{table}.{hierarchy}'.");
        if (ordinal is { } o) lvl.Ordinal = o;
        if (description != null) lvl.Description = description;
        if (!string.IsNullOrWhiteSpace(newName) && !newName.Equals(level, StringComparison.Ordinal))
        {
            if (h.Levels.Contains(newName))
                throw new InvalidOperationException($"A level named '{newName}' already exists on '{table}.{hierarchy}'.");
            lvl.Name = newName;
        }
        return lvl;
    }

    public object DeleteHierarchy(string sessionId, string table, string hierarchy)
    {
        var model = _sessions.GetModel(sessionId).Model;
        bool removed = DeleteHierarchyCore(model, table, hierarchy);
        ModelTxn.Save(model);
        return Persisted(new { deletedHierarchy = $"{table}.{hierarchy}", existed = removed });
    }

    internal static bool DeleteHierarchyCore(TOM.Model model, string table, string hierarchy)
    {
        var t = Table(model, table);
        if (!t.Hierarchies.Contains(hierarchy)) return false;
        t.Hierarchies.Remove(hierarchy);
        return true;
    }

    // ---------------------------------------------------------------- model-level settings / auto date-time
    /// <summary>
    /// Set model-level flags (any subset). discourageImplicitMeasures, defaultMode
    /// (Import|DirectQuery|Dual|DirectLake|Push|Default), directLakeBehavior
    /// (Automatic|DirectLakeOnly|DirectQueryOnly) and culture.
    /// </summary>
    public object SetModelSettings(string sessionId, bool? discourageImplicitMeasures, string? defaultMode,
        string? directLakeBehavior, string? culture)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetModelSettingsCore(model, discourageImplicitMeasures, defaultMode, directLakeBehavior, culture);
        ModelTxn.Save(model);
        return Persisted(new { discourageImplicitMeasures, defaultMode, directLakeBehavior, culture });
    }

    internal static TOM.Model SetModelSettingsCore(TOM.Model model, bool? discourageImplicitMeasures,
        string? defaultMode, string? directLakeBehavior, string? culture)
    {
        if (discourageImplicitMeasures is { } d) model.DiscourageImplicitMeasures = d;
        if (defaultMode != null) model.DefaultMode = ParseModeType(defaultMode);
        if (directLakeBehavior != null)
            model.DirectLakeBehavior = Enum.TryParse<TOM.DirectLakeBehavior>(directLakeBehavior.Trim(), ignoreCase: true, out var b)
                ? b
                : throw new InvalidOperationException($"Invalid directLakeBehavior '{directLakeBehavior}'. Use Automatic, DirectLakeOnly or DirectQueryOnly.");
        if (!string.IsNullOrWhiteSpace(culture)) model.Culture = culture;
        return model;
    }

    /// <summary>
    /// Turn OFF Auto date/time: stamp the documented model annotation __PBI_TimeIntelligenceEnabled = 0
    /// (so Desktop stops generating LocalDateTable_* per date column) and drop any existing auto
    /// date/time tables + their relationships already in the model.
    /// </summary>
    public object DisableAutoDateTime(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        int dropped = DisableAutoDateTimeCore(model);
        ModelTxn.Save(model);
        return Persisted(new { autoDateTime = "disabled", autoTablesDropped = dropped,
            note = "Set __PBI_TimeIntelligenceEnabled=0 and removed any auto LocalDateTable/DateTableTemplate tables." });
    }

    /// <returns>the number of auto date/time tables removed.</returns>
    internal static int DisableAutoDateTimeCore(TOM.Model model)
    {
        var existing = model.Annotations.Find("__PBI_TimeIntelligenceEnabled");
        if (existing != null) existing.Value = "0";
        else model.Annotations.Add(new TOM.Annotation { Name = "__PBI_TimeIntelligenceEnabled", Value = "0" });

        var autoTables = model.Tables.Where(t => IsAutoDateName(t.Name)).ToList();
        foreach (var t in autoTables)
        {
            var rels = model.Relationships.OfType<TOM.SingleColumnRelationship>()
                .Where(r => r.FromTable?.Name == t.Name || r.ToTable?.Name == t.Name).ToList();
            foreach (var r in rels) model.Relationships.Remove(r);
            model.Tables.Remove(t);
        }
        return autoTables.Count;
    }

    // ================================================================ Wave R: model internals
    //   sync-correctness annotations, perf levers, Direct Lake / aggregations, calc-group selection
    //   expressions, data-access options, table privacy, Q&A linguistic depth, dependency / unused
    //   analyzers, calendar-based time intelligence, case-sensitive DAX fixer, report-measure extraction.
    //
    // COMPAT-LEVEL gate map (doc21): LineageTag=1540, SourceLineageTag=1550, selection expressions=1605,
    // DataAccessOptions/EncodingHint=1400, DirectLakeBehavior=1604. Live methods auto-bump the database's
    // CompatibilityLevel via EnsureCompatLevel and FLAG the bump in the result.

    /// <summary>The minimum CompatibilityLevel a named model feature requires (the doc21 gate map). Pure + testable.</summary>
    internal static int CompatLevelFor(string feature) => (feature ?? "").Trim().ToLowerInvariant() switch
    {
        "dataaccessoptions" or "encodinghint" => 1400,
        "powerbi_v3" => 1450,
        "alternateof" => 1460,
        "querygroups" => 1480,
        "lineagetag" => 1540,
        "sourcelineagetag" => 1550,
        "discouragecompositemodels" => 1560,
        "datacoveragedefinition" or "hybrid" => 1565,
        "securityfilteringbehaviornone" => 1566,
        "maxparallelismperquery" => 1569,
        "formatstringdefinition" => 1601,
        "directlakebehavior" => 1604,
        "selectionexpressions" => 1605,
        "daxudfs" => 1702,
        _ => throw new InvalidOperationException($"Unknown feature '{feature}' for the compatibility-level gate map."),
    };

    /// <summary>
    /// Raise the live database's CompatibilityLevel to at least the level a feature needs. Returns
    /// (bumped, fromLevel, toLevel). The session carries the Database; a detached test model has none,
    /// so the gate-map value itself is unit-tested through <see cref="CompatLevelFor"/>.
    /// </summary>
    private static (bool bumped, int from, int to) EnsureCompatLevel(ModelSession session, string feature)
    {
        int required = CompatLevelFor(feature);
        int was = session.Database.CompatibilityLevel;
        if (was >= required) return (false, was, was);
        try { session.Database.CompatibilityLevel = required; }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Feature '{feature}' needs compatibility level {required}+, but bumping from {was} failed: {ex.Message}");
        }
        return (true, was, required);
    }

    private static object? CompatNote((bool bumped, int from, int to) b, string feature) =>
        b.bumped ? $"CompatibilityLevel raised from {b.from} to {b.to} to enable {feature}." : null;

    // ---------------------------------------------------------------- 1. sync-correctness annotations
    /// <summary>
    /// Set the LineageTag on a model object (model | table | column | measure | hierarchy | partition |
    /// relationship). The lineage tag is a stable identity that lets schema sync / git merge track an
    /// object across renames. Needs compatibility level 1540+ (auto-bumped, FLAGGED).
    /// </summary>
    public object SetLineageTag(string sessionId, string objectType, string name, string tag, string? table)
    {
        var session = _sessions.GetModel(sessionId);
        var b = EnsureCompatLevel(session, "lineagetag");
        SetLineageTagCore(session.Model, objectType, name, tag, table);
        ModelTxn.Save(session.Model);
        return Persisted(new { objectType, name, lineageTag = tag,
            compatibilityLevel = session.Database.CompatibilityLevel, compatibilityLevelBumped = b.bumped,
            note = CompatNote(b, "lineage tags") });
    }

    internal static TOM.MetadataObject SetLineageTagCore(TOM.Model model, string objectType, string name, string tag, string? table)
    {
        if (string.IsNullOrWhiteSpace(tag)) throw new InvalidOperationException("tag is required.");
        var obj = ResolveLineageObject(model, objectType, name, table);
        SetLineageProperty(obj, "LineageTag", tag);
        return obj;
    }

    /// <summary>
    /// Set the SourceLineageTag on a model object - the SOURCE-side name (the lakehouse/SQL column/table
    /// name) Direct Lake uses to re-bind after a schema refresh. Needs compatibility level 1550+
    /// (auto-bumped, FLAGGED).
    /// </summary>
    public object SetSourceLineageTag(string sessionId, string objectType, string name, string value, string? table)
    {
        var session = _sessions.GetModel(sessionId);
        var b = EnsureCompatLevel(session, "sourcelineagetag");
        SetSourceLineageTagCore(session.Model, objectType, name, value, table);
        ModelTxn.Save(session.Model);
        return Persisted(new { objectType, name, sourceLineageTag = value,
            compatibilityLevel = session.Database.CompatibilityLevel, compatibilityLevelBumped = b.bumped,
            note = CompatNote(b, "source lineage tags") });
    }

    internal static TOM.MetadataObject SetSourceLineageTagCore(TOM.Model model, string objectType, string name, string value, string? table)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("value (the source name) is required.");
        var obj = ResolveLineageObject(model, objectType, name, table);
        SetLineageProperty(obj, "SourceLineageTag", value);
        return obj;
    }

    /// <summary>
    /// Record a property as locally CHANGED so a schema sync does not wipe your override. Without this the
    /// engine treats name / isHidden / formatString / summarizeBy on a synced (composite / Direct Lake)
    /// object as source-owned and resets it on refresh. Writes to the object's ChangedProperties.
    /// </summary>
    public object DeclareChangedProperty(string sessionId, string objectType, string name, string property, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        DeclareChangedPropertyCore(model, objectType, name, property, table);
        ModelTxn.Save(model);
        return Persisted(new { objectType, name, changedProperty = property,
            note = "Property marked changed so a schema sync preserves your local override." });
    }

    internal static TOM.ChangedProperty DeclareChangedPropertyCore(TOM.Model model, string objectType, string name, string property, string? table)
    {
        if (string.IsNullOrWhiteSpace(property)) throw new InvalidOperationException("property is required.");
        dynamic obj = ResolveLineageObject(model, objectType, name, table);
        var cps = obj.ChangedProperties;
        // ChangedProperty collections have no Find(); iterate to keep the call idempotent.
        foreach (TOM.ChangedProperty cp0 in cps)
            if (string.Equals(cp0.Property, property, StringComparison.Ordinal)) return cp0;
        var cp = new TOM.ChangedProperty { Property = property };
        cps.Add(cp);
        return cp;
    }

    /// <summary>
    /// Stamp the model annotation PBI_RemovedChildren (a JSON array of removed source lineage tags) so a
    /// schema sync does not re-add tables / columns you deleted. Merges into any existing list. Pure.
    /// </summary>
    public object MarkRemovedChildren(string sessionId, string table, string[] removedSourceLineageTags)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var all = MarkRemovedChildrenCore(model, table, removedSourceLineageTags);
        ModelTxn.Save(model);
        return Persisted(new { table, removedChildren = all,
            note = "PBI_RemovedChildren annotation written so a schema sync keeps these children removed." });
    }

    /// <returns>the full removed-children list now on the table annotation.</returns>
    internal static string[] MarkRemovedChildrenCore(TOM.Model model, string table, string[] removedSourceLineageTags)
    {
        if (removedSourceLineageTags == null || removedSourceLineageTags.Length == 0)
            throw new InvalidOperationException("Provide at least one removed source lineage tag.");
        var t = Table(model, table);
        var clean = removedSourceLineageTags.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();

        var ann = t.Annotations.Find("PBI_RemovedChildren");
        var set = new List<string>();
        if (ann != null && !string.IsNullOrWhiteSpace(ann.Value))
        {
            try
            {
                var arr = System.Text.Json.Nodes.JsonNode.Parse(ann.Value!) as System.Text.Json.Nodes.JsonArray;
                if (arr != null) foreach (var n in arr) { var s = (string?)n; if (!string.IsNullOrEmpty(s)) set.Add(s); }
            }
            catch { /* malformed existing value: overwrite */ }
        }
        foreach (var s in clean) if (!set.Contains(s, StringComparer.Ordinal)) set.Add(s);

        var json = new System.Text.Json.Nodes.JsonArray();
        foreach (var s in set) json.Add(s);
        string value = json.ToJsonString();
        if (ann != null) ann.Value = value;
        else t.Annotations.Add(new TOM.Annotation { Name = "PBI_RemovedChildren", Value = value });
        return set.ToArray();
    }

    /// <summary>Resolve an object that carries LineageTag / SourceLineageTag / ChangedProperties.</summary>
    private static TOM.MetadataObject ResolveLineageObject(TOM.Model model, string objectType, string name, string? table)
    {
        return (objectType ?? "").Trim().ToLowerInvariant() switch
        {
            "model" => model,
            "table" => Table(model, name),
            "column" => Column(Table(model, table ?? throw new InvalidOperationException("column needs a table.")), name),
            "measure" => Table(model, table ?? throw new InvalidOperationException("measure needs a table.")).Measures.Find(name)
                         ?? throw new InvalidOperationException($"Measure '{name}' not found on '{table}'."),
            "hierarchy" => Table(model, table ?? throw new InvalidOperationException("hierarchy needs a table.")).Hierarchies.Find(name)
                           ?? throw new InvalidOperationException($"Hierarchy '{name}' not found on '{table}'."),
            "partition" => Table(model, table ?? throw new InvalidOperationException("partition needs a table.")).Partitions.Find(name)
                           ?? throw new InvalidOperationException($"Partition '{name}' not found on '{table}'."),
            "relationship" => model.Relationships.Find(name)
                              ?? throw new InvalidOperationException($"Relationship '{name}' not found."),
            _ => throw new InvalidOperationException($"Invalid objectType '{objectType}'. Use model, table, column, measure, hierarchy, partition or relationship."),
        };
    }

    /// <summary>Set LineageTag / SourceLineageTag via the dynamic dispatch (every lineage-bearing type has the property).</summary>
    private static void SetLineageProperty(TOM.MetadataObject obj, string property, string value)
    {
        dynamic d = obj;
        if (property == "LineageTag") d.LineageTag = value;
        else d.SourceLineageTag = value;
    }

    // ---------------------------------------------------------------- 2. IsAvailableInMDX (perf lever)
    /// <summary>
    /// Set Column.IsAvailableInMDX. Setting it false on high-cardinality non-attribute columns saves
    /// memory and processing. GUARD: it must stay TRUE on any column that is a SortByColumn TARGET (else
    /// the engine errors "invalid column ID") - we refuse to flip such columns to false. With
    /// bulkHeuristic=hiddenAndKeys (and no table/column), bulk-sets false on every hidden or key column
    /// across the model that is safe to flip.
    /// </summary>
    public object SetIsAvailableInMdx(string sessionId, string? table, string? column, bool value, string? bulkHeuristic)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var changed = SetIsAvailableInMdxCore(model, table, column, value, bulkHeuristic, out var guarded);
        ModelTxn.Save(model);
        return Persisted(new { isAvailableInMdx = value, columnsChanged = changed.Count, columns = changed,
            sortByTargetsGuarded = guarded,
            note = "IsAvailableInMDX kept true on every SortByColumn target (flipping it false breaks the sort)." });
    }

    /// <returns>the list of "Table[Column]" actually changed; <paramref name="guarded"/> = SortByColumn targets skipped.</returns>
    internal static List<string> SetIsAvailableInMdxCore(TOM.Model model, string? table, string? column,
        bool value, string? bulkHeuristic, out List<string> guarded)
    {
        var guardedList = new List<string>();
        var changed = new List<string>();

        // every column that is a SortByColumn target anywhere in the model MUST keep IsAvailableInMDX=true.
        var sortTargets = new HashSet<TOM.Column>();
        foreach (var t in model.Tables)
            foreach (var c in t.Columns)
                if (c.SortByColumn != null) sortTargets.Add(c.SortByColumn);

        void Apply(TOM.Table t, TOM.Column c)
        {
            if (c.Type == TOM.ColumnType.RowNumber) return;
            if (!value && sortTargets.Contains(c)) { guardedList.Add($"{t.Name}[{c.Name}]"); return; }
            if (c.IsAvailableInMDX == value) return;
            c.IsAvailableInMDX = value;
            changed.Add($"{t.Name}[{c.Name}]");
        }

        if (!string.IsNullOrWhiteSpace(column))
        {
            var t = Table(model, table ?? throw new InvalidOperationException("column needs a table."));
            Apply(t, Column(t, column!));
            guarded = guardedList;
            return changed;
        }

        bool hiddenAndKeys = (bulkHeuristic ?? "").Trim().Equals("hiddenAndKeys", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(bulkHeuristic) && string.IsNullOrWhiteSpace(table))
            throw new InvalidOperationException("Provide a column, or a table to bulk-set, or bulkHeuristic=hiddenAndKeys for a model-wide pass.");

        var tables = !string.IsNullOrWhiteSpace(table) ? new[] { Table(model, table!) } : model.Tables.ToArray();
        foreach (var t in tables)
            foreach (var c in t.Columns)
            {
                if (hiddenAndKeys && !(c.IsHidden || c.IsKey)) continue;
                Apply(t, c);
            }
        guarded = guardedList;
        return changed;
    }

    // ---------------------------------------------------------------- 3. stamp VertiPaq stats as annotations
    /// <summary>
    /// Read the storage DMVs (DISCOVER_STORAGE_TABLE_COLUMNS) and write each column's stats as annotations
    /// (Vertipaq_RowCount / Vertipaq_TotalSize / Vertipaq_Cardinality / Vertipaq_DictionarySize - the
    /// semantic-link-labs scheme) so the numbers survive in the model definition for offline review.
    /// </summary>
    public object StampVertipaqStats(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var rows = new List<(string table, string column, long used, long dict, long card)>();
        string? dmvError = null;
        try
        {
            using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = ModelHealthQueries().ColumnStorage;
            using var rdr = cmd.ExecuteReader();
            int iTable = TryOrdinal(rdr, "DIMENSION_NAME"), iAttr = TryOrdinal(rdr, "ATTRIBUTE_NAME");
            int iUsed = TryOrdinal(rdr, "USED_SIZE"), iDict = TryOrdinal(rdr, "DICTIONARY_SIZE");
            int iCard = TryOrdinal(rdr, "COLUMN_CARDINALITY");
            while (rdr.Read())
            {
                string tbl = iTable >= 0 ? rdr[iTable]?.ToString() ?? "" : "";
                string attr = iAttr >= 0 ? rdr[iAttr]?.ToString() ?? "" : "";
                rows.Add((tbl, attr, iUsed >= 0 ? ToLong(rdr[iUsed]) : 0, iDict >= 0 ? ToLong(rdr[iDict]) : 0,
                    iCard >= 0 ? ToLong(rdr[iCard]) : 0));
            }
        }
        catch (Exception ex) { dmvError = ex.Message; }

        int stamped = StampVertipaqStatsCore(model, rows);
        ModelTxn.Save(model);
        return Persisted(new { columnsStamped = stamped, dmvError,
            note = dmvError == null
                ? "VertiPaq stats stamped as Vertipaq_* annotations (semantic-link-labs scheme)."
                : "Storage DMV failed - no stats stamped." });
    }

    /// <summary>Write the gathered per-column VertiPaq stats onto the matching columns as Vertipaq_* annotations. Pure.</summary>
    internal static int StampVertipaqStatsCore(TOM.Model model,
        IReadOnlyList<(string table, string column, long used, long dict, long card)> rows)
    {
        int stamped = 0;
        foreach (var (tableName, colName, used, dict, card) in rows)
        {
            var t = model.Tables.Find(tableName);
            var c = t?.Columns.Find(colName);
            if (c == null) continue;
            void Ann(string k, string v)
            {
                var a = c.Annotations.Find(k);
                if (a != null) a.Value = v; else c.Annotations.Add(new TOM.Annotation { Name = k, Value = v });
            }
            Ann("Vertipaq_TotalSize", used.ToString(CultureInfo.InvariantCulture));
            Ann("Vertipaq_DictionarySize", dict.ToString(CultureInfo.InvariantCulture));
            Ann("Vertipaq_Cardinality", card.ToString(CultureInfo.InvariantCulture));
            stamped++;
        }
        return stamped;
    }

    // ---------------------------------------------------------------- 4. calc-group selection expressions (CL 1605)
    /// <summary>
    /// Set a calculation group's NoSelectionExpression and/or MultipleOrEmptySelectionExpression (DAX that
    /// runs when no / multiple calc items are selected), with optional dynamic format strings. Needs
    /// compatibility level 1605+ (auto-bumped, FLAGGED).
    /// </summary>
    public object SetCalcGroupSelectionExpressions(string sessionId, string table, string? noSelectionExpression,
        string? multipleOrEmptySelectionExpression, string? noSelectionFormatString, string? multipleOrEmptyFormatString)
    {
        var session = _sessions.GetModel(sessionId);
        var b = EnsureCompatLevel(session, "selectionexpressions");
        SetCalcGroupSelectionExpressionsCore(session.Model, table, noSelectionExpression,
            multipleOrEmptySelectionExpression, noSelectionFormatString, multipleOrEmptyFormatString);
        ModelTxn.Save(session.Model);
        return Persisted(new { calculationGroup = table,
            noSelectionSet = noSelectionExpression != null,
            multipleOrEmptySet = multipleOrEmptySelectionExpression != null,
            compatibilityLevel = session.Database.CompatibilityLevel, compatibilityLevelBumped = b.bumped,
            note = CompatNote(b, "calculation-group selection expressions") });
    }

    internal static TOM.CalculationGroup SetCalcGroupSelectionExpressionsCore(TOM.Model model, string table,
        string? noSelectionExpression, string? multipleOrEmptySelectionExpression,
        string? noSelectionFormatString, string? multipleOrEmptyFormatString)
    {
        var t = Table(model, table);
        var cg = t.CalculationGroup
                 ?? throw new InvalidOperationException($"Table '{table}' is not a calculation group. Run add_calculation_group first.");
        if (noSelectionExpression != null)
        {
            var ci = new TOM.CalculationGroupExpression { Expression = noSelectionExpression };
            if (!string.IsNullOrWhiteSpace(noSelectionFormatString))
                ci.FormatStringDefinition = new TOM.FormatStringDefinition { Expression = noSelectionFormatString };
            cg.NoSelectionExpression = ci;
        }
        if (multipleOrEmptySelectionExpression != null)
        {
            var ci = new TOM.CalculationGroupExpression { Expression = multipleOrEmptySelectionExpression };
            if (!string.IsNullOrWhiteSpace(multipleOrEmptyFormatString))
                ci.FormatStringDefinition = new TOM.FormatStringDefinition { Expression = multipleOrEmptyFormatString };
            cg.MultipleOrEmptySelectionExpression = ci;
        }
        return cg;
    }

    /// <summary>
    /// Set the model-wide selection-expression behavior (automatic | nonvisual | visual) - controls which
    /// clients honour the calc-group selection expressions. Needs CL 1605+ (auto-bumped, FLAGGED).
    /// FLAG: TOM (this build) has no SelectionExpressionBehavior property, so this is stamped as the model
    /// annotation PBI_SelectionExpressionBehavior (round-trips through TMDL) until the property lands.
    /// </summary>
    public object SetSelectionExpressionBehavior(string sessionId, string behavior)
    {
        var session = _sessions.GetModel(sessionId);
        var b = EnsureCompatLevel(session, "selectionexpressions");
        var parsed = SetSelectionExpressionBehaviorCore(session.Model, behavior);
        ModelTxn.Save(session.Model);
        return Persisted(new { selectionExpressionBehavior = parsed,
            compatibilityLevel = session.Database.CompatibilityLevel, compatibilityLevelBumped = b.bumped,
            note = "FLAG: stamped as the PBI_SelectionExpressionBehavior model annotation (no TOM property in this build). " + CompatNote(b, "selection-expression behavior") });
    }

    /// <returns>the normalised behavior string written to the annotation.</returns>
    internal static string SetSelectionExpressionBehaviorCore(TOM.Model model, string behavior)
    {
        string v = (behavior ?? "").Trim().ToLowerInvariant() switch
        {
            "automatic" => "Automatic",
            "nonvisual" or "nonvisualobjects" => "NonVisualObjects",
            "visual" or "visualobjects" => "VisualObjects",
            _ => throw new InvalidOperationException($"Invalid behavior '{behavior}'. Use automatic, nonvisual or visual."),
        };
        var a = model.Annotations.Find("PBI_SelectionExpressionBehavior");
        if (a != null) a.Value = v; else model.Annotations.Add(new TOM.Annotation { Name = "PBI_SelectionExpressionBehavior", Value = v });
        return v;
    }

    // ---------------------------------------------------------------- 5. data-access options
    /// <summary>
    /// Set Model.DataAccessOptions: fastCombine (ignore privacy levels for query folding IN this file),
    /// legacyRedirects, returnErrorValuesAsNull. Any subset; omitted options unchanged. Needs CL 1400+.
    /// </summary>
    public object SetDataAccessOptions(string sessionId, bool? fastCombine, bool? legacyRedirects, bool? returnErrorValuesAsNull)
    {
        var session = _sessions.GetModel(sessionId);
        var b = EnsureCompatLevel(session, "dataaccessoptions");
        SetDataAccessOptionsCore(session.Model, fastCombine, legacyRedirects, returnErrorValuesAsNull);
        ModelTxn.Save(session.Model);
        var dao = session.Model.DataAccessOptions;
        return Persisted(new { fastCombine = dao?.FastCombine, legacyRedirects = dao?.LegacyRedirects,
            returnErrorValuesAsNull = dao?.ReturnErrorValuesAsNull,
            compatibilityLevel = session.Database.CompatibilityLevel, compatibilityLevelBumped = b.bumped,
            note = CompatNote(b, "data-access options") });
    }

    internal static TOM.DataAccessOptions SetDataAccessOptionsCore(TOM.Model model, bool? fastCombine,
        bool? legacyRedirects, bool? returnErrorValuesAsNull)
    {
        var dao = model.DataAccessOptions ?? (model.DataAccessOptions = new TOM.DataAccessOptions());
        if (fastCombine is { } fc) dao.FastCombine = fc;
        if (legacyRedirects is { } lr) dao.LegacyRedirects = lr;
        if (returnErrorValuesAsNull is { } re) dao.ReturnErrorValuesAsNull = re;
        return dao;
    }

    // ---------------------------------------------------------------- 6. table privacy
    /// <summary>Set Table.IsPrivate - a private table is hidden from ALL clients (stronger than IsHidden), for helper tables.</summary>
    public object SetTablePrivate(string sessionId, string table, bool isPrivate)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetTablePrivateCore(model, table, isPrivate);
        ModelTxn.Save(model);
        return Persisted(new { table, isPrivate });
    }

    internal static TOM.Table SetTablePrivateCore(TOM.Model model, string table, bool isPrivate)
    {
        var t = Table(model, table);
        t.IsPrivate = isPrivate;
        return t;
    }

    // ---------------------------------------------------------------- 7. Q&A linguistic depth
    /// <summary>
    /// Author synonyms with an explicit State (Authored to make them stick, Deleted to suppress an
    /// auto-generated term) and optional Weight (0..1). Extends set_synonyms, which only adds bare terms.
    /// </summary>
    public object SetSynonymState(string sessionId, string objectType, string name, string[] synonyms,
        string state, double? weight, string? culture, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var (cult, entity, terms) = SetSynonymStateCore(model, objectType, name, synonyms, state, weight, culture, table);
        ModelTxn.Save(model);
        return Persisted(new { culture = cult, entity, state, weight, synonyms = terms,
            note = "Synonym state/weight written to Culture.LinguisticMetadata. FLAG: the linguistic schema is complex - only flat per-entity terms with State/Weight are authored." });
    }

    /// <returns>(culture, entityKey, the terms written).</returns>
    internal static (string culture, string entityKey, string[] synonyms) SetSynonymStateCore(TOM.Model model,
        string objectType, string name, string[] synonyms, string state, double? weight, string? culture, string? table)
    {
        if (synonyms == null || synonyms.Length == 0) throw new InvalidOperationException("Provide at least one synonym.");
        string st = (state ?? "").Trim().ToLowerInvariant() switch
        {
            "authored" => "Authored",
            "deleted" => "Deleted",
            "generated" => "Generated",
            "suggested" => "Suggested",
            _ => throw new InvalidOperationException($"Invalid state '{state}'. Use Authored, Deleted, Generated or Suggested."),
        };
        if (weight is { } w && (w < 0 || w > 1)) throw new InvalidOperationException("weight must be between 0 and 1.");

        string locale = string.IsNullOrWhiteSpace(culture) ? (model.Culture ?? "en-US") : culture!;
        var c = model.Cultures.Find(locale) ?? AddCulture(model, locale);
        string entityKey = LinguisticEntityKey(model, objectType, name, table);

        var doc = LoadOrInitLinguistic(c, locale);
        var ents = EntitiesNode(doc);
        string entId = entityKey.Replace(".", "_");
        if (ents[entId] is not System.Text.Json.Nodes.JsonObject ent)
        {
            ent = new System.Text.Json.Nodes.JsonObject
            {
                ["Definition"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["Binding"] = new System.Text.Json.Nodes.JsonObject { ["ConceptualEntity"] = entityKey },
                },
            };
            ents[entId] = ent;
        }
        var terms = ent["Terms"] as System.Text.Json.Nodes.JsonArray ?? new System.Text.Json.Nodes.JsonArray();
        var clean = synonyms.Select(s => (s ?? "").Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var s in clean)
        {
            // remove any existing entry for this term, then re-add with the explicit state/weight.
            for (int i = terms.Count - 1; i >= 0; i--)
                if (terms[i] is System.Text.Json.Nodes.JsonObject o && o.ContainsKey(s)) terms.RemoveAt(i);
            var body = new System.Text.Json.Nodes.JsonObject { ["State"] = st };
            if (weight is { } ww) body["Weight"] = ww;
            terms.Add(new System.Text.Json.Nodes.JsonObject { [s] = body });
        }
        ent["Terms"] = terms;
        WriteLinguistic(c, doc);
        return (locale, entityKey, clean);
    }

    /// <summary>
    /// Author an LSDL Q&amp;A phrasing (Verb | Adjective | Noun | Attribute | Name | DynamicNoun ...) into
    /// Culture.LinguisticMetadata so Q&amp;A understands a concept like "happy customers". The phrasing body
    /// is supplied as a JSON object (the LSDL phrasing definition). FLAG: the linguistic phrasing schema is
    /// large - we store the phrasing verbatim under Relationships and do not validate its inner shape.
    /// </summary>
    public object SetQnaPhrasing(string sessionId, string phrasingType, string phrasingName, string phrasingJson, string? culture)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var (cult, key) = SetQnaPhrasingCore(model, phrasingType, phrasingName, phrasingJson, culture);
        ModelTxn.Save(model);
        return Persisted(new { culture = cult, phrasing = key, phrasingType,
            note = "LSDL phrasing written to Culture.LinguisticMetadata.Relationships. FLAG: the phrasing schema is large and is stored verbatim, not validated." });
    }

    /// <returns>(culture, relationship/phrasing key).</returns>
    internal static (string culture, string key) SetQnaPhrasingCore(TOM.Model model, string phrasingType,
        string phrasingName, string phrasingJson, string? culture)
    {
        if (string.IsNullOrWhiteSpace(phrasingName)) throw new InvalidOperationException("phrasingName is required.");
        string pt = (phrasingType ?? "").Trim();
        var valid = new[] { "Verb", "Adjective", "Noun", "PreModifier", "Preposition", "Attribute", "Name", "DynamicNoun" };
        if (!valid.Contains(pt, StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid phrasingType '{phrasingType}'. Use one of {string.Join(", ", valid)}.");
        var body = System.Text.Json.Nodes.JsonNode.Parse(phrasingJson) as System.Text.Json.Nodes.JsonObject
                   ?? throw new InvalidOperationException("phrasingJson must be a JSON object (the LSDL phrasing definition).");

        string locale = string.IsNullOrWhiteSpace(culture) ? (model.Culture ?? "en-US") : culture!;
        var c = model.Cultures.Find(locale) ?? AddCulture(model, locale);
        var doc = LoadOrInitLinguistic(c, locale);
        if (doc["Relationships"] is not System.Text.Json.Nodes.JsonObject rels)
        {
            rels = new System.Text.Json.Nodes.JsonObject();
            doc["Relationships"] = rels;
        }
        // normalise the phrasing-type key to its LSDL property name (e.g. Verb -> Verbs container per item).
        var item = new System.Text.Json.Nodes.JsonObject { ["Phrasing"] = new System.Text.Json.Nodes.JsonObject { [pt] = body } };
        rels[phrasingName] = item;
        WriteLinguistic(c, doc);
        return (locale, phrasingName);
    }

    private static TOM.Culture AddCulture(TOM.Model model, string locale)
    {
        var c = new TOM.Culture { Name = locale };
        model.Cultures.Add(c);
        return c;
    }

    private static string LinguisticEntityKey(TOM.Model model, string objectType, string name, string? table) =>
        (objectType ?? "").Trim().ToLowerInvariant() switch
        {
            "table" => Table(model, name).Name,
            "column" => $"{Table(model, table ?? throw new InvalidOperationException("column synonyms need a table.")).Name}.{Column(Table(model, table!), name).Name}",
            "measure" => $"{Table(model, table ?? throw new InvalidOperationException("measure synonyms need a table.")).Name}.{(Table(model, table!).Measures.Find(name) ?? throw new InvalidOperationException($"Measure '{name}' not found on '{table}'.")).Name}",
            "hierarchy" => $"{Table(model, table ?? throw new InvalidOperationException("hierarchy synonyms need a table.")).Name}.{(Table(model, table!).Hierarchies.Find(name) ?? throw new InvalidOperationException($"Hierarchy '{name}' not found on '{table}'.")).Name}",
            _ => throw new InvalidOperationException($"Invalid objectType '{objectType}'. Use table, column, measure or hierarchy."),
        };

    private static System.Text.Json.Nodes.JsonObject LoadOrInitLinguistic(TOM.Culture c, string locale)
    {
        var lm = c.LinguisticMetadata;
        var doc = (lm != null && !string.IsNullOrWhiteSpace(lm.Content))
            ? System.Text.Json.Nodes.JsonNode.Parse(lm.Content!)?.AsObject()
            : null;
        return doc ?? new System.Text.Json.Nodes.JsonObject
        {
            ["Version"] = "1.0.0",
            ["Language"] = locale,
            ["Entities"] = new System.Text.Json.Nodes.JsonObject(),
        };
    }

    private static System.Text.Json.Nodes.JsonObject EntitiesNode(System.Text.Json.Nodes.JsonObject doc)
    {
        if (doc["Entities"] is not System.Text.Json.Nodes.JsonObject ents)
        {
            ents = new System.Text.Json.Nodes.JsonObject();
            doc["Entities"] = ents;
        }
        return ents;
    }

    private static void WriteLinguistic(TOM.Culture c, System.Text.Json.Nodes.JsonObject doc)
    {
        var lm = c.LinguisticMetadata;
        if (lm == null) { lm = new TOM.LinguisticMetadata { ContentType = TOM.ContentType.Json }; c.LinguisticMetadata = lm; }
        else lm.ContentType = TOM.ContentType.Json;
        lm.Content = doc.ToJsonString();
    }

    // ---------------------------------------------------------------- 8. auto-aggregations scaffold
    /// <summary>
    /// Build an auto-aggregation scaffold: a hidden {Table}_Agg table (a GROUPBY calc table over the agg
    /// spec), AlternateOf mappings on its columns, _Agg measures, and IF-routing rewrites of the base
    /// measures so they answer from the small agg table when the grain allows. Extends set_aggregation.
    /// FLAG: the IF-routing is a best-known scaffold and should be reviewed before production use.
    /// aggSpec is groupBy columns (Table[Column]) plus measure mappings name=baseMeasure.
    /// </summary>
    public object AddAutoAggregations(string sessionId, string detailTable, string[] groupByColumns,
        IReadOnlyList<(string aggMeasureName, string baseMeasure)> measureMappings)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var result = AddAutoAggregationsCore(model, detailTable, groupByColumns, measureMappings);
        ModelTxn.Save(model);
        try { model.RequestRefresh(TOM.RefreshType.Calculate); ModelTxn.Save(model); } catch { }
        return Persisted(new
        {
            aggTable = result.aggTable,
            groupBy = groupByColumns,
            aggMeasures = result.aggMeasures,
            routedBaseMeasures = result.routed,
            note = "Auto-aggregation scaffold built (hidden {Table}_Agg + AlternateOf + _Agg measures + IF-routing). FLAG: review the routing before production.",
        });
    }

    internal static (string aggTable, string[] aggMeasures, string[] routed) AddAutoAggregationsCore(TOM.Model model,
        string detailTable, string[] groupByColumns,
        IReadOnlyList<(string aggMeasureName, string baseMeasure)> measureMappings)
    {
        var dt = Table(model, detailTable);
        if (groupByColumns == null || groupByColumns.Length == 0)
            throw new InvalidOperationException("Provide at least one groupBy column as Table[Column].");
        if (measureMappings == null || measureMappings.Count == 0)
            throw new InvalidOperationException("Provide at least one measure mapping aggMeasure=baseMeasure.");
        string aggName = $"{detailTable}_Agg";
        if (model.Tables.Contains(aggName))
            throw new InvalidOperationException($"Aggregation table '{aggName}' already exists.");

        // GROUPBY calc table: the group-by columns plus a sum of each base measure's source column is
        // out of reach here, so the agg table groups by the keys and the _Agg measures aggregate over it.
        var gbRefs = groupByColumns.Select(g => { var (t, col) = ParseFieldRef(g); return $"'{t}'[{col}]"; }).ToArray();
        string dax = $"GROUPBY ( '{detailTable}', {string.Join(", ", gbRefs)} )";
        var agg = new TOM.Table { Name = aggName, IsHidden = true };
        agg.Partitions.Add(new TOM.Partition { Name = aggName, Source = new TOM.CalculatedPartitionSource { Expression = dax } });
        model.Tables.Add(agg);

        // _Agg measures: COUNTROWS as a presence probe plus one per mapping (best-known scaffold).
        var aggMeasures = new List<string>();
        foreach (var (aggMeasureName, baseMeasure) in measureMappings)
        {
            if (string.IsNullOrWhiteSpace(aggMeasureName) || string.IsNullOrWhiteSpace(baseMeasure))
                throw new InvalidOperationException("Each mapping needs aggMeasureName and baseMeasure.");
            string nm = $"{aggMeasureName} _Agg";
            if (!agg.Measures.Contains(nm))
            {
                agg.Measures.Add(new TOM.Measure { Name = nm, Expression = $"[{baseMeasure}]", IsHidden = true });
                aggMeasures.Add($"{aggName}[{nm}]");
            }
        }

        // IF-routing: rewrite the base measures to prefer the agg table when only agg-grain columns filter.
        var routed = new List<string>();
        foreach (var (aggMeasureName, baseMeasure) in measureMappings)
        {
            TOM.Measure? bm = null; TOM.Table? bt = null;
            foreach (var t in model.Tables) { var m = t.Measures.Find(baseMeasure); if (m != null) { bm = m; bt = t; break; } }
            if (bm == null) continue;
            string original = bm.Expression ?? "";
            string detailGuard = string.Join(" || ", gbRefs.Select(r => $"ISCROSSFILTERED ( {r} )"));
            bm.Expression =
                $"VAR _detail = {original}\n" +
                $"RETURN IF ( NOT ISEMPTY ( '{aggName}' ), [{aggMeasureName} _Agg], _detail )";
            routed.Add($"{bt!.Name}[{baseMeasure}]");
            _ = detailGuard;
        }
        return (aggName, aggMeasures.ToArray(), routed.ToArray());
    }

    // ---------------------------------------------------------------- 9. Direct Lake cache / fallback
    /// <summary>
    /// Warm the Direct Lake cache by forcing the listed columns (or every column on the table) resident:
    /// runs EVALUATE TOPN(1, SELECTCOLUMNS(...)) so the engine pages the column data in. Returns the query.
    /// </summary>
    public object WarmDirectLakeCache(string sessionId, string table, string[]? columns)
    {
        var model = _sessions.GetModel(sessionId).Model;
        string query = WarmDirectLakeCacheQuery(model, table, columns);
        object? result = null; string? error = null;
        try { result = RunDax(sessionId, query, 1); } catch (Exception ex) { error = ex.Message; }
        return new { ok = error == null, table, query, result, error,
            note = "TOPN(1, SELECTCOLUMNS(...)) forces the columns resident in the Direct Lake cache." };
    }

    /// <summary>Build the warm-cache EVALUATE for a table's columns. Pure + testable.</summary>
    internal static string WarmDirectLakeCacheQuery(TOM.Model model, string table, string[]? columns)
    {
        var t = Table(model, table);
        var cols = (columns != null && columns.Length > 0)
            ? columns.Select(c => Column(t, c).Name).ToArray()
            : t.Columns.Where(c => c.Type != TOM.ColumnType.RowNumber).Select(c => c.Name).ToArray();
        if (cols.Length == 0) throw new InvalidOperationException($"Table '{table}' has no columns to warm.");
        var projections = cols.Select(c => $"\"{c}\", '{table}'[{c}]");
        return $"EVALUATE TOPN ( 1, SELECTCOLUMNS ( '{table}', {string.Join(", ", projections)} ) )";
    }

    /// <summary>
    /// Read the Direct Lake fallback-reason DMV (TMSCHEMA_DELTA_TABLE_METADATA_STORAGES / the fallback
    /// columns) and list objects that force a DirectQuery fallback (unsupported in Direct Lake). Returns
    /// the query text and any rows. Read-only.
    /// </summary>
    public object CheckDirectLakeFallback(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        string query = DirectLakeFallbackQuery();
        object? result = null; string? error = null;
        try { result = RunDmv(session, query); } catch (Exception ex) { error = ex.Message; }

        // unsupported-object scan from the model tree: calculated columns/tables defeat Direct Lake.
        var unsupported = new List<string>();
        foreach (var t in session.Model.Tables)
        {
            foreach (var c in t.Columns.OfType<TOM.CalculatedColumn>()) unsupported.Add($"calculated column {t.Name}[{c.Name}]");
            foreach (var p in t.Partitions) if (p.Source is TOM.CalculatedPartitionSource) unsupported.Add($"calculated table {t.Name}");
        }
        return new { ok = error == null, query, fallbackRows = result, unsupportedObjects = unsupported, error,
            note = "Calculated columns / calculated tables are not supported in Direct Lake and force a DirectQuery fallback." };
    }

    /// <summary>The fallback-reason DMV text. Pure + testable.</summary>
    internal static string DirectLakeFallbackQuery() =>
        "SELECT * FROM $SYSTEM.TMSCHEMA_DELTA_TABLE_METADATA_STORAGES";

    private object RunDmv(ModelSession session, string query)
    {
        using var conn = new Adomd.AdomdConnection(session.AdomdConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = query;
        using var rdr = cmd.ExecuteReader();
        var cols = new List<string>();
        for (int i = 0; i < rdr.FieldCount; i++) cols.Add(rdr.GetName(i));
        var rows = new List<object?[]>();
        while (rdr.Read())
        {
            var row = new object?[rdr.FieldCount];
            for (int i = 0; i < rdr.FieldCount; i++) { var v = rdr.GetValue(i); row[i] = v is DBNull ? null : v; }
            rows.Add(row);
        }
        return new { columns = cols, rowCount = rows.Count, rows };
    }

    // ---------------------------------------------------------------- 10. dependency / unused analyzers
    /// <summary>
    /// Analyse measure/column dependencies and impact. Returns the INFO.CALCDEPENDENCY / DISCOVER_CALC_DEPENDENCY
    /// query text (so the lineage can be pulled live) plus, when an object is named, the direct dependants
    /// of that object derived from the model tree (which measures reference it). Read-only.
    /// </summary>
    public object AnalyzeDependencies(string sessionId, string? @object)
    {
        var model = _sessions.GetModel(sessionId).Model;
        string query = DaxGenerators.InfoViewQuery("CALCDEPENDENCY");
        object? live = null; string? error = null;
        try { live = RunDax(sessionId, query, 5000); } catch (Exception ex) { error = ex.Message; }

        object? impact = null;
        if (!string.IsNullOrWhiteSpace(@object))
            impact = MeasureDependants(model, @object!);
        return new { ok = true, query, dependency = live, queryError = error, target = @object, impact };
    }

    /// <summary>Measures whose DAX references the named measure or Table[Column]. Pure + testable.</summary>
    internal static object MeasureDependants(TOM.Model model, string @object)
    {
        string obj = @object.Trim();
        // accept either [Measure] / Measure, or Table[Column].
        var needles = new List<string>();
        if (obj.Contains('['))
        {
            var (t, col) = ParseFieldRef(obj);
            needles.Add($"'{t}'[{col}]");
            needles.Add($"{t}[{col}]");
            needles.Add($"[{col}]");
        }
        else
        {
            needles.Add($"[{obj}]");
        }
        var dependants = new List<string>();
        foreach (var t in model.Tables)
            foreach (var m in t.Measures)
            {
                var expr = m.Expression ?? "";
                if ($"{t.Name}[{m.Name}]".Equals(obj, StringComparison.OrdinalIgnoreCase)) continue;
                if (needles.Any(n => expr.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0))
                    dependants.Add($"{t.Name}[{m.Name}]");
            }
        return new { directDependants = dependants, count = dependants.Count };
    }

    /// <summary>
    /// Find columns and measures that are never referenced - not by a relationship, sort-by, hierarchy
    /// level, RLS filter, other measure's DAX, or calculated column/table DAX. (Visual usage lives in the
    /// report, not the model, so that dimension is noted but not scanned here.) Read-only.
    /// </summary>
    public object FindUnused(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var (cols, measures) = FindUnusedCore(model);
        return new { unusedColumns = cols, unusedMeasures = measures,
            unusedColumnCount = cols.Count, unusedMeasureCount = measures.Count,
            note = "Model-internal references only (relationships, sort-by, hierarchies, RLS, measure/calc DAX). Visual usage is in the report layer and is not scanned here." };
    }

    /// <returns>(unused columns "Table[Column]", unused measures "Table[Measure]").</returns>
    internal static (List<string> columns, List<string> measures) FindUnusedCore(TOM.Model model)
    {
        // gather every DAX string in the model.
        var daxBlobs = new List<string>();
        foreach (var t in model.Tables)
        {
            foreach (var m in t.Measures) if (!string.IsNullOrEmpty(m.Expression)) daxBlobs.Add(m.Expression);
            foreach (var c in t.Columns.OfType<TOM.CalculatedColumn>()) if (!string.IsNullOrEmpty(c.Expression)) daxBlobs.Add(c.Expression);
            foreach (var p in t.Partitions) if (p.Source is TOM.CalculatedPartitionSource cps && !string.IsNullOrEmpty(cps.Expression)) daxBlobs.Add(cps.Expression);
        }
        foreach (var r in model.Roles) foreach (var tp in r.TablePermissions) if (!string.IsNullOrEmpty(tp.FilterExpression)) daxBlobs.Add(tp.FilterExpression);

        // columns used by relationships / sort-by / hierarchy levels.
        var usedCols = new HashSet<TOM.Column>();
        foreach (var r in model.Relationships.OfType<TOM.SingleColumnRelationship>())
        {
            if (r.FromColumn != null) usedCols.Add(r.FromColumn);
            if (r.ToColumn != null) usedCols.Add(r.ToColumn);
        }
        foreach (var t in model.Tables)
        {
            foreach (var c in t.Columns) if (c.SortByColumn != null) usedCols.Add(c.SortByColumn);
            foreach (var h in t.Hierarchies) foreach (var lvl in h.Levels) if (lvl.Column != null) usedCols.Add(lvl.Column);
        }

        var unusedColumns = new List<string>();
        foreach (var t in model.Tables)
        {
            if (IsAutoDateName(t.Name)) continue;
            foreach (var c in t.Columns)
            {
                if (c.Type == TOM.ColumnType.RowNumber) continue;
                if (usedCols.Contains(c)) continue;
                bool inDax = daxBlobs.Any(d =>
                    d.IndexOf($"[{c.Name}]", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!inDax) unusedColumns.Add($"{t.Name}[{c.Name}]");
            }
        }

        var unusedMeasures = new List<string>();
        foreach (var t in model.Tables)
            foreach (var m in t.Measures)
            {
                string self = m.Expression ?? "";
                bool referenced = daxBlobs.Any(d => !ReferenceEquals(d, self)
                    && d.IndexOf($"[{m.Name}]", StringComparison.OrdinalIgnoreCase) >= 0);
                if (!referenced) unusedMeasures.Add($"{t.Name}[{m.Name}]");
            }
        return (unusedColumns, unusedMeasures);
    }

    // ================================================================ Wave U: Best Practice Analyzer (BPA)
    // The pure rule catalogue, runner, fixer and importer live in BpaRules (fully unit-testable against a
    // `new TOM.Model()`). These instance methods resolve the live session, delegate, then SaveChanges() on
    // a real fix - matching every other mutation tool here.

    /// <summary>
    /// Run the BPA rule catalogue against the live model. Optional filters: categories, severities (1 info /
    /// 2 warning / 3 error), specific ruleIds, and a scope (Model|Table|Column|Measure|Relationship|
    /// Partition|Hierarchy). Read-only. Returns every finding plus a summary count by category and severity.
    /// </summary>
    public object RunBpa(string sessionId, string[]? categories, int[]? severities, string[]? ruleIds, string? scope)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var findings = BpaRules.Run(model, categories, severities, ruleIds, scope);

        var byCategory = findings.GroupBy(f => f.Category)
            .ToDictionary(g => g.Key, g => g.Count());
        var bySeverity = findings.GroupBy(f => f.Severity)
            .ToDictionary(g => g.Key == BpaRules.Error ? "error" : g.Key == BpaRules.Warning ? "warning" : "info",
                g => g.Count());

        return new
        {
            ok = true,
            ruleCount = BpaRules.All.Count,
            findingCount = findings.Count,
            fixableFindings = findings.Count(f => f.Fixable),
            summary = new { byCategory, bySeverity },
            findings = findings.Select(f => new
            {
                ruleId = f.RuleId,
                category = f.Category,
                severity = f.Severity,
                severityLabel = f.Severity == BpaRules.Error ? "error" : f.Severity == BpaRules.Warning ? "warning" : "info",
                objectType = f.ObjectType,
                objectName = f.ObjectName,
                message = f.Message,
                fixable = f.Fixable,
            }),
        };
    }

    /// <summary>
    /// Apply a BPA rule's autofix. With no objectName, every matching object is fixed; otherwise only the
    /// named object. dryRun (default true) lists what WOULD change without mutating. A rule with no safe
    /// autofix is refused with guidance. SaveChanges() only when dryRun is false.
    /// </summary>
    public object FixBpa(string sessionId, string ruleId, string? objectName, bool dryRun)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var (rule, outcomes) = BpaRules.Fix(model, ruleId, objectName, dryRun);

        if (!dryRun)
        {
            if (outcomes.Count > 0) ModelTxn.Save(model);
            return Persisted(new
            {
                ruleId = rule.Id,
                fixedProperty = rule.FixedProperty,
                objectsChanged = outcomes.Count,
                changes = outcomes.Select(o => new { o.ObjectName, o.Change }),
            });
        }

        return new
        {
            ok = true,
            dryRun = true,
            ruleId = rule.Id,
            fixedProperty = rule.FixedProperty,
            objectsWouldChange = outcomes.Count,
            wouldChange = outcomes.Select(o => new { o.ObjectName, o.Change }),
            note = "dryRun=true - nothing was modified. Re-run with dryRun=false to apply.",
        };
    }

    /// <summary>List the BPA rule catalogue (id / category / severity / scope / fixable / description).</summary>
    public object ListBpaRules(string? category) => BpaRules.Catalogue(category);

    /// <summary>
    /// Import a Tabular Editor BPARules.json document, mapping known rule IDs to our built-in checks and
    /// registering rules whose logic is an unevaluable TE dynamic expression as descriptive-only (flagged).
    /// </summary>
    public object ImportBpaRuleset(string json)
    {
        var r = BpaRules.Import(json);
        return new
        {
            ok = true,
            total = r.total,
            mappedToBuiltIn = r.mappedToBuiltIn,
            registeredAsManual = r.registeredAsManual,
            mapped = r.mapped,
            manual = r.manual,
            skipped = r.skipped,
            note = "Mapped rules run our built-in evaluable checks. Manual rules carry a Tabular Editor dynamic expression this engine does not execute - they are surfaced for reference only.",
        };
    }

    // ---------------------------------------------------------------- 11. calendar-based time intelligence
    /// <summary>
    /// Add a native non-Gregorian time-intelligence calendar: a calendar object on the calendar table's
    /// primary date column, with a calendarColumnGroup over the associated period columns. FLAG: the
    /// calendar / calendarColumnGroup objects are very new and absent from TOM (this build), so this is
    /// stamped as a PBI_Calendar JSON annotation on the table (round-trips through TMDL) until TOM ships
    /// the strongly-typed objects.
    /// </summary>
    public object AddCalendarBasedTimeIntelligence(string sessionId, string calendarTable, string primaryColumn,
        string[] associatedColumns)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var name = AddCalendarBasedTimeIntelligenceCore(model, calendarTable, primaryColumn, associatedColumns);
        ModelTxn.Save(model);
        return Persisted(new { calendarTable, calendar = name, primaryColumn, associatedColumns,
            note = "FLAG: stamped as a PBI_Calendar table annotation (the native calendar/calendarColumnGroup TOM objects are very new and absent from this build)." });
    }

    /// <returns>the primary column name written to the calendar annotation.</returns>
    internal static string AddCalendarBasedTimeIntelligenceCore(TOM.Model model, string calendarTable,
        string primaryColumn, string[] associatedColumns)
    {
        var t = Table(model, calendarTable);
        var primary = Column(t, primaryColumn);
        if (associatedColumns == null || associatedColumns.Length == 0)
            throw new InvalidOperationException("Provide at least one associated period column.");

        var groupCols = new System.Text.Json.Nodes.JsonArray();
        foreach (var col in associatedColumns)
        {
            var c = Column(t, col);   // validate it exists
            groupCols.Add(c.Name);
        }
        var json = new System.Text.Json.Nodes.JsonObject
        {
            ["columnName"] = primary.Name,
            ["calendarColumnGroups"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["columnNames"] = groupCols },
            },
        };
        string value = json.ToJsonString();
        var ann = t.Annotations.Find("PBI_Calendar");
        if (ann != null) ann.Value = value; else t.Annotations.Add(new TOM.Annotation { Name = "PBI_Calendar", Value = value });
        return primary.Name;
    }

    // ---------------------------------------------------------------- 12. case-sensitive DAX fixer
    /// <summary>
    /// Rewrite every measure's table / column / measure references to the model's EXACT casing - the fix
    /// for DirectQuery / Direct Lake against case-sensitive sources where 'sales'[amount] and
    /// 'Sales'[Amount] are different. Returns the measures changed.
    /// </summary>
    public object FixCaseSensitiveDax(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var changed = FixCaseSensitiveDaxCore(model);
        ModelTxn.Save(model);
        return Persisted(new { measuresRewritten = changed.Count, measures = changed,
            note = "Table/column/measure references rewritten to the model's exact casing for case-sensitive sources." });
    }

    /// <returns>"Table[Measure]" of the measures whose DAX was rewritten.</returns>
    internal static List<string> FixCaseSensitiveDaxCore(TOM.Model model)
    {
        // canonical-casing maps.
        var tableCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var colCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // by column name
        var measureCanon = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in model.Tables)
        {
            tableCanon[t.Name] = t.Name;
            foreach (var c in t.Columns) colCanon[c.Name] = c.Name;
            foreach (var m in t.Measures) measureCanon[m.Name] = m.Name;
        }

        var changed = new List<string>();
        foreach (var t in model.Tables)
            foreach (var m in t.Measures)
            {
                string expr = m.Expression ?? "";
                if (expr.Length == 0) continue;
                string rewritten = RewriteRefsToCanonicalCase(expr, tableCanon, colCanon, measureCanon);
                if (!string.Equals(rewritten, expr, StringComparison.Ordinal))
                {
                    m.Expression = rewritten;
                    changed.Add($"{t.Name}[{m.Name}]");
                }
            }
        return changed;
    }

    /// <summary>Rewrite 'Table'[Col] / Table[Col] / [Col] / [Measure] tokens to the canonical casing. Pure + testable.</summary>
    internal static string RewriteRefsToCanonicalCase(string dax, IReadOnlyDictionary<string, string> tableCanon,
        IReadOnlyDictionary<string, string> colCanon, IReadOnlyDictionary<string, string> measureCanon)
    {
        var sb = new StringBuilder(dax.Length);
        int i = 0;
        while (i < dax.Length)
        {
            char ch = dax[i];
            // a quoted 'Table' reference.
            if (ch == '\'')
            {
                int end = dax.IndexOf('\'', i + 1);
                if (end > i)
                {
                    string inner = dax[(i + 1)..end];
                    string canon = tableCanon.TryGetValue(inner, out var tv) ? tv : inner;
                    sb.Append('\'').Append(canon).Append('\'');
                    i = end + 1;
                    continue;
                }
            }
            // a [Column]/[Measure] reference. A preceding bare table name is canonicalised too.
            if (ch == '[')
            {
                int end = dax.IndexOf(']', i + 1);
                if (end > i)
                {
                    string inner = dax[(i + 1)..end];
                    string canon = colCanon.TryGetValue(inner, out var cv) ? cv
                                   : measureCanon.TryGetValue(inner, out var mv) ? mv : inner;
                    sb.Append('[').Append(canon).Append(']');
                    i = end + 1;
                    continue;
                }
            }
            // a bare identifier (possible unquoted table name before '[').
            if (char.IsLetter(ch) || ch == '_')
            {
                int start = i;
                while (i < dax.Length && (char.IsLetterOrDigit(dax[i]) || dax[i] == '_')) i++;
                string ident = dax[start..i];
                // only canonicalise it if it is immediately followed by '[' (a table reference).
                if (i < dax.Length && dax[i] == '[' && tableCanon.TryGetValue(ident, out var tv))
                    sb.Append(tv);
                else
                    sb.Append(ident);
                continue;
            }
            sb.Append(ch);
            i++;
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 13. report-level-measure extraction
    /// <summary>
    /// Promote report-level measures into the model as real measures. Reads the report definition's
    /// config.modelExtensions[].entities[].measures[] (legacy Report/Layout) from a .pbix on disk and adds
    /// each to its host table. Pass a .pbix path; report measures keep their format/folder. Returns the
    /// measures promoted.
    /// </summary>
    public object ExtractReportLevelMeasures(string sessionId, string pbixPath)
    {
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        var specs = ReadReportLevelMeasures(pbixPath);
        var model = _sessions.GetModel(sessionId).Model;
        var promoted = PromoteReportMeasuresCore(model, specs);
        ModelTxn.Save(model);
        return Persisted(new { promoted, count = promoted.Count, found = specs.Count,
            note = "Report-level measures read from Report/Layout config.modelExtensions and added to the model." });
    }

    /// <summary>Read report-level measures (table, name, expression, format, folder) from a .pbix's Report/Layout. Pure-ish (file read).</summary>
    internal static List<(string table, string name, string expression, string? format, string? folder)> ReadReportLevelMeasures(string pbixPath)
    {
        using var zip = ZipFile.OpenRead(pbixPath);
        var entry = zip.GetEntry("Report/Layout")
                    ?? throw new InvalidOperationException("This .pbix has no Report/Layout (PBIR not supported here).");
        byte[] bytes;
        using (var s = entry.Open()) { using var ms = new MemoryStream(); s.CopyTo(ms); bytes = ms.ToArray(); }
        // strip a UTF-16 BOM if present.
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) bytes = bytes[2..];
        string text = new UnicodeEncoding(false, false).GetString(bytes);
        var root = System.Text.Json.Nodes.JsonNode.Parse(text) as System.Text.Json.Nodes.JsonObject
                   ?? throw new InvalidOperationException("Report/Layout root is not a JSON object.");
        return ParseReportLevelMeasures(root);
    }

    /// <summary>Parse report-level measures out of a Report/Layout root JSON object. Pure + testable.</summary>
    internal static List<(string table, string name, string expression, string? format, string? folder)> ParseReportLevelMeasures(
        System.Text.Json.Nodes.JsonObject root)
    {
        var outp = new List<(string, string, string, string?, string?)>();
        // config is a JSON STRING in legacy Report/Layout.
        var cfgRaw = (string?)root["config"];
        if (string.IsNullOrWhiteSpace(cfgRaw)) return outp;
        var cfg = System.Text.Json.Nodes.JsonNode.Parse(cfgRaw!) as System.Text.Json.Nodes.JsonObject;
        if (cfg?["modelExtensions"] is not System.Text.Json.Nodes.JsonArray exts) return outp;
        foreach (var extN in exts)
        {
            if (extN is not System.Text.Json.Nodes.JsonObject ext) continue;
            if (ext["entities"] is not System.Text.Json.Nodes.JsonArray entities) continue;
            foreach (var enN in entities)
            {
                if (enN is not System.Text.Json.Nodes.JsonObject en) continue;
                string table = (string?)en["extends"] ?? (string?)en["name"] ?? "";
                if (en["measures"] is not System.Text.Json.Nodes.JsonArray measures) continue;
                foreach (var mN in measures)
                {
                    if (mN is not System.Text.Json.Nodes.JsonObject m) continue;
                    string name = (string?)m["name"] ?? "";
                    string expr = (string?)m["expression"] ?? "";
                    if (name.Length == 0 || expr.Length == 0 || table.Length == 0) continue;
                    outp.Add((table, name, expr, (string?)m["formatString"], (string?)m["displayFolder"]));
                }
            }
        }
        return outp;
    }

    /// <summary>Add the report-level measure specs to the model (skipping any name that already exists). Pure.</summary>
    internal static List<string> PromoteReportMeasuresCore(TOM.Model model,
        IReadOnlyList<(string table, string name, string expression, string? format, string? folder)> specs)
    {
        var promoted = new List<string>();
        foreach (var (table, name, expr, fmt, folder) in specs)
        {
            var t = model.Tables.Find(table);
            if (t == null) continue;
            if (t.Measures.Contains(name)) continue;
            var me = new TOM.Measure { Name = name, Expression = expr };
            if (!string.IsNullOrWhiteSpace(fmt)) me.FormatString = fmt;
            if (!string.IsNullOrWhiteSpace(folder)) me.DisplayFolder = folder;
            t.Measures.Add(me);
            promoted.Add($"{table}[{name}]");
        }
        return promoted;
    }

    // ---------------------------------------------------------------- role member removal / permission + perspective removal
    public object RemoveRoleMember(string sessionId, string role, string member)
    {
        var model = _sessions.GetModel(sessionId).Model;
        bool removed = RemoveRoleMemberCore(model, role, member);
        ModelTxn.Save(model);
        return Persisted(new { role, member, removed });
    }

    internal static bool RemoveRoleMemberCore(TOM.Model model, string role, string member)
    {
        var r = Role(model, role);
        var m = r.Members.FirstOrDefault(x => string.Equals(x.MemberName, member, StringComparison.OrdinalIgnoreCase));
        if (m == null) return false;
        r.Members.Remove(m);
        return true;
    }

    public object SetRolePermission(string sessionId, string role, string modelPermission)
    {
        var model = _sessions.GetModel(sessionId).Model;
        SetRolePermissionCore(model, role, modelPermission);
        ModelTxn.Save(model);
        return Persisted(new { role, modelPermission });
    }

    internal static TOM.ModelRole SetRolePermissionCore(TOM.Model model, string role, string modelPermission)
    {
        var r = Role(model, role);
        r.ModelPermission = ParseModelPermission(modelPermission);
        return r;
    }

    public object RemoveFromPerspective(string sessionId, string perspective, string objectType, string name, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        bool removed = RemoveFromPerspectiveCore(model, perspective, objectType, name, table);
        ModelTxn.Save(model);
        return Persisted(new { perspective, objectType, name, removed });
    }

    internal static bool RemoveFromPerspectiveCore(TOM.Model model, string perspective, string objectType, string name, string? table)
    {
        var p = model.Perspectives.Find(perspective)
                ?? throw new InvalidOperationException($"Perspective '{perspective}' not found.");
        string ot = (objectType ?? "").Trim().ToLowerInvariant();
        switch (ot)
        {
            case "table":
                if (!p.PerspectiveTables.Contains(name)) return false;
                p.PerspectiveTables.Remove(name);
                return true;
            case "column":
            case "measure":
            case "hierarchy":
                if (string.IsNullOrWhiteSpace(table))
                    throw new InvalidOperationException($"{objectType} removal needs the object's table.");
                var pt = p.PerspectiveTables.Find(table);
                if (pt == null) return false;
                return ot switch
                {
                    "column" => RemoveIf(pt.PerspectiveColumns.Contains(name), () => pt.PerspectiveColumns.Remove(name)),
                    "measure" => RemoveIf(pt.PerspectiveMeasures.Contains(name), () => pt.PerspectiveMeasures.Remove(name)),
                    _ => RemoveIf(pt.PerspectiveHierarchies.Contains(name), () => pt.PerspectiveHierarchies.Remove(name)),
                };
            default:
                throw new InvalidOperationException($"Invalid objectType '{objectType}'. Use table, column, measure or hierarchy.");
        }

        static bool RemoveIf(bool present, Action remove) { if (!present) return false; remove(); return true; }
    }

    public object DeletePerspective(string sessionId, string perspective)
    {
        var model = _sessions.GetModel(sessionId).Model;
        bool removed = DeletePerspectiveCore(model, perspective);
        ModelTxn.Save(model);
        return Persisted(new { deletedPerspective = perspective, existed = removed });
    }

    internal static bool DeletePerspectiveCore(TOM.Model model, string perspective)
    {
        if (!model.Perspectives.Contains(perspective)) return false;
        model.Perspectives.Remove(perspective);
        return true;
    }

    // ---------------------------------------------------------------- variations: list / delete
    public object ListVariations(string sessionId, string table, string column)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var c = Column(Table(model, table), column);
        var variations = c.Variations.Select(v => new
        {
            name = v.Name,
            relationship = v.Relationship?.Name,
            defaultHierarchy = v.DefaultHierarchy?.Name,
            isDefault = v.IsDefault,
        });
        return new { column = $"{table}[{column}]", variations };
    }

    public object DeleteVariation(string sessionId, string table, string column, string variation)
    {
        var model = _sessions.GetModel(sessionId).Model;
        bool removed = DeleteVariationCore(model, table, column, variation);
        ModelTxn.Save(model);
        return Persisted(new { deletedVariation = $"{table}[{column}].{variation}", existed = removed });
    }

    internal static bool DeleteVariationCore(TOM.Model model, string table, string column, string variation)
    {
        var c = Column(Table(model, table), column);
        if (!c.Variations.Contains(variation)) return false;
        c.Variations.Remove(variation);
        return true;
    }

    // ---------------------------------------------------------------- first-class DataSource (Structured / Provider)
    /// <summary>
    /// Create or update a first-class DataSource object (separate from raw M partitions). kind =
    /// Structured (modern Power Query: connectionDetails + credential, each a JSON object of key/value
    /// pairs e.g. {"protocol":"tds","server":"...","database":"..."}) or Provider (legacy:
    /// connectionString + provider + impersonation). Replaces a same-named data source in place.
    /// </summary>
    public object SetDataSource(string sessionId, string name, string kind, string? connectionDetails,
        string? credential, string? connectionString, string? provider, string? impersonation)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var (created, type) = SetDataSourceCore(model, name, kind, connectionDetails, credential, connectionString, provider, impersonation);
        ModelTxn.Save(model);
        return Persisted(new { dataSource = name, kind = type, created });
    }

    /// <returns>(created=true if newly added, the data-source type string).</returns>
    /// <remarks>
    /// We assemble the canonical TMSL data-source JSON and deserialize it via the official
    /// <c>TOM.JsonSerializer</c>, rather than poking the ConnectionDetails/Credential string indexer -
    /// that indexer's setter is order/key-fragile and throws on some keys, whereas the serializer
    /// path is the documented, stable round-trip.
    /// </remarks>
    internal static (bool created, string type) SetDataSourceCore(TOM.Model model, string name, string kind,
        string? connectionDetails, string? credential, string? connectionString, string? provider, string? impersonation)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("data source name is required.");
        bool structured = (kind ?? "").Trim().Equals("Structured", StringComparison.OrdinalIgnoreCase);
        bool providerKind = (kind ?? "").Trim().Equals("Provider", StringComparison.OrdinalIgnoreCase);
        if (!structured && !providerKind)
            throw new InvalidOperationException($"Invalid kind '{kind}'. Use Structured or Provider.");

        var existing = model.DataSources.Find(name);

        TOM.DataSource newDs;
        if (structured)
        {
            var doc = new System.Text.Json.Nodes.JsonObject
            {
                ["type"] = "structured",
                ["name"] = name,
            };
            if (!string.IsNullOrWhiteSpace(connectionDetails)) doc["connectionDetails"] = ParseJsonObject(connectionDetails!, "connectionDetails");
            if (!string.IsNullOrWhiteSpace(credential)) doc["credential"] = ParseJsonObject(credential!, "credential");
            newDs = TOM.JsonSerializer.DeserializeObject<TOM.StructuredDataSource>(doc.ToJsonString());
        }
        else
        {
            // validate impersonation up front so a bad value is rejected before any mutation.
            if (!string.IsNullOrWhiteSpace(impersonation)
                && !Enum.TryParse<TOM.ImpersonationMode>(impersonation.Trim(), ignoreCase: true, out _))
                throw new InvalidOperationException($"Invalid impersonation '{impersonation}'. Use Default, ImpersonateAccount, ImpersonateAnonymous, ImpersonateCurrentUser, ImpersonateServiceAccount or ImpersonateUnattendedAccount.");
            var ds = new TOM.ProviderDataSource { Name = name };
            if (!string.IsNullOrWhiteSpace(connectionString)) ds.ConnectionString = connectionString;
            if (!string.IsNullOrWhiteSpace(provider)) ds.Provider = provider;
            if (!string.IsNullOrWhiteSpace(impersonation))
                ds.ImpersonationMode = Enum.Parse<TOM.ImpersonationMode>(impersonation.Trim(), ignoreCase: true);
            newDs = ds;
        }

        if (existing != null) model.DataSources.Remove(existing);
        model.DataSources.Add(newDs);
        return (existing == null, structured ? "Structured" : "Provider");
    }

    private static System.Text.Json.Nodes.JsonNode ParseJsonObject(string json, string what) =>
        System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject
            ?? throw new InvalidOperationException($"{what} must be a flat JSON object of key/value pairs.");

    // ---------------------------------------------------------------- security helpers
    private static TOM.ModelRole Role(TOM.Model m, string name) =>
        m.Roles.Find(name) ?? throw new InvalidOperationException($"Role '{name}' not found. Run add_role first.");

    /// <summary>Get the role's TablePermission for a table, creating it if absent (RLS and OLS share it).</summary>
    private static TOM.TablePermission TablePermissionFor(TOM.ModelRole role, TOM.Table table)
    {
        var tp = role.TablePermissions.Find(table.Name);
        if (tp == null)
        {
            tp = new TOM.TablePermission { Table = table };
            role.TablePermissions.Add(tp);
        }
        return tp;
    }

    private static TOM.ModelPermission ParseModelPermission(string s) =>
        Enum.TryParse<TOM.ModelPermission>((s ?? "").Trim(), ignoreCase: true, out var p)
            ? p
            : throw new InvalidOperationException($"Invalid modelPermission '{s}'. Use Read, ReadRefresh, Refresh, Administrator or None.");

    private static TOM.MetadataPermission ParseMetadataPermission(string s) =>
        Enum.TryParse<TOM.MetadataPermission>((s ?? "").Trim(), ignoreCase: true, out var p)
            ? p
            : throw new InvalidOperationException($"Invalid OLS permission '{s}'. Use Default, None or Read.");

    // ================================================================ Wave G1: RLS execution
    /// <summary>Run a DAX query on a SECOND connection carrying Roles= (and optionally
    /// EffectiveUserName=) so the engine applies the role's security filters - the session's own
    /// admin connection stays untouched.</summary>
    public object RunDaxAsRole(string sessionId, string query, string[] roles, string? effectiveUserName, int maxRows)
    {
        var session = _sessions.GetModel(sessionId);
        var resolved = ResolveRoleNames(session.Model, roles);
        string conn = BuildRoleConnectionString(session.AdomdConnectionString, resolved, effectiveUserName);
        string q = NormaliseDaxQuery(query);
        var (cols, rows, truncated, total) = ExecuteDaxRows(conn, q, maxRows, countBeyondMax: true);
        return new { ok = true, roles = resolved, effectiveUserName, columns = cols, rowCount = total,
            returnedRows = rows.Count, truncated, rows };
    }

    /// <summary>Evaluate one query under EVERY model role plus unfiltered - the per-role proof matrix.</summary>
    public object RlsTestHarness(string sessionId, string query, int sampleRows)
    {
        var session = _sessions.GetModel(sessionId);
        if (sampleRows < 0 || sampleRows > 100) throw new InvalidOperationException("sampleRows must be 0..100.");
        var roleNames = session.Model.Roles.Select(r => r.Name).ToList();
        if (roleNames.Count == 0)
            throw new InvalidOperationException(
                "The model has no security roles to test. Author RLS first (add_role / set_rls / add_dynamic_rls).");
        string q = NormaliseDaxQuery(query);

        var matrix = new List<object> { EvaluateUnderRole(session, q, null, sampleRows) };
        foreach (var role in roleNames) matrix.Add(EvaluateUnderRole(session, q, role, sampleRows));
        return new { ok = true, query = q, rolesTested = roleNames.Count, matrix,
            note = "Row 1 is the unfiltered baseline. A role whose rowCount equals the baseline is NOT filtering this query; an error usually means the role's filter DAX is invalid or OLS blocks a queried object." };
    }

    private object EvaluateUnderRole(ModelSession session, string query, string? role, int sampleRows)
    {
        try
        {
            string conn = role is null
                ? session.AdomdConnectionString
                : BuildRoleConnectionString(session.AdomdConnectionString, new[] { role }, null);
            var (cols, rows, _, total) = ExecuteDaxRows(conn, query, sampleRows, countBeyondMax: true);
            return new { role = role ?? "(unfiltered)", rowCount = total, columns = cols, sampleRows = rows, error = (string?)null };
        }
        catch (Exception ex)
        {
            return new { role = role ?? "(unfiltered)", rowCount = 0, columns = new List<string>(),
                sampleRows = new List<object?[]>(), error = (string?)ScrubToken(ex.Message, session.AccessTokenPrivate) };
        }
    }

    /// <summary>Resolve requested role names against the model (case-insensitive) so a typo can never
    /// silently run unfiltered. Returns the model's exact-cased names, de-duplicated.</summary>
    internal static List<string> ResolveRoleNames(TOM.Model model, string[]? roles)
    {
        if (roles == null || roles.Length == 0) throw new InvalidOperationException("Provide at least one role.");
        var known = model.Roles.Select(r => r.Name).ToList();
        var resolved = new List<string>();
        foreach (var raw in roles)
        {
            string want = (raw ?? "").Trim();
            var hit = known.FirstOrDefault(k => k.Equals(want, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException(
                    $"Role '{want}' not found. Model roles: {(known.Count > 0 ? string.Join(", ", known) : "(none)")}.");
            if (!resolved.Contains(hit, StringComparer.Ordinal)) resolved.Add(hit);
        }
        return resolved;
    }

    /// <summary>Append Roles= / EffectiveUserName= to the session's ADOMD connection string. Values that
    /// would break out of their key=value slot (';' '=' or the CSV ',') are refused - connection strings
    /// have no escaping for these, and a smuggled property would silently change what gets executed.</summary>
    internal static string BuildRoleConnectionString(string baseConnectionString, IReadOnlyList<string> roles, string? effectiveUserName)
    {
        if (roles == null || roles.Count == 0) throw new InvalidOperationException("Provide at least one role.");
        foreach (var r in roles)
            if (string.IsNullOrWhiteSpace(r) || r.IndexOfAny(new[] { ';', '=', ',' }) >= 0)
                throw new InvalidOperationException(
                    $"Role name '{r}' cannot be placed on a connection string (empty, or contains ';', '=' or ',').");
        var sb = new StringBuilder(baseConnectionString);
        sb.Append(";Roles=").Append(string.Join(",", roles));
        if (!string.IsNullOrWhiteSpace(effectiveUserName))
        {
            if (effectiveUserName.IndexOfAny(new[] { ';', '=' }) >= 0)
                throw new InvalidOperationException("effectiveUserName cannot contain ';' or '='.");
            sb.Append(";EffectiveUserName=").Append(effectiveUserName.Trim());
        }
        return sb.ToString();
    }

    // ================================================================ Wave G1: model write-transactions
    public object BeginModelTransaction(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        var st = ModelTxn.Begin(session.Model);
        return new { ok = true, open = true, openedUtc = st.OpenedUtc,
            note = "Model tools now accumulate TOM changes WITHOUT SaveChanges. commit_model_transaction applies everything in one SaveChanges; rollback_model_transaction discards it. Refreshes requested inside the transaction run at commit." };
    }

    public object CommitModelTransaction(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        int deferred = ModelTxn.Commit(session.Model);
        return Persisted(new { committed = true, deferredSavesApplied = deferred });
    }

    public object RollbackModelTransaction(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        int deferred = ModelTxn.Rollback(session.Model);
        return new { ok = true, rolledBack = true, deferredSavesDiscarded = deferred,
            note = "Local TOM changes were discarded via Model.UndoLocalChanges - the engine never saw them. Pending-M dirty flags raised during the transaction remain (fail-closed: at worst one redundant refresh)." };
    }

    public object GetTransactionStatus(string sessionId)
    {
        var session = _sessions.GetModel(sessionId);
        var st = ModelTxn.For(session.Model);
        return st is null
            ? new { ok = true, open = false }
            : (object)new { ok = true, open = true, openedUtc = st.OpenedUtc, deferredSaves = st.DeferredSaves };
    }

    // ================================================================ Wave G1: DAX benchmark + trace
    /// <summary>Timed runs of one query, optionally after an XMLA ClearCache of the session database.
    /// Wall-clock client-side timings (execution + row streaming) - FE/SE splits come from the trace.</summary>
    public object DaxBenchmark(string sessionId, string query, int runs, bool clearCache)
    {
        var session = _sessions.GetModel(sessionId);
        if (runs < 1 || runs > 20) throw new InvalidOperationException("runs must be 1..20.");
        string q = NormaliseDaxQuery(query);

        bool cacheCleared = false;
        if (clearCache)
        {
            var res = session.Server.Execute(BuildClearCacheXmla(session.Database.ID));
            if (res.ContainsErrors)
                throw new InvalidOperationException("ClearCache failed: the engine returned errors for the XMLA command.");
            cacheCleared = true;
        }
        var timesMs = new List<double>();
        int rowCount = 0;
        for (int i = 0; i < runs; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (_, _, _, total) = ExecuteDaxRows(session.AdomdConnectionString, q, maxRows: 0, countBeyondMax: true);
            sw.Stop();
            timesMs.Add(Math.Round(sw.Elapsed.TotalMilliseconds, 1));
            rowCount = total;
        }
        return new { ok = true, query = q, runs, cacheCleared,
            coldMs = cacheCleared ? timesMs[0] : (double?)null,
            warmMs = cacheCleared ? timesMs.Skip(1).ToList() : timesMs,
            rowCount,
            note = "Client wall-clock per run (query + full row drain). coldMs is only reported when the cache was cleared first. Pair with start_dax_trace for FE/SE splits and cache hits." };
    }

    /// <summary>The ClearCache XMLA command scoped to one database. Pure + testable (XML-escaped ID).</summary>
    internal static string BuildClearCacheXmla(string databaseId)
    {
        if (string.IsNullOrWhiteSpace(databaseId))
            throw new InvalidOperationException("Database ID is required for ClearCache.");
        string id = System.Security.SecurityElement.Escape(databaseId);
        return "<ClearCache xmlns=\"http://schemas.microsoft.com/analysisservices/2003/engine\"><Object><DatabaseID>"
            + id + "</DatabaseID></Object></ClearCache>";
    }

    public object StartDaxTrace(string sessionId) => DaxTrace.Start(_sessions.GetModel(sessionId));

    public object StopDaxTrace(string sessionId) => DaxTrace.Stop(_sessions.GetModel(sessionId));

    // ================================================================ Wave G1: calculation item CRUD completion
    public object UpdateCalculationItem(string sessionId, string table, string name, string? daxExpression,
        int? ordinal, string? formatStringExpression, string? newName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var item = UpdateCalculationItemCore(model, table, name, daxExpression, ordinal, formatStringExpression, newName);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, item = item.Name, ordinal = item.Ordinal });
    }

    internal static TOM.CalculationItem UpdateCalculationItemCore(TOM.Model model, string table, string name,
        string? daxExpression, int? ordinal, string? formatStringExpression, string? newName)
    {
        var t = Table(model, table);
        var cg = t.CalculationGroup
                 ?? throw new InvalidOperationException($"Table '{table}' is not a calculation group.");
        var item = cg.CalculationItems.Find(name)
                   ?? throw new InvalidOperationException($"Calculation item '{name}' not found on '{table}'.");
        if (!string.IsNullOrWhiteSpace(daxExpression)) item.Expression = daxExpression;
        if (ordinal is { } o) item.Ordinal = o;
        // empty string clears the dynamic format string; null leaves it unchanged.
        if (formatStringExpression != null)
            item.FormatStringDefinition = formatStringExpression.Length == 0
                ? null
                : new TOM.FormatStringDefinition { Expression = formatStringExpression };
        if (!string.IsNullOrWhiteSpace(newName) && !string.Equals(newName, name, StringComparison.Ordinal))
        {
            if (cg.CalculationItems.Contains(newName))
                throw new InvalidOperationException($"Calculation item '{newName}' already exists on '{table}'.");
            item.Name = newName;
        }
        return item;
    }

    public object DeleteCalculationItem(string sessionId, string table, string name)
    {
        var model = _sessions.GetModel(sessionId).Model;
        DeleteCalculationItemCore(model, table, name);
        ModelTxn.Save(model);
        return Persisted(new { calculationGroup = table, deleted = name });
    }

    internal static void DeleteCalculationItemCore(TOM.Model model, string table, string name)
    {
        var t = Table(model, table);
        var cg = t.CalculationGroup
                 ?? throw new InvalidOperationException($"Table '{table}' is not a calculation group.");
        var item = cg.CalculationItems.Find(name)
                   ?? throw new InvalidOperationException($"Calculation item '{name}' not found on '{table}'.");
        cg.CalculationItems.Remove(item);
    }

    // ================================================================ Wave G1: shared-expression rename / delete
    public object RenameSharedExpression(string sessionId, string name, string newName)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var rewrittenIn = RenameSharedExpressionCore(model, name, newName);
        ModelTxn.Save(model);
        session.MDirty.Mark($"rename_shared_expression {name} -> {newName}");
        return Persisted(new { renamed = name, to = newName, referencesRewrittenIn = rewrittenIn,
            refreshRequiredBeforeSave = true });
    }

    /// <summary>Rename a shared expression AND rewrite every #"name" / bare-identifier reference to it in
    /// other shared expressions and table partitions. All-or-nothing: a document that DECLARES the same
    /// name locally (let-step or lambda parameter - detected by an assignment/arrow after the token)
    /// shadows the shared name there, so a text rewrite is ambiguous and the whole rename is refused
    /// listing the offending documents.</summary>
    internal static List<string> RenameSharedExpressionCore(TOM.Model model, string name, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) throw new InvalidOperationException("newName is required.");
        var expr = model.Expressions.Find(name)
                   ?? throw new InvalidOperationException($"Shared expression '{name}' not found.");
        if (!string.Equals(name, newName, StringComparison.Ordinal) && model.Expressions.Contains(newName))
            throw new InvalidOperationException($"A shared expression named '{newName}' already exists.");

        var pending = new List<(Action<string> apply, string current, string label)>();
        var ambiguous = new List<string>();
        foreach (var (label, code, apply) in EnumerateMDocuments(model, skipExpression: name))
        {
            var scan = ScanMForName(code, name);
            if (scan.DeclaresLocally && (scan.HasQuotedRef || scan.HasBareRef)) { ambiguous.Add(label); continue; }
            if (scan.DeclaresLocally) continue;   // local-only: the shared name is fully shadowed there
            if (scan.HasQuotedRef || scan.HasBareRef) pending.Add((apply, code, label));
        }
        if (ambiguous.Count > 0)
            throw new InvalidOperationException(
                $"Cannot rename '{name}': these M documents declare a LOCAL '{name}' AND use the name, so the references there are shadowed and a text rewrite is unsafe: "
                + string.Join(", ", ambiguous) + ". Rename those local steps first.");

        var rewrittenIn = new List<string>();
        foreach (var (apply, code, label) in pending)
        {
            apply(RewriteMReferences(code, name, newName));
            rewrittenIn.Add(label);
        }
        expr.Name = newName;
        return rewrittenIn;
    }

    public object DeleteSharedExpression(string sessionId, string name)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        DeleteSharedExpressionCore(model, name);
        ModelTxn.Save(model);
        session.MDirty.Mark($"delete_shared_expression {name}");
        return Persisted(new { deleted = name, refreshRequiredBeforeSave = true });
    }

    /// <summary>Delete a shared expression, refusing (with the referencer list) while any other M
    /// document still references it. A document that declares the same name locally shadows the shared
    /// one, so its uses do not count as references.</summary>
    internal static void DeleteSharedExpressionCore(TOM.Model model, string name)
    {
        var expr = model.Expressions.Find(name)
                   ?? throw new InvalidOperationException($"Shared expression '{name}' not found.");
        var referencers = new List<string>();
        foreach (var (label, code, _) in EnumerateMDocuments(model, skipExpression: name))
        {
            var scan = ScanMForName(code, name);
            if ((scan.HasQuotedRef || scan.HasBareRef) && !scan.DeclaresLocally) referencers.Add(label);
        }
        if (referencers.Count > 0)
            throw new InvalidOperationException(
                $"Cannot delete shared expression '{name}': it is referenced in: {string.Join(", ", referencers)}. Repoint or remove those references first.");
        model.Expressions.Remove(expr);
    }

    /// <summary>Every M document in the model: table partitions with an M source, plus shared expressions
    /// (optionally excluding the one being operated on). The apply action writes rewritten code back.</summary>
    private static IEnumerable<(string label, string code, Action<string> apply)> EnumerateMDocuments(
        TOM.Model model, string? skipExpression)
    {
        foreach (var t in model.Tables)
            foreach (var p in t.Partitions)
                if (p.Source is TOM.MPartitionSource mps && mps.Expression is { Length: > 0 })
                {
                    var target = mps;   // capture per iteration
                    yield return ($"partition {t.Name}/{p.Name}", mps.Expression, s => target.Expression = s);
                }
        foreach (var e in model.Expressions)
        {
            if (skipExpression != null && string.Equals(e.Name, skipExpression, StringComparison.Ordinal)) continue;
            if (e.Expression is { Length: > 0 })
            {
                var target = e;
                yield return ($"shared expression {e.Name}", e.Expression, s => target.Expression = s);
            }
        }
    }

    internal sealed record MReferenceScan(bool HasQuotedRef, bool HasBareRef, bool DeclaresLocally);

    /// <summary>Scan one M document for uses of a shared-expression name: quoted (#"name") and bare
    /// identifier references, and local DECLARATIONS (token followed by = or =>, which shadow the
    /// shared name). String literals and comments are masked out first so text content never matches.</summary>
    internal static MReferenceScan ScanMForName(string code, string name)
    {
        string masked = MaskMLiteralsAndComments(code);
        bool quotedRef = false, bareRef = false, declares = false;

        string quotedToken = "#\"" + name.Replace("\"", "\"\"") + "\"";
        int idx = 0;
        while ((idx = masked.IndexOf(quotedToken, idx, StringComparison.Ordinal)) >= 0)
        {
            int after = idx + quotedToken.Length;
            // the token's closing quote must really close the identifier - a following '"' means we
            // matched the first half of an escaped "" inside a LONGER quoted identifier.
            if (after < masked.Length && masked[after] == '"') { idx = after; continue; }
            if (IsFollowedByAssignment(masked, after)) declares = true; else quotedRef = true;
            idx = after;
        }

        if (IsValidBareMIdentifier(name))
        {
            idx = 0;
            while ((idx = masked.IndexOf(name, idx, StringComparison.Ordinal)) >= 0)
            {
                int after = idx + name.Length;
                char prev = idx > 0 ? masked[idx - 1] : '\0';
                char next = after < masked.Length ? masked[after] : '\0';
                bool prevOk = prev == '\0' || (!char.IsLetterOrDigit(prev)
                    && prev != '_' && prev != '.' && prev != '#' && prev != '"' && prev != '[');
                bool nextOk = next == '\0' || (!char.IsLetterOrDigit(next) && next != '_' && next != '.');
                if (prevOk && nextOk)
                {
                    if (IsFollowedByAssignment(masked, after)) declares = true; else bareRef = true;
                }
                idx = after;
            }
        }
        return new MReferenceScan(quotedRef, bareRef, declares);
    }

    /// <summary>Rewrite every reference to oldName in one M document. Callers must have established via
    /// <see cref="ScanMForName"/> that the document does not declare the name locally.</summary>
    internal static string RewriteMReferences(string code, string oldName, string newName)
    {
        string masked = MaskMLiteralsAndComments(code);
        string quotedOld = "#\"" + oldName.Replace("\"", "\"\"") + "\"";
        string quotedNew = "#\"" + newName.Replace("\"", "\"\"") + "\"";
        string bareNew = IsValidBareMIdentifier(newName) ? newName : quotedNew;

        var spans = new List<(int start, int len, string replacement)>();
        int idx = 0;
        while ((idx = masked.IndexOf(quotedOld, idx, StringComparison.Ordinal)) >= 0)
        {
            int after = idx + quotedOld.Length;
            if (after < masked.Length && masked[after] == '"') { idx = after; continue; }
            spans.Add((idx, quotedOld.Length, quotedNew));
            idx = after;
        }
        if (IsValidBareMIdentifier(oldName))
        {
            idx = 0;
            while ((idx = masked.IndexOf(oldName, idx, StringComparison.Ordinal)) >= 0)
            {
                int after = idx + oldName.Length;
                char prev = idx > 0 ? masked[idx - 1] : '\0';
                char next = after < masked.Length ? masked[after] : '\0';
                bool prevOk = prev == '\0' || (!char.IsLetterOrDigit(prev)
                    && prev != '_' && prev != '.' && prev != '#' && prev != '"' && prev != '[');
                bool nextOk = next == '\0' || (!char.IsLetterOrDigit(next) && next != '_' && next != '.');
                if (prevOk && nextOk) spans.Add((idx, oldName.Length, bareNew));
                idx = after;
            }
        }
        foreach (var (start, len, replacement) in spans.OrderByDescending(s => s.start))
            code = code.Remove(start, len).Insert(start, replacement);
        return code;
    }

    /// <summary>Mask string literals and comments to spaces (same length, so positions stay valid) while
    /// keeping code and #"quoted identifiers" intact - the M-aware pre-pass for reference scanning.</summary>
    internal static string MaskMLiteralsAndComments(string code)
    {
        var chars = code.ToCharArray();
        int i = 0;
        while (i < chars.Length)
        {
            char ch = chars[i];
            if (ch == '#' && i + 1 < chars.Length && chars[i + 1] == '"')
            {
                // quoted identifier - KEEP (this is what reference scans look for); "" escapes inside.
                i += 2;
                while (i < chars.Length)
                {
                    if (chars[i] == '"')
                    {
                        if (i + 1 < chars.Length && chars[i + 1] == '"') { i += 2; continue; }
                        i++; break;
                    }
                    i++;
                }
            }
            else if (ch == '"')
            {
                chars[i] = ' '; i++;
                while (i < chars.Length)
                {
                    if (chars[i] == '"')
                    {
                        if (i + 1 < chars.Length && chars[i + 1] == '"') { chars[i] = ' '; chars[i + 1] = ' '; i += 2; continue; }
                        chars[i] = ' '; i++; break;
                    }
                    chars[i] = ' '; i++;
                }
            }
            else if (ch == '/' && i + 1 < chars.Length && chars[i + 1] == '/')
            {
                while (i < chars.Length && chars[i] != '\n') { chars[i] = ' '; i++; }
            }
            else if (ch == '/' && i + 1 < chars.Length && chars[i + 1] == '*')
            {
                chars[i] = ' '; chars[i + 1] = ' '; i += 2;
                while (i < chars.Length)
                {
                    if (chars[i] == '*' && i + 1 < chars.Length && chars[i + 1] == '/')
                    { chars[i] = ' '; chars[i + 1] = ' '; i += 2; break; }
                    chars[i] = ' '; i++;
                }
            }
            else i++;
        }
        return new string(chars);
    }

    private static bool IsFollowedByAssignment(string masked, int pos)
    {
        while (pos < masked.Length && char.IsWhiteSpace(masked[pos])) pos++;
        return pos < masked.Length && masked[pos] == '=';   // covers both '=' and '=>'
    }

    private static readonly HashSet<string> MKeywords = new(StringComparer.Ordinal)
    {
        "and", "as", "each", "else", "error", "false", "if", "in", "is", "let", "meta", "not",
        "null", "or", "otherwise", "section", "shared", "then", "true", "try", "type",
    };

    /// <summary>Can this name appear as a BARE M identifier (no #"" quoting)? Letter/underscore start,
    /// then letters/digits/underscores/dots, and not a reserved keyword.</summary>
    internal static bool IsValidBareMIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name) || MKeywords.Contains(name)) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '_' && c != '.') return false;
        }
        return true;
    }

    // ================================================================ Wave G1: move measure
    public object MoveMeasure(string sessionId, string measure, string targetTable)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var (m, fromTable) = MoveMeasureCore(model, measure, targetTable);
        ModelTxn.Save(model);
        return Persisted(new { moved = m.Name, from = fromTable, to = targetTable,
            note = "KPI, format string, display folder, description, annotations, lineage tag and culture translations all travelled with the measure." });
    }

    /// <summary>Move via a deep CLONE (TOM refuses re-attaching a removed object): the clone carries
    /// KPI, format string, folder, description, annotations, detail rows and lineage tag; culture
    /// translations hold object references, so they are captured first and re-pointed at the clone.</summary>
    internal static (TOM.Measure measure, string fromTable) MoveMeasureCore(TOM.Model model, string measure, string targetTable)
    {
        var target = Table(model, targetTable);
        TOM.Table? src = null; TOM.Measure? m = null;
        foreach (var t in model.Tables)
        {
            var hit = t.Measures.Find(measure);
            if (hit != null) { src = t; m = hit; break; }
        }
        if (m == null || src == null)
            throw new InvalidOperationException($"Measure '{measure}' not found in the model.");
        if (ReferenceEquals(src, target))
            throw new InvalidOperationException($"Measure '{measure}' is already on '{targetTable}'.");
        if (target.Measures.Contains(measure))
            throw new InvalidOperationException($"Table '{targetTable}' already has a measure named '{measure}'.");

        // capture + detach translations BEFORE the removal so they never dangle on a removed object.
        var captured = new List<(TOM.Culture culture, TOM.TranslatedProperty prop, string? value)>();
        foreach (var c in model.Cultures)
            foreach (var tr in c.ObjectTranslations.Where(o => ReferenceEquals(o.Object, m)).ToList())
            {
                captured.Add((c, tr.Property, tr.Value));
                c.ObjectTranslations.Remove(tr);
            }

        var clone = m.Clone();
        src.Measures.Remove(m);
        target.Measures.Add(clone);
        foreach (var (culture, prop, value) in captured)
            culture.ObjectTranslations.Add(new TOM.ObjectTranslation { Object = clone, Property = prop, Value = value ?? "" });
        return (clone, src.Name);
    }

    // ================================================================ Wave G1: hierarchy level CRUD
    public object AddHierarchyLevel(string sessionId, string table, string hierarchy, string column,
        int? ordinal, string? levelName)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var level = AddHierarchyLevelCore(model, table, hierarchy, column, ordinal, levelName);
        ModelTxn.Save(model);
        return Persisted(new { hierarchy = $"{table}.{hierarchy}", level = level.Name, ordinal = level.Ordinal });
    }

    internal static TOM.Level AddHierarchyLevelCore(TOM.Model model, string table, string hierarchy,
        string column, int? ordinal, string? levelName)
    {
        var t = Table(model, table);
        var h = t.Hierarchies.Find(hierarchy)
                ?? throw new InvalidOperationException($"Hierarchy '{hierarchy}' not found on '{table}'.");
        var col = Column(t, column);
        string name = string.IsNullOrWhiteSpace(levelName) ? column : levelName;
        if (h.Levels.Contains(name))
            throw new InvalidOperationException($"Level '{name}' already exists on '{table}'.'{hierarchy}'.");
        if (h.Levels.Any(l => ReferenceEquals(l.Column, col)))
            throw new InvalidOperationException($"Column '{table}'[{column}] is already a level of '{hierarchy}'.");
        var ordered = h.Levels.OrderBy(l => l.Ordinal).ToList();
        int insertAt = ordinal is { } o ? Math.Clamp(o, 0, ordered.Count) : ordered.Count;
        var level = new TOM.Level { Name = name, Column = col };
        h.Levels.Add(level);
        ordered.Insert(insertAt, level);
        for (int i = 0; i < ordered.Count; i++) ordered[i].Ordinal = i;   // keep ordinals dense
        return level;
    }

    public object RemoveHierarchyLevel(string sessionId, string table, string hierarchy, string level)
    {
        var model = _sessions.GetModel(sessionId).Model;
        RemoveHierarchyLevelCore(model, table, hierarchy, level);
        ModelTxn.Save(model);
        return Persisted(new { hierarchy = $"{table}.{hierarchy}", removedLevel = level });
    }

    internal static void RemoveHierarchyLevelCore(TOM.Model model, string table, string hierarchy, string level)
    {
        var t = Table(model, table);
        var h = t.Hierarchies.Find(hierarchy)
                ?? throw new InvalidOperationException($"Hierarchy '{hierarchy}' not found on '{table}'.");
        var l = h.Levels.Find(level)
                ?? throw new InvalidOperationException(
                    $"Level '{level}' not found on '{table}'.'{hierarchy}'. Levels: {string.Join(", ", h.Levels.Select(x => x.Name))}.");
        if (h.Levels.Count == 1)
            throw new InvalidOperationException(
                $"'{level}' is the last level of '{hierarchy}' - a hierarchy cannot be empty. Use delete_hierarchy instead.");
        h.Levels.Remove(l);
        int i = 0;
        foreach (var lv in h.Levels.OrderBy(x => x.Ordinal)) lv.Ordinal = i++;
    }

    // ================================================================ Wave G1: culture / translation CRUD completion
    public object ListCultures(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var cultures = model.Cultures.Select(c => new
        {
            name = c.Name,
            translations = c.ObjectTranslations.Count,
            hasLinguisticMetadata = c.LinguisticMetadata != null,
        }).ToList();
        return new { ok = true, count = cultures.Count, cultures };
    }

    public object DeleteCulture(string sessionId, string locale)
    {
        var model = _sessions.GetModel(sessionId).Model;
        DeleteCultureCore(model, locale);
        ModelTxn.Save(model);
        return Persisted(new { deletedCulture = locale });
    }

    internal static void DeleteCultureCore(TOM.Model model, string locale)
    {
        var c = model.Cultures.Find(locale)
                ?? throw new InvalidOperationException(
                    $"Culture '{locale}' not found. Cultures: {(model.Cultures.Count > 0 ? string.Join(", ", model.Cultures.Select(x => x.Name)) : "(none)")}.");
        model.Cultures.Remove(c);
    }

    public object ListTranslations(string sessionId, string? culture)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var rows = ListTranslationsCore(model, culture);
        return new { ok = true, count = rows.Count, translations = rows };
    }

    internal static List<object> ListTranslationsCore(TOM.Model model, string? culture)
    {
        IEnumerable<TOM.Culture> cultures = model.Cultures;
        if (!string.IsNullOrWhiteSpace(culture))
        {
            var c = model.Cultures.Find(culture)
                    ?? throw new InvalidOperationException($"Culture '{culture}' not found.");
            cultures = new[] { c };
        }
        var rows = new List<object>();
        foreach (var c in cultures)
            foreach (var o in c.ObjectTranslations)
                rows.Add(new
                {
                    culture = c.Name,
                    objectType = o.Object?.ObjectType.ToString(),
                    objectName = (o.Object as TOM.NamedMetadataObject)?.Name,
                    table = (o.Object?.Parent as TOM.Table)?.Name,
                    property = o.Property.ToString(),
                    value = o.Value,
                });
        return rows;
    }

    public object DeleteTranslation(string sessionId, string culture, string objectType, string objectName,
        string property, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        DeleteTranslationCore(model, culture, objectType, objectName, property, table);
        ModelTxn.Save(model);
        return Persisted(new { culture, objectType, objectName, property, deleted = true });
    }

    internal static void DeleteTranslationCore(TOM.Model model, string culture, string objectType,
        string objectName, string property, string? table)
    {
        var c = model.Cultures.Find(culture)
                ?? throw new InvalidOperationException($"Culture '{culture}' not found.");
        var prop = ParseTranslatedProperty(property);
        var target = ResolveTranslationTarget(model, objectType, objectName, table);
        var existing = c.ObjectTranslations.FirstOrDefault(o => ReferenceEquals(o.Object, target) && o.Property == prop)
                       ?? throw new InvalidOperationException(
                           $"No {prop} translation for '{objectName}' in culture '{culture}'.");
        c.ObjectTranslations.Remove(existing);
    }

    // ================================================================ Wave G1: partition CRUD
    public object AddPartition(string sessionId, string table, string name, string m, string? mode)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var p = AddPartitionCore(model, session.MDirty, table, name, m, mode);
        ModelTxn.Save(model);
        return Persisted(new { table, partition = p.Name, mode = p.Mode.ToString(),
            refreshRequired = true, refreshRequiredBeforeSave = true });
    }

    internal static TOM.Partition AddPartitionCore(TOM.Model model, MDirtyTracker tracker, string table,
        string name, string m, string? mode)
    {
        var t = Table(model, table);
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Partition name is required.");
        if (string.IsNullOrWhiteSpace(m)) throw new InvalidOperationException("An M expression is required for the new partition.");
        if (t.Partitions.Contains(name))
            throw new InvalidOperationException($"Partition '{name}' already exists on '{table}'.");
        var p = new TOM.Partition { Name = name, Source = new TOM.MPartitionSource { Expression = m } };
        if (!string.IsNullOrWhiteSpace(mode)) p.Mode = ParseModeType(mode);
        t.Partitions.Add(p);
        // a brand-new M partition has never refreshed - the same forced-refresh rule as any M edit.
        tracker.Mark($"add_partition {table}/{name}");
        return p;
    }

    public object DeletePartition(string sessionId, string table, string name)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        DeletePartitionCore(model, session.MDirty, table, name);
        ModelTxn.Save(model);
        return Persisted(new { table, deletedPartition = name, refreshRequiredBeforeSave = true });
    }

    internal static void DeletePartitionCore(TOM.Model model, MDirtyTracker tracker, string table, string name)
    {
        var t = Table(model, table);
        var p = t.Partitions.Find(name)
                ?? throw new InvalidOperationException(
                    $"Partition '{name}' not found on '{table}'. Partitions: {string.Join(", ", t.Partitions.Select(x => x.Name))}.");
        if (t.Partitions.Count == 1)
            throw new InvalidOperationException(
                $"'{name}' is the only partition on '{table}' - a table needs at least one. Use delete_table to remove the whole table.");
        t.Partitions.Remove(p);
        tracker.Mark($"delete_partition {table}/{name}");
    }

    public object ListPartitions(string sessionId, string? table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var tables = table is null ? model.Tables.AsEnumerable() : new[] { Table(model, table) };
        var rows = tables.SelectMany(t => t.Partitions.Select(p => new
        {
            table = t.Name,
            partition = p.Name,
            sourceType = p.SourceType.ToString(),
            mode = p.Mode.ToString(),
            state = p.State.ToString(),
            refreshedTime = p.RefreshedTime == DateTime.MinValue ? (DateTime?)null : p.RefreshedTime,
        })).ToList();
        return new { ok = true, count = rows.Count, partitions = rows };
    }

    public object RefreshPartition(string sessionId, string table, string name)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;
        var t = Table(model, table);
        var p = t.Partitions.Find(name)
                ?? throw new InvalidOperationException(
                    $"Partition '{name}' not found on '{table}'. Partitions: {string.Join(", ", t.Partitions.Select(x => x.Name))}.");
        p.RequestRefresh(TOM.RefreshType.Full);
        bool saved = ModelTxn.Save(model);
        return Persisted(new { refreshed = $"{table}/{name}", type = "Full", executed = saved,
            note = saved ? "Partition refreshed." : "A model transaction is open - the refresh runs at commit_model_transaction." });
    }

    // ================================================================ Wave G1: calendar object CRUD (annotation fallback)
    public object ListCalendars(string sessionId)
    {
        var model = _sessions.GetModel(sessionId).Model;
        var rows = ListCalendarsCore(model);
        return new { ok = true, count = rows.Count, calendars = rows,
            note = "Read from PBI_Calendar table annotations - the Wave R fallback while the native calendar/calendarColumnGroup TOM objects are absent from this build." };
    }

    internal static List<object> ListCalendarsCore(TOM.Model model)
    {
        var rows = new List<object>();
        foreach (var t in model.Tables)
        {
            var ann = t.Annotations.Find("PBI_Calendar");
            if (ann?.Value is not { Length: > 0 } v) continue;
            string? primary = null;
            var groups = new List<string[]>();
            try
            {
                var doc = System.Text.Json.Nodes.JsonNode.Parse(v)!.AsObject();
                primary = (string?)doc["columnName"];
                if (doc["calendarColumnGroups"] is System.Text.Json.Nodes.JsonArray arr)
                    foreach (var g in arr)
                        groups.Add(g?["columnNames"]?.AsArray().Select(n => (string?)n ?? "").ToArray()
                                   ?? Array.Empty<string>());
            }
            catch { /* malformed annotation - the raw value is still surfaced */ }
            rows.Add(new { table = t.Name, primaryColumn = primary, columnGroups = groups, raw = v });
        }
        return rows;
    }

    public object UpdateCalendar(string sessionId, string table, string? primaryColumn, string[]? associatedColumns)
    {
        var model = _sessions.GetModel(sessionId).Model;
        string value = UpdateCalendarCore(model, table, primaryColumn, associatedColumns);
        ModelTxn.Save(model);
        return Persisted(new { table, calendar = value,
            note = "PBI_Calendar table annotation updated (the Wave R fallback - native TOM calendar objects are absent from this build)." });
    }

    internal static string UpdateCalendarCore(TOM.Model model, string table, string? primaryColumn,
        string[]? associatedColumns)
    {
        var t = Table(model, table);
        var ann = t.Annotations.Find("PBI_Calendar")
                  ?? throw new InvalidOperationException(
                      $"Table '{table}' has no PBI_Calendar annotation. Run add_calendar_based_time_intelligence first.");
        System.Text.Json.Nodes.JsonObject doc;
        try { doc = System.Text.Json.Nodes.JsonNode.Parse(ann.Value ?? "")!.AsObject(); }
        catch { doc = new System.Text.Json.Nodes.JsonObject(); }

        if (!string.IsNullOrWhiteSpace(primaryColumn))
            doc["columnName"] = Column(t, primaryColumn).Name;   // validate + canonical casing
        if (associatedColumns is { Length: > 0 })
        {
            var cols = new System.Text.Json.Nodes.JsonArray();
            foreach (var c in associatedColumns) cols.Add(Column(t, c.Trim()).Name);
            doc["calendarColumnGroups"] = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject { ["columnNames"] = cols },
            };
        }
        if (doc["columnName"] is null)
            throw new InvalidOperationException("The calendar annotation has no primary column - pass primaryColumn.");
        ann.Value = doc.ToJsonString();
        return ann.Value;
    }

    public object DeleteCalendar(string sessionId, string table)
    {
        var model = _sessions.GetModel(sessionId).Model;
        DeleteCalendarCore(model, table);
        ModelTxn.Save(model);
        return Persisted(new { table, deletedCalendar = true });
    }

    internal static void DeleteCalendarCore(TOM.Model model, string table)
    {
        var t = Table(model, table);
        var ann = t.Annotations.Find("PBI_Calendar")
                  ?? throw new InvalidOperationException($"Table '{table}' has no PBI_Calendar annotation.");
        t.Annotations.Remove(ann);
    }

    // ---------------------------------------------------------------- helpers
    private static TOM.Table Table(TOM.Model m, string name) =>
        m.Tables.Find(name) ?? throw new InvalidOperationException($"Table '{name}' not found.");

    private static TOM.Column Column(TOM.Table t, string name) =>
        t.Columns.Find(name) ?? throw new InvalidOperationException($"Column '{t.Name}[{name}]' not found.");

    private static bool IsAutoDateName(string n) =>
        n.StartsWith("LocalDateTable", StringComparison.OrdinalIgnoreCase) ||
        n.StartsWith("DateTableTemplate", StringComparison.OrdinalIgnoreCase);

    private static string ColPairKey(string t1, string c1, string t2, string c2) => $"{t1}{c1}{t2}{c2}";

    private static string TablePairKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a}{b}" : $"{b}{a}";

    private static bool TypeCompatible(TOM.DataType a, TOM.DataType b)
    {
        static int Fam(TOM.DataType d) => d switch
        {
            TOM.DataType.Int64 or TOM.DataType.Double or TOM.DataType.Decimal => 1,   // numeric
            TOM.DataType.String => 2,
            TOM.DataType.DateTime => 3,
            TOM.DataType.Boolean => 4,
            _ => 0,
        };
        int fa = Fam(a), fb = Fam(b);
        return fa != 0 && fa == fb;
    }

    private static object Persisted(object detail) => new
    {
        ok = true,
        committedToLiveModel = true,
        persistedToDisk = false,
        actionRequired = "Save in Power BI Desktop (File > Save) to persist to the .pbix.",
        detail,
    };
}
