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
