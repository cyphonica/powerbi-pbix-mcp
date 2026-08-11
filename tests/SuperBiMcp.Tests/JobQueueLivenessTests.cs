using System.Globalization;
using Microsoft.Data.Sqlite;
using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Regression coverage for the Phase 0 adversarial-review findings around QUEUED-row liveness: the stranded
/// head-of-lane deadlock (a QUEUED row whose waiter died blocked its lane forever), the requeue verb
/// manufacturing an immortal head, the bare-pid reconcile trusting a recycled owner pid, the global
/// SUPERBI_MAX_BUILDS cap, the heartbeat lost-CAS surface, and the in-place migration of a pre-liveness
/// queue.db. Time-dependent cases backdate rows with raw SQL (the same trick the ReapStuck NULL-heartbeat
/// test uses) rather than waiting out real windows.
/// </summary>
public sealed class JobQueueLivenessTests : IDisposable
{
    private readonly string _scratch = NewScratch();

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "jobqueue-liveness-tests-" + Guid.NewGuid().ToString("N"));
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

    private static string Ts(DateTimeOffset v) => v.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Rewrites a row's timestamps in place - the seam that lets these tests age a row without
    /// waiting out the real stale window. Null heartbeat writes SQL NULL (a pre-liveness enqueuer).</summary>
    private void Backdate(string jobId, DateTimeOffset createdUtc, DateTimeOffset? heartbeatUtc)
    {
        using var raw = new SqliteConnection($"Data Source={DbPath}");
        raw.Open();
        using var cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE jobs SET created_utc = $created, heartbeat_utc = $beat WHERE job_id = $id;";
        cmd.Parameters.AddWithValue("$created", Ts(createdUtc));
        cmd.Parameters.AddWithValue("$beat", heartbeatUtc is null ? DBNull.Value : Ts(heartbeatUtc.Value));
        cmd.Parameters.AddWithValue("$id", jobId);
        Assert.Equal(1, cmd.ExecuteNonQuery());
    }

    // ---------------- the stranded-head deadlock (the three critical findings) ----------------

    [Fact]
    public void TryAdmit_IgnoresAStaleQueuedHead_SoAStrandedRowCannotBrickTheLane()
    {
        using var q = Open();
        string stranded = JobId.New();
        string live = JobId.New();
        q.Enqueue(Sub(stranded));
        q.Enqueue(Sub(live));

        // The failure scenario: a service restart killed `stranded`'s waiter thread; its row survived in
        // queue.db as the oldest QUEUED row. Pre-fix, head-of-lane returned it forever and every later job
        // in the lane was refused admission for eternity.
        DateTimeOffset old = DateTimeOffset.UtcNow.AddMinutes(-10);
        Backdate(stranded, createdUtc: old, heartbeatUtc: old);

        // The live waiter behind it must be admitted: a heartbeat-stale row is invisible to fairness.
        Assert.True(q.TryAdmit(live, laneLimit: 1, ownerPid: 1, _ => true));
        Assert.Equal(JobState.QUEUED, q.Get(stranded)!.State);   // ignored, not mutated - the sweep retires it

        // And the stale row can never admit ITSELF either - only a fresh heartbeat (a live waiter) can.
        Assert.False(q.TryAdmit(stranded, laneLimit: 2, ownerPid: 1, _ => true));
    }

    [Fact]
    public void TryAdmit_StillHonoursAnUnpolledRowInsideItsCreationGrace()
    {
        using var q = Open();
        string unpolled = JobId.New();
        string behind = JobId.New();
        q.Enqueue(Sub(unpolled));
        q.Enqueue(Sub(behind));

        // A NULL heartbeat inside the creation grace is a row whose waiter simply has not polled yet
        // (or a pre-liveness enqueuer mid-flight). It keeps its head position; nobody jumps it. Created
        // 60s ago: older than `behind`, but still inside the 120s grace.
        Backdate(unpolled, createdUtc: DateTimeOffset.UtcNow.AddSeconds(-60), heartbeatUtc: null);

        Assert.False(q.TryAdmit(behind, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(unpolled, laneLimit: 2, ownerPid: 1, _ => true));
    }

    [Fact]
    public void TouchQueued_IsTheWaitersProofOfLife_AndKeepsItsHeadPosition()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));

        // An old row whose waiter is still polling: TouchQueued every poll is what keeps it admissible.
        DateTimeOffset old = DateTimeOffset.UtcNow.AddMinutes(-10);
        Backdate(id, createdUtc: old, heartbeatUtc: old);

        Assert.True(q.TouchQueued(id));
        Assert.True(q.TryAdmit(id, laneLimit: 1, ownerPid: 1, _ => true));

        // Once the row is no longer QUEUED the touch reports it moved - news for the waiter, not an error.
        Assert.False(q.TouchQueued(id));
    }

    [Fact]
    public void ReapStuck_RetiresAStaleQueuedRow_AsAdmissionWaiterLost_AndSparesLiveWaiters()
    {
        using var q = Open();
        string stranded = JobId.New();
        string fresh = JobId.New();
        q.Enqueue(Sub(stranded));
        q.Enqueue(Sub(fresh));

        DateTimeOffset old = DateTimeOffset.UtcNow.AddMinutes(-10);
        Backdate(stranded, createdUtc: old, heartbeatUtc: old);

        Assert.Equal(1, q.ReapStuck(TimeSpan.FromMinutes(45), DateTimeOffset.UtcNow));

        Assert.Equal(JobState.DEAD, q.Get(stranded)!.State);
        Assert.Contains("admission waiter lost", q.Get(stranded)!.Error);
        Assert.Equal(JobState.QUEUED, q.Get(fresh)!.State);   // its enqueue heartbeat is fresh - waiter alive
    }

    [Fact]
    public void ReapStuck_LeavesAnUnpolledQueuedRowAlone_UntilItsCreationGraceExpires()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Backdate(id, createdUtc: DateTimeOffset.UtcNow, heartbeatUtc: null);

        // Inside the grace nothing may judge it; the waiter has had no chance to stamp yet.
        Assert.Equal(0, q.ReapStuck(TimeSpan.FromMinutes(45), DateTimeOffset.UtcNow));

        // Past the grace with still no heartbeat, the waiter never existed - retired.
        Assert.Equal(1, q.ReapStuck(TimeSpan.FromMinutes(45), DateTimeOffset.UtcNow.AddMinutes(5)));
        Assert.Equal(JobState.DEAD, q.Get(id)!.State);
    }

    // ---------------- incarnation: restart orphans die immediately ----------------

    [Fact]
    public void Reconcile_DeadsQueuedRowsFromAnotherIncarnation_ImmediatelyAtStartup()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));   // stamped with THIS process's incarnation

        // Our own incarnation adopts the row: nothing to kill.
        Assert.Equal(0, q.Reconcile(Environment.ProcessId, (_, _) => true, DateTimeOffset.UtcNow,
                                    myIncarnation: JobQueue.ProcessIncarnation));

        // A restarted engine has a NEW incarnation GUID; every QUEUED row it finds was enqueued by a dead
        // process whose waiter threads died with it. They are retired at once - no 60s wait, no 5-min tick.
        Assert.Equal(1, q.Reconcile(Environment.ProcessId, (_, _) => true, DateTimeOffset.UtcNow,
                                    myIncarnation: Guid.NewGuid().ToString("N")));
        Assert.Equal(JobState.DEAD, q.Get(id)!.State);
        Assert.Equal("orphaned by engine restart", q.Get(id)!.Error);
    }

    [Fact]
    public void Reconcile_TreatsALegacyNullIncarnationQueuedRow_AsOrphaned()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        using (var raw = new SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var strip = raw.CreateCommand();
            strip.CommandText = "UPDATE jobs SET incarnation = NULL WHERE job_id = $id;";
            strip.Parameters.AddWithValue("$id", id);
            Assert.Equal(1, strip.ExecuteNonQuery());
        }

        // A row with no incarnation was written by a pre-liveness build - which is by definition not the
        // running engine, so it has no waiter either.
        Assert.Equal(1, q.Reconcile(Environment.ProcessId, (_, _) => true, DateTimeOffset.UtcNow,
                                    myIncarnation: JobQueue.ProcessIncarnation));
        Assert.Equal(JobState.DEAD, q.Get(id)!.State);
    }

    [Fact]
    public void Reconcile_WithoutAnIncarnation_NeverTouchesQueuedRows_TheCliSafetyContract()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));

        // `jobs reconcile` passes no incarnation: the CLI cannot know the live engine's GUID, and killing
        // rows the live engine's waiters are polling would be the CLI manufacturing the very outage the
        // engine's own reconcile exists to clear.
        Assert.Equal(0, q.Reconcile(Environment.ProcessId, (_, _) => false, DateTimeOffset.UtcNow));
        Assert.Equal(JobState.QUEUED, q.Get(id)!.State);
    }

    // ---------------- owner identity: pid + start time, never a bare pid ----------------

    [Fact]
    public void Reconcile_KillsARowWhoseOwnerPidWasRecycled_ByStartTimeMismatch()
    {
        using var q = Open();
        string id = JobId.New();
        var crashedStart = new DateTime(2026, 7, 16, 3, 0, 0, DateTimeKind.Utc);
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, ownerPid: 4212, ownerStartUtc: crashedStart));

        // The failure scenario: the engine crashed mid-bake and Windows recycled pid 4212 onto a svchost.
        // A bare-pid check says "alive" and the phantom row holds the heavy lane for the watchdog's full 45
        // minutes. The identity check gets the recorded start time and reports the mismatch.
        var recycledStart = crashedStart.AddMinutes(7);
        int killed = q.Reconcile(myPid: 1,
                                 ownerAlive: (pid, start) => Math.Abs((start - recycledStart).TotalSeconds) < 1.0,
                                 now: DateTimeOffset.UtcNow);

        Assert.Equal(1, killed);
        Assert.Equal(JobState.DEAD, q.Get(id)!.State);
        Assert.Equal(0, q.ActiveCount(Lane.Heavy));   // the lane is free NOW, not 45 minutes from now
    }

    [Fact]
    public void Reconcile_KillsALiveStateRowWithNoRecordedOwnerStart_BecauseABarePidIsNotIdentity()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        // A foreign pid with no explicit start time records NULL - the queue never guesses at another
        // process's identity (this is also the shape a pre-migration row has).
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, ownerPid: 4242));

        int killed = q.Reconcile(myPid: 1, ownerAlive: (_, _) => true, now: DateTimeOffset.UtcNow);

        Assert.Equal(1, killed);
        Assert.Contains("start time", q.Get(id)!.Error);
    }

    [Fact]
    public void TryTransition_RecordsThisProcesssOwnIdentity_WhenItClaimsWithItsOwnPid()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, Environment.ProcessId));

        JobRow row = q.Get(id)!;
        Assert.Equal(Environment.ProcessId, row.OwnerPid);
        Assert.NotNull(row.OwnerStartUtc);   // production callers get identity stamped with no extra plumbing

        // And the recorded identity survives the very check production runs (PidAlive, 1s tolerance).
        Assert.Equal(0, q.Reconcile(myPid: 1, ownerAlive: SuperBiMcp.DesktopInterop.PidAlive,
                                    now: DateTimeOffset.UtcNow));
        Assert.Equal(JobState.RUNNING, q.Get(id)!.State);
    }

    // ---------------- requeue: back of the lane, never an immortal head ----------------

    [Fact]
    public void Requeue_MovesTheRowToTheBackOfItsLane_SoItCanNeverDeadlockAdmission()
    {
        using var q = Open();
        string failed = JobId.New();
        q.Enqueue(Sub(failed));
        Assert.True(q.TryAdmit(failed, laneLimit: 1, ownerPid: 1, _ => true));
        Assert.True(q.TryTransition(failed, JobState.ADMITTED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(failed, JobState.RUNNING, JobState.FAILED, 1, "boom"));

        string younger = JobId.New();
        q.Enqueue(Sub(younger));

        // The failure scenario: pre-fix, requeue kept the original (old) created_utc, so the requeued row
        // instantly became THE head of its lane - and since no client can resubmit an engine-minted id, no
        // waiter would ever admit it: the recovery verb converted one dead job into a permanent lane outage.
        DateTimeOffset before = DateTimeOffset.UtcNow;
        Assert.True(q.Requeue(failed, JobState.FAILED));

        JobRow row = q.Get(failed)!;
        Assert.Equal(JobState.QUEUED, row.State);
        Assert.True(row.CreatedUtc >= before, "created_utc must be reset to now - the back of the lane");
        Assert.True(row.HeartbeatUtc >= before, "heartbeat must be reset so the row is adoptable, not instantly stale");
        Assert.Null(row.AdmittedUtc);
        Assert.Null(row.StartedUtc);
        Assert.Null(row.FinishedUtc);
        Assert.Null(row.OwnerPid);

        // The younger row is now ahead of the requeued one, not blocked behind an immortal head.
        Assert.False(q.TryAdmit(failed, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(younger, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(failed, laneLimit: 2, ownerPid: 1, _ => true));
    }

    [Fact]
    public void Requeue_RefusesANonTerminalFromState_AndReportsALostRace()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));

        Assert.Throws<ArgumentException>(() => q.Requeue(id, JobState.QUEUED));

        // A CAS miss (the row is QUEUED, not FAILED) is a false, never a mutation.
        Assert.False(q.Requeue(id, JobState.FAILED));
        Assert.Equal(JobState.QUEUED, q.Get(id)!.State);
    }

    // ---------------- the global in-flight cap (SUPERBI_MAX_BUILDS across BOTH lanes) ----------------

    [Fact]
    public void TryAdmit_EnforcesTheGlobalCapAcrossBothLanes_SoMaxBuildsOneMeansOneJobTotal()
    {
        using var q = Open();
        string heavy = JobId.New();
        string cheap = JobId.New();
        q.Enqueue(Sub(heavy, Lane.Heavy));
        q.Enqueue(Sub(cheap, Lane.Cheap));

        // The deploy target: SUPERBI_MAX_BUILDS=1 on an 8GB box. The lane split alone would let a heavy bake
        // and a cheap build stack; the global cap restores "one build at a time" byte-for-byte.
        Assert.True(q.TryAdmit(heavy, laneLimit: 1, ownerPid: 1, _ => true, maxTotal: 1));
        Assert.False(q.TryAdmit(cheap, laneLimit: 3, ownerPid: 1, _ => true, maxTotal: 1));
        Assert.Equal(JobState.QUEUED, q.Get(cheap)!.State);

        // RUNNING still holds the cap.
        Assert.True(q.TryTransition(heavy, JobState.ADMITTED, JobState.RUNNING, 1));
        Assert.False(q.TryAdmit(cheap, laneLimit: 3, ownerPid: 1, _ => true, maxTotal: 1));

        // Only a terminal state releases it.
        Assert.True(q.TryTransition(heavy, JobState.RUNNING, JobState.DONE, 1));
        Assert.True(q.TryAdmit(cheap, laneLimit: 3, ownerPid: 1, _ => true, maxTotal: 1));
    }

    [Fact]
    public void TryAdmit_WithNoGlobalCap_KeepsTheLanesIndependentlyBudgeted()
    {
        using var q = Open();
        string heavy = JobId.New();
        string cheap = JobId.New();
        q.Enqueue(Sub(heavy, Lane.Heavy));
        q.Enqueue(Sub(cheap, Lane.Cheap));

        // maxTotal 0 = uncapped: lane parallelism is the operator's explicit opt-in.
        Assert.True(q.TryAdmit(heavy, laneLimit: 1, ownerPid: 1, _ => true, maxTotal: 0));
        Assert.True(q.TryAdmit(cheap, laneLimit: 1, ownerPid: 1, _ => true, maxTotal: 0));
    }

    // ---------------- the heartbeat lost-CAS surface ----------------

    [Fact]
    public void Heartbeat_ReportsALiveRowTrue_AndAnExternallyDeadedRowFalse()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id));
        Assert.True(q.TryTransition(id, JobState.QUEUED, JobState.RUNNING, 1));

        Assert.True(q.Heartbeat(id));   // in flight: the beat lands

        // The watchdog (or a reconcile) DEADs the row while work() is still executing. From that moment the
        // runner's beats miss - the signal that tells it to stop writing state instead of overwriting the
        // external verdict with a phantom "done".
        Assert.True(q.TryTransition(id, JobState.RUNNING, JobState.DEAD, 1, "watchdog"));
        Assert.False(q.Heartbeat(id));
        Assert.Equal(JobState.DEAD, q.Get(id)!.State);
    }

    // ---------------- migration: the box's existing queue.db ----------------

    [Fact]
    public void Open_MigratesAPreLivenessQueueDb_AddingColumnsWithoutDroppingRows()
    {
        // Build a queue.db exactly as the previous build laid it out: no owner_start_utc, no incarnation.
        const string legacyQueued = "legacy-queued";
        const string legacyRunning = "legacy-running";
        using (var raw = new SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using (var ddl = raw.CreateCommand())
            {
                ddl.CommandText = """
                    CREATE TABLE schema_version (version INTEGER NOT NULL);
                    CREATE TABLE jobs (
                      job_id            TEXT    PRIMARY KEY NOT NULL,
                      tenant_id         TEXT    NOT NULL,
                      lane              TEXT    NOT NULL CHECK (lane IN ('cheap','heavy')),
                      route             TEXT    NOT NULL,
                      state             TEXT    NOT NULL,
                      attempt           INTEGER NOT NULL DEFAULT 0,
                      max_attempts      INTEGER NOT NULL DEFAULT 1,
                      reserve_mb        INTEGER NOT NULL DEFAULT 0,
                      owner_pid         INTEGER,
                      desktop_pid       INTEGER,
                      desktop_start_utc TEXT,
                      msmdsrv_pid       INTEGER,
                      msmdsrv_start_utc TEXT,
                      msmdsrv_port      INTEGER,
                      job_root          TEXT    NOT NULL,
                      source_path       TEXT,
                      work_path         TEXT,
                      retained_path     TEXT,
                      artifact_sha256   TEXT,
                      artifact_bytes    INTEGER,
                      error             TEXT,
                      created_utc       TEXT    NOT NULL,
                      admitted_utc      TEXT,
                      started_utc       TEXT,
                      heartbeat_utc     TEXT,
                      finished_utc      TEXT
                    );
                    CREATE TABLE job_events (
                      id         INTEGER PRIMARY KEY AUTOINCREMENT,
                      job_id     TEXT    NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
                      ts_utc     TEXT    NOT NULL,
                      phase      TEXT    NOT NULL,
                      from_state TEXT,
                      to_state   TEXT,
                      ok         INTEGER NOT NULL,
                      detail     TEXT
                    );
                    INSERT INTO schema_version(version) VALUES (1);
                    """;
                ddl.ExecuteNonQuery();
            }
            using (var ins = raw.CreateCommand())
            {
                ins.CommandText = """
                    INSERT INTO jobs(job_id, tenant_id, lane, route, state, job_root, created_utc)
                    VALUES ($q, 'acme', 'heavy', '/ingest', 'QUEUED', 'C:\daxops\jobs\x', $created);
                    INSERT INTO jobs(job_id, tenant_id, lane, route, state, owner_pid, job_root, created_utc, heartbeat_utc)
                    VALUES ($r, 'acme', 'heavy', '/ingest', 'RUNNING', 999, 'C:\daxops\jobs\y', $created, $created);
                    """;
                ins.Parameters.AddWithValue("$q", legacyQueued);
                ins.Parameters.AddWithValue("$r", legacyRunning);
                ins.Parameters.AddWithValue("$created", Ts(DateTimeOffset.UtcNow.AddMinutes(-3)));
                ins.ExecuteNonQuery();
            }
        }

        // The new build opens it in place: ALTER TABLE ADD COLUMN, never a drop, never a rebuild.
        using var q = Open();
        Assert.Equal(2, q.List(null, null, 100).Count);          // no data lost

        JobRow migrated = q.Get(legacyQueued)!;
        Assert.Equal(JobState.QUEUED, migrated.State);
        Assert.Null(migrated.Incarnation);                       // pre-liveness rows read as unstamped
        Assert.Null(migrated.OwnerStartUtc);

        // And the startup reconcile retires both legacy rows: the QUEUED one has no incarnation (no waiter
        // can exist for it), the RUNNING one has a bare owner pid with no start-time identity.
        int killed = q.Reconcile(Environment.ProcessId, (_, _) => true, DateTimeOffset.UtcNow,
                                 myIncarnation: JobQueue.ProcessIncarnation);
        Assert.Equal(2, killed);
        Assert.Equal("orphaned by engine restart", q.Get(legacyQueued)!.Error);

        // New work through the migrated file gets the full liveness treatment.
        string fresh = JobId.New();
        q.Enqueue(Sub(fresh));
        Assert.Equal(JobQueue.ProcessIncarnation, q.Get(fresh)!.Incarnation);
        Assert.NotNull(q.Get(fresh)!.HeartbeatUtc);
        Assert.True(q.TryAdmit(fresh, laneLimit: 1, ownerPid: 1, _ => true, maxTotal: 1));
    }
}
