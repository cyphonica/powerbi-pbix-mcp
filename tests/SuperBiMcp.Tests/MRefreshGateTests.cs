using SuperBiMcp.Services;
using Xunit;

namespace SuperBiMcp.Tests;

/// <summary>
/// THE canary for the silent-revert bug class. An M (Power Query) edit lands live via one tool and the save is
/// dispatched by a DIFFERENT tool; before the gate nothing connected them, so an unrefreshed partition reached
/// the .pbix carrying the OLD M text beside the new data and detonated on the next full refresh.
///
/// <see cref="ModelPersistService"/>'s save path delegates this exact decision to
/// <see cref="MRefreshGate.EnsureFullRefreshBeforeSave(MDirtyTracker, Action, Microsoft.Extensions.Logging.ILogger?)"/>,
/// the pure overload these assertions drive, so they cover the real gate rather than a copy of its shape.
///
/// Both directions are proven deliberately: a gate that refuses everything is not a gate, and a gate that
/// refuses nothing is the bug it exists to stop.
/// </summary>
public sealed class MRefreshGateTests
{
    [Fact]
    public void Canary_PartitionMEditWhoseForcedFullRefreshFails_IsRefusedBeforeAnySaveIsDispatched()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("set_partition_m Sales/Partition");      // the canary: an M edit is pending, unrefreshed
        bool saveWasDispatched = false;

        var ex = Assert.Throws<MRefreshRequiredException>(() =>
        {
            MRefreshGate.EnsureFullRefreshBeforeSave(
                dirty,
                fullRefresh: () => throw new Exception("OLE DB or ODBC error: the source file could not be found."),
                log: null);
            saveWasDispatched = true;                        // must be UNREACHABLE
        });

        Assert.False(saveWasDispatched);                     // fail-closed: no refresh => no save
        Assert.True(dirty.IsDirty);                          // the flag is NOT cleared on failure - a retry must re-refresh
        Assert.Contains("the save was refused", ex.Message);
        Assert.Contains("the .pbix on disk is unchanged", ex.Message);
        Assert.Contains("OLE DB or ODBC error", ex.Message); // the engine's real reason survives
    }

    [Fact]
    public void Canary_TheRefusalCannotBeMisroutedByFixModelsCatchFilter()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("group_by Sales/Partition");
        var ex = Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => throw new Exception("boom"), null));

        // ModelPersistService.FixModel catches `InvalidOperationException when ex.Message.Contains("Power Query (M)")`
        // and downgrades it to a soft ok=false advisory. A hard failure must escape that filter on BOTH counts.
        Assert.IsNotType<InvalidOperationException>(ex);
        Assert.DoesNotContain("Power Query (M)", ex.Message);
    }

    [Fact]
    public void Canary_TheEnginesFailureIsKeptAsTheInnerException_NotFlattenedIntoText()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("set_partition_m Sales/Partition");
        var engineFailure = new TimeoutException("the refresh timed out");

        var ex = Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => throw engineFailure, null));

        Assert.Same(engineFailure, ex.InnerException);
    }

    [Fact]
    public void Gate_RunsTheFullRefreshAndClearsTheFlag_WhenAnMEditIsPending()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("set_shared_expression SourceFolder");
        int refreshes = 0;

        var outcome = MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => refreshes++, null);

        Assert.Equal(MRefreshOutcome.Ran, outcome);
        Assert.Equal(1, refreshes);
        Assert.False(dirty.IsDirty);
        Assert.Empty(dirty.Reasons);
    }

    [Fact]
    public void Gate_IsANoOpAndCostsNothing_WhenNoMEditIsPending()
    {
        var dirty = new MDirtyTracker();
        int refreshes = 0;

        Assert.Equal(MRefreshOutcome.NotNeeded, MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => refreshes++, null));
        Assert.Equal(0, refreshes);   // a measure-only edit must not become a minutes-long source re-import
    }

    [Fact]
    public void Gate_CoversEveryPowerQueryTransform_NotJustSetPartitionM()
    {
        // ModelService.AppendTransform is the shared mutator behind ~40 tools; it marks the SAME flag.
        foreach (string reason in new[] { "merge_queries Sales/P", "pivot_column Sales/P", "fill_down Sales/P", "set_shared_expression Root" })
        {
            var dirty = new MDirtyTracker();
            dirty.Mark(reason);
            Assert.Throws<MRefreshRequiredException>(() =>
                MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => throw new Exception("x"), null));
        }
    }

    [Fact]
    public void Gate_NamesEveryPendingEdit_SoTheOperatorKnowsWhatIsUnsaved()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("set_partition_m Sales/Partition");
        dirty.Mark("fill_down Sales/Partition");

        var ex = Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => throw new Exception("x"), null));

        Assert.Contains("set_partition_m Sales/Partition", ex.Message);
        Assert.Contains("fill_down Sales/Partition", ex.Message);
    }

    [Fact]
    public void Gate_ReRefreshesOnRetry_AfterAFailureLeftTheFlagStanding()
    {
        var dirty = new MDirtyTracker();
        dirty.Mark("set_partition_m Sales/Partition");
        int refreshes = 0;

        // A first save is refused because the source was unreachable.
        Assert.Throws<MRefreshRequiredException>(() =>
            MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => { refreshes++; throw new Exception("source offline"); }, null));

        // The operator fixes the source and retries: the gate must run the refresh AGAIN, not wave the save
        // through on the strength of the attempt that failed.
        Assert.Equal(MRefreshOutcome.Ran, MRefreshGate.EnsureFullRefreshBeforeSave(dirty, () => refreshes++, null));
        Assert.Equal(2, refreshes);
        Assert.False(dirty.IsDirty);
    }
}
