using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The splash-hang conviction rules and the kill-safety recording seam of <see cref="DesktopSession"/>.
///
/// Conviction (D12): the LOAD-BEARING splash-hang condition is engine absence - no msmdsrv child of the
/// launched pid listening within the deadline. A window title is only ever corroborating evidence over that
/// established failure, and it matches the splash text ("Opening ...") as an EXACT PREFIX, never a substring:
/// a healthy document whose NAME merely contains "Opening" must never be classified as stuck, because the
/// classification kills the Desktop.
///
/// Recording (D11): Launch writes its Desktop + msmdsrv identities into the queue the moment they are proven,
/// through <see cref="DesktopSession.RecordInto"/> - the half of the kill-safety architecture the reaper's
/// structural argument depends on ("only what the queue recorded can ever be a victim").
/// </summary>
public sealed class DesktopSessionTests : IDisposable
{
    private static readonly DateTime LongAgo = new(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc);

    private readonly string _scratch = NewScratch();

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "desktopsession-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    // ---------------- the substring false positive (the confirmed finding) ----------------

    [Theory]
    [InlineData("Store Openings FY27 - Power BI Desktop")]
    [InlineData("Reopening Analysis - Power BI Desktop")]
    [InlineData("Q3 opening hours - Power BI Desktop")]
    [InlineData("OPENING SOON - Power BI Desktop")]
    public void TitleIsSplashOpening_NeverMatchesADocumentNameThatMerelyContainsOpening(string title)
        // Before the fix these matched a case-insensitive Contains("Opening"), so a fully loaded, healthy
        // Desktop burned the whole load deadline and was then killed with a splash-hang verdict - on every
        // retry, forever, for the crime of its filename.
        => Assert.False(DesktopSession.TitleIsSplashOpening(title));

    [Theory]
    [InlineData("Opening")]
    [InlineData("Opening Sales FY27.pbix")]
    [InlineData("Opening Store Openings FY27.pbix")]
    public void TitleIsSplashOpening_MatchesTheSplashTextItself(string title)
        => Assert.True(DesktopSession.TitleIsSplashOpening(title));

    [Fact]
    public void TitleIsSplashOpening_RequiresTheWordBoundary_NotJustTheLetters()
        // "Openings..." is a document name, not the splash text "Opening <file>".
        => Assert.False(DesktopSession.TitleIsSplashOpening("Openings Dashboard - Power BI Desktop"));

    // ---------------- corroboration only refines an established engine-absence failure ----------------

    [Fact]
    public void ClassifyEngineAbsence_WithNoWindow_IsNoWindow()
        => Assert.Equal(SplashHangKind.NoWindow, DesktopSession.ClassifyEngineAbsence(hasWindow: false, ""));

    [Fact]
    public void ClassifyEngineAbsence_WithTheSplashTitle_IsTitleStuckOpening()
        => Assert.Equal(SplashHangKind.TitleStuckOpening,
            DesktopSession.ClassifyEngineAbsence(hasWindow: true, "Opening Sales FY27.pbix"));

    [Fact]
    public void ClassifyEngineAbsence_WithANormalDocumentTitle_IsNoEnginePort()
        // Even here a filename containing "Opening" only picks WHICH verdict of an already-dead launch; it
        // can never create a failure on its own.
        => Assert.Equal(SplashHangKind.NoEnginePort,
            DesktopSession.ClassifyEngineAbsence(hasWindow: true, "Store Openings FY27 - Power BI Desktop"));

    // ---------------- the window watch can no longer convict ----------------

    [Fact]
    public void AwaitWindow_ReturnsOnTheFirstLook_ForAHealthyDocumentWhoseNameContainsOpening()
    {
        // The finding's exact scenario: engine up (this watch only runs after ResolveEnginePort succeeded),
        // window up, title "Store Openings FY27 - Power BI Desktop". Before the fix this spun out the entire
        // 180s deadline and threw SplashHangException, and Launch's catch killed the healthy Desktop.
        int naps = 0;
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);

        DesktopSession.AwaitWindow(
            hasWindow: () => true,
            title: () => "Store Openings FY27 - Power BI Desktop",
            nowUtc: () => now,
            deadlineUtc: now.AddMinutes(3),
            sleep: _ => naps++,
            log: _ => { });

        Assert.Equal(0, naps);   // no deadline burned, no exception, Desktop untouched
    }

    [Fact]
    public void AwaitWindow_AtTheDeadline_LogsAndReturns_InsteadOfThrowingASplashHang()
    {
        // Even a title that IS the splash text cannot convict here: the engine is provably up, and engine
        // absence is the load-bearing splash-hang condition. The old deadline-throw fed a Desktop whose
        // engine was alive to CleanupDesktop; now a stuck-looking window degrades loudly and proceeds - the
        // model wait and the save step carry their own proofs.
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var lines = new List<string>();

        DesktopSession.AwaitWindow(
            hasWindow: () => true,
            title: () => "Opening Sales FY27.pbix",
            nowUtc: () => now,
            deadlineUtc: now.AddMinutes(3),
            sleep: _ => now = now.AddSeconds(30),
            log: lines.Add);

        Assert.NotEmpty(lines);   // degraded loudly, killed nothing
    }

    [Fact]
    public void AwaitWindow_YieldsWithinTheCorroborationCap_SoADocumentNamedOpeningCannotBurnTheLaunchDeadline()
    {
        // The residual half of the splash-title finding: not killing the Desktop was not enough - a title
        // that reads as the splash forever ("Opening Balances - Power BI Desktop") used to hold AwaitWindow
        // until the FULL launch deadline, handing AwaitModel an exhausted budget so the refresh failed with
        // "model never attached" before a single probe. The corroboration cap bounds that wait: the engine
        // is already proven up, so a persistently splash-looking title yields within the cap.
        var start = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        var now = start;
        var lines = new List<string>();

        DesktopSession.AwaitWindow(
            hasWindow: () => true,
            title: () => "Opening Balances - Power BI Desktop",
            nowUtc: () => now,
            deadlineUtc: start.AddMinutes(3),
            sleep: _ => now = now.AddSeconds(1),
            log: lines.Add);

        Assert.True(now - start <= DesktopSession.WindowCorroborationCap + TimeSpan.FromSeconds(1),
            $"burned {(now - start).TotalSeconds}s of a 180s deadline on title corroboration alone");
        Assert.NotEmpty(lines);
    }

    [Fact]
    public void EffectiveModelDeadline_FloorsAnExhaustedBudget_AndLeavesAHealthyOneAlone()
    {
        // AwaitModel must always get real probes: an upstream wait that consumed the shared launch deadline
        // used to make it throw "model never attached" before its first look at the engine.
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);

        var floored = DesktopSession.EffectiveModelDeadline(now.AddSeconds(-1), () => now);
        Assert.Equal(now.AddSeconds(60), floored);

        var healthy = DesktopSession.EffectiveModelDeadline(now.AddMinutes(3), () => now);
        Assert.Equal(now.AddMinutes(3), healthy);
    }

    [Fact]
    public void AwaitWindow_KeepsWaitingWhileThereIsNoWindow_ThenReturnsWhenOneAppears()
    {
        var now = new DateTime(2026, 7, 17, 0, 0, 0, DateTimeKind.Utc);
        int looks = 0;

        DesktopSession.AwaitWindow(
            hasWindow: () => ++looks >= 3,
            title: () => "Sales FY27 - Power BI Desktop",
            nowUtc: () => now,
            deadlineUtc: now.AddMinutes(3),
            sleep: _ => now = now.AddSeconds(1),
            log: _ => { });

        Assert.Equal(3, looks);
    }

    // ---------------- the recording seam (the unwired half of the kill-safety architecture) ----------------

    [Fact]
    public void RecordInto_WritesTheLaunchedIdentitiesIntoTheQueue_WhereTheReaperCanSeeThem()
    {
        // The confirmed finding: no production code ever called JobQueue.SetDesktop, so RecordedProcesses()
        // was always empty and the reaper permanently inert. Launch now records through this seam the moment
        // both identities (pid + start time) are proven.
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        string jobId = JobId.New();
        q.Enqueue(new JobSubmission(jobId, "acme", Lane.Heavy, "build_report", 4096, 1, "C:\\daxops\\jobs\\_queue", null));
        Assert.True(q.TryTransition(jobId, JobState.QUEUED, JobState.RUNNING, 1));

        DesktopSession.RecordInto(q, jobId, 4242, LongAgo, 4243, LongAgo, 55001);

        var row = q.Get(jobId)!;
        Assert.Equal(4242, row.DesktopPid);
        Assert.Equal(4243, row.MsmdsrvPid);
        Assert.Equal(55001, row.MsmdsrvPort);

        // ...and once the job dies, the recorded processes are exactly what the reaper plans over.
        Assert.True(q.TryTransition(jobId, JobState.RUNNING, JobState.DEAD, 1));
        Assert.Equal(2, q.RecordedProcesses().Count);
    }

    [Fact]
    public void RecordInto_EngagesTheTwoJobCollisionDefence_SoASecondJobCannotClaimTheSameDesktop()
    {
        // The ux_jobs_desktop_live / ux_jobs_port_live indexes never engaged in production because nothing
        // wrote the columns. Through the seam, a second live job claiming the same pid/port is refused one
        // layer below the code that was about to Ctrl+S into another job's Desktop.
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        string a = JobId.New(), b = JobId.New();
        q.Enqueue(new JobSubmission(a, "acme", Lane.Heavy, "build_report", 4096, 1, "C:\\daxops\\jobs\\_queue", null));
        q.Enqueue(new JobSubmission(b, "acme", Lane.Heavy, "build_report", 4096, 1, "C:\\daxops\\jobs\\_queue", null));
        Assert.True(q.TryTransition(a, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(b, JobState.QUEUED, JobState.RUNNING, 1));
        DesktopSession.RecordInto(q, a, 4242, LongAgo, 4243, LongAgo, 55001);

        Assert.Throws<InvalidOperationException>(
            () => DesktopSession.RecordInto(q, b, 4242, LongAgo, 4243, LongAgo, 55001));
    }

    [Fact]
    public void RecordInto_WithoutAQueueOrJobId_IsANoOp_SoTheCliRefreshPathStillLaunches()
    {
        // BulkOps' CLI refresh has no queue context; the seam must cost it nothing.
        DesktopSession.RecordInto(null, null, 4242, LongAgo, 4243, LongAgo, 55001);
        DesktopSession.RecordInto(null, "job-without-queue", 4242, LongAgo, 4243, LongAgo, 55001);

        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        DesktopSession.RecordInto(q, null, 4242, LongAgo, 4243, LongAgo, 55001);
        Assert.Empty(q.RecordedProcesses());
    }
}
