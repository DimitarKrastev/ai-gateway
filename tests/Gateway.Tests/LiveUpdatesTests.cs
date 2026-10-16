using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Channels;
using Gateway.Fakes;
using Gateway.Live;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using static Gateway.Tests.ProxyRequests;

namespace Gateway.Tests;

public class LiveUpdatesTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Notifier_coalesces_topics_until_read()
    {
        var notifier = new LiveNotifier();
        notifier.Notify(LiveTopic.Usage);
        notifier.Notify(LiveTopic.InFlight);
        notifier.Notify(LiveTopic.Usage);
        Assert.Equal(LiveTopic.Usage | LiveTopic.InFlight, await notifier.WaitAsync(CancellationToken.None).WaitAsync(Timeout));

        var next = notifier.WaitAsync(CancellationToken.None);
        await Task.Delay(50);
        Assert.False(next.IsCompleted);
        notifier.Notify(LiveTopic.Limits);
        Assert.Equal(LiveTopic.Limits, await next.WaitAsync(Timeout));
    }

    [Fact]
    public void Meter_reports_completed_seconds_with_quiet_seconds_as_zero()
    {
        var time = new MutableTime(DateTimeOffset.FromUnixTimeSeconds(1_000_000));
        var meter = new LiveMeter(time);
        meter.AddOutput(10);
        meter.AddPrompt(500);
        time.Now = time.Now.AddSeconds(2);
        meter.AddOutput(7);
        time.Now = time.Now.AddSeconds(1);

        var samples = meter.Range(1_000_000);
        Assert.Equal([1_000_000_000L, 1_000_001_000L, 1_000_002_000L], samples.Select(s => s.T));
        Assert.Equal([10.0, 0, 7.0], samples.Select(s => s.Output));
        Assert.Equal([500.0, 0, 0], samples.Select(s => s.Prompt));
    }

    [Fact]
    public void Late_output_is_spread_over_the_generation_and_revised_seconds_are_reported_once()
    {
        var time = new MutableTime(DateTimeOffset.FromUnixTimeSeconds(2_000_010));
        var meter = new LiveMeter(time);
        meter.AddOutputSpread(40, 2_000_006_000, 2_000_009_500); // 4 seconds already pushed
        meter.AddPrompt(1000, 2_000_006_200);

        var revised = meter.TakeRevised();
        Assert.Equal([2_000_006_000L, 2_000_007_000L, 2_000_008_000L, 2_000_009_000L], revised.Select(r => r.T));
        Assert.All(revised, r => Assert.Equal(10.0, r.Output));
        Assert.Equal(1000.0, revised[0].Prompt);
        Assert.Empty(meter.TakeRevised());
        Assert.Equal(40.0, meter.Range(2_000_000).Sum(r => r.Output));
    }

    private sealed class MutableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task Meter_totals_match_the_recorded_usage_of_a_streamed_request()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => { o.AutoRegister = false; o.StreamChunkDelayMs = 100; });
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new { apiKey = acct.ApiKey });

        var body = new { model = "claude-sonnet-4-5-20250929", max_tokens = 64, stream = true, messages = new[] { new { role = "user", content = "hi" } } };
        var res = await gw.CreateClient().SendAsync(Anthropic(key, body, bearer: true));
        await res.Content.ReadAsStringAsync();

        JsonElement record = default;
        for (var i = 0; i < 50 && record.ValueKind == JsonValueKind.Undefined; i++)
        {
            var items = (await admin.GetFromJsonAsync<JsonElement>("/api/analytics/requests?limit=5")).GetProperty("items");
            if (items.GetArrayLength() > 0) record = items[0];
            else await Task.Delay(100);
        }
        // The request's last second completes within a second; wait for it, then compare the sums.
        await Task.Delay(1200);
        var samples = (await admin.GetFromJsonAsync<JsonElement>("/api/analytics/meter?seconds=60")).GetProperty("samples").EnumerateArray().ToList();
        var output = samples.Sum(s => s.GetProperty("output").GetDouble());
        var prompt = samples.Sum(s => s.GetProperty("prompt").GetDouble());
        Assert.Equal(record.GetProperty("outputTokens").GetInt64(), output, 0.5);
        Assert.Equal(record.GetProperty("inputTokens").GetInt64() + record.GetProperty("cacheReadTokens").GetInt64()
            + record.GetProperty("cacheWriteTokens").GetInt64(), prompt, 0.5);
        Assert.True(output > 0);
    }

    [Fact]
    public async Task Hub_requires_admin_login()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        using var gw = new ProxyGatewayFactory(fake);
        var res = await gw.CreateClient().PostAsync($"{LiveExtensions.HubPath}/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Pushes_in_flight_snapshot_and_usage_change_for_a_proxied_request()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => { o.AutoRegister = false; o.StreamChunkDelayMs = 150; });
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new { apiKey = acct.ApiKey });

        var inFlight = Channel.CreateUnbounded<JsonElement>();
        var changed = Channel.CreateUnbounded<string[]>();
        await using var hub = await ConnectAsync(gw);
        hub.On<JsonElement>("inFlight", s => inFlight.Writer.TryWrite(s));
        hub.On<JsonElement>("changed", m => changed.Writer.TryWrite(
            m.GetProperty("topics").EnumerateArray().Select(t => t.GetString()!).ToArray()));

        var body = new { model = "claude-sonnet-4-5-20250929", max_tokens = 64, stream = true, messages = new[] { new { role = "user", content = "hi" } } };
        var res = await gw.CreateClient().SendAsync(Anthropic(key, body, bearer: true));
        res.EnsureSuccessStatusCode();
        await res.Content.ReadAsStringAsync();

        // While streaming the snapshot shows the request; after it the snapshot is empty again.
        var counts = new List<int>();
        while (!(counts.Count > 1 && counts[^1] == 0))
            counts.Add((await inFlight.Reader.ReadAsync().AsTask().WaitAsync(Timeout)).GetProperty("inFlightRequests").GetInt32());
        Assert.Contains(1, counts);

        string[] topics;
        do topics = await changed.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        while (!topics.Contains("usage"));
    }

    [Fact]
    public async Task Pushes_account_changes()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var acct = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();

        var changed = Channel.CreateUnbounded<string[]>();
        await using var hub = await ConnectAsync(gw);
        hub.On<JsonElement>("changed", m => changed.Writer.TryWrite(
            m.GetProperty("topics").EnumerateArray().Select(t => t.GetString()!).ToArray()));

        await gw.LinkAsync(admin, new { apiKey = acct.ApiKey });
        string[] topics;
        do topics = await changed.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
        while (!topics.Contains("accounts"));
    }

    /// <summary>Connects to the hub over the in-memory test server with the admin session cookie.</summary>
    private static async Task<HubConnection> ConnectAsync(GatewayFactory gw)
    {
        var login = await gw.CreateClient(new() { HandleCookies = false })
            .PostAsJsonAsync("/api/auth/login", new { password = GatewayFactory.AdminPassword });
        login.EnsureSuccessStatusCode();
        var cookie = string.Join("; ", login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));

        var hub = new HubConnectionBuilder()
            .WithUrl(new Uri(gw.Server.BaseAddress, LiveExtensions.HubPath), o =>
            {
                o.HttpMessageHandlerFactory = _ => gw.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers["Cookie"] = cookie;
            })
            .Build();
        await hub.StartAsync().WaitAsync(Timeout);
        return hub;
    }
}
