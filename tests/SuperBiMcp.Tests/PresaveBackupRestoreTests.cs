using SuperBiMcp.Services;
using TOM = Microsoft.AnalysisServices.Tabular;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The live save route's crash shield. Scripted Ctrl+S hands the write to Power BI Desktop, and the forced
/// pre-save M refresh runs inside that same Desktop - so from the moment SaveOpenPbix starts, the .pbix on
/// disk can be half-written by a process this code does not control. The shield is a {path}.presave.bak copy
/// taken before either can touch the file, and a REFUSED gated save that still moved the file is rolled back
/// from it, so the refusal's "the .pbix on disk is unchanged" promise is enforced rather than assumed.
/// Everything here runs headless: no Desktop, no engine, no network (port 1 never hosts an msmdsrv).
/// </summary>
public sealed class PresaveBackupRestoreTests
{
    private static string WritePbix(TempDir dir, string name, string content)
    {
        string p = dir.File(name);
        File.WriteAllText(p, content);
        return p;
    }

    // ---------------------------------------------------------------- the shield itself

    [Fact]
    public void TakePresaveBackup_CopiesTheFile_AndOverwritesItsOwnPreviousShield()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "v1");

        string? bak = ModelPersistService.TakePresaveBackup(pbix);

        Assert.Equal(pbix + ".presave.bak", bak);
        Assert.Equal("v1", File.ReadAllText(bak!));

        // a later save overwrites the shield - it is this code's own file, never the user's
        File.WriteAllText(pbix, "v2 - a later on-disk state");
        Assert.Equal(bak, ModelPersistService.TakePresaveBackup(pbix));
        Assert.Equal("v2 - a later on-disk state", File.ReadAllText(bak!));
    }

    [Fact]
    public void TakePresaveBackup_IsNull_WhenThereIsNothingOnDiskToShield()
    {
        using var dir = Fixtures.NewWorkDir();
        Assert.Null(ModelPersistService.TakePresaveBackup(dir.File("missing.pbix")));
        Assert.Null(ModelPersistService.TakePresaveBackup(""));
        Assert.False(File.Exists(dir.File("missing.pbix.presave.bak")));
    }

    // ---------------------------------------------------------------- restore

    [Fact]
    public void RestoreFromBackup_PutsTheFileBack_AndKeepsTheShieldForASecondRestore()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "original bytes");
        ModelPersistService.TakePresaveBackup(pbix);

        File.WriteAllText(pbix, "HALF-WRITTEN");
        Assert.True(ModelPersistService.RestoreFromBackup(pbix));

        Assert.Equal("original bytes", File.ReadAllText(pbix));
        Assert.True(File.Exists(pbix + ".presave.bak"));             // never consumed
        Assert.Equal("original bytes", File.ReadAllText(pbix + ".presave.bak"));
    }

    [Fact]
    public void RestoreFromBackup_RecreatesAFileThatVanished()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "original bytes");
        ModelPersistService.TakePresaveBackup(pbix);

        File.Delete(pbix);
        Assert.True(ModelPersistService.RestoreFromBackup(pbix));
        Assert.Equal("original bytes", File.ReadAllText(pbix));
    }

    [Fact]
    public void RestoreFromBackup_ReportsFalse_WhenNoShieldExists_AndTouchesNothing()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "untouched");
        Assert.False(ModelPersistService.RestoreFromBackup(pbix));
        Assert.Equal("untouched", File.ReadAllText(pbix));
    }

    // ---------------------------------------------------------------- the gate-refusal hook

    [Fact]
    public void GateRefusalHook_LeavesAnUntouchedFileAlone()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "v1");
        string? bak = ModelPersistService.TakePresaveBackup(pbix);
        // snapshot into locals NOW - FileInfo lazy-loads on first property access
        var fi = new FileInfo(pbix);
        DateTime preWrite = fi.LastWriteTimeUtc;
        long preLen = fi.Length;

        var svc = new ModelPersistService(new SessionStore());
        svc.RestoreAfterFailedGatedSave(pbix, bak, preWrite, preLen, new Exception("refused"));

        // no write happened: content AND timestamp are exactly as they were
        Assert.Equal("v1", File.ReadAllText(pbix));
        Assert.Equal(preWrite, File.GetLastWriteTimeUtc(pbix));
    }

    [Fact]
    public void GateRefusalHook_RestoresAFileTheRefusedSaveMoved()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "v1");
        string? bak = ModelPersistService.TakePresaveBackup(pbix);
        // snapshot into locals NOW - FileInfo lazy-loads on first property access
        var fi = new FileInfo(pbix);
        DateTime preWrite = fi.LastWriteTimeUtc;
        long preLen = fi.Length;

        File.WriteAllText(pbix, "PARTIALLY WRITTEN BY DESKTOP");     // what a mid-refresh crash leaves behind
        var svc = new ModelPersistService(new SessionStore());
        svc.RestoreAfterFailedGatedSave(pbix, bak, preWrite, preLen, new Exception("refused"));

        Assert.Equal("v1", File.ReadAllText(pbix));
    }

    [Fact]
    public void GateRefusalHook_DoesNothingWithoutAShield_EvenWhenTheFileMoved()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "v2 - moved");
        var svc = new ModelPersistService(new SessionStore());

        svc.RestoreAfterFailedGatedSave(pbix, presaveBak: null, preWriteUtc: default, preLength: -1, new Exception("refused"));

        Assert.Equal("v2 - moved", File.ReadAllText(pbix));
    }

    [Fact]
    public void GateRefusalHook_EscalatesInsteadOfLying_WhenTheRestoreItselfFails()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "r.pbix", "v1");
        string? bak = ModelPersistService.TakePresaveBackup(pbix);
        // snapshot into locals NOW - FileInfo lazy-loads on first property access
        var fi = new FileInfo(pbix);
        DateTime preWrite = fi.LastWriteTimeUtc;
        long preLen = fi.Length;

        File.WriteAllText(pbix, "PARTIALLY WRITTEN");
        File.Delete(bak!);                                           // the shield is gone - restore cannot succeed
        var refusal = new Exception("the save was refused and the .pbix on disk is unchanged.");

        var svc = new ModelPersistService(new SessionStore());
        var ex = Assert.Throws<MRefreshRequiredException>(() =>
            svc.RestoreAfterFailedGatedSave(pbix, bak, preWrite, preLen, refusal));

        Assert.Contains("CORRECTION", ex.Message);                   // the "unchanged" promise is explicitly corrected
        Assert.Contains("restore it", ex.Message);
        Assert.Same(refusal, ex.InnerException);                     // the original refusal survives for the operator
        Assert.DoesNotContain("Power Query (M)", ex.Message);        // must never re-enter FixModel's soft catch filter
    }

    // ---------------------------------------------------------------- wired through SaveOpenPbix (headless)

    private static (ModelPersistService svc, SessionStore store) NewService()
    {
        var store = new SessionStore();
        return (new ModelPersistService(store), store);
    }

    private static ModelSession NewLocalSession(SessionStore store, string id, string pbixPath, int port = 1)
    {
        var session = new ModelSession
        {
            Id = id,
            Port = port,                                             // port 1 never hosts an msmdsrv - a dispatch fails cleanly
            Server = new TOM.Server(),
            // GUID-unique database name: MDirty is ENGINE-keyed (port + database name, D10), and these
            // helper sessions model INDEPENDENT fake Desktops - a shared name would share one flag across
            // tests exactly the way a real reconnect to the same Desktop now deliberately does.
            Database = new TOM.Database("t-" + Guid.NewGuid().ToString("N")) { Model = new TOM.Model() },
            PbixPath = pbixPath,
        };
        store.AddModel(session);
        return session;
    }

    [Fact]
    public void SaveOpenPbix_TakesTheShieldBeforeTheDispatchCanRun()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "open.pbix", "on-disk state");
        var (svc, store) = NewService();
        NewLocalSession(store, "model-shield-1", pbix);

        var r = svc.SaveOpenPbix("model-shield-1", pbix, saveRetries: 1);

        Assert.False(r.SaveDispatched);                              // nothing owns port 1 (and non-Windows has no Ctrl+S)
        Assert.NotNull(r.Error);
        Assert.Equal("on-disk state", File.ReadAllText(pbix + ".presave.bak"));
    }

    [Fact]
    public void SaveOpenPbix_AGateRefusal_LeavesTheFileAsItWas_WithTheShieldBesideIt()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "open.pbix", "on-disk state");
        var (svc, store) = NewService();
        var session = NewLocalSession(store, "model-shield-2", pbix);
        session.MDirty.Mark("set_partition_m Sales/Partition");      // pending M edit; the detached model cannot refresh

        Assert.Throws<MRefreshRequiredException>(() => svc.SaveOpenPbix("model-shield-2", pbix, saveRetries: 1));

        Assert.Equal("on-disk state", File.ReadAllText(pbix));
        Assert.Equal("on-disk state", File.ReadAllText(pbix + ".presave.bak"));
        Assert.True(session.MDirty.IsDirty);                         // a retry must re-run the refresh
    }

    [Fact]
    public void SaveOpenPbix_AnXmlaSession_TakesNoShield_ThereIsNoLocalFileToProtect()
    {
        using var dir = Fixtures.NewWorkDir();
        string pbix = WritePbix(dir, "remote.pbix", "irrelevant");
        var (svc, store) = NewService();
        NewLocalSession(store, "model-shield-3", pbix, port: 0);

        // D9: a clean XMLA session is a SUCCESS (persistence is server-side), not the old
        // "no local engine port" failure - and still no shield, because there is no local file.
        var r = svc.SaveOpenPbix("model-shield-3", pbix, saveRetries: 1);

        Assert.False(r.SaveDispatched);                              // no window ever receives a keystroke
        Assert.Null(r.Error);
        Assert.Equal("xmla-refresh", r.Persisted);
        Assert.False(r.MRefreshRan);                                 // clean tracker - nothing needed flushing
        Assert.False(File.Exists(pbix + ".presave.bak"));
    }
}
