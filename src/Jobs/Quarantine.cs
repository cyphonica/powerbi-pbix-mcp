namespace SuperBiMcp.Jobs;

/// <summary>One artifact a sweep may move aside. Kind: "tempsave" | "workspace" | "wv2-lock" | "autorecovery-lock".</summary>
internal readonly record struct QuarantineCandidate(string Path, bool IsDirectory, string Kind);

/// <summary>What a sweep did to one candidate. Outcome: "moved" | "skipped-live" | "skipped-missing" | "move-failed:&lt;reason&gt;".</summary>
internal readonly record struct QuarantineAction(string From, string? To, string Outcome);

/// <summary>
/// Pre-flight hygiene for a Desktop launch: stale TempSaves, WebView2 locks and AutoRecovery locks left by a
/// crashed prior run are MOVED ASIDE to "{path}.quarantine-{jobId}" in the same directory.
///
/// Two rules this class exists to hold:
///
/// Nothing is ever deleted. The rename is same-directory, therefore same-volume, therefore a metadata
/// operation that can never degrade into a copy-then-delete. A Move that fails is RECORDED and skipped; there
/// is no delete fallback on any path, and none may be added.
///
/// Nothing with a live owner is touched. These artifacts are how a running Desktop finds its own state, so
/// moving a live sibling job's lock corrupts that sibling. Liveness is therefore proven before every Move,
/// and anything that cannot be PROVEN dead - an unreadable file, a probe that throws, a non-Windows host
/// where the probes do not apply - counts as live and is left alone.
/// </summary>
internal static class Quarantine
{
    private const string Suffix = ".quarantine-";

    /// <summary>Skips reparse points rather than walking through them: a junction under these roots would
    /// otherwise lead the enumeration out of the tree it is scoped to.</summary>
    private static readonly EnumerationOptions Shallow = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    private static readonly EnumerationOptions Deep = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
        MatchCasing = MatchCasing.CaseInsensitive,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    /// <summary>Candidates under the stock and Store-app Desktop roots. Pure over the roots it is given: a root
    /// that does not exist contributes nothing rather than throwing.</summary>
    internal static IReadOnlyList<QuarantineCandidate> Enumerate(string localAppData, string userProfile)
    {
        var found = new List<QuarantineCandidate>();

        if (!string.IsNullOrWhiteSpace(localAppData))
            AddRoot(found, Path.Combine(localAppData, "Microsoft", "Power BI Desktop"));
        if (!string.IsNullOrWhiteSpace(userProfile))
            AddRoot(found, Path.Combine(userProfile, "Microsoft", "Power BI Desktop Store App"));

        // The EBWebView patterns can name the same entry twice on a case-insensitive volume.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        found.RemoveAll(c => !seen.Add(c.Path));
        return found;
    }

    /// <summary>
    /// True unless the candidate is PROVEN to have no owner. A lock or tempsave is dead only if it opens
    /// FileShare.None; a workspace is dead only if no msmdsrv answers on its recorded port AND nothing inside
    /// it is held open - the port file is written slightly after the engine starts, so the port alone would
    /// call a sibling's half-started workspace dead.
    /// </summary>
    internal static bool HasLiveOwner(QuarantineCandidate c)
    {
        // Off Windows none of these probes mean what they mean here, and an unproven candidate is never moved.
        if (!OperatingSystem.IsWindows()) return true;

        try
        {
            if (c.Kind == "workspace") return PortHasLiveOwner(c.Path) || HasLockedFile(c.Path);
            return c.IsDirectory ? HasLockedFile(c.Path) : IsLocked(c.Path);
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// Renames each dead candidate to "{path}.quarantine-{jobId}" in its own directory. Side effects are
    /// confined to the injected <paramref name="move"/>, so the whole decision path is provable offline.
    ///
    /// A Move that fails or refuses leaves the original exactly where it was: a Desktop launch that trips over
    /// a lock this could not move is a job that fails loudly, which is the cheap outcome. Deleting it instead
    /// would be the expensive one.
    /// </summary>
    internal static IReadOnlyList<QuarantineAction> Sweep(
        string jobId,
        IEnumerable<QuarantineCandidate> candidates,
        Func<QuarantineCandidate, bool> hasLiveOwner,
        Func<string, string, bool> move)
    {
        var actions = new List<QuarantineAction>();

        foreach (var c in candidates)
        {
            if (!Exists(c))
            {
                actions.Add(new QuarantineAction(c.Path, null, "skipped-missing"));
                continue;
            }

            bool live;
            try { live = hasLiveOwner(c); }
            catch { live = true; }      // a probe that throws has proven nothing

            if (live)
            {
                actions.Add(new QuarantineAction(c.Path, null, "skipped-live"));
                continue;
            }

            string to = c.Path + Suffix + jobId;
            try
            {
                actions.Add(move(c.Path, to)
                    ? new QuarantineAction(c.Path, to, "moved")
                    : new QuarantineAction(c.Path, to, "move-failed:refused"));
            }
            catch (Exception ex)
            {
                // The type, not the message: the message carries paths and is culture-dependent.
                actions.Add(new QuarantineAction(c.Path, to, $"move-failed:{ex.GetType().Name}"));
            }
        }

        return actions;
    }

    /// <summary>
    /// Production entry: sweep the real Desktop roots for this job.
    ///
    /// The profile root is a PARAMETER because the profile that matters is the one Desktop runs in, and that
    /// is not always the calling process's own: a Windows service (session 0) resolves its profile as
    /// C:\Windows\System32\config\systemprofile, while Desktop automation runs in the console session under
    /// the operator's profile. With no explicit root, a service-session caller therefore LOGS AND SWEEPS
    /// NOTHING - an honest no-op beats confidently sweeping a tree Desktop never uses and reporting success.
    /// <paramref name="serviceSession"/> is a test seam; production callers leave it null and the real session
    /// is probed.
    /// </summary>
    internal static IReadOnlyList<QuarantineAction> SweepReal(string jobId, string? profileRoot = null,
                                                              Action<string>? log = null, bool? serviceSession = null)
    {
        var sink = log ?? (_ => { });
        var roots = ResolveRoots(profileRoot,
                                 Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                 Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                                 serviceSession ?? IsServiceSession());
        if (roots is null)
        {
            sink("quarantine: service session with no explicit profile root - sweeping nothing rather than the " +
                 "wrong profile (this process's own profile is systemprofile, not the console session that runs Desktop)");
            return Array.Empty<QuarantineAction>();
        }

        return Sweep(jobId, Enumerate(roots.Value.localAppData, roots.Value.userProfile), HasLiveOwner, MoveReal);
    }

    /// <summary>
    /// Which profile a real sweep may touch. Pure; null means "no safe answer, sweep nothing". An explicit
    /// root always wins - the caller is asserting it knows which session Desktop runs in - and the calling
    /// process's own profile is only trustworthy outside a service session.
    /// </summary>
    internal static (string localAppData, string userProfile)? ResolveRoots(
        string? explicitProfileRoot, string defaultLocalAppData, string defaultUserProfile, bool serviceSession)
    {
        if (!string.IsNullOrWhiteSpace(explicitProfileRoot))
            return (Path.Combine(explicitProfileRoot, "AppData", "Local"), explicitProfileRoot);
        if (serviceSession) return null;
        return (defaultLocalAppData, defaultUserProfile);
    }

    /// <summary>Session 0 is where Windows services live; interactive logons are session 1+. The systemprofile
    /// path check is the belt to that: a LocalSystem service's resolved profile sits under config\systemprofile
    /// even when the session probe cannot be read. Anything unprovable reads as a service session, because not
    /// sweeping is the cheap mistake and sweeping the wrong profile is the expensive one.</summary>
    private static bool IsServiceSession()
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            if (self.SessionId == 0) return true;
        }
        catch { return true; }

        try
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return profile.Contains(@"\config\systemprofile", StringComparison.OrdinalIgnoreCase);
        }
        catch { return true; }
    }

    /// <summary>Same directory both sides, so this is a rename and never a copy. Failures propagate to
    /// <see cref="Sweep"/>, which records them.</summary>
    private static bool MoveReal(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
        return true;
    }

    private static void AddRoot(List<QuarantineCandidate> into, string root)
    {
        AddFiles(into, Path.Combine(root, "TempSaves"), "*", Shallow, "tempsave");
        AddDirs(into, Path.Combine(root, "TempSaves"), "*", "tempsave");
        AddDirs(into, Path.Combine(root, "AnalysisServicesWorkspaces"), "*", "workspace");
        AddFiles(into, Path.Combine(root, "User", "EBWebView"), "LOCK", Deep, "wv2-lock");
        AddFiles(into, Path.Combine(root, "User", "EBWebView"), "*.lock", Deep, "wv2-lock");
        AddFiles(into, Path.Combine(root, "AutoRecovery"), "*.lock", Shallow, "autorecovery-lock");
    }

    private static void AddFiles(List<QuarantineCandidate> into, string dir, string pattern,
                                 EnumerationOptions options, string kind)
    {
        foreach (string p in Enumerate(dir, pattern, options, directories: false))
            into.Add(new QuarantineCandidate(p, IsDirectory: false, kind));
    }

    private static void AddDirs(List<QuarantineCandidate> into, string dir, string pattern, string kind)
    {
        foreach (string p in Enumerate(dir, pattern, Shallow, directories: true))
            into.Add(new QuarantineCandidate(p, IsDirectory: true, kind));
    }

    /// <summary>Enumeration is best-effort per subtree: a root that is absent, denied or racing a Desktop that
    /// is removing its own state yields nothing rather than failing the launch it is meant to protect.</summary>
    private static IEnumerable<string> Enumerate(string dir, string pattern, EnumerationOptions options, bool directories)
    {
        List<string> hits = new();
        try
        {
            if (!Directory.Exists(dir)) return hits;
            hits.AddRange(directories
                ? Directory.EnumerateDirectories(dir, pattern, options)
                : Directory.EnumerateFiles(dir, pattern, options));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        // An earlier sweep's output is not an input to the next one.
        hits.RemoveAll(p => Path.GetFileName(p).Contains(Suffix, StringComparison.OrdinalIgnoreCase));
        return hits;
    }

    private static bool Exists(QuarantineCandidate c)
        => c.IsDirectory ? Directory.Exists(c.Path) : File.Exists(c.Path);

    private static bool PortHasLiveOwner(string workspaceDir)
    {
        foreach (string portFile in Enumerate(workspaceDir, "msmdsrv.port.txt", Deep, directories: false))
        {
            int port = ReadPort(portFile);
            if (port > 0 && DesktopInterop.FindMsmdsrvPidOnPort(port) != 0) return true;
        }
        return false;
    }

    /// <summary>The port file is written by the engine in one of several encodings and carries no newline, so
    /// this reads digits rather than trusting a format.</summary>
    private static int ReadPort(string portFile)
    {
        string text;
        try { text = File.ReadAllText(portFile); }
        catch { return 0; }

        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out int port)
               && port is > 0 and <= 65535
            ? port
            : 0;
    }

    private static bool HasLockedFile(string dir)
    {
        foreach (string f in Enumerate(dir, "*", Deep, directories: false))
            if (IsLocked(f)) return true;
        return false;
    }

    /// <summary>An exclusive open is the only evidence that nothing else holds the file. A read-only attribute
    /// denies write access without proving another opener, hence the second, narrower attempt.</summary>
    private static bool IsLocked(string path)
    {
        foreach (FileAccess access in new[] { FileAccess.ReadWrite, FileAccess.Read })
        {
            try
            {
                using var s = new FileStream(path, FileMode.Open, access, FileShare.None);
                return false;
            }
            catch (FileNotFoundException) { return false; }
            catch (DirectoryNotFoundException) { return false; }
            catch (IOException) { return true; }             // sharing violation: something holds it
            catch (UnauthorizedAccessException) { }          // may be the attribute, not an owner
        }
        return true;
    }
}
