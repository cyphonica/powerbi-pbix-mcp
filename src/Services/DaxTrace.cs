using System.Collections.Concurrent;
using System.Globalization;
using AMO = Microsoft.AnalysisServices;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>One captured trace event, flattened to plain values (no AMO types leak to callers).</summary>
public sealed record DaxTraceEvent(
    string EventClass,
    string? EventSubclass,
    long? DurationMs,
    long? CpuTimeMs,
    string? TextData,
    DateTime? StartTime,
    string? Database);

/// <summary>Computed FE/SE split for one capture window (Server Timings style).</summary>
public sealed record DaxTraceSummary(
    long QueryMs,
    long StorageEngineMs,
    long FormulaEngineMs,
    int StorageEngineQueries,
    int CacheMatches,
    double? StorageEnginePct);

/// <summary>
/// Analysis Services session-scoped server traces for DAX performance work: QueryEnd, VertiPaq SE
/// QueryEnd, VertiPaq SE CacheMatch and DAXEvaluationLog captured via the AMO Trace API on the
/// session's existing TOM server connection. One active trace per model session; stop returns the
/// structured events plus the FE/SE split. The capture is server-wide on the instance (Desktop's
/// embedded engine hosts a single database), filtered to the session database when the engine
/// stamps DatabaseName on the event.
/// </summary>
public static class DaxTrace
{
    private sealed class ActiveTrace
    {
        public required TOM.Trace Trace { get; init; }
        public required string TraceId { get; init; }
        public required string Database { get; init; }
        public DateTime StartedUtc { get; } = DateTime.UtcNow;
        public bool DaxEvaluationLogCaptured { get; init; }
        public readonly object Gate = new();
        public readonly List<DaxTraceEvent> Events = new();
    }

    // sessionId -> active trace. Plain dictionary (not weak): stop_dax_trace must always find it.
    private static readonly ConcurrentDictionary<string, ActiveTrace> Active = new(StringComparer.Ordinal);

    public static object Start(ModelSession session)
    {
        if (Active.ContainsKey(session.Id))
            throw new InvalidOperationException(
                "A DAX trace is already running on this session. Call stop_dax_trace first.");

        string traceId = $"SuperBI_DaxTrace_{Guid.NewGuid():N}";
        var trace = session.Server.Traces.Add(traceId);
        bool daxLog = true;
        AddEvents(trace, includeDaxEvaluationLog: true);
        try
        {
            trace.Update();
        }
        catch
        {
            // Older engines reject the DAXEvaluationLog event class - retry with the core trio only.
            daxLog = false;
            trace.Events.Clear();
            AddEvents(trace, includeDaxEvaluationLog: false);
            trace.Update();
        }

        var active = new ActiveTrace
        {
            Trace = trace,
            TraceId = traceId,
            Database = session.Database.Name,
            DaxEvaluationLogCaptured = daxLog,
        };
        trace.OnEvent += (_, e) =>
        {
            var ev = Flatten(e);
            lock (active.Gate) active.Events.Add(ev);
        };
        trace.Start();

        if (!Active.TryAdd(session.Id, active))
        {
            try { trace.Stop(); trace.Drop(); } catch { /* best effort */ }
            throw new InvalidOperationException(
                "A DAX trace is already running on this session. Call stop_dax_trace first.");
        }
        return new
        {
            ok = true,
            traceId,
            events = daxLog
                ? new[] { "QueryEnd", "VertiPaqSEQueryEnd", "VertiPaqSEQueryCacheMatch", "DAXEvaluationLog" }
                : new[] { "QueryEnd", "VertiPaqSEQueryEnd", "VertiPaqSEQueryCacheMatch" },
            daxEvaluationLogCaptured = daxLog,
            note = "Run the queries to profile (run_dax / dax_benchmark), then stop_dax_trace to collect the events.",
        };
    }

    public static object Stop(ModelSession session)
    {
        if (!Active.TryRemove(session.Id, out var active))
            throw new InvalidOperationException(
                "No DAX trace is running on this session. Call start_dax_trace first.");

        try { active.Trace.Stop(); } catch { /* engine may already have dropped it */ }
        Thread.Sleep(250);            // let in-flight event callbacks land before snapshotting
        List<DaxTraceEvent> events;
        lock (active.Gate) events = new List<DaxTraceEvent>(active.Events);
        try { active.Trace.Drop(); } catch { /* best effort */ }

        // keep only this session's database when the engine stamped one (Desktop = single database).
        var scoped = events
            .Where(e => e.Database is null || e.Database.Length == 0 ||
                        string.Equals(e.Database, active.Database, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var summary = ComputeSummary(scoped);
        return new
        {
            ok = true,
            traceId = active.TraceId,
            startedUtc = active.StartedUtc,
            daxEvaluationLogCaptured = active.DaxEvaluationLogCaptured,
            eventCount = scoped.Count,
            summary = new
            {
                queryMs = summary.QueryMs,
                formulaEngineMs = summary.FormulaEngineMs,
                storageEngineMs = summary.StorageEngineMs,
                storageEngineQueries = summary.StorageEngineQueries,
                cacheMatches = summary.CacheMatches,
                storageEnginePct = summary.StorageEnginePct,
            },
            events = scoped.Select(e => new
            {
                eventClass = e.EventClass,
                eventSubclass = e.EventSubclass,
                durationMs = e.DurationMs,
                cpuTimeMs = e.CpuTimeMs,
                startTime = e.StartTime,
                textData = e.TextData,
            }),
        };
    }

    private static void AddEvents(TOM.Trace trace, bool includeDaxEvaluationLog)
    {
        // Per-event column sets kept to columns the profiler defines for that event class.
        Add(trace, AMO.TraceEventClass.QueryEnd,
            AMO.TraceColumn.EventClass, AMO.TraceColumn.EventSubclass, AMO.TraceColumn.TextData,
            AMO.TraceColumn.Duration, AMO.TraceColumn.CpuTime, AMO.TraceColumn.StartTime, AMO.TraceColumn.DatabaseName);
        Add(trace, AMO.TraceEventClass.VertiPaqSEQueryEnd,
            AMO.TraceColumn.EventClass, AMO.TraceColumn.EventSubclass, AMO.TraceColumn.TextData,
            AMO.TraceColumn.Duration, AMO.TraceColumn.CpuTime, AMO.TraceColumn.StartTime, AMO.TraceColumn.DatabaseName);
        Add(trace, AMO.TraceEventClass.VertiPaqSEQueryCacheMatch,
            AMO.TraceColumn.EventClass, AMO.TraceColumn.EventSubclass, AMO.TraceColumn.TextData,
            AMO.TraceColumn.StartTime, AMO.TraceColumn.DatabaseName);
        if (includeDaxEvaluationLog)
            Add(trace, AMO.TraceEventClass.DAXEvaluationLog,
                AMO.TraceColumn.EventClass, AMO.TraceColumn.TextData,
                AMO.TraceColumn.StartTime, AMO.TraceColumn.DatabaseName);
    }

    private static void Add(TOM.Trace trace, AMO.TraceEventClass eventClass, params AMO.TraceColumn[] columns)
    {
        var te = new TOM.TraceEvent(eventClass);
        foreach (var c in columns) te.Columns.Add(c);
        trace.Events.Add(te);
    }

    private static DaxTraceEvent Flatten(TOM.TraceEventArgs e)
    {
        string? Get(AMO.TraceColumn c)
        {
            try { return e[c]; } catch { return null; }
        }
        static long? ParseLong(string? s) =>
            long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        static DateTime? ParseTime(string? s) =>
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var v) ? v : null;

        return new DaxTraceEvent(
            EventClass: e.EventClass.ToString(),
            EventSubclass: Get(AMO.TraceColumn.EventSubclass),
            DurationMs: ParseLong(Get(AMO.TraceColumn.Duration)),
            CpuTimeMs: ParseLong(Get(AMO.TraceColumn.CpuTime)),
            TextData: Get(AMO.TraceColumn.TextData),
            StartTime: ParseTime(Get(AMO.TraceColumn.StartTime)),
            Database: Get(AMO.TraceColumn.DatabaseName));
    }

    /// <summary>
    /// Server Timings arithmetic, pure and unit-testable. QueryMs sums QueryEnd durations;
    /// StorageEngineMs sums VertiPaqSEQueryEnd durations of subclass 0 (VertiPaq Scan) only - the
    /// internal subclass-10 scans are nested inside subclass 0 and would double-count; FE = query - SE
    /// (floored at 0: SE runs on parallel threads so its sum can exceed wall-clock query time).
    /// </summary>
    internal static DaxTraceSummary ComputeSummary(IReadOnlyList<DaxTraceEvent> events)
    {
        long queryMs = 0, seMs = 0;
        int seQueries = 0, cacheMatches = 0;
        foreach (var e in events)
        {
            switch (e.EventClass)
            {
                case "QueryEnd":
                    queryMs += e.DurationMs ?? 0;
                    break;
                case "VertiPaqSEQueryEnd":
                    if (IsScanSubclass(e.EventSubclass))
                    {
                        seMs += e.DurationMs ?? 0;
                        seQueries++;
                    }
                    break;
                case "VertiPaqSEQueryCacheMatch":
                    cacheMatches++;
                    break;
            }
        }
        long feMs = Math.Max(queryMs - seMs, 0);
        double? sePct = queryMs > 0 ? Math.Round(100.0 * Math.Min(seMs, queryMs) / queryMs, 1) : null;
        return new DaxTraceSummary(queryMs, seMs, feMs, seQueries, cacheMatches, sePct);
    }

    /// <summary>Subclass 0 is the VertiPaq Scan; the engine reports it numerically or by name.</summary>
    internal static bool IsScanSubclass(string? subclass) =>
        string.IsNullOrWhiteSpace(subclass) ||
        subclass.Trim() == "0" ||
        subclass.Trim().Equals("VertiPaqScan", StringComparison.OrdinalIgnoreCase);
}
