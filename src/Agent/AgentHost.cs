using System.Text.Json.Nodes;

namespace SuperBiMcp.Agent;

/// <summary>
/// A headless AI prompter: an Anthropic tool-use loop that drives the engine's generate-safe tools to
/// build a report from a plain-English prompt (append user turn -&gt; call model -&gt; on tool_use dispatch +
/// feed tool_result -&gt; repeat until end_turn), with a hard token-budget ceiling and a tool allowlist so a
/// prompt can never reach the live-model / file tools unless the host opts in.
/// </summary>
public sealed class AgentHost
{
    public sealed class Result
    {
        public string Text = "";
        public long InputTokens;
        public long OutputTokens;
        public int Loops;
        public int ToolCalls;
        public string StopReason = "";
        public List<string> ToolsUsed = new();
    }

    private const int MaxLoops = 40;   // runaway guard, matches the desktop

    private readonly ToolRegistry _reg;
    private readonly AnthropicClient _llm;
    private readonly HashSet<string> _allowed;

    public AgentHost(ToolRegistry reg, AnthropicClient llm, IEnumerable<string> allowedTools)
    {
        _reg = reg;
        _llm = llm;
        _allowed = new HashSet<string>(allowedTools, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Run the loop. Stops early (cleanly) if the running token cost would exceed <paramref name="maxCredits"/>.
    /// One credit = <paramref name="tokensPerCredit"/> total tokens.
    /// </summary>
    public async Task<Result> RunAsync(string model, int maxTokens, string system, JsonArray tools,
        string userPrompt, int maxCredits, int tokensPerCredit)
    {
        var res = new Result();
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "user", ["content"] = userPrompt },
        };

        var text = new System.Text.StringBuilder();
        while (res.Loops < MaxLoops)
        {
            res.Loops++;

            // hard credit ceiling: never spend past what the customer has
            int creditsSoFar = (int)Math.Ceiling((res.InputTokens + res.OutputTokens) / (double)Math.Max(1, tokensPerCredit));
            if (creditsSoFar >= maxCredits && maxCredits > 0)
            {
                text.Append("\n[stopped: AI credit budget reached]");
                res.StopReason = "credit_limit";
                break;
            }

            var resp = await _llm.CreateAsync(model, maxTokens, system, tools, messages);

            if (resp["usage"] is JsonObject u)
            {
                res.InputTokens += ReadLong(u["input_tokens"]) + ReadLong(u["cache_read_input_tokens"]) + ReadLong(u["cache_creation_input_tokens"]);
                res.OutputTokens += ReadLong(u["output_tokens"]);
            }

            string stop = resp["stop_reason"]?.ToString() ?? "";
            res.StopReason = stop;
            if (resp["content"] is not JsonArray content) break;

            // echo the assistant turn back verbatim (required by the tool protocol)
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = JsonNode.Parse(content.ToJsonString()) });

            foreach (var blk in content)
                if (blk is JsonObject bo && bo["type"]?.ToString() == "text")
                    text.Append(bo["text"]?.ToString());

            if (string.Equals(stop, "tool_use", StringComparison.OrdinalIgnoreCase))
            {
                var toolResults = DispatchTools(content, res);
                messages.Add(new JsonObject { ["role"] = "user", ["content"] = toolResults });
            }
            else
            {
                // end_turn | refusal | max_tokens | anything else -> done
                if (string.Equals(stop, "refusal", StringComparison.OrdinalIgnoreCase))
                    text.Append("\n[the model declined this request]");
                else if (string.Equals(stop, "max_tokens", StringComparison.OrdinalIgnoreCase))
                    text.Append("\n[response truncated - max_tokens reached]");
                break;
            }
        }

        if (res.Loops >= MaxLoops) text.Append("\n[stopped: tool-loop limit reached]");
        res.Text = text.ToString().Trim();
        return res;
    }

    private JsonArray DispatchTools(JsonArray content, Result res)
    {
        var results = new JsonArray();
        foreach (var blk in content)
        {
            if (blk is not JsonObject bo || bo["type"]?.ToString() != "tool_use") continue;
            string name = bo["name"]?.ToString() ?? "";
            string id = bo["id"]?.ToString() ?? "";
            var args = bo["input"] as JsonObject ?? new JsonObject();

            string payload;
            bool isError = false;
            if (!_allowed.Contains(name))
            {
                payload = "{\"ok\":false,\"error\":\"tool not available in the cloud builder\"}";
                isError = true;
            }
            else
            {
                try { payload = _reg.Invoke(name, args); }
                catch (Exception ex) { payload = $"{{\"ok\":false,\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message)}}}"; isError = true; }
            }

            res.ToolCalls++;
            res.ToolsUsed.Add(name);
            var rb = new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = payload };
            if (isError) rb["is_error"] = true;
            results.Add(rb);
        }
        return results;
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
