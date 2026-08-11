using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// xUnit serialisation barrier for the test classes that mutate the SHARED static Phase 0 jobs root
/// (<c>JobPaths.RootForTest</c>, and the SUPERBI_JOBS_ROOT / SUPERBI_JOBROOT process environment behind it).
/// This is a DIFFERENT static from the job-root static behind <see cref="JobRootCollection"/>, so it gets its
/// own collection - sharing that one would chain these classes behind an unrelated
/// suite for no safety gain. The hazard is the same: xUnit runs different collections in PARALLEL, and any
/// class that points the one global jobs root at its own temp dir and resets it on Dispose would otherwise
/// have its root moved out from under it by another class's teardown mid-test. Failures are intermittent and
/// ordering-dependent: they pass locally and flake in CI.
/// </summary>
[CollectionDefinition(Name)]
public sealed class JobsRootCollection
{
    public const string Name = "jobs-root-serial";
}
