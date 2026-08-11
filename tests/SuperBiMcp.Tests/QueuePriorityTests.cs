using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Regression coverage for paid-first admission (Phase 0 F1-F4): the priority column and its in-place
/// migration, the effective-priority head-of-lane ordering with 15-minute anti-starvation aging, the honest
/// effective-priority QueuePosition, the tenant pipeline count behind the free-tier one-at-a-time cap, and
/// the additive `priority` field in the jobs CLI row JSON. Time-dependent cases backdate rows with raw SQL
/// (the same trick the liveness tests use) rather than waiting out real windows.
/// </summary>
public sealed class QueuePriorityTests : IDisposable
{
    private readonly string _scratch = NewScratch();

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "queue-priority-tests-" + Guid.NewGuid().ToString("N"));
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

    private static JobSubmission Sub(string jobId, int priority = 0, Lane lane = Lane.Heavy, string tenant = "acme") =>
        new(jobId, tenant, lane, "build_report", ReserveMB: 4096, MaxAttempts: 1,
            JobRoot: "C:\\daxops\\jobs\\_queue", SourcePath: null, Priority: priority);

    private static string Ts(DateTimeOffset v) => v.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Rewrites a row's timestamps in place - the seam that lets these tests age a row past the
    /// 15-minute priority window without waiting it out. The heartbeat is set fresh separately from
    /// created_utc, because an aged row must still be a LIVE waiter to compete at all.</summary>
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

    // ---------------- F1: paid-first head-of-lane ----------------

    [Fact]
    public void TryAdmit_AdmitsAPaidJobEnqueuedAfterAFreeOne_BecausePriorityOutranksArrival()
    {
        using var q = Open();
        string free = JobId.New();
        string paid = JobId.New();
        q.Enqueue(Sub(free, priority: 1));
        q.Enqueue(Sub(paid, priority: 0));   // younger AND paid

        // The priority is stamped at Enqueue and read back verbatim.
        Assert.Equal(1, q.Get(free)!.Priority);
        Assert.Equal(0, q.Get(paid)!.Priority);

        // The free row arrived first, but the paid row is the effective head: the free row may not admit
        // itself past it, and the paid row admits despite not being oldest.
        Assert.False(q.TryAdmit(free, laneLimit: 1, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(paid, laneLimit: 1, ownerPid: 1, _ => true));

        // Delayed, never lost: once the paid job releases the lane, the free row is served.
        Assert.True(q.TryTransition(paid, JobState.ADMITTED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(paid, JobState.RUNNING, JobState.DONE, 1));
        Assert.True(q.TryAdmit(free, laneLimit: 1, ownerPid: 1, _ => true));
    }

    [Fact]
    public void TryAdmit_AgesAFreeRowPastFifteenMinutes_SoItBeatsAFreshPaidRow()
    {
        using var q = Open();
        string free = JobId.New();
        q.Enqueue(Sub(free, priority: 1));

        // The free row has waited out the aging window. Its waiter is still alive (fresh heartbeat), so it
        // is a live head candidate - and past 15 minutes its tier stops mattering: it competes as priority 0
        // with the older created_utc, so a fresh paid arrival may no longer jump it.
        Backdate(free, createdUtc: DateTimeOffset.UtcNow.AddMinutes(-16), heartbeatUtc: DateTimeOffset.UtcNow);

        string paid = JobId.New();
        q.Enqueue(Sub(paid, priority: 0));

        Assert.False(q.TryAdmit(paid, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(free, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(paid, laneLimit: 2, ownerPid: 1, _ => true));
    }

    [Fact]
    public void TryAdmit_KeepsFifoBetweenTwoFreeRows()
    {
        using var q = Open();
        string first = JobId.New();
        string second = JobId.New();
        q.Enqueue(Sub(first, priority: 1));
        q.Enqueue(Sub(second, priority: 1));

        // Same priority = the pre-priority ordering, byte for byte: created_utc then job_id.
        Assert.False(q.TryAdmit(second, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(first, laneLimit: 2, ownerPid: 1, _ => true));
        Assert.True(q.TryAdmit(second, laneLimit: 2, ownerPid: 1, _ => true));
    }

    [Fact]
    public void TryAdmit_StillIgnoresAHeartbeatStaleFreeRow_EvenThoughItHasAgedIntoPriorityZero()
    {
        using var q = Open();
        string stranded = JobId.New();
        string paid = JobId.New();
        q.Enqueue(Sub(stranded, priority: 1));
        q.Enqueue(Sub(paid, priority: 0));

        // Aged past 15 minutes AND heartbeat-stale: aging must not resurrect a row whose waiter is gone.
        // The liveness filter runs before any ordering, exactly as it did before priorities existed.
        DateTimeOffset old = DateTimeOffset.UtcNow.AddMinutes(-16);
        Backdate(stranded, createdUtc: old, heartbeatUtc: old);

        Assert.True(q.TryAdmit(paid, laneLimit: 1, ownerPid: 1, _ => true));
        Assert.Equal(JobState.QUEUED, q.Get(stranded)!.State);   // ignored, not mutated - the sweep retires it
    }

    // ---------------- F1: honest QueuePosition ----------------

    [Fact]
    public void QueuePosition_CountsAYoungerPaidRowAsAhead_SoAFreeCallersPositionIsHonest()
    {
        using var q = Open();
        string free = JobId.New();
        string paid = JobId.New();
        q.Enqueue(Sub(free, priority: 1));
        q.Enqueue(Sub(paid, priority: 0));

        // The paid row is behind the free row in time but ahead of it in priority: the position each caller
        // is told must be the order TryAdmit will actually serve.
        Assert.Equal(2, q.QueuePosition(free));
        Assert.Equal(1, q.QueuePosition(paid));

        // Once the free row ages past the window it competes as priority 0 with the older created_utc, and
        // the reported positions swap with it.
        Backdate(free, createdUtc: DateTimeOffset.UtcNow.AddMinutes(-16), heartbeatUtc: DateTimeOffset.UtcNow);
        Assert.Equal(1, q.QueuePosition(free));
        Assert.Equal(2, q.QueuePosition(paid));
    }

    // ---------------- F2: the tenant pipeline count ----------------

    [Fact]
    public void CountLiveForTenant_CountsExactlyThePipelineStates_AndOnlyThatTenant()
    {
        using var q = Open();
        const string capped = "free-tenant";
        const string other = "other-tenant";
        q.Enqueue(Sub(JobId.New(), priority: 0, tenant: other));   // another tenant's live job never counts

        string a = JobId.New();
        q.Enqueue(Sub(a, priority: 1, tenant: capped));
        Assert.Equal(1, q.CountLiveForTenant(capped));             // QUEUED counts: the cap holds from acceptance

        Assert.True(q.TryTransition(a, JobState.QUEUED, JobState.ADMITTED, 1));
        Assert.Equal(1, q.CountLiveForTenant(capped));
        Assert.True(q.TryTransition(a, JobState.ADMITTED, JobState.RUNNING, 1));
        Assert.Equal(1, q.CountLiveForTenant(capped));
        Assert.True(q.TryTransition(a, JobState.RUNNING, JobState.VERIFYING, 1));
        Assert.Equal(1, q.CountLiveForTenant(capped));

        Assert.True(q.TryTransition(a, JobState.VERIFYING, JobState.DONE, 1));
        Assert.Equal(0, q.CountLiveForTenant(capped));             // DONE releases the slot

        string b = JobId.New();
        q.Enqueue(Sub(b, priority: 1, tenant: capped));
        Assert.True(q.TryTransition(b, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(b, JobState.RUNNING, JobState.FAILED, 1, "boom"));
        Assert.Equal(0, q.CountLiveForTenant(capped));             // FAILED releases it too

        string c = JobId.New();
        q.Enqueue(Sub(c, priority: 1, tenant: capped));
        Assert.True(q.TryTransition(c, JobState.QUEUED, JobState.RUNNING, 1));
        Assert.True(q.TryTransition(c, JobState.RUNNING, JobState.DEAD, 1, "watchdog"));
        Assert.Equal(0, q.CountLiveForTenant(capped));             // and DEAD

        Assert.Equal(1, q.CountLiveForTenant(other));              // the other tenant's job was never touched
    }

    // ---------------- F3: in-place migration of the production queue.db ----------------

    [Fact]
    public void Open_MigratesAPrePriorityQueueDb_ExistingRowsDefaultToPaidPriority()
    {
        // Build a queue.db exactly as the deployed build laid it out: full liveness columns, no priority.
        const string legacyQueued = "legacy-queued";
        const string legacyDone = "legacy-done";
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
                      owner_start_utc   TEXT,
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
                      finished_utc      TEXT,
                      incarnation       TEXT
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
                    INSERT INTO jobs(job_id, tenant_id, lane, route, state, job_root, created_utc, heartbeat_utc, incarnation)
                    VALUES ($q, 'acme', 'heavy', '/ingest', 'QUEUED', 'C:\daxops\jobs\x', $created, $beat, 'deployed-build');
                    INSERT INTO jobs(job_id, tenant_id, lane, route, state, job_root, created_utc, finished_utc)
                    VALUES ($d, 'acme', 'heavy', '/ingest', 'DONE', 'C:\daxops\jobs\y', $created, $beat);
                    """;
                ins.Parameters.AddWithValue("$q", legacyQueued);
                ins.Parameters.AddWithValue("$d", legacyDone);
                ins.Parameters.AddWithValue("$created", Ts(DateTimeOffset.UtcNow.AddMinutes(-3)));
                ins.Parameters.AddWithValue("$beat", Ts(DateTimeOffset.UtcNow));
                ins.ExecuteNonQuery();
            }
        }

        // The new build opens it in place: ALTER TABLE ADD COLUMN, never a drop, never a rebuild.
        using (var q = Open())
        {
            Assert.Equal(2, q.List(null, null, 100).Count);      // no data lost

            // Existing rows read back priority 0 = paid: work in flight at deploy time is never punished.
            Assert.Equal(0, q.Get(legacyQueued)!.Priority);
            Assert.Equal(0, q.Get(legacyDone)!.Priority);

            // The migrated head still admits - the file works, not merely opens.
            Assert.True(q.TryAdmit(legacyQueued, laneLimit: 1, ownerPid: 1, _ => true));

            // New work through the migrated file round-trips a free priority.
            string fresh = JobId.New();
            q.Enqueue(Sub(fresh, priority: 1));
            Assert.Equal(1, q.Get(fresh)!.Priority);
        }

        // Additive and defaulted = NOT a version bump: an older build still opens this file after a rollback.
        using (var raw = new SqliteConnection($"Data Source={DbPath}"))
        {
            raw.Open();
            using var check = raw.CreateCommand();
            check.CommandText = "SELECT version FROM schema_version LIMIT 1;";
            Assert.Equal(1L, check.ExecuteScalar());
        }
    }

    // ---------------- F4: the jobs CLI row JSON ----------------

    [Fact]
    public void JobsCliRowJson_CarriesThePriorityField_WithoutRenamingWhatWpAlreadyParses()
    {
        using var q = Open();
        string id = JobId.New();
        q.Enqueue(Sub(id, priority: 1));

        JsonObject json = JobsCli.RowJson(q.Get(id)!);

        Assert.Equal(1, json["priority"]!.GetValue<int>());      // the one additive field
        Assert.Equal(id, json["jobId"]!.GetValue<string>());     // and the existing shape is untouched
        Assert.Equal("QUEUED", json["state"]!.GetValue<string>());
        Assert.Equal("acme", json["tenantId"]!.GetValue<string>());
        Assert.Equal("heavy", json["lane"]!.GetValue<string>());
    }
}
