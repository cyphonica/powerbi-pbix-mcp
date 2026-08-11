namespace SuperBiMcp.Jobs;

/// <summary>
/// Heavy is a runtime property, not a caller property. Build work only reaches msmdsrv.exe when
/// SUPERBI_AS_SERVER is set AND the engine binary is installed; without both,
/// <see cref="Bake.BakeProject"/> returns baked:false and the job degrades to a PBIP zip that costs nothing.
/// Keying the lane on the job kind instead would serialise an entire scaffold-only deployment to one job.
/// </summary>
internal static class LaneClassifier
{
    /// <summary>routeCanBake: the caller's runtime answer to "will this work() closure reach Bake.BakeProject?".</summary>
    internal static Lane Classify(bool routeCanBake, string? asServer, string enginePath, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        // The same two conditions Bake.BakeProject itself gates the bake on, in the same order.
        return routeCanBake && !string.IsNullOrWhiteSpace(asServer) && fileExists(enginePath)
            ? Lane.Heavy
            : Lane.Cheap;
    }

    /// <summary>Production convenience: asServer is the host's configured AS server, against the installed engine.</summary>
    internal static Lane Classify(bool routeCanBake, string? asServer) =>
        Classify(routeCanBake, asServer, PbiEngine.ResolveEnginePath(), File.Exists);
}
