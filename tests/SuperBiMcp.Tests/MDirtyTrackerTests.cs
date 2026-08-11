using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The pending-M-edit flag itself. <see cref="MRefreshGateTests"/> proves what the flag DRIVES at save time;
/// these prove the flag's own semantics: mark/clear round-trip, reason de-duplication, snapshot reads, and
/// safety under concurrent marking. The production tracker is ENGINE-keyed in the process-wide
/// <see cref="MDirtyRegistry"/> (endpoint ?? "local:"+port, database name) - <see cref="ModelSession.MDirty"/>
/// delegates there, so tool calls on different threads AND different session objects against the same engine
/// share one instance; <see cref="MDirtyRegistryTests"/> proves that sharing. A standalone
/// `new MDirtyTracker()` (as used here) has no registry wiring and pins the flag's raw semantics.
///
/// XMLA sessions (Port == 0, Endpoint set) carry the tracker too: an M edit needs the same forced refresh
/// whether the engine is Desktop or Fabric, so "local only" filtering keyed off Port would silently exempt
/// every Fabric session from the gate.
/// </summary>
public sealed class MDirtyTrackerTests
{
    // ---------------- mark / clear round-trip ----------------

    [Fact]
    public void ANewTracker_IsClean()
    {
        var dirty = new MDirtyTracker();

        Assert.False(dirty.IsDirty);
        Assert.Empty(dirty.Reasons);
    }

    [Fact]
    public void Mark_RaisesTheFlag_AndRecordsTheReason()
    {
        var dirty = new MDirtyTracker();

        dirty.Mark("set_partition_m Sales/Partition");

        Assert.True(dirty.IsDirty);
        Assert.Equal("set_partition_m Sales/Partition", Assert.Single(dirty.Reasons));
    }

    [Fact]
    public void Clear_DropsTheFlag_AndEveryReasonWithIt()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("set_partition_m Sales/Partition");
        dirty.Mark("fill_down Sales/Partition");

        dirty.Clear("full refresh before save");

        Assert.False(dirty.IsDirty);
        Assert.Empty(dirty.Reasons);
    }

    [Fact]
    public void MarkAfterClear_RaisesTheFlagAgain()
    {
        // a failed save retried later must re-arm the gate, not stay clean on a stale clear
        var dirty = new MDirtyTracker();
        dirty.Mark("group_by Sales/Partition");
        dirty.Clear("full refresh before save");

        dirty.Mark("pivot_column Sales/Partition");

        Assert.True(dirty.IsDirty);
        Assert.Equal("pivot_column Sales/Partition", Assert.Single(dirty.Reasons));
    }

    // ---------------- reasons ----------------

    [Fact]
    public void Mark_DeDuplicatesIdenticalReasons()
    {
        // 40 fill_down calls on one partition are one pending edit; the refusal text embeds this list
        var dirty = new MDirtyTracker();
        for (int i = 0; i < 40; i++) dirty.Mark("fill_down Sales/Partition");

        Assert.Equal("fill_down Sales/Partition", Assert.Single(dirty.Reasons));
    }

    [Fact]
    public void Mark_IgnoresANullOrBlankReason()
    {
        var dirty = new MDirtyTracker();

        dirty.Mark(null!);
        dirty.Mark("");
        dirty.Mark("   ");

        Assert.False(dirty.IsDirty);
        Assert.Empty(dirty.Reasons);
    }

    [Fact]
    public void Reasons_IsASnapshot_NotALiveView()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("set_shared_expression SourceFolder");

        var snapshot = dirty.Reasons;
        dirty.Mark("set_partition_m Sales/Partition");

        Assert.Single(snapshot);          // the earlier read never mutates under the caller
        Assert.Equal(2, dirty.Reasons.Count);
    }

    // ---------------- XMLA sessions carry the tracker ----------------

    [Fact]
    public void AnXmlaSession_PortZeroNoPid_CarriesAWorkingTracker()
    {
        // GUID-unique workspace: the tracker lives in the process-wide engine-keyed MDirtyRegistry (D10),
        // so a fixed endpoint would share state with any other test touching the same key.
        string endpoint = $"powerbi://api.powerbi.com/v1.0/myorg/Workspace-{Guid.NewGuid():N}";
        var session = new ModelSession
        {
            Id = "model-xmla",
            Port = 0,                      // XMLA sessions have no local engine port
            Server = new TOM.Server(),
            Database = new TOM.Database("t") { Model = new TOM.Model() },
            Endpoint = endpoint,
            Catalog = "Dataset",
        };

        Assert.NotNull(session.MDirty);
        Assert.Same(session.MDirty, session.MDirty);   // one tracker per ENGINE, not one per read

        session.MDirty.Mark("set_shared_expression SourceFolder");

        Assert.True(session.MDirty.IsDirty);

        // the same gate the save path runs refuses this session's save when its forced refresh fails
        Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(session.MDirty, () => throw new Exception("boom"), null));
        Assert.True(session.MDirty.IsDirty);
    }

    // ---------------- thread-safety of the flag ----------------

    /// <summary>Dedicated threads, not Task.Run: a starved thread pool on a small CI box would serialise the
    /// workers and the barrier would wait on pool injection. Each worker records its own failure - an
    /// unhandled exception on a raw Thread would take the whole test process down instead of failing here.</summary>
    private static Exception[] RunConcurrently(int threads, Action<int> body)
    {
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        using var start = new Barrier(threads);
        var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
        {
            try
            {
                start.SignalAndWait();     // maximise contention: everyone enters the tracker together
                body(t);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        })).ToArray();
        foreach (var w in workers) w.Start();
        foreach (var w in workers) w.Join();
        return failures.ToArray();
    }

    [Fact]
    public void EightThreadsMarkingConcurrently_NeverThrow_AndEveryReasonSurvivesExactlyOnce()
    {
        const int threads = 8, perThread = 200;
        var dirty = new MDirtyTracker();

        var failures = RunConcurrently(threads, t =>
        {
            for (int i = 0; i < perThread; i++)
            {
                dirty.Mark($"fill_down Sales/P{t}-{i}");
                dirty.Mark("set_partition_m Sales/Partition");   // shared reason: de-dup under contention
                _ = dirty.IsDirty;
                _ = dirty.Reasons;         // snapshot reads race the writes and must never see a torn list
            }
        });

        Assert.Empty(failures);
        Assert.True(dirty.IsDirty);
        var reasons = dirty.Reasons;
        Assert.Equal(threads * perThread + 1, reasons.Count);
        Assert.Equal(reasons.Count, reasons.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ConcurrentMarkAndClear_NeverThrow_AndTheFinalClearLeavesItClean()
    {
        var dirty = new MDirtyTracker();

        var failures = RunConcurrently(4, t =>
        {
            for (int i = 0; i < 500; i++)
            {
                if (t % 2 == 0) dirty.Mark($"merge_queries Sales/P{t}-{i}");
                else dirty.Clear("racing clear");
                _ = dirty.IsDirty;
            }
        });

        Assert.Empty(failures);
        dirty.Clear("final");
        Assert.False(dirty.IsDirty);
        Assert.Empty(dirty.Reasons);
    }
}
