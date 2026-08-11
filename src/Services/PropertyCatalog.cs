using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Services;

/// <summary>
/// The visual-property REGISTRY: ingests the bundled Power BI report-theme JSON schema once and exposes,
/// for every visual type, the full set of formatting cards -> properties -> {type, enum values, min/max}.
/// commonCards (title/background/border/...) are merged onto every visual. Used for discovery
/// (list_visual_types / list_visual_properties / get_visual_schema) and non-fatal validation of formatting.
///
/// Schema shape (reportThemeSchema-*.json):
///   visualStyles.properties.&lt;visualType&gt;.properties["*"].$ref -> #/definitions/visual-&lt;type&gt;
///   visual-&lt;type&gt; = allOf:[ {$ref commonCards}, { properties:{ &lt;card&gt;:{items:{properties:{&lt;prop&gt;:&lt;schema&gt;}}} } } ]
/// Each card body is an array; the per-item properties are the formatting properties. $ref is resolved and
/// allOf is merged so every visual yields a flat cards -> properties map. If a property schema is a composite
/// object we cannot reduce to a scalar, it is flagged with type "object" (a documented, pragmatic limit).
/// </summary>
public sealed class PropertyCatalog
{
    /// <summary>A single formatting property's resolved metadata.</summary>
    public sealed record PropertyInfo(
        string Name,
        string Type,                 // bool | number | text | color | enum | object
        IReadOnlyList<string>? Enum, // enum member values, when Type == enum
        double? Min,                 // numeric minimum, when known
        double? Max,                 // numeric maximum, when known
        string? Title);             // the schema's human title, when present

    /// <summary>A formatting card (e.g. title, legend, valueAxis) and its properties.</summary>
    public sealed record CardInfo(
        string Name,
        bool Common,                                          // true when inherited from commonCards
        IReadOnlyDictionary<string, PropertyInfo> Properties);

    /// <summary>One visual type's complete resolved catalogue (commonCards + visual-specific cards).</summary>
    public sealed record VisualInfo(
        string VisualType,
        bool IsDataVisual,
        IReadOnlyDictionary<string, CardInfo> Cards);

    // canonical container / non-data visual types (everything else is treated as a data visual)
    private static readonly HashSet<string> ContainerTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "report", "page", "filter", "group",
        "textbox", "image", "shape", "actionButton", "bookmarkNavigator", "pageNavigator",
    };

    private readonly Lazy<IReadOnlyDictionary<string, VisualInfo>> _visuals;
    private readonly Lazy<string> _schemaVersion;

    public PropertyCatalog()
    {
        // built once, cached for the process lifetime.
        var built = new Lazy<(IReadOnlyDictionary<string, VisualInfo> visuals, string version)>(BuildFromSchema);
        _visuals = new Lazy<IReadOnlyDictionary<string, VisualInfo>>(() => built.Value.visuals);
        _schemaVersion = new Lazy<string>(() => built.Value.version);
    }

    /// <summary>The bundled schema version (e.g. "2.155"), parsed from the resource file name.</summary>
    public string SchemaVersion => _schemaVersion.Value;

    /// <summary>All canonical visual-type keys.</summary>
    public IReadOnlyCollection<string> VisualTypes => (IReadOnlyCollection<string>)_visuals.Value.Keys;

    public bool Knows(string visualType) =>
        visualType is not null && _visuals.Value.ContainsKey(visualType);

    public VisualInfo? Get(string visualType) =>
        visualType is not null && _visuals.Value.TryGetValue(visualType, out var v) ? v : null;

    public IReadOnlyDictionary<string, VisualInfo> All => _visuals.Value;

    // ---- discovery projections (plain anonymous shapes the tools serialise) ----

    public object ListVisualTypes()
    {
        var items = _visuals.Value.Values
            .OrderBy(v => v.VisualType, StringComparer.OrdinalIgnoreCase)
            .Select(v => new { visualType = v.VisualType, kind = v.IsDataVisual ? "data" : "container", cards = v.Cards.Count })
            .ToArray();
        return new
        {
            ok = true,
            schemaVersion = SchemaVersion,
            count = items.Length,
            dataVisuals = items.Count(i => i.kind == "data"),
            containers = items.Count(i => i.kind == "container"),
            visualTypes = items,
        };
    }

    public object ListVisualProperties(string visualType, string? card = null)
    {
        var v = Get(visualType)
            ?? throw new ArgumentException($"Unknown visual type '{visualType}'. Call list_visual_types for the canonical keys.");

        IEnumerable<CardInfo> cards = v.Cards.Values;
        if (!string.IsNullOrWhiteSpace(card))
        {
            if (!v.Cards.TryGetValue(card, out var only))
                throw new ArgumentException($"Visual '{visualType}' has no card '{card}'. Call list_visual_properties without a card to list them.");
            cards = new[] { only };
        }

        var cardOut = cards
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .Select(c => new
            {
                card = c.Name,
                common = c.Common,
                properties = c.Properties.Values
                    .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(p => new { name = p.Name, type = p.Type, title = p.Title, @enum = p.Enum, min = p.Min, max = p.Max })
                    .ToArray(),
            })
            .ToArray();

        return new
        {
            ok = true,
            visualType = v.VisualType,
            kind = v.IsDataVisual ? "data" : "container",
            schemaVersion = SchemaVersion,
            cardCount = cardOut.Length,
            propertyCount = cardOut.Sum(c => c.properties.Length),
            cards = cardOut,
        };
    }

    /// <summary>
    /// Validate a formatJson (the set_visual_format shape: { vcObjects:{card:{prop:val}}, objects:{card:{prop:val}} }
    /// or a bare { card:{prop:val} } map) against the registry. Returns non-fatal warnings only: unknown cards,
    /// unknown properties, and type mismatches. Never throws for content; an unknown visual type is reported as a
    /// single "unknownVisualType" note (callers treat as not-validatable, not an error).
    /// </summary>
    public ValidationResult Validate(string visualType, string formatJson)
    {
        var warnings = new List<string>();
        var v = Get(visualType);
        if (v is null)
            return new ValidationResult(false, true, Array.Empty<string>());

        JsonNode? root;
        try { root = JsonNode.Parse(formatJson); }
        catch (Exception ex) { return new ValidationResult(false, false, new[] { $"formatJson did not parse: {ex.Message}" }); }
        if (root is not JsonObject obj)
            return new ValidationResult(false, false, new[] { "formatJson must be a JSON object." });

        // accept either the bucketed shape (vcObjects/objects) or a bare card map.
        var buckets = new List<JsonObject>();
        if (obj["vcObjects"] is JsonObject vco) buckets.Add(vco);
        if (obj["objects"] is JsonObject oo) buckets.Add(oo);
        if (buckets.Count == 0) buckets.Add(obj);

        foreach (var bucket in buckets)
            foreach (var (cardName, cardNode) in bucket)
            {
                if (!v.Cards.TryGetValue(cardName, out var card))
                {
                    warnings.Add($"unknown card '{cardName}' for visual '{visualType}'");
                    continue;
                }
                if (cardNode is not JsonObject props) continue;
                foreach (var (propName, valNode) in props)
                {
                    if (!card.Properties.TryGetValue(propName, out var pi))
                    {
                        warnings.Add($"unknown property '{cardName}.{propName}' for visual '{visualType}'");
                        continue;
                    }
                    var mismatch = TypeMismatch(pi, valNode);
                    if (mismatch is not null)
                        warnings.Add($"type mismatch '{cardName}.{propName}': {mismatch}");
                }
            }

        return new ValidationResult(warnings.Count == 0, false, warnings);
    }

    public sealed record ValidationResult(bool Valid, bool UnknownVisualType, IReadOnlyList<string> Warnings);

    /// <summary>Returns a human note when value clearly does not fit the declared property type, else null.</summary>
    private static string? TypeMismatch(PropertyInfo pi, JsonNode? val)
    {
        if (val is null) return null;
        // wrapped/encoded expression values, measure/column-bound shorthand and structured FillRule colours
        // are already-shaped PBI JSON - taken on trust (the encoder writes them verbatim).
        bool isStructured = val is JsonObject vo &&
            (vo.ContainsKey("expr") || vo.ContainsKey("solid") || vo.ContainsKey("measure") || vo.ContainsKey("column"));
        if (isStructured) return null;

        bool isObject = val is JsonObject || val is JsonArray;

        switch (pi.Type)
        {
            case "bool":
                if (isObject) return "expected a scalar bool, got a nested object/array";
                if (!TryBool(val, out _)) return $"expected bool, got '{Raw(val)}'";
                return null;
            case "number":
                if (isObject) return "expected a scalar number, got a nested object/array";
                if (!TryNumber(val, out var n)) return $"expected number, got '{Raw(val)}'";
                if (pi.Min is double mn && n < mn) return $"{n} below minimum {mn}";
                if (pi.Max is double mx && n > mx) return $"{n} above maximum {mx}";
                return null;
            case "enum":
                if (isObject) return "expected a scalar enum value, got a nested object/array";
                if (pi.Enum is { Count: > 0 } && val is JsonValue ev && ev.TryGetValue<string>(out var s)
                    && !pi.Enum.Contains(s, StringComparer.Ordinal))
                    return $"'{s}' not one of [{string.Join("|", pi.Enum)}]";
                return null;
            case "object":
                // a structured property MUST receive structured JSON - a bare scalar where an object is
                // expected is a malformed write (the old encoder silently stringified it).
                if (!isObject) return $"expected a structured object, got the scalar '{Raw(val)}'";
                return null;
            // color / text: accepted leniently (colour may be hex or a fill object; text may carry markup).
            default:
                return null;
        }
    }

    private static bool TryBool(JsonNode v, out bool b)
    {
        b = false;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<bool>(out b)) return true;
            if (jv.TryGetValue<string>(out var s) && bool.TryParse(s, out b)) return true;
        }
        return false;
    }

    private static bool TryNumber(JsonNode v, out double n)
    {
        n = 0;
        if (v is JsonValue jv)
        {
            if (jv.TryGetValue<double>(out n)) return true;
            if (jv.TryGetValue<string>(out var s) && double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out n)) return true;
        }
        return false;
    }

    private static string Raw(JsonNode v) => v.ToJsonString();

    // ===================================================================== schema ingestion

    /// <summary>Locate the embedded reportThemeSchema-*.json (EmbeddedResource) and parse it.</summary>
    private static (IReadOnlyDictionary<string, VisualInfo>, string) BuildFromSchema()
    {
        var asm = typeof(PropertyCatalog).Assembly;
        var resName = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.Contains("reportThemeSchema", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Embedded report-theme schema resource not found. Expected an EmbeddedResource named *reportThemeSchema*.json in the assembly.");

        string version = ExtractVersion(resName);

        using var stream = asm.GetManifestResourceStream(resName)
            ?? throw new InvalidOperationException($"Could not open embedded resource '{resName}'.");
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 256 });

        var root = doc.RootElement;
        if (!root.TryGetProperty("definitions", out var definitions))
            throw new InvalidOperationException("Schema has no 'definitions' section.");
        if (!root.TryGetProperty("properties", out var topProps) || !topProps.TryGetProperty("visualStyles", out var visualStyles)
            || !visualStyles.TryGetProperty("properties", out var styleTypes))
            throw new InvalidOperationException("Schema has no properties.visualStyles.properties section.");

        var defs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var d in definitions.EnumerateObject()) defs[d.Name] = d.Value;

        var visuals = new Dictionary<string, VisualInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var typeProp in styleTypes.EnumerateObject())
        {
            string visualType = typeProp.Name;
            // resolve the per-visual definition via properties["*"].$ref
            if (!typeProp.Value.TryGetProperty("properties", out var presetProps)
                || !presetProps.TryGetProperty("*", out var star)
                || !TryRef(star, out var defRef))
                continue;
            if (!ResolveDef(defs, defRef, out var visualDef))
                continue;

            var cards = new Dictionary<string, CardInfo>(StringComparer.Ordinal);
            MergeVisualDef(defs, visualDef, cards, common: false);

            visuals[visualType] = new VisualInfo(
                visualType,
                IsDataVisual: !ContainerTypes.Contains(visualType),
                Cards: cards);
        }

        return (visuals, version);
    }

    /// <summary>Walk a visual definition (allOf or direct properties) and add each card's resolved properties.
    /// The commonCards $ref branch is flagged so inherited cards are marked Common.</summary>
    private static void MergeVisualDef(Dictionary<string, JsonElement> defs, JsonElement node,
        Dictionary<string, CardInfo> cards, bool common)
    {
        // allOf: [ {$ref commonCards}, { properties:{...} } ]
        if (node.TryGetProperty("allOf", out var allOf) && allOf.ValueKind == JsonValueKind.Array)
        {
            foreach (var member in allOf.EnumerateArray())
            {
                if (TryRef(member, out var memRef) && ResolveDef(defs, memRef, out var resolved))
                    MergeVisualDef(defs, resolved, cards, common: memRef.Contains("commonCards", StringComparison.OrdinalIgnoreCase));
                else
                    MergeVisualDef(defs, member, cards, common);
            }
            return;
        }

        if (!node.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return;

        foreach (var cardProp in props.EnumerateObject())
        {
            if (cardProp.Name == "*") continue; // wildcard preset, not a real card
            var cardProps = ResolveCardProperties(defs, cardProp.Value);
            if (cardProps.Count == 0) continue;
            // a card may legitimately appear via commonCards and a visual override; merge, keeping common flag if either is common.
            if (cards.TryGetValue(cardProp.Name, out var existing))
            {
                var merged = new Dictionary<string, PropertyInfo>(existing.Properties, StringComparer.Ordinal);
                foreach (var kv in cardProps) merged[kv.Key] = kv.Value;
                cards[cardProp.Name] = existing with { Common = existing.Common || common, Properties = merged };
            }
            else
            {
                cards[cardProp.Name] = new CardInfo(cardProp.Name, common, cardProps);
            }
        }
    }

    /// <summary>Pull the per-item property map from a card body: card.items.properties (array card) or card.properties.</summary>
    private static Dictionary<string, PropertyInfo> ResolveCardProperties(Dictionary<string, JsonElement> defs, JsonElement card)
    {
        var result = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);

        JsonElement propsHolder;
        if (card.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Object
            && items.TryGetProperty("properties", out var ip))
            propsHolder = ip;
        else if (card.TryGetProperty("properties", out var dp))
            propsHolder = dp;
        else
            return result;

        foreach (var p in propsHolder.EnumerateObject())
        {
            if (p.Name == "*") continue;
            result[p.Name] = Classify(defs, p.Name, p.Value, depth: 0);
        }
        return result;
    }

    /// <summary>Classify a property schema into {type, enum, min, max}. Resolves a single layer of $ref
    /// (with one extra recursion guard) and recognises the well-known fill/fontSize/alignment definitions.</summary>
    private static PropertyInfo Classify(Dictionary<string, JsonElement> defs, string name, JsonElement schema, int depth)
    {
        string? title = schema.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        // $ref: special-case fill -> colour, fontSize -> number; otherwise resolve and re-classify.
        if (TryRef(schema, out var refName))
        {
            string leaf = refName.Split('/').Last();
            if (leaf.Equals("fill", StringComparison.OrdinalIgnoreCase)
                || leaf.Contains("color", StringComparison.OrdinalIgnoreCase)
                || leaf.Contains("colour", StringComparison.OrdinalIgnoreCase))
                return new PropertyInfo(name, "color", null, null, null, title);

            if (depth < 3 && defs.TryGetValue(leaf, out var target))
            {
                var inner = Classify(defs, name, target, depth + 1);
                return inner with { Title = title ?? inner.Title };
            }
            // unresolved ref (e.g. image/itemLocation/icon composite objects) -> opaque object.
            return new PropertyInfo(name, "object", null, null, null, title);
        }

        // enum: oneOf with const members (string-typed enum).
        if (schema.TryGetProperty("oneOf", out var oneOf) && oneOf.ValueKind == JsonValueKind.Array)
        {
            var values = new List<string>();
            foreach (var member in oneOf.EnumerateArray())
                if (member.TryGetProperty("const", out var c) && c.ValueKind == JsonValueKind.String)
                    values.Add(c.GetString()!);
            if (values.Count > 0)
                return new PropertyInfo(name, "enum", values, null, null, title);
            // oneOf without consts is a composite (e.g. fillRule) -> object.
            return new PropertyInfo(name, "object", null, null, null, title);
        }

        // primitive type (may be a union array like ["string","number"] -> pick the first scalar).
        string? type = ReadType(schema);
        switch (type)
        {
            case "boolean":
                return new PropertyInfo(name, "bool", null, null, null, title);
            case "number":
            case "integer":
                double? min = schema.TryGetProperty("minimum", out var mn) && mn.ValueKind == JsonValueKind.Number ? mn.GetDouble() : null;
                double? max = schema.TryGetProperty("maximum", out var mx) && mx.ValueKind == JsonValueKind.Number ? mx.GetDouble() : null;
                return new PropertyInfo(name, "number", null, min, max, title);
            case "string":
                return new PropertyInfo(name, "text", null, null, null, title);
            case "object":
            case "array":
                return new PropertyInfo(name, "object", null, null, null, title);
            default:
                return new PropertyInfo(name, "text", null, null, null, title);
        }
    }

    private static string? ReadType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var t)) return null;
        if (t.ValueKind == JsonValueKind.String) return t.GetString();
        if (t.ValueKind == JsonValueKind.Array)
        {
            // union type, e.g. ["string","number","integer","boolean"] -> prefer a usable scalar in order.
            var members = t.EnumerateArray().Select(e => e.GetString()).Where(s => s is not null).ToList();
            foreach (var pref in new[] { "boolean", "number", "integer", "string" })
                if (members.Contains(pref)) return pref;
            return members.FirstOrDefault();
        }
        return null;
    }

    private static bool TryRef(JsonElement node, out string refName)
    {
        refName = "";
        if (node.ValueKind == JsonValueKind.Object && node.TryGetProperty("$ref", out var r) && r.ValueKind == JsonValueKind.String)
        {
            refName = r.GetString()!;
            return true;
        }
        return false;
    }

    private static bool ResolveDef(Dictionary<string, JsonElement> defs, string refName, out JsonElement def)
    {
        string leaf = refName.Split('/').Last();
        return defs.TryGetValue(leaf, out def);
    }

    private static string ExtractVersion(string resourceName)
    {
        var m = System.Text.RegularExpressions.Regex.Match(resourceName, @"reportThemeSchema-([0-9][0-9.]*)\.json", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "unknown";
    }
}
