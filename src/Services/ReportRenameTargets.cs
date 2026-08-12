using System.Text.Json.Nodes;

namespace SuperBiMcp.Services;

/// <summary>
/// The report side of a propagating rename: one writable handle over whichever report source the
/// caller supplied (a legacy Layout session, an open PBIR session, or a PBIR .pbix / PBIP path).
/// The orchestration contract is snapshot -> rewrite -> flush BEFORE the model transaction commits,
/// so a model-commit failure can Restore() the report to exactly its pre-rename state (in-memory
/// snapshot for sessions, file backup for the path target) - nothing is ever left half-renamed.
/// </summary>
public interface IReportRenameTarget
{
    string Kind { get; }
    /// <summary>How (or whether) the rewrite reached disk - surfaced in the tool result.</summary>
    string PersistenceNote { get; }
    void Snapshot();
    object Rewrite(IReadOnlyDictionary<string, string> fieldMap, IReadOnlyDictionary<string, string> entityMap);
    /// <summary>Persist to disk for path-based targets (backup first); no-op for session targets.</summary>
    void Flush();
    void Restore();
}

/// <summary>Resolves a reportSource argument to a writable rename target - the same resolution
/// order as the Wave G3 cross-layer readers (legacy session, PBIR session, then a path).</summary>
public static class ReportRenameTargets
{
    public static IReportRenameTarget? Resolve(ReportService report, PbirService pbir, string? reportSource)
    {
        if (string.IsNullOrWhiteSpace(reportSource)) return null;
        string src = reportSource.Trim();
        try { return new LegacyReportRenameTarget(report, report.SessionOf(src)); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unknown reportSessionId")) { }
        try { return new PbirSessionRenameTarget(pbir.SessionModel(src)); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Unknown pbirSessionId")) { }
        if (File.Exists(src) || Directory.Exists(src)) return new PbirPathRenameTarget(pbir, src);
        throw new InvalidOperationException(
            $"reportSource '{src}' is not an open report session, an open PBIR session, or an existing path.");
    }
}

/// <summary>Legacy Report/Layout session target: rewrites in memory through the fix_broken_visuals
/// machinery; save_report persists later. Restore swaps the deep-cloned root back.</summary>
public sealed class LegacyReportRenameTarget : IReportRenameTarget
{
    private readonly ReportService _svc;
    private readonly ReportSession _session;
    private JsonObject? _rootSnapshot;
    private bool _dirtySnapshot;

    public LegacyReportRenameTarget(ReportService svc, ReportSession session)
    {
        _svc = svc;
        _session = session;
    }

    public string Kind => "legacy Layout (reportSessionId)";
    public string PersistenceNote => "rewritten in the open report session - run save_report to persist to the .pbix";

    public void Snapshot()
    {
        _rootSnapshot = (JsonObject)_session.Layout.Root.DeepClone();
        _dirtySnapshot = _session.Dirty;
    }

    public object Rewrite(IReadOnlyDictionary<string, string> fieldMap, IReadOnlyDictionary<string, string> entityMap)
        => _svc.PropagateRenamesLegacy(_session, fieldMap, entityMap);

    public void Flush() { /* session target - save_report is the explicit persist step */ }

    public void Restore()
    {
        if (_rootSnapshot == null) return;
        _session.Layout = new ReportLayout { Root = _rootSnapshot, LayoutPartName = _session.Layout.LayoutPartName };
        _session.Dirty = _dirtySnapshot;
        _rootSnapshot = null;
    }
}

/// <summary>Open PBIR session target: rewrites the in-memory definition tree; save_pbir persists
/// later. Restore puts every entry's parsed JSON and dirty flag back from the snapshot.</summary>
public sealed class PbirSessionRenameTarget : IReportRenameTarget
{
    private readonly PbirService.PbirModel _model;
    private Dictionary<string, (JsonObject? Json, bool Dirty)>? _snapshot;

    public PbirSessionRenameTarget(PbirService.PbirModel model)
    {
        _model = model;
    }

    public string Kind => "PBIR (pbirSessionId)";
    public string PersistenceNote => "rewritten in the open PBIR session - run save_pbir to persist";

    public void Snapshot()
    {
        _snapshot = new Dictionary<string, (JsonObject?, bool)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rel in _model.Order)
        {
            var e = _model.Entries[rel];
            _snapshot[rel] = (e.Json?.DeepClone() as JsonObject, e.Dirty);
        }
    }

    public object Rewrite(IReadOnlyDictionary<string, string> fieldMap, IReadOnlyDictionary<string, string> entityMap)
        => ReportService.PropagateRenamesPbir(_model, fieldMap, entityMap);

    public void Flush() { /* session target - save_pbir is the explicit persist step */ }

    public void Restore()
    {
        if (_snapshot == null) return;
        foreach (var (rel, snap) in _snapshot)
            if (_model.Entries.TryGetValue(rel, out var e))
            {
                if (snap.Json != null) e.Json = snap.Json;
                e.Dirty = snap.Dirty;
            }
        _snapshot = null;
    }
}

/// <summary>PBIR path target (.pbix or PBIP folder): opens the tree, rewrites, and Flush WRITES the
/// changed files with a backup first - a .pbix is copied whole, a folder backs up each changed file
/// in memory. Restore puts the flushed files back exactly, so a model-commit failure after the
/// flush still leaves the report byte-identical to before the rename.</summary>
public sealed class PbirPathRenameTarget : IReportRenameTarget
{
    private readonly PbirService _svc;
    private readonly string _path;
    private readonly PbirService.PbirModel _model;
    private string? _pbixBackup;
    private string? _folderRoot;
    private Dictionary<string, byte[]?>? _fileBackups;   // rel -> original bytes (null = file did not exist)
    private bool _flushed;

    public PbirPathRenameTarget(PbirService svc, string path)
    {
        _svc = svc;
        _path = path;
        _model = svc.OpenModel(path);
    }

    public string Kind => _model.FromPbix ? "PBIR (.pbix path)" : "PBIR (PBIP folder path)";
    public string PersistenceNote => _flushed
        ? "written to disk (a backup was taken first)"
        : "no binding referenced a renamed object - nothing was written";

    public void Snapshot() { /* the parsed tree is transient - disk state is captured at Flush */ }

    public object Rewrite(IReadOnlyDictionary<string, string> fieldMap, IReadOnlyDictionary<string, string> entityMap)
        => ReportService.PropagateRenamesPbir(_model, fieldMap, entityMap);

    public void Flush()
    {
        var dirty = _model.Order.Where(p => _model.Entries[p].Dirty).ToList();
        if (dirty.Count == 0) return;

        if (_model.FromPbix)
        {
            string bak = _path + ".rename-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            int n = 1;
            while (File.Exists(bak)) bak = _path + ".rename-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + (n++);
            File.Copy(_path, bak, overwrite: false);
            _pbixBackup = bak;
        }
        else
        {
            _folderRoot = PbirService.ResolveFolderRoot(_model);
            _fileBackups = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var rel in dirty)
            {
                string full = Path.Combine(_folderRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                _fileBackups[rel] = File.Exists(full) ? File.ReadAllBytes(full) : null;
            }
        }
        _svc.SaveModel(_model);
        _flushed = true;
    }

    public void Restore()
    {
        if (!_flushed) return;   // nothing reached disk
        if (_pbixBackup != null && File.Exists(_pbixBackup))
        {
            // stage-then-swap so a crash mid-restore never leaves a truncated .pbix
            string tmp = _path + ".restore-" + Guid.NewGuid().ToString("N");
            File.Copy(_pbixBackup, tmp, overwrite: false);
            File.Move(tmp, _path, overwrite: true);
        }
        else if (_folderRoot != null && _fileBackups != null)
        {
            foreach (var (rel, bytes) in _fileBackups)
            {
                string full = Path.Combine(_folderRoot, rel.Replace('/', Path.DirectorySeparatorChar));
                if (bytes == null) { if (File.Exists(full)) File.Delete(full); }
                else File.WriteAllBytes(full, bytes);
            }
        }
        _flushed = false;
    }
}
