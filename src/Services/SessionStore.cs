using System.Collections.Concurrent;
using System.Text.Json.Serialization;
using Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>Live TOM connection to one open Power BI Desktop model, or to a Fabric/Premium
/// XMLA endpoint when <see cref="Endpoint"/> is set (Port is 0 for XMLA sessions).</summary>
public sealed class ModelSession : IDisposable
{
    public required string Id { get; init; }
    public required int Port { get; init; }
    public required Server Server { get; init; }
    public required Database Database { get; init; }

    /// <summary>XMLA endpoint, e.g. powerbi://api.powerbi.com/v1.0/myorg/WorkspaceName. Null = local Desktop.</summary>
    public string? Endpoint { get; init; }

    /// <summary>Dataset name - required with Endpoint (a workspace hosts many datasets; Desktop's
    /// single-database instance never needs one).</summary>
    public string? Catalog { get; init; }

    /// <summary>AAD access token, in-memory only. Must never surface in tool results, logs or error text.</summary>
    internal string? AccessTokenPrivate { get; init; }

    /// <summary>PID of the Power BI Desktop whose engine hosts this session, when WE launched it.
    /// null = attached to a human's open Desktop (interactive) or an XMLA session. Never `required`:
    /// ConnectXmla constructs with Port = 0 and no PID.</summary>
    public int? LaunchedPid { get; init; }

    /// <summary>Start time of LaunchedPid. A PID alone is not an identity - Windows recycles PIDs, and a
    /// ModelSession lives for the whole process lifetime on a singleton SessionStore.</summary>
    [JsonIgnore]
    public DateTime? LaunchedStartUtc { get; init; }

    /// <summary>The .pbix path Desktop was launched with. null when attached.</summary>
    public string? PbixPath { get; init; }

    /// <summary>Unrefreshed Power Query (M) mutations pending on the live model behind this session. The
    /// dirty condition lives in the ENGINE - SaveChanges() committed the M edit into Desktop's/Fabric's
    /// in-memory model, where it survives any number of session objects - so the flag is engine-keyed in
    /// the process-wide <see cref="MDirtyRegistry"/> (endpoint ?? "local:"+port, database name), NOT per
    /// session: a reconnect after "Unknown model sessionId" or a parallel chat's second connect_model to
    /// the same engine shares the flag, and a fresh session can never launder a dirty model past the save
    /// gate. Property surface unchanged (callers still write session.MDirty.Mark(...)), and it applies to
    /// XMLA sessions too - an M edit needs the same forced refresh whether the engine is Desktop or
    /// Fabric. Disposing the session deliberately leaves the flag standing: the engine outlives it.</summary>
    [JsonIgnore]
    public MDirtyTracker MDirty => MDirtyRegistry.Default.For(Endpoint, Port, Database.Name);

    public Model Model => Database.Model;

    /// <summary>Connection string for ADOMD (DAX) execution. Contains the access token on XMLA
    /// sessions - never echo it into a tool result or log line.</summary>
    [JsonIgnore]
    public string AdomdConnectionString =>
        Endpoint is null
            ? $"Data Source=localhost:{Port}"
            : BuildXmlaConnectionString(Endpoint, Catalog ?? "", AccessTokenPrivate);

    internal static string BuildXmlaConnectionString(string endpoint, string catalog, string? accessToken) =>
        $"Data Source={endpoint};Initial Catalog={catalog};User ID=;Password={accessToken}";

    /// <summary>Token-free summary (safe for logs and diagnostics).</summary>
    public override string ToString() =>
        Endpoint is null ? $"{Id} (localhost:{Port})" : $"{Id} ({Endpoint}/{Catalog})";

    public void Dispose()
    {
        try { Server.Disconnect(); } catch { /* best effort */ }
        try { Server.Dispose(); } catch { /* best effort */ }
    }
}

/// <summary>Cross-tool state: an M edit lands via one tool (set_partition_m, any of the ~40 Power Query
/// transforms, set_shared_expression) and the save is laundered through a DIFFERENT tool (save_open_pbix).
/// This is the flag that connects them. Thread-safe: ModelPersistService is a DI singleton. Production
/// trackers live in <see cref="MDirtyRegistry"/>, keyed by the engine hosting the model (see
/// <see cref="ModelSession.MDirty"/>); a standalone `new MDirtyTracker()` (tests, the gate's pure
/// overload) has no registry wiring and behaves exactly as before.</summary>
public sealed class MDirtyTracker
{
    private readonly object _gate = new();
    private readonly List<string> _reasons = new();

    /// <summary>Registry wiring - set only by <see cref="MDirtyRegistry"/> at entry creation. Mark
    /// re-asserts the entry so a flag raised concurrently with an eviction is never lost; Clear offers
    /// the now-empty entry back for eviction. Both fire OUTSIDE <see cref="_gate"/> - the registry never
    /// runs caller code under this tracker's lock.</summary>
    internal MDirtyRegistry? Registry { get; init; }
    internal string? RegistryKey { get; init; }

    /// <summary>Eviction recency (DateTime.UtcNow.Ticks). Written via Interlocked by the registry only.</summary>
    internal long TouchedUtcTicks;

    public bool IsDirty { get { lock (_gate) return _reasons.Count > 0; } }

    /// <summary>Snapshot - the caller must never see the list mutate mid-enumeration.</summary>
    public IReadOnlyList<string> Reasons { get { lock (_gate) return _reasons.ToArray(); } }

    /// <summary>De-duplicates identical reasons: 40 fill_down calls on one partition are one pending edit,
    /// and the refusal text embeds this list.</summary>
    public void Mark(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return;
        lock (_gate)
        {
            if (!_reasons.Contains(reason, StringComparer.Ordinal)) _reasons.Add(reason);
        }
        if (Registry is { } reg && RegistryKey is { } key) reg.Reassert(key, this);
    }

    /// <summary><paramref name="why"/> documents the clearing site at the call, not in a log line.</summary>
    public void Clear(string why)
    {
        _ = why;
        lock (_gate) _reasons.Clear();
        if (Registry is { } reg && RegistryKey is { } key) reg.OfferEviction(key, this);
    }
}

/// <summary>Process-wide home of the pending-M flag, keyed by the ENGINE hosting the model
/// (endpoint ?? "local:"+port, plus the database name), NOT by the ModelSession object. The dirty
/// condition lives in the engine's in-memory model - an M edit is committed live via SaveChanges() and
/// survives any number of session objects - so the flag must survive them too: a reconnect after
/// "Unknown model sessionId", a parallel chat's second connect_model, or any transient-error retry lands
/// on the SAME flag, and a fresh session can never launder a dirty model past the M-refresh save gate.
///
/// Lifecycle: an entry is created on first access and removed only when its flag is cleared - the gate's
/// successful full refresh (or refresh_table(full: true) over the whole model) Clear()s it, which offers
/// the now-empty entry back for eviction - plus a bounded-count sweep of CLEAN entries so engines that
/// came and went with nothing pending never accumulate. A DIRTY entry is never evicted: losing it is
/// exactly the silent revert the gate exists to stop, so pending state outlives even the sweep.
///
/// Instantiable purely for test isolation; production uses the static <see cref="Default"/> singleton
/// (one registry per process, shared by every SessionStore, service and tool call).</summary>
internal sealed class MDirtyRegistry
{
    /// <summary>Engine keys compare case-insensitively: XMLA endpoints are URLs (host names are
    /// case-insensitive) and AS database names compare case-insensitively by default, so a
    /// differently-cased reconnect that missed the entry would be a gate bypass.</summary>
    private readonly ConcurrentDictionary<string, MDirtyTracker> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxEntries;

    internal static MDirtyRegistry Default { get; } = new();

    internal MDirtyRegistry(int maxEntries = 256) => _maxEntries = maxEntries;

    internal static string KeyFor(string? endpoint, int port, string database) =>
        $"{endpoint ?? $"local:{port}"}|{database}";

    /// <summary>The tracker for one engine+database, created on first access. Every access refreshes the
    /// entry's eviction recency, so an in-use clean entry is never the sweep's first pick.</summary>
    internal MDirtyTracker For(string? endpoint, int port, string database)
    {
        string key = KeyFor(endpoint, port, database);
        var tracker = _entries.GetOrAdd(key, k => new MDirtyTracker { Registry = this, RegistryKey = k });
        Interlocked.Exchange(ref tracker.TouchedUtcTicks, DateTime.UtcNow.Ticks);
        TrimExcessCleanEntries();
        return tracker;
    }

    /// <summary>Mark-side re-registration. A Mark that races an eviction (the bounded sweep, or a
    /// clear-evict from another session's save) must never be lost, so every Mark re-asserts its entry.
    /// If a For() already re-created the key with a fresh instance, the reasons are merged into the
    /// registered one - every interleaving converges on "the raised flag is registered" (fail closed;
    /// the worst race outcome is a stale-POSITIVE flag, never a lost one).</summary>
    internal void Reassert(string key, MDirtyTracker tracker)
    {
        var registered = _entries.GetOrAdd(key, tracker);
        if (!ReferenceEquals(registered, tracker))
        {
            foreach (var reason in tracker.Reasons) registered.Mark(reason);
        }
        Interlocked.Exchange(ref registered.TouchedUtcTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Clear-side eviction (clear-on-successful-gate): a cleared entry carries no information,
    /// so it is released rather than left to accumulate. Removes the entry only while it is still the
    /// registered instance AND still clean; a Mark that slips in around the removal is restored by the
    /// post-removal re-check plus <see cref="Reassert"/>.</summary>
    internal void OfferEviction(string key, MDirtyTracker tracker)
    {
        if (tracker.IsDirty) return;
        if (_entries.TryRemove(new KeyValuePair<string, MDirtyTracker>(key, tracker)) && tracker.IsDirty)
            Reassert(key, tracker);        // a Mark landed between the check and the removal - put it back
    }

    /// <summary>Bounded-count backstop: when the registry outgrows its cap, the oldest-touched CLEAN
    /// entries are dropped (dead engines: Desktops long closed, XMLA datasets never edited). Dirty
    /// entries are never candidates - the bound only limits engines with nothing pending, so a pending
    /// edit can never be swept into a gate bypass.</summary>
    private void TrimExcessCleanEntries()
    {
        if (_entries.Count <= _maxEntries) return;
        foreach (var kvp in _entries.ToArray()
                     .Where(e => !e.Value.IsDirty)
                     .OrderBy(e => Interlocked.Read(ref e.Value.TouchedUtcTicks)))
        {
            if (_entries.Count <= _maxEntries) break;
            if (_entries.TryRemove(kvp) && kvp.Value.IsDirty)
                Reassert(kvp.Key, kvp.Value);          // raced a Mark - the flag survives the sweep
        }
    }

    // ---- test seams (InternalsVisibleTo: SuperBiMcp.Tests) ----
    internal int Count => _entries.Count;
    internal bool ContainsKey(string key) => _entries.ContainsKey(key);
}

/// <summary>An open report (Report/Layout) loaded from a .pbix for editing.</summary>
public sealed class ReportSession
{
    public required string Id { get; init; }
    public required string PbixPath { get; init; }
    public required ReportLayout Layout { get; set; }
    public bool Dirty { get; set; }

    /// <summary>New binary parts (e.g. logo images) to write into the .pbix ZIP on Save - zip path -> bytes.</summary>
    public Dictionary<string, byte[]> PendingResources { get; } = new();

    /// <summary>When set, Save drops every existing base theme part (SharedResources/BaseThemes/*.json) EXCEPT
    /// this one - so a tier-accurate rebuild leaves only the chosen palette theme, not the starter's stale one.</summary>
    public string? KeepOnlyBaseThemePart { get; set; }
}

/// <summary>An open ENHANCED-report (PBIR) definition tree loaded from a .pbix or a PBIP folder for editing.
/// The parsed tree lives in PbirService.PbirModel; the session just holds it across tool calls.</summary>
public sealed class PbirSession
{
    public required string Id { get; init; }
    public required string SourcePath { get; init; }
    public required PbirService.PbirModel Model { get; init; }
    public bool Dirty { get; private set; }
    public void MarkDirty() => Dirty = true;
}

/// <summary>
/// Holds connections/documents across tool calls. Singleton: the long-lived TOM
/// <see cref="Server"/> object graph is what makes batched edits possible.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly ConcurrentDictionary<string, ModelSession> _models = new();
    private readonly ConcurrentDictionary<string, ReportSession> _reports = new();
    private readonly ConcurrentDictionary<string, PbirSession> _pbir = new();
    private int _seq;

    public string NewId(string prefix) => $"{prefix}-{Interlocked.Increment(ref _seq)}";

    // ---- model sessions ----
    public void AddModel(ModelSession s) => _models[s.Id] = s;
    public ModelSession GetModel(string id) =>
        _models.TryGetValue(id, out var s) ? s
            : throw new InvalidOperationException($"Unknown model sessionId '{id}'. Call connect_model first.");
    public IEnumerable<ModelSession> Models => _models.Values;
    public bool RemoveModel(string id)
    {
        if (_models.TryRemove(id, out var s)) { s.Dispose(); return true; }
        return false;
    }

    // ---- report sessions ----
    public void AddReport(ReportSession s) => _reports[s.Id] = s;
    public ReportSession GetReport(string id) =>
        _reports.TryGetValue(id, out var s) ? s
            : throw new InvalidOperationException($"Unknown reportSessionId '{id}'. Call open_report first.");
    public bool RemoveReport(string id) => _reports.TryRemove(id, out _);

    // ---- PBIR (enhanced-report) sessions ----
    public void AddPbir(PbirSession s) => _pbir[s.Id] = s;
    public PbirSession GetPbir(string id) =>
        _pbir.TryGetValue(id, out var s) ? s
            : throw new InvalidOperationException($"Unknown pbirSessionId '{id}'. Call read_pbir first.");
    public bool RemovePbir(string id) => _pbir.TryRemove(id, out _);

    public void Dispose()
    {
        foreach (var m in _models.Values) m.Dispose();
        _models.Clear();
        _reports.Clear();
        _pbir.Clear();
    }
}
