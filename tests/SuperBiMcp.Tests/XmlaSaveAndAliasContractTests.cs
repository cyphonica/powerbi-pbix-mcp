using System.Text.Json;
using System.Text.Json.Nodes;
using SuperBiMcp.Services;
using SuperBiMcp.Tools;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Adversarial-review fix round, wave 1 - the two contracts this file pins:
///
/// D9 - XMLA save semantics. An XMLA (Fabric/Service) session has no local .pbix: the dataset lives - and
/// persists - server-side, so save_open_pbix's only meaningful work is flushing a pending M change through
/// the gate's forced FULL refresh. That refresh IS the persistence, so it must report SUCCESS
/// (ok:true, persisted:"xmla-refresh", saveDispatched:false), never the old "no local engine port" failure
/// AFTER silently running (and billing) a full production refresh. A FAILED refresh keeps the dirty flag and
/// refuses with XMLA-specific text - the Desktop wording ("the .pbix on disk is unchanged ... then save") is
/// unfollowable for a session with no .pbix. No Desktop window machinery may ever run for an XMLA session.
///
/// D8 - back-compat alias keys (I1). Pre-Phase-0, save_open_pbix returned `saved` and the live persist route
/// returned `persistedToDisk` + `desktopSave.saved`; Phase 0 renamed them to `saveDispatched` and dropped the
/// rest, breaking external tenant scripts written against the documented contract. The legacy keys are
/// restored as DEPRECATED aliases that always mirror saveDispatched.
///
/// Everything here runs headless: no Desktop, no engine, no network. A detached test model cannot execute a
/// real Full refresh, so the XMLA SUCCESS path substitutes the service's internal gate seam with a stub that
/// honours the real gate's contract (clear-on-success, Ran); the FAILURE path uses the REAL gate, whose
/// refresh action throws on the detached model.
/// </summary>
public sealed class XmlaSaveAndAliasContractTests
{
    private static (ModelPersistService svc, SessionStore store) NewService()
    {
        var store = new SessionStore();
        return (new ModelPersistService(store), store);
    }

    private static ModelSession AddSession(SessionStore store, string id, int port, string? endpoint = null,
        string? pbixPath = null)
    {
        var session = new ModelSession
        {
            Id = id,
            Port = port,
            Endpoint = endpoint,
            Catalog = endpoint is null ? null : "dataset",
            Server = new TOM.Server(),
            // GUID-unique database name: MDirty is ENGINE-keyed (endpoint ?? "local:"+port, database name),
            // so a shared name would share one dirty flag across tests.
            Database = new TOM.Database("t-" + Guid.NewGuid().ToString("N")) { Model = new TOM.Model() },
            PbixPath = pbixPath,
        };
        store.AddModel(session);
        return session;
    }

    private static string NewEndpoint() =>
        "powerbi://api.powerbi.com/v1.0/myorg/WS-" + Guid.NewGuid().ToString("N");

    // ================================================================= D9: XMLA save semantics

    [Fact]
    public void XmlaSave_PendingMEdit_GateRefreshIsThePersistence_AndReportsSuccess()
    {
        var (svc, store) = NewService();
        var session = AddSession(store, "model-xmla-ok", port: 0, endpoint: NewEndpoint());
        session.MDirty.Mark("set_partition_m Fact_Sales/Partition");

        // the stub stands in for a server-side Full refresh that SUCCEEDS (same contract as the real gate:
        // clear the flag, report Ran) - a detached test model cannot run one for real.
        svc.MRefreshGateOverride = s => { s.MDirty.Clear("test refresh ran"); return MRefreshOutcome.Ran; };

        var r = svc.SaveOpenPbix("model-xmla-ok", pbixPath: null, saveRetries: 1);

        Assert.Equal("xmla-refresh", r.Persisted);
        Assert.True(r.MRefreshRan);          // the caller is told a capacity-consuming refresh ran
        Assert.False(r.SaveDispatched);      // no window ever receives a keystroke
        Assert.Equal(0, r.DesktopPid);
        Assert.Null(r.Error);                // SUCCESS - not the old "no local engine port" failure
        Assert.False(session.MDirty.IsDirty);
    }

    [Fact]
    public void XmlaSave_ToolResponse_OkTrue_PersistedXmlaRefresh_SaveDispatchedFalse()
    {
        var (svc, store) = NewService();
        var session = AddSession(store, "model-xmla-tool", port: 0, endpoint: NewEndpoint());
        session.MDirty.Mark("set_partition_m Fact_Sales/Partition");
        svc.MRefreshGateOverride = s => { s.MDirty.Clear("test refresh ran"); return MRefreshOutcome.Ran; };

        var json = JsonNode.Parse(PersistTools.SaveOpenPbix(svc, "model-xmla-tool"))!;

        Assert.True((bool)json["ok"]!);                              // the refresh WAS the save - success
        Assert.False((bool)json["saveDispatched"]!);
        Assert.False((bool)json["saved"]!);                          // deprecated alias mirrors saveDispatched
        Assert.Equal("xmla-refresh", (string)json["persisted"]!);
        Assert.Null(json["error"]);
        Assert.Contains("server-side refresh", (string)json["note"]!);
    }

    [Fact]
    public void XmlaSave_CleanSession_OkTrue_AndHonestlyReportsNoRefreshRan()
    {
        var (svc, store) = NewService();
        AddSession(store, "model-xmla-clean", port: 0, endpoint: NewEndpoint());

        var r = svc.SaveOpenPbix("model-xmla-clean", pbixPath: null, saveRetries: 1);
        Assert.Equal("xmla-refresh", r.Persisted);
        Assert.False(r.MRefreshRan);                                 // nothing pending, nothing billed

        var json = JsonNode.Parse(PersistTools.SaveOpenPbix(svc, "model-xmla-clean"))!;
        Assert.True((bool)json["ok"]!);
        Assert.Contains("nothing was pending", (string)json["note"]!);
    }

    [Fact]
    public void XmlaSave_FailedRefresh_XmlaSpecificRefusal_AndTheDirtyFlagSurvives()
    {
        var (svc, store) = NewService();
        var session = AddSession(store, "model-xmla-fail", port: 0, endpoint: NewEndpoint());
        session.MDirty.Mark("set_partition_m Fact_Sales/Partition"); // REAL gate: the detached model cannot refresh

        var ex = Assert.Throws<MRefreshRequiredException>(() => svc.SaveOpenPbix("model-xmla-fail", null, 1));

        // XMLA-specific, followable guidance - not the Desktop wording
        Assert.Contains("XMLA", ex.Message);
        Assert.Contains("refresh IS the persistence", ex.Message);
        Assert.Contains("refresh_table", ex.Message);                    // the terminal step over XMLA
        Assert.DoesNotContain(".pbix on disk is unchanged", ex.Message); // a promise with no .pbix is a lie
        Assert.DoesNotContain("then save", ex.Message);                  // there is no save step after the refresh
        Assert.DoesNotContain("Power Query (M)", ex.Message);            // FixModel's soft-catch filter substring

        // NOT consumed: a retry must re-run the refresh
        Assert.True(session.MDirty.IsDirty);

        // and at the tool boundary the retry surfaces as ok:false with the same XMLA text
        var json = JsonNode.Parse(PersistTools.SaveOpenPbix(svc, "model-xmla-fail"))!;
        Assert.False((bool)json["ok"]!);
        Assert.Contains("XMLA", (string)json["error"]!);
        Assert.True(session.MDirty.IsDirty);
    }

    // ================================================================= D8: deprecated alias keys (I1)

    [Fact]
    public void SaveOpenPbixTool_EmitsTheDeprecatedSavedAlias_AlwaysEqualToSaveDispatched()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = dir.File("open.pbix");
        File.WriteAllText(pbix, "on-disk");
        var (svc, store) = NewService();
        AddSession(store, "model-alias-1", port: 1, pbixPath: pbix); // port 1: the dispatch fails cleanly headless

        var json = JsonNode.Parse(PersistTools.SaveOpenPbix(svc, "model-alias-1", pbix, saveRetries: 1))!;

        Assert.NotNull(json["saveDispatched"]);
        Assert.NotNull(json["saved"]);                               // the pre-Phase-0 key is back
        Assert.Equal((bool)json["saveDispatched"]!, (bool)json["saved"]!);
        Assert.False((bool)json["ok"]!);
        Assert.NotNull(json["desktopPid"]);
        Assert.NotNull(json["error"]);
    }

    [Fact]
    public void PersistLiveResult_RestoresPersistedToDisk_AndSaved_AsAliasesOfSaveDispatched()
    {
        // the dispatched=true case is unreachable headless (it needs a real Desktop), so the response is
        // pinned through the internal builder seam the live route itself uses.
        var onJson = JsonNode.Parse(JsonSerializer.Serialize(ModelPersistService.BuildPersistLiveResult(
            "model-1", 12345, new[] { "add_measure Fact[M]" }, recalculated: true, recalcError: null,
            new ModelPersistService.SaveOutcome { SaveDispatched = true, DesktopPid = 4242 })))!;

        Assert.True((bool)onJson["ok"]!);
        Assert.Equal("live_desktop", (string)onJson["route"]!);
        Assert.True((bool)onJson["saveDispatched"]!);
        Assert.True((bool)onJson["saved"]!);                         // deprecated alias
        Assert.True((bool)onJson["persistedToDisk"]!);               // deprecated alias (pre-Phase-0 key)
        Assert.True((bool)onJson["desktopSave"]!["saveDispatched"]!);
        Assert.True((bool)onJson["desktopSave"]!["saved"]!);         // deprecated alias inside desktopSave
        Assert.Equal(4242, (int)onJson["desktopSave"]!["desktopPid"]!);

        var offJson = JsonNode.Parse(JsonSerializer.Serialize(ModelPersistService.BuildPersistLiveResult(
            "model-1", 12345, Array.Empty<string>(), recalculated: false, recalcError: null,
            new ModelPersistService.SaveOutcome { SaveDispatched = false, Error = "no window" })))!;

        Assert.False((bool)offJson["saveDispatched"]!);
        Assert.False((bool)offJson["saved"]!);
        Assert.False((bool)offJson["persistedToDisk"]!);
        Assert.False((bool)offJson["desktopSave"]!["saved"]!);
        Assert.Equal("no window", (string)offJson["desktopSave"]!["error"]!);
    }
}
