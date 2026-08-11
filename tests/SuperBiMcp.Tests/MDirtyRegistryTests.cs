using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// D10 regression: the pending-M flag is keyed to the ENGINE (endpoint ?? "local:"+port, database name),
/// NOT to the ModelSession object. The confirmed finding: ModelService.Connect creates a NEW ModelSession
/// with a fresh, clean tracker per call, so a reconnect after "Unknown model sessionId" (or a parallel
/// chat's second connect_model to the same Desktop) bypassed the M-refresh gate and a bare Ctrl+S saved an
/// M-dirty model - the silent revert I7 exists to stop. These tests reproduce that scenario through the
/// production path (<see cref="ModelSession.MDirty"/>, backed by <see cref="MDirtyRegistry.Default"/>)
/// and prove the shared registry closes it, plus pin the registry's eviction contract: only a Clear (the
/// gate's successful full refresh) releases an entry, clean entries are count-bounded, and a DIRTY entry
/// is never evicted or lost to a race.
///
/// Isolation: cross-session tests use GUID database names/endpoints so their Default-registry keys never
/// collide across parallel test classes; bulk/eviction tests run on private registry instances.
/// </summary>
public sealed class MDirtyRegistryTests
{
    private static ModelSession NewSession(string id, int port, string database, string? endpoint = null) => new()
    {
        Id = id,
        Port = port,
        Server = new TOM.Server(),
        Database = new TOM.Database(database) { Model = new TOM.Model() },
        Endpoint = endpoint,
        Catalog = endpoint is null ? null : database,
    };

    private static string UniqueDb() => "db-" + Guid.NewGuid().ToString("N");

    // ---------------- the finding's failure scenario, closed ----------------

    [Fact]
    public void Finding_ASecondSessionToTheSameEngine_InheritsThePendingMEdit_AndItsGateRefusesTheSave()
    {
        string db = UniqueDb();
        var first = NewSession("model-1", 51001, db);
        first.MDirty.Mark("set_partition_m Fact_Sales/Partition");   // Desktop's live model is now M-dirty

        // the finding: a transient error / lost SessionStore / parallel chat makes the agent call
        // connect_model again -> a brand-new ModelSession object for the SAME engine.
        var second = NewSession("model-2", 51001, db);

        Assert.True(second.MDirty.IsDirty);                          // pre-fix this was false: gate bypassed
        Assert.Same(first.MDirty, second.MDirty);                    // literally one engine-keyed tracker

        // save_open_pbix(model-2) must now hit the gate; with the refresh failing, the save is refused
        // instead of dispatching the bare Ctrl+S of the failure scenario.
        Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(second.MDirty, () => throw new Exception("source offline"), null));
        Assert.True(first.MDirty.IsDirty);                           // and the failure clears nothing
    }

    [Fact]
    public void GateSuccessOnAnySession_ClearsTheFlagForEverySessionOnThatEngine_AndReleasesTheEntry()
    {
        string db = UniqueDb();
        var first = NewSession("model-1", 51002, db);
        var second = NewSession("model-2", 51002, db);
        first.MDirty.Mark("fill_down Sales/Partition");

        Assert.Equal(MRefreshOutcome.Ran,
            MRefreshGate.EnsureFullRefreshBeforeSave(second.MDirty, () => { /* refresh ok */ }, null));

        // clear-on-successful-gate releases the registry entry (checked BEFORE touching .MDirty again,
        // which would lazily re-create a clean one - that re-creation is free and carries no state).
        Assert.False(MDirtyRegistry.Default.ContainsKey(MDirtyRegistry.KeyFor(null, 51002, db)));
        Assert.False(first.MDirty.IsDirty);
        Assert.False(second.MDirty.IsDirty);
    }

    [Fact]
    public void DistinctEngines_NeverShareTheFlag()
    {
        string db = UniqueDb();
        var portA = NewSession("model-a", 51003, db);
        var portB = NewSession("model-b", 51004, db);                // same database name, other port
        var otherDb = NewSession("model-c", 51003, UniqueDb());      // same port, other database

        portA.MDirty.Mark("set_partition_m T/P");

        Assert.False(portB.MDirty.IsDirty);
        Assert.False(otherDb.MDirty.IsDirty);
    }

    [Fact]
    public void XmlaSessions_ShareByEndpointAndDatabase_CaseInsensitively()
    {
        string ws = $"powerbi://api.powerbi.com/v1.0/myorg/WS-{Guid.NewGuid():N}";
        string db = UniqueDb();
        var first = NewSession("model-x1", 0, db, endpoint: ws);
        first.MDirty.Mark("set_shared_expression SourceFolder");

        // a reconnect that cases the endpoint differently must land on the SAME flag - a miss is a bypass
        var second = NewSession("model-x2", 0, db, endpoint: ws.ToUpperInvariant());

        Assert.True(second.MDirty.IsDirty);
        Assert.Same(first.MDirty, second.MDirty);
    }

    // ---------------- eviction contract ----------------

    [Fact]
    public void Sweep_BoundsCleanEntries_ButNeverEvictsADirtyOne()
    {
        var reg = new MDirtyRegistry(maxEntries: 8);
        var pending = reg.For(null, 1, "keep");
        pending.Mark("set_partition_m T/P");                         // real pending state - must be immortal

        for (int i = 0; i < 50; i++) _ = reg.For(null, 1000 + i, "transient");   // dead engines come and go

        Assert.True(reg.Count <= 8, $"registry grew to {reg.Count}, cap is 8");
        Assert.True(reg.ContainsKey(MDirtyRegistry.KeyFor(null, 1, "keep")));
        Assert.Same(pending, reg.For(null, 1, "keep"));              // same instance, flag intact
        Assert.True(pending.IsDirty);
    }

    [Fact]
    public void Sweep_WhenEveryEntryIsDirty_TheCapYieldsRatherThanDropPendingState()
    {
        var reg = new MDirtyRegistry(maxEntries: 4);
        for (int i = 0; i < 10; i++) reg.For(null, 2000 + i, "d").Mark($"set_partition_m T/P{i}");

        Assert.Equal(10, reg.Count);                                 // over cap, by design: dirty > bounded
        for (int i = 0; i < 10; i++)
            Assert.True(reg.For(null, 2000 + i, "d").IsDirty);
    }

    [Fact]
    public void ClearReleasesTheEntry_AndALateMarkOnTheOldReference_ReassertsTheSameInstance()
    {
        var reg = new MDirtyRegistry();
        string key = MDirtyRegistry.KeyFor(null, 7, "race");
        var tracker = reg.For(null, 7, "race");

        tracker.Clear("full refresh before save");                   // the gate's successful clear
        Assert.False(reg.ContainsKey(key));                          // entry released

        tracker.Mark("set_partition_m T/P");                         // a caller still holding the reference
        Assert.True(reg.ContainsKey(key));                           // the Mark re-registered itself
        Assert.Same(tracker, reg.For(null, 7, "race"));              // the SAME instance, nothing forked
        Assert.True(reg.For(null, 7, "race").IsDirty);
    }

    [Fact]
    public void ALateMarkAfterTheKeyWasRecreated_MergesIntoTheRegisteredTracker_NoFlagIsEverLost()
    {
        var reg = new MDirtyRegistry();
        var orphaned = reg.For(null, 8, "merge");
        orphaned.Clear("full refresh before save");                  // entry released
        var registered = reg.For(null, 8, "merge");                  // fresh instance for the same engine
        Assert.NotSame(orphaned, registered);

        orphaned.Mark("pivot_column T/P");                           // late mark on the orphaned instance

        Assert.True(registered.IsDirty);                             // merged: the registered flag is up
        Assert.Equal("pivot_column T/P", Assert.Single(registered.Reasons));
        Assert.Same(registered, reg.For(null, 8, "merge"));
    }

    // ---------------- key shape ----------------

    [Fact]
    public void KeyFor_IsEndpointOrLocalPort_PlusDatabaseName()
    {
        Assert.Equal("local:12345|Model_abc", MDirtyRegistry.KeyFor(null, 12345, "Model_abc"));
        Assert.Equal("powerbi://api.powerbi.com/v1.0/myorg/WS|Sales",
            MDirtyRegistry.KeyFor("powerbi://api.powerbi.com/v1.0/myorg/WS", 0, "Sales"));
    }
}
