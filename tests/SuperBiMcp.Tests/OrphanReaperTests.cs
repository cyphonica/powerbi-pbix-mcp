using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The reaper's safety argument, which is STRUCTURAL rather than a filter: its only input is what the queue
/// itself recorded, so a process the queue never recorded cannot be named, planned or killed. An operator's own
/// interactive Power BI Desktop is invisible here by construction, and these tests are what holds that - the
/// day someone adds a process-table scan for candidates, the unrecorded-stranger claims below fail.
///
/// Three further guards narrow what remains: a job in a live state is never touched, a pid whose live start
/// time does not match the recorded one has been RECYCLED onto a stranger, and a pid younger than the age floor
/// is never touched. Both the clock and the start-time lookup are injected, so nothing here needs a live host.
/// </summary>
public sealed class OrphanReaperTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 3, 0, 0, TimeSpan.Zero);
    private static readonly DateTime LongAgo = new(2026, 7, 16, 1, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MinAge = TimeSpan.FromSeconds(120);

    private readonly string _scratch = NewScratch();

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "reaper-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private static RecordedProc Proc(JobState state, int pid, DateTime? started = null, string name = "PBIDesktop") =>
        new(JobId.New(), state, pid, started ?? LongAgo, name);

    /// <summary>A box where a kill actually works: a killed pid stops resolving a start time, which is exactly
    /// the ABSENCE the reaper's post-kill verification must observe before anything counts as killed.</summary>
    private sealed class FakeProcs
    {
        private readonly HashSet<int> _dead = new();
        public List<int> Killed { get; } = new();
        public void Kill(int pid) { Killed.Add(pid); _dead.Add(pid); }
        public DateTime? StartTime(int pid) => _dead.Contains(pid) ? null : LongAgo;
    }

    // ---------------- the structural guard: only what the queue recorded ----------------

    [Fact]
    public void Plan_OverAnEmptyRecordedSet_NamesNobody_SoAnUnrecordedProcessCanNeverBeAVictim()
    {
        // The operator runs Power BI Desktop interactively on this box. The queue never recorded it, so it is
        // not in `recorded`, so it cannot be planned. There is no filter to get wrong.
        Assert.Empty(OrphanReaper.Plan(Array.Empty<RecordedProc>(), _ => LongAgo, Now, MinAge));
    }

    [Fact]
    public void Reap_NeverKillsAPidTheQueueDidNotRecord()
    {
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        const int strangerPid = 9999;         // the operator's own interactive Desktop, running right now
        Seed(q, JobState.DEAD, desktopPid: 4242, msmdsrvPid: 4243, port: 55001);

        var box = new FakeProcs();            // every unkilled pid on this box looks alive and old enough
        int count = OrphanReaper.Reap(q, _ => { }, Now,
            startTimeUtc: box.StartTime,
            killTree: box.Kill,
            minAge: MinAge, sleep: _ => { });

        // The stranger is old, alive and a Power BI Desktop - it fits every heuristic a reaper might use. It
        // survives because the queue never recorded it, and that is the only reason it needs.
        Assert.Equal(2, count);
        Assert.Equal(new[] { 4242, 4243 }, box.Killed.OrderBy(p => p).ToArray());
        Assert.DoesNotContain(strangerPid, box.Killed);
    }

    [Fact]
    public void Reap_OnAQueueItCannotRead_KillsNothing()
    {
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        Seed(q, JobState.DEAD, 4242, 4243, 55001);
        q.Dispose();                          // the read will throw

        var killed = new List<int>();
        // Reaping on a guess is how a stranger dies. No evidence, no victims - and no throw either: a box that
        // cannot be tidied must still take work.
        Assert.Equal(0, OrphanReaper.Reap(q, _ => { }, Now, _ => LongAgo, killed.Add, MinAge));
        Assert.Empty(killed);
    }

    // ---------------- the live-job guard ----------------

    // The rows carry state NAMES: a public test method cannot expose the internal JobState in its signature.

    [Theory]
    [InlineData("ADMITTED")]
    [InlineData("RUNNING")]
    [InlineData("VERIFYING")]
    public void Plan_NeverNamesTheDesktopOfAJobThatIsStillLive(string liveName)
    {
        // Its job still owns it. Killing it would take down a paying job mid-build.
        Assert.Empty(OrphanReaper.Plan(new[] { Proc(Enum.Parse<JobState>(liveName), 4242) }, _ => LongAgo, Now, MinAge));
    }

    [Theory]
    [InlineData("DONE")]
    [InlineData("FAILED")]
    [InlineData("DEAD")]
    public void Plan_NamesTheDesktopOfAJobThatIsNoLongerLive(string finishedName)
    {
        JobState finished = Enum.Parse<JobState>(finishedName);
        var victims = OrphanReaper.Plan(new[] { Proc(finished, 4242) }, _ => LongAgo, Now, MinAge);

        var victim = Assert.Single(victims);
        Assert.Equal(4242, victim.Pid);
        Assert.Equal($"orphan:{finished}", victim.Reason);
    }

    // ---------------- the pid-reuse guard ----------------

    [Fact]
    public void Plan_NamesADeadJobsDesktop_WhenTheLiveStartTimeMatchesTheRecordedOne()
    {
        var victims = OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242, LongAgo) }, _ => LongAgo, Now, MinAge);

        Assert.Equal(4242, Assert.Single(victims).Pid);
    }

    [Fact]
    public void Plan_RefusesAPidWhoseLiveStartTimeDiffers_BecauseWindowsRecycledItOntoAStranger()
    {
        // Pid 4242 died; Windows handed the number to something else two minutes ago. Pid + start time is the
        // identity, and a pid alone is not identity on Windows.
        var recycledOnto = LongAgo.AddMinutes(58);
        Assert.Empty(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242, LongAgo) }, _ => recycledOnto, Now, MinAge));
    }

    [Fact]
    public void Plan_ToleratesTheSubSecondDriftOfAStartTimeStoredAsText()
    {
        // The recorded start makes a round trip through ISO-8601 text; a fraction of a second of drift is
        // storage, not a different process.
        var drifted = LongAgo.AddMilliseconds(400);
        Assert.Single(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242, LongAgo) }, _ => drifted, Now, MinAge));
    }

    [Fact]
    public void Plan_DrawsItsToleranceWhereDesktopInteropDoes_SoTheTwoIdentityChecksAgree()
    {
        // DesktopInterop.PidAlive(pid, expectedStart) reads a drift under one second as "same process" and one
        // of a second or more as a stranger. The reaper must draw the line at exactly the same place: a pid the
        // launcher would still treat as its own must never be a stranger to the reaper, and vice versa.
        var justInside = LongAgo.AddMilliseconds(999);
        var atTheLine = LongAgo.AddSeconds(1);

        Assert.Single(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242, LongAgo) }, _ => justInside, Now, MinAge));
        Assert.Empty(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242, LongAgo) }, _ => atTheLine, Now, MinAge));

        Assert.True(DesktopInterop.PidAlive(Environment.ProcessId,
            DesktopInterop.PidStartTimeUtc(Environment.ProcessId)!.Value.AddMilliseconds(999)));
        Assert.False(DesktopInterop.PidAlive(Environment.ProcessId,
            DesktopInterop.PidStartTimeUtc(Environment.ProcessId)!.Value.AddSeconds(1)));
    }

    [Fact]
    public void Plan_RefusesAPidThatIsAlreadyGone()
    {
        Assert.Empty(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242) }, _ => null, Now, MinAge));
    }

    [Fact]
    public void Plan_RefusesAPidWhoseStartTimeCannotBeRead()
    {
        // An unidentified process is never a target: without a start time the pid is not identity.
        Assert.Empty(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242) },
            _ => throw new UnauthorizedAccessException("access denied"), Now, MinAge));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Plan_RefusesANonsensePid(int pid)
    {
        Assert.Empty(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, pid) }, _ => LongAgo, Now, MinAge));
    }

    // ---------------- the age guard ----------------

    [Fact]
    public void Plan_RefusesAPidYoungerThanTheAgeFloor()
    {
        // A Desktop that started 30 seconds ago is one another job may be about to record. The floor keeps the
        // reaper off the launch window.
        var justStarted = Now.UtcDateTime.AddSeconds(-30);
        Assert.Empty(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242, justStarted) }, _ => justStarted, Now, MinAge));
    }

    [Fact]
    public void Plan_NamesAPidOlderThanTheAgeFloor()
    {
        var old = Now.UtcDateTime.AddSeconds(-121);
        Assert.Single(OrphanReaper.Plan(new[] { Proc(JobState.DEAD, 4242, old) }, _ => old, Now, MinAge));
    }

    // ---------------- reap ----------------

    [Fact]
    public void Reap_ReleasesTheRowOnceItsProcessesAreVerifiablyGone()
    {
        // Updated with the verified-kill fix: the release now requires the post-kill verification to OBSERVE
        // each pid gone (the fake box makes killed pids stop resolving), not merely the kill call returning.
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        string jobId = Seed(q, JobState.DEAD, 4242, msmdsrvPid: 4243, port: 55001);

        var box = new FakeProcs();
        int killed = OrphanReaper.Reap(q, _ => { }, Now, box.StartTime, box.Kill, MinAge, sleep: _ => { });

        Assert.Equal(2, killed);                       // the Desktop and its engine
        Assert.Null(q.Get(jobId)!.DesktopPid);
        Assert.Null(q.Get(jobId)!.MsmdsrvPid);
        Assert.Empty(q.RecordedProcesses());           // and it is not planned again next sweep
    }

    [Fact]
    public void Reap_KeepsTheRow_WhenAProcessSurvivedTheKill()
    {
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        string jobId = Seed(q, JobState.DEAD, 4242, msmdsrvPid: 4243, port: 55001);

        // ClearDesktop releases BOTH recorded pids at once, so a job with a survivor must keep its row:
        // clearing it would erase the only record of a process still running, putting it beyond every later
        // reap and leaking a Desktop forever.
        var box = new FakeProcs();
        int killed = OrphanReaper.Reap(q, _ => { }, Now, box.StartTime,
            pid => { if (pid == 4243) throw new InvalidOperationException("access denied"); box.Kill(pid); },
            MinAge, sleep: _ => { });

        Assert.Equal(1, killed);
        Assert.Equal(4242, q.Get(jobId)!.DesktopPid);
        Assert.Equal(4243, q.Get(jobId)!.MsmdsrvPid);
    }

    // ---------------- kill verification: gone or it never happened ----------------

    [Fact]
    public void Reap_CountsASwallowedKillFailureAsASurvivor_AndKeepsTheRecordForTheNextSweep()
    {
        // The confirmed finding this pins: the production kill delegate (DesktopInterop.KillTree) swallows
        // EVERY exception - an unelevated `jobs reap` against a service-launched Desktop gets access-denied
        // and returns normally. Before the fix that counted as killed, the "reap: killed" line was logged,
        // and ClearDesktop erased the only record of the still-running 3-4GB Desktop, putting it beyond every
        // future sweep. Now: nothing counts as killed until the process is OBSERVED gone.
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        string jobId = Seed(q, JobState.DEAD, 4242, msmdsrvPid: 4243, port: 55001);

        var lines = new List<string>();
        int killed = OrphanReaper.Reap(q, lines.Add, Now,
            startTimeUtc: _ => LongAgo,   // both processes still alive with their recorded start times
            killTree: _ => { },           // the swallow: returns normally, kills nothing
            minAge: MinAge, sleep: _ => { });

        Assert.Equal(0, killed);
        Assert.Equal(4242, q.Get(jobId)!.DesktopPid);       // the record survives...
        Assert.Equal(4243, q.Get(jobId)!.MsmdsrvPid);
        Assert.Equal(2, q.RecordedProcesses().Count);       // ...so the next sweep can still see the orphan
        Assert.Contains(lines, l => l.Contains("survived"));
        Assert.DoesNotContain(lines, l => l.Contains("reap: killed"));
    }

    [Fact]
    public void Reap_WaitsOutASlowDeath_RatherThanDeclaringASurvivorOnTheFirstLook()
    {
        // Kill signals termination; a multi-GB Desktop tree takes a moment to actually exit. The verification
        // polls instead of reading the first still-alive look as failure.
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        Seed(q, JobState.DEAD, 4242, msmdsrvPid: 4243, port: 55001);

        var killedAt = new HashSet<int>();
        var looks = new Dictionary<int, int>();
        DateTime? Start(int pid)
        {
            if (!killedAt.Contains(pid)) return LongAgo;
            int n = looks.GetValueOrDefault(pid);
            looks[pid] = n + 1;
            return n < 2 ? LongAgo : null;   // still exiting for two looks, observed gone on the third
        }

        int naps = 0;
        int killed = OrphanReaper.Reap(q, _ => { }, Now, Start, pid => killedAt.Add(pid), MinAge,
            sleep: _ => naps++);

        Assert.Equal(2, killed);
        Assert.True(naps >= 2);              // it waited rather than convicting instantly
        Assert.Empty(q.RecordedProcesses());
    }

    [Fact]
    public void Reap_ReadsAPidRecycledAfterTheKill_AsGone()
    {
        // Post-kill the number resolves to a DIFFERENT start time: Windows handed it to a stranger, so the
        // recorded process is dead and the record may be released. The stranger itself was never a target.
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        Seed(q, JobState.DEAD, 4242, msmdsrvPid: 4243, port: 55001);

        var killedPids = new HashSet<int>();
        int killed = OrphanReaper.Reap(q, _ => { }, Now,
            startTimeUtc: pid => killedPids.Contains(pid) ? LongAgo.AddMinutes(90) : LongAgo,
            killTree: pid => killedPids.Add(pid),
            minAge: MinAge, sleep: _ => { });

        Assert.Equal(2, killed);
        Assert.Empty(q.RecordedProcesses());
    }

    [Fact]
    public void Reap_ReadsAnUnreadableVerificationProbe_AsASurvivor()
    {
        // A probe that throws after the kill has proven nothing. Keeping the record is the recoverable
        // mistake; clearing it would put a possibly-live orphan beyond every later reap.
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        string jobId = Seed(q, JobState.DEAD, 4242, msmdsrvPid: 4243, port: 55001);

        var killedPids = new HashSet<int>();
        int killed = OrphanReaper.Reap(q, _ => { }, Now,
            startTimeUtc: pid => killedPids.Contains(pid)
                ? throw new UnauthorizedAccessException("access denied")
                : (DateTime?)LongAgo,
            killTree: pid => killedPids.Add(pid),
            minAge: MinAge, sleep: _ => { });

        Assert.Equal(0, killed);
        Assert.Equal(4242, q.Get(jobId)!.DesktopPid);
        Assert.Equal(4243, q.Get(jobId)!.MsmdsrvPid);
    }

    [Fact]
    public void Reap_OverAQueueWithNothingToReap_KillsNothing()
    {
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        Seed(q, JobState.RUNNING, 4242, 4243, 55001);   // live: not an orphan

        var killed = new List<int>();
        Assert.Equal(0, OrphanReaper.Reap(q, _ => { }, Now, _ => LongAgo, killed.Add, MinAge));
        Assert.Empty(killed);
    }

    [Fact]
    public void RecordedProcesses_ExposesOnlyTheProcessesOfJobsThatAreNoLongerLive()
    {
        using var q = JobQueue.Open(Path.Combine(_scratch, "queue.db"));
        Seed(q, JobState.RUNNING, 1111, msmdsrvPid: 1112, port: 55001);
        Seed(q, JobState.DEAD, 2222, msmdsrvPid: 2223, port: 55002);

        var recorded = q.RecordedProcesses();

        Assert.Equal(new[] { 2222, 2223 }, recorded.Select(r => r.Pid).OrderBy(p => p).ToArray());
    }

    /// <summary>Seeds one job that reached <paramref name="state"/> having recorded its processes, the way a
    /// real run leaves the row behind. The port is per-seed: two live rows may not claim one port.</summary>
    private static string Seed(JobQueue q, JobState state, int desktopPid, int msmdsrvPid, int port)
    {
        string jobId = JobId.New();
        q.Enqueue(new JobSubmission(jobId, "acme", Lane.Heavy, "build_report", 4096, 1, "C:\\daxops\\jobs\\_queue", null));
        Assert.True(q.TryTransition(jobId, JobState.QUEUED, JobState.RUNNING, 1));
        q.SetDesktop(jobId, desktopPid, LongAgo, msmdsrvPid, LongAgo, port);

        if (state != JobState.RUNNING) Assert.True(q.TryTransition(jobId, JobState.RUNNING, state, 1));
        return jobId;
    }
}
