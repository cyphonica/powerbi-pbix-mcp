using System.Security.Cryptography;

namespace SuperBiMcp.Jobs;

/// <summary>A retained artifact: where it landed, the sha256 of its bytes, and its size.</summary>
internal readonly record struct RetainedArtifact(string Path, string Sha256, long Bytes);

/// <summary>
/// A retained artifact exists under this tenant and jobId with DIFFERENT bytes. Never resolved by overwriting:
/// the jobId is the idempotency key, so two different artifacts under one key means the key was reused, and the
/// first artifact is the one a caller may already have been told about.
/// </summary>
internal sealed class RetentionConflictException : Exception
{
    internal RetentionConflictException(string message) : base(message) { }
}

/// <summary>
/// Exactly-once artifact production and immutable retention.
///
/// The jobId IS the idempotency key. A DONE jobId resubmitted returns the retained artifact and the engine
/// never re-runs, so retention is what makes the work billable-once: the queue only flips DONE after the bytes
/// provably exist here under a matching sha256.
///
/// Immutable means immutable. A final that already exists is either the same bytes (a replay - returned as-is,
/// not rewritten) or a conflict (thrown). Nothing here ever overwrites or deletes a retained artifact.
///
/// The artifact lands by temp-then-rename, so a reader never sees a half-written final: a rename on one volume
/// is atomic, and every temp name is created inside the retained dir itself so the rename stays on it.
/// </summary>
internal static class RetentionStore
{
    private const string ShaSuffix = ".sha256";
    private const string TempMarker = ".tmp-";

    internal static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    /// <summary>
    /// Moves <paramref name="producedPath"/> into {Root}\_retained\{tenant}\{jobId}\{finalName} via a
    /// same-volume temp-then-rename. An existing final with the SAME sha is returned as-is; a DIFFERENT sha
    /// throws <see cref="RetentionConflictException"/>. The caller's file is never deleted.
    /// </summary>
    internal static RetainedArtifact Retain(string tenantId, string jobId, string producedPath, string finalName)
    {
        string leaf = RequireLeaf(finalName);

        if (!File.Exists(producedPath))
            throw new InvalidOperationException($"Nothing to retain: '{producedPath}' does not exist.");
        if (new FileInfo(producedPath).Length == 0)
            throw new InvalidOperationException($"Nothing to retain: '{producedPath}' is empty.");

        string sha = Sha256File(producedPath);

        string dir = JobPaths.RetainedDir(tenantId, jobId);
        Directory.CreateDirectory(dir);
        string final = System.IO.Path.Combine(dir, leaf);

        if (File.Exists(final))
        {
            string existing = Sha256File(final);
            if (SameSha(existing, sha)) return Artifact(final, existing);   // replay: the work is already retained
            throw new RetentionConflictException(
                $"{final} already exists with sha {existing}, refusing to overwrite retained artifact with {sha}");
        }

        string tmp = final + TempMarker + Guid.NewGuid().ToString("N");

        // Same volume: the produced file MOVES, so tmp then holds the only copy of the artifact and must never be
        // deleted. Cross volume: tmp is a copy we made, the caller's file stays where it is (the job dir is swept
        // later), and only that copy is ours to remove.
        bool tmpIsOnlyCopy = SameVolume(producedPath, tmp);

        try
        {
            if (tmpIsOnlyCopy) File.Move(producedPath, tmp);
            else File.Copy(producedPath, tmp, false);

            File.Move(tmp, final);
            File.WriteAllText(final + ShaSuffix, sha + "  " + leaf + "\n");
            return Artifact(final, sha);
        }
        catch
        {
            // A racing retain of the same jobId can land `final` between the check above and the rename. The
            // artifact is immutable, so the loser reconciles against the bytes on disk rather than overwriting.
            if (File.Exists(final) && File.Exists(tmp))
            {
                string landed = Sha256File(final);
                if (SameSha(landed, sha))
                {
                    TryDelete(tmp);   // `final` already carries these bytes: tmp is redundant, whoever made it
                    return Artifact(final, landed);
                }

                Unwind(tmp, producedPath, tmpIsOnlyCopy);
                throw new RetentionConflictException(
                    $"{final} already exists with sha {landed}, refusing to overwrite retained artifact with {sha}");
            }

            Unwind(tmp, producedPath, tmpIsOnlyCopy);
            throw;
        }
    }

    /// <summary>The retained artifact under this name, or null. The sha is recomputed from the bytes on disk and
    /// verified against the sidecar, so a corrupted or swapped artifact is never served as the job's result.</summary>
    internal static RetainedArtifact? Find(string tenantId, string jobId, string finalName)
    {
        string final = System.IO.Path.Combine(JobPaths.RetainedDir(tenantId, jobId), RequireLeaf(finalName));
        return File.Exists(final) ? ReadBack(final) : null;
    }

    /// <summary>The job's retained artifact, whatever it is named. Phase 0 retains one artifact per job; the
    /// ordinal ordering only makes "whichever" deterministic if that ever stops being true.</summary>
    internal static RetainedArtifact? FindAny(string tenantId, string jobId)
    {
        string dir = JobPaths.RetainedDir(tenantId, jobId);
        if (!Directory.Exists(dir)) return null;

        string? final = Directory.EnumerateFiles(dir)
            .Where(p => !IsOurs(p))
            .OrderBy(System.IO.Path.GetFileName, StringComparer.Ordinal)
            .FirstOrDefault();

        return final is null ? null : ReadBack(final);
    }

    /// <summary>Hash the retained bytes now, and hold them to the sha recorded when they were retained.</summary>
    private static RetainedArtifact ReadBack(string final)
    {
        string sha = Sha256File(final);

        string sidecar = final + ShaSuffix;
        if (File.Exists(sidecar))
        {
            string recorded = File.ReadAllText(sidecar).Trim().Split(' ')[0];
            if (recorded.Length > 0 && !SameSha(recorded, sha))
                throw new RetentionConflictException(
                    $"{final} hashes to {sha} but was retained as {recorded}: the retained artifact changed on disk.");
        }

        return Artifact(final, sha);
    }

    /// <summary>Put a failed retain back the way it was. tmp holding the only copy is moved back to the caller's
    /// path; if even that fails it stays under its temp name, which is recoverable - a delete is not.</summary>
    private static void Unwind(string tmp, string producedPath, bool tmpIsOnlyCopy)
    {
        if (!File.Exists(tmp)) return;

        if (!tmpIsOnlyCopy)
        {
            TryDelete(tmp);
            return;
        }

        try { File.Move(tmp, producedPath); }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private static RetainedArtifact Artifact(string final, string sha)
        => new(final, sha, new FileInfo(final).Length);

    private static bool SameSha(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when both paths sit on one volume, so File.Move between them is an atomic rename.</summary>
    private static bool SameVolume(string a, string b)
        => string.Equals(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(a)),
                         System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(b)),
                         StringComparison.OrdinalIgnoreCase);

    /// <summary>A file this class writes beside an artifact, rather than an artifact.</summary>
    private static bool IsOurs(string path)
    {
        string name = System.IO.Path.GetFileName(path);
        return name.EndsWith(ShaSuffix, StringComparison.OrdinalIgnoreCase) || name.Contains(TempMarker, StringComparison.Ordinal);
    }

    /// <summary>The artifact name is a leaf and only a leaf: tenant and jobId are already sanitised into the
    /// retained path, and this is the one remaining segment a caller supplies.</summary>
    private static string RequireLeaf(string finalName)
    {
        // "." and ".." survive Path.GetFileName unchanged - they carry no separator yet still navigate, and
        // Combine(dir, "..") names the tenant directory itself.
        if (string.IsNullOrWhiteSpace(finalName)
            || finalName is "." or ".."
            || !string.Equals(System.IO.Path.GetFileName(finalName), finalName, StringComparison.Ordinal))
            throw new ArgumentException($"Not an artifact file name: '{finalName}'.", nameof(finalName));
        return finalName;
    }
}
