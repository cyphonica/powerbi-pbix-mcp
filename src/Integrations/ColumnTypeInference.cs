using System.Globalization;

namespace SuperBiMcp.Integrations;

/// <summary>
/// Infers a column's data type from a sample of its values, producing one of the spec data-type tokens
/// <see cref="Headless.MapType"/> understands: int64 | double | date | boolean | string. This drives both
/// the inferred schema (for the no-Solution AI-auto-shape path) and the <c>Table.TransformColumnTypes</c>
/// step in the synthesised model spec.
///
/// Rule: sample the non-empty values and widen to the narrowest type that fits them ALL. All integers
/// -&gt; int64; all decimals (or a mix of int + decimal) -&gt; double; all dates -&gt; date; all true/false
/// -&gt; boolean; otherwise string. An empty or all-blank column falls back to string. Inference is
/// invariant-culture (matching the engine's InvariantGlobalization setting), with a couple of common
/// unambiguous date layouts accepted (ISO and dd/MM/yyyy).
/// </summary>
public static class ColumnTypeInference
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd HH:mm:ss",
        "dd/MM/yyyy", "d/M/yyyy", "MM/dd/yyyy",
    };

    /// <summary>Infer the spec data-type token for a column given a sample of its raw string values.</summary>
    public static string Infer(IEnumerable<string?> sample)
    {
        bool any = false, allInt = true, allNum = true, allDate = true, allBool = true;

        foreach (var raw in sample)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            any = true;
            string v = raw.Trim();

            if (allInt && !long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                allInt = false;
            if (allNum && !double.TryParse(v, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out _))
                allNum = false;
            if (allDate && !IsDate(v))
                allDate = false;
            if (allBool && !IsBool(v))
                allBool = false;

            // once nothing numeric/date/bool fits, it can only be string - stop early
            if (!allNum && !allDate && !allBool) break;
        }

        if (!any) return "string";
        if (allBool) return "boolean";
        if (allInt) return "int64";
        if (allNum) return "double";
        if (allDate) return "date";
        return "string";
    }

    private static bool IsDate(string v)
        => DateTime.TryParseExact(v, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        || DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static bool IsBool(string v)
        => v.Equals("true", StringComparison.OrdinalIgnoreCase)
        || v.Equals("false", StringComparison.OrdinalIgnoreCase);
}
