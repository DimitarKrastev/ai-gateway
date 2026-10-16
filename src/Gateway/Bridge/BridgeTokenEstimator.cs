using System.Text.Json.Nodes;
using static Gateway.Bridge.BridgeJson;

namespace Gateway.Bridge;

/// <summary>
/// Local input-token estimate for <c>/v1/messages/count_tokens</c> on bridged models (the Codex backend has no counting
/// endpoint, and the gateway never calls a provider just to count). About 4 characters per token over the text the
/// model would see (system, messages, tool calls/results, tool definitions); images and documents count a fixed amount.
/// </summary>
public static class BridgeTokenEstimator
{
    public const int ImageTokens = 1600;
    public const int DocumentTokens = 3000;
    private const int PerMessageOverhead = 4;
    private const int PerToolOverhead = 8;

    public static int Estimate(JsonNode anthropicRequest)
    {
        if (anthropicRequest is not JsonObject req) return 1;
        long chars = 0;
        long fixedTokens = 0;

        if (Str(req["system"]) is { } sys) chars += sys.Length;
        else foreach (var b in Arr(req["system"])?.OfType<JsonObject>() ?? []) chars += (Str(b["text"]) ?? "").Length;

        foreach (var m in Arr(req["messages"])?.OfType<JsonObject>() ?? [])
        {
            fixedTokens += PerMessageOverhead;
            Content(m["content"], ref chars, ref fixedTokens);
        }

        foreach (var t in Arr(req["tools"])?.OfType<JsonObject>() ?? [])
        {
            fixedTokens += PerToolOverhead;
            chars += (Str(t["name"]) ?? "").Length + (Str(t["description"]) ?? "").Length;
            if (t["input_schema"] is { } schema) chars += schema.ToJsonString().Length;
        }

        return (int)Math.Max(1, Math.Min(int.MaxValue, (chars + 3) / 4 + fixedTokens));
    }

    private static void Content(JsonNode? content, ref long chars, ref long fixedTokens)
    {
        if (Str(content) is { } s)
        {
            chars += s.Length;
            return;
        }
        foreach (var b in Arr(content)?.OfType<JsonObject>() ?? [])
        {
            switch (Str(b["type"]))
            {
                case "text": chars += (Str(b["text"]) ?? "").Length; break;
                case "thinking": chars += (Str(b["thinking"]) ?? "").Length; break;
                case "image": fixedTokens += ImageTokens; break;
                case "document":
                    if (Str(Obj(b["source"])?["type"]) == "text") chars += (Str(Obj(b["source"])?["data"]) ?? "").Length;
                    else fixedTokens += DocumentTokens;
                    break;
                case "tool_use":
                    chars += (Str(b["name"]) ?? "").Length + (b["input"]?.ToJsonString().Length ?? 0);
                    break;
                case "tool_result":
                    Content(b["content"], ref chars, ref fixedTokens);
                    break;
                case "search_result":
                    Content(b["content"], ref chars, ref fixedTokens);
                    break;
            }
        }
    }
}
