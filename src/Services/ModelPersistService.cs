using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TOM = Microsoft.AnalysisServices.Tabular;

namespace SuperBiMcp.Services;

/// <summary>
/// Persisting a MODEL edit (add/update measure, add/fix relationship, add calculated column) all the way to
/// a .pbix on disk - closing the gap where the existing model tools only commit to the LIVE in-memory engine
/// and need a manual Power BI Desktop File &gt; Save.
///
/// There are exactly THREE persistence routes, and which one is possible is decided by ONE hard fact about
/// the bundled headless AS engine (proven, see <see cref="PbiEngine"/>): it can host a model whose partitions
/// are engine-native (M-FREE), but it CANNOT host a model with Power Query (M) import partitions (no Mashup
/// container - a raw ImageLoad fails "MEngineHelper is not loaded"). So:
///
///  1. OFFLINE IMAGE PERSIST (data-preserving, NO Desktop) - only for M-FREE models. ImageLoad the .pbix's
///     DataModel into an ephemeral bundled engine, apply the edit via TOM, RefreshType.Calculate for
///     calc-columns/relationships (recomputed from the ALREADY-LOADED VertiPaq data - no source access), then
///     ImageSave the image and repack it into the .pbix. All table data survives. <see cref="PersistOfflineImage"/>.
///
///  2. LIVE DESKTOP PERSIST (data-preserving, ANY model incl. M) - apply the edit to the connected live model
///     (SaveChanges), then drive Power BI Desktop's own File &gt; Save (scripted Ctrl+S) so Desktop writes its
///     full in-memory model+data image back to the .pbix. Requires the .pbix OPEN in Desktop. <see cref="PersistLive"/>.
///
///  3. OFFLINE STRUCTURE EXPORT (NO data) - export/deserialize the model to TMDL, apply the edit to the text,
///     re-serialize (and optionally bake a structure-only .pbix). The model STRUCTURE round-trips; import
///     (M) table DATA does not (empty until a refresh). For templates / thin models. <see cref="EditTmdlFolder"/>.
///
/// The <c>Apply*</c> object-tree mutation is shared by all three (and unit-tested against a plain
/// <c>new TOM.Model()</c>). Writes are atomic (temp file + move) and take a timestamped .bak backup first.
/// </summary>
public sealed class ModelPersistService
{
    private readonly SessionStore _sessions;
    private readonly ILogger<ModelPersistService>? _log;

    public ModelPersistService(SessionStore sessions, ILogger<ModelPersistService>? log = null)
    {
        _sessions = sessions;
        _log = log;
    }

    // ================================================================= edit model (parsing + object tree)

    /// <summary>One structural model edit. <c>op</c> = add_measure | update_measure | delete_measure |
    /// add_calculated_column | add_relationship | update_relationship | delete_relationship.</summary>
    public sealed class ModelEdit
    {
        public string Op { get; set; } = "";
        public string? Table { get; set; }
        public string? Name { get; set; }
        public string? Dax { get; set; }
        public string? FormatString { get; set; }
        public string? DisplayFolder { get; set; }
        public string? Description { get; set; }
        public string? FromTable { get; set; }
        public string? FromColumn { get; set; }
        public string? ToTable { get; set; }
        public string? ToColumn { get; set; }
        public bool? BothDirections { get; set; }
        public bool? Active { get; set; }
        public string? FromCardinality { get; set; }
        public string? ToCardinality { get; set; }
        public string? CrossFilteringBehavior { get; set; }
    }

    private static readonly JsonSerializerOptions EditJson = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Parse the edits argument - a JSON array of edit objects, or a single edit object.</summary>
    public static List<ModelEdit> ParseEdits(string editsJson)
    {
        if (string.IsNullOrWhiteSpace(editsJson))
            throw new InvalidOperationException("edits is empty - pass a JSON array of edit objects.");
        var trimmed = editsJson.TrimStart();
        List<ModelEdit>? list;
        if (trimmed.StartsWith("["))
            list = JsonSerializer.Deserialize<List<ModelEdit>>(editsJson, EditJson);
        else
        {
            var one = JsonSerializer.Deserialize<ModelEdit>(editsJson, EditJson);
            list = one == null ? null : new List<ModelEdit> { one };
        }
        if (list == null || list.Count == 0)
            throw new InvalidOperationException("no edits parsed from the edits argument.");
        foreach (var e in list)
            if (string.IsNullOrWhiteSpace(e.Op))
                throw new InvalidOperationException("every edit needs an \"op\" (e.g. add_measure, add_relationship, add_calculated_column).");
        return list;
    }

    /// <summary>The outcome of applying a batch of edits to a model's object tree.</summary>
    public sealed class ApplyResult
    {
        public List<string> Applied { get; } = new();
        /// <summary>True when a calc column or relationship was touched - the caller must recalc (offline/live)
        /// so the new indexes/values compute from the loaded data.</summary>
        public bool NeedsRecalc { get; set; }
    }

    /// <summary>
    /// Apply the edits to <paramref name="model"/>'s object tree (NO SaveChanges - the caller persists).
    /// Pure and deterministic so it can be unit-tested against an in-memory model. Reuses the proven
    /// <see cref="ModelService"/> *Core helpers where they exist.
    /// </summary>
    public static ApplyResult ApplyEdits(TOM.Model model, IReadOnlyList<ModelEdit> edits)
    {
        var res = new ApplyResult();
        foreach (var e in edits) ApplyOne(model, e, res);
        return res;
    }

    private static void ApplyOne(TOM.Model model, ModelEdit e, ApplyResult res)
    {
        switch (e.Op.Trim().ToLowerInvariant())
        {
            case "add_measure":
            {
                Require(e.Table, "table", e.Op); Require(e.Name, "name", e.Op); Require(e.Dax, "dax", e.Op);
                ModelService.AddMeasureCore(model, e.Table!, e.Name!, e.Dax!, e.FormatString, e.DisplayFolder, e.Description);
                res.Applied.Add($"add_measure {e.Table}[{e.Name}]");
                break;
            }
            case "update_measure":
            {
                Require(e.Table, "table", e.Op); Require(e.Name, "name", e.Op);
                var me = Tbl(model, e.Table!).Measures.Find(e.Name!)
                         ?? throw new InvalidOperationException($"update_measure: measure '{e.Name}' not found on '{e.Table}'.");
                if (e.Dax != null) me.Expression = e.Dax;
                if (e.FormatString != null) me.FormatString = e.FormatString;
                if (e.DisplayFolder != null) me.DisplayFolder = e.DisplayFolder;
                if (e.Description != null) me.Description = e.Description;
                res.Applied.Add($"update_measure {e.Table}[{e.Name}]");
                break;
            }
            case "delete_measure":
            {
                Require(e.Table, "table", e.Op); Require(e.Name, "name", e.Op);
                var t = Tbl(model, e.Table!);
                if (!t.Measures.Contains(e.Name!))
                    throw new InvalidOperationException($"delete_measure: measure '{e.Name}' not found on '{e.Table}'.");
                t.Measures.Remove(e.Name!);
                res.Applied.Add($"delete_measure {e.Table}[{e.Name}]");
                break;
            }
            case "add_calculated_column":
            {
                Require(e.Table, "table", e.Op); Require(e.Name, "name", e.Op); Require(e.Dax, "dax", e.Op);
                var t = Tbl(model, e.Table!);
                if (t.Columns.Contains(e.Name!))
                    throw new InvalidOperationException($"add_calculated_column: column '{e.Name}' already exists on '{e.Table}'.");
                t.Columns.Add(new TOM.CalculatedColumn { Name = e.Name!, Expression = e.Dax! });
                res.Applied.Add($"add_calculated_column {e.Table}[{e.Name}]");
                res.NeedsRecalc = true;
                break;
            }
            case "add_relationship":
            {
                Require(e.FromTable, "fromTable", e.Op); Require(e.FromColumn, "fromColumn", e.Op);
                Require(e.ToTable, "toTable", e.Op); Require(e.ToColumn, "toColumn", e.Op);
                ModelService.AddRelationshipCore(model, e.FromTable!, e.FromColumn!, e.ToTable!, e.ToColumn!,
                    e.BothDirections ?? false, e.Active ?? true, e.FromCardinality, e.ToCardinality,
                    e.CrossFilteringBehavior, null, null);
                res.Applied.Add($"add_relationship {e.FromTable}[{e.FromColumn}] -> {e.ToTable}[{e.ToColumn}]");
                res.NeedsRecalc = true;
                break;
            }
            case "update_relationship":
            {
                var rel = ModelService.UpdateRelationshipCore(model, e.Name, e.FromTable, e.FromColumn, e.ToTable, e.ToColumn,
                    e.FromCardinality, e.ToCardinality, e.CrossFilteringBehavior, null, e.Active, null);
                res.Applied.Add($"update_relationship {rel.FromTable?.Name}[{rel.FromColumn?.Name}] -> {rel.ToTable?.Name}[{rel.ToColumn?.Name}]");
                res.NeedsRecalc = true;
                break;
            }
            case "delete_relationship":
            {
                var rel = ModelService.ResolveRelationship(model, e.Name, e.FromTable, e.FromColumn, e.ToTable, e.ToColumn);
                string label = $"{rel.FromTable?.Name}[{rel.FromColumn?.Name}] -> {rel.ToTable?.Name}[{rel.ToColumn?.Name}]";
                model.Relationships.Remove(rel);
                res.Applied.Add($"delete_relationship {label}");
                res.NeedsRecalc = true;
                break;
            }
            default:
                throw new InvalidOperationException(
                    $"unknown edit op '{e.Op}'. Supported: add_measure, update_measure, delete_measure, add_calculated_column, add_relationship, update_relationship, delete_relationship.");
        }
    }

    private static TOM.Table Tbl(TOM.Model m, string name) =>
        m.Tables.Find(name) ?? throw new InvalidOperationException($"table '{name}' not found in the model.");

    private static void Require(string? v, string field, string op)
    {
        if (string.IsNullOrWhiteSpace(v)) throw new InvalidOperationException($"{op}: '{field}' is required.");
    }

    // ================================================================= route 1: offline image (M-FREE, data-preserving)

    /// <summary>
    /// Data-preserving OFFLINE persist for an M-FREE model: ImageLoad the .pbix DataModel into an ephemeral
    /// bundled engine, apply the edits, RefreshType.Calculate when a calc-column/relationship was touched
    /// (recomputed from the already-loaded VertiPaq data - no source access, so nothing is lost), ImageSave the
    /// image and repack it into the .pbix. Fails with a clear, actionable message for a model that carries M
    /// import partitions (the bundled engine cannot host it - open it in Desktop and use the live route).
    /// The .pbix must be CLOSED in Desktop. A timestamped .bak is taken and the write is atomic.
    /// </summary>
    public object PersistOfflineImage(string pbixPath, IReadOnlyList<ModelEdit> edits)
    {
        if (!File.Exists(pbixPath)) throw new FileNotFoundException($"pbix not found: {pbixPath}");
        byte[]? dm = ExtractDataModel(pbixPath);
        if (dm == null)
            throw new InvalidOperationException("this .pbix has no DataModel part (a live-connection or thin report). Nothing to persist a model edit into.");

        string enginePath = PbiEngine.ResolveEnginePath();
        if (!File.Exists(enginePath))
            throw new InvalidOperationException(
                $"Power BI engine not found at '{enginePath}' (set SUPERBI_PBI_ENGINE). The offline data-preserving route needs the bundled msmdsrv.exe.");

        using var engine = new PbiEngine(enginePath);
        engine.Start();
        string dbName = "daxops_persist_" + Guid.NewGuid().ToString("N")[..10];
        using var server = new TOM.Server();
        server.Connect(engine.ConnectionString);

        // ImageLoad is where an M-based model is rejected ("MEngineHelper is not loaded") - translate that into
        // the honest, actionable guidance rather than a raw engine error.
        try
        {
            using var ms = new MemoryStream(dm);
            server.ImageLoad(dbName, dbName, ms);
        }
        catch (Exception ex) when (IsMEngineFailure(ex))
        {
            throw new InvalidOperationException(
                "This report imports its data via Power Query (M), so the offline headless engine cannot host it " +
                "(no Mashup/M engine). The offline data-preserving route only works for M-free models. To persist a " +
                "model edit WITHOUT losing data, open the .pbix in Power BI Desktop, connect_model, and use " +
                "persist_open_model (live edit + scripted File > Save). For a structure-only rebuild (data emptied) " +
                "use edit_model_offline.");
        }

        try { server.Refresh(); } catch { /* metadata cache refresh - best effort */ }
        var db = server.Databases.FindByName(dbName)
                 ?? (server.Databases.Count > 0 ? server.Databases[0] : null)
                 ?? throw new InvalidOperationException("ImageLoad did not register a database on the engine.");
        var model = db.Model;

        var apply = ApplyEdits(model, edits);
        model.SaveChanges();
        // recalculated proves no exception escaped, not that a refresh ran - recalcError carries the swallowed
        // failure so a false never reads as benign.
        bool recalculated = false;
        string? recalcError = null;
        if (apply.NeedsRecalc)
        {
            try { model.RequestRefresh(TOM.RefreshType.Calculate); model.SaveChanges(); recalculated = true; }
            catch (Exception ex) { recalcError = ex.Message; _log?.LogWarning(ex, "Calculate after offline edit failed"); }
        }

        byte[] outDm;
        using (var outMs = new MemoryStream()) { server.ImageSave(db.Name, outMs); outDm = outMs.ToArray(); }

        try { server.Databases.FindByName(db.Name)?.Drop(); } catch { }

        string backup = Backup(pbixPath);
        RepackDataModel(pbixPath, outDm);

        return new
        {
            ok = true,
            route = "offline_image",
            pbixPath,
            persistedToDisk = true,
            dataPreserved = true,
            recalculated,
            recalcError,
            applied = apply.Applied,
            backup,
            capability = "Applied the model edit and wrote it back to the .pbix with ALL table data preserved " +
                         "(the model is engine-native / M-free, so the existing VertiPaq data was ImageLoad-ed, " +
                         "edited, and ImageSave-d intact). No Power BI Desktop required.",
            note = "The .pbix must NOT be open in Power BI Desktop during this operation.",
        };
    }

    // ================================================================= route 2: live Desktop (ANY model, data-preserving)

    /// <summary>
    /// Data-preserving persist for ANY model (including M import models): apply the edits to the connected
    /// LIVE model (SaveChanges + Calculate when needed), then drive Power BI Desktop's own File &gt; Save
    /// (scripted Ctrl+S located by the session's engine port) so Desktop writes its full in-memory model+data
    /// image back to the .pbix. Requires the .pbix OPEN in Power BI Desktop. <paramref name="pbixPath"/> lets
    /// the tool confirm the save landed (the file's LastWriteTime advances).
    /// </summary>
    public object PersistLive(string sessionId, IReadOnlyList<ModelEdit> edits, string? pbixPath, int saveRetries)
    {
        var session = _sessions.GetModel(sessionId);
        if (session.Port <= 0)
            throw new InvalidOperationException(
                "this session has no local engine port - it is an XMLA (Fabric/Service) session, not a local Power BI Desktop " +
                "model, so there is no local .pbix to save. Publish/refresh through the Service tools instead.");
        var model = session.Model;

        var apply = ApplyEdits(model, edits);
        model.SaveChanges();
        // recalculated proves no exception escaped, not that a refresh ran - recalcError carries the swallowed
        // failure so a false never reads as benign.
        bool recalculated = false;
        string? recalcError = null;
        if (apply.NeedsRecalc)
        {
            try { model.RequestRefresh(TOM.RefreshType.Calculate); model.SaveChanges(); recalculated = true; }
            catch (Exception ex) { recalcError = ex.Message; _log?.LogWarning(ex, "Calculate after live edit failed"); }
        }

        // now persist to disk via Desktop's File > Save
        var save = SaveOpenPbix(sessionId, pbixPath, saveRetries);
        return BuildPersistLiveResult(sessionId, session.Port, apply.Applied, recalculated, recalcError, save);
    }

    /// <summary>The live route's response object - an internal seam so a headless test can pin the response
    /// SHAPE without a live engine. Back-compat (I1): <c>saved</c> and <c>persistedToDisk</c> (top level, and
    /// <c>saved</c> inside <c>desktopSave</c>) are DEPRECATED aliases of <c>saveDispatched</c> - always the
    /// same value, kept because they were the pre-Phase-0 contract keys external tenant scripts read. New
    /// readers should use <c>saveDispatched</c>.</summary>
    internal static object BuildPersistLiveResult(string sessionId, int port, IReadOnlyList<string> applied,
        bool recalculated, string? recalcError, SaveOutcome save)
    {
        bool saveDispatched = save.SaveDispatched;
        return new
        {
            ok = true,
            route = "live_desktop",
            sessionId,
            port,
            committedToLiveModel = true,
            applied,
            recalculated,
            recalcError,
            saveDispatched,
            // DEPRECATED aliases of saveDispatched (the pre-Phase-0 keys) - same value, do not remove.
            saved = saveDispatched,
            persistedToDisk = saveDispatched,
            dataPreserved = true,
            desktopSave = new
            {
                saveDispatched = save.SaveDispatched,
                saved = save.SaveDispatched, // DEPRECATED alias of saveDispatched (the pre-Phase-0 key)
                desktopPid = save.DesktopPid,
                error = save.Error,
            },
            capability = saveDispatched
                ? "Ctrl+S was dispatched to the Power BI Desktop that owns this session's engine, and the .pbix " +
                  "LastWriteTime advanced. The model held all data in memory, so the save is expected to carry the " +
                  "edit WITH data. This is a dispatch confirmation from a file timestamp, not a content verification."
                : "The edit is live in the open model but scripted File>Save did not confirm. Press Ctrl+S in " +
                  "Power BI Desktop.",
            note = "Requires the .pbix to be open in Power BI Desktop (the only host that can persist a data-loaded, M-based model).",
        };
    }

    /// <summary>The outcome of <see cref="SaveOpenPbix"/>.
    /// Desktop route (<see cref="Persisted"/> null): <see cref="SaveDispatched"/> reports that Ctrl+S landed
    /// and the .pbix LastWriteTime advanced - a dispatch confirmation, not a content verification.
    /// XMLA route (<see cref="Persisted"/> = <c>"xmla-refresh"</c>): the model lives server-side, so
    /// persistence is the forced full refresh the M gate runs when a change is pending
    /// (<see cref="MRefreshRan"/> true) - <see cref="SaveDispatched"/> is always false because no window ever
    /// receives a keystroke.</summary>
    public sealed class SaveOutcome
    {
        public bool SaveDispatched { get; init; }
        public int DesktopPid { get; init; }
        public string? Error { get; init; }
        /// <summary><c>"xmla-refresh"</c> on the XMLA route; null on the local Desktop route.</summary>
        public string? Persisted { get; init; }
        /// <summary>XMLA route only: true when a pending M change made the gate run the full server-side
        /// refresh (a capacity-consuming operation the caller deserves to know about); false when the
        /// session was clean and nothing needed flushing.</summary>
        public bool MRefreshRan { get; init; }
    }

    /// <summary>Test seam for the M-refresh gate: null (production) runs the real
    /// <see cref="MRefreshGate.EnsureFullRefreshBeforeSave(ModelSession, Microsoft.Extensions.Logging.ILogger)"/>;
    /// a headless test can substitute a stub because a detached test model cannot execute a real Full refresh.</summary>
    internal Func<ModelSession, MRefreshOutcome>? MRefreshGateOverride;

    private MRefreshOutcome RunMRefreshGate(ModelSession session) =>
        MRefreshGateOverride is { } gate ? gate(session) : MRefreshGate.EnsureFullRefreshBeforeSave(session, _log);

    /// <summary>Scripted Power BI Desktop File &gt; Save for a connected model session (no edits) - the pure
    /// persist half of the live route, exposed on its own so it composes. This is the single save choke
    /// point: a pending M (Power Query) change forces a blocking full refresh FIRST, and a refresh failure
    /// refuses the save outright (<see cref="MRefreshRequiredException"/>) - a bare save here would write the
    /// OLD M text alongside the new data. The on-disk .pbix is copied to <c>{path}.presave.bak</c> before the
    /// forced refresh or Ctrl+S can touch it, and a refused save that still moved the file is rolled back
    /// from that copy (<see cref="RestoreFromBackup"/>), so the refusal's "unchanged on disk" promise is
    /// enforced rather than assumed. <c>SaveDispatched</c> reports that Ctrl+S landed and the file's
    /// LastWriteTime advanced, not that the written content was verified.
    /// XMLA (Fabric/Service) sessions (Endpoint set, Port 0) branch off FIRST: their model lives server-side,
    /// so the gate's refresh IS the persistence and none of the Desktop machinery (shield, port-ownership
    /// assert, Ctrl+S) may ever run for them - see <see cref="SaveXmlaSession"/>.</summary>
    public SaveOutcome SaveOpenPbix(string sessionId, string? pbixPath, int saveRetries)
    {
        var session = _sessions.GetModel(sessionId);

        // XMLA session: no local .pbix, no Desktop window - handled entirely on its own path so the
        // Desktop machinery below can never touch a server-side model.
        if (session.Port <= 0) return SaveXmlaSession(session);

        string path = pbixPath ?? session.PbixPath ?? "";

        // shield the on-disk file while anything in this call can write to it. Best effort: the shield is
        // additive, so failing to take it must not refuse a save that always worked without one.
        string? presaveBak = null;
        DateTime preWriteUtc = default;
        long preLength = -1;
        try
        {
            presaveBak = TakePresaveBackup(path);
            if (presaveBak != null)
            {
                var fi = new FileInfo(path);
                preWriteUtc = fi.LastWriteTimeUtc;
                preLength = fi.Length;
            }
        }
        catch (Exception ex)
        {
            _log?.LogWarning(ex, "could not take the presave backup of {Pbix}; saving without the restore shield", path);
        }

        // fail-closed: an unrefreshed M change must never be laundered onto disk by a save.
        try
        {
            RunMRefreshGate(session);
        }
        catch (MRefreshRequiredException ex)
        {
            // the failed forced refresh ran inside the Desktop that owns the file, so a partial write was
            // possible while it ran; a file that moved is put back so the refusal leaves it as it was.
            RestoreAfterFailedGatedSave(path, presaveBak, preWriteUtc, preLength, ex);
            throw;
        }

        // if we launched this Desktop, prove the port still belongs to it before we drive its window.
        if (session.LaunchedPid is { } lp) DesktopInterop.AssertPortOwnedByLaunchedPid(session.Port, lp);

        var r = DesktopInterop.SaveDesktopHostingPort(session.Port, path, saveRetries);
        return new SaveOutcome { SaveDispatched = r.saveDispatched, DesktopPid = r.desktopPid, Error = r.error };
    }

    /// <summary>"Save" for an XMLA (Fabric/Service) session. The dataset lives - and persists - server-side,
    /// so the only thing a save can mean here is flushing a pending M change through the gate's forced FULL
    /// refresh: that refresh IS the persistence over XMLA. Success is <c>Persisted = "xmla-refresh"</c> with
    /// <c>SaveDispatched = false</c> (no window, no keystroke, no presave shield - there is no local file).
    /// A FAILED refresh keeps the dirty flag (the gate never clears on failure, so a retry re-runs it) and
    /// rethrows with XMLA-specific guidance: the gate's Desktop wording ("the .pbix on disk is unchanged ...
    /// then save") is meaningless for a session with no .pbix and no save step after the refresh.</summary>
    private SaveOutcome SaveXmlaSession(ModelSession session)
    {
        MRefreshOutcome outcome;
        try
        {
            outcome = RunMRefreshGate(session);
        }
        catch (MRefreshRequiredException ex)
        {
            // the wording must never contain the literal `Power Query (M)` - FixModel filters on that substring.
            string engineSaid = ex.InnerException?.Message ?? ex.Message;
            throw new MRefreshRequiredException(
                "an M (Power Query) change is pending on this XMLA (Fabric/Service) session "
              + $"({string.Join("; ", session.MDirty.Reasons)}) and the required full server-side refresh FAILED, "
              + "so it was NOT persisted - over XMLA the refresh IS the persistence; there is no local .pbix and "
              + "no Desktop File > Save. The change stays marked as pending: fix the source/credentials and retry "
              + "save_open_pbix, or run refresh_table(full: true) against this session - over XMLA that refresh is "
              + $"the terminal step. Engine said: {engineSaid}", ex);
        }
        return new SaveOutcome
        {
            SaveDispatched = false,
            DesktopPid = 0,
            Error = null,
            Persisted = "xmla-refresh",
            MRefreshRan = outcome == MRefreshOutcome.Ran,
        };
    }

    /// <summary>The live route's presave shield: <c>{path}.presave.bak</c>. ONE well-known name per .pbix,
    /// overwritten on every save, so <see cref="RestoreFromBackup"/> can find it without bookkeeping.</summary>
    internal static string PresaveBackupPath(string pbixPath) => pbixPath + ".presave.bak";

    /// <summary>Copy the .pbix to its presave shield before anything on the live route can write to the
    /// original. Overwrites a previous shield - that file is always this code's own, never the user's.
    /// Returns the shield's path, or null when there is no file on disk to shield.</summary>
    internal static string? TakePresaveBackup(string pbixPath)
    {
        if (string.IsNullOrWhiteSpace(pbixPath) || !File.Exists(pbixPath)) return null;
        string bak = PresaveBackupPath(pbixPath);
        File.Copy(pbixPath, bak, overwrite: true);
        return bak;
    }

    /// <summary>Put the .pbix back exactly as it was when its presave shield was taken. Stage-then-swap so a
    /// crash mid-restore can never leave a truncated .pbix; the shield itself is kept (never deleted) so a
    /// second restore stays possible. False = no shield exists for this path.</summary>
    internal static bool RestoreFromBackup(string pbixPath)
    {
        string bak = PresaveBackupPath(pbixPath);
        if (!File.Exists(bak)) return false;
        string tmp = pbixPath + ".restore-" + Guid.NewGuid().ToString("N");
        File.Copy(bak, tmp, overwrite: false);
        File.Move(tmp, pbixPath, overwrite: true);
        return true;
    }

    /// <summary>Did the .pbix move on disk since the shield snapshot? A missing file counts as changed - a
    /// restore can recreate it.</summary>
    internal static bool PbixChangedSince(string pbixPath, DateTime preWriteUtc, long preLength)
    {
        if (!File.Exists(pbixPath)) return true;
        var fi = new FileInfo(pbixPath);
        return fi.LastWriteTimeUtc != preWriteUtc || fi.Length != preLength;
    }

    /// <summary>Enforce the gate refusal's "the .pbix on disk is unchanged" promise. An untouched file is
    /// left alone (a needless copy over the user's file is a write of its own); a moved or missing one is
    /// put back from the shield. A restore that itself fails must not hide behind the refusal text - it
    /// rethrows with the promise explicitly corrected.</summary>
    internal void RestoreAfterFailedGatedSave(string pbixPath, string? presaveBak, DateTime preWriteUtc, long preLength, Exception refusal)
    {
        if (presaveBak == null || !PbixChangedSince(pbixPath, preWriteUtc, preLength)) return;
        string failure;
        try
        {
            if (RestoreFromBackup(pbixPath))
            {
                _log?.LogWarning("the refused save left {Pbix} changed on disk; restored it from {Bak}", pbixPath, presaveBak);
                return;
            }
            failure = $"the presave backup '{presaveBak}' is gone";
        }
        catch (Exception rex)
        {
            _log?.LogError(rex, "restoring {Pbix} from {Bak} after a refused save failed", pbixPath, presaveBak);
            failure = rex.Message;
        }
        throw new MRefreshRequiredException(
            refusal.Message
            + $" CORRECTION: the .pbix DID change on disk during the refused save, and the automatic restore from "
            + $"'{presaveBak}' failed ({failure}) - the file may no longer match its pre-save state; restore it "
            + $"from that backup manually.",
            refusal);
    }

    // ================================================================= route 3: offline TMDL structure export (NO data)

    /// <summary>
    /// STRUCTURE-only offline edit: deserialize the TMDL model at <paramref name="tmdlFolder"/> (a
    /// <c>.../definition</c> folder or a PBIP SemanticModel folder - engine-free), apply the edits to the object
    /// tree, and re-serialize to <paramref name="outputFolder"/>. The model STRUCTURE (measures, relationships,
    /// calc columns, tables, partition definitions) round-trips exactly; NO VertiPaq data is involved (a bake of
    /// an M/import table comes back empty until a refresh). Returns what was applied plus the honest caveat.
    /// </summary>
    public object EditTmdlFolder(string tmdlFolder, string outputFolder, IReadOnlyList<ModelEdit> edits)
    {
        string defFolder = ResolveDefinitionFolder(tmdlFolder)
            ?? throw new InvalidOperationException(
                $"no TMDL definition folder found at '{tmdlFolder}' (expected model.tmdl / a definition/ folder, or a <name>.SemanticModel).");

        var model = TOM.TmdlSerializer.DeserializeModelFromFolder(defFolder);
        var apply = ApplyEdits(model, edits);

        if (Directory.Exists(outputFolder)) Directory.Delete(outputFolder, true);
        Directory.CreateDirectory(outputFolder);
        TOM.TmdlSerializer.SerializeModelToFolder(model, outputFolder);

        int files = Directory.GetFiles(outputFolder, "*.tmdl", SearchOption.AllDirectories).Length;
        return new
        {
            ok = true,
            route = "offline_tmdl",
            outputFolder,
            tmdlFiles = files,
            applied = apply.Applied,
            persistedToDisk = true,
            dataPreserved = false,
            capability = "Edited the model STRUCTURE as TMDL text and re-serialized it. Structure round-trips exactly; " +
                         "this route carries NO compressed VertiPaq data - a table's rows are only regenerated on a " +
                         "refresh (or, for a DATATABLE/engine-native partition, on a bake). For a data-preserving edit " +
                         "of a loaded report use persist_model_edit (offline, M-free) or persist_open_model (Desktop).",
        };
    }

    /// <summary>Find the TMDL <c>definition</c> folder: the folder itself if it holds *.tmdl, else a nested
    /// <c>definition</c> or a <c>*.SemanticModel/definition</c> under it.</summary>
    public static string? ResolveDefinitionFolder(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        if (Directory.EnumerateFiles(folder, "*.tmdl", SearchOption.TopDirectoryOnly).Any()) return folder;
        string def = Path.Combine(folder, "definition");
        if (Directory.Exists(def) && Directory.EnumerateFiles(def, "*.tmdl", SearchOption.TopDirectoryOnly).Any()) return def;
        foreach (var sm in Directory.GetDirectories(folder, "*.SemanticModel", SearchOption.TopDirectoryOnly))
        {
            string d = Path.Combine(sm, "definition");
            if (Directory.Exists(d) && Directory.EnumerateFiles(d, "*.tmdl", SearchOption.TopDirectoryOnly).Any()) return d;
        }
        // last resort: any definition folder anywhere under the tree that holds *.tmdl
        foreach (var d in Directory.GetDirectories(folder, "definition", SearchOption.AllDirectories))
            if (Directory.EnumerateFiles(d, "*.tmdl", SearchOption.TopDirectoryOnly).Any()) return d;
        return null;
    }

    // ================================================================= pbix DataModel plumbing

    /// <summary>Read the raw <c>DataModel</c> zip part of a .pbix, or null if it has none.</summary>
    public static byte[]? ExtractDataModel(string pbixPath)
    {
        using var zip = ZipFile.OpenRead(pbixPath);
        var e = zip.GetEntry("DataModel");
        if (e == null) return null;
        using var s = e.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Atomically replace the <c>DataModel</c> part of a .pbix with <paramref name="newBytes"/> (all other
    /// parts copied verbatim, SecurityBindings dropped, DataModel stored uncompressed) - mirrors the report
    /// Repack contract. Writes to a temp file then swaps, so a crash mid-write never corrupts the original.
    /// </summary>
    public static void RepackDataModel(string pbixPath, byte[] newBytes)
    {
        string tmp = pbixPath + ".tmp-" + Guid.NewGuid().ToString("N");
        var oic = StringComparison.OrdinalIgnoreCase;
        bool wroteDm = false;
        using (var src = ZipFile.OpenRead(pbixPath))
        using (var dstStream = new FileStream(tmp, FileMode.Create, FileAccess.Write))
        using (var dst = new ZipArchive(dstStream, ZipArchiveMode.Create))
        {
            foreach (var e in src.Entries)
            {
                if (string.Equals(e.FullName, "SecurityBindings", oic)) continue; // drop signature (Desktop re-adds)
                if (string.Equals(e.FullName, "DataModel", oic))
                {
                    var ne = dst.CreateEntry(e.FullName, CompressionLevel.NoCompression);
                    using var s = ne.Open(); s.Write(newBytes, 0, newBytes.Length);
                    wroteDm = true;
                }
                else
                {
                    var ne = dst.CreateEntry(e.FullName, CompressionLevel.Optimal);
                    using var os = e.Open(); using var ns = ne.Open(); os.CopyTo(ns);
                }
            }
            if (!wroteDm)
            {
                var ne = dst.CreateEntry("DataModel", CompressionLevel.NoCompression);
                using var s = ne.Open(); s.Write(newBytes, 0, newBytes.Length);
            }
        }
        File.Delete(pbixPath);
        File.Move(tmp, pbixPath);
    }

    /// <summary>Copy the .pbix to a timestamped sibling backup and return its path (additive - never deletes).</summary>
    public static string Backup(string pbixPath)
    {
        string bak = pbixPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
        int n = 1;
        while (File.Exists(bak)) bak = pbixPath + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (n++);
        File.Copy(pbixPath, bak, overwrite: false);
        return bak;
    }

    private static bool IsMEngineFailure(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            string m = cur.Message;
            if (m.Contains("MEngineHelper", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("M engine integration", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("MEngine", StringComparison.OrdinalIgnoreCase) ||
                m.Contains("Mashup", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ================================================================= high-level dispatcher

    /// <summary>
    /// The single "fix a measure/relationship/column in this model" entry: pick the best persistence route for
    /// <paramref name="target"/> and report what it did AND what is / isn't preserved.
    ///  - a live model <c>sessionId</c> (starts "model-") -&gt; live Desktop persist (data preserved, any model);
    ///  - a cold <c>.pbix</c> path -&gt; offline image persist (data preserved) when the model is M-free, else an
    ///    honest fallback message pointing at the Desktop route.
    /// </summary>
    public object FixModel(string target, IReadOnlyList<ModelEdit> edits, string? pbixPath, int saveRetries)
    {
        bool looksLikeSession = _sessions.Models.Any(m => m.Id == target) ||
                                (target.StartsWith("model-", StringComparison.OrdinalIgnoreCase) && !File.Exists(target));
        if (looksLikeSession)
            return PersistLive(target, edits, pbixPath, saveRetries);

        if (!File.Exists(target))
            throw new InvalidOperationException(
                $"target '{target}' is neither an open model sessionId nor an existing .pbix path.");

        // cold .pbix: try the data-preserving offline image route; on the M-engine boundary, hand back the
        // honest Desktop guidance instead of failing opaquely.
        try
        {
            return PersistOfflineImage(target, edits);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Power Query (M)", StringComparison.OrdinalIgnoreCase))
        {
            return new
            {
                ok = false,
                route = "needs_desktop",
                target,
                reason = ex.Message,
                dataPreserved = (bool?)null,
                nextSteps = new[]
                {
                    "Open the .pbix in Power BI Desktop.",
                    "connect_model to get a sessionId.",
                    "Re-run persist_model_edit / persist_open_model with that sessionId (data-preserving, scripted File > Save).",
                    "OR: use edit_model_offline for a structure-only TMDL/pbix rebuild (import table data will be empty until a refresh).",
                },
            };
        }
    }
}
