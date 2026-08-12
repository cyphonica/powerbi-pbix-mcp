using System.Runtime.CompilerServices;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>State of one open model write-transaction. Created by <see cref="ModelTxn.Begin"/> and held
/// until commit/rollback. Thread-safe: tool calls on the same session can arrive from parallel chats.</summary>
public sealed class ModelTransactionState
{
    public DateTime OpenedUtc { get; } = DateTime.UtcNow;

    private int _deferredSaves;

    /// <summary>How many gated SaveChanges calls have been deferred since the transaction opened.</summary>
    public int DeferredSaves => Volatile.Read(ref _deferredSaves);

    public void RecordDeferredSave() => Interlocked.Increment(ref _deferredSaves);
}

/// <summary>
/// The model write-transaction gate. While a transaction is open on a session's live TOM model, every
/// ModelService mutation still edits the object tree but its trailing SaveChanges is DEFERRED - nothing
/// reaches the engine until commit_model_transaction calls SaveChanges once (rollback discards the local
/// changes via Model.UndoLocalChanges). Keyed by the TOM <see cref="TOM.Model"/> INSTANCE, which is 1:1
/// with a ModelSession (two sessions on the same engine each carry their own Model object with its own
/// local-change tracking, so per-model is exactly per-session). ConditionalWeakTable so a disposed
/// session's entry never pins the model graph.
/// </summary>
public static class ModelTxn
{
    private static readonly ConditionalWeakTable<TOM.Model, ModelTransactionState> Open = new();

    /// <summary>The open transaction on this model, or null when none.</summary>
    public static ModelTransactionState? For(TOM.Model model) =>
        Open.TryGetValue(model, out var s) ? s : null;

    public static ModelTransactionState Begin(TOM.Model model)
    {
        lock (Open)
        {
            if (Open.TryGetValue(model, out _))
                throw new InvalidOperationException(
                    "A model transaction is already open on this session. Commit or roll it back first.");
            var state = new ModelTransactionState();
            Open.Add(model, state);
            return state;
        }
    }

    /// <summary>Commit: one real SaveChanges for everything accumulated. If SaveChanges throws, the
    /// transaction STAYS open so the caller can fix the model or roll back - nothing is half-committed.</summary>
    public static int Commit(TOM.Model model) => CommitWith(model, () => model.SaveChanges());

    /// <summary>Rollback: discard every accumulated local change without touching the engine.</summary>
    public static int Rollback(TOM.Model model) => RollbackWith(model, () => model.UndoLocalChanges());

    /// <summary>The gated save every ModelService mutation ends with. Returns true when SaveChanges
    /// actually ran, false when a transaction deferred it (callers that chain post-save work - e.g. the
    /// refresh gate clearing its dirty flag - must consult the result).</summary>
    public static bool Save(TOM.Model model) => GateSave(For(model), () => model.SaveChanges());

    // ---- seams (pure decision logic, unit-testable with a fake save hook) ----

    internal static bool GateSave(ModelTransactionState? txn, Action save)
    {
        if (txn != null)
        {
            txn.RecordDeferredSave();
            return false;
        }
        save();
        return true;
    }

    internal static int CommitWith(TOM.Model model, Action save)
    {
        var state = For(model) ?? throw new InvalidOperationException(
            "No open model transaction on this session. Call begin_model_transaction first.");
        save();                       // may throw - the transaction then stays open
        lock (Open) Open.Remove(model);
        return state.DeferredSaves;
    }

    internal static int RollbackWith(TOM.Model model, Action undo)
    {
        var state = For(model) ?? throw new InvalidOperationException(
            "No open model transaction on this session. Call begin_model_transaction first.");
        undo();                       // may throw - the transaction then stays open
        lock (Open) Open.Remove(model);
        return state.DeferredSaves;
    }
}
