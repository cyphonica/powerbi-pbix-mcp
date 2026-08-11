using SuperBiMcp.Jobs;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// The two rules <see cref="Quarantine"/> exists to hold, proven over a fabricated Desktop tree on scratch - no
/// live Power BI, no real LocalAppData.
///
/// Nothing is ever deleted: a sweep MOVES an artifact aside in its own directory, and a Move that fails is
/// recorded and skipped. The file-count conservation assertions are the standing guard on that - a recursive
/// delete anywhere on this path would drop the count and fail them.
///
/// Nothing with a live owner is touched: these artifacts are how a running Desktop finds its own state, so
/// moving a live SIBLING job's lock corrupts that sibling's run.
/// </summary>
public sealed class QuarantineTests : IDisposable
{
    private readonly string _scratch = NewScratch();

    private static string NewScratch()
    {
        string root = Environment.GetEnvironmentVariable("SUPERBI_TEST_SCRATCH") is { Length: > 0 } sr ? sr : Path.GetTempPath();
        string dir = Path.Combine(root, "quarantine-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Dispose()
    {
        // Only the directory this test itself created, under a name no other run holds.
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private string File_(string name, string content = "lock")
    {
        string path = Path.Combine(_scratch, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, content);
        return path;
    }

    /// <summary>A real same-directory rename, the way <see cref="Quarantine"/>'s production move works.</summary>
    private static bool RealMove(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else System.IO.File.Move(from, to);
        return true;
    }

    private int FileCount() => Directory.GetFiles(_scratch, "*", SearchOption.AllDirectories).Length;

    // ---------------- the move ----------------

    [Fact]
    public void Sweep_RenamesADeadLock_ToAQuarantineNameInTheSameDirectory()
    {
        string jobId = JobId.New();
        string lockPath = File_(Path.Combine("EBWebView", "LOCK"));
        var candidate = new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock");

        var actions = Quarantine.Sweep(jobId, new[] { candidate }, _ => false, RealMove);

        var action = Assert.Single(actions);
        Assert.Equal("moved", action.Outcome);
        Assert.Equal(lockPath + ".quarantine-" + jobId, action.To);
        Assert.False(System.IO.File.Exists(lockPath));
        Assert.True(System.IO.File.Exists(action.To!));

        // Same directory both sides: the rename is a metadata operation that can never degrade into a
        // copy-then-delete across volumes.
        Assert.Equal(Path.GetDirectoryName(lockPath), Path.GetDirectoryName(action.To!));
    }

    [Fact]
    public void Sweep_MovesADeadWorkspaceDirectory_WithItsContentsIntact()
    {
        string jobId = JobId.New();
        string workspace = Path.Combine(_scratch, "AnalysisServicesWorkspaces", "ws1");
        Directory.CreateDirectory(workspace);
        File_(Path.Combine("AnalysisServicesWorkspaces", "ws1", "msmdsrv.port.txt"), "55001");
        int before = FileCount();

        var actions = Quarantine.Sweep(jobId, new[] { new QuarantineCandidate(workspace, IsDirectory: true, "workspace") },
                                       _ => false, RealMove);

        Assert.Equal("moved", Assert.Single(actions).Outcome);
        Assert.Equal(before, FileCount());   // moved aside, not deleted
        Assert.True(System.IO.File.Exists(Path.Combine(workspace + ".quarantine-" + jobId, "msmdsrv.port.txt")));
    }

    // ---------------- the live owner ----------------

    [Fact]
    public void Sweep_SkipsAnArtifactWithALiveOwner_SoASiblingJobIsNotCorrupted()
    {
        string jobId = JobId.New();
        string lockPath = File_(Path.Combine("EBWebView", "LOCK"));
        var candidate = new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock");
        bool moved = false;

        // A sibling job's Desktop is running and holds this lock. It is how that Desktop finds its own state.
        var actions = Quarantine.Sweep(jobId, new[] { candidate }, _ => true, (_, _) => { moved = true; return true; });

        var action = Assert.Single(actions);
        Assert.Equal("skipped-live", action.Outcome);
        Assert.Null(action.To);
        Assert.False(moved);
        Assert.True(System.IO.File.Exists(lockPath));   // exactly where its owner left it
    }

    [Fact]
    public void Sweep_TreatsAProbeThatThrows_AsLive_BecauseItHasProvenNothing()
    {
        string jobId = JobId.New();
        string lockPath = File_(Path.Combine("EBWebView", "LOCK"));
        bool moved = false;

        var actions = Quarantine.Sweep(jobId,
            new[] { new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock") },
            _ => throw new UnauthorizedAccessException("the probe could not open it"),
            (_, _) => { moved = true; return true; });

        // Anything that cannot be PROVEN dead is left alone. The cheap outcome is a job that fails loudly on a
        // lock it could not clear; the expensive one is a running sibling losing its state.
        Assert.Equal("skipped-live", Assert.Single(actions).Outcome);
        Assert.False(moved);
        Assert.True(System.IO.File.Exists(lockPath));
    }

    [Fact]
    public void HasLiveOwner_OfAFileThisTestHoldsOpen_IsTrue()
    {
        string lockPath = File_("held.lock");
        using var held = new FileStream(lockPath, FileMode.Open, FileAccess.Read, FileShare.Read);

        // Off Windows the probes do not mean what they mean here and everything reads live, which is the same
        // verdict this asserts - so the claim holds on either host.
        Assert.True(Quarantine.HasLiveOwner(new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock")));
    }

    // ---------------- failure is recorded, never escalated ----------------

    [Fact]
    public void Sweep_RecordsAMoveThatRefuses_AndLeavesTheOriginalInPlace()
    {
        string jobId = JobId.New();
        string lockPath = File_(Path.Combine("EBWebView", "LOCK"));

        var actions = Quarantine.Sweep(jobId, new[] { new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock") },
                                       _ => false, (_, _) => false);

        Assert.Equal("move-failed:refused", Assert.Single(actions).Outcome);
        Assert.True(System.IO.File.Exists(lockPath));   // no delete fallback: the file stays put
    }

    [Fact]
    public void Sweep_RecordsAMoveThatThrows_ByTypeAndLeavesTheOriginalInPlace()
    {
        string jobId = JobId.New();
        string lockPath = File_(Path.Combine("EBWebView", "LOCK"));

        var actions = Quarantine.Sweep(jobId, new[] { new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock") },
                                       _ => false, (_, _) => throw new IOException("the file is in use by another process"));

        // The type, not the message: the message carries paths and is culture-dependent.
        Assert.Equal("move-failed:IOException", Assert.Single(actions).Outcome);
        Assert.True(System.IO.File.Exists(lockPath));
    }

    [Fact]
    public void Sweep_RecordsAMissingCandidate_RatherThanThrowing()
    {
        var actions = Quarantine.Sweep(JobId.New(),
            new[] { new QuarantineCandidate(Path.Combine(_scratch, "never-existed.lock"), IsDirectory: false, "wv2-lock") },
            _ => false, RealMove);

        Assert.Equal("skipped-missing", Assert.Single(actions).Outcome);
    }

    [Fact]
    public void Sweep_CarriesOnPastOneFailure_SoOneStuckLockDoesNotStrandTheRest()
    {
        string jobId = JobId.New();
        string stuck = File_("a.lock");
        string clean = File_("b.lock");

        var actions = Quarantine.Sweep(jobId,
            new[]
            {
                new QuarantineCandidate(stuck, IsDirectory: false, "wv2-lock"),
                new QuarantineCandidate(clean, IsDirectory: false, "wv2-lock"),
            },
            _ => false,
            (from, to) => from == stuck ? throw new IOException("held") : RealMove(from, to));

        Assert.Equal(2, actions.Count);
        Assert.Equal("move-failed:IOException", actions[0].Outcome);
        Assert.Equal("moved", actions[1].Outcome);
    }

    // ---------------- the anti-recursive-delete guard ----------------

    [Fact]
    public void Sweep_ConservesEveryFileUnderTheTree_WhateverEachCandidatesOutcome()
    {
        string jobId = JobId.New();
        string dead = File_(Path.Combine("EBWebView", "LOCK"));
        string live = File_(Path.Combine("TempSaves", "held.tmp"));
        string refused = File_(Path.Combine("AutoRecovery", "a.lock"));
        string missing = Path.Combine(_scratch, "gone.lock");
        int before = FileCount();

        var actions = Quarantine.Sweep(jobId,
            new[]
            {
                new QuarantineCandidate(dead, IsDirectory: false, "wv2-lock"),
                new QuarantineCandidate(live, IsDirectory: false, "tempsave"),
                new QuarantineCandidate(refused, IsDirectory: false, "autorecovery-lock"),
                new QuarantineCandidate(missing, IsDirectory: false, "wv2-lock"),
            },
            c => c.Path == live,
            (from, to) => from != refused && RealMove(from, to));

        // Every branch of the sweep ran - moved, skipped-live, move-failed, skipped-missing - and the file
        // count is UNCHANGED. Nothing on this path may delete; a recursive delete would drop this count.
        Assert.Equal(new[] { "moved", "skipped-live", "move-failed:refused", "skipped-missing" },
                     actions.Select(a => a.Outcome).ToArray());
        Assert.Equal(before, FileCount());
        Assert.True(System.IO.File.Exists(dead + ".quarantine-" + jobId));
        Assert.True(System.IO.File.Exists(live));
        Assert.True(System.IO.File.Exists(refused));
    }

    [Fact]
    public void Sweep_OfTheSameTreeTwice_DoesNotReQuarantineItsOwnOutput()
    {
        string jobId = JobId.New();
        string lockPath = File_(Path.Combine("EBWebView", "LOCK"));
        int before = FileCount();

        Quarantine.Sweep(jobId, new[] { new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock") }, _ => false, RealMove);
        var second = Quarantine.Sweep(JobId.New(), new[] { new QuarantineCandidate(lockPath, IsDirectory: false, "wv2-lock") }, _ => false, RealMove);

        Assert.Equal("skipped-missing", Assert.Single(second).Outcome);
        Assert.Equal(before, FileCount());
    }

    // ---------------- enumeration ----------------

    [Fact]
    public void Enumerate_OverRootsThatDoNotExist_IsEmptyRatherThanAThrow()
    {
        // A scaffold-only box, a fresh container, a host with no Power BI installed at all: a launch must not
        // fail because there was nothing to tidy.
        Assert.Empty(Quarantine.Enumerate(Path.Combine(_scratch, "no-localappdata"), Path.Combine(_scratch, "no-profile")));
        Assert.Empty(Quarantine.Enumerate("", ""));
        Assert.Empty(Quarantine.Enumerate("   ", "   "));
    }

    [Fact]
    public void Enumerate_FindsTheDesktopArtifacts_AndClassifiesThem()
    {
        string localAppData = Path.Combine(_scratch, "LocalAppData");
        string root = Path.Combine(localAppData, "Microsoft", "Power BI Desktop");
        Directory.CreateDirectory(Path.Combine(root, "TempSaves"));
        System.IO.File.WriteAllText(Path.Combine(root, "TempSaves", "a.tmp"), "x");
        Directory.CreateDirectory(Path.Combine(root, "AnalysisServicesWorkspaces", "ws1"));
        Directory.CreateDirectory(Path.Combine(root, "User", "EBWebView", "Default"));
        System.IO.File.WriteAllText(Path.Combine(root, "User", "EBWebView", "Default", "LOCK"), "x");
        Directory.CreateDirectory(Path.Combine(root, "AutoRecovery"));
        System.IO.File.WriteAllText(Path.Combine(root, "AutoRecovery", "a.lock"), "x");

        var found = Quarantine.Enumerate(localAppData, Path.Combine(_scratch, "no-profile"));

        Assert.Contains(found, c => c.Kind == "tempsave" && !c.IsDirectory);
        Assert.Contains(found, c => c.Kind == "workspace" && c.IsDirectory);
        Assert.Contains(found, c => c.Kind == "wv2-lock");
        Assert.Contains(found, c => c.Kind == "autorecovery-lock");
        Assert.Equal(found.Select(c => c.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count(), found.Count);
    }

    [Fact]
    public void Enumerate_IgnoresAnEarlierSweepsOutput()
    {
        string localAppData = Path.Combine(_scratch, "LocalAppData");
        string autoRecovery = Path.Combine(localAppData, "Microsoft", "Power BI Desktop", "AutoRecovery");
        Directory.CreateDirectory(autoRecovery);
        System.IO.File.WriteAllText(Path.Combine(autoRecovery, "a.lock.quarantine-" + JobId.New()), "x");

        // An earlier sweep's output is not an input to the next one, or each job would re-quarantine the last.
        Assert.Empty(Quarantine.Enumerate(localAppData, Path.Combine(_scratch, "no-profile")));
    }

    // ---------------- the real entry: profile roots and service mode ----------------
    // These pin the confirmed finding "quarantine sweep is dead code + wrong profile in service mode":
    // SweepReal is now wired into DesktopSession.Launch, its profile root is a parameter, and a service
    // session with no explicit root logs and no-ops instead of sweeping systemprofile's empty tree.

    [Fact]
    public void ResolveRoots_AnExplicitProfileRoot_WinsEvenInAServiceSession()
    {
        // The engine may run as a service while Desktop runs in the console session: a caller that knows the
        // console profile passes it, and it is honoured regardless of the caller's own session.
        var roots = Quarantine.ResolveRoots(@"C:\Users\console-op", @"X:\wrong", @"X:\wrong-too", serviceSession: true);

        Assert.NotNull(roots);
        Assert.Equal(@"C:\Users\console-op", roots!.Value.userProfile);
        Assert.Equal(Path.Combine(@"C:\Users\console-op", "AppData", "Local"), roots.Value.localAppData);
    }

    [Fact]
    public void ResolveRoots_AServiceSessionWithNoExplicitRoot_RefusesToNameAProfile()
    {
        // Session 0's own profile is systemprofile - not where Desktop runs. Sweeping it would enumerate an
        // empty tree and truthfully report "nothing to do" while protecting nothing.
        Assert.Null(Quarantine.ResolveRoots(null,
            @"C:\Windows\System32\config\systemprofile\AppData\Local",
            @"C:\Windows\System32\config\systemprofile",
            serviceSession: true));
    }

    [Fact]
    public void ResolveRoots_AnInteractiveSessionWithNoExplicitRoot_UsesTheCallersOwnProfile()
    {
        var roots = Quarantine.ResolveRoots(null, @"C:\Users\op\AppData\Local", @"C:\Users\op", serviceSession: false);

        Assert.Equal((@"C:\Users\op\AppData\Local", @"C:\Users\op"), roots);
    }

    [Fact]
    public void SweepReal_InAServiceSessionWithNoExplicitRoot_LogsAndSweepsNothing()
    {
        var lines = new List<string>();

        var actions = Quarantine.SweepReal(JobId.New(), profileRoot: null, log: lines.Add, serviceSession: true);

        Assert.Empty(actions);
        Assert.Contains(lines, l => l.Contains("systemprofile"));
    }

    [Fact]
    public void SweepReal_WithAnExplicitProfileRoot_SweepsThatProfilesDesktopTree()
    {
        // The end-to-end wiring the finding said was dead code, against a fabricated profile on scratch.
        // serviceSession:true doubles as the fail-safe: if explicit-root handling ever regressed, this would
        // no-op (and fail the assert) rather than touch the developer's real profile.
        if (!OperatingSystem.IsWindows()) return;   // the real HasLiveOwner probes only prove death on Windows

        string jobId = JobId.New();
        string stale = File_(Path.Combine("AppData", "Local", "Microsoft", "Power BI Desktop", "TempSaves", "stale.pbix"));

        var actions = Quarantine.SweepReal(jobId, profileRoot: _scratch, log: _ => { }, serviceSession: true);

        Assert.Contains(actions, a => a.From == stale && a.Outcome == "moved");
        Assert.False(System.IO.File.Exists(stale));
        Assert.True(System.IO.File.Exists(stale + ".quarantine-" + jobId));
    }
}
