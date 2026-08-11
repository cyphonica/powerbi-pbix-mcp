using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

/// <summary>
/// Persisting a MODEL edit all the way to the .pbix on disk - the gap the plain model tools leave open (they
/// commit only to the LIVE in-memory engine and need a manual Power BI Desktop File &gt; Save). Every tool
/// here states its DATA-preservation contract plainly so neither the agent nor the user is ever misled about
/// whether a report's data survives the edit.
///
/// The edits argument is a JSON array of edit objects, each with an "op":
///   {"op":"add_measure","table":"Fact_Sales","name":"Margin %","dax":"DIVIDE([Profit],[Sales])","formatString":"0.0%"}
///   {"op":"update_measure","table":"Fact_Sales","name":"Sales","dax":"SUM(Fact_Sales[Amount])"}
///   {"op":"delete_measure","table":"Fact_Sales","name":"Old"}
///   {"op":"add_calculated_column","table":"Dim_Date","name":"IsWeekend","dax":"WEEKDAY([Date])>5"}
///   {"op":"add_relationship","fromTable":"Fact_Sales","fromColumn":"DateKey","toTable":"Dim_Date","toColumn":"DateKey"}
///   {"op":"update_relationship","fromTable":"Fact_Sales","fromColumn":"DateKey","toTable":"Dim_Date","toColumn":"DateKey","active":true}
///   {"op":"delete_relationship","name":"Fact_Sales-Dim_Date"}
/// </summary>
[McpServerToolType]
public static class PersistTools
{
    [McpServerTool(Name = "persist_model_edit")]
    [Description(
        "THE single entry to fix a measure / relationship / calculated column in a model AND persist it to the .pbix. " +
        "Picks the best route for `target` and reports what it did plus what is / isn't preserved. " +
        "target = an open model sessionId (from connect_model) -> live edit + scripted Power BI Desktop File>Save; " +
        "DATA PRESERVED for ANY model (needs the .pbix open in Desktop). " +
        "target = a cold .pbix PATH -> offline engine edit (ImageLoad->edit->ImageSave->repack); DATA PRESERVED, no Desktop, " +
        "but ONLY for M-free / engine-native models. If the .pbix imports via Power Query (M) - a normal data report - the " +
        "offline route cannot host it and the result explains to open it in Desktop and pass the sessionId instead. " +
        "edits = a JSON array of edit objects (see the tool-type summary). A .bak backup is taken and the write is atomic.")]
    public static string PersistModelEdit(ModelPersistService persist,
        [Description("an open model sessionId (data-preserving via Desktop) OR a cold .pbix path (offline, M-free only)")] string target,
        [Description("JSON array of edit objects, each with an \"op\" (add_measure, add_relationship, add_calculated_column, ...)")] string edits,
        [Description("the .pbix path to confirm the save landed (only used for the live/Desktop route; optional)")] string? pbixPath = null,
        [Description("scripted File>Save attempts before giving up (live route; default 2)")] int saveRetries = 2)
        => J.Try(() => persist.FixModel(target, ModelPersistService.ParseEdits(edits), pbixPath, saveRetries));

    [McpServerTool(Name = "persist_open_model")]
    [Description(
        "DATA-PRESERVING persist for a report OPEN in Power BI Desktop (the loop for a loaded report like a live dashboard): " +
        "apply the model edits to the connected LIVE model (SaveChanges + a Calculate when a calc column/relationship changed), " +
        "then drive Desktop's own File>Save (scripted Ctrl+S, located by the session's engine port) so Desktop writes its full " +
        "model+DATA image back to the .pbix. Works for ANY model including Power Query (M) import models. " +
        "PREREQUISITE (manual): the .pbix must be OPEN in Power BI Desktop and connected via connect_model - Desktop is the only " +
        "host that can persist a data-loaded, M-based model to disk. Pass pbixPath so the tool can confirm the save landed.")]
    public static string PersistOpenModel(ModelPersistService persist,
        [Description("sessionId from connect_model (a LOCAL Desktop model)")] string sessionId,
        [Description("JSON array of edit objects, each with an \"op\"")] string edits,
        [Description("the open report's .pbix path (to confirm the save landed; optional)")] string? pbixPath = null,
        [Description("scripted File>Save attempts before giving up (default 2)")] int saveRetries = 2)
        => J.Try(() => persist.PersistLive(sessionId, ModelPersistService.ParseEdits(edits), pbixPath, saveRetries));

    [McpServerTool(Name = "save_open_pbix")]
    [Description(
        "Persist an OPEN Power BI Desktop model to its .pbix by driving Desktop's own File>Save (scripted Ctrl+S), located by the " +
        "session's engine port. This is the model-side 'save' the toolkit otherwise lacks: after any live model edit (add_measure, " +
        "add_relationship, ...) call this to write the change - WITH data - back to disk without a manual click. " +
        "Requires the .pbix open in Power BI Desktop. Pass pbixPath so the tool can confirm the save landed (LastWriteTime advanced). " +
        "On an XMLA (Fabric/Service) session there is no local .pbix: the dataset persists server-side, so a pending M change is " +
        "flushed by a full server-side refresh instead (ok:true, persisted:'xmla-refresh', saveDispatched:false; no Desktop is touched).")]
    public static string SaveOpenPbix(ModelPersistService persist,
        [Description("sessionId from connect_model")] string sessionId,
        [Description("the open report's .pbix path (to confirm the save landed; optional)")] string? pbixPath = null,
        [Description("scripted File>Save attempts before giving up (default 2)")] int saveRetries = 2)
        => J.Try(() =>
        {
            var r = persist.SaveOpenPbix(sessionId, pbixPath, saveRetries);
            bool xmla = r.Persisted == "xmla-refresh";
            return new
            {
                ok = r.SaveDispatched || xmla,
                saveDispatched = r.SaveDispatched,
                // DEPRECATED alias of saveDispatched - the pre-Phase-0 response key external scripts read.
                // Always the same value; new readers should use saveDispatched.
                saved = r.SaveDispatched,
                persisted = r.Persisted,
                desktopPid = r.DesktopPid,
                error = r.Error,
                note = xmla
                    ? (r.MRefreshRan
                        ? "XMLA (Fabric/Service) session: the pending M change was persisted by a full " +
                          "server-side refresh - over XMLA that refresh IS the save. There is no local .pbix " +
                          "and no Desktop Ctrl+S was dispatched."
                        : "XMLA (Fabric/Service) session: nothing was pending - edits over XMLA persist " +
                          "server-side when they are committed, so there was no local .pbix save to dispatch " +
                          "and no refresh to run.")
                    : r.SaveDispatched
                        ? "Ctrl+S was dispatched to the Power BI Desktop that owns this session and the .pbix " +
                          "LastWriteTime advanced. That confirms a save was dispatched, not that the file content " +
                          "was verified."
                        : "Scripted File>Save did not confirm - ensure the report is open in Desktop and press Ctrl+S manually.",
            };
        });

    [McpServerTool(Name = "edit_model_offline")]
    [Description(
        "STRUCTURE-ONLY offline model edit (NO Power BI Desktop, NO live engine): deserialize a TMDL model (a definition/ folder " +
        "or a PBIP <name>.SemanticModel - the model half export_tmdl / generate_pbip / unpack_to_source produce), apply the edits " +
        "to the object tree, and re-serialize edited TMDL to outputFolder. The model STRUCTURE (measures, relationships, calc " +
        "columns, tables, partition definitions) round-trips exactly. DATA IS NOT PRESERVED: TMDL carries no compressed VertiPaq " +
        "data, so import (M) tables come back empty until a refresh. Use this for template / thin-model edits and source-control " +
        "workflows. For a DATA-preserving edit of a loaded report use persist_model_edit (offline, M-free) or persist_open_model " +
        "(Desktop). NOTE: a cold data .pbix that imports via M cannot be read offline - first export its model with export_tmdl " +
        "from a live session, then edit that folder here.")]
    public static string EditModelOffline(ModelPersistService persist,
        [Description("a TMDL definition folder, a PBIP <name>.SemanticModel, or a project folder containing one")] string tmdlFolder,
        [Description("output folder for the edited TMDL (created/overwritten)")] string outputFolder,
        [Description("JSON array of edit objects, each with an \"op\"")] string edits)
        => J.Try(() => persist.EditTmdlFolder(tmdlFolder, outputFolder, ModelPersistService.ParseEdits(edits)));
}
