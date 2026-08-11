using System.Globalization;
using System.Text.Json.Nodes;
using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The forensic trail, over <see cref="JobLog"/>, and the one rule that stands above its content: NOTHING it
/// does may reach Console.Out. stdout is the MCP JSON-RPC channel, and in service mode Console.Out and
/// Console.Error are the same file writer, so a stray stdout write would never surface in service testing -
/// these tests are the only place the distinction exists to be asserted.
///
/// The line shape is a wire contract: the trail is parsed by line, so the key set AND order are fixed
/// (additive-only forever), an absent pid or port is 0 rather than a missing or null key, and a log that
/// cannot open or cannot write must swallow the failure - a log that throws or blocks kills the job it exists
/// to describe, which is worse than a trail with a gap in it.
///
/// One test resolves the trail through JobPaths (ForJob), so the class joins the jobs-root serialisation
/// collection; everything else supplies the jsonl path explicitly on its own scratch.
/// </summary>
[Collection(JobsRootCollection.Name)]
public sealed class JobLogTests : IDisposable
{
    private static readonly string[] PhaseKeys =
        { "ts", "jobId", "tenant", "phase", "pid", "msmdsrvPort", "ramFreeMB", "cFreeGB", "dFreeGB", "event", "ok" };

    private static readonly string[] SummaryKeys =
        PhaseKeys.Concat(new[] { "final", "elapsedSec", "sha256", "bytes", "error" }).ToArray();

    private readonly string? _savedRoot = JobPaths.RootForTest;
    private readonly string _scratch;
    private readonly string _root;

    public JobLogTests()
    {
        _scratch = NewScratch();
        _root = Path.Combine(_scratch, "superbi-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        JobPaths.RootForTest = _root;
    }

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "joblog-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        JobPaths.RootForTest = _savedRoot;
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private string TrailPath() => Path.Combine(_scratch, Guid.NewGuid().ToString("N"), "job.jsonl");

    /// <summary>A deterministic host reading, so the line's numeric fields are assertable values rather than
    /// whatever this box happens to have free.</summary>
    private static HostSnapshot Snap() =>
        new(18342, 32768, 41.2, 903.5, new DateTimeOffset(2026, 7, 16, 3, 0, 0, TimeSpan.Zero));

    /// <summary>Reads the trail the way a sibling tailer must: sharing ReadWrite, because the log's own writer
    /// still holds the file open. File.ReadAllLines shares Read only and would refuse the live handle.</summary>
    private static string[] ReadTrail(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line) lines.Add(line);
        return lines.ToArray();
    }

    // ---------------- the line shape ----------------

    [Fact]
    public void ThreePhasesAndASummary_AreFourParseableLines_WithTheContractKeySetInItsFixedOrder()
    {
        string path = TrailPath();
        string jobId = JobId.New();
        using (var log = new JobLog(jobId, "acme", path))
        {
            log.Phase("submit", "accepted", ok: true, host: Snap());
            log.Phase("admit", "admitted", ok: true, pid: 9120, host: Snap());
            log.Phase("launch", "no-desktop", ok: false, host: Snap());
            log.Summary(JobState.DONE, TimeSpan.FromSeconds(184.24), "abc123", 48211234L, null);
        }

        string[] lines = File.ReadAllLines(path);
        Assert.Equal(4, lines.Length);
        var parsed = lines.Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();

        // The key set and its order are the wire contract - a renamed or reordered key breaks every parser of
        // the trail, so the assertion is exact sequence equality, not set membership.
        foreach (var line in parsed.Take(3))
            Assert.Equal(PhaseKeys, line.Select(kv => kv.Key).ToArray());
        Assert.Equal(SummaryKeys, parsed[3].Select(kv => kv.Key).ToArray());

        foreach (var line in parsed)
        {
            Assert.Equal(jobId, (string?)line["jobId"]);
            Assert.Equal("acme", (string?)line["tenant"]);
            Assert.True(DateTimeOffset.TryParseExact(
                (string?)line["ts"], "o", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        }
        Assert.Equal(new[] { "submit", "admit", "launch", "summary" },
                     parsed.Select(l => (string?)l["phase"]).ToArray());
        Assert.Equal(new bool?[] { true, true, false, true },
                     parsed.Select(l => (bool?)l["ok"]).ToArray());

        Assert.Equal("done", (string?)parsed[3]["event"]);
        Assert.Equal("DONE", (string?)parsed[3]["final"]);
        Assert.Equal(184.2, (double?)parsed[3]["elapsedSec"]);
        Assert.Equal("abc123", (string?)parsed[3]["sha256"]);
        Assert.Equal(48211234L, (long?)parsed[3]["bytes"]);
        Assert.Null(parsed[3]["error"]);   // the KEY is present (the order assert above), the value is null
    }

    [Fact]
    public void Phase_CarriesTheHostReadingItIsGiven_AndAnAbsentPidOrPortIsZeroNotNull()
    {
        string path = TrailPath();
        using var log = new JobLog(JobId.New(), "acme", path);

        log.Phase("preflight", "clear", ok: true, host: Snap());
        log.Phase("connect", "connected", ok: true, pid: 9120, msmdsrvPort: 55001, host: Snap());

        var parsed = ReadTrail(path).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();

        // 0, not null and not a missing key: the key set holds its shape and its type across every line.
        Assert.Equal(0, (int?)parsed[0]["pid"]);
        Assert.Equal(0, (int?)parsed[0]["msmdsrvPort"]);
        Assert.Equal(9120, (int?)parsed[1]["pid"]);
        Assert.Equal(55001, (int?)parsed[1]["msmdsrvPort"]);

        foreach (var line in parsed)
        {
            Assert.Equal(18342L, (long?)line["ramFreeMB"]);
            Assert.Equal(41.2, (double?)line["cFreeGB"]);
            Assert.Equal(903.5, (double?)line["dFreeGB"]);
        }
    }

    [Fact]
    public void Phase_AppendsADetailAfterOk_OnlyWhenOneIsGiven()
    {
        string path = TrailPath();
        using var log = new JobLog(JobId.New(), "acme", path);

        log.Phase("verify", "sha-mismatch", ok: false, host: Snap(), detail: "expected abc, found def");
        log.Phase("verify", "sha-ok", ok: true, host: Snap());

        var parsed = ReadTrail(path).Select(l => JsonNode.Parse(l)!.AsObject()).ToArray();
        Assert.Equal(PhaseKeys.Append("detail").ToArray(), parsed[0].Select(kv => kv.Key).ToArray());
        Assert.Equal("expected abc, found def", (string?)parsed[0]["detail"]);
        Assert.False(parsed[1].ContainsKey("detail"));   // additive-only: absent, never null padding
    }

    [Fact]
    public void Summary_OfAFailure_CarriesTheErrorAndOkFalse()
    {
        string path = TrailPath();
        using (var log = new JobLog(JobId.New(), "acme", path))
            log.Summary(JobState.FAILED, TimeSpan.FromSeconds(3), null, null, "the refresh timed out");

        var line = JsonNode.Parse(File.ReadAllLines(path).Single())!.AsObject();
        Assert.Equal("failed", (string?)line["event"]);
        Assert.False((bool?)line["ok"]);
        Assert.Equal("FAILED", (string?)line["final"]);
        Assert.Null(line["sha256"]);
        Assert.Null(line["bytes"]);
        Assert.Equal("the refresh timed out", (string?)line["error"]);
    }

    // ---------------- stdout is the JSON-RPC channel ----------------

    [Fact]
    public void NothingReachesConsoleOut_AcrossTheWholeLifecycle()
    {
        // The marker is the jobId: every line JobLog can emit carries it, and a fresh ULID cannot appear in any
        // other test's output - so the claim survives suite parallelism, where asserting an EMPTY trap would
        // flake on a CLI-verb test legitimately printing its result to stdout at the same moment.
        string jobId = JobId.New();
        string path = TrailPath();
        string blocker = Path.Combine(_scratch, "stdout-blocker");
        File.WriteAllText(blocker, "x");

        TextWriter real = Console.Out;
        using var trap = new StringWriter();
        Console.SetOut(trap);
        try
        {
            using (var log = new JobLog(jobId, "acme", path))
            {
                log.Phase("submit", "accepted", ok: true, host: Snap());
                log.Phase("refresh", "full", ok: true, pid: 9120, msmdsrvPort: 55001, host: Snap(), detail: "8 tables");
                log.Summary(JobState.DONE, TimeSpan.FromSeconds(2), "abc", 42L, null);
            }
            using (var broken = new JobLog(jobId, "acme", Path.Combine(blocker, "logs", "job.jsonl")))
            {
                broken.Phase("submit", "accepted", ok: true, host: Snap());
                broken.Summary(JobState.FAILED, TimeSpan.Zero, null, null, "boom");
            }
        }
        finally
        {
            Console.SetOut(real);
        }

        Assert.DoesNotContain(jobId, trap.ToString(), StringComparison.Ordinal);
        Assert.Equal(3, File.ReadAllLines(path).Length);   // the lines went to the file, not the channel
    }

    // ---------------- the log never takes the job down ----------------

    [Fact]
    public void ALogThatCannotOpen_SwallowsEveryWrite_AndNeverThrows()
    {
        // The parent path is a FILE, so the logs directory cannot be created and the writer never opens. The
        // job this trail belongs to must still run to completion.
        string blocker = Path.Combine(_scratch, "blocker");
        File.WriteAllText(blocker, "x");
        string path = Path.Combine(blocker, "logs", "job.jsonl");

        using var log = new JobLog(JobId.New(), "acme", path);
        log.Phase("submit", "accepted", ok: true);
        log.Phase("launch", "no-desktop", ok: false, detail: "scaffold-only host");
        log.Summary(JobState.FAILED, TimeSpan.Zero, null, null, "the log could not open");

        Assert.Equal(path, log.Path);        // the path is still reported, so the failure is diagnosable
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void AWriteAfterDispose_IsSwallowed_NotAThrowAndNotALine()
    {
        string path = TrailPath();
        var log = new JobLog(JobId.New(), "acme", path);
        log.Phase("submit", "accepted", ok: true, host: Snap());
        log.Dispose();

        // A stray worker that outlived its own job: its late lines vanish, they do not crash it.
        log.Phase("reap", "late", ok: false, host: Snap());
        log.Summary(JobState.DEAD, TimeSpan.Zero, null, null, "late");
        log.Dispose();

        Assert.Single(File.ReadAllLines(path));
    }

    // ---------------- the file is shared, not owned ----------------

    [Fact]
    public void ASiblingCanTailTheTrail_WhileTheLogStillHoldsIt_AndAppendingPastTheTailerStillWorks()
    {
        string path = TrailPath();
        using var log = new JobLog(JobId.New(), "acme", path);
        log.Phase("refresh", "started", ok: true, host: Snap());

        // FileShare.ReadWrite on the log's writer is what admits this reader; AutoFlush is what makes the line
        // already visible to it. A tailer that cannot open the trail of a LIVE job is a trail nobody can watch.
        Assert.NotNull(JsonNode.Parse(ReadTrail(path).Single()));

        log.Phase("refresh", "finished", ok: true, host: Snap());
        using var tail = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        log.Phase("save", "dispatched", ok: true, host: Snap());   // appending past a live tailer must not throw
        Assert.Equal(3, ReadTrail(path).Length);
    }

    // ---------------- path resolution ----------------

    [Fact]
    public void ForJob_OpensTheTrailInsideTheJobsOwnTree_AndSanitisesTheTenantInEveryLine()
    {
        string jobId = JobId.New();
        using var log = JobLog.ForJob(jobId, "the tenant!");
        log.Phase("submit", "accepted", ok: true, host: Snap());

        Assert.Equal(Path.Combine(_root, jobId, "logs", "job.jsonl"), log.Path);

        var line = JsonNode.Parse(ReadTrail(log.Path).Single())!.AsObject();
        // The tenant lands by the same rule that names its directories: one safe segment, no separators.
        Assert.Equal("the_tenant_", (string?)line["tenant"]);
    }
}
