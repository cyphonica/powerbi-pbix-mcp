using System.ComponentModel;
using ModelContextProtocol.Server;
using SuperBiMcp.Agent;
using SuperBiMcp.Services;

namespace SuperBiMcp.Tools;

/// <summary>
/// Power BI Desktop Bridge tools (Wave G5) - the edit/see loop over the JSON-RPC server recent Desktop
/// builds host on \\.\pipe\pbi-desktop-bridge-&lt;pid&gt; (a preview feature; may need Desktop's "external
/// tool access through secure local APIs" preview switch). Composes with save_pbir: edit the PBIR tree on
/// disk -> bridge_reload -> bridge_screenshot = render-verified report editing against a live Desktop.
/// All four reach into (or spawn) a Desktop on the operator's own machine, so they are interactive-attach
/// only - unattended pipelines keep using the queue-managed DesktopSession launch with its kill-safety
/// recording.
/// </summary>
[McpServerToolType]
public static class DesktopBridgeTools
{
    [McpServerTool(Name = "bridge_status")]
    [UnsafeForPipeline]
    [Description("UNSAFE-FOR-PIPELINE (interactive attach only): report every Power BI Desktop instance on this machine through the Desktop Bridge named pipe: bridge availability + manifest methods, the open file path, the unsaved-changes flag, the on-disk PBIR pages and the AS engine ports list_open_models discovers. A Desktop without the bridge (older build, or the 'external tool access through secure local APIs' preview switch off) degrades to a clear entry, never an error.")]
    public static string BridgeStatus(
        DesktopBridge bridge,
        [Description("per-instance pipe connect/probe timeout in seconds (default 3)")] int connectTimeoutSec = 3)
        => J.Try(() => bridge.Status(connectTimeoutSec));

    [McpServerTool(Name = "bridge_screenshot")]
    [UnsafeForPipeline]
    [Description("UNSAFE-FOR-PIPELINE (interactive attach only): capture pixel-accurate PNG renders of report pages from a RUNNING Power BI Desktop (report.snapshot.capture/v1) and write them to outDir - the render-verification half of the edit/see loop. Pass pageName (page GUID or displayName) for one page, or allPages=true for the whole report (needs an on-disk PBIR definition to enumerate). Returns per-page paths + status.")]
    public static string BridgeScreenshot(
        DesktopBridge bridge,
        [Description("Desktop process id from bridge_status / open_desktop")] int pid,
        [Description("directory the PNGs are written to (created if missing)")] string outDir,
        [Description("page GUID name or displayName; omit when allPages=true")] string? pageName = null,
        [Description("capture every page of the report (needs the on-disk PBIR page list)")] bool allPages = false,
        [Description("render scale 1..3 (default 2)")] int scale = 2,
        [Description("per-call timeout in seconds (default 60)")] int timeoutSec = 60)
        => J.Try(() => bridge.Screenshot(pid, outDir, pageName, allPages, scale, timeoutSec));

    [McpServerTool(Name = "bridge_reload")]
    [UnsafeForPipeline]
    [Description("UNSAFE-FOR-PIPELINE (interactive attach only): hot-reload the on-disk PBIP/PBIR definition into the open Power BI Desktop (file.reload/v1) without close/reopen - the composition step after save_pbir edits the tree on disk. Refuses while the Desktop reports unsaved changes (or cannot report at all) unless force=true, because a reload discards the in-memory state.")]
    public static string BridgeReload(
        DesktopBridge bridge,
        [Description("Desktop process id from bridge_status / open_desktop")] int pid,
        [Description("also reload the semantic model definition, not just the report (default false)")] bool reloadModelDefinition = false,
        [Description("reload even when the Desktop reports (or cannot rule out) unsaved changes, discarding them")] bool force = false,
        [Description("per-call timeout in seconds (default 60)")] int timeoutSec = 60)
        => J.Try(() => bridge.Reload(pid, reloadModelDefinition, force, timeoutSec));

    [McpServerTool(Name = "open_desktop")]
    [UnsafeForPipeline]
    [Description("UNSAFE-FOR-PIPELINE (interactive attach only): launch Power BI Desktop on a .pbix/.pbip (standard MSI install, the WindowsApps Store layout or the Store execution alias; spawn is console-window-free - Desktop shows its own window) and wait until its Desktop Bridge answers or waitForBridgeSec lapses. Returns pid + bridge availability + manifest; no bridge by the deadline degrades to a clear entry (the Desktop itself may still be healthy). Unattended pipelines must keep using their own queue-managed launch.")]
    public static string OpenDesktop(
        DesktopBridge bridge,
        [Description("absolute path to the .pbix or .pbip to open")] string path,
        [Description("how long to wait for the bridge pipe to answer, in seconds (default 60)")] int waitForBridgeSec = 60,
        [Description("explicit PBIDesktop.exe path; omit to probe the standard install + Store layouts")] string? exePath = null)
        => J.Try(() => bridge.OpenDesktop(path, waitForBridgeSec, exePath));
}
