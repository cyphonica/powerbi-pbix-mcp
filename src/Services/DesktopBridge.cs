using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace SuperBiMcp.Services;

/// <summary>
/// The Power BI Desktop Bridge integration (Wave G5) - the edit/see loop that closes "agent edits blind".
/// Recent Desktop builds host a JSON-RPC server INSIDE PBIDesktop.exe on a named pipe
/// (\\.\pipe\pbi-desktop-bridge-&lt;pid&gt;, one pipe per Desktop process; a preview feature that may need
/// Desktop's "external tool access through secure local APIs" preview switch). Protocol verified against
/// Microsoft's shipped @microsoft/powerbi-desktop-bridge-cli: LSP-style Content-Length framing
/// (vscode-jsonrpc StreamMessageReader/Writer), params wrapped as {client, clientActivityId, args}, and the
/// methods bridge.manifest / application.state.get/v1 / file.reload/v1 / report.snapshot.capture/v1.
///
/// The four surfaces:
///   Status()      - every Desktop instance with its bridge manifest, open file, unsaved flag, on-disk PBIR
///                   pages and the AS engine ports list_open_models discovers. A Desktop without the bridge
///                   degrades to a clear entry, never an exception.
///   Screenshot()  - pixel-accurate PNG renders of report pages from the RUNNING Desktop, written to disk.
///   Reload()      - hot-reload the on-disk PBIP/PBIR definition into the open Desktop (no close/reopen);
///                   refused while the Desktop reports unsaved changes unless forced.
///   OpenDesktop() - launch Desktop on a file and wait for its bridge to answer.
///
/// Composes with save_pbir: edit the PBIR tree on disk -> Reload() -> Screenshot() = the render-verification
/// loop. Every decision (framing, envelope, manifest gating, page resolution, reload refusal, exe lookup,
/// instance correlation) lives in a pure internal helper proven offline; only the pipe/process plumbing needs
/// a live Desktop, matching the DesktopInterop convention.
/// </summary>
public sealed class DesktopBridge
{
    private readonly ILogger<DesktopBridge> _log;
    private readonly PortDiscovery _discovery;

    public DesktopBridge(ILogger<DesktopBridge> log, PortDiscovery discovery)
    {
        _log = log;
        _discovery = discovery;
    }

    internal const string PipePrefix = "pbi-desktop-bridge-";
    private const string PipeDir = @"\\.\pipe\";

    /// <summary>The degrade message for a Desktop with no bridge pipe - the two known causes, spelled out.</summary>
    internal const string NoBridgeReason =
        "no pbi-desktop-bridge pipe for this Desktop - either the build predates the bridge, or the "
      + "'external tool access through secure local APIs' preview switch is off (File > Options > Preview features).";

    /// <summary>Desktop applies a reload asynchronously; a snapshot fired instantly can still render the old
    /// definition. Matches the settle Microsoft's own CLI applies after file.reload/v1.</summary>
    internal const int PostReloadSettleMs = 500;

    // ============================================================================ pipe discovery

    /// <summary>Every pid with a live bridge pipe, via a directory listing of \\.\pipe\ (the same discovery
    /// Microsoft's CLI uses). The lister is injectable so the filter/parse is provable offline.</summary>
    internal static IReadOnlyList<int> DiscoverBridgePids(Func<string[]>? listPipes = null)
    {
        string[] names;
        try { names = (listPipes ?? (() => Directory.GetFiles(PipeDir)))(); }
        catch { return Array.Empty<int>(); }   // pipe-dir listing can refuse on odd hosts - degrade to none
        return names.Select(TryParseBridgePipePid).Where(p => p > 0).Distinct().OrderBy(p => p).ToList();
    }

    /// <summary>Parse the owning pid out of a bridge pipe path or bare pipe name; 0 when it is not one.</summary>
    internal static int TryParseBridgePipePid(string pipePathOrName)
    {
        string name = pipePathOrName;
        int slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0) name = name[(slash + 1)..];
        if (!name.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)) return 0;
        string tail = name[PipePrefix.Length..];
        return int.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out int pid) && pid > 0 ? pid : 0;
    }

    // ============================================================================ instance correlation

    /// <summary>One Desktop instance before probing: whether it has a bridge pipe, and the engine ports its
    /// msmdsrv children listen on.</summary>
    internal sealed record InstanceSeed(int Pid, bool HasBridgePipe, IReadOnlyList<int> Ports);

    /// <summary>Union the three instance sources - bridge pipes, engine-port owners (the parent Desktop of
    /// each discovered msmdsrv) and bare PBIDesktop processes - into one per-pid seed list, ordered by pid.
    /// Engine rows whose Desktop parent could not be resolved (pid 0) are dropped: an orphan engine is not a
    /// Desktop instance.</summary>
    internal static IReadOnlyList<InstanceSeed> CorrelateInstances(
        IEnumerable<int> bridgePids,
        IEnumerable<(int DesktopPid, int Port)> engineOwners,
        IEnumerable<int> desktopProcessPids)
    {
        var map = new SortedDictionary<int, (bool Bridge, SortedSet<int> Ports)>();
        void Ensure(int pid) { if (pid > 0 && !map.ContainsKey(pid)) map[pid] = (false, new SortedSet<int>()); }
        foreach (int p in bridgePids) { Ensure(p); if (p > 0) map[p] = (true, map[p].Ports); }
        foreach (var (desktopPid, port) in engineOwners) { if (desktopPid <= 0) continue; Ensure(desktopPid); map[desktopPid].Ports.Add(port); }
        foreach (int p in desktopProcessPids) Ensure(p);
        return map.Select(kv => new InstanceSeed(kv.Key, kv.Value.Bridge, kv.Value.Ports.ToList())).ToList();
    }

    // ============================================================================ bridge_status

    /// <summary>bridge_status: the per-instance structured report. Never throws for an instance - a Desktop
    /// without the bridge, or one whose probe fails, degrades to a clear entry.</summary>
    public object Status(int connectTimeoutSec = 3)
    {
        if (!OperatingSystem.IsWindows())
            return new { ok = true, count = 0, instances = Array.Empty<object>(), note = "the Desktop Bridge only exists on Windows." };
        var timeout = TimeSpan.FromSeconds(Math.Clamp(connectTimeoutSec, 1, 30));

        var bridgePids = DiscoverBridgePids();
        var engines = _discovery.Discover();
        var engineByPort = engines.ToDictionary(e => e.Port);
        var engineOwners = engines.Select(e => (DesktopPid: DesktopInterop.GetParentPid(e.OwnerPid), e.Port)).ToList();
        var seeds = CorrelateInstances(bridgePids, engineOwners, SafeDesktopPids());

        var instances = new List<object>();
        foreach (var seed in seeds)
        {
            var ports = seed.Ports.Select(p => (object)new
            { port = p, enginePid = engineByPort[p].OwnerPid, workspace = engineByPort[p].WorkspaceDir }).ToList();
            string? process = DesktopInterop.ProcessName(seed.Pid);

            if (!seed.HasBridgePipe)
            {
                instances.Add(new { pid = seed.Pid, process, bridge = new { available = false, reason = NoBridgeReason }, enginePorts = ports });
                continue;
            }
            try
            {
                var probe = ProbeBridge(seed.Pid, timeout);
                string? reportDir = ResolveReportDir(probe.CurrentFilePath);
                List<object>? pages = null;
                if (reportDir != null)
                    pages = ReadPagesFromDefinition(reportDir)
                        .Select(p => (object)new { name = p.Id, displayName = p.DisplayName }).ToList();
                instances.Add(new
                {
                    pid = seed.Pid,
                    process,
                    bridge = new { available = true, methods = probe.Methods.OrderBy(m => m, StringComparer.Ordinal).ToList() },
                    state = new { currentFilePath = probe.CurrentFilePath, hasUnsavedChanges = probe.HasUnsavedChanges },
                    reportDir,
                    pages,
                    enginePorts = ports,
                });
            }
            catch (Exception ex)
            {
                // pipe present but unanswerable (busy handshake, stale pipe of a dead pid, timeout) - degrade
                _log.LogWarning(ex, "bridge probe failed for pid {Pid}", seed.Pid);
                instances.Add(new
                {
                    pid = seed.Pid,
                    process,
                    bridge = new { available = false, reason = $"bridge pipe present but the probe failed: {ex.Message}" },
                    enginePorts = ports,
                });
            }
        }
        return new { ok = true, count = instances.Count, instances };
    }

    // ============================================================================ bridge_screenshot

    /// <summary>bridge_screenshot: pixel-accurate PNG renders of report pages from the running Desktop
    /// (report.snapshot.capture/v1), written to outDir. Pass pageName (GUID or displayName) for one page or
    /// allPages=true for the whole report; per-page failures are reported per page, never as one throw.</summary>
    public object Screenshot(int pid, string outDir, string? pageName = null, bool allPages = false,
        int scale = 2, int timeoutSec = 60)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the Desktop Bridge only exists on Windows.");
        if (string.IsNullOrWhiteSpace(outDir)) throw new ArgumentException("outDir is required.");
        if (scale is < 1 or > 3) throw new ArgumentException("scale must be 1..3 (the bridge's accepted range).");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 600));

        using var stream = ConnectBridgePipe(pid, timeout);
        var client = new BridgeRpcClient(stream);
        var methods = BridgeRpc.MethodNames(client.Call(BridgeRpc.MethodManifest, new JsonObject(), timeout));
        BridgeRpc.RequireMethod(methods, BridgeRpc.MethodSnapshotCapture);

        // page ids come from the on-disk PBIR definition of the file the Desktop reports open (the bridge
        // itself does not enumerate pages); a plain .pbix has no on-disk tree, so a raw pageId still works.
        string? currentFile = null;
        if (methods.Contains(BridgeRpc.MethodApplicationState))
            try { currentFile = (string?)client.Call(BridgeRpc.MethodApplicationState, new JsonObject(), timeout)["currentFilePath"]; }
            catch (BridgeRpcException) { /* state is optional here - page resolution degrades below */ }
        string? reportDir = ResolveReportDir(currentFile);
        var known = reportDir != null ? ReadPagesFromDefinition(reportDir) : Array.Empty<PbirPage>();
        var targets = ResolvePages(known, pageName, allPages);

        string fullOutDir = Path.GetFullPath(outDir);
        Directory.CreateDirectory(fullOutDir);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<object>();
        int failed = 0;
        foreach (var t in targets)
        {
            try
            {
                var res = client.Call(BridgeRpc.MethodSnapshotCapture,
                    new JsonObject { ["pageId"] = t.Id, ["scale"] = scale }, timeout);
                byte[] bytes = DecodeSnapshotPayload(res);
                string display = (string?)res["pageDisplayName"] ?? t.DisplayName ?? t.Id;
                string file = Path.Combine(fullOutDir, UniqueFileName(used, SafeFileName(display)) + ".png");
                File.WriteAllBytes(file, bytes);
                results.Add(new { pageId = t.Id, displayName = display, ok = true, path = file, bytes = bytes.Length });
            }
            catch (Exception ex)
            {
                failed++;
                results.Add(new { pageId = t.Id, displayName = t.DisplayName, ok = false, error = ex.Message });
            }
        }
        return new { ok = failed == 0, pid, outDir = fullOutDir, captured = results.Count - failed, failed, pages = results };
    }

    // ============================================================================ bridge_reload

    /// <summary>bridge_reload: hot-reload the on-disk PBIP/PBIR definition into the open Desktop
    /// (file.reload/v1) without close/reopen. Refused while the Desktop reports unsaved changes - or cannot
    /// report at all - unless force, because a reload discards the in-memory state.</summary>
    public object Reload(int pid, bool reloadModelDefinition = false, bool force = false, int timeoutSec = 60)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("the Desktop Bridge only exists on Windows.");
        var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSec, 5, 600));

        using var stream = ConnectBridgePipe(pid, timeout);
        var client = new BridgeRpcClient(stream);
        var methods = BridgeRpc.MethodNames(client.Call(BridgeRpc.MethodManifest, new JsonObject(), timeout));
        BridgeRpc.RequireMethod(methods, BridgeRpc.MethodFileReload);

        bool? hasUnsaved = null;
        string? currentFile = null;
        if (methods.Contains(BridgeRpc.MethodApplicationState))
            try
            {
                var state = client.Call(BridgeRpc.MethodApplicationState, new JsonObject(), timeout);
                currentFile = (string?)state["currentFilePath"];
                hasUnsaved = (bool?)state["hasUnsavedChanges"];
            }
            catch (BridgeRpcException) { /* hasUnsaved stays unknown; the gate below refuses unless forced */ }

        string? note = EnsureReloadAllowed(hasUnsaved, force);
        var res = client.Call(BridgeRpc.MethodFileReload,
            new JsonObject { ["reloadModelDefinition"] = reloadModelDefinition }, timeout);
        bool success = (bool?)res["success"] ?? true;
        Thread.Sleep(PostReloadSettleMs);
        return new { ok = success, pid, currentFilePath = currentFile, reloadModelDefinition, note, bridgeResult = res };
    }

    /// <summary>The reload gate, pure. Unsaved changes refuse without force; an UNKNOWABLE state (no
    /// application.state.get/v1) also refuses without force, because a blind reload can silently discard the
    /// operator's work. The returned note (null when nothing was at risk) is surfaced in the result.</summary>
    internal static string? EnsureReloadAllowed(bool? hasUnsavedChanges, bool force)
    {
        if (hasUnsavedChanges == true && !force)
            throw new InvalidOperationException(
                "the Desktop reports unsaved changes - a reload would discard them. Save in Desktop first, or pass force=true to discard them deliberately.");
        if (hasUnsavedChanges is null && !force)
            throw new InvalidOperationException(
                "the bridge cannot report whether the document has unsaved changes (application.state.get/v1 unavailable) - refusing to reload blind. Pass force=true to reload anyway.");
        if (hasUnsavedChanges == true) return "forced: unsaved changes were discarded by the reload.";
        if (hasUnsavedChanges is null) return "forced: the unsaved-changes state was unknowable (application.state.get/v1 unavailable).";
        return null;
    }

    // ============================================================================ open_desktop

    /// <summary>open_desktop: launch Power BI Desktop on a file and wait until a bridge for that process
    /// answers, or the timeout lapses (which degrades to bridgeAvailable=false, never an exception - the
    /// Desktop itself may be perfectly healthy without the bridge). The spawn is window-hidden-friendly
    /// (UseShellExecute=false, CreateNoWindow) so no console ever flashes; Desktop shows its own window.</summary>
    public object OpenDesktop(string path, int waitForBridgeSec = 60, string? exePath = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("open_desktop drives Power BI Desktop and only runs on Windows.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full)) throw new FileNotFoundException("file not found: " + full, full);
        string exe = ResolveDesktopExe(exePath, File.Exists, SafeEnumDirs);

        var baseline = new HashSet<int>(DiscoverBridgePids());
        var psi = new ProcessStartInfo(exe, $"\"{full}\"") { UseShellExecute = false, CreateNoWindow = true };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        int pid = proc.Id;
        string? note = null;

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(waitForBridgeSec, 5, 600));
        var probeTimeout = TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            var pids = DiscoverBridgePids();
            if (pids.Contains(pid)) break;
            bool exited;
            try { exited = proc.HasExited; } catch { exited = false; }
            if (exited)
            {
                // single-instance handoff: the launched exe (the Store alias in particular) can hand the
                // document to another PBIDesktop process and exit; adopt the new bridge that opened our file.
                int? adopted = PickHandoffPid(baseline, pids, p => TryPeekCurrentFile(p, probeTimeout), full);
                if (adopted is { } a)
                {
                    note = $"the launched process exited (single-instance handoff) - adopted Desktop pid {a}, whose bridge answered.";
                    pid = a;
                    break;
                }
            }
            Thread.Sleep(500);
        }

        if (DiscoverBridgePids().Contains(pid))
        {
            try
            {
                var probe = ProbeBridge(pid, probeTimeout);
                int enginePort = EnginePortOf(pid);
                return new
                {
                    ok = true, pid, exe,
                    bridge = new { available = true, methods = probe.Methods.OrderBy(m => m, StringComparer.Ordinal).ToList() },
                    state = new { currentFilePath = probe.CurrentFilePath, hasUnsavedChanges = probe.HasUnsavedChanges },
                    enginePort = enginePort == 0 ? (int?)null : enginePort,
                    note,
                };
            }
            catch (Exception ex)
            {
                return new { ok = true, pid, exe, bridge = new { available = false, reason = "bridge pipe present but the probe failed: " + ex.Message }, note };
            }
        }
        return new
        {
            ok = true, pid, exe,
            bridge = new { available = false, reason = $"no bridge pipe answered within {waitForBridgeSec}s - {NoBridgeReason}" },
            note,
        };
    }

    /// <summary>The handoff adoption rule, pure. Prefer a NEW bridge pid whose open file IS the requested
    /// path; with no file match, exactly one new pid is the best available guess (the caller notes it);
    /// anything else adopts nothing.</summary>
    internal static int? PickHandoffPid(IReadOnlySet<int> baseline, IReadOnlyList<int> current,
        Func<int, string?> currentFileOf, string requestedPath)
    {
        var fresh = current.Where(p => !baseline.Contains(p)).ToList();
        if (fresh.Count == 0) return null;
        foreach (int p in fresh)
        {
            string? f = currentFileOf(p);
            if (f != null && PathsEqual(f, requestedPath)) return p;
        }
        return fresh.Count == 1 ? fresh[0] : null;
    }

    internal static bool PathsEqual(string a, string b)
    {
        try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
    }

    // ============================================================================ exe resolution

    private const string StorePackagePrefix = "Microsoft.MicrosoftPowerBIDesktop";

    /// <summary>Everywhere PBIDesktop.exe can live, in probe order: the DesktopInterop resolution first
    /// (override, then DAXOPS_PBIDESKTOP_EXE, then the stock MSI install), the x86 MSI layout, every
    /// WindowsApps Store package, and finally the Store execution alias. The directory enumerator is
    /// injected because WindowsApps ACLs make the real one refuse on some hosts.</summary>
    internal static IReadOnlyList<string> DesktopExeCandidates(string? overridePath,
        Func<string, string, IEnumerable<string>> enumerateDirs)
    {
        var list = new List<string> { DesktopInterop.ResolvePbixExe(overridePath) };
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrEmpty(pf86))
            list.Add(Path.Combine(pf86, "Microsoft Power BI Desktop", "bin", "PBIDesktop.exe"));
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(pf))
            foreach (string dir in enumerateDirs(Path.Combine(pf, "WindowsApps"), StorePackagePrefix + "*"))
                list.Add(Path.Combine(dir, "bin", "PBIDesktop.exe"));
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(local))
            list.Add(Path.Combine(local, "Microsoft", "WindowsApps", "PBIDesktopStore.exe"));
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>First existing candidate wins. An EXPLICIT exePath that does not exist is its own failure
    /// (falling through would silently mask the operator's typo with a different install).</summary>
    internal static string ResolveDesktopExe(string? overridePath, Func<string, bool> fileExists,
        Func<string, string, IEnumerable<string>> enumerateDirs)
    {
        if (overridePath != null && !fileExists(overridePath))
            throw new FileNotFoundException($"exePath '{overridePath}' does not exist.", overridePath);
        var candidates = DesktopExeCandidates(overridePath, enumerateDirs);
        foreach (string c in candidates)
            if (fileExists(c)) return c;
        throw new FileNotFoundException(
            "PBIDesktop.exe not found - probed: " + string.Join("; ", candidates)
          + ". Install Power BI Desktop, set DAXOPS_PBIDESKTOP_EXE, or pass exePath.");
    }

    internal static IEnumerable<string> SafeEnumDirs(string root, string pattern)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root, pattern) : Enumerable.Empty<string>(); }
        catch { return Enumerable.Empty<string>(); }   // WindowsApps ACLs can refuse enumeration - degrade to none
    }

    // ============================================================================ PBIR page listing

    /// <summary>One page of the on-disk PBIR definition: the GUID folder name (the bridge's pageId) and its
    /// displayName (null when page.json is missing or unreadable).</summary>
    internal sealed record PbirPage(string Id, string? DisplayName);

    /// <summary>Locate the *.Report definition folder for the file the Desktop reports open. A .pbip pointer
    /// names its report artifact (artifacts[].report.path) - honoured before the conventional sibling
    /// "&lt;name&gt;.Report" guess; a folder is accepted when it holds definition/pages itself or through a
    /// *.Report child. A plain .pbix returns null: its definition lives inside the zip, not on disk.</summary>
    internal static string? ResolveReportDir(string? currentFilePath)
    {
        if (string.IsNullOrWhiteSpace(currentFilePath)) return null;

        if (Directory.Exists(currentFilePath))
        {
            if (HasPagesTree(currentFilePath)) return currentFilePath;
            foreach (string sub in SafeEnumDirs(currentFilePath, "*.Report"))
                if (HasPagesTree(sub)) return sub;
            return null;
        }
        if (!File.Exists(currentFilePath)) return null;
        if (!currentFilePath.EndsWith(".pbip", StringComparison.OrdinalIgnoreCase)) return null;

        string dir = Path.GetDirectoryName(Path.GetFullPath(currentFilePath))!;
        // the pbip pointer names its report artifact; honour it before guessing
        try
        {
            if (JsonNode.Parse(File.ReadAllText(currentFilePath)) is JsonObject pbip
                && pbip["artifacts"] is JsonArray artifacts)
                foreach (var a in artifacts)
                    if ((a as JsonObject)?["report"] is JsonObject rep && (string?)rep["path"] is { Length: > 0 } rel)
                    {
                        string candidate = Path.Combine(dir, rel);
                        if (HasPagesTree(candidate)) return candidate;
                    }
        }
        catch { /* a malformed pointer falls through to the conventional guesses */ }

        string sibling = Path.Combine(dir, Path.GetFileNameWithoutExtension(currentFilePath) + ".Report");
        if (HasPagesTree(sibling)) return sibling;
        foreach (string sub in SafeEnumDirs(dir, "*.Report"))
            if (HasPagesTree(sub)) return sub;
        return null;
    }

    private static bool HasPagesTree(string reportDir)
        => Directory.Exists(Path.Combine(reportDir, "definition", "pages"));

    /// <summary>Read the page list from definition/pages: pages.json's pageOrder (falling back to its pages
    /// array, name-then-id, the way Microsoft's CLI does), then each page's displayName from
    /// &lt;id&gt;/page.json. Best-effort throughout - a missing or unreadable file degrades, never throws.</summary>
    internal static IReadOnlyList<PbirPage> ReadPagesFromDefinition(string reportDir)
    {
        string pagesRoot = Path.Combine(reportDir, "definition", "pages");
        string idxPath = Path.Combine(pagesRoot, "pages.json");
        if (!File.Exists(idxPath)) return Array.Empty<PbirPage>();

        JsonObject? idx;
        try { idx = JsonNode.Parse(File.ReadAllText(idxPath)) as JsonObject; }
        catch { return Array.Empty<PbirPage>(); }
        if (idx is null) return Array.Empty<PbirPage>();

        var ids = new List<string>();
        if (idx["pageOrder"] is JsonArray order)
        {
            foreach (var n in order)
                if (n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0) ids.Add(s);
        }
        else if (idx["pages"] is JsonArray pages)
        {
            foreach (var n in pages)
                if (n is JsonObject po && ((string?)po["name"] ?? (string?)po["id"]) is { Length: > 0 } s) ids.Add(s);
        }

        var result = new List<PbirPage>(ids.Count);
        foreach (string id in ids)
        {
            string? display = null;
            try
            {
                string pagePath = Path.Combine(pagesRoot, id, "page.json");
                if (File.Exists(pagePath) && JsonNode.Parse(File.ReadAllText(pagePath)) is JsonObject page)
                    display = (string?)page["displayName"];
            }
            catch { /* displayName stays null - the GUID still identifies the page */ }
            result.Add(new PbirPage(id, display));
        }
        return result;
    }

    /// <summary>The screenshot target rule, pure. Exactly one of pageName / allPages. allPages needs a
    /// readable on-disk page list; a pageName resolves against it by GUID or displayName, and a name that is
    /// NOT in a non-empty list is a typo (thrown with the list), while with NO list at all the caller's
    /// value passes through as a raw pageId - the bridge is the authority for a .pbix.</summary>
    internal static IReadOnlyList<PbirPage> ResolvePages(IReadOnlyList<PbirPage> known, string? pageName, bool allPages)
    {
        bool hasName = !string.IsNullOrWhiteSpace(pageName);
        if (allPages == hasName)
            throw new ArgumentException("pass exactly one of pageName (a single page) or allPages=true (the whole report).");
        if (allPages)
        {
            if (known.Count == 0)
                throw new InvalidOperationException(
                    "no pages found in an on-disk PBIR definition for the open file - allPages needs a .pbip/PBIR "
                  + "source whose definition/pages tree is readable (a plain .pbix keeps its definition inside the "
                  + "zip; pass pageName with the page GUID instead).");
            return known;
        }
        var match = known.FirstOrDefault(p =>
            string.Equals(p.Id, pageName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(p.DisplayName, pageName, StringComparison.OrdinalIgnoreCase));
        if (match != null) return new[] { match };
        if (known.Count > 0)
            throw new ArgumentException($"page '{pageName}' is not in the report - pages: "
                + string.Join(", ", known.Select(p => p.DisplayName is null ? p.Id : $"{p.Id} ({p.DisplayName})")) + ".");
        return new[] { new PbirPage(pageName!, null) };
    }

    // ============================================================================ snapshot payload

    /// <summary>Decode report.snapshot.capture/v1's payload: base64 (the default when encoding is omitted)
    /// to PNG bytes. Anything else is a loud failure - writing a mis-decoded "PNG" would be worse.</summary>
    internal static byte[] DecodeSnapshotPayload(JsonObject result)
    {
        string? payload = (string?)result["payload"];
        if (string.IsNullOrEmpty(payload))
            throw new InvalidOperationException("snapshot result carried no payload - nothing to write.");
        string encoding = (string?)result["encoding"] ?? "base64";
        if (!encoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"unsupported snapshot encoding '{encoding}' - expected base64.");
        try { return Convert.FromBase64String(payload); }
        catch (FormatException) { throw new InvalidOperationException("snapshot payload was not valid base64."); }
    }

    /// <summary>Display names become file names: invalid characters go to '_' (preserving word shape),
    /// trailing dots/spaces are trimmed (Windows drops them silently - better loud here), and a name with
    /// nothing left but underscores falls back to "page".</summary>
    internal static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name) sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        string clean = sb.ToString().Trim(' ', '.');
        return clean.All(c => c == '_' || char.IsWhiteSpace(c)) ? "page" : clean;
    }

    /// <summary>Two pages can share a displayName; the second capture must not overwrite the first.</summary>
    internal static string UniqueFileName(ISet<string> used, string baseName)
    {
        string candidate = baseName;
        for (int i = 2; !used.Add(candidate); i++) candidate = $"{baseName} ({i})";
        return candidate;
    }

    // ============================================================================ live plumbing

    /// <summary>What one manifest + application-state probe learns about a bridge.</summary>
    private sealed record BridgeProbe(HashSet<string> Methods, string? CurrentFilePath, bool? HasUnsavedChanges);

    /// <summary>Connect and interrogate one bridge: manifest always (it is also the forward-compat gate),
    /// application state only when the manifest declares it - an older bridge without it degrades to
    /// unknown state rather than failing the whole probe.</summary>
    private BridgeProbe ProbeBridge(int pid, TimeSpan timeout)
    {
        using var stream = ConnectBridgePipe(pid, timeout);
        var client = new BridgeRpcClient(stream);
        var methods = BridgeRpc.MethodNames(client.Call(BridgeRpc.MethodManifest, new JsonObject(), timeout));
        string? file = null;
        bool? unsaved = null;
        if (methods.Contains(BridgeRpc.MethodApplicationState))
            try
            {
                var state = client.Call(BridgeRpc.MethodApplicationState, new JsonObject(), timeout);
                file = (string?)state["currentFilePath"];
                unsaved = (bool?)state["hasUnsavedChanges"];
            }
            catch (BridgeRpcException) { /* declared but refused - state stays unknown */ }
        return new BridgeProbe(methods, file, unsaved);
    }

    private string? TryPeekCurrentFile(int pid, TimeSpan timeout)
    {
        try { return ProbeBridge(pid, timeout).CurrentFilePath; }
        catch (Exception ex) { _log.LogDebug(ex, "handoff peek failed for pid {Pid}", pid); return null; }
    }

    internal static string PipeName(int pid) => PipePrefix + pid.ToString(CultureInfo.InvariantCulture);

    private static Stream ConnectBridgePipe(int pid, TimeSpan timeout)
    {
        var pipe = new NamedPipeClientStream(".", PipeName(pid), PipeDirection.InOut, PipeOptions.Asynchronous);
        try { pipe.Connect((int)timeout.TotalMilliseconds); }
        catch { pipe.Dispose(); throw; }
        return pipe;
    }

    private static int[] SafeDesktopPids()
    {
        try
        {
            var ps = Process.GetProcessesByName("PBIDesktop");
            int[] ids = ps.Select(p => p.Id).ToArray();
            foreach (var p in ps) p.Dispose();
            return ids;
        }
        catch { return Array.Empty<int>(); }
    }

    private int EnginePortOf(int desktopPid)
    {
        foreach (int child in DesktopInterop.FindChildProcessIds(desktopPid, "msmdsrv.exe"))
        {
            int port = DesktopInterop.FindListeningPort(child);
            if (port != 0) return port;
        }
        return 0;
    }
}

// ================================================================================ JSON-RPC protocol layer

/// <summary>
/// The minimal JSON-RPC protocol layer for the Desktop Bridge, verified against Microsoft's shipped
/// @microsoft/powerbi-desktop-bridge-cli (which drives the SAME pipe through vscode-jsonrpc): LSP-style
/// framing ("Content-Length: N\r\n\r\n" + a UTF-8 JSON body - the length counts BYTES, not chars), request
/// params wrapped as {client, clientActivityId, args}, and errors carrying the bridge's own code (e.g.
/// METHOD_NOT_AVAILABLE) in error.data. All pure or stream-driven, so provable against in-memory streams.
/// </summary>
internal static class BridgeRpc
{
    internal const string ClientName = "super-bi-mcp";
    internal const string MethodManifest = "bridge.manifest";
    internal const string MethodApplicationState = "application.state.get/v1";
    internal const string MethodFileReload = "file.reload/v1";
    internal const string MethodSnapshotCapture = "report.snapshot.capture/v1";

    /// <summary>Largest framed body accepted - a scale-3 page render is a few MB of base64, so 64MB is
    /// generous headroom while still refusing a nonsense length before allocating it.</summary>
    internal const int MaxBodyBytes = 64 * 1024 * 1024;

    /// <summary>Frame one message: the Content-Length header counts the UTF-8 BYTES of the body.</summary>
    internal static byte[] Frame(string json)
    {
        byte[] body = Encoding.UTF8.GetBytes(json);
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        byte[] framed = new byte[header.Length + body.Length];
        Buffer.BlockCopy(header, 0, framed, 0, header.Length);
        Buffer.BlockCopy(body, 0, framed, header.Length, body.Length);
        return framed;
    }

    /// <summary>The request envelope the bridge expects: params = {client, clientActivityId, args}.</summary>
    internal static JsonObject BuildRequest(int id, string method, JsonObject args, string activityId) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["method"] = method,
        ["params"] = new JsonObject
        {
            ["client"] = ClientName,
            ["clientActivityId"] = activityId,
            ["args"] = args,
        },
    };

    /// <summary>Read one framed message. Null at a clean EOF between messages; a close mid-header or
    /// mid-body is an IOException - the bridge broke framing, and resynchronising is impossible.</summary>
    internal static async Task<JsonObject?> ReadMessageAsync(Stream s, CancellationToken ct)
    {
        var header = new List<byte>(128);
        var one = new byte[1];
        while (true)
        {
            int n = await s.ReadAsync(one.AsMemory(0, 1), ct).ConfigureAwait(false);
            if (n == 0)
            {
                if (header.Count == 0) return null;
                throw new IOException("the bridge closed the pipe mid-header.");
            }
            header.Add(one[0]);
            if (header.Count >= 4
                && header[^4] == (byte)'\r' && header[^3] == (byte)'\n'
                && header[^2] == (byte)'\r' && header[^1] == (byte)'\n') break;
            if (header.Count > 8192) throw new IOException("bridge framing error - the header block never terminated within 8KB.");
        }
        int len = ParseContentLength(Encoding.ASCII.GetString(header.ToArray()));
        if (len < 0 || len > MaxBodyBytes)
            throw new IOException($"bridge framing error - Content-Length {len} is outside the accepted range.");
        byte[] body = new byte[len];
        try { await s.ReadExactlyAsync(body, ct).ConfigureAwait(false); }
        catch (EndOfStreamException) { throw new IOException("the bridge closed the pipe mid-body."); }
        return JsonNode.Parse(Encoding.UTF8.GetString(body)) as JsonObject
            ?? throw new IOException("the bridge sent a message that is not a JSON object.");
    }

    /// <summary>Header names are case-insensitive and unknown headers (Content-Type) are tolerated, the way
    /// vscode-jsonrpc's own reader behaves. No Content-Length at all is a framing failure.</summary>
    internal static int ParseContentLength(string headerBlock)
    {
        foreach (string line in headerBlock.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (!line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
            if (int.TryParse(line[(colon + 1)..].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int len))
                return len;
            throw new IOException($"bridge framing error - unparseable Content-Length in '{line.Trim()}'.");
        }
        throw new IOException("bridge framing error - no Content-Length header (the bridge speaks LSP-style framing).");
    }

    /// <summary>Does this message answer request &lt;id&gt;? A missing id is a notification (skipped); a
    /// server quoting the id back as a string still matches.</summary>
    internal static bool MatchesId(JsonObject msg, int id)
    {
        if (msg["id"] is not JsonValue v) return false;
        if (v.TryGetValue<int>(out int i)) return i == id;
        if (v.TryGetValue<long>(out long l)) return l == id;
        if (v.TryGetValue<string>(out string? s)) return s == id.ToString(CultureInfo.InvariantCulture);
        return false;
    }

    /// <summary>Split a response into its result or its error. The bridge's own code and details ride in
    /// error.data ({code, details:{requiredMethod, availableMethods}, retryable}); the JSON-RPC code stays
    /// alongside so -32601 (method not found) is recognised even without bridge data.</summary>
    internal static (JsonObject? Result, BridgeRpcError? Error) ParseResponse(JsonObject msg)
    {
        if (msg["error"] is JsonObject err)
        {
            int? rpcCode = null;
            if (err["code"] is JsonValue cv && cv.TryGetValue<int>(out int c)) rpcCode = c;
            var data = err["data"] as JsonObject;
            var details = data?["details"] as JsonObject;
            var avail = new List<string>();
            if ((details?["availableMethods"] ?? data?["availableMethods"]) is JsonArray aArr)
                foreach (var a in aArr)
                    if (a is JsonValue av && av.TryGetValue<string>(out string? s)) avail.Add(s);
            return (null, new BridgeRpcError
            {
                JsonRpcCode = rpcCode,
                BridgeCode = data?["code"] is JsonValue bv && bv.TryGetValue<string>(out string? bc) ? bc : null,
                Message = (string?)err["message"] ?? "",
                AvailableMethods = avail,
                Retryable = data?["retryable"] is JsonValue rv && rv.TryGetValue<bool>(out bool r) && r,
            });
        }
        return (msg["result"] as JsonObject ?? new JsonObject(), null);
    }

    /// <summary>The manifest's methods array ({methods:[{name},...]}) as a set - the forward-compat gate
    /// every versioned call is checked against before it is sent.</summary>
    internal static HashSet<string> MethodNames(JsonObject? manifest)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (manifest?["methods"] is JsonArray arr)
            foreach (var m in arr)
                if ((m as JsonObject)?["name"] is JsonValue v && v.TryGetValue<string>(out string? name) && name.Length > 0)
                    set.Add(name);
        return set;
    }

    /// <summary>Refuse a call the manifest does not declare, listing what IS available - the same gate the
    /// bridge itself would apply, but with a message that tells the operator what to do about it.</summary>
    internal static void RequireMethod(IReadOnlySet<string> methods, string method)
    {
        if (methods.Contains(method)) return;
        throw new InvalidOperationException(
            $"this Desktop's bridge does not offer '{method}' - available: "
            + (methods.Count == 0 ? "(none)" : string.Join(", ", methods.OrderBy(m => m, StringComparer.Ordinal)))
            + ". Update Power BI Desktop to a build that ships it.");
    }
}

/// <summary>A bridge-side error, normalised: the JSON-RPC code, the bridge's own code (error.data.code) and
/// its availableMethods list when the failure is a missing method.</summary>
internal sealed class BridgeRpcError
{
    internal int? JsonRpcCode { get; init; }
    internal string? BridgeCode { get; init; }
    internal string Message { get; init; } = "";
    internal IReadOnlyList<string> AvailableMethods { get; init; } = Array.Empty<string>();
    internal bool Retryable { get; init; }

    /// <summary>Both spellings of "no such method": the bridge's METHOD_NOT_AVAILABLE and JSON-RPC -32601.</summary>
    internal bool IsMethodNotAvailable
        => string.Equals(BridgeCode, "METHOD_NOT_AVAILABLE", StringComparison.Ordinal) || JsonRpcCode == -32601;
}

/// <summary>An error response from the bridge, carried as an exception so J.Try surfaces it in-band.</summary>
internal sealed class BridgeRpcException : Exception
{
    internal BridgeRpcError Error { get; }

    internal BridgeRpcException(BridgeRpcError error) : base(Compose(error)) => Error = error;

    private static string Compose(BridgeRpcError e)
    {
        string msg = e.Message.Length > 0 ? e.Message : "bridge error";
        if (e.IsMethodNotAvailable && e.AvailableMethods.Count > 0)
            msg += " - available methods: " + string.Join(", ", e.AvailableMethods);
        return e.BridgeCode != null ? $"{e.BridgeCode}: {msg}" : msg;
    }
}

/// <summary>
/// One bridge conversation over an already-connected duplex stream (the caller owns the stream's lifetime).
/// Requests go out framed; responses are matched by id, skipping notifications and other requests' answers;
/// the whole exchange runs under one hard deadline so a silent bridge becomes a clean TimeoutException
/// instead of a hang.
/// </summary>
internal sealed class BridgeRpcClient
{
    private readonly Stream _stream;
    private int _lastId;

    internal BridgeRpcClient(Stream duplex) => _stream = duplex;

    internal JsonObject Call(string method, JsonObject args, TimeSpan timeout)
    {
        int id = Interlocked.Increment(ref _lastId);
        var request = BridgeRpc.BuildRequest(id, method, args, Guid.NewGuid().ToString("D"));
        using var cts = new CancellationTokenSource(timeout);
        try { return CallAsync(id, method, request, cts.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"the bridge did not answer '{method}' within {timeout.TotalSeconds:0.#}s.");
        }
    }

    private async Task<JsonObject> CallAsync(int id, string method, JsonObject request, CancellationToken ct)
    {
        byte[] framed = BridgeRpc.Frame(request.ToJsonString());
        await _stream.WriteAsync(framed, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        while (true)
        {
            var msg = await BridgeRpc.ReadMessageAsync(_stream, ct).ConfigureAwait(false)
                ?? throw new IOException($"the bridge closed the pipe before answering '{method}'.");
            if (!BridgeRpc.MatchesId(msg, id)) continue;   // notifications and other requests' answers
            var (result, error) = BridgeRpc.ParseResponse(msg);
            if (error != null) throw new BridgeRpcException(error);
            return result ?? new JsonObject();
        }
    }
}
