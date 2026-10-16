using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gateway.Fakes;

public sealed record FakeToolCall(string Id, string Name, string InputJson);

/// <summary>
/// What the fake model "answers". Prompt triggers (in the last user text):
/// <c>[[tool:NAME]]</c> call NAME; <c>[[tools:N]]</c> N parallel calls; <c>[[no-tool]]</c> plain text;
/// <c>[[error:CODE]]</c> fail with HTTP CODE; <c>[[slow:MS]]</c> delay between stream events; <c>[[out:N]]</c> ~N output tokens.
/// Default: when tools are offered and the last message is not a tool result, call the first tool; otherwise answer with text.
/// </summary>
public sealed partial class FakeTurn
{
    public string Text { get; private init; } = "";
    public List<FakeToolCall> ToolCalls { get; } = [];
    public int? ErrorStatus { get; private init; }
    public int? DelayMs { get; private init; }
    public int OutputTokens { get; private init; }

    public static FakeTurn Plan(string provider, string accountId, string lastUserText, bool lastIsToolResult,
        IReadOnlyList<string> toolNames, string toolIdPrefix)
    {
        int? Int(Regex re) => re.Match(lastUserText) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

        var error = Int(ErrorRe());
        var delay = Int(SlowRe());
        var outTokens = Int(OutRe());
        var named = ToolRe().Match(lastUserText) is { Success: true } tm ? tm.Groups[1].Value : null;
        var count = Int(ToolsRe());
        var noTool = lastUserText.Contains("[[no-tool]]", StringComparison.Ordinal);

        var calls = new List<FakeToolCall>();
        if (!noTool)
        {
            if (named != null) calls.Add(Call(named, toolIdPrefix, 0));
            else if (count is > 0 && toolNames.Count > 0)
                for (var i = 0; i < count; i++) calls.Add(Call(toolNames[i % toolNames.Count], toolIdPrefix, i));
            else if (toolNames.Count > 0 && !lastIsToolResult) calls.Add(Call(toolNames[0], toolIdPrefix, 0));
        }

        var text = calls.Count > 0
            ? $"Calling {string.Join(", ", calls.Select(c => c.Name))}."
            : $"Fake {provider} response from account {accountId}.";
        if (outTokens is > 0)
        {
            var sb = new StringBuilder(text);
            while (Tokens.Estimate(sb.ToString()) < outTokens) sb.Append(" lorem ipsum dolor sit amet");
            text = sb.ToString();
        }
        var output = Tokens.Estimate(text) + calls.Sum(c => Tokens.Estimate(c.InputJson) + 10);

        var turn = new FakeTurn { Text = text, ErrorStatus = error, DelayMs = delay, OutputTokens = output };
        turn.ToolCalls.AddRange(calls);
        return turn;
    }

    private static FakeToolCall Call(string name, string prefix, int i) =>
        new(prefix + FakeProviderState.RandomToken(12).Replace("-", "a").Replace("_", "b"), name,
            JsonSerializer.Serialize(new { command = $"echo fake-{i}" }));

    [GeneratedRegex(@"\[\[error:(\d{3})\]\]")] private static partial Regex ErrorRe();
    [GeneratedRegex(@"\[\[slow:(\d+)\]\]")] private static partial Regex SlowRe();
    [GeneratedRegex(@"\[\[out:(\d+)\]\]")] private static partial Regex OutRe();
    [GeneratedRegex(@"\[\[tool:([A-Za-z0-9_\-\.]+)\]\]")] private static partial Regex ToolRe();
    [GeneratedRegex(@"\[\[tools:(\d+)\]\]")] private static partial Regex ToolsRe();
}

public static class Tokens
{
    /// <summary>Deterministic ~4 chars/token estimate.</summary>
    public static int Estimate(string? s) => string.IsNullOrEmpty(s) ? 0 : Math.Max(1, (s.Length + 3) / 4);

    public static int Estimate(JsonElement? e) => e is { } v && v.ValueKind != JsonValueKind.Undefined && v.ValueKind != JsonValueKind.Null ? Estimate(v.GetRawText()) : 0;

    public static string Hash(params string?[] parts)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u0001", parts.Select(p => p ?? ""))));
        return Convert.ToHexString(bytes, 0, 12);
    }
}

/// <summary>Writes server-sent events and flushes after each one.</summary>
public sealed class SseWriter(HttpResponse response, int delayMs)
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public async Task EventAsync(string? eventName, object data)
    {
        if (delayMs > 0) await Task.Delay(delayMs);
        var sb = new StringBuilder();
        if (eventName != null) sb.Append("event: ").Append(eventName).Append('\n');
        sb.Append("data: ").Append(data is string s ? s : JsonSerializer.Serialize(data, Json)).Append("\n\n");
        await response.WriteAsync(sb.ToString());
        await response.Body.FlushAsync();
    }

    public static void Start(HttpResponse response)
    {
        response.StatusCode = 200;
        response.ContentType = "text/event-stream; charset=utf-8";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";
    }
}

internal static class JsonExt
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public static JsonElement? Prop(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

    public static bool Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    public static IEnumerable<JsonElement> Arr(this JsonElement? e) =>
        e is { ValueKind: JsonValueKind.Array } a ? a.EnumerateArray() : [];
}
