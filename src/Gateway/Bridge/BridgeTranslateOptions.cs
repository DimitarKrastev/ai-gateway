using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Gateway.Bridge;

/// <summary>How one Anthropic Messages request is translated for an OpenAI Responses upstream.</summary>
public sealed record BridgeTranslateOptions
{
    /// <summary>The model id the client asked for (what we advertise, e.g. <c>anthropic-openai-gpt-5-codex</c>).</summary>
    public required string AdvertisedModelId { get; init; }

    /// <summary>The OpenAI model the request is sent to.</summary>
    public required string UpstreamModel { get; init; }

    /// <summary>True for the ChatGPT Codex backend (subscription accounts): always streams, no max_output_tokens.</summary>
    public bool CodexBackend { get; init; }

    /// <summary>Reasoning efforts the model supports (from the catalog), or null when unknown.</summary>
    public IReadOnlyList<string>? SupportedEfforts { get; init; }

    /// <summary>The client's session id; sent as <c>prompt_cache_key</c> so the provider keeps the prompt cache.</summary>
    public string? SessionId { get; init; }
}

/// <summary>A translated Responses API request.</summary>
/// <param name="Body">The Responses request body.</param>
/// <param name="ClientStream">Whether the client asked for an SSE stream.</param>
/// <param name="ToolNameMap">Upstream tool name → original Anthropic tool name, for names that had to be shortened or
/// sanitized (OpenAI allows <c>^[a-zA-Z0-9_-]{1,64}$</c>). Pass it to the response translators.</param>
/// <param name="DroppedTools">Tools that have no Responses equivalent (Anthropic server tools) and were left out.</param>
public sealed record BridgeRequest(
    JsonObject Body,
    bool ClientStream,
    IReadOnlyDictionary<string, string> ToolNameMap,
    IReadOnlyList<string> DroppedTools)
{
    /// <summary>Whether the upstream request streams (always for the Codex backend).</summary>
    public bool UpstreamStream => BridgeJson.Bool(Body["stream"]) == true;
}

/// <summary>The input request cannot be translated (maps to an Anthropic 400 invalid_request_error).</summary>
public sealed class BridgeTranslationException(string message) : Exception(message);

/// <summary>Token usage reported by the Responses API.</summary>
/// <param name="InputTokens">Total input tokens (includes cached).</param>
/// <param name="CachedTokens">Input tokens served from the prompt cache.</param>
/// <param name="OutputTokens">Output tokens (includes reasoning).</param>
/// <param name="ReasoningTokens">Reasoning tokens (subset of output).</param>
/// <param name="WebSearchRequests">Web searches the model ran (<c>web_search_call</c> items).</param>
public sealed record BridgeUsage(long InputTokens, long CachedTokens, long OutputTokens, long ReasoningTokens, long WebSearchRequests = 0)
{
    public static BridgeUsage? From(JsonNode? usage)
    {
        if (usage is not JsonObject u) return null;
        return new BridgeUsage(
            BridgeJson.Long(u["input_tokens"]) ?? 0,
            BridgeJson.Long(BridgeJson.Obj(u["input_tokens_details"])?["cached_tokens"]) ?? 0,
            BridgeJson.Long(u["output_tokens"]) ?? 0,
            BridgeJson.Long(BridgeJson.Obj(u["output_tokens_details"])?["reasoning_tokens"]) ?? 0);
    }

    /// <summary>Anthropic usage: <c>input_tokens</c> excludes cache reads; OpenAI has no cache writes.</summary>
    public JsonObject ToAnthropic()
    {
        var usage = new JsonObject
        {
            ["input_tokens"] = Math.Max(0L, InputTokens - CachedTokens),
            ["cache_creation_input_tokens"] = 0L,
            ["cache_read_input_tokens"] = CachedTokens,
            ["output_tokens"] = OutputTokens,
        };
        if (WebSearchRequests > 0)
            usage["server_tool_use"] = new JsonObject { ["web_search_requests"] = WebSearchRequests, ["web_fetch_requests"] = 0L };
        return usage;
    }

    public static JsonObject ZeroAnthropic() => new BridgeUsage(0, 0, 0, 0).ToAnthropic();
}

/// <summary>
/// Carries an OpenAI reasoning item through Claude Code: the Anthropic <c>thinking</c> block's signature is
/// <c>bridge1:</c> + base64url(JSON <c>{id, encrypted_content}</c>). Claude Code sends thinking blocks back unchanged on the
/// next turn, so the encrypted reasoning can be replayed (requests use <c>store:false</c>).
/// </summary>
public static class ThinkingEnvelope
{
    public const string Prefix = "bridge1:";

    public static string Encode(string? id, string? encryptedContent)
    {
        var json = new JsonObject { ["id"] = id, ["encrypted_content"] = encryptedContent }.ToJsonString();
        return Prefix + Base64Url(Encoding.UTF8.GetBytes(json));
    }

    /// <summary>Decodes a bridge envelope. False for any other signature (e.g. a real Anthropic one).</summary>
    public static bool TryDecode(string? signature, out string? id, out string? encryptedContent)
    {
        id = null;
        encryptedContent = null;
        if (signature is null || !signature.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        try
        {
            var b64 = signature[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            if (JsonNode.Parse(Convert.FromBase64String(b64)) is not JsonObject o) return false;
            id = BridgeJson.Str(o["id"]);
            encryptedContent = BridgeJson.Str(o["encrypted_content"]);
            return true;
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return false;
        }
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Maps Anthropic tool names to names OpenAI accepts (deterministic, so history and new tools agree).</summary>
public static partial class BridgeToolNames
{
    [GeneratedRegex("^[a-zA-Z0-9_-]{1,64}$")]
    private static partial Regex Valid();

    [GeneratedRegex("[^a-zA-Z0-9_-]")]
    private static partial Regex Invalid();

    public static string ToUpstream(string name)
    {
        if (Valid().IsMatch(name)) return name;
        var clean = Invalid().Replace(name, "_");
        if (clean.Length == 0) clean = "tool";
        // A hash suffix keeps sanitized/shortened names unique (e.g. "a.b" vs "a_b").
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8];
        return (clean.Length > 55 ? clean[..55] : clean) + "_" + hash;
    }

    public static string ToClient(string name, IReadOnlyDictionary<string, string>? map) =>
        map is not null && map.TryGetValue(name, out var original) ? original : name;
}

/// <summary>Tolerant JSON accessors (never throw on unexpected shapes).</summary>
internal static class BridgeJson
{
    public static JsonObject? Obj(JsonNode? n) => n as JsonObject;

    public static JsonArray? Arr(JsonNode? n) => n as JsonArray;

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static bool? Bool(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public static long? Long(JsonNode? n)
    {
        if (n is not JsonValue v) return null;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<double>(out var d)) return (long)d;
        return null;
    }

    public static JsonObject Text(string type, string text) => new() { ["type"] = type, ["text"] = text };
}
