using System.Text;
using System.Text.Json;
using Gateway.Accounts;
using Gateway.Proxy;

namespace Gateway.Usage;

public static class Endpoints
{
    public const string Messages = "messages";
    public const string CountTokens = "count_tokens";
    public const string Responses = "responses";
    public const string Chat = "chat";
    public const string Models = "models";
    public const string Other = "other";

    public static string Classify(string provider, string path)
    {
        var p = path.Trim('/').ToLowerInvariant();
        if (provider == Providers.Anthropic)
        {
            if (p == "v1/messages") return Messages;
            if (p == "v1/messages/count_tokens") return CountTokens;
        }
        else
        {
            if (p.EndsWith("responses")) return Responses;
            if (p.EndsWith("chat/completions")) return Chat;
        }
        if (p.EndsWith("models") || p.Contains("/models/")) return Models;
        return Other;
    }

    /// <summary>Endpoints that generate tokens; only these carry usage and agent attribution.</summary>
    public static bool IsGeneration(string endpoint) => endpoint is Messages or Responses or Chat;
}

/// <summary>What the gateway learns from a request body and headers before forwarding it.</summary>
public sealed record RequestInfo(
    string Endpoint,
    string? Model,
    bool Stream,
    string Client,
    string? ClientVersion,
    string? SubagentHint,
    IReadOnlyList<string> ToolNames,
    string SystemPrefix,
    string FirstUserPrefix)
{
    private const int PrefixChars = 512;

    /// <summary>Raw requested speed: Anthropic <c>speed</c>, OpenAI <c>service_tier</c>. Null when absent (standard).</summary>
    public string? RequestedSpeed { get; init; }

    /// <summary>Explicit agent-instance id sent by the client (Claude Code <c>x-claude-code-agent-id</c>), when present.</summary>
    public string? AgentHeader { get; init; }

    /// <summary>OpenAI <c>prompt_cache_key</c>; Codex uses its thread id here.</summary>
    public string? PromptCacheKey { get; init; }

    /// <summary>Header names that carry an explicit agent-instance id, in order of preference.</summary>
    public static readonly string[] AgentIdHeaders = ["x-claude-code-agent-id", "x-agent-id"];

    public static RequestInfo Parse(ProxiedRequestContext ctx)
    {
        var endpoint = Endpoints.Classify(ctx.Provider, ctx.Path);
        var (client, version) = ParseClient(ctx.RequestHeaders);
        var subagent = ctx.RequestHeaders.GetValueOrDefault("x-openai-subagent");
        var agentHeader = AgentIdHeaders.Select(h => ctx.RequestHeaders.GetValueOrDefault(h)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();
        JsonElement? root = null;
        if (ctx.RequestBody.Length > 0 && (Endpoints.IsGeneration(endpoint) || endpoint == Endpoints.CountTokens))
        {
            try { root = JsonDocument.Parse(ctx.RequestBody).RootElement; }
            catch (JsonException) { }
        }
        if (root is not { ValueKind: JsonValueKind.Object } body)
            return new RequestInfo(endpoint, null, false, client, version, subagent, [], "", "") { AgentHeader = agentHeader };

        var model = body.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var stream = body.TryGetProperty("stream", out var s) && s.ValueKind == JsonValueKind.True;
        return new RequestInfo(endpoint, model, stream, client, version, string.IsNullOrEmpty(subagent) ? null : subagent,
            ReadToolNames(body), Truncate(SystemText(body)), Truncate(FirstUserText(body)))
        {
            RequestedSpeed = ScalarText(body, ctx.Provider == Providers.Anthropic ? "speed" : "service_tier"),
            AgentHeader = agentHeader,
            PromptCacheKey = Str(body, "prompt_cache_key") is { Length: > 0 } pck ? pck : null,
        };
    }

    private static string? ScalarText(JsonElement body, string name) =>
        body.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    /// <summary>Claude Code sends <c>claude-cli/&lt;ver&gt;</c>, Codex <c>codex_cli_rs/&lt;ver&gt;</c> (and <c>originator</c>).</summary>
    public static (string Client, string? Version) ParseClient(IReadOnlyDictionary<string, string> headers)
    {
        var ua = headers.GetValueOrDefault("user-agent") ?? "";
        var originator = headers.GetValueOrDefault("originator") ?? "";
        var product = ua.Split(' ', 2)[0];
        var slash = product.IndexOf('/');
        var name = (slash > 0 ? product[..slash] : product).ToLowerInvariant();
        var version = slash > 0 ? product[(slash + 1)..] : null;
        if (name.StartsWith("claude-cli") || name.StartsWith("claude-code")) return ("claude-code", version);
        if (name.StartsWith("codex") || originator.StartsWith("codex", StringComparison.OrdinalIgnoreCase)) return ("codex", name.StartsWith("codex") ? version : null);
        return (string.IsNullOrEmpty(name) ? "unknown" : name, version);
    }

    private static List<string> ReadToolNames(JsonElement body)
    {
        var names = new List<string>();
        if (!body.TryGetProperty("tools", out var tools) || tools.ValueKind != JsonValueKind.Array) return names;
        foreach (var t in tools.EnumerateArray())
        {
            if (t.ValueKind != JsonValueKind.Object) continue;
            var name = Str(t, "name")
                ?? (t.TryGetProperty("function", out var f) && f.ValueKind == JsonValueKind.Object ? Str(f, "name") : null)
                ?? Str(t, "type");
            if (name != null) names.Add(name);
        }
        return names;
    }

    private static string SystemText(JsonElement body)
    {
        // Anthropic: system (string or text blocks); Responses: instructions; Chat: first system/developer message.
        if (body.TryGetProperty("system", out var sys))
        {
            if (sys.ValueKind == JsonValueKind.String) return sys.GetString()!;
            if (sys.ValueKind == JsonValueKind.Array)
            {
                var sb = new StringBuilder();
                foreach (var block in sys.EnumerateArray())
                {
                    var text = block.ValueKind == JsonValueKind.Object ? Str(block, "text") : null;
                    // Claude Code prepends a per-request billing header block; it is not part of the agent's identity.
                    if (text == null || text.StartsWith("x-anthropic-billing-header", StringComparison.OrdinalIgnoreCase)) continue;
                    sb.Append(text).Append('\n');
                }
                return sb.ToString();
            }
        }
        if (Str(body, "instructions") is { } instructions) return instructions;
        if (body.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            foreach (var msg in msgs.EnumerateArray())
                if (Str(msg, "role") is "system" or "developer") return ContentText(msg);
        return "";
    }

    private static string FirstUserText(JsonElement body)
    {
        if (body.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
        {
            foreach (var msg in msgs.EnumerateArray())
                if (msg.ValueKind == JsonValueKind.Object && Str(msg, "role") == "user") return ContentText(msg);
            return "";
        }
        if (body.TryGetProperty("input", out var input))
        {
            if (input.ValueKind == JsonValueKind.String) return input.GetString()!;
            if (input.ValueKind == JsonValueKind.Array)
                foreach (var item in input.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && Str(item, "role") == "user") return ContentText(item);
        }
        return "";
    }

    private static string ContentText(JsonElement msg)
    {
        if (!msg.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString()!;
        if (content.ValueKind != JsonValueKind.Array) return "";
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.ValueKind == JsonValueKind.Object && Str(part, "text") is { } text) sb.Append(text).Append('\n');
            if (sb.Length > PrefixChars) break;
        }
        return sb.ToString();
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s) => s.Length <= PrefixChars ? s : s[..PrefixChars];
}
