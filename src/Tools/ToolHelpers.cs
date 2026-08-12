using System.Text.Json;

namespace SuperBiMcp.Tools;

internal static class J
{
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static string Of(object o) => JsonSerializer.Serialize(o, Opts);

    /// <summary>Run a tool body, returning its result as JSON or a clean error object.</summary>
    public static string Try(Func<object> body)
    {
        try { return Of(body()); }
        catch (Exception ex) { return Of(new { ok = false, error = ex.Message, type = ex.GetType().Name }); }
    }
}

/// <summary>Wave G4: the live-vs-offline dispatch for the generators that accept either a live
/// sessionId or a pbipFolder (target=pbip, engine-free TMDL). Exactly one must be supplied.</summary>
internal static class GeneratorTarget
{
    public static object Run(string? sessionId, string? pbipFolder, Func<object> live, Func<object> offline)
    {
        bool hasSession = !string.IsNullOrWhiteSpace(sessionId);
        bool hasPbip = !string.IsNullOrWhiteSpace(pbipFolder);
        if (hasSession == hasPbip)
            throw new ArgumentException("Pass exactly one of sessionId (live engine) or pbipFolder (offline TMDL target).");
        return hasPbip ? offline() : live();
    }
}
