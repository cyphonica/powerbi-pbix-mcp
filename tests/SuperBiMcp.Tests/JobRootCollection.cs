using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// xUnit serialisation barrier for the test classes that mutate the SHARED static job root.
/// xUnit runs different test collections in PARALLEL, but these classes all
/// point the one global job root at their own temp dir and reset it on Dispose, so running two of them at once
/// would let one class's teardown move the root out from under another mid-test. Placing them in ONE named
/// collection forces them to run sequentially with respect to each other (tests within a collection never run
/// in parallel), while the rest of the suite still parallelises freely.
/// </summary>
[CollectionDefinition(Name)]
public sealed class JobRootCollection
{
    public const string Name = "job-root-serial";
}
