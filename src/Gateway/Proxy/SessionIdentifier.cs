using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.Accounts;

namespace Gateway.Proxy;

public sealed record SessionIdentity(string Id, string Source);

/// <summary>
/// Derives the agent session a request belongs to, so all its requests stick to one linked account.
/// <list type="bullet">
/// <item>Claude Code: <c>x-claude-code-session-id</c> header, else <c>metadata.user_id</c> (either
/// <c>user_…_account_…_session_&lt;uuid&gt;</c> or a JSON string with <c>session_id</c>).</item>
/// <item>Codex: <c>session_id</c> / <c>conversation_id</c> / <c>x-session-id</c> header, else <c>prompt_cache_key</c>.</item>
/// <item>Fallback: hash of client key + first message text + system/instructions text (see <see cref="FallbackHash"/>).</item>
/// </list>
/// </summary>
public static partial class SessionIdentifier
{
    [GeneratedRegex(@"session_([0-9A-Za-z-]{8,})")]
    private static partial Regex UserIdSession();

    public static SessionIdentity Identify(string provider, IHeaderDictionary headers, JsonElement? body, long clientKeyId)
    {
        if (provider == Providers.Anthropic)
        {
            if (Header(headers, "x-claude-code-session-id") is { } h) return new(h, "header");
            if (body is { ValueKind: JsonValueKind.Object } b
                && b.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("user_id", out var uid) && uid.ValueKind == JsonValueKind.String
                && FromUserId(uid.GetString()!) is { } s)
                return new(s, "metadata");
        }
        else
        {
            foreach (var name in new[] { "session_id", "conversation_id", "x-session-id" })
                if (Header(headers, name) is { } h) return new(h, "header");
            if (body is { ValueKind: JsonValueKind.Object } b
                && b.TryGetProperty("prompt_cache_key", out var pck) && pck.ValueKind == JsonValueKind.String
                && pck.GetString() is { Length: > 0 } key)
                return new(key, "prompt_cache_key");
        }
        return new("h-" + FallbackHash(body, clientKeyId), "derived");
    }

    private static string? FromUserId(string userId)
    {
        if (userId.TrimStart().StartsWith('{'))
        {
            try
            {
                var doc = JsonDocument.Parse(userId).RootElement;
                if (doc.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String && sid.GetString() is { Length: > 0 } s)
                    return s;
            }
            catch (JsonException) { }
            return null;
        }
        var m = UserIdSession().Match(userId);
        return m.Success ? m.Groups[1].Value : null;
    }

    /// <summary>
    /// Hash of client key + first message text + system/instructions text. Only text is hashed, so request-to-request noise
    /// does not split a session: <c>cache_control</c> breakpoints that move between turns are ignored, and Claude Code's
    /// per-request <c>x-anthropic-billing-header</c> system block is dropped.
    /// </summary>
    private static string FallbackHash(JsonElement? body, long clientKeyId)
    {
        var sb = new StringBuilder().Append(clientKeyId).Append('|');
        if (body is { ValueKind: JsonValueKind.Object } b)
        {
            foreach (var name in new[] { "messages", "input" })
                if (b.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    sb.Append(Truncate(MessageText(arr[0]))).Append('|');
                else if (arr.ValueKind == JsonValueKind.String)
                    sb.Append(Truncate(arr.GetString()!)).Append('|');
            foreach (var name in new[] { "system", "instructions" })
                if (b.TryGetProperty(name, out var v)) sb.Append(Truncate(SystemText(v))).Append('|');
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..24];
    }

    private const string BillingHeaderPrefix = "x-anthropic-billing-header";

    private static string SystemText(JsonElement system)
    {
        if (system.ValueKind == JsonValueKind.String)
            return string.Join('\n', system.GetString()!.Split('\n').Where(l => !l.TrimStart().StartsWith(BillingHeaderPrefix, StringComparison.OrdinalIgnoreCase)));
        if (system.ValueKind != JsonValueKind.Array) return system.GetRawText();
        var parts = new List<string>();
        foreach (var block in system.EnumerateArray())
        {
            var text = BlockText(block);
            if (!text.TrimStart().StartsWith(BillingHeaderPrefix, StringComparison.OrdinalIgnoreCase)) parts.Add(text);
        }
        return string.Join('\n', parts);
    }

    /// <summary>Text of a message / input item: its string content or the text of its content blocks.</summary>
    private static string MessageText(JsonElement message)
    {
        if (message.ValueKind == JsonValueKind.String) return message.GetString()!;
        if (message.ValueKind != JsonValueKind.Object) return message.GetRawText();
        var role = message.TryGetProperty("role", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
        if (!message.TryGetProperty("content", out var content)) return role + ":" + BlockText(message);
        if (content.ValueKind == JsonValueKind.String) return role + ":" + content.GetString();
        if (content.ValueKind != JsonValueKind.Array) return role + ":" + content.GetRawText();
        return role + ":" + string.Join('\n', content.EnumerateArray().Select(BlockText));
    }

    private static string BlockText(JsonElement block)
    {
        if (block.ValueKind == JsonValueKind.String) return block.GetString()!;
        if (block.ValueKind != JsonValueKind.Object) return block.GetRawText();
        if (block.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String) return t.GetString()!;
        return block.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? "[" + type.GetString() + "]" : "";
    }

    private static string Truncate(string s) => s.Length > 4096 ? s[..4096] : s;

    private static string? Header(IHeaderDictionary headers, string name) =>
        headers.TryGetValue(name, out var v) && v.ToString() is { Length: > 0 } s ? s.Trim() : null;
}
