using System.ComponentModel;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Agent;

/// <summary>
/// Marks a tool that reaches into a Power BI Desktop instance a human already has open, so it is
/// only safe for an interactive operator attaching to their own machine. The registry refuses
/// marked tools on every surface it feeds (the agent tool list and invoke-by-name), independent
/// of any allowlist the caller passes; the MCP stdio server does not read this attribute, so a
/// local human session keeps them. Detected by type NAME, the same way Discover reads the SDK
/// attributes, so the marker stays version-proof.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class UnsafeForPipelineAttribute : Attribute { }

/// <summary>
/// A reflection-built registry of every engine tool, so the server-side agent host and any HTTP
/// "call a tool by name" surface can reuse the SAME static [McpServerTool] methods the MCP stdio
/// server exposes - no second copy of the catalog. The MCP SDK hides its registry after startup,
/// so we mirror its discovery here (scan for the attribute by NAME, which is version-proof) and
/// generate the JSON input schema from the method signature exactly the way the SDK does.
///
/// Service parameters (ModelService, ReportService, ExcelService, ...) are resolved from DI and
/// never appear in the schema; the remaining scalar parameters become the tool's JSON arguments.
/// </summary>
public sealed class ToolRegistry
{
    public sealed class ToolInfo
    {
        public string Name = "";
        public string Description = "";
        public string DeclaringType = "";
        public bool UnsafeForPipeline;
        public MethodInfo Method = null!;
        public ParameterInfo[] Parameters = Array.Empty<ParameterInfo>();
        public JsonObject InputSchema = new();
    }

    private readonly Dictionary<string, ToolInfo> _tools = new(StringComparer.OrdinalIgnoreCase);
    private readonly IServiceProvider _sp;

    public ToolRegistry(IServiceProvider sp)
    {
        _sp = sp;
        Discover();
    }

    public IReadOnlyCollection<ToolInfo> All => _tools.Values;
    public bool Has(string name) => _tools.ContainsKey(name);
    public ToolInfo? Get(string name) => _tools.TryGetValue(name, out var t) ? t : null;

    // ---- discovery -------------------------------------------------------------------------

    private void Discover()
    {
        var asm = typeof(Cli).Assembly;
        foreach (var type in asm.GetTypes())
        {
            if (!HasAttr(type.GetCustomAttributes(), "McpServerToolTypeAttribute")) continue;
            foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attrs = m.GetCustomAttributes().ToArray();
                var toolAttr = attrs.FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (toolAttr == null) continue;

                string name = ReadStringProp(toolAttr, "Name") ?? ToSnake(m.Name);
                var info = new ToolInfo
                {
                    Name = name,
                    Description = (m.GetCustomAttribute<DescriptionAttribute>()?.Description) ?? "",
                    DeclaringType = type.Name,
                    UnsafeForPipeline = HasAttr(attrs, "UnsafeForPipelineAttribute"),
                    Method = m,
                    Parameters = m.GetParameters(),
                };
                info.InputSchema = BuildSchema(info.Parameters);
                _tools[name] = info;
            }
        }
    }

    // a parameter is a JSON argument (not an injected service) when its type is a scalar we can
    // carry over JSON; everything else (ModelService, ReportService, ...) is resolved from DI.
    private static bool IsArg(ParameterInfo p)
    {
        var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        return t == typeof(string) || t == typeof(int) || t == typeof(long) || t == typeof(double)
            || t == typeof(decimal) || t == typeof(float) || t == typeof(bool) || t.IsEnum;
    }

    private static JsonObject BuildSchema(ParameterInfo[] ps)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var p in ps)
        {
            if (!IsArg(p)) continue;
            var prop = new JsonObject { ["type"] = JsonType(p.ParameterType) };
            var desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (!string.IsNullOrEmpty(desc)) prop["description"] = desc;
            var t = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
            if (t.IsEnum) prop["enum"] = new JsonArray(Enum.GetNames(t).Select(n => (JsonNode)n!).ToArray());
            props[p.Name!] = prop;
            // required only when there is no default and the type is non-nullable
            bool optional = p.HasDefaultValue || Nullable.GetUnderlyingType(p.ParameterType) != null
                || (p.ParameterType == typeof(string) && NullableRef(p));
            if (!optional) required.Add(p.Name!);
        }
        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    private static bool NullableRef(ParameterInfo p)
    {
        // treat string? (NRT) as optional; the compiler emits a NullableAttribute on the param
        foreach (var a in p.GetCustomAttributes())
            if (a.GetType().Name == "NullableAttribute") return true;
        return false;
    }

    private static string JsonType(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(bool)) return "boolean";
        if (t == typeof(int) || t == typeof(long)) return "integer";
        if (t == typeof(double) || t == typeof(decimal) || t == typeof(float)) return "number";
        return "string";
    }

    // ---- invocation ------------------------------------------------------------------------

    /// <summary>Invoke a tool by name with a JSON args object. Returns the tool's JSON string result.
    /// [UnsafeForPipeline] tools are refused here regardless of any caller-side allowlist: every
    /// invoke-by-name surface (cloud agent, HTTP /tool) funnels through this method, and none of
    /// them is the interactive attach those tools exist for.</summary>
    public string Invoke(string name, JsonObject? args)
    {
        if (!_tools.TryGetValue(name, out var t))
            return JsonSerializer.Serialize(new { ok = false, error = $"unknown tool '{name}'" });
        if (t.UnsafeForPipeline)
            return JsonSerializer.Serialize(new { ok = false,
                error = $"tool '{name}' is interactive-attach only (unsafe for pipeline use) and is exposed on the local MCP server only" });

        args ??= new JsonObject();
        var values = new object?[t.Parameters.Length];
        for (int i = 0; i < t.Parameters.Length; i++)
        {
            var p = t.Parameters[i];
            if (IsArg(p)) values[i] = Coerce(args[p.Name!], p);
            else values[i] = _sp.GetService(p.ParameterType)
                ?? throw new InvalidOperationException($"cannot resolve service {p.ParameterType.Name} for tool {name}");
        }
        // tool methods return string (J.Try already wraps their own exceptions in a JSON error)
        var result = t.Method.Invoke(null, values);
        return result as string ?? JsonSerializer.Serialize(result);
    }

    private static object? Coerce(JsonNode? node, ParameterInfo p)
    {
        var target = Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
        if (node == null)
            return p.HasDefaultValue ? p.DefaultValue
                 : target.IsValueType && Nullable.GetUnderlyingType(p.ParameterType) == null ? Activator.CreateInstance(target)
                 : null;
        try
        {
            if (target == typeof(string)) return node.GetValue<object>()?.ToString() ?? node.ToString();
            if (target == typeof(bool)) return ToBool(node);
            if (target == typeof(int)) return (int)ToLong(node);
            if (target == typeof(long)) return ToLong(node);
            if (target == typeof(double)) return ToDouble(node);
            if (target == typeof(decimal)) return (decimal)ToDouble(node);
            if (target == typeof(float)) return (float)ToDouble(node);
            if (target.IsEnum) return Enum.Parse(target, node.ToString(), ignoreCase: true);
        }
        catch { /* fall through to default */ }
        return p.HasDefaultValue ? p.DefaultValue : null;
    }

    private static bool ToBool(JsonNode n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return b;
            if (v.TryGetValue<string>(out var s)) return bool.TryParse(s, out var bs) && bs;
            if (v.TryGetValue<long>(out var l)) return l != 0;
        }
        return false;
    }

    private static long ToLong(JsonNode n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
            if (v.TryGetValue<string>(out var s) && long.TryParse(s, out var ls)) return ls;
        }
        return 0;
    }

    private static double ToDouble(JsonNode n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<double>(out var d)) return d;
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<string>(out var s) && double.TryParse(s, out var ds)) return ds;
        }
        return 0;
    }

    // ---- Anthropic tool schemas ------------------------------------------------------------

    /// <summary>Map the (optionally filtered) tools into the Anthropic `tools` array shape.
    /// [UnsafeForPipeline] tools are dropped before the filter runs, so no allowlist predicate,
    /// however broad, can offer an interactive-attach tool to an unattended model.</summary>
    public JsonArray ToAnthropicTools(Func<ToolInfo, bool>? filter = null)
    {
        var arr = new JsonArray();
        foreach (var t in _tools.Values)
        {
            if (t.UnsafeForPipeline) continue;
            if (filter != null && !filter(t)) continue;
            arr.Add(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.InputSchema.ToJsonString()),
            });
        }
        return arr;
    }

    // ---- helpers ---------------------------------------------------------------------------

    private static bool HasAttr(IEnumerable<Attribute> attrs, string name)
        => attrs.Any(a => a.GetType().Name == name);

    private static string? ReadStringProp(object o, string prop)
        => o.GetType().GetProperty(prop)?.GetValue(o) as string;

    private static string ToSnake(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (char.IsUpper(c)) { if (i > 0) sb.Append('_'); sb.Append(char.ToLowerInvariant(c)); }
            else sb.Append(c);
        }
        return sb.ToString();
    }
}
