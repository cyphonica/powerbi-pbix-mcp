using System.Text;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Jobs;

/// <summary>
/// One JSON line per phase, appended to {Root}\{jobId}\logs\job.jsonl - the forensic trail of a job.
///
/// It never writes to Console.Out. stdout is the JSON-RPC channel, so one stray line there breaks MCP framing
/// and any caller piping a CLI verb into a JSON parser. In service mode Console.Out and Console.Error are the
/// same file writer, which is why this is a rule of the class and not a thing to notice in testing: the
/// distinction that would catch the bug does not exist where the server actually runs.
///
/// Every write is swallowed. A log that throws, blocks or fails a job is worse than a log with a gap in it.
///
/// The line's key set and order are fixed and additive-only forever - the trail is parsed by line, so a
/// renamed or reordered key is a breaking change:
///   ts jobId tenant phase pid msmdsrvPort ramFreeMB cFreeGB dFreeGB event ok [detail]
/// A summary line carries the same keys with phase "summary", then final elapsedSec sha256 bytes error.
///
/// Phase vocabulary (closed): submit admit preflight launch connect refresh save verify retain reap summary.
///
/// cFreeGB and dFreeGB carry the resolved job-root and temp volumes, NOT the drives those names suggest: the
/// letters are historical and a host need not have a D: at all. A reading of -1 is "unknown" (unreadable, or
/// one volume serving both paths), never "no space", and reading a volume never throws.
/// </summary>
internal sealed class JobLog : IDisposable
{
    private const int LineBufferBytes = 8192;

    private readonly object _gate = new();
    private readonly string _jobId;
    private readonly string _tenant;
    private StreamWriter? _writer;

    /// <summary>Opens {Root}\{jobId}\logs\job.jsonl for append, creating the logs dir if the tree is not there yet.</summary>
    internal static JobLog ForJob(string jobId, string tenantId)
        => new(jobId, tenantId, JobPaths.JobLogPath(jobId));

    internal JobLog(string jobId, string tenantId, string jsonlPath)
    {
        _jobId = jobId;
        _tenant = JobPaths.SafeTenant(tenantId);
        Path = jsonlPath;

        try
        {
            string? dir = System.IO.Path.GetDirectoryName(jsonlPath);
            if (dir is { Length: > 0 }) Directory.CreateDirectory(dir);

            // FileShare.ReadWrite: a sibling process tailing or appending must never take the log - or the job - down.
            // The buffer holds a whole line, so an append is one write and two writers cannot split each other's.
            var fs = new FileStream(jsonlPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _writer = new StreamWriter(fs, new UTF8Encoding(false), LineBufferBytes) { AutoFlush = true };
        }
        catch
        {
            _writer = null;   // an unopenable log is a job that runs without a trail, not a job that fails
        }
    }

    internal string Path { get; }

    /// <summary>
    /// One phase line. An absent pid or port is 0, not null, so the key set holds its shape and its type across
    /// every line. Pass the <paramref name="host"/> reading the caller already has; with none, one is taken.
    /// </summary>
    internal void Phase(string phase, string @event, bool ok,
                        int? pid = null, int? msmdsrvPort = null,
                        HostSnapshot? host = null, string? detail = null)
    {
        var line = Line(phase, @event, ok, pid, msmdsrvPort, host);
        if (line is null) return;
        if (detail is not null) line["detail"] = detail;
        Write(line);
    }

    /// <summary>The job's last line: how it ended, and what it produced.</summary>
    internal void Summary(JobState final, TimeSpan elapsed, string? sha256, long? bytes, string? error)
    {
        var line = Line("summary", final.ToString().ToLowerInvariant(), final == JobState.DONE, null, null, null);
        if (line is null) return;

        line["final"] = final.ToString();
        line["elapsedSec"] = Math.Round(elapsed.TotalSeconds, 1);
        line["sha256"] = sha256;
        line["bytes"] = bytes;
        line["error"] = error;
        Write(line);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }

    private JsonObject? Line(string phase, string @event, bool ok, int? pid, int? msmdsrvPort, HostSnapshot? host)
    {
        if (_writer is null) return null;   // nothing to write to: do not pay for the reading either

        var h = host ?? Host();
        return new JsonObject
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("o"),
            ["jobId"] = _jobId,
            ["tenant"] = _tenant,
            ["phase"] = phase,
            ["pid"] = pid ?? 0,
            ["msmdsrvPort"] = msmdsrvPort ?? 0,
            ["ramFreeMB"] = h.RamFreeMB,
            ["cFreeGB"] = h.CFreeGB,
            ["dFreeGB"] = h.DFreeGB,
            ["event"] = @event,
            ["ok"] = ok,
        };
    }

    private void Write(JsonObject line)
    {
        try
        {
            string text = line.ToJsonString();
            lock (_gate)
            {
                // One WriteLine per line under the gate, on an AutoFlush writer opened for append: a line is
                // whole and in order, and never interleaved with another thread's.
                _writer?.WriteLine(text);
            }
        }
        catch { }
    }

    private static HostSnapshot Host()
    {
        try { return HostResources.Probe(JobPaths.Root); }
        catch { return new HostSnapshot(-1, -1, null, DateTimeOffset.UtcNow); }
    }
}
