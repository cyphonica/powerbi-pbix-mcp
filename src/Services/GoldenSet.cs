using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperBiMcp.Services;

/// <summary>
/// Measure-regression golden sets: a git-friendly JSON baseline of scalar DAX results that
/// run_golden_set replays against the live model and compares number-for-number. The file is
/// deterministic (sorted by name, indented, UTF-8 no BOM, no timestamps) so a re-capture with an
/// unchanged model produces a byte-identical file. Only pure parse/render/compare logic lives
/// here - evaluation is ModelService's job - so every rule is unit-testable offline.
/// </summary>
public static class GoldenSet
{
    /// <summary>Absolute numeric tolerance used by run_golden_set.</summary>
    internal const double DefaultTolerance = 1e-6;

    private const string ErrorMarkerPrefix = "{\"error\":";

    public sealed class Golden
    {
        [JsonPropertyName("name")] public required string Name { get; init; }
        [JsonPropertyName("dax")] public required string Dax { get; init; }
        [JsonPropertyName("expected")] public required string Expected { get; init; }
    }

    private sealed class GoldenFile
    {
        [JsonPropertyName("goldens")] public List<Golden> Goldens { get; set; } = new();
    }

    private static readonly JsonSerializerOptions FileOpts = new() { WriteIndented = true };

    // ---------------------------------------------------------------- file round-trip
    public static void Save(string path, IEnumerable<Golden> goldens)
    {
        var file = new GoldenFile
        {
            Goldens = goldens.OrderBy(g => g.Name, StringComparer.Ordinal).ToList(),
        };
        File.WriteAllText(path, JsonSerializer.Serialize(file, FileOpts), new UTF8Encoding(false));
    }

    public static IReadOnlyList<Golden> Load(string path)
    {
        var file = JsonSerializer.Deserialize<GoldenFile>(File.ReadAllText(path))
            ?? throw new InvalidOperationException($"'{path}' is not a golden-set file.");
        return file.Goldens;
    }

    // ---------------------------------------------------------------- rendering
    /// <summary>Invariant-culture rendering of a scalar DAX result (null/BLANK renders empty).</summary>
    internal static string RenderValue(object? value) => value switch
    {
        null => "",
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Error-state marker: a golden captured while the measure errors is stored as this,
    /// so a regression to (or recovery from) an error state fails the run like any wrong number.</summary>
    internal static string RenderError(string message) =>
        ErrorMarkerPrefix + JsonSerializer.Serialize(message) + "}";

    internal static bool IsError(string rendering) =>
        rendering.StartsWith(ErrorMarkerPrefix, StringComparison.Ordinal);

    // ---------------------------------------------------------------- compare
    /// <summary>Numeric-vs-numeric within tolerance (delta returned), error-state vs error-state
    /// (messages ignored - engine wording drifts), otherwise ordinal string compare.</summary>
    internal static (bool Pass, double? Delta) Compare(string expected, string actual, double tolerance)
    {
        bool expErr = IsError(expected), actErr = IsError(actual);
        if (expErr || actErr) return (expErr && actErr, null);
        if (TryParseNumber(expected, out double e) && TryParseNumber(actual, out double a))
        {
            double delta = Math.Abs(a - e);
            return (delta <= tolerance, delta);
        }
        return (string.Equals(expected, actual, StringComparison.Ordinal), null);
    }

    internal static bool TryParseNumber(string s, out double value) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
