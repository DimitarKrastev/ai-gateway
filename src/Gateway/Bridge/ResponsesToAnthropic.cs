using System.Text.Json;
using System.Text.Json.Nodes;
using static Gateway.Bridge.BridgeJson;

namespace Gateway.Bridge;

/// <summary>
/// Translates a (non-streaming) OpenAI Responses API response object into an Anthropic Messages response:
/// reasoning → <c>thinking</c> (summary text, signature = <see cref="ThinkingEnvelope"/>), message text/refusal →
/// <c>text</c>, function_call → <c>tool_use</c>. Usage: cached input tokens become <c>cache_read_input_tokens</c>.
/// </summary>
public static class ResponsesToAnthropic
{
    /// <summary>The Anthropic message, or an Anthropic error object (<c>type:"error"</c>) for a failed response.</summary>
    public static JsonObject FromResponseJson(JsonNode response, string advertisedId, IReadOnlyDictionary<string, string>? toolNameMap = null)
    {
        if (response is not JsonObject r) return AnthropicErrors.Error("api_error", "Upstream returned an invalid response.");
        if (Str(r["status"]) == "failed")
        {
            var err = Obj(r["error"]);
            return AnthropicErrors.Error(AnthropicErrors.TypeForCode(Str(err?["code"])), Str(err?["message"]) ?? "Upstream response failed.");
        }

        var content = new JsonArray();
        var hasToolUse = false;
        foreach (var item in Arr(r["output"])?.OfType<JsonObject>() ?? [])
        {
            switch (Str(item["type"]))
            {
                case "reasoning":
                    var thinking = SummaryText(item);
                    var encrypted = Str(item["encrypted_content"]);
                    if (thinking.Length == 0 && encrypted is null) break;
                    content.Add(new JsonObject
                    {
                        ["type"] = "thinking",
                        ["thinking"] = thinking,
                        ["signature"] = ThinkingEnvelope.Encode(Str(item["id"]), encrypted),
                    });
                    break;
                case "message":
                    foreach (var part in Arr(item["content"])?.OfType<JsonObject>() ?? [])
                    {
                        var text = PartText(part);
                        if (!string.IsNullOrEmpty(text)) content.Add(Text("text", text));
                    }
                    break;
                case "function_call":
                    hasToolUse = true;
                    content.Add(new JsonObject
                    {
                        ["type"] = "tool_use",
                        ["id"] = Str(item["call_id"]) ?? Str(item["id"]),
                        ["name"] = BridgeToolNames.ToClient(Str(item["name"]) ?? "", toolNameMap),
                        ["input"] = ParseArguments(Str(item["arguments"])),
                    });
                    break;
            }
        }

        var usage = BridgeUsage.From(r["usage"]);
        return new JsonObject
        {
            ["id"] = MessageId(Str(r["id"])),
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = advertisedId,
            ["content"] = content,
            ["stop_reason"] = StopReason(Str(r["status"]) == "incomplete", Str(Obj(r["incomplete_details"])?["reason"]), hasToolUse),
            ["stop_sequence"] = null,
            ["usage"] = usage?.ToAnthropic() ?? BridgeUsage.ZeroAnthropic(),
        };
    }

    /// <summary>Anthropic stop_reason for a finished response.</summary>
    public static string StopReason(bool incomplete, string? incompleteReason, bool hasToolUse)
    {
        if (incomplete && incompleteReason == "max_output_tokens") return "max_tokens";
        if (incomplete && incompleteReason == "content_filter") return "refusal";
        return hasToolUse ? "tool_use" : "end_turn";
    }

    public static string MessageId(string? responseId)
    {
        if (string.IsNullOrEmpty(responseId)) return "msg_" + Guid.NewGuid().ToString("N");
        return "msg_" + (responseId.StartsWith("resp_", StringComparison.Ordinal) ? responseId[5..] : responseId);
    }

    /// <summary>Tool arguments as an object: "" → {}, a non-object or invalid JSON is kept under <c>_raw_arguments</c>.</summary>
    public static JsonObject ParseArguments(string? arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments)) return new JsonObject();
        try
        {
            if (JsonNode.Parse(arguments) is JsonObject o) return o;
        }
        catch (JsonException)
        {
        }
        return new JsonObject { ["_raw_arguments"] = arguments };
    }

    internal static string SummaryText(JsonObject reasoningItem) =>
        string.Join("\n\n", Arr(reasoningItem["summary"])?.OfType<JsonObject>()
            .Select(s => Str(s["text"])).Where(t => !string.IsNullOrEmpty(t)) ?? []);

    internal static string? PartText(JsonObject part) => Str(part["type"]) switch
    {
        "output_text" => Str(part["text"]),
        "refusal" => Str(part["refusal"]),
        _ => null,
    };
}
