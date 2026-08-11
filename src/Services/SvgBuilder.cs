using System.Globalization;
using System.Text;

namespace SuperBiMcp.Services;

/// <summary>
/// Pure generators for SVG-as-ImageUrl DAX measures (the David Bacci / Injae Park community technique):
/// a measure returns a "data:image/svg+xml;utf8,&lt;svg...&gt;" string and its DataCategory is set to
/// "ImageUrl", so Power BI renders the SVG inside table / matrix / card cells.
///
/// Encode-safety rules baked in (the #1 failure mode):
///   - every attribute inside the SVG uses SINGLE quotes (") -> (') so the SVG can sit inside a
///     double-quoted DAX string literal without escaping.
///   - a literal '#' in a colour is written as %23 (an un-encoded '#' breaks the data-URI fragment).
///   - the prefix is "data:image/svg+xml;utf8," (no base64) so the DAX stays human-readable.
///
/// Nothing here touches the model - these are static string builders so they are fully unit-testable.
/// </summary>
internal static class SvgBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The data-URI prefix every SVG measure returns.</summary>
    public const string Prefix = "data:image/svg+xml;utf8,";

    /// <summary>The DataCategory that makes a measure render its returned string as an image.</summary>
    public const string ImageUrlCategory = "ImageUrl";

    /// <summary>Make a colour safe to embed in an SVG inside a DAX string literal: # -> %23, " -> '.</summary>
    public static string EncodeColor(string? hex)
    {
        string c = (hex ?? "").Trim();
        if (c.Length == 0) c = "%23808080";
        c = c.Replace("\"", "'").Replace("#", "%23");
        return c;
    }

    /// <summary>Escape a DAX double-quoted string literal body (" -> "").</summary>
    public static string DaxStr(string s) => (s ?? "").Replace("\"", "\"\"");

    /// <summary>Wrap a static SVG body (already encode-safe) as the RETURN of a DAX measure - a plain
    /// string literal: "data:image/svg+xml;utf8,&lt;svg ...&gt;...&lt;/svg&gt;".</summary>
    public static string StaticReturn(string svgBody) => "\"" + DaxStr(Prefix + svgBody) + "\"";

    private static string N(double d) => d.ToString(Inv);

    // ---------------------------------------------------------------- 1. data bar
    /// <summary>
    /// DAX for an in-cell DATA BAR: a &lt;rect&gt; whose width scales to value / max. valueMeasure and
    /// maxMeasure are measure refs ([m] or Table[m]); when maxMeasure is omitted, max defaults to the
    /// largest absolute value in the visual's filter context (MAXX over ALLSELECTED is impractical inside a
    /// measure, so we fall back to ABS(value) giving a full bar - caller should pass a maxMeasure).
    /// align=left|right; negativeFill colours bars below zero.
    /// </summary>
    public static string DataBar(string valueMeasure, string? maxMeasure, string fill, string? negativeFill,
        double width, double height, string align)
    {
        string v = MeasureRef(valueMeasure);
        string mx = string.IsNullOrWhiteSpace(maxMeasure) ? $"ABS ( {v} )" : MeasureRef(maxMeasure!);
        string pos = EncodeColor(fill);
        string neg = EncodeColor(string.IsNullOrWhiteSpace(negativeFill) ? fill : negativeFill);
        bool rtl = align.Trim().Equals("right", StringComparison.OrdinalIgnoreCase);
        string w = N(width), h = N(height);

        // bar width = clamp(|value| / max, 0, 1) * width ; colour by sign.
        var sb = new StringBuilder();
        sb.Append($"VAR _v = {v}\n");
        sb.Append($"VAR _max = {mx}\n");
        sb.Append("VAR _ratio = IF ( _max = 0, 0, MIN ( 1, DIVIDE ( ABS ( _v ), _max ) ) )\n");
        sb.Append($"VAR _w = _ratio * {w}\n");
        sb.Append($"VAR _fill = IF ( _v < 0, \"{neg}\", \"{pos}\" )\n");
        // x is 0 (left) or (width - barwidth) (right) so right-aligned bars grow leftwards.
        string xExpr = rtl ? $"({w} - _w)" : "0";
        sb.Append("VAR _svg =\n");
        sb.Append($"    \"{DaxStr(Prefix)}\" &\n");
        sb.Append($"    \"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}'>\" &\n");
        sb.Append($"    \"<rect x='\" & {xExpr} & \"' y='0' width='\" & _w & \"' height='{h}' fill='\" & _fill & \"'/>\" &\n");
        sb.Append("    \"</svg>\"\n");
        sb.Append("RETURN _svg");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 2. sparkline
    /// <summary>
    /// DAX for an in-cell SPARKLINE over a category column: builds a min-max scaled point set with
    /// CONCATENATEX and emits a &lt;polyline&gt; (line) / &lt;path&gt; (area|gradient-area). showLastPoint adds a
    /// terminal marker; intercept draws a zero baseline. categoryColumn must be 'Table'[Col].
    /// </summary>
    public static string Sparkline(string valueMeasure, string categoryColumn, string kind,
        bool showLastPoint, bool intercept, string lineColor, double width, double height)
    {
        string v = MeasureRef(valueMeasure);
        string cat = ColumnRef(categoryColumn);
        string col = EncodeColor(lineColor);
        string w = N(width), h = N(height);
        bool area = kind.Contains("area", StringComparison.OrdinalIgnoreCase);
        bool gradient = kind.Equals("gradient-area", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        // index each category by its sort position (RANKX over the category itself), attach the value,
        // then scale x by the index and y by the value's position in the [min,max] range.
        sb.Append($"VAR _cats = VALUES ( {cat} )\n");
        sb.Append($"VAR _t = ADDCOLUMNS ( _cats, \"@v\", {v}, \"@i\", RANKX ( _cats, {cat},, ASC, DENSE ) )\n");
        sb.Append("VAR _n = COUNTROWS ( _t )\n");
        sb.Append("VAR _min = MINX ( _t, [@v] )\n");
        sb.Append("VAR _max = MAXX ( _t, [@v] )\n");
        sb.Append("VAR _range = IF ( _max - _min = 0, 1, _max - _min )\n");
        // x spreads evenly across the width; y flips so high values sit at the top.
        sb.Append("VAR _pts =\n");
        sb.Append("    CONCATENATEX (\n");
        sb.Append("        _t,\n");
        sb.Append($"        ( ( [@i] - 1 ) / MAX ( 1, _n - 1 ) * {w} ) & \",\" & ( {h} - DIVIDE ( [@v] - _min, _range ) * {h} ),\n");
        sb.Append("        \" \",\n");
        sb.Append("        [@i], ASC\n");
        sb.Append("    )\n");
        sb.Append("VAR _svg =\n");
        sb.Append($"    \"{DaxStr(Prefix)}\" &\n");
        sb.Append($"    \"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}'>\" &\n");
        if (gradient)
        {
            sb.Append($"    \"<defs><linearGradient id='g' x1='0' y1='0' x2='0' y2='1'><stop offset='0%' stop-color='\" & \"{col}\" & \"' stop-opacity='0.6'/><stop offset='100%' stop-color='\" & \"{col}\" & \"' stop-opacity='0'/></linearGradient></defs>\" &\n");
        }
        if (area)
        {
            // close the polyline down to the baseline for a filled area
            string fillRef = gradient ? "url(%23g)" : col;
            sb.Append($"    \"<polygon points='0,{h} \" & _pts & \" {w},{h}' fill='\" & \"{fillRef}\" & \"' stroke='none'/>\" &\n");
        }
        sb.Append($"    \"<polyline points='\" & _pts & \"' fill='none' stroke='\" & \"{col}\" & \"' stroke-width='1.5'/>\" &\n");
        if (intercept)
            sb.Append($"    \"<line x1='0' y1='\" & ( {h} - DIVIDE ( 0 - _min, _range ) * {h} ) & \"' x2='{w}' y2='\" & ( {h} - DIVIDE ( 0 - _min, _range ) * {h} ) & \"' stroke='%23999999' stroke-width='0.5' stroke-dasharray='2,2'/>\" &\n");
        if (showLastPoint)
            // marker at the final point: x = width, y = the last category's value position.
            sb.Append($"    \"<circle cx='{w}' cy='\" & ( {h} - DIVIDE ( MAXX ( FILTER ( _t, [@i] = _n ), [@v] ) - _min, _range ) * {h} ) & \"' r='2' fill='\" & \"{col}\" & \"'/>\" &\n");
        sb.Append("    \"</svg>\"\n");
        sb.Append("RETURN _svg");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 3. progress bar / bullet
    /// <summary>
    /// DAX for a PROGRESS BAR (track + fill scaled to value/target) or a BULLET (track + fill + target tick).
    /// kind=bar|bullet. trackColor is the background rail; fillColor is the achieved portion.
    /// </summary>
    public static string ProgressBar(string valueMeasure, string targetMeasure, string kind,
        string trackColor, string fillColor, double width, double height)
    {
        string v = MeasureRef(valueMeasure);
        string tgt = MeasureRef(targetMeasure);
        string track = EncodeColor(trackColor);
        string fill = EncodeColor(fillColor);
        string w = N(width), h = N(height);
        bool bullet = kind.Trim().Equals("bullet", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append($"VAR _v = {v}\n");
        sb.Append($"VAR _t = {tgt}\n");
        sb.Append("VAR _ratio = IF ( _t = 0, 0, MIN ( 1, DIVIDE ( _v, _t ) ) )\n");
        sb.Append($"VAR _fw = _ratio * {w}\n");
        sb.Append("VAR _svg =\n");
        sb.Append($"    \"{DaxStr(Prefix)}\" &\n");
        sb.Append($"    \"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}'>\" &\n");
        // track + fill
        sb.Append($"    \"<rect x='0' y='0' width='{w}' height='{h}' rx='2' fill='\" & \"{track}\" & \"'/>\" &\n");
        sb.Append($"    \"<rect x='0' y='0' width='\" & _fw & \"' height='{h}' rx='2' fill='\" & \"{fill}\" & \"'/>\" &\n");
        if (bullet)
            // target tick at x = width (target = 100%), drawn as a vertical mark
            sb.Append($"    \"<line x1='{w}' y1='0' x2='{w}' y2='{h}' stroke='%23333333' stroke-width='2'/>\" &\n");
        sb.Append("    \"</svg>\"\n");
        sb.Append("RETURN _svg");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 4. gauge
    /// <summary>
    /// DAX for a semicircular ARC GAUGE: a background arc + a value arc, the value mapped onto min..max.
    /// thresholds (ascending) optionally tint bands; here they drive the value-arc colour by which band
    /// the value falls into (last threshold whose bound the value exceeds).
    /// </summary>
    public static string Gauge(string valueMeasure, double min, double max,
        IReadOnlyList<(double at, string color)>? thresholds, string fillColor, double size)
    {
        string v = MeasureRef(valueMeasure);
        string fill = EncodeColor(fillColor);
        double r = size / 2 - 6;
        string cx = N(size / 2), cy = N(size / 2), rad = N(r), w = N(size), h = N(size / 2 + 8);

        // _fill is either the plain fill colour or a nested IF picking the highest band the value clears.
        string fillExpr = "\"" + fill + "\"";
        if (thresholds is { Count: > 0 })
        {
            var fe = new StringBuilder("\"" + fill + "\"");
            foreach (var (at, color) in thresholds.OrderBy(t => t.at))
                fe.Insert(0, "IF ( _v >= " + N(at) + ", \"" + EncodeColor(color) + "\", ").Append(" )");
            fillExpr = fe.ToString();
        }

        var sb = new StringBuilder();
        sb.Append($"VAR _v = {v}\n");
        sb.Append($"VAR _min = {N(min)}\n");
        sb.Append($"VAR _max = {N(max)}\n");
        sb.Append("VAR _frac = MIN ( 1, MAX ( 0, DIVIDE ( _v - _min, _max - _min ) ) )\n");
        // sweep the semicircle from 180deg (left) to 0deg (right). angle in radians.
        sb.Append("VAR _ang = PI() * ( 1 - _frac )\n");
        sb.Append($"VAR _ex = {cx} + {rad} * COS ( _ang )\n");
        sb.Append($"VAR _ey = {cy} - {rad} * SIN ( _ang )\n");
        sb.Append("VAR _fill = " + fillExpr + "\n");
        sb.Append("VAR _svg =\n");
        sb.Append($"    \"{DaxStr(Prefix)}\" &\n");
        sb.Append($"    \"<svg xmlns='http://www.w3.org/2000/svg' width='{w}' height='{h}'>\" &\n");
        // background semicircle
        sb.Append($"    \"<path d='M \" & ( {cx} - {rad} ) & \" {cy} A {rad} {rad} 0 0 1 \" & ( {cx} + {rad} ) & \" {cy}' fill='none' stroke='%23E6E9EF' stroke-width='8'/>\" &\n");
        // value arc
        sb.Append($"    \"<path d='M \" & ( {cx} - {rad} ) & \" {cy} A {rad} {rad} 0 0 1 \" & _ex & \" \" & _ey & \"' fill='none' stroke='\" & _fill & \"' stroke-width='8'/>\" &\n");
        sb.Append("    \"</svg>\"\n");
        sb.Append("RETURN _svg");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 5. threshold icon
    /// <summary>
    /// DAX for a THRESHOLD GLYPH: picks the rule whose [min,max) band contains the value and renders its
    /// glyph (a unicode char as &lt;text&gt;) in its colour. rules: ascending bands of {min, max, glyph, color}.
    /// </summary>
    public static string Icon(string valueMeasure, IReadOnlyList<(double min, double max, string? glyph, string? color)> rules,
        double size)
    {
        if (rules.Count == 0) throw new ArgumentException("at least one icon rule is required.");
        string v = MeasureRef(valueMeasure);
        string s = N(size);
        string mid = N(size / 2);

        // nested IF over the bands selecting (glyph, colour). Build as two parallel pickers.
        var glyphExpr = new StringBuilder("\"\"");
        var colorExpr = new StringBuilder("\"%23808080\"");
        // iterate in reverse so the first matching (earliest) rule wins after the inserts unwind.
        foreach (var (min, max, glyph, color) in rules.AsEnumerable().Reverse())
        {
            string g = DaxStr(glyph ?? "");
            string c = EncodeColor(color);
            string cond = $"_v >= {N(min)} && _v < {N(max)}";
            glyphExpr.Insert(0, $"IF ( {cond}, \"{g}\", ").Append(" )");
            colorExpr.Insert(0, $"IF ( {cond}, \"{c}\", ").Append(" )");
        }

        var sb = new StringBuilder();
        sb.Append($"VAR _v = {v}\n");
        sb.Append("VAR _glyph = " + glyphExpr + "\n");
        sb.Append("VAR _col = " + colorExpr + "\n");
        sb.Append("VAR _svg =\n");
        sb.Append($"    \"{DaxStr(Prefix)}\" &\n");
        sb.Append($"    \"<svg xmlns='http://www.w3.org/2000/svg' width='{s}' height='{s}'>\" &\n");
        sb.Append($"    \"<text x='{mid}' y='{mid}' font-size='{N(size * 0.8)}' text-anchor='middle' dominant-baseline='central' fill='\" & _col & \"'>\" & _glyph & \"</text>\" &\n");
        sb.Append("    \"</svg>\"\n");
        sb.Append("RETURN _svg");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- 6. chip / pill
    /// <summary>
    /// DAX for an auto-sizing rounded CHIP (Bacci pill): a &lt;foreignObject&gt; holds an HTML span so the
    /// pill grows to fit the text; the rect width is estimated from the text length. textMeasure supplies the
    /// label; colorMeasure (optional) supplies the fill colour (else a default).
    /// </summary>
    public static string Chip(string textMeasure, string? colorMeasure, string defaultFill, double height)
    {
        string txt = MeasureRef(textMeasure);
        string h = N(height);
        string fillExpr = string.IsNullOrWhiteSpace(colorMeasure)
            ? "\"" + EncodeColor(defaultFill) + "\""
            // a colour measure returns a hex; encode its '#' at runtime via SUBSTITUTE.
            : $"SUBSTITUTE ( {MeasureRef(colorMeasure!)}, \"#\", \"%23\" )";

        var sb = new StringBuilder();
        sb.Append($"VAR _txt = {txt}\n");
        sb.Append("VAR _fill = " + fillExpr + "\n");
        // estimate width: ~7.5px per char + padding, clamped.
        sb.Append("VAR _w = MAX ( 28, LEN ( _txt ) * 7.5 + 16 )\n");
        sb.Append("VAR _svg =\n");
        sb.Append($"    \"{DaxStr(Prefix)}\" &\n");
        sb.Append($"    \"<svg xmlns='http://www.w3.org/2000/svg' width='\" & _w & \"' height='{h}'>\" &\n");
        sb.Append($"    \"<rect x='0' y='0' width='\" & _w & \"' height='{h}' rx='{N(height / 2)}' fill='\" & _fill & \"'/>\" &\n");
        sb.Append($"    \"<text x='\" & ( _w / 2 ) & \"' y='{N(height / 2)}' font-size='{N(height * 0.5)}' font-family='Segoe UI, Arial' text-anchor='middle' dominant-baseline='central' fill='%23FFFFFF'>\" & _txt & \"</text>\" &\n");
        sb.Append("    \"</svg>\"\n");
        sb.Append("RETURN _svg");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- measure / column refs
    /// <summary>Normalise a measure reference to the DAX bracket form [Measure]. Accepts "Measure",
    /// "[Measure]" or "Table[Measure]" (the table qualifier is dropped - DAX measures are referenced
    /// bare in brackets).</summary>
    internal static string MeasureRef(string m)
    {
        string s = (m ?? "").Trim();
        int lb = s.IndexOf('[');
        if (lb >= 0 && s.EndsWith("]")) return "[" + s[(lb + 1)..^1] + "]";   // strip any table qualifier
        return "[" + s + "]";
    }

    /// <summary>Normalise a column reference to 'Table'[Column]. Accepts "Table[Column]" or
    /// "'Table'[Column]".</summary>
    internal static string ColumnRef(string c)
    {
        string s = (c ?? "").Trim();
        int lb = s.IndexOf('[');
        if (lb < 0 || !s.EndsWith("]"))
            throw new ArgumentException($"category column must be Table[Column] (got '{c}').");
        string table = s[..lb].Trim().Trim('\'');
        string col = s[(lb + 1)..^1];
        return $"'{table}'[{col}]";
    }
}
