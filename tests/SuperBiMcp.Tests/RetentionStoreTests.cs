using System.Text;
using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// Exactly-once production and immutable retention, over <see cref="RetentionStore"/>. The jobId is the
/// idempotency key: a DONE jobId resubmitted must return the retained bytes and never re-run the engine, so
/// these claims are what makes the work billable-once rather than billable-per-retry.
///
/// The retained tree hangs off the process-global JobPaths.RootForTest, so this class joins the jobs-root
/// serialisation collection: another class's teardown moving that root mid-test would flake the whole suite.
/// </summary>
[Collection(JobsRootCollection.Name)]
public sealed class RetentionStoreTests : IDisposable
{
    private readonly string? _savedRoot = JobPaths.RootForTest;
    private readonly string _scratch;
    private readonly string _root;

    public RetentionStoreTests()
    {
        _scratch = NewScratch();
        _root = Path.Combine(_scratch, "superbi-jobs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        JobPaths.RootForTest = _root;
    }

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "retention-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        JobPaths.RootForTest = _savedRoot;
        // Only the tree this test itself created, under a name no other run holds.
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>A produced artifact in the job's out dir, the way a real run leaves one.</summary>
    private string Produce(string content, string name = "report.pbix")
    {
        string dir = Path.Combine(_scratch, "produced-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }

    // ---------------- retain ----------------

    [Fact]
    public void Retain_LandsTheArtifactUnderTheTenantAndJobId_WithAShaSidecar()
    {
        string jobId = JobId.New();

        RetainedArtifact art = RetentionStore.Retain("acme", jobId, Produce("the report bytes"), "report.pbix");

        Assert.Equal(Path.Combine(_root, "_retained", "acme", jobId, "report.pbix"), art.Path);
        Assert.True(File.Exists(art.Path));
        Assert.True(File.Exists(art.Path + ".sha256"));
        Assert.Equal(RetentionStore.Sha256File(art.Path), art.Sha256);
        Assert.Equal(new FileInfo(art.Path).Length, art.Bytes);
        Assert.Contains(art.Sha256, File.ReadAllText(art.Path + ".sha256"));
    }

    [Fact]
    public void Retain_SanitisesAHostileTenant_IntoTheRetainedTreeRatherThanOutOfIt()
    {
        string jobId = JobId.New();
        string retainedRoot = Path.GetFullPath(Path.Combine(_root, "_retained")) + Path.DirectorySeparatorChar;

        RetainedArtifact art = RetentionStore.Retain("../../windows", jobId, Produce("x"), "report.pbix");

        Assert.StartsWith(retainedRoot, Path.GetFullPath(art.Path), StringComparison.Ordinal);
    }

    [Fact]
    public void Retain_LeavesNoTempFileBesideTheArtifact()
    {
        string jobId = JobId.New();
        RetainedArtifact art = RetentionStore.Retain("acme", jobId, Produce("bytes"), "report.pbix");

        // A reader must never see a half-written final, and a completed retain must leave no debris that a
        // later FindAny could serve as the job's result.
        string[] left = Directory.GetFiles(Path.GetDirectoryName(art.Path)!);
        Assert.Equal(2, left.Length);
        Assert.DoesNotContain(left, p => p.Contains(".tmp-", StringComparison.Ordinal));
    }

    // ---------------- idempotency: exactly-once ----------------

    [Fact]
    public void Retain_ReplayedWithTheSameBytes_ReturnsTheRetainedArtifactWithoutRewritingIt()
    {
        string jobId = JobId.New();
        RetainedArtifact first = RetentionStore.Retain("acme", jobId, Produce("identical bytes"), "report.pbix");
        DateTime written = File.GetLastWriteTimeUtc(first.Path);

        RetainedArtifact replay = RetentionStore.Retain("acme", jobId, Produce("identical bytes"), "report.pbix");

        // Exactly-once: the replay is served from what is already retained. An untouched mtime is the evidence
        // that no second write happened - a rewrite would be a second production of paid work.
        Assert.Equal(first.Path, replay.Path);
        Assert.Equal(first.Sha256, replay.Sha256);
        Assert.Equal(first.Bytes, replay.Bytes);
        Assert.Equal(written, File.GetLastWriteTimeUtc(replay.Path));
    }

    [Fact]
    public void Find_OfARetainedJob_ReturnsTheArtifact_SoAResubmitNeverReRunsTheEngine()
    {
        string jobId = JobId.New();
        RetainedArtifact retained = RetentionStore.Retain("acme", jobId, Produce("the deliverable"), "report.pbix");

        RetainedArtifact? found = RetentionStore.Find("acme", jobId, "report.pbix");
        RetainedArtifact? any = RetentionStore.FindAny("acme", jobId);

        Assert.NotNull(found);
        Assert.Equal(retained.Path, found!.Value.Path);
        Assert.Equal(retained.Sha256, found.Value.Sha256);
        Assert.NotNull(any);
        Assert.Equal(retained.Path, any!.Value.Path);   // and the sidecar is never served as the artifact
    }

    [Fact]
    public void Find_OfAJobThatWasNeverRetained_IsNullRatherThanAThrow()
    {
        Assert.Null(RetentionStore.Find("acme", JobId.New(), "report.pbix"));
        Assert.Null(RetentionStore.FindAny("acme", JobId.New()));
    }

    // ---------------- immutability ----------------

    [Fact]
    public void Retain_ReplayedWithDIFFERENTBytes_Throws_AndLeavesTheOriginalByteIdentical()
    {
        string jobId = JobId.New();
        RetainedArtifact first = RetentionStore.Retain("acme", jobId, Produce("the original bytes"), "report.pbix");
        byte[] before = File.ReadAllBytes(first.Path);

        // Two different artifacts under one key means the key was reused, and the first is the one a caller may
        // already have been told about. Never resolved by overwriting.
        Assert.Throws<RetentionConflictException>(() =>
            RetentionStore.Retain("acme", jobId, Produce("completely different bytes"), "report.pbix"));

        Assert.Equal(before, File.ReadAllBytes(first.Path));
        Assert.Equal(first.Sha256, RetentionStore.Sha256File(first.Path));
    }

    [Fact]
    public void Retain_ThatConflicts_DoesNotDestroyTheCallersProducedFile()
    {
        string jobId = JobId.New();
        RetentionStore.Retain("acme", jobId, Produce("the original bytes"), "report.pbix");
        string produced = Produce("different bytes");

        Assert.Throws<RetentionConflictException>(() => RetentionStore.Retain("acme", jobId, produced, "report.pbix"));

        // The caller's file is theirs. A refused retain unwinds; it never consumes the evidence.
        Assert.True(File.Exists(produced));
        Assert.Equal("different bytes", File.ReadAllText(produced));
    }

    [Fact]
    public void Find_CatchesAShaMismatch_RatherThanServingACorruptedArtifactAsTheResult()
    {
        string jobId = JobId.New();
        RetainedArtifact art = RetentionStore.Retain("acme", jobId, Produce("the real bytes"), "report.pbix");

        // The bytes are swapped under us: a corrupted disk, a restored backup, a hand edit.
        File.WriteAllText(art.Path, "tampered bytes");

        var ex = Assert.Throws<RetentionConflictException>(() => RetentionStore.Find("acme", jobId, "report.pbix"));
        Assert.Contains("changed on disk", ex.Message);
    }

    [Fact]
    public void FindAny_CatchesAShaMismatchToo_SoNeitherLookupCanServeCorruptedBytes()
    {
        string jobId = JobId.New();
        RetainedArtifact art = RetentionStore.Retain("acme", jobId, Produce("the real bytes"), "report.pbix");
        File.WriteAllText(art.Path, "tampered bytes");

        Assert.Throws<RetentionConflictException>(() => RetentionStore.FindAny("acme", jobId));
    }

    // ---------------- refusals ----------------

    [Fact]
    public void Retain_OfAZeroByteFile_Throws_BeforeAnythingIsRetained()
    {
        string jobId = JobId.New();
        string empty = Produce("");

        // A zero-byte .pbix is a failed build, not a deliverable. Retaining it would flip the job DONE and bill
        // for nothing.
        Assert.Throws<InvalidOperationException>(() => RetentionStore.Retain("acme", jobId, empty, "report.pbix"));
        Assert.False(Directory.Exists(JobPaths.RetainedDir("acme", jobId)));
        Assert.True(File.Exists(empty));
    }

    [Fact]
    public void Retain_OfAMissingFile_Throws()
    {
        string jobId = JobId.New();
        Assert.Throws<InvalidOperationException>(() =>
            RetentionStore.Retain("acme", jobId, Path.Combine(_scratch, "never-produced.pbix"), "report.pbix"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape.pbix")]
    [InlineData(@"..\escape.pbix")]
    [InlineData(@"sub\report.pbix")]
    [InlineData(@"C:\windows\report.pbix")]
    [InlineData("")]
    [InlineData("   ")]
    public void Retain_RefusesAnArtifactNameThatIsNotALeaf(string finalName)
    {
        // tenant and jobId are already sanitised into the retained path; this is the one segment a caller
        // supplies, so it is the one that must not be allowed to navigate.
        Assert.Throws<ArgumentException>(() => RetentionStore.Retain("acme", JobId.New(), Produce("x"), finalName));
    }

    [Fact]
    public void Retain_RefusesAJobIdThatIsNotAUlid()
    {
        Assert.Throws<ArgumentException>(() => RetentionStore.Retain("acme", "../../windows", Produce("x"), "report.pbix"));
    }
}
