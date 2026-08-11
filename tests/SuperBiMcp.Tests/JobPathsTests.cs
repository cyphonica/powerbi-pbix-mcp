using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Offline unit tests for <see cref="JobPaths"/>: the root resolution order, and the tenant guard that keeps a
/// tenant directory from ever naming part of the Phase 0 tree itself.
///
/// These mutate the process environment and the static test seam, so they share the jobs-root serialisation
/// collection: two classes racing on the one global root would let one's teardown move it mid-test.
/// </summary>
[Collection(JobsRootCollection.Name)]
public sealed class JobPathsTests : IDisposable
{
    private readonly string? _rootForTest = JobPaths.RootForTest;
    private readonly string? _jobsRoot = Environment.GetEnvironmentVariable("SUPERBI_JOBS_ROOT");
    private readonly string? _jobRoot = Environment.GetEnvironmentVariable("SUPERBI_JOBROOT");

    public void Dispose()
    {
        JobPaths.RootForTest = _rootForTest;
        Environment.SetEnvironmentVariable("SUPERBI_JOBS_ROOT", _jobsRoot);
        Environment.SetEnvironmentVariable("SUPERBI_JOBROOT", _jobRoot);
    }

    // ---------------- root resolution ----------------

    [Fact]
    public void Root_PrefersTheTestSeam_OverBothEnvVars()
    {
        Environment.SetEnvironmentVariable("SUPERBI_JOBS_ROOT", @"X:\explicit");
        Environment.SetEnvironmentVariable("SUPERBI_JOBROOT", @"X:\server");
        JobPaths.RootForTest = @"X:\seam";

        Assert.Equal(@"X:\seam", JobPaths.Root);
    }

    [Fact]
    public void Root_PrefersTheExplicitOverride_OverTheServersJobStore()
    {
        JobPaths.RootForTest = null;
        Environment.SetEnvironmentVariable("SUPERBI_JOBS_ROOT", @"X:\explicit");
        Environment.SetEnvironmentVariable("SUPERBI_JOBROOT", @"X:\server");

        // The explicit override is taken verbatim: it names the root itself, not a store to nest under.
        Assert.Equal(@"X:\explicit", JobPaths.Root);
    }

    [Fact]
    public void Root_NestsAReservedLeafUnderTheServersExistingJobStore()
    {
        JobPaths.RootForTest = null;
        Environment.SetEnvironmentVariable("SUPERBI_JOBS_ROOT", null);
        Environment.SetEnvironmentVariable("SUPERBI_JOBROOT", @"C:\daxops\jobs");

        Assert.Equal(@"C:\daxops\jobs\_queue", JobPaths.Root);
    }

    [Fact]
    public void Root_FallsBackUnderTheTempPath_WhenNothingIsConfigured()
    {
        JobPaths.RootForTest = null;
        Environment.SetEnvironmentVariable("SUPERBI_JOBS_ROOT", null);
        Environment.SetEnvironmentVariable("SUPERBI_JOBROOT", null);

        // The fallback tracks whatever volume the host actually hands out, never a fixed drive letter: a root
        // hard-coded to a letter this box does not mount as a fixed disk throws on the first job.
        Assert.StartsWith(Path.GetTempPath(), JobPaths.Root, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "superbi-jobs", "_queue"), JobPaths.Root);
    }

    [Fact]
    public void Root_IsReResolvedPerRead_RatherThanCachedFromFirstUse()
    {
        JobPaths.RootForTest = @"X:\first";
        Assert.Equal(@"X:\first", JobPaths.Root);

        JobPaths.RootForTest = @"X:\second";
        Assert.Equal(@"X:\second", JobPaths.Root);
    }

    [Fact]
    public void QueueDbAndRetained_HangOffTheResolvedRoot()
    {
        JobPaths.RootForTest = @"X:\seam";

        Assert.Equal(@"X:\seam\queue.db", JobPaths.QueueDbPath);
        Assert.Equal(@"X:\seam\_retained", JobPaths.RetainedRoot);
    }

    // ---------------- the tenant guard ----------------

    [Theory]
    [InlineData("_queue")]
    [InlineData("_QUEUE")]          // directory names compare OrdinalIgnoreCase, so the guard must too
    [InlineData("_Queue")]
    [InlineData("_retained")]
    [InlineData("_RETAINED")]
    [InlineData("_Retained")]
    [InlineData("/queue")]          // sanitises INTO a reserved name: '/' becomes '_'
    [InlineData(".queue")]
    [InlineData("-retained")]
    [InlineData(" retained")]
    public void SafeTenant_NeverReturnsAReservedName(string raw)
    {
        string segment = JobPaths.SafeTenant(raw);

        Assert.False(string.Equals(segment, "_queue", StringComparison.OrdinalIgnoreCase),
            $"'{raw}' must not name the queue tree");
        Assert.False(string.Equals(segment, "_retained", StringComparison.OrdinalIgnoreCase),
            $"'{raw}' must not name the retained tree");
    }

    [Fact]
    public void SafeTenant_OfAReservedName_CannotCollideWithTheTreeItself()
    {
        JobPaths.RootForTest = @"X:\seam";
        string jobId = JobId.New();

        // The whole point of the guard: a tenant named after the tree lands beside it, not on top of it.
        Assert.NotEqual(JobPaths.Root, Path.Combine(JobPaths.RetainedRoot, JobPaths.SafeTenant("_queue")));
        Assert.StartsWith(JobPaths.RetainedRoot + Path.DirectorySeparatorChar,
            JobPaths.RetainedDir("_retained", jobId), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "tenant")]
    [InlineData("", "tenant")]
    [InlineData("///", "___")]
    [InlineData("acme", "acme")]
    [InlineData("Acme-Co_1", "Acme-Co_1")]
    [InlineData("../../etc", "______etc")]
    [InlineData("a b", "a_b")]
    [InlineData(@"C:\x", "C__x")]
    public void SafeTenant_SanitisesToOneSegment(string? raw, string expected)
    {
        Assert.Equal(expected, JobPaths.SafeTenant(raw));
    }

    [Fact]
    public void SafeTenant_OfAnyInput_StaysInsideTheRetainedRoot()
    {
        JobPaths.RootForTest = Path.Combine(Path.GetTempPath(), "jobpaths-" + Guid.NewGuid().ToString("N"));
        string retainedFull = Path.GetFullPath(JobPaths.RetainedRoot) + Path.DirectorySeparatorChar;
        string jobId = JobId.New();

        foreach (string raw in new[] { "..", "../..", @"..\..\windows", "_queue", "_retained", "///", "a/b" })
        {
            string full = Path.GetFullPath(JobPaths.RetainedDir(raw, jobId));
            Assert.StartsWith(retainedFull, full, StringComparison.Ordinal);
        }
    }

    // ---------------- the jobId guard ----------------

    [Fact]
    public void EveryJobDerivedPath_RejectsAnIdIsValidRejects()
    {
        JobPaths.RootForTest = @"X:\seam";

        Assert.Throws<ArgumentException>(() => JobPaths.JobDir(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.In(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.Work(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.Out(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.Temp(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.Wv2(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.QuarantineDir(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.Logs(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.JobLogPath(".."));
        Assert.Throws<ArgumentException>(() => JobPaths.RetainedDir("acme", ".."));
    }

    [Fact]
    public void JobSubdirs_AllHangOffTheOneJobDir()
    {
        JobPaths.RootForTest = @"X:\seam";
        string jobId = JobId.New();
        string dir = JobPaths.JobDir(jobId);

        Assert.Equal(Path.Combine(dir, "in"), JobPaths.In(jobId));
        Assert.Equal(Path.Combine(dir, "work"), JobPaths.Work(jobId));
        Assert.Equal(Path.Combine(dir, "out"), JobPaths.Out(jobId));
        Assert.Equal(Path.Combine(dir, "temp"), JobPaths.Temp(jobId));
        Assert.Equal(Path.Combine(dir, "wv2"), JobPaths.Wv2(jobId));
        Assert.Equal(Path.Combine(dir, "quarantine"), JobPaths.QuarantineDir(jobId));
        Assert.Equal(Path.Combine(dir, "logs"), JobPaths.Logs(jobId));
        Assert.Equal(Path.Combine(dir, "logs", "job.jsonl"), JobPaths.JobLogPath(jobId));
    }

    [Fact]
    public void CreateRoot_AndCreateJobTree_AreIdempotent()
    {
        string root = Path.Combine(Path.GetTempPath(), "jobpaths-" + Guid.NewGuid().ToString("N"));
        JobPaths.RootForTest = root;
        string jobId = JobId.New();

        try
        {
            JobPaths.CreateRoot();
            JobPaths.CreateRoot();
            JobPaths.CreateJobTree(jobId);
            JobPaths.CreateJobTree(jobId);

            Assert.True(Directory.Exists(JobPaths.RetainedRoot));
            foreach (string sub in new[] { "in", "work", "out", "temp", "wv2", "quarantine", "logs" })
                Assert.True(Directory.Exists(Path.Combine(JobPaths.JobDir(jobId), sub)), $"{sub} must exist");
        }
        finally
        {
            // Only the tree this test just minted, under a name no other run holds.
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RootForTest_Setter_CreatesNothing()
    {
        string root = Path.Combine(Path.GetTempPath(), "jobpaths-" + Guid.NewGuid().ToString("N"));
        JobPaths.RootForTest = root;

        // A test must be able to point the root at a path that must not exist yet.
        Assert.False(Directory.Exists(root));
    }
}
