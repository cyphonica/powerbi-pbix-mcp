using Microsoft.Data.Sqlite;
using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Real SQLite on scratch, over <see cref="JobQueue"/> - the durable serializer behind lane capacity, job state
/// and Desktop ownership. Every claim here is one the queue must hold across TWO PROCESSES, so the concurrency
/// tests race two separate <see cref="JobQueue"/> instances (two connections to one file) rather than two
/// threads on one instance: an instance-level lock would pass a test that a second `serve` host would fail.
///
/// The db path is supplied explicitly, so nothing here touches the process-global JobPaths.RootForTest and the
/// class needs no serialisation collection.
/// </summary>
public sealed class JobQueueTests : IDisposable
{
    private readonly string _scratch = NewScratch();

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "jobqueue-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private string DbPath => Path.Combine(_scratch, "queue.db");

    private JobQueue Open() => JobQueue.Open(DbPath);

    private static JobSubmission Sub(string jobId, Lane lane = Lane.Heavy, string tenant = "acme") =>
        new(jobId, tenant, lane, "build_report", ReserveMB: 4096, MaxAttempts: 1, JobRoot: "C:\\daxops\\jobs\\_queue", SourcePath: null);

    // ---------------- submission / idempotency ----------------

    [Fact]
    public void Enqueue_OfTheSameJobIdTwice_ReturnsTheSameRowAndNeverMutatesIt()
    {
        using var q = Open();
        string id = JobId.New();

        JobRow first = q.Enqueue(Sub(id));
        // A resubmission carries a different lane, route and reserve. None of it may land: the jobId is the
        // idempotency key, so re-POSTing a job must return what it already is, never re-shape it.
        JobRow second = q.Enqueue(new JobSubmission(id, "someone-else", Lane.Cheap, "other_route", 1, 9, "X:\\other", "X:\\src.pbix"));

        Assert.Equal(first.TenantId, second.TenantId);
        Assert.Equal(first.Lane, second.Lane);
        Assert.Equal(first.Route, second.Route);
        Assert.Equal(first.ReserveMB, second.ReserveMB);
        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.Equal(JobState.QUEUED, second.State);
        Assert.Single(q.List(null, null, 100));
    }

    [Fact]
    public void Enqueue_OfADoneJob_DoesNotReviveIt_SoPaidWorkIsNeverReRun()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.ADMITTED, 1));
        Assert.True(q.TryTransition(id, JobState.ADMITTED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(id, JobState.RUNNING, JobState.DONE, 1));

        JobRow replayed = q.Enqueue(Sub(id));

        Assert.Equal(JobState.DONE, replayed.State);   // a DONE row stays DONE: the caller gets the artifact, not a re-run
    }

    // ---------------- the atomic transition ----------------

    [Fact]
    public void TryTransition_RefusesWhenTheRowIsNotInTheDeclaredFromState()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));

        // The verdict is the CAS on `from` alone: the row is QUEUED, so a caller that believed it was RUNNING
        // has lost a race it must not proceed past, and the row it misread is left exactly as it was.
        Assert.False(q.TryTransition(id, JobState.RUNNING, JobState.DONE, 1));
        Assert.False(q.TryTransition(id, JobState.VERIFYING, JobState.DONE, 1));
        Assert.Equal(JobState.QUEUED, q.Get(id)!.State);
    }

    [Fact]
    public void TryTransition_IsACompareAndSwap_SoOnlyTheFirstIdenticalCallWins()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));

        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.ADMITTED, 1));
        Assert.False(q.TryTransition(id, JobState.QUEUED, JobState.ADMITTED, 1));   // the row already moved
        Assert.Equal(JobState.ADMITTED, q.Get(id)!.State);
    }

    [Fact]
    public void TryTransition_OfAnUnknownJob_IsFalseRatherThanAThrow()
    {
        using var q = Open();
        Assert.False(q.TryTransition(JobId.New(), JobState.QUEUED, JobState.ADMITTED, 1));
    }

    [Fact]
    public void TryTransition_RacedByTwoConnections_YieldsExactlyOneWinner()
    {
        string id = JobId.New();
        using (var seed = Open()) seed.Enqueue(Sub(id));

        using var a = Open();
        using var b = Open();
        int winners = RaceCount(
            () => a.TryTransition(id, JobState.QUEUED, JobState.ADMITTED, 101),
            () => b.TryTransition(id, JobState.QUEUED, JobState.ADMITTED, 202));

        // Both callers would go on to launch a Desktop. Exactly one may.
        Assert.Equal(1, winners);
        Assert.Equal(JobState.ADMITTED, a.Get(id)!.State);
    }

    // ---------------- the atomic admission ----------------

    [Fact]
    public void TryAdmit_IntoAHeavyLaneOfOne_AdmitsTheHeadAndRefusesTheNext()
    {
        using var q = Open();
        string first = JobId.New();
        string second = JobId.New();
        q.Enqueue(Sub(first));
        q.Enqueue(Sub(second));

        Assert.True(q.TryAdmit(first, laneLimit: 1, ownerPid: 1, _ => true));
        Assert.False(q.TryAdmit(second, laneLimit: 1, ownerPid: 1, _ => true));

        Assert.Equal(1, q.ActiveCount(Lane.Heavy));
        Assert.Equal(JobState.QUEUED, q.Get(second)!.State);
    }

    [Fact]
    public void TryAdmit_RacedByTwoConnectionsOnOneJob_YieldsExactlyOneWinner()
    {
        string id = JobId.New();
        using (var seed = Open()) seed.Enqueue(Sub(id));

        using var a = Open();
        using var b = Open();
        int winners = RaceCount(
            () => a.TryAdmit(id, laneLimit: 1, ownerPid: 101, _ => true),
            () => b.TryAdmit(id, laneLimit: 1, ownerPid: 202, _ => true));

        Assert.Equal(1, winners);
        Assert.Equal(1, a.ActiveCount(Lane.Heavy));
    }

    [Fact]
    public void TryAdmit_RacedByTwoConnectionsOnTwoJobs_LetsExactlyOneIntoALaneOfOne()
    {
        string first = JobId.New();
        string second = JobId.New();
        using (var seed = Open())
        {
            seed.Enqueue(Sub(first));
            seed.Enqueue(Sub(second));
        }

        using var a = Open();
        using var b = Open();
        // The real collision: two hosts each deciding for a DIFFERENT job, both counting a lane with room.
        // Nothing serialises them but the queue, and two Desktops on this box is the failure being excluded.
        int winners = RaceCount(
            () => a.TryAdmit(first, laneLimit: 1, ownerPid: 101, _ => true),
            () => b.TryAdmit(second, laneLimit: 1, ownerPid: 202, _ => true));

        Assert.Equal(1, winners);
        Assert.Equal(1, a.ActiveCount(Lane.Heavy));
    }

    [Fact]
    public void TryAdmit_RefusesWhenTheHostFloorsSayNo_AndLeavesTheRowQueued()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));

        Assert.False(q.TryAdmit(id, laneLimit: 1, ownerPid: 1, hostAllows: _ => false));

        // A held job is a job that waits, never a job that is lost: the next sweep must find it queued.
        Assert.Equal(JobState.QUEUED, q.Get(id)!.State);
        Assert.Equal(0, q.ActiveCount(Lane.Heavy));
    }

    [Fact]
    public void TryAdmit_RefusesOutOfOrder_SoALaneIsFair()
    {
        using var q = Open();
        string head = JobId.New();
        string behind = JobId.New();
        q.Enqueue(Sub(head));
        q.Enqueue(Sub(behind));

        // A lane of 2 has room for both, but the queue is FIFO: the one behind may not jump.
        Assert.False(q.TryAdmit(behind, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(head, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(behind, laneLimit: 2, ownerPid: 1, _ => true));
    }

    [Fact]
    public void TryAdmit_RefusesALimitOfZero_RatherThanTreatingItAsUnbounded()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));

        Assert.False(q.TryAdmit(id, laneLimit: 0, ownerPid: 1, _ => true));
        Assert.Equal(JobState.QUEUED, q.Get(id)!.State);
    }

    [Fact]
    public void TryAdmit_CountsTheLanesItOwns_AndNotTheOther()
    {
        using var q = Open();
        string heavy = JobId.New();
        string cheap = JobId.New();
        q.Enqueue(Sub(heavy, Lane.Heavy));
        q.Enqueue(Sub(cheap, Lane.Cheap));

        Assert.True(q.TryAdmit(heavy, laneLimit: 1, ownerPid: 1, _ => true));
        // A full heavy lane must not close the cheap lane: they are separately budgeted.
        Assert.True(q.TryAdmit(cheap, laneLimit: 1, ownerPid: 1, _ => true));
        Assert.Equal(1, q.ActiveCount(Lane.Heavy));
        Assert.Equal(1, q.ActiveCount(Lane.Cheap));
    }

    // ---------------- reads ----------------

    [Fact]
    public void ActiveCount_CountsEveryStateThatHoldsLaneCapacity()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Assert.Equal(0, q.ActiveCount(Lane.Heavy));      // QUEUED holds nothing

        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.ADMITTED, 1));
        Assert.Equal(1, q.ActiveCount(Lane.Heavy));
        Assert.True(q.TryTransition(id, JobState.ADMITTED, JobState.RUNNING, 1));
        Assert.Equal(1, q.ActiveCount(Lane.Heavy));
        Assert.True(q.TryTransition(id, JobState.RUNNING, JobState.VERIFYING, 1));
        Assert.Equal(1, q.ActiveCount(Lane.Heavy));      // a job still verifying still owns its Desktop

        Assert.True(q.TryTransition(id, JobState.VERIFYING, JobState.DONE, 1));
        Assert.Equal(0, q.ActiveCount(Lane.Heavy));
    }

    [Fact]
    public void QueuePosition_IsOneBasedWithinTheLane_AndZeroOnceTheJobIsAdmitted()
    {
        using var q = Open();
        string first = JobId.New();
        string second = JobId.New();
        string third = JobId.New();
        q.Enqueue(Sub(first));
        q.Enqueue(Sub(second));
        q.Enqueue(Sub(third));

        Assert.Equal(1, q.QueuePosition(first));
        Assert.Equal(2, q.QueuePosition(second));
        Assert.Equal(3, q.QueuePosition(third));

        Assert.True(q.TryAdmit(first, laneLimit: 1, ownerPid: 1, _ => true));
        Assert.Equal(0, q.QueuePosition(first));         // admitted: it has no position left to report
        Assert.Equal(1, q.QueuePosition(second));        // and everyone behind moves up
        Assert.Equal(0, q.QueuePosition(JobId.New()));   // an unknown job has none either
    }

    // ---------------- Desktop ownership ----------------

    [Fact]
    public void SetDesktop_RefusesASecondLiveJobClaimingTheSameDesktopPid()
    {
        using var q = Open();
        string a = JobId.New();
        string b = JobId.New();
        q.Enqueue(Sub(a));
        q.Enqueue(Sub(b));
        Assert.True(q.TryTransition(a, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(b, JobState.QUEUED, JobState.RUNNING, 1));

        var start = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
        q.SetDesktop(a, desktopPid: 4242, start, msmdsrvPid: 4243, start, msmdsrvPort: 55001);

        // The collision caught one layer below the code that was about to Ctrl+S into another job's Desktop.
        Assert.Throws<InvalidOperationException>(() =>
            q.SetDesktop(b, desktopPid: 4242, start, msmdsrvPid: 9999, start, msmdsrvPort: 55002));
    }

    [Fact]
    public void SetDesktop_RefusesASecondLiveJobClaimingTheSamePort()
    {
        using var q = Open();
        string a = JobId.New();
        string b = JobId.New();
        q.Enqueue(Sub(a));
        q.Enqueue(Sub(b));
        Assert.True(q.TryTransition(a, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(b, JobState.QUEUED, JobState.RUNNING, 1));

        var start = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
        q.SetDesktop(a, 4242, start, 4243, start, msmdsrvPort: 55001);

        Assert.Throws<InvalidOperationException>(() => q.SetDesktop(b, 5252, start, 5253, start, msmdsrvPort: 55001));
    }

    [Fact]
    public void ClearDesktop_ReleasesThePort_SoTheNextJobMayClaimIt()
    {
        using var q = Open();
        string a = JobId.New();
        string b = JobId.New();
        q.Enqueue(Sub(a));
        q.Enqueue(Sub(b));
        Assert.True(q.TryTransition(a, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(b, JobState.QUEUED, JobState.RUNNING, 1));

        var start = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
        q.SetDesktop(a, 4242, start, 4243, start, 55001);
        q.ClearDesktop(a);

        q.SetDesktop(b, 4242, start, 4243, start, 55001);   // must not throw
        Assert.Equal(4242, q.Get(b)!.DesktopPid);
        Assert.Null(q.Get(a)!.DesktopPid);
    }

    // ---------------- maintenance ----------------

    [Fact]
    public void Reconcile_KillsOrphanedInFlightRowsAtStartup_AndLeavesAdoptedQueuedWorkAlone()
    {
        using var q = Open();
        string running = JobId.New();
        string queued = JobId.New();
        var start = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
        q.Enqueue(Sub(running));
        q.Enqueue(Sub(queued));
        Assert.True(q.TryTransition(running, JobState.QUEUED, JobState.RUNNING, ownerPid: 31337, ownerStartUtc: start));

        // A host that crashed left this row RUNNING. Its owner is gone, so nothing is driving it. The queued
        // row was enqueued by THIS process (our incarnation), so a reconcile passing our incarnation must
        // treat it as adopted and spare it.
        int killed = q.Reconcile(myPid: 1, ownerAlive: (_, _) => false, now: DateTimeOffset.UtcNow,
                                 myIncarnation: JobQueue.ProcessIncarnation);

        Assert.Equal(1, killed);
        Assert.Equal(JobState.DEAD, q.Get(running)!.State);
        Assert.Contains("31337", q.Get(running)!.Error);
        Assert.Equal(JobState.QUEUED, q.Get(queued)!.State);   // our own queued work survives
        Assert.Equal(0, q.ActiveCount(Lane.Heavy));            // and the lane it was holding is released
    }

    [Fact]
    public void Reconcile_SparesARowWhoseOwnerIsStillAlive_ByPidPlusStartTimeIdentity()
    {
        using var q = Open();
        string id = JobId.New();
        var start = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, ownerPid: 4242, ownerStartUtc: start));

        // The identity check gets BOTH halves: the pid and the start time recorded at the claim.
        Assert.Equal(0, q.Reconcile(myPid: 1, ownerAlive: (pid, s) => pid == 4242 && s == start,
                                    now: DateTimeOffset.UtcNow));
        Assert.Equal(JobState.RUNNING, q.Get(id)!.State);
    }

    [Fact]
    public void Reconcile_KillsARowClaimingOurOwnPid_BecauseWeHaveOnlyJustStarted()
    {
        using var q = Open();
        string id = JobId.New();
        var start = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, ownerPid: 777, ownerStartUtc: start));

        // The row says pid 777 owns it and we ARE 777 - but we have only just started, so it cannot be ours.
        // It is a dead predecessor whose pid Windows recycled onto us.
        Assert.Equal(1, q.Reconcile(myPid: 777, ownerAlive: (_, _) => true, now: DateTimeOffset.UtcNow));
        Assert.Equal(JobState.DEAD, q.Get(id)!.State);
    }

    [Fact]
    public void ReapStuck_KillsARowThatStoppedHeartbeating()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, 1));

        Assert.Equal(0, q.ReapStuck(TimeSpan.FromMinutes(45), DateTimeOffset.UtcNow));

        int killed = q.ReapStuck(TimeSpan.FromMinutes(45), DateTimeOffset.UtcNow.AddHours(2));

        Assert.Equal(1, killed);
        Assert.Equal(JobState.DEAD, q.Get(id)!.State);
    }

    [Fact]
    public void ReapStuck_ReapsARowThatNeverHeartbeatAtAll_ViaTheCreationTimeFallback()
    {
        string id = JobId.New();
        using (var q = Open())
        {
            q.Enqueue(Sub(id));
            Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, 1));
        }

        // A row can reach an in-flight state carrying NO heartbeat at all - a schema predecessor, a hand edit,
        // a host that died between the claim and the first beat. NULL compares as NULL in SQL, so without the
        // COALESCE fallback to admission/creation time this row would be immortal and hold its lane forever.
        using (var raw = new SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var strip = raw.CreateCommand();
            strip.CommandText = "UPDATE jobs SET heartbeat_utc = NULL WHERE job_id = $id;";
            strip.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, strip.ExecuteNonQuery());
        }

        using (var q = Open())
        {
            Assert.Equal(1, q.ReapStuck(TimeSpan.FromMinutes(45), DateTimeOffset.UtcNow.AddHours(2)));
            Assert.Equal(JobState.DEAD, q.Get(id)!.State);
            Assert.Equal(0, q.ActiveCount(Lane.Heavy));   // the lane it was holding is released
        }
    }

    [Fact]
    public void ReapStuck_LeavesAFinishedRowAlone_HoweverOldItIs()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(id, JobState.RUNNING, JobState.DONE, 1));

        Assert.Equal(0, q.ReapStuck(TimeSpan.FromMinutes(45), DateTimeOffset.UtcNow.AddYears(1)));
        Assert.Equal(JobState.DONE, q.Get(id)!.State);   // a delivered job is not resurrectable as DEAD
    }

    [Fact]
    public void Heartbeat_DoesNotResurrectAFinishedRow()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(id, JobState.RUNNING, JobState.DONE, 1));
        DateTimeOffset? beat = q.Get(id)!.HeartbeatUtc;

        // A stray worker that outlived its own job. The false return is the lost-CAS surface: the worker's
        // job is no longer live, so it must stop writing state rather than overwrite the external verdict.
        Assert.False(q.Heartbeat(id));

        Assert.Equal(beat, q.Get(id)!.HeartbeatUtc);
        Assert.Equal(JobState.DONE, q.Get(id)!.State);
    }

    // ---------------- open ----------------

    [Fact]
    public void Open_OnAGarbageFile_Throws_RatherThanReportingAnEmptyQueue()
    {
        string path = Path.Combine(_scratch, "garbage.db");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE, 0x42, 0x42 });

        // A queue that reports "no jobs" because its file is unreadable re-runs paid work and double-launches
        // Desktops. Corruption must be loud.
        Assert.Throws<QueueOpenException>(() => JobQueue.Open(path));
    }

    [Fact]
    public void Open_OnATruncatedDatabase_Throws_RatherThanReportingAnEmptyQueue()
    {
        using (var q = Open()) q.Enqueue(Sub(JobId.New()));

        byte[] whole = File.ReadAllBytes(DbPath);
        File.WriteAllBytes(DbPath, whole.Take(whole.Length / 3).ToArray());

        Assert.Throws<QueueOpenException>(() => JobQueue.Open(DbPath));
    }

    [Fact]
    public void Open_OnAnEmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() => JobQueue.Open("   "));
    }

    [Fact]
    public void Open_CreatesTheRootDirectory_WhenItIsNotThereYet()
    {
        string nested = Path.Combine(_scratch, "not-yet", "queue.db");
        using var q = JobQueue.Open(nested);

        Assert.True(File.Exists(nested));
    }

    /// <summary>Runs both delegates at once off a barrier and returns how many returned true. Both callers are
    /// released together, so the window the queue has to serialise is as narrow as the scheduler allows.</summary>
    private static int RaceCount(Func<bool> left, Func<bool> right)
    {
        using var ready = new Barrier(2);
        var results = new bool[2];

        var tasks = new[]
        {
            Task.Run(() => { ready.SignalAndWait(); results[0] = left(); }),
            Task.Run(() => { ready.SignalAndWait(); results[1] = right(); }),
        };
        Task.WaitAll(tasks, TimeSpan.FromSeconds(30));

        return results.Count(r => r);
    }
}
