using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SuperBiMcp.Jobs;

/// <summary>The lifecycle a job walks. The names are the storage encoding: they are written to and read from
/// the `state` column verbatim, so renaming one is a schema change.</summary>
internal enum JobState { QUEUED, ADMITTED, RUNNING, VERIFYING, DONE, FAILED, DEAD }

/// <summary>One row of the queue. ReserveMB is the claim admission was decided on, not the job's actual use.
/// OwnerStartUtc is the owner pid's process start time - the half of the identity that survives pid
/// recycling - and Incarnation is the per-process GUID of the engine that enqueued the row, so a restart can
/// tell its own queue from a dead predecessor's. Priority is the admission class stamped at Enqueue
/// (0 = high, 1 = normal); head-of-lane orders by it, with <see cref="JobQueue.PriorityAgingAfter"/> aging so
/// a normal-priority row cannot starve.</summary>
internal sealed record JobRow(
    string JobId, string TenantId, Lane Lane, string Route, JobState State,
    int Attempt, int MaxAttempts, long ReserveMB,
    int? OwnerPid, DateTime? OwnerStartUtc,
    int? DesktopPid, DateTime? DesktopStartUtc, int? MsmdsrvPid, DateTime? MsmdsrvStartUtc, int? MsmdsrvPort,
    string JobRoot, string? SourcePath, string? WorkPath,
    string? RetainedPath, string? ArtifactSha256, long? ArtifactBytes,
    string? Error,
    DateTimeOffset CreatedUtc, DateTimeOffset? AdmittedUtc, DateTimeOffset? StartedUtc,
    DateTimeOffset? HeartbeatUtc, DateTimeOffset? FinishedUtc,
    string? Incarnation, int Priority);

/// <summary>Everything a caller must decide before a job exists. Nothing here is derived from the host.
/// Priority is the caller's admission class, resolved by the dispatcher: 0 = high (also unknown/absent
/// classes - never accidentally throttle a caller by default), 1 = normal. Defaulted so
/// every pre-priority call site keeps its meaning: an unstated priority is high.</summary>
internal sealed record JobSubmission(
    string JobId, string TenantId, Lane Lane, string Route,
    long ReserveMB, int MaxAttempts, string JobRoot, string? SourcePath, int Priority = 0);

/// <summary>A pid the queue itself recorded, with the start time that proves the pid was not recycled onto
/// an unrelated process. Both halves are required: a pid alone is not identity on Windows.</summary>
internal readonly record struct RecordedProc(string JobId, JobState State, int Pid, DateTime RecordedStartUtc, string Name);

/// <summary>
/// The store could not be opened. Never swallowed into an empty queue: a queue that reports "no jobs" because
/// its file is unreadable will re-run completed work and double-launch Desktops.
/// </summary>
internal sealed class QueueOpenException : Exception
{
    internal QueueOpenException(string message) : base(message) { }
    internal QueueOpenException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// The durable, cross-process serializer for lane capacity, job state and desktop ownership.
///
/// Every state change is a compare-and-swap - a conditional UPDATE whose rows-affected is the verdict - and
/// never a read-then-write, because the two processes that share this file (a `serve` host and a CLI verb) can
/// interleave between any read and any write. Admission additionally decides inside a BEGIN IMMEDIATE
/// transaction: a deferred transaction takes its write lock only at the claim, by which point another process
/// has already passed the capacity count, and both admit into a lane of one.
///
/// Instances are thread-safe. One connection is held for the lifetime of the instance because the pragmas
/// below are per-connection, not properties of the file, and a pooled connection handed back without them is
/// a connection with SQLite's throwing defaults. Pooling is off so Dispose releases the file handle at once,
/// which is what lets a test delete its scratch directory.
/// </summary>
internal sealed class JobQueue : IDisposable
{
    /// <summary>The schema this build writes. A file stamped higher was written by a newer build and is not
    /// downgraded or guessed at - it is refused. The liveness columns (owner_start_utc, incarnation) are
    /// deliberately NOT a version bump: they are additive and NULLable, an older build reads and writes the
    /// migrated file untouched, so a rollback never bricks the queue.</summary>
    private const int SchemaVersion = 1;

    private const string LiveStates = "('ADMITTED','RUNNING','VERIFYING')";

    /// <summary>LiveStates plus QUEUED: everything a tenant currently has in the pipeline. The per-caller cap
    /// counts a job from the moment it is accepted, or a capped caller could stack the queue itself.</summary>
    private const string PendingOrLiveStates = "('QUEUED','ADMITTED','RUNNING','VERIFYING')";

    /// <summary>The engine incarnation: one GUID per PROCESS, stamped onto every row at Enqueue. A QUEUED row
    /// whose incarnation is not the running engine's has no waiter thread behind it by construction - waiters
    /// only exist in the process that enqueued - which is what lets a startup reconcile retire restart
    /// orphans immediately instead of waiting out a heartbeat.</summary>
    internal static string ProcessIncarnation { get; } = Guid.NewGuid().ToString("N");

    /// <summary>A QUEUED row whose waiter has not stamped its heartbeat for this long is presumed abandoned:
    /// head-of-lane admission ignores it and the stuck sweep retires it. A live waiter polls (and touches)
    /// every 500ms, so 60s is two orders of magnitude of slack.</summary>
    internal static readonly TimeSpan QueuedHeartbeatStaleAfter = TimeSpan.FromSeconds(60);

    /// <summary>Grace measured from creation before a QUEUED row may be judged stale at all, covering the gap
    /// between Enqueue and the waiter's first poll (and rows enqueued by a process that stamps heartbeats on
    /// a different cadence).</summary>
    internal static readonly TimeSpan QueuedCreationGrace = TimeSpan.FromSeconds(120);

    /// <summary>Anti-starvation aging for priority admission: once a QUEUED row has waited this long, its
    /// class stops mattering and it competes as priority 0. A busy high-priority lane can delay a normal job, never
    /// starve it.</summary>
    internal static readonly TimeSpan PriorityAgingAfter = TimeSpan.FromMinutes(15);

    private const string Columns =
        "job_id, tenant_id, lane, route, state, attempt, max_attempts, reserve_mb, " +
        "owner_pid, owner_start_utc, desktop_pid, desktop_start_utc, msmdsrv_pid, msmdsrv_start_utc, msmdsrv_port, " +
        "job_root, source_path, work_path, retained_path, artifact_sha256, artifact_bytes, error, " +
        "created_utc, admitted_utc, started_utc, heartbeat_utc, finished_utc, incarnation, priority";

    private readonly SqliteConnection _db;
    private readonly object _gate = new();
    private bool _disposed;

    internal string DbPath { get; }

    private JobQueue(SqliteConnection db, string dbPath)
    {
        _db = db;
        DbPath = dbPath;
    }

    /// <summary>Open (creating if absent) the queue at <paramref name="dbPath"/>, apply the pragmas and the
    /// DDL, and check the schema stamp. Throws <see cref="QueueOpenException"/> for every failure mode.</summary>
    internal static JobQueue Open(string dbPath)
    {
        if (string.IsNullOrWhiteSpace(dbPath)) throw new ArgumentException("A queue path is required.", nameof(dbPath));

        SqliteConnection? db = null;
        try
        {
            string full = Path.GetFullPath(dbPath);
            string? dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var cs = new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Private,
                Pooling = false,
            }.ToString();

            db = new SqliteConnection(cs);
            db.Open();
            ApplyPragmas(db);
            ApplySchema(db);
            MigrateSchema(db);
            CheckVersion(db, full);
            return new JobQueue(db, full);
        }
        catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
        {
            db?.Dispose();
            throw new QueueOpenException(
                "the SQLite native provider (e_sqlite3) is missing from the published output next to SuperBiMcp.dll - " +
                $"publish with -r win-x64 and verify e_sqlite3.dll landed. Inner: {ex.Message}", ex);
        }
        catch (QueueOpenException)
        {
            db?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            db?.Dispose();
            throw new QueueOpenException($"the job queue at '{dbPath}' could not be opened: {ex.Message}", ex);
        }
    }

    private static void ApplyPragmas(SqliteConnection db)
    {
        // WAL: two processes share this file and a rollback journal serializes readers behind the writer.
        // synchronous FULL: a queue that loses its last commit to a power cut re-runs a completed build.
        // busy_timeout: the default is 0, which throws SQLITE_BUSY under exactly this concurrency.
        Exec(db, "PRAGMA journal_mode = WAL;");
        Exec(db, "PRAGMA synchronous = FULL;");
        Exec(db, "PRAGMA busy_timeout = 10000;");
        Exec(db, "PRAGMA foreign_keys = ON;");
    }

    private static void ApplySchema(SqliteConnection db) => Exec(db, Ddl);

    /// <summary>
    /// Bring an existing file up to this build's column set. The DDL above is CREATE IF NOT EXISTS, so a
    /// queue.db written by an earlier build keeps its old jobs table; the liveness columns are added here by
    /// ALTER TABLE ADD COLUMN, which never rewrites or drops a row. Rows from before the migration read back
    /// with NULL in the new columns, which every consumer treats as "recorded by a pre-liveness build".
    /// </summary>
    private static void MigrateSchema(SqliteConnection db)
    {
        var have = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = db.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(jobs);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) have.Add(reader.GetString(1));
        }

        if (!have.Contains("owner_start_utc")) Exec(db, "ALTER TABLE jobs ADD COLUMN owner_start_utc TEXT;");
        if (!have.Contains("incarnation"))     Exec(db, "ALTER TABLE jobs ADD COLUMN incarnation TEXT;");
        // Priority defaults to 0 = high: rows already in flight at deploy time are never punished, and a
        // rolled-back build's INSERT (which does not name the column) keeps writing priority-0 rows. Additive and
        // defaulted, so - like the liveness columns - deliberately NOT a schema version bump.
        if (!have.Contains("priority"))        Exec(db, "ALTER TABLE jobs ADD COLUMN priority INTEGER NOT NULL DEFAULT 0;");
    }

    private static void CheckVersion(SqliteConnection db, string dbPath)
    {
        using var tx = db.BeginTransaction(deferred: false);

        using (var read = db.CreateCommand())
        {
            read.Transaction = tx;
            read.CommandText = "SELECT version FROM schema_version LIMIT 1;";
            object? found = read.ExecuteScalar();
            if (found is not null && found is not DBNull)
            {
                long version = Convert.ToInt64(found, CultureInfo.InvariantCulture);
                if (version > SchemaVersion)
                    throw new QueueOpenException(
                        $"the job queue at '{dbPath}' is schema v{version}, but this build understands v{SchemaVersion}. " +
                        "It was written by a newer build; run that build, or move the file aside.");
                tx.Commit();
                return;
            }
        }

        using (var stamp = db.CreateCommand())
        {
            stamp.Transaction = tx;
            stamp.CommandText = "INSERT INTO schema_version(version) VALUES ($v);";
            stamp.Parameters.AddWithValue("$v", SchemaVersion);
            stamp.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // ---- submission / idempotency ------------------------------------------------

    /// <summary>Insert-or-return, keyed by jobId. An existing row is NEVER mutated, so a resubmitted jobId
    /// returns the row it already has: re-POSTing a DONE job must not re-run it. A new row is stamped with
    /// this process's <see cref="ProcessIncarnation"/> and an initial heartbeat: the enqueuer is alive at the
    /// instant of the insert, and both stamps are what queued-row liveness is later judged against.</summary>
    internal JobRow Enqueue(JobSubmission sub)
    {
        ArgumentNullException.ThrowIfNull(sub);

        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction(deferred: false);

            string now = Ts(DateTimeOffset.UtcNow);
            int inserted;
            using (var ins = _db.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText =
                    "INSERT OR IGNORE INTO jobs(job_id, tenant_id, lane, route, state, attempt, max_attempts, " +
                    "  reserve_mb, job_root, source_path, created_utc, heartbeat_utc, incarnation, priority) " +
                    "VALUES ($id, $tenant, $lane, $route, 'QUEUED', 0, $max, $reserve, $root, $source, $now, $now, $inc, $priority);";
                ins.Parameters.AddWithValue("$id", sub.JobId);
                ins.Parameters.AddWithValue("$tenant", sub.TenantId);
                ins.Parameters.AddWithValue("$lane", LaneText(sub.Lane));
                ins.Parameters.AddWithValue("$route", sub.Route);
                ins.Parameters.AddWithValue("$max", Math.Max(1, sub.MaxAttempts));
                ins.Parameters.AddWithValue("$reserve", Math.Max(0, sub.ReserveMB));
                ins.Parameters.AddWithValue("$root", sub.JobRoot);
                ins.Parameters.AddWithValue("$source", (object?)sub.SourcePath ?? DBNull.Value);
                ins.Parameters.AddWithValue("$now", now);
                ins.Parameters.AddWithValue("$inc", ProcessIncarnation);
                ins.Parameters.AddWithValue("$priority", Math.Max(0, sub.Priority));
                inserted = ins.ExecuteNonQuery();
            }

            if (inserted == 1) InsertEvent(tx, sub.JobId, now, "enqueue", null, JobState.QUEUED, true, sub.Route);

            JobRow row = ReadRow(tx, sub.JobId)
                ?? throw new QueueOpenException($"job '{sub.JobId}' vanished from the queue immediately after it was written.");
            tx.Commit();
            return row;
        }
    }

    internal JobRow? Get(string jobId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return ReadRow(null, jobId);
        }
    }

    // ---- the atomic transition ---------------------------------------------------

    /// <summary>Compare-and-swap the state. True = this caller won the transition and may proceed; false = the
    /// row was not in <paramref name="from"/>, someone else moved it, and the caller MUST NOT proceed.
    /// A transition into a live state also records the owner's identity: pid plus process start time, because
    /// a bare pid is not identity on Windows. <paramref name="ownerStartUtc"/> is the test seam; production
    /// callers pass their own pid and the start time is resolved from this process.</summary>
    internal bool TryTransition(string jobId, JobState from, JobState to, int ownerPid,
                                string? error = null, string? phase = null, DateTime? ownerStartUtc = null)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction(deferred: false);

            string now = Ts(DateTimeOffset.UtcNow);
            DateTime? ownerStart = ResolveOwnerStart(ownerPid, ownerStartUtc);
            int rows;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "UPDATE jobs " +
                    "   SET state           = $to, " +
                    "       admitted_utc    = CASE WHEN $to = 'ADMITTED' THEN $now ELSE admitted_utc END, " +
                    "       started_utc     = CASE WHEN $to = 'RUNNING'  THEN $now ELSE started_utc  END, " +
                    "       finished_utc    = CASE WHEN $to IN ('DONE','FAILED','DEAD') THEN $now ELSE finished_utc END, " +
                    "       heartbeat_utc   = $now, " +
                    "       owner_pid       = CASE WHEN $to IN ('ADMITTED','RUNNING','VERIFYING') THEN $owner ELSE owner_pid END, " +
                    "       owner_start_utc = CASE WHEN $to IN ('ADMITTED','RUNNING','VERIFYING') THEN $ostart ELSE owner_start_utc END, " +
                    "       error           = CASE WHEN $to IN ('FAILED','DEAD') THEN COALESCE($error, error) ELSE error END " +
                    " WHERE job_id = $id " +
                    "   AND state  = $from;";
                cmd.Parameters.AddWithValue("$to", to.ToString());
                cmd.Parameters.AddWithValue("$from", from.ToString());
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$owner", ownerPid);
                cmd.Parameters.AddWithValue("$ostart", ownerStart is null ? DBNull.Value : Ts(ownerStart.Value));
                cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$id", jobId);
                rows = cmd.ExecuteNonQuery();
            }

            string label = phase ?? "transition";
            if (rows == 1)
            {
                InsertEvent(tx, jobId, now, label, from, to, true, error);
                tx.Commit();
                return true;
            }

            // A lost CAS is the interesting case, so it is recorded - but only against a row that exists, or
            // the event's foreign key would turn a benign miss into a throw.
            JobRow? current = ReadRow(tx, jobId);
            if (current is not null)
                InsertEvent(tx, jobId, now, label, from, to, false,
                            $"expected {from} but the row is {current.State}");
            tx.Commit();
            return false;
        }
    }

    /// <summary>
    /// Admit this job if, and only if, it is head-of-lane, the lane has capacity, the global in-flight cap
    /// has room and the host allows it. All checks and the claim happen under one RESERVED lock, so no two
    /// processes can both pass the count.
    ///
    /// Head-of-lane is priority-first: rows order by EFFECTIVE priority (0 before 1), then
    /// created_utc, then job_id - where a row past <see cref="PriorityAgingAfter"/> competes as priority 0
    /// regardless of class, so high-priority load can delay normal work but never starve it.
    ///
    /// Head-of-lane only considers LIVE-WAITER rows: a QUEUED row whose heartbeat is staler than
    /// <see cref="QueuedHeartbeatStaleAfter"/> (past its <see cref="QueuedCreationGrace"/>) has no thread
    /// polling for it and can never be admitted, so treating it as the head would deadlock the lane forever -
    /// the pre-fix behaviour after any service restart with a queued job. Stale rows are retired by
    /// <see cref="ReapStuck"/>; here they are merely invisible to fairness.
    ///
    /// <paramref name="maxTotal"/> is the GLOBAL concurrent in-flight cap across BOTH lanes
    /// (ADMITTED+RUNNING+VERIFYING); 0 = uncapped. On the production box SUPERBI_MAX_BUILDS=1 flows in here
    /// and byte-preserves the pre-Phase-0 single-semaphore serialization: one job engine-wide, lane
    /// parallelism opt-in by raising it.
    /// </summary>
    internal bool TryAdmit(string jobId, int laneLimit, int ownerPid, Func<JobRow, bool> hostAllows,
                           int maxTotal = 0, DateTime? ownerStartUtc = null)
    {
        ArgumentNullException.ThrowIfNull(hostAllows);

        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction(deferred: false);

            JobRow? row = ReadRow(tx, jobId);
            if (row is null || row.State != JobState.QUEUED) return false;
            if (laneLimit <= 0) return false;

            string lane = LaneText(row.Lane);

            // 1. capacity, counted inside the transaction: first the lane, then the global cap.
            if (CountState(tx, lane, live: true) >= laneLimit) return false;
            if (maxTotal > 0 && CountLiveTotal(tx) >= maxTotal) return false;

            // 2. head-of-lane fairness among live waiters: priority first (EFFECTIVE priority - a row past
            //    PriorityAgingAfter competes as priority 0 whatever its class, so normal-priority work is delayed but
            //    never starved), then created_utc, ties on job_id, which is ordinal in mint order. Liveness =
            //    a heartbeat inside the stale window, or a row still inside its creation grace (not yet
            //    polled) - exactly as before priorities existed. Timestamps are fixed-width ISO-8601 "o", so
            //    the string comparisons are chronological.
            DateTimeOffset queryNow = DateTimeOffset.UtcNow;
            using (var head = _db.CreateCommand())
            {
                head.Transaction = tx;
                head.CommandText =
                    "SELECT job_id FROM jobs WHERE lane = $lane AND state = 'QUEUED' " +
                    "   AND ((heartbeat_utc IS NOT NULL AND heartbeat_utc >= $stale) OR created_utc >= $grace) " +
                    " ORDER BY CASE WHEN created_utc <= $aged THEN 0 ELSE priority END ASC, " +
                    "          created_utc ASC, job_id ASC LIMIT 1;";
                head.Parameters.AddWithValue("$lane", lane);
                head.Parameters.AddWithValue("$stale", Ts(queryNow - QueuedHeartbeatStaleAfter));
                head.Parameters.AddWithValue("$grace", Ts(queryNow - QueuedCreationGrace));
                head.Parameters.AddWithValue("$aged", Ts(queryNow - PriorityAgingAfter));
                if (head.ExecuteScalar() as string != jobId) return false;
            }

            // 3. host floors, decided in C# but still inside the transaction
            if (!hostAllows(row)) return false;

            // 4. claim, recording the owner's identity (pid + start time - a bare pid is not identity)
            string now = Ts(queryNow);
            DateTime? ownerStart = ResolveOwnerStart(ownerPid, ownerStartUtc);
            int rows;
            using (var claim = _db.CreateCommand())
            {
                claim.Transaction = tx;
                claim.CommandText =
                    "UPDATE jobs SET state='ADMITTED', admitted_utc=$now, heartbeat_utc=$now, owner_pid=$owner, " +
                    "                owner_start_utc=$ostart " +
                    " WHERE job_id = $id AND state = 'QUEUED';";
                claim.Parameters.AddWithValue("$now", now);
                claim.Parameters.AddWithValue("$owner", ownerPid);
                claim.Parameters.AddWithValue("$ostart", ownerStart is null ? DBNull.Value : Ts(ownerStart.Value));
                claim.Parameters.AddWithValue("$id", jobId);
                rows = claim.ExecuteNonQuery();
            }
            if (rows != 1) return false;

            InsertEvent(tx, jobId, now, "admit", JobState.QUEUED, JobState.ADMITTED, true,
                        $"lane={lane} limit={laneLimit} totalCap={maxTotal} reserveMB={row.ReserveMB}");
            tx.Commit();
            return true;
        }
    }

    // ---- reads -------------------------------------------------------------------

    /// <summary>1-based position within the job's own lane. 0 when the job is unknown or is no longer queued,
    /// because a job that has been admitted has no queue position to report. Position is counted in EFFECTIVE
    /// priority order - the exact order TryAdmit serves - so a caller's reported position is honest: a
    /// higher-priority job enqueued behind it in time but ahead of it in priority counts as ahead.</summary>
    internal int QueuePosition(string jobId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            JobRow? row = ReadRow(null, jobId);
            if (row is null || row.State != JobState.QUEUED) return 0;

            // Same liveness filter as the head-of-lane query: a heartbeat-stale corpse awaiting the sweep is
            // invisible to admission, so counting it would over-report the position for up to a sweep cycle.
            // The row's own effective priority is computed here with the same aging rule the SQL applies to
            // everyone else, against the same `now`.
            DateTimeOffset now = DateTimeOffset.UtcNow;
            int myEffective = now - row.CreatedUtc >= PriorityAgingAfter ? 0 : row.Priority;
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT COUNT(*) + 1 FROM jobs " +
                " WHERE lane = $lane AND state = 'QUEUED' " +
                "   AND ((heartbeat_utc IS NOT NULL AND heartbeat_utc >= $stale) OR created_utc >= $grace) " +
                "   AND (CASE WHEN created_utc <= $aged THEN 0 ELSE priority END < $myEff " +
                "        OR (CASE WHEN created_utc <= $aged THEN 0 ELSE priority END = $myEff " +
                "            AND (created_utc < $created OR (created_utc = $created AND job_id < $id))));";
            cmd.Parameters.AddWithValue("$lane", LaneText(row.Lane));
            cmd.Parameters.AddWithValue("$stale", Ts(now - QueuedHeartbeatStaleAfter));
            cmd.Parameters.AddWithValue("$grace", Ts(now - QueuedCreationGrace));
            cmd.Parameters.AddWithValue("$aged", Ts(now - PriorityAgingAfter));
            cmd.Parameters.AddWithValue("$myEff", myEffective);
            cmd.Parameters.AddWithValue("$created", Ts(row.CreatedUtc));
            cmd.Parameters.AddWithValue("$id", jobId);
            return ToInt(cmd.ExecuteScalar());
        }
    }

    /// <summary>ADMITTED, RUNNING or VERIFYING: everything holding lane capacity.</summary>
    internal int ActiveCount(Lane lane)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return CountState(null, LaneText(lane), live: true);
        }
    }

    internal int QueuedCount(Lane lane)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return CountState(null, LaneText(lane), live: false);
        }
    }

    /// <summary>Every job this tenant has anywhere in the pipeline - QUEUED as well as the in-flight states -
    /// across both lanes. The denominator of the per-tenant one-at-a-time cap, which the dispatcher checks
    /// BEFORE Enqueue: a capped tenant's second concurrent submission is refused while the first is still
    /// queued, not only while it is running. The tenant id is the sanitised id the rows already carry.</summary>
    internal int CountLiveForTenant(string tenantId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM jobs WHERE tenant_id = $tenant AND state IN {PendingOrLiveStates};";
            cmd.Parameters.AddWithValue("$tenant", tenantId);
            return ToInt(cmd.ExecuteScalar());
        }
    }

    /// <summary>How long the longest-lived in-flight job has been in flight, or null when none is. A job that
    /// was admitted but never started is still in flight, so admission time stands in for its start.</summary>
    internal TimeSpan? OldestRunningAge(DateTimeOffset now)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT MIN(COALESCE(started_utc, admitted_utc, created_utc)) FROM jobs " +
                $" WHERE state IN {LiveStates};";
            if (cmd.ExecuteScalar() is not string oldest || oldest.Length == 0) return null;

            TimeSpan age = now - ParseDto(oldest);
            return age < TimeSpan.Zero ? TimeSpan.Zero : age;
        }
    }

    // ---- in-flight bookkeeping ---------------------------------------------------

    /// <summary>Only an in-flight row has a heartbeat: a finished row must not be resurrectable by a stray
    /// worker that outlived its own job. Returns true when the beat landed on a live row; false means the row
    /// is no longer in flight - it was DEADed externally (reconcile, watchdog) or finished - and the caller's
    /// job has been lost from under it, so the caller MUST stop writing state for that job and log loudly
    /// rather than overwrite the external verdict.</summary>
    internal bool Heartbeat(string jobId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = $"UPDATE jobs SET heartbeat_utc = $now WHERE job_id = $id AND state IN {LiveStates};";
            cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$id", jobId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>The admission waiter's proof of life: stamps the heartbeat of a row that is still QUEUED.
    /// WaitForAdmission calls this every poll, which is what keeps the row visible to head-of-lane fairness
    /// and out of the stuck sweep's 'admission waiter lost' verdict. Returns false when the row is no longer
    /// QUEUED (admitted by us a moment ago, or moved externally) - not an error, just news.</summary>
    internal bool TouchQueued(string jobId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET heartbeat_utc = $now WHERE job_id = $id AND state = 'QUEUED';";
            cmd.Parameters.AddWithValue("$now", Ts(DateTimeOffset.UtcNow));
            cmd.Parameters.AddWithValue("$id", jobId);
            return cmd.ExecuteNonQuery() == 1;
        }
    }

    /// <summary>
    /// The operator's requeue: CAS a terminal FAILED/DEAD row back to QUEUED at the BACK of its lane.
    /// created_utc AND heartbeat_utc are reset to now - a requeued row must never resurrect with its original
    /// (old) timestamp, or it becomes the immortal head of its lane and blocks every later admission (the
    /// pre-fix `jobs requeue` outage). The owner identity is cleared: a QUEUED row is unowned by definition.
    /// Requeueing only re-queues; nothing runs until a client resubmits the same jobId, and an unadopted
    /// requeue goes heartbeat-stale and is swept DEAD ('admission waiter lost') - accepted and documented.
    /// </summary>
    internal bool Requeue(string jobId, JobState from)
    {
        // DONE is a valid source ONLY for the stable-ingest-id resubmit (an idempotent refresh re-runs its
        // reused id by design); the operator CLI gates itself to FAILED/DEAD before calling.
        if (from is not (JobState.FAILED or JobState.DEAD or JobState.DONE))
            throw new ArgumentException("Only a FAILED, DEAD or DONE row can be requeued.", nameof(from));

        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction(deferred: false);

            string now = Ts(DateTimeOffset.UtcNow);
            int rows;
            using (var cmd = _db.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText =
                    "UPDATE jobs SET state='QUEUED', created_utc=$now, heartbeat_utc=$now, " +
                    "                admitted_utc=NULL, started_utc=NULL, finished_utc=NULL, " +
                    "                owner_pid=NULL, owner_start_utc=NULL " +
                    " WHERE job_id = $id AND state = $from;";
                cmd.Parameters.AddWithValue("$now", now);
                cmd.Parameters.AddWithValue("$id", jobId);
                cmd.Parameters.AddWithValue("$from", from.ToString());
                rows = cmd.ExecuteNonQuery();
            }
            if (rows != 1)
            {
                JobRow? current = ReadRow(tx, jobId);
                if (current is not null)
                    InsertEvent(tx, jobId, now, "requeue", from, JobState.QUEUED, false,
                                $"expected {from} but the row is {current.State}");
                tx.Commit();
                return false;
            }

            InsertEvent(tx, jobId, now, "requeue", from, JobState.QUEUED, true,
                        "moved to the back of its lane; a client must resubmit this jobId for it to run");
            tx.Commit();
            return true;
        }
    }

    /// <summary>Record the processes this job owns. The start times are half of the identity: a pid on its own
    /// is reused by Windows and would let the reaper kill a stranger.</summary>
    internal void SetDesktop(string jobId, int desktopPid, DateTime desktopStartUtc,
                             int msmdsrvPid, DateTime msmdsrvStartUtc, int msmdsrvPort)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE jobs SET desktop_pid = $dpid, desktop_start_utc = $dstart, " +
                "                msmdsrv_pid = $mpid, msmdsrv_start_utc = $mstart, msmdsrv_port = $port " +
                " WHERE job_id = $id;";
            cmd.Parameters.AddWithValue("$dpid", desktopPid);
            cmd.Parameters.AddWithValue("$dstart", Ts(desktopStartUtc));
            cmd.Parameters.AddWithValue("$mpid", msmdsrvPid);
            cmd.Parameters.AddWithValue("$mstart", Ts(msmdsrvStartUtc));
            cmd.Parameters.AddWithValue("$port", msmdsrvPort);
            cmd.Parameters.AddWithValue("$id", jobId);

            try
            {
                cmd.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                // ux_jobs_desktop_live / ux_jobs_port_live. This is the two-job collision caught structurally,
                // one layer below the code that was about to send a Ctrl+S into another job's Desktop.
                throw new InvalidOperationException(
                    $"job '{jobId}' cannot claim Desktop pid {desktopPid} on port {msmdsrvPort}: another live job " +
                    $"already owns it. {ex.Message}", ex);
            }
        }
    }

    /// <summary>Release the recorded processes. Also releases the live-ownership indexes, so the next job may
    /// claim the same port.</summary>
    internal void ClearDesktop(string jobId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE jobs SET desktop_pid = NULL, desktop_start_utc = NULL, " +
                "                msmdsrv_pid = NULL, msmdsrv_start_utc = NULL, msmdsrv_port = NULL " +
                " WHERE job_id = $id;";
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();
        }
    }

    internal void SetWorkPath(string jobId, string workPath)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText = "UPDATE jobs SET work_path = $path WHERE job_id = $id;";
            cmd.Parameters.AddWithValue("$path", workPath);
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();
        }
    }

    internal void SetRetained(string jobId, string retainedPath, string sha256, long bytes)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "UPDATE jobs SET retained_path = $path, artifact_sha256 = $sha, artifact_bytes = $bytes " +
                " WHERE job_id = $id;";
            cmd.Parameters.AddWithValue("$path", retainedPath);
            cmd.Parameters.AddWithValue("$sha", sha256);
            cmd.Parameters.AddWithValue("$bytes", bytes);
            cmd.Parameters.AddWithValue("$id", jobId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Increment and return the attempt count. 0 for an unknown job.</summary>
    internal int BumpAttempt(string jobId)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction(deferred: false);

            using (var bump = _db.CreateCommand())
            {
                bump.Transaction = tx;
                bump.CommandText = "UPDATE jobs SET attempt = attempt + 1 WHERE job_id = $id;";
                bump.Parameters.AddWithValue("$id", jobId);
                if (bump.ExecuteNonQuery() != 1) return 0;
            }

            using var read = _db.CreateCommand();
            read.Transaction = tx;
            read.CommandText = "SELECT attempt FROM jobs WHERE job_id = $id;";
            read.Parameters.AddWithValue("$id", jobId);
            int attempt = ToInt(read.ExecuteScalar());

            tx.Commit();
            return attempt;
        }
    }

    // ---- maintenance -------------------------------------------------------------

    /// <summary>
    /// At process start, kill off in-flight rows no live owner is behind. A row claiming OUR pid is a victim
    /// too: we have only just started, so it cannot be ours - it is a dead predecessor's record whose pid
    /// Windows recycled onto us. Ownership is judged by pid PLUS recorded start time (the callback is
    /// DesktopInterop.PidAlive(pid, startUtc) in production, 1s tolerance): a bare pid is not identity on
    /// Windows, and trusting one left a phantom job holding the heavy lane for the watchdog's full 45 minutes
    /// whenever a crash's pid was recycled onto a stranger. A live-state row with no recorded start time
    /// cannot be identified, so it is retired rather than trusted.
    ///
    /// When <paramref name="myIncarnation"/> is given (the engine host passes <see cref="ProcessIncarnation"/>
    /// at startup; the CLI passes null - it is not the engine and must not kill rows the LIVE engine's waiters
    /// are polling), QUEUED rows stamped by any other incarnation are DEADed immediately: their waiter threads
    /// died with the process that enqueued them, so no admission will ever come.
    /// </summary>
    internal int Reconcile(int myPid, Func<int, DateTime, bool> ownerAlive, DateTimeOffset now,
                           string? myIncarnation = null)
    {
        ArgumentNullException.ThrowIfNull(ownerAlive);

        lock (_gate)
        {
            ThrowIfDisposed();

            var victims = new List<(string JobId, string Reason, bool Queued)>();
            using (var scan = _db.CreateCommand())
            {
                scan.CommandText = $"SELECT job_id, owner_pid, owner_start_utc FROM jobs WHERE state IN {LiveStates};";
                using var reader = scan.ExecuteReader();
                while (reader.Read())
                {
                    string jobId = reader.GetString(0);
                    int? owner = reader.IsDBNull(1) ? null : reader.GetInt32(1);
                    DateTime? ownerStart = reader.IsDBNull(2) ? null : ParseDt(reader.GetString(2));

                    string? reason =
                        owner is null ? "in flight with no recorded owner at startup"
                        : owner.Value == myPid ? $"owner pid {owner.Value} is this process, so the row predates us"
                        : ownerStart is null ? $"owner pid {owner.Value} has no recorded start time, so it cannot be verified as ours"
                        : !ownerAlive(owner.Value, ownerStart.Value) ? $"owner pid {owner.Value} is not alive (or its pid was recycled onto another process)"
                        : null;
                    if (reason is not null) victims.Add((jobId, reason, Queued: false));
                }
            }

            if (myIncarnation is not null)
            {
                using var scan = _db.CreateCommand();
                scan.CommandText =
                    "SELECT job_id FROM jobs WHERE state = 'QUEUED' " +
                    "   AND (incarnation IS NULL OR incarnation <> $inc);";
                scan.Parameters.AddWithValue("$inc", myIncarnation);
                using var reader = scan.ExecuteReader();
                while (reader.Read())
                    victims.Add((reader.GetString(0), "orphaned by engine restart", Queued: true));
            }

            if (victims.Count == 0) return 0;

            using var tx = _db.BeginTransaction(deferred: false);
            string stamp = Ts(now);
            int killed = 0;
            foreach (var (jobId, reason, queued) in victims)
                if (Kill(tx, jobId, reason, stamp, "reconcile", queued ? QueuedState : LiveStates)) killed++;
            tx.Commit();
            return killed;
        }
    }

    /// <summary>In-flight rows that stopped heartbeating. A row that never heartbeat at all is measured from
    /// its admission instead, or a NULL comparison would make it immortal. With RUNNING work heartbeating on
    /// a timer, <paramref name="maxSilence"/> only catches genuinely dead rows.
    ///
    /// Also retires QUEUED rows whose admission waiter is gone: a heartbeat staler than
    /// <see cref="QueuedHeartbeatStaleAfter"/>, past the <see cref="QueuedCreationGrace"/>, means no thread is
    /// polling for that row (a live waiter touches it every 500ms), so it can never be admitted and must not
    /// linger as a DB row nobody owns. Head-of-lane fairness already ignores such rows, so this sweep is
    /// hygiene, not the deadlock fix itself.</summary>
    internal int ReapStuck(TimeSpan maxSilence, DateTimeOffset now)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            string cutoff = Ts(now - maxSilence);

            var victims = new List<string>();
            using (var scan = _db.CreateCommand())
            {
                scan.CommandText =
                    "SELECT job_id FROM jobs " +
                    $" WHERE state IN {LiveStates} " +
                    "   AND COALESCE(heartbeat_utc, admitted_utc, created_utc) < $cutoff;";
                scan.Parameters.AddWithValue("$cutoff", cutoff);
                using var reader = scan.ExecuteReader();
                while (reader.Read()) victims.Add(reader.GetString(0));
            }

            var lostWaiters = new List<string>();
            using (var scan = _db.CreateCommand())
            {
                scan.CommandText =
                    "SELECT job_id FROM jobs " +
                    " WHERE state = 'QUEUED' " +
                    "   AND (heartbeat_utc IS NULL OR heartbeat_utc < $stale) " +
                    "   AND created_utc < $grace;";
                scan.Parameters.AddWithValue("$stale", Ts(now - QueuedHeartbeatStaleAfter));
                scan.Parameters.AddWithValue("$grace", Ts(now - QueuedCreationGrace));
                using var reader = scan.ExecuteReader();
                while (reader.Read()) lostWaiters.Add(reader.GetString(0));
            }

            if (victims.Count == 0 && lostWaiters.Count == 0) return 0;

            using var tx = _db.BeginTransaction(deferred: false);
            string stamp = Ts(now);
            string reason = $"no heartbeat for {maxSilence.TotalMinutes.ToString("0.#", CultureInfo.InvariantCulture)} minutes";
            int killed = 0;
            foreach (string jobId in victims)
                if (Kill(tx, jobId, reason, stamp, "reap-stuck")) killed++;
            foreach (string jobId in lostWaiters)
                if (Kill(tx, jobId, "admission waiter lost", stamp, "reap-stuck", QueuedState)) killed++;
            tx.Commit();
            return killed;
        }
    }

    /// <summary>
    /// Every process this queue recorded against a job that is no longer live - the reaper's ONLY input. A
    /// process the queue never recorded cannot appear here, which is the whole safety argument: an operator's
    /// own interactive Power BI is invisible to the reaper by construction, not by a filter. A row missing its
    /// start time is dropped for the same reason - without it the pid is not identity, and an unidentified
    /// process is never a target.
    /// </summary>
    internal IReadOnlyList<RecordedProc> RecordedProcesses()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                "SELECT job_id, state, desktop_pid, desktop_start_utc, 'PBIDesktop' FROM jobs " +
                $" WHERE desktop_pid IS NOT NULL AND state NOT IN {LiveStates} " +
                "UNION ALL " +
                "SELECT job_id, state, msmdsrv_pid, msmdsrv_start_utc, 'msmdsrv' FROM jobs " +
                $" WHERE msmdsrv_pid IS NOT NULL AND state NOT IN {LiveStates};";

            var found = new List<RecordedProc>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                if (reader.IsDBNull(3)) continue;
                found.Add(new RecordedProc(
                    reader.GetString(0), ParseState(reader.GetString(1)), reader.GetInt32(2),
                    ParseDt(reader.GetString(3)), reader.GetString(4)));
            }
            return found;
        }
    }

    internal void LogEvent(string jobId, string phase, JobState? from, JobState? to, bool ok, string? detail)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var tx = _db.BeginTransaction(deferred: false);
            InsertEvent(tx, jobId, Ts(DateTimeOffset.UtcNow), phase, from, to, ok, detail);
            tx.Commit();
        }
    }

    /// <summary>Newest first. A null filter matches every value of that column.</summary>
    internal IReadOnlyList<JobRow> List(Lane? lane, JobState? state, int limit)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            using var cmd = _db.CreateCommand();
            cmd.CommandText =
                $"SELECT {Columns} FROM jobs " +
                " WHERE ($lane IS NULL OR lane = $lane) AND ($state IS NULL OR state = $state) " +
                " ORDER BY created_utc DESC, job_id DESC LIMIT $limit;";
            cmd.Parameters.AddWithValue("$lane", lane is null ? DBNull.Value : LaneText(lane.Value));
            cmd.Parameters.AddWithValue("$state", state is null ? DBNull.Value : state.Value.ToString());
            cmd.Parameters.AddWithValue("$limit", limit <= 0 ? 0 : limit);

            var rows = new List<JobRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) rows.Add(MapRow(reader));
            return rows;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _db.Dispose();
        }
    }

    // ---- internals ---------------------------------------------------------------

    private const string QueuedState = "('QUEUED')";

    /// <summary>CAS a single row to DEAD from one of <paramref name="fromStates"/> (a SQL state list; live
    /// states by default, QUEUED for the waiter-liveness sweeps). False when it moved on under us, which is
    /// not an error: the job finished (or was adopted) on its own between the scan and the sweep.</summary>
    private bool Kill(SqliteTransaction tx, string jobId, string reason, string nowStamp, string phase,
                      string fromStates = LiveStates)
    {
        int rows;
        using (var cmd = _db.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE jobs SET state='DEAD', error=$reason, finished_utc=$now, heartbeat_utc=$now " +
                $" WHERE job_id=$id AND state IN {fromStates};";
            cmd.Parameters.AddWithValue("$reason", reason);
            cmd.Parameters.AddWithValue("$now", nowStamp);
            cmd.Parameters.AddWithValue("$id", jobId);
            rows = cmd.ExecuteNonQuery();
        }
        if (rows != 1) return false;

        InsertEvent(tx, jobId, nowStamp, phase, null, JobState.DEAD, false, reason);
        return true;
    }

    private void InsertEvent(SqliteTransaction tx, string jobId, string nowStamp, string phase,
                             JobState? from, JobState? to, bool ok, string? detail)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText =
            "INSERT INTO job_events(job_id, ts_utc, phase, from_state, to_state, ok, detail) " +
            "VALUES ($id, $ts, $phase, $from, $to, $ok, $detail);";
        cmd.Parameters.AddWithValue("$id", jobId);
        cmd.Parameters.AddWithValue("$ts", nowStamp);
        cmd.Parameters.AddWithValue("$phase", phase);
        cmd.Parameters.AddWithValue("$from", from is null ? DBNull.Value : from.Value.ToString());
        cmd.Parameters.AddWithValue("$to", to is null ? DBNull.Value : to.Value.ToString());
        cmd.Parameters.AddWithValue("$ok", ok ? 1 : 0);
        cmd.Parameters.AddWithValue("$detail", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private int CountState(SqliteTransaction? tx, string lane, bool live)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = live
            ? $"SELECT COUNT(*) FROM jobs WHERE lane = $lane AND state IN {LiveStates};"
            : "SELECT COUNT(*) FROM jobs WHERE lane = $lane AND state = 'QUEUED';";
        cmd.Parameters.AddWithValue("$lane", lane);
        return ToInt(cmd.ExecuteScalar());
    }

    /// <summary>Everything in flight across BOTH lanes - the denominator of the global cap.</summary>
    private int CountLiveTotal(SqliteTransaction? tx)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT COUNT(*) FROM jobs WHERE state IN {LiveStates};";
        return ToInt(cmd.ExecuteScalar());
    }

    // The current process's start time, captured once. Paired with Environment.ProcessId it is this process's
    // identity, stored so a later reconcile can tell "our pid, still us" from "our pid, recycled onto a
    // stranger". Reading our own start time cannot realistically fail, but a queue must never refuse a state
    // transition over it, so the fallback degrades to the moment this type was first touched.
    private static readonly DateTime SelfStartUtc = ReadSelfStartUtc();

    private static DateTime ReadSelfStartUtc()
    {
        try { using var p = System.Diagnostics.Process.GetCurrentProcess(); return p.StartTime.ToUniversalTime(); }
        catch { return DateTime.UtcNow; }
    }

    /// <summary>The owner start time a live-state stamp records: an explicit value (the test seam) wins;
    /// otherwise the caller claiming its OWN pid gets this process's start time, and a foreign pid gets NULL -
    /// the queue never guesses at another process's identity.</summary>
    private static DateTime? ResolveOwnerStart(int ownerPid, DateTime? explicitStart) =>
        explicitStart ?? (ownerPid == Environment.ProcessId ? SelfStartUtc : null);

    private JobRow? ReadRow(SqliteTransaction? tx, string jobId)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = $"SELECT {Columns} FROM jobs WHERE job_id = $id;";
        cmd.Parameters.AddWithValue("$id", jobId);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? MapRow(reader) : null;
    }

    private static JobRow MapRow(SqliteDataReader r) => new(
        r.GetString(0), r.GetString(1), ParseLane(r.GetString(2)), r.GetString(3), ParseState(r.GetString(4)),
        r.GetInt32(5), r.GetInt32(6), r.GetInt64(7),
        NullableInt(r, 8), NullableDt(r, 9),
        NullableInt(r, 10), NullableDt(r, 11), NullableInt(r, 12), NullableDt(r, 13), NullableInt(r, 14),
        r.GetString(15), NullableStr(r, 16), NullableStr(r, 17),
        NullableStr(r, 18), NullableStr(r, 19), r.IsDBNull(20) ? null : r.GetInt64(20),
        NullableStr(r, 21),
        ParseDto(r.GetString(22)), NullableDto(r, 23), NullableDto(r, 24), NullableDto(r, 25), NullableDto(r, 26),
        NullableStr(r, 27),
        r.IsDBNull(28) ? 0 : r.GetInt32(28));   // NULL never happens (NOT NULL DEFAULT 0), read defensively as priority 0

    private static void Exec(SqliteConnection db, string sql)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static int ToInt(object? scalar) =>
        scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);

    private static int? NullableInt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    private static string? NullableStr(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static DateTime? NullableDt(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseDt(r.GetString(i));
    private static DateTimeOffset? NullableDto(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : ParseDto(r.GetString(i));

    // Timestamps are stored ISO-8601 "o" at UTC, which is both round-trippable and fixed-width, so the ordinal
    // string comparisons the queue ordering depends on are chronological comparisons.
    private static string Ts(DateTimeOffset v) => v.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);

    private static string Ts(DateTime v) => Ts(new DateTimeOffset(
        v.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : v.ToUniversalTime()));

    private static DateTimeOffset ParseDto(string s) =>
        DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static DateTime ParseDt(string s) => ParseDto(s).UtcDateTime;

    private static string LaneText(Lane lane) => lane == Lane.Heavy ? "heavy" : "cheap";

    private static Lane ParseLane(string s) => s switch
    {
        "heavy" => Lane.Heavy,
        "cheap" => Lane.Cheap,
        _ => throw new InvalidOperationException($"The queue holds an unknown lane '{s}'."),
    };

    private static JobState ParseState(string s) => s switch
    {
        "QUEUED" => JobState.QUEUED,
        "ADMITTED" => JobState.ADMITTED,
        "RUNNING" => JobState.RUNNING,
        "VERIFYING" => JobState.VERIFYING,
        "DONE" => JobState.DONE,
        "FAILED" => JobState.FAILED,
        "DEAD" => JobState.DEAD,
        _ => throw new InvalidOperationException($"The queue holds an unknown state '{s}'."),
    };

    private const string Ddl = """
        CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);

        CREATE TABLE IF NOT EXISTS jobs (
          job_id            TEXT    PRIMARY KEY NOT NULL,
          tenant_id         TEXT    NOT NULL,
          lane              TEXT    NOT NULL CHECK (lane IN ('cheap','heavy')),
          route             TEXT    NOT NULL,
          state             TEXT    NOT NULL CHECK (state IN ('QUEUED','ADMITTED','RUNNING','VERIFYING','DONE','FAILED','DEAD')),
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
          incarnation       TEXT,
          priority          INTEGER NOT NULL DEFAULT 0
        );

        CREATE INDEX IF NOT EXISTS ix_jobs_lane_state_created ON jobs(lane, state, created_utc, job_id);
        CREATE INDEX IF NOT EXISTS ix_jobs_state_heartbeat    ON jobs(state, heartbeat_utc);
        CREATE INDEX IF NOT EXISTS ix_jobs_tenant_state       ON jobs(tenant_id, state);
        CREATE INDEX IF NOT EXISTS ix_jobs_desktop            ON jobs(desktop_pid) WHERE desktop_pid IS NOT NULL;

        -- one live job may own a given Desktop pid, and one live job may own a given port. The structural half
        -- of the defence against job B's save landing in job A's Desktop.
        CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_desktop_live
          ON jobs(desktop_pid)
          WHERE desktop_pid IS NOT NULL AND state IN ('ADMITTED','RUNNING','VERIFYING');

        CREATE UNIQUE INDEX IF NOT EXISTS ux_jobs_port_live
          ON jobs(msmdsrv_port)
          WHERE msmdsrv_port IS NOT NULL AND state IN ('ADMITTED','RUNNING','VERIFYING');

        CREATE TABLE IF NOT EXISTS job_events (
          id         INTEGER PRIMARY KEY AUTOINCREMENT,
          job_id     TEXT    NOT NULL REFERENCES jobs(job_id) ON DELETE CASCADE,
          ts_utc     TEXT    NOT NULL,
          phase      TEXT    NOT NULL,
          from_state TEXT,
          to_state   TEXT,
          ok         INTEGER NOT NULL,
          detail     TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_events_job ON job_events(job_id, id);
        """;
}
