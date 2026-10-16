using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Fakes;
using static Gateway.Tests.ProxyRequests;

namespace Gateway.Tests;

/// <summary>
/// Parallel slow requests must not queue behind each other anywhere in the gateway (upstream connections, usage
/// recording, SQLite), and their recorded latency must match what the client experienced.
/// Note: the fakes' <c>[[slow:MS]]</c> trigger delays every SSE event, so a stream takes events × MS.
/// </summary>
public class ProxyConcurrencyTests
{
    private const int DelayMs = 150;
    private const int Parallel = 8;

    [Fact]
    public async Task Parallel_slow_streams_complete_concurrently_and_record_true_latency()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var anthropic = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        var openai = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new { apiKey = anthropic.ApiKey });
        await gw.LinkAsync(admin, new { apiKey = openai.ApiKey });
        var client = gw.CreateClient();

        HttpRequestMessage Make(int i) => i % 2 == 0
            ? Anthropic(key, AnthropicBody(stream: true, session: $"00000000-0000-0000-0000-{i:D12}", text: $"task {i} [[slow:{DelayMs}]]"))
            : OpenAI(key, ResponsesBody(stream: true, cacheKey: $"conv-{i}", text: $"task {i} [[slow:{DelayMs}]]"), session: $"conv-{i}");

        // Baseline: one stream of each kind, sequentially.
        var single = new Dictionary<int, (long Ms, int Events)>();
        foreach (var i in new[] { 0, 1 })
            single[i % 2] = await TimedStream(client, Make(i));

        var wall = Stopwatch.StartNew();
        var results = await Task.WhenAll(Enumerable.Range(2, Parallel).Select(i => TimedStream(client, Make(i))));
        wall.Stop();

        foreach (var (ms, events) in results)
        {
            Assert.True(events >= 5, $"expected a multi-event stream, got {events}");
            Assert.InRange(ms, events * DelayMs * 0.9, events * DelayMs + 2000);
        }
        // Concurrent, not serialized: the whole batch takes about as long as its slowest single stream.
        var slowestSingle = Math.Max(single[0].Ms, single[1].Ms);
        Assert.True(wall.ElapsedMilliseconds < slowestSingle * 2,
            $"{Parallel} parallel streams took {wall.ElapsedMilliseconds} ms; one takes ~{slowestSingle} ms");

        var items = await WaitForRequests(admin, Parallel + 2);
        Assert.Equal(Parallel + 2, items.Length);
        foreach (var item in items)
        {
            Assert.Equal(200, item.GetProperty("status").GetInt32());
            var latency = item.GetProperty("latencyMs").GetInt64();
            var expected = item.GetProperty("provider").GetString() == "anthropic" ? single[0] : single[1];
            Assert.InRange(latency, expected.Events * DelayMs * 0.9, expected.Ms + 1500);
        }
    }

    private static async Task<(long Ms, int Events)> TimedStream(HttpClient client, HttpRequestMessage req)
    {
        var sw = Stopwatch.StartNew();
        using var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var body = await res.Content.ReadAsStringAsync();
        sw.Stop();
        return (sw.ElapsedMilliseconds, ParseSse(body).Count);
    }

    private static async Task<JsonElement[]> WaitForRequests(HttpClient admin, int count)
    {
        for (var i = 0; ; i++)
        {
            var page = await admin.GetFromJsonAsync<JsonElement>("/api/analytics/requests?limit=100");
            var items = page.GetProperty("items").EnumerateArray().ToArray();
            if (items.Length >= count || i > 100) return items;
            await Task.Delay(50);
        }
    }
}
