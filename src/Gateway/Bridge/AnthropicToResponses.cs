using System.Text.Json.Nodes;
using static Gateway.Bridge.BridgeJson;

namespace Gateway.Bridge;

/// <summary>
/// Translates an Anthropic Messages request (what Claude Code sends) into an OpenAI Responses API request.
/// <list type="bullet">
/// <item>system → <c>instructions</c> (Claude Code's per-request billing-header block is dropped so the prompt prefix,
/// and with it OpenAI's prompt cache, stays stable).</item>
/// <item>messages → input items: user text/images/PDFs → <c>message</c> with <c>input_*</c> parts; assistant text →
/// <c>output_text</c>; <c>tool_use</c> → <c>function_call</c>; <c>tool_result</c> → <c>function_call_output</c> (placed
/// before the rest of that user turn); bridge-enveloped <c>thinking</c> → <c>reasoning</c> with encrypted content.</item>
/// <item>tools → function tools; the web search server tool → the built-in <c>web_search</c> tool (<see cref="BridgeWebSearch"/>);
/// other server tools are dropped. tool_choice / disable_parallel_tool_use mapped.</item>
/// <item>thinking / output_config.effort → <c>reasoning</c> {effort, summary}.</item>
/// <item>Dropped: temperature, top_p, top_k, stop_sequences, cache_control, context_management, metadata, betas.</item>
/// </list>
/// </summary>
public static class AnthropicToResponses
{
    /// <summary>Instructions used when the client sent no system prompt but the upstream requires one (Codex backend).</summary>
    public const string DefaultInstructions = "You are a helpful assistant.";

    private const string BillingHeaderPrefix = "x-anthropic-billing-header";

    /// <summary>Effort levels in increasing order (OpenAI names).</summary>
    public static readonly IReadOnlyList<string> EffortOrder = ["none", "minimal", "low", "medium", "high", "xhigh"];

    public static BridgeRequest Translate(JsonNode anthropicRequest, BridgeTranslateOptions o)
    {
        if (anthropicRequest is not JsonObject req) throw new BridgeTranslationException("Request body must be a JSON object.");
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var body = new JsonObject { ["model"] = o.UpstreamModel };

        var instructions = SystemText(req["system"]);
        if (string.IsNullOrWhiteSpace(instructions)) instructions = o.CodexBackend ? DefaultInstructions : null;
        if (instructions is not null) body["instructions"] = instructions;

        body["input"] = Input(req["messages"], names);

        var dropped = new List<string>();
        var tools = Tools(req["tools"], names, dropped);
        var toolChoice = Obj(req["tool_choice"]);
        var parallel = Bool(toolChoice?["disable_parallel_tool_use"]) != true;
        if (tools.Count > 0)
        {
            body["tools"] = tools;
            body["tool_choice"] = ToolChoice(toolChoice, names, tools);
            body["parallel_tool_calls"] = parallel;
        }
        else if (o.CodexBackend)
        {
            // The Codex backend expects the tool fields to be present.
            body["tools"] = new JsonArray();
            body["tool_choice"] = "auto";
            body["parallel_tool_calls"] = false;
        }

        var reasoning = Reasoning(req, o.SupportedEfforts);
        if (reasoning.Count > 0) body["reasoning"] = reasoning;

        // store:false means nothing is kept server side; the encrypted reasoning travels in the thinking signatures.
        body["store"] = false;
        body["include"] = new JsonArray("reasoning.encrypted_content");
        // The searched pages, for the web_search_tool_result blocks.
        if (tools.Any(t => Str(t?["type"]) == "web_search")) ((JsonArray)body["include"]!).Add("web_search_call.action.sources");

        var clientStream = Bool(req["stream"]) == true;
        body["stream"] = o.CodexBackend || clientStream;

        if (!string.IsNullOrEmpty(o.SessionId)) body["prompt_cache_key"] = o.SessionId;
        if (Str(req["speed"]) == "fast") body["service_tier"] = "priority";
        if (!o.CodexBackend && Long(req["max_tokens"]) is { } maxTokens and > 0) body["max_output_tokens"] = maxTokens;

        return new BridgeRequest(body, clientStream, names, dropped);
    }

    /// <summary>Maps Anthropic effort / thinking budget to an OpenAI effort, clamped to <paramref name="supported"/>.</summary>
    public static string? MapEffort(string? anthropicEffort, long? budgetTokens, IReadOnlyList<string>? supported)
    {
        var known = supported?.Select(s => s.ToLowerInvariant()).Where(s => EffortOrder.Contains(s)).ToList();
        if (known is { Count: 0 }) known = null;
        var effort = anthropicEffort?.ToLowerInvariant() switch
        {
            null or "" => budgetTokens switch
            {
                null => null,
                <= 4096 => "low",
                <= 16384 => "medium",
                _ => "high",
            },
            "max" => known is null ? "high" : "xhigh",
            var e when EffortOrder.Contains(e) => e,
            _ => null,
        };
        if (effort is null || known is null || known.Contains(effort)) return effort;
        var wanted = Rank(effort);
        var below = known.Where(k => Rank(k) <= wanted).OrderByDescending(Rank).FirstOrDefault();
        return below ?? known.OrderBy(Rank).First();
    }

    private static int Rank(string effort)
    {
        for (var i = 0; i < EffortOrder.Count; i++)
            if (EffortOrder[i] == effort) return i;
        return -1;
    }

    private static JsonObject Reasoning(JsonObject req, IReadOnlyList<string>? supported)
    {
        var thinking = Obj(req["thinking"]);
        var thinkingType = Str(thinking?["type"]);
        var thinkingOn = thinkingType is "enabled" or "adaptive";
        var budget = thinkingType == "enabled" ? Long(thinking?["budget_tokens"]) : null;
        var effort = MapEffort(Str(Obj(req["output_config"])?["effort"]), budget, supported);
        var r = new JsonObject();
        if (effort is not null) r["effort"] = effort;
        if (thinkingOn) r["summary"] = "auto";
        return r;
    }

    private static string? SystemText(JsonNode? system)
    {
        if (Str(system) is { } s) return s;
        if (system is not JsonArray blocks) return null;
        var texts = blocks.OfType<JsonObject>()
            .Where(b => Str(b["type"]) == "text")
            .Select(b => Str(b["text"]))
            .Where(t => !string.IsNullOrEmpty(t) && !t.StartsWith(BillingHeaderPrefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return texts.Count == 0 ? null : string.Join("\n\n", texts);
    }

    private static JsonArray Input(JsonNode? messages, Dictionary<string, string> names)
    {
        if (messages is not JsonArray arr) throw new BridgeTranslationException("messages: field required (array).");
        var input = new JsonArray();
        foreach (var m in arr.OfType<JsonObject>())
        {
            var role = Str(m["role"]);
            var content = m["content"];
            if (role == "assistant") AddAssistant(input, content, names);
            else AddUser(input, content);
        }
        return input;
    }

    private static void AddUser(JsonArray input, JsonNode? content)
    {
        var parts = new JsonArray();
        if (Str(content) is { } s)
        {
            if (s.Length > 0) parts.Add(Text("input_text", s));
        }
        else if (content is JsonArray blocks)
        {
            // Tool results answer the previous assistant turn's function calls, so they go first.
            foreach (var b in blocks.OfType<JsonObject>().Where(b => Str(b["type"]) == "tool_result"))
                input.Add(FunctionCallOutput(b));
            foreach (var b in blocks.OfType<JsonObject>())
            {
                switch (Str(b["type"]))
                {
                    case "text" when Str(b["text"]) is { Length: > 0 } t:
                        parts.Add(Text("input_text", t));
                        break;
                    case "image" when Image(b["source"]) is { } img:
                        parts.Add(img);
                        break;
                    case "document":
                        foreach (var p in Document(b)) parts.Add(p);
                        break;
                    case "search_result":
                        var sr = string.Join("\n", new[] { Str(b["title"]), Str(b["source"]) }.Where(x => !string.IsNullOrEmpty(x))
                            .Concat(ContentTexts(b["content"])));
                        if (sr.Length > 0) parts.Add(Text("input_text", sr));
                        break;
                }
            }
        }
        if (parts.Count > 0) input.Add(new JsonObject { ["type"] = "message", ["role"] = "user", ["content"] = parts });
    }

    private static void AddAssistant(JsonArray input, JsonNode? content, Dictionary<string, string> names)
    {
        var parts = new JsonArray();
        void Flush()
        {
            if (parts.Count == 0) return;
            input.Add(new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = parts });
            parts = new JsonArray();
        }

        if (Str(content) is { } s)
        {
            if (s.Length > 0) parts.Add(Text("output_text", s));
        }
        else if (content is JsonArray blocks)
        {
            foreach (var b in blocks.OfType<JsonObject>())
            {
                switch (Str(b["type"]))
                {
                    case "text" when Str(b["text"]) is { Length: > 0 } t:
                        parts.Add(Text("output_text", t));
                        break;
                    case "tool_use":
                        Flush();
                        var name = Str(b["name"]) ?? "tool";
                        input.Add(new JsonObject
                        {
                            ["type"] = "function_call",
                            ["call_id"] = Str(b["id"]),
                            ["name"] = UpstreamName(name, names),
                            ["arguments"] = (b["input"] ?? new JsonObject()).ToJsonString(),
                        });
                        break;
                    case "thinking":
                        if (ReasoningItem(Str(b["signature"]), Str(b["thinking"])) is { } r1) { Flush(); input.Add(r1); }
                        break;
                    case "redacted_thinking":
                        if (ReasoningItem(Str(b["data"]), null) is { } r2) { Flush(); input.Add(r2); }
                        break;
                    // server_tool_use / web_search_tool_result etc. have no Responses equivalent and are dropped.
                }
            }
        }
        Flush();
    }

    /// <summary>A reasoning input item from a bridge envelope; null for thinking that did not come from OpenAI.</summary>
    private static JsonObject? ReasoningItem(string? signature, string? thinking)
    {
        if (!ThinkingEnvelope.TryDecode(signature, out _, out var encrypted) || string.IsNullOrEmpty(encrypted)) return null;
        var summary = new JsonArray();
        if (!string.IsNullOrEmpty(thinking)) summary.Add(Text("summary_text", thinking));
        // No id: with store:false the server cannot look items up by id.
        return new JsonObject { ["type"] = "reasoning", ["summary"] = summary, ["encrypted_content"] = encrypted };
    }

    private static JsonObject FunctionCallOutput(JsonObject b)
    {
        var parts = new List<JsonObject>();
        var content = b["content"];
        if (Str(content) is { } s) parts.Add(Text("input_text", s));
        else if (content is JsonArray blocks)
        {
            foreach (var c in blocks.OfType<JsonObject>())
            {
                switch (Str(c["type"]))
                {
                    case "text": parts.Add(Text("input_text", Str(c["text"]) ?? "")); break;
                    case "image" when Image(c["source"]) is { } img: parts.Add(img); break;
                    case "document": parts.AddRange(Document(c)); break;
                    case "search_result":
                        parts.Add(Text("input_text", string.Join("\n", ContentTexts(c["content"]))));
                        break;
                }
            }
        }

        if (Bool(b["is_error"]) == true)
        {
            var first = parts.FirstOrDefault(p => Str(p["type"]) == "input_text");
            if (first is null) parts.Insert(0, Text("input_text", "Error"));
            else if (!(Str(first["text"]) ?? "").StartsWith("Error", StringComparison.OrdinalIgnoreCase))
                first["text"] = "Error: " + Str(first["text"]);
        }

        JsonNode output = parts.All(p => Str(p["type"]) == "input_text")
            ? JsonValue.Create(string.Join("\n", parts.Select(p => Str(p["text"]))))
            : new JsonArray([.. parts]);
        return new JsonObject { ["type"] = "function_call_output", ["call_id"] = Str(b["tool_use_id"]), ["output"] = output };
    }

    private static JsonObject? Image(JsonNode? source)
    {
        var src = Obj(source);
        var url = Str(src?["type"]) switch
        {
            "base64" => $"data:{Str(src!["media_type"]) ?? "image/png"};base64,{Str(src["data"])}",
            "url" => Str(src!["url"]),
            _ => null, // Anthropic file ids mean nothing to OpenAI.
        };
        return url is null ? null : new JsonObject { ["type"] = "input_image", ["image_url"] = url };
    }

    private static IEnumerable<JsonObject> Document(JsonObject b)
    {
        var src = Obj(b["source"]);
        var title = Str(b["title"]);
        switch (Str(src?["type"]))
        {
            case "base64":
                var mediaType = Str(src!["media_type"]) ?? "application/pdf";
                yield return new JsonObject
                {
                    ["type"] = "input_file",
                    ["filename"] = title ?? (mediaType == "application/pdf" ? "document.pdf" : "document"),
                    ["file_data"] = $"data:{mediaType};base64,{Str(src["data"])}",
                };
                break;
            case "text":
                var text = Str(src!["data"]) ?? "";
                yield return Text("input_text", title is null ? text : $"{title}\n\n{text}");
                break;
            case "url":
                yield return new JsonObject { ["type"] = "input_file", ["file_url"] = Str(src!["url"]) };
                break;
            case "content":
                foreach (var c in Arr(src!["content"])?.OfType<JsonObject>() ?? [])
                {
                    if (Str(c["type"]) == "text") yield return Text("input_text", Str(c["text"]) ?? "");
                    else if (Str(c["type"]) == "image" && Image(c["source"]) is { } img) yield return img;
                }
                break;
        }
    }

    private static IEnumerable<string> ContentTexts(JsonNode? content) =>
        Str(content) is { } s ? [s]
        : Arr(content)?.OfType<JsonObject>().Select(c => Str(c["text"])).OfType<string>() ?? [];

    private static JsonArray Tools(JsonNode? tools, Dictionary<string, string> names, List<string> dropped)
    {
        var result = new JsonArray();
        foreach (var t in Arr(tools)?.OfType<JsonObject>() ?? [])
        {
            var type = Str(t["type"]);
            var name = Str(t["name"]);
            if (name is null) continue;
            if (BridgeWebSearch.IsServerTool(t))
            {
                result.Add(BridgeWebSearch.UpstreamTool(t, dropped));
                continue;
            }
            if (type is not (null or "custom"))
            {
                dropped.Add($"{name} ({type})");
                continue;
            }
            var parameters = Obj(t["input_schema"])?.DeepClone() as JsonObject ?? new JsonObject();
            parameters["type"] ??= "object";
            if (Str(parameters["type"]) == "object") parameters["properties"] ??= new JsonObject();
            var tool = new JsonObject { ["type"] = "function", ["name"] = UpstreamName(name, names) };
            if (Str(t["description"]) is { } d) tool["description"] = d;
            tool["parameters"] = parameters;
            tool["strict"] = false;
            result.Add(tool);
        }
        return result;
    }

    private static JsonNode ToolChoice(JsonObject? choice, Dictionary<string, string> names, JsonArray tools) => Str(choice?["type"]) switch
    {
        "any" => "required",
        "none" => "none",
        // Forcing the web search server tool: the built-in tool cannot be named, so require a tool call.
        "tool" when Str(choice!["name"]) == BridgeWebSearch.ToolName && tools.All(t => Str(t?["name"]) != BridgeWebSearch.ToolName) => "required",
        "tool" when Str(choice!["name"]) is { } n => new JsonObject { ["type"] = "function", ["name"] = UpstreamName(n, names) },
        _ => "auto",
    };

    private static string UpstreamName(string name, Dictionary<string, string> names)
    {
        var upstream = BridgeToolNames.ToUpstream(name);
        if (upstream != name) names[upstream] = name;
        return upstream;
    }
}
