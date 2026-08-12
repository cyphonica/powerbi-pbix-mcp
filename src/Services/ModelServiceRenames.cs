using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>
/// Wave G4 propagating renames: rename_table / rename_column / set_measure_properties(newName) with
/// propagate=true, and apply_rename_plan. One orchestration
/// (<see cref="ApplyPropagatingRenames"/>) drives every path: the report side is rewritten and
/// flushed FIRST (fully restorable - in-memory snapshot for sessions, file backup for a PBIR path),
/// then the model side runs under the G1 write-transaction so ONE SaveChanges applies the renames
/// plus every DAX/M reference rewrite; any failure rolls the model back AND restores the report.
/// </summary>
public sealed partial class ModelService
{
    public object RenameTablePropagating(string sessionId, string table, string newName, IReportRenameTarget? report)
        => ApplyPropagatingRenames(sessionId, new[] { new RenameSpec("table", null, table, newName) }, report, null);

    public object RenameColumnPropagating(string sessionId, string table, string column, string newName, IReportRenameTarget? report)
        => ApplyPropagatingRenames(sessionId, new[] { new RenameSpec("column", table, column, newName) }, report, null);

    /// <summary>The propagating measure rename. hidden / description ride along inside the same
    /// transaction (applied AFTER the rename, so the measure is addressed by its new name).</summary>
    public object SetMeasurePropertiesPropagating(string sessionId, string table, string measure,
        bool? hidden, string? description, string newName, IReportRenameTarget? report)
        => ApplyPropagatingRenames(sessionId, new[] { new RenameSpec("measure", table, measure, newName) }, report,
            hidden != null || description != null
                ? m => SetMeasurePropertiesCore(m, table, newName, hidden, description, null)
                : null);

    /// <summary>apply_rename_plan: consume an audit_naming rename plan through the propagating
    /// machinery. Rows that cannot apply (missing object, collision, malformed) are skipped and
    /// reported; the survivors are applied as ONE atomic batch. One result row per rename.</summary>
    public object ApplyRenamePlan(string sessionId, string planJson, IReportRenameTarget? report)
    {
        var session = _sessions.GetModel(sessionId);
        var (specs, rejected) = RenamePlan.Parse(planJson);
        var (valid, skipped) = RenamePlan.Validate(session.Model, specs);

        var rows = new List<object>();
        foreach (var r in rejected) rows.Add(new { applied = false, detail = r });
        foreach (var s in skipped) rows.Add(new { applied = false, detail = s });

        object? applyResult = null;
        if (valid.Count > 0)
        {
            applyResult = ApplyPropagatingRenames(sessionId, valid, report, null);
            foreach (var s in valid)
                rows.Add(new { applied = true, objectType = s.ObjectType, table = s.Table, oldName = s.OldName, newName = s.NewName });
        }
        return new
        {
            ok = true,
            requested = specs.Count + rejected.Count,
            applied = valid.Count,
            skipped = rows.Count - valid.Count,
            rows,
            propagation = applyResult,
            note = valid.Count == 0
                ? "No valid renames in the plan - every row was rejected or skipped (see rows for reasons)."
                : "Applied rows ran as one atomic batch: TOM renames + every DAX/M reference rewrite in a single SaveChanges"
                  + (report != null ? ", with the report bindings rewritten through the supplied reportSource." : "; no reportSource was supplied, so report bindings were not touched."),
        };
    }

    /// <summary>
    /// The single propagating-rename engine. Order of operations (all-or-nothing):
    ///  1. validate the batch and build the rename maps from the CURRENT (old-name) model;
    ///  2. report side: snapshot -> rewrite -> flush (a PBIR path writes to disk with a backup;
    ///     session targets stay in memory) - any failure restores the report and aborts untouched;
    ///  3. model side under the G1 transaction: rewrite every DAX site, apply the TOM renames,
    ///     rewrite M query references, then ONE SaveChanges. A failure rolls the model back via
    ///     Model.UndoLocalChanges AND restores the report. When the caller already holds an open
    ///     model transaction the changes JOIN it (their commit applies them) - noted in the result.
    /// </summary>
    public object ApplyPropagatingRenames(string sessionId, IReadOnlyList<RenameSpec> specs,
        IReportRenameTarget? report, Action<TOM.Model>? extraEdit)
    {
        var session = _sessions.GetModel(sessionId);
        var model = session.Model;

        var set = RenameSet.Build(model, specs);
        var wiring = ModelRenamer.WiringSummary(model, set);
        var (fieldMap, entityMap) = ModelRenamer.BuildReportMaps(model, set);

        // ---- report first: everything it does is restorable, so a later model failure can undo it ----
        object? reportResult = null;
        bool reportTouched = false;
        if (report != null)
        {
            report.Snapshot();
            try
            {
                reportResult = report.Rewrite(fieldMap, entityMap);
                report.Flush();
                reportTouched = true;
            }
            catch
            {
                report.Restore();
                throw;
            }
        }

        // ---- model side under the write-transaction (G1 machinery) ----
        bool joined = ModelTxn.For(model) != null;
        if (!joined) ModelTxn.Begin(model);
        ModelDaxRewriteResult dax;
        List<string> applied;
        int mSites;
        List<string> partitionsRenamed;
        try
        {
            dax = ModelRenamer.RewriteModelDax(model, set);       // host tables still carry old names
            applied = ModelRenamer.ApplyTomRenames(model, set);
            (mSites, partitionsRenamed) = ModelRenamer.RewriteModelM(model, set);
            extraEdit?.Invoke(model);
            if (!joined) ModelTxn.Commit(model);                  // the single SaveChanges
        }
        catch
        {
            if (!joined)
            {
                try { ModelTxn.Rollback(model); }
                catch { /* rollback after a failed commit is best effort - the exception below is the story */ }
            }
            if (reportTouched)
            {
                try { report!.Restore(); }
                catch { /* report restore is best effort; the backups remain on disk */ }
            }
            throw;
        }
        if (mSites > 0)
            session.MDirty.Mark($"rename propagation rewrote {mSites} M reference site(s)");

        return new
        {
            ok = true,
            renames = applied,
            propagated = true,
            transaction = joined
                ? "joined-open-transaction (commit_model_transaction applies the model side)"
                : "committed (one SaveChanges for the renames and every rewrite)",
            committedToLiveModel = !joined,
            persistedToDisk = false,
            daxSitesRewritten = dax.Total,
            daxSites = dax.Categories,
            daxSiteList = dax.Sites,
            mSitesRewritten = mSites,
            partitionsRenamed,
            wiring,
            report = report == null
                ? (object)new
                {
                    rewritten = false,
                    note = "No reportSource supplied - report bindings still reference the old names. Pass reportSource "
                         + "(a reportSessionId, a pbirSessionId, or a PBIR .pbix/PBIP path) to rewrite them, or repair later "
                         + "with scan_broken_refs + fix_broken_visuals.",
                }
                : new { rewritten = true, kind = report.Kind, persistence = report.PersistenceNote, result = reportResult },
            actionRequired = "Save in Power BI Desktop (File > Save) to persist the model to the .pbix.",
            note = "Standalone UNQUOTED table references in DAX (e.g. ALL ( Sales ) with no quotes or brackets) are not "
                 + "rewritten - a bare word could be a VAR or function name. Quoted and bracketed forms are all rewritten.",
        };
    }
}
