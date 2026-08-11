using System.Text;

namespace SuperBiMcp.Jobs;

/// <summary>
/// The entire on-disk layout of the job root: pure path derivation plus lazy directory creation.
///
/// Every jobId-derived path is gated on <see cref="JobId.IsValid"/>. That guard is the boundary that keeps a
/// hostile or malformed id inside the root - a fixed alphabet at a fixed length carries no separator, no drive
/// letter and no "..", so no caller can reach a path outside <see cref="Root"/> by naming one.
///
/// This is a DIFFERENT root and a DIFFERENT key space from a host's tenant store (%TEMP%/superbi-jobs/{tenant}/),
/// which is untouched. The two only agree on how a tenant id becomes a path segment (see <see cref="SafeTenant"/>).
/// </summary>
internal static class JobPaths
{
    /// <summary>The root's own leaf, under the server's existing job store. Reserved: never a tenant segment.</summary>
    private const string QueueDirName = "_queue";

    /// <summary>The retained-artifact subtree's leaf, under the root. Reserved: never a tenant segment.</summary>
    private const string RetainedDirName = "_retained";

    /// <summary>Where a tenant id lands when it sanitises to nothing, matching the host tenant store.</summary>
    private const string FallbackTenant = "tenant";

    private static readonly string[] JobSubdirs = { "in", "work", "out", "temp", "wv2", "quarantine", "logs" };

    /// <summary>
    /// Resolved per get, never cached: RootForTest, else env SUPERBI_JOBS_ROOT, else the server's existing job
    /// store (env SUPERBI_JOBROOT) plus a reserved leaf, else a temp fallback under %TEMP%/superbi-jobs.
    ///
    /// The root is derived from what the host already uses rather than a fixed drive: the box this runs on has
    /// exactly one fixed volume, and a hard-coded root on any other letter throws on the first job.
    /// </summary>
    internal static string Root
    {
        get
        {
            if (RootForTest is { Length: > 0 } forTest) return forTest;
            if (Environment.GetEnvironmentVariable("SUPERBI_JOBS_ROOT") is { Length: > 0 } explicitRoot) return explicitRoot;
            if (Environment.GetEnvironmentVariable("SUPERBI_JOBROOT") is { Length: > 0 } serverRoot)
                return Path.Combine(serverRoot, QueueDirName);
            return Path.Combine(Path.GetTempPath(), "superbi-jobs", QueueDirName);
        }
    }

    /// <summary>null = use env/default. The setter deliberately has no side effect: it does not create the
    /// directory, so a test can point the root at a path that must not exist yet.</summary>
    internal static string? RootForTest { get; set; }

    internal static string QueueDbPath => Path.Combine(Root, "queue.db");

    internal static string RetainedRoot => Path.Combine(Root, RetainedDirName);

    internal static string JobDir(string jobId) => Path.Combine(Root, RequireJobId(jobId));

    internal static string In(string jobId) => Path.Combine(JobDir(jobId), "in");

    internal static string Work(string jobId) => Path.Combine(JobDir(jobId), "work");

    internal static string Out(string jobId) => Path.Combine(JobDir(jobId), "out");

    internal static string Temp(string jobId) => Path.Combine(JobDir(jobId), "temp");

    internal static string Wv2(string jobId) => Path.Combine(JobDir(jobId), "wv2");

    internal static string QuarantineDir(string jobId) => Path.Combine(JobDir(jobId), "quarantine");

    internal static string Logs(string jobId) => Path.Combine(JobDir(jobId), "logs");

    internal static string JobLogPath(string jobId) => Path.Combine(Logs(jobId), "job.jsonl");

    internal static string RetainedDir(string tenantId, string jobId)
        => Path.Combine(RetainedRoot, SafeTenant(tenantId), RequireJobId(jobId));

    /// <summary>
    /// Sanitise a tenant id into one safe path segment, by the same rule as the host tenant store so a tenant
    /// keeps one name across both trees: anything outside letters, digits, '-' and '_' becomes '_', and an id
    /// that sanitises to nothing becomes "tenant".
    ///
    /// The reserved leaves are additionally never returned: the sanitising alphabet admits '_', so a tenant
    /// literally named "_queue" or "_retained" would otherwise name part of the tree itself. Directory names
    /// compare OrdinalIgnoreCase, so the guard must too.
    /// </summary>
    internal static string SafeTenant(string? raw)
    {
        if (raw is null) return FallbackTenant;

        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
            sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');

        string s = sb.ToString();
        if (s.Length == 0) return FallbackTenant;

        // One pass is enough: prefixing an underscore cannot produce a reserved name.
        return IsReserved(s) ? "_" + s : s;
    }

    /// <summary>Creates the job's subdirs. Idempotent.</summary>
    internal static void CreateJobTree(string jobId)
    {
        string dir = JobDir(jobId);
        foreach (string sub in JobSubdirs)
            Directory.CreateDirectory(Path.Combine(dir, sub));
    }

    /// <summary>Creates the root and its retained subtree. Idempotent.</summary>
    internal static void CreateRoot()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(RetainedRoot);
    }

    private static bool IsReserved(string segment)
        => string.Equals(segment, QueueDirName, StringComparison.OrdinalIgnoreCase)
        || string.Equals(segment, RetainedDirName, StringComparison.OrdinalIgnoreCase);

    private static string RequireJobId(string jobId)
        => JobId.IsValid(jobId) ? jobId : throw new ArgumentException($"Not a job id: '{jobId}'.", nameof(jobId));
}
