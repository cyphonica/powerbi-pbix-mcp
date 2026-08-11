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
