using Microsoft.Extensions.Logging;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

internal enum MRefreshOutcome { NotNeeded, Ran }

/// <summary>Public so PersistTools' J.Try surfaces it as {ok:false,error}. Deliberately NOT an
/// InvalidOperationException: ModelPersistService.FixModel catches
/// `InvalidOperationException when ex.Message.Contains("Power Query (M)")` and would misroute a hard failure
/// into its soft `route:"needs_desktop"` advisory.</summary>
public sealed class MRefreshRequiredException : Exception
{
    public MRefreshRequiredException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The fail-closed gate between an M (Power Query) edit and a save. An M edit lands live via one tool
/// and the save is dispatched by a DIFFERENT tool; an unrefreshed partition that reaches the .pbix keeps the OLD
/// M text alongside the new data and detonates on the next full refresh. The gate fires at SAVE time, not at edit
/// time, so a measure-only edit never pays for a source re-import.</summary>
internal static class MRefreshGate
{
    internal static MRefreshOutcome EnsureFullRefreshBeforeSave(ModelSession session, ILogger? log) =>
        EnsureFullRefreshBeforeSave(
            // session.MDirty is the ENGINE-keyed flag (MDirtyRegistry: endpoint ?? "local:"+port, database
            // name), not per-session state - so the gate a reconnect or second session runs still sees the
            // first session's pending edits, and a successful full refresh here clears the flag for EVERY
            // session on that engine and releases the registry entry.
            session.MDirty,
            // SaveChanges() after RequestRefresh(Full) blocks until the refresh actually completes, which is what
            // makes "the refresh ran" provable rather than "no exception escaped".
            () => { session.Database.Model.RequestRefresh(TOM.RefreshType.Full); session.Database.Model.SaveChanges(); },
            log);

    /// <summary>The PURE, unit-testable overload. No TOM, no Desktop, no session.</summary>
    internal static MRefreshOutcome EnsureFullRefreshBeforeSave(MDirtyTracker dirty, Action fullRefresh, ILogger? log)
    {
        if (!dirty.IsDirty) return MRefreshOutcome.NotNeeded;
        try { fullRefresh(); }
        catch (Exception ex)
        {
            log?.LogWarning(ex, "forced full refresh before save failed; the save was refused");
            // the wording must never contain the literal `Power Query (M)` - FixModel filters on that substring.
            throw new MRefreshRequiredException(
                $"an M (Power Query) change is pending ({string.Join("; ", dirty.Reasons)}) and the required full refresh FAILED, "
              + $"so the save was refused and the .pbix on disk is unchanged. Fix the source/credentials, call "
              + $"refresh_table(full: true), then save. Engine said: {ex.Message}", ex);
        }
        // deliberately not cleared on failure: a retry must re-run the refresh. On a registry-backed
        // tracker this successful clear is ALSO what releases the engine's MDirtyRegistry entry - the
        // gate's proven full refresh is the only thing that retires a pending-M flag.
        dirty.Clear("full refresh before save");
        return MRefreshOutcome.Ran;
    }
}
