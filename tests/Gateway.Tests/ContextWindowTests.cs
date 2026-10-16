using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Usage;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

public class ContextWindowTests
{
    private static RequestRecord Req(string agent, long start, long end, long context, double cost = 0, string model = "claude-opus-5-5") => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), Attempt = 1, Provider = "anthropic", Client = "claude-code", Method = "POST",
        Path = "v1/messages", Endpoint = "messages", Model = model, SessionId = "s1", AgentId = agent, AgentRole = "main", Status = 200,
        ContextTokens = context, InputTokens = context, CostUsd = cost, Priced = cost > 0, StartedAt = start, CompletedAt = end,
    };

    private static async Task<JsonElement> Series(HttpClient admin, string query) =>
        await admin.GetFromJsonAsync<JsonElement>($"/api/analytics/timeseries?{query}");

    /// <summary>Value of series <paramref name="key"/> in the bucket that starts at <paramref name="t"/>.</summary>
    private static double At(JsonElement ts, string key, long t) =>
        ts.GetProperty("points").EnumerateArray().First(p => p.GetProperty("t").GetInt64() == t).TryGetProperty(key, out var v) ? v.GetDouble() : 0;

    private static string KeyOf(JsonElement ts, string id) =>
        ts.GetProperty("series").EnumerateArray().First(s => s.GetProperty("id").GetString() == id).GetProperty("key").GetString()!;

    [Fact]
    public async Task Each_agent_holds_its_latest_context_and_groups_sum_their_agents()
    {
        using var gw = new GatewayFactory();
        var admin = await gw.CreateLoggedInClientAsync();
        var store = gw.Services.GetRequiredService<UsageStore>();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() / 1000 * 1000;

        // Agent A: 1k context at 1 s, grows to 3k at 5 s. Agent B: 500 at 3 s. Then nothing for 6 minutes.
        store.Insert(Req("A", t0 + 1000, t0 + 2000, 1000));
        store.Insert(Req("B", t0 + 3000, t0 + 4000, 500));
        store.Insert(Req("A", t0 + 5000, t0 + 6000, 3000));
        var range = $"from={t0}&to={t0 + 400_000}&bucket=1s&metric=contextWindow";

        var perAgent = await Series(admin, range + "&groupBy=agentId");
        var a = KeyOf(perAgent, "A");
        var b = KeyOf(perAgent, "B");
        Assert.Equal(0, At(perAgent, a, t0));
        Assert.Equal(1000, At(perAgent, a, t0 + 1000));
        Assert.Equal(1000, At(perAgent, a, t0 + 4000)); // held between requests
        Assert.Equal(3000, At(perAgent, a, t0 + 5000)); // replaced by the newer request
        Assert.Equal(500, At(perAgent, b, t0 + 3000));
        Assert.Equal(500, At(perAgent, b, t0 + 303_000)); // still held 299 s after B's response...
        Assert.Equal(0, At(perAgent, b, t0 + 305_000)); // ...and gone after the 5-minute idle gap

        var total = await Series(admin, range + "&groupBy=none");
        var key = total.GetProperty("series")[0].GetProperty("key").GetString()!;
        Assert.Equal(1500, At(total, key, t0 + 4000));
        Assert.Equal(3500, At(total, key, t0 + 5000));
    }

    [Fact]
    public async Task Live_time_basis_places_a_long_request_at_its_completion()
    {
        using var gw = new GatewayFactory();
        var admin = await gw.CreateLoggedInClientAsync();
        var store = gw.Services.GetRequiredService<UsageStore>();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() / 1000 * 1000;
        store.Insert(Req("A", t0, t0 + 90_000, 1000, cost: 0.25)); // a 90 s request

        var q = $"from={t0}&to={t0 + 120_000}&bucket=1s&metric=cost&groupBy=none";
        var byStart = await Series(admin, q);
        var byEnd = await Series(admin, q + "&timeBasis=completed");
        var k = byStart.GetProperty("series")[0].GetProperty("key").GetString()!;
        Assert.Equal(0.25, At(byStart, k, t0));
        Assert.Equal(0.25, At(byEnd, k, t0 + 90_000));
        Assert.Equal(0, At(byEnd, k, t0));
    }
}
