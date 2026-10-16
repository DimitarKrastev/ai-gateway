using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Usage;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

public class SpeedBreakdownTests
{
    private static RequestRecord Req(string provider, string model, string mode, long start, long durationMs, long output, int status = 200,
        long? firstTokenAfter = null) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), Attempt = 1, Provider = provider, Client = "claude-code", Method = "POST",
        Path = "v1/messages", Endpoint = "messages", Model = model, Status = status, OutputTokens = output, SpeedMode = mode,
        StartedAt = start, CompletedAt = start + durationMs, FirstTokenAt = firstTokenAfter is { } f ? start + f : null,
        GenerationMs = firstTokenAfter is { } g ? durationMs - g : null,
    };

    [Fact]
    public async Task Speed_is_measured_from_request_sent_to_response_received_per_provider_model_and_mode()
    {
        using var gw = new GatewayFactory();
        var admin = await gw.CreateLoggedInClientAsync();
        var store = gw.Services.GetRequiredService<UsageStore>();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-20).ToUnixTimeMilliseconds();

        // Opus fast: 1000 tokens in 4 s and 500 in 1 s -> 1500 / 5 s = 300 tok/s end to end; p50 of (250, 500) = 375.
        store.Insert(Req("anthropic", "claude-opus-5-5", "fast", t0, 4000, 1000, firstTokenAfter: 1500));
        store.Insert(Req("anthropic", "claude-opus-5-5", "fast", t0 + 10_000, 1000, 500, firstTokenAfter: 500));
        // Opus standard: 600 tokens in 6 s -> 100.
        store.Insert(Req("anthropic", "claude-opus-5-5", "standard", t0 + 20_000, 6000, 600, firstTokenAfter: 2000));
        // OpenAI fast: 400 in 2 s -> 200.
        store.Insert(Req("openai", "gpt-5.6-sol", "fast", t0 + 30_000, 2000, 400));
        // Not measured: an error and a request without output.
        store.Insert(Req("anthropic", "claude-opus-5-5", "fast", t0 + 40_000, 1000, 900, status: 529));
        store.Insert(Req("anthropic", "claude-opus-5-5", "fast", t0 + 50_000, 1000, 0));

        var rows = (await admin.GetFromJsonAsync<JsonElement>($"/api/analytics/speed?from={t0}&to={t0 + 60_000}"))
            .GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("key").GetString()!);

        Assert.Equal(3, rows.Count);
        var fast = rows["anthropic|claude-opus-5-5|fast"];
        Assert.Equal(2, fast.GetProperty("requests").GetInt64());
        Assert.Equal(300, fast.GetProperty("endToEnd").GetDouble());
        Assert.Equal(375, fast.GetProperty("endToEndP50").GetDouble());
        Assert.Equal(1000, fast.GetProperty("avgTtftMs").GetDouble());
        Assert.Equal(100, rows["anthropic|claude-opus-5-5|standard"].GetProperty("endToEnd").GetDouble());
        Assert.Equal(200, rows["openai|gpt-5.6-sol|fast"].GetProperty("endToEnd").GetDouble());
        Assert.Equal("anthropic · claude-opus-5-5 · fast", fast.GetProperty("label").GetString());
    }
}
