using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SuperBiMcp.Agent;

/// <summary>
/// A minimal Anthropic Messages API client (BCL only - HttpClient + System.Text.Json). One request shape:
/// model + max_tokens + a cached system block + the tool catalog + the running message list. Returns the
/// parsed response so the host can read content blocks, stop_reason and usage.
///
/// The key arrives via SUPERBI_ANTHROPIC_KEY (or the constructor). A different provider (OpenAI / Gemini)
/// can be slotted in behind this same interface later.
/// </summary>
public sealed class AnthropicClient
{
    public const string DefaultModel = "claude-sonnet-4-6";   // the platform default (Opus/Haiku selectable)
    private const string AnthropicVersion = "2023-06-01";

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _baseUrl;

    /// <summary>Per-instance usage accumulators (input includes cache read/write tokens, matching the
    /// AgentHost budget meter). A caller that news one client per request reads these after the run to
    /// account for the exact token spend.</summary>
    public long TotalInputTokens { get; private set; }
    public long TotalOutputTokens { get; private set; }

    public AnthropicClient(string apiKey, string? baseUrl = null, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://api.anthropic.com").TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>
    /// One turn: POST the conversation and return the parsed Messages response. Every call - success or
    /// failure - updates the process-wide AiHealth record; a failure throws AnthropicApiException with the
    /// failure already classified (billing/auth/rate/network/api) and a customer-safe PublicMessage.
    /// </summary>
    public async Task<JsonObject> CreateAsync(string model, int maxTokens, string system, JsonArray tools, JsonArray messages, string source = "call")
    {
        var body = new JsonObject
        {
            ["model"] = model,
            ["max_tokens"] = maxTokens,
            // system as a single cached text block - caches tools + system across the loop's turns
            ["system"] = new JsonArray(new JsonObject
            {
                ["type"] = "text",
                ["text"] = system,
                ["cache_control"] = new JsonObject { ["type"] = "ephemeral" },
            }),
            ["messages"] = JsonNode.Parse(messages.ToJsonString()),
        };
        if (tools.Count > 0) body["tools"] = JsonNode.Parse(tools.ToJsonString());

        using var req = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/v1/messages");
        req.Headers.TryAddWithoutValidation("x-api-key", _apiKey);
        req.Headers.TryAddWithoutValidation("anthropic-version", AnthropicVersion);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        HttpResponseMessage resp;
        string text;
        try
        {
            resp = await _http.SendAsync(req);
            text = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex) // DNS/socket/timeout - the request never completed
        {
            AiHealth.RecordFailure(source, AiHealth.FailureClass.Network);
            throw new AnthropicApiException(0, null, ex.Message);
        }

        using (resp)
        {
            var json = JsonNode.Parse(SafeJson(text)) as JsonObject;
            if (!resp.IsSuccessStatusCode || json == null)
            {
                var err = json?["error"] as JsonObject;
                string msg = err?["message"]?.ToString() ?? (json == null ? "non-JSON response" : text);
                var apiEx = new AnthropicApiException((int)resp.StatusCode, err?["type"]?.ToString(), msg);
                AiHealth.RecordFailure(source, apiEx.Failure);
                throw apiEx;
            }

            if (json["usage"] is JsonObject u)
            {
                TotalInputTokens += ReadLong(u["input_tokens"]) + ReadLong(u["cache_read_input_tokens"]) + ReadLong(u["cache_creation_input_tokens"]);
                TotalOutputTokens += ReadLong(u["output_tokens"]);
            }
            AiHealth.RecordSuccess(source);
            return json;
        }
    }

    /// <summary>A body that fails to parse must classify the HTTP failure, not crash the classifier.</summary>
    private static string SafeJson(string text)
    {
        try { JsonNode.Parse(text); return text; } catch { return "null"; }
    }

    private static long ReadLong(JsonNode? n)
    {
        if (n is JsonValue v)
        {
            if (v.TryGetValue<long>(out var l)) return l;
            if (v.TryGetValue<double>(out var d)) return (long)d;
        }
        return 0;
    }
}
