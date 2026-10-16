using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Fakes;
using Gateway.Limits;
using Gateway.Proxy;
using Microsoft.AspNetCore.Http;
using static Gateway.Tests.ProxyRequests;

namespace Gateway.Tests;

public class LimitsRoutingTests
{
    private static Task<FakeProviderServer> StartFake() => FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);

    private static async Task<(ProxyGatewayFactory Gw, HttpClient Admin, string Key)> Setup(FakeProviderServer fake)
    {
        var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        return (gw, admin, await gw.CreateClientKeyAsync(admin));
    }

    private static object Claude(FakeAccount a, string? name = null, int priority = 100) => new
    {
        name, priority,
        credentialsJson = JsonSerializer.Serialize(new
        {
            claudeAiOauth = new { accessToken = a.AccessToken, refreshToken = a.RefreshToken, expiresAt = a.AccessTokenExpiresAt!.Value.ToUnixTimeMilliseconds() },
        }),
    };

    private static object ChatGpt(FakeAccount a) => new
    {
        credentialsJson = JsonSerializer.Serialize(new
        {
            tokens = new { id_token = "unused", access_token = a.AccessToken, refresh_token = a.RefreshToken, account_id = a.ChatGptAccountId },
        }),
    };

    private static FakeWindowSpec FiveHour(double? utilization = null, TimeSpan? resetsIn = null) =>
        new("five_hour", utilization, ResetsAt: resetsIn is { } r ? DateTimeOffset.UtcNow + r : null, IncrementPerRequest: 0);

    private static async Task<JsonElement> Limits(HttpClient admin, long id) =>
        (await admin.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}")).GetProperty("limits");

    private static JsonElement Window(JsonElement limits, string key) =>
        limits.GetProperty("windows").EnumerateArray().Single(w => w.GetProperty("key").GetString() == key);

    private static async Task Refresh(HttpClient admin, params long[] ids)
    {
        foreach (var id in ids) (await admin.PostAsync($"/api/accounts/{id}/limits/refresh", null)).EnsureSuccessStatusCode();
    }

    private static async Task<HttpStatusCode> Send(HttpClient client, string key, string session, string text = "[[no-tool]] hi")
    {
        var res = await client.SendAsync(Anthropic(key, AnthropicBody(false, session, text)));
        await res.Content.ReadAsStringAsync();
        return res.StatusCode;
    }

    private static string LastAccount(FakeProviderServer fake) => fake.Anthropic.Requests.Last(r => r.Path == "/v1/messages").AccountId!;

    private static async Task<List<JsonElement>> Events(HttpClient admin) =>
        (await admin.GetFromJsonAsync<JsonElement>("/api/routing/events")).EnumerateArray().ToList();

    private const string S1 = "aaaaaaaa-0000-0000-0000-000000000001";
    private const string S2 = "aaaaaaaa-0000-0000-0000-000000000002";

    // ---------- header parsing ----------

    [Fact]
    public void Parses_limit_headers_of_every_provider()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

        var claude = LimitHeaderParser.Parse(new Dictionary<string, string>
        {
            ["anthropic-ratelimit-unified-status"] = "allowed_warning",
            ["anthropic-ratelimit-unified-5h-utilization"] = "0.42",
            ["anthropic-ratelimit-unified-5h-reset"] = "1800003600",
            ["anthropic-ratelimit-unified-5h-status"] = "allowed",
            ["anthropic-ratelimit-unified-7d-utilization"] = "0.85",
            ["anthropic-ratelimit-unified-7d-reset"] = "1800300000",
            ["anthropic-ratelimit-unified-7d_opus-utilization"] = "1",
            ["anthropic-ratelimit-unified-7d_opus-status"] = "rejected",
            ["anthropic-ratelimit-unified-representative-claim"] = "five_hour",
            ["anthropic-ratelimit-unified-overage-status"] = "rejected",
            ["anthropic-ratelimit-unified-reset"] = "1800003600",
        }, now);
        Assert.Equal(["five_hour", "seven_day", "seven_day_opus"], claude.Windows.Select(w => w.Key).Order());
        var fiveHour = claude.Windows.Single(w => w.Key == "five_hour");
        Assert.Equal(42, fiveHour.Utilization);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_800_003_600), fiveHour.ResetsAt);
        Assert.Equal(300, fiveHour.WindowMinutes);
        Assert.Equal(LimitStatuses.Ok, fiveHour.Status);
        Assert.Equal(LimitStatuses.Warning, claude.Windows.Single(w => w.Key == "seven_day").Status);
        Assert.Equal(LimitStatuses.Exhausted, claude.Windows.Single(w => w.Key == "seven_day_opus").Status);
        Assert.Equal("allowed_warning", claude.UnifiedStatus);
        Assert.Equal("five_hour", claude.RepresentativeClaim);
        Assert.Equal("rejected", claude.OverageStatus);
        Assert.False(claude.LimitReached);

        var anthropicKey = LimitHeaderParser.Parse(new Dictionary<string, string>
        {
            ["anthropic-ratelimit-requests-limit"] = "4000",
            ["anthropic-ratelimit-requests-remaining"] = "3000",
            ["anthropic-ratelimit-requests-reset"] = "2027-01-15T08:00:30Z",
            ["anthropic-ratelimit-input-tokens-limit"] = "2000000",
            ["anthropic-ratelimit-input-tokens-remaining"] = "0",
            ["anthropic-ratelimit-input-tokens-reset"] = "2027-01-15T08:00:30Z",
        }, now);
        var requests = anthropicKey.Windows.Single(w => w.Key == "requests");
        Assert.Equal((4000.0, 3000.0, 1000.0, 25.0), (requests.Limit!.Value, requests.Remaining!.Value, requests.Used!.Value, requests.Utilization!.Value));
        Assert.Equal(DateTimeOffset.Parse("2027-01-15T08:00:30Z"), requests.ResetsAt);
        Assert.Equal(LimitStatuses.Exhausted, anthropicKey.Windows.Single(w => w.Key == "input_tokens").Status);

        var codex = LimitHeaderParser.Parse(new Dictionary<string, string>
        {
            ["x-codex-primary-used-percent"] = "12.5",
            ["x-codex-primary-window-minutes"] = "300",
            ["x-codex-primary-reset-after-seconds"] = "600",
            ["x-codex-secondary-used-percent"] = "90",
            ["x-codex-secondary-window-minutes"] = "10080",
            ["x-codex-secondary-reset-at"] = "1800500000",
        }, now);
        var primary = codex.Windows.Single(w => w.Key == "primary");
        Assert.Equal((12.5, 300, now.AddSeconds(600), "Session (5h)"), (primary.Utilization!.Value, primary.WindowMinutes!.Value, primary.ResetsAt!.Value, primary.Label));
        var secondary = codex.Windows.Single(w => w.Key == "secondary");
        Assert.Equal((LimitStatuses.Warning, "Weekly", DateTimeOffset.FromUnixTimeSeconds(1_800_500_000)), (secondary.Status, secondary.Label, secondary.ResetsAt!.Value));

        var openai = LimitHeaderParser.Parse(new Dictionary<string, string>
        {
            ["x-ratelimit-limit-requests"] = "500",
            ["x-ratelimit-remaining-requests"] = "499",
            ["x-ratelimit-reset-requests"] = "1m30s",
            ["x-ratelimit-limit-tokens"] = "30000",
            ["x-ratelimit-remaining-tokens"] = "29000",
            ["x-ratelimit-reset-tokens"] = "120ms",
        }, now);
        Assert.Equal(now.AddSeconds(90), openai.Windows.Single(w => w.Key == "requests").ResetsAt);
        Assert.Equal(now.AddMilliseconds(120), openai.Windows.Single(w => w.Key == "tokens").ResetsAt);
        Assert.Equal(1000, openai.Windows.Single(w => w.Key == "tokens").Used);
    }

    // ---------- capture & refresh ----------

    [Fact]
    public async Task Captures_limits_from_responses_of_all_account_kinds()
    {
        await using var fake = await StartFake();
        var claude = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(30)]));
        var chatgpt = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth", Windows: [new("primary", 55, IncrementPerRequest: 0)]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var claudeId = await gw.LinkAsync(admin, Claude(claude));
        var chatgptId = await gw.LinkAsync(admin, ChatGpt(chatgpt));
        var client = gw.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        var res = await client.SendAsync(OpenAI(key, ResponsesBody(text: "[[no-tool]] hi"), session: "c1"));
        await res.Content.ReadAsStringAsync();

        var l = await Limits(admin, claudeId);
        Assert.Equal(30, Window(l, "five_hour").GetProperty("utilization").GetDouble());
        Assert.Equal("headers", Window(l, "five_hour").GetProperty("source").GetString());
        Assert.Equal("Session (5h)", Window(l, "five_hour").GetProperty("label").GetString());
        Assert.True(Window(l, "seven_day").GetProperty("resetsAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(6));
        Assert.Equal("allowed", l.GetProperty("unifiedStatus").GetString());
        Assert.False(l.GetProperty("exhausted").GetBoolean());

        l = await Limits(admin, chatgptId);
        Assert.Equal(55, Window(l, "primary").GetProperty("utilization").GetDouble());
        Assert.Equal(300, Window(l, "primary").GetProperty("windowMinutes").GetInt32());
        Assert.Equal(10080, Window(l, "secondary").GetProperty("windowMinutes").GetInt32());

        // API keys report per-minute limits.
        await using var fake2 = await StartFake();
        var antKey = fake2.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        var oaiKey = fake2.OpenAI.Register(new FakeAccountSpec(Kind: "api_key", RequestsLimit: 500));
        var (gw2, admin2, key2) = await Setup(fake2);
        using var __ = gw2;
        var antKeyId = await gw2.LinkAsync(admin2, new { apiKey = antKey.ApiKey });
        var oaiKeyId = await gw2.LinkAsync(admin2, new { apiKey = oaiKey.ApiKey });
        var client2 = gw2.CreateClient();
        Assert.Equal(HttpStatusCode.OK, await Send(client2, key2, S1));
        await (await client2.SendAsync(OpenAI(key2, ResponsesBody(text: "[[no-tool]] hi"), session: "c1"))).Content.ReadAsStringAsync();

        var req = Window(await Limits(admin2, antKeyId), "requests");
        Assert.Equal(4000, req.GetProperty("limit").GetDouble());
        Assert.Equal(3999, req.GetProperty("remaining").GetDouble());
        Assert.Contains("tokens", (await Limits(admin2, antKeyId)).GetProperty("windows").EnumerateArray().Select(w => w.GetProperty("key").GetString()));
        var oaiReq = Window(await Limits(admin2, oaiKeyId), "requests");
        Assert.Equal((500.0, 499.0), (oaiReq.GetProperty("limit").GetDouble(), oaiReq.GetProperty("remaining").GetDouble()));
    }

    [Fact]
    public async Task Refreshes_limits_and_reset_credits_from_usage_endpoints()
    {
        await using var fake = await StartFake();
        var claude = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(42)]));
        var chatgpt = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth", PlanType: "pro", Windows: [new("secondary", 77)]));
        var (gw, admin, _) = await Setup(fake);
        using var __ = gw;
        var claudeId = await gw.LinkAsync(admin, Claude(claude));
        var chatgptId = await gw.LinkAsync(admin, ChatGpt(chatgpt));

        var res = await admin.PostAsync($"/api/accounts/{claudeId}/limits/refresh", null);
        res.EnsureSuccessStatusCode();
        var l = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(42, Window(l, "five_hour").GetProperty("utilization").GetDouble());
        Assert.Equal("usage_endpoint", Window(l, "five_hour").GetProperty("source").GetString());
        Assert.Equal(["five_hour", "seven_day", "seven_day_opus"], l.GetProperty("windows").EnumerateArray().Select(w => w.GetProperty("key").GetString()!).Order());
        Assert.Equal(2, l.GetProperty("resets").GetProperty("available").GetInt32());
        Assert.True(l.GetProperty("refreshedAt").ValueKind == JsonValueKind.String);

        await Refresh(admin, chatgptId);
        l = await Limits(admin, chatgptId);
        Assert.Equal(77, Window(l, "secondary").GetProperty("utilization").GetDouble());
        Assert.Equal("Weekly", Window(l, "secondary").GetProperty("label").GetString());
        Assert.Equal("pro", l.GetProperty("planType").GetString());
        Assert.Equal(2, l.GetProperty("resets").GetProperty("available").GetInt32());

        // The account list carries the same limits summary for the UI.
        var list = await admin.GetFromJsonAsync<JsonElement>("/api/accounts");
        Assert.All(list.EnumerateArray(), a => Assert.Equal(2, a.GetProperty("limits").GetProperty("resets").GetProperty("available").GetInt32()));
        // Usage endpoints were called with the account's own credentials.
        Assert.Contains(fake.Anthropic.Requests, r => r.Path == "/api/oauth/usage" && r.AccountId == claude.Id);
        Assert.Contains(fake.OpenAI.Requests, r => r.Path == "/backend-api/wham/usage" && r.AccountId == chatgpt.Id);
    }

    [Fact]
    public async Task Rate_limited_account_is_marked_exhausted_until_its_window_resets()
    {
        await using var fake = await StartFake();
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Exhausted: true, Windows: [FiveHour(50, TimeSpan.FromHours(2))]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var id = await gw.LinkAsync(admin, Claude(a));

        Assert.Equal(HttpStatusCode.TooManyRequests, await Send(gw.CreateClient(), key, S1));
        var l = await Limits(admin, id);
        Assert.True(l.GetProperty("exhausted").GetBoolean());
        Assert.Equal("rate_limited", l.GetProperty("exhaustedReason").GetString());
        var until = l.GetProperty("exhaustedUntil").GetDateTimeOffset();
        Assert.InRange(until, DateTimeOffset.UtcNow.AddMinutes(110), DateTimeOffset.UtcNow.AddMinutes(125));
    }

    // ---------- resets ----------

    [Fact]
    public async Task Lists_and_uses_provider_and_manual_resets()
    {
        await using var fake = await StartFake();
        var now = DateTimeOffset.UtcNow;
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Exhausted: true,
            Resets: [new("r-late", now.AddDays(9)), new("r-soon", now.AddDays(2))]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var id = await gw.LinkAsync(admin, Claude(a));
        var client = gw.CreateClient();
        Assert.Equal(HttpStatusCode.TooManyRequests, await Send(client, key, S1));

        var resets = (await admin.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}/resets")).EnumerateArray().ToList();
        Assert.Equal(2, resets.Count);
        Assert.All(resets, r => Assert.Equal("provider", r.GetProperty("source").GetString()));
        Assert.True(resets[0].GetProperty("expiresAt").GetDateTimeOffset() < resets[1].GetProperty("expiresAt").GetDateTimeOffset());

        var use = await admin.PostAsync($"/api/accounts/{id}/resets/{resets[0].GetProperty("id").GetInt64()}/use", null);
        Assert.Equal(HttpStatusCode.OK, use.StatusCode);
        var body = await use.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.GetProperty("limits").GetProperty("exhausted").GetBoolean());
        Assert.Equal(1, body.GetProperty("limits").GetProperty("resets").GetProperty("available").GetInt32());
        Assert.Equal(1, fake.Anthropic.Get(a.Id)!.RedeemedResets);
        Assert.Equal(["r-late"], fake.Anthropic.Get(a.Id)!.Resets.Select(r => r.Id));
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));

        // Using it again is refused.
        use = await admin.PostAsync($"/api/accounts/{id}/resets/{resets[0].GetProperty("id").GetInt64()}/use", null);
        Assert.Equal(HttpStatusCode.Conflict, use.StatusCode);

        // Manual credits: added by hand, used to clear exhaustion locally, deletable.
        var add = await admin.PostAsJsonAsync($"/api/accounts/{id}/resets", new { expiresAt = now.AddDays(1), note = "from support" });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var manual = await add.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("manual", manual.GetProperty("source").GetString());
        Assert.False(manual.GetProperty("redeemable").GetBoolean());
        var list = await Limits(admin, id);
        Assert.Equal(2, list.GetProperty("resets").GetProperty("available").GetInt32());
        Assert.True(list.GetProperty("resets").GetProperty("nextExpiresAt").GetDateTimeOffset() < now.AddDays(1.5));

        use = await admin.PostAsync($"/api/accounts/{id}/resets/{manual.GetProperty("id").GetInt64()}/use", null);
        Assert.Equal(HttpStatusCode.OK, use.StatusCode);
        var all = (await admin.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}/resets?includeUsed=true")).EnumerateArray().ToList();
        Assert.Equal(3, all.Count);
        Assert.Equal(2, all.Count(r => r.GetProperty("usedBy").GetString() == "manual"));
        Assert.Equal(1, fake.Anthropic.Get(a.Id)!.RedeemedResets); // manual credits never call the provider

        var add2 = await (await admin.PostAsJsonAsync($"/api/accounts/{id}/resets", new { note = "spare" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/accounts/{id}/resets/{add2.GetProperty("id").GetInt64()}")).StatusCode);
        var providerReset = all.First(r => r.GetProperty("source").GetString() == "provider" && r.GetProperty("available").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/accounts/{id}/resets/{providerReset.GetProperty("id").GetInt64()}")).StatusCode);
        Assert.Contains(await Events(admin), e => e.GetProperty("reason").GetString() == "reset_used");
    }

    // ---------- routing: default mode ----------

    [Fact]
    public async Task Default_mode_keeps_sessions_sticky_and_moves_them_off_exhausted_accounts()
    {
        await using var fake = await StartFake();
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var b = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var aId = await gw.LinkAsync(admin, Claude(a, priority: 1));
        var bId = await gw.LinkAsync(admin, Claude(b, priority: 2));
        var client = gw.CreateClient();

        for (var i = 0; i < 3; i++) Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.All(fake.Anthropic.Requests, r => Assert.Equal(a.Id, r.AccountId));

        fake.Anthropic.Patch(a.Id, new FakeAccountSpec(Exhausted: true));
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1)); // 429 from A, failover to B
        Assert.Equal(b.Id, LastAccount(fake));
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(b.Id, LastAccount(fake));
        // The gateway now knows A is exhausted: B is used directly, also for new sessions.
        var before = fake.Anthropic.Requests.Count(r => r.AccountId == a.Id);
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S2));
        Assert.Equal(before, fake.Anthropic.Requests.Count(r => r.AccountId == a.Id));

        var move = Assert.Single(await Events(admin));
        Assert.Equal(("exhausted", aId, bId, S1), (move.GetProperty("reason").GetString(), move.GetProperty("fromAccountId").GetInt64(),
            move.GetProperty("toAccountId").GetInt64(), move.GetProperty("sessionId").GetString()));

        // Disabling the bound account moves the session too.
        await admin.PatchAsJsonAsync($"/api/accounts/{bId}", new { enabled = false });
        fake.Anthropic.Patch(a.Id, new FakeAccountSpec(Exhausted: false));
        await admin.PostAsync($"/api/accounts/{aId}/limits/refresh", null);
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(a.Id, LastAccount(fake));
        Assert.Equal("unavailable", (await Events(admin))[0].GetProperty("reason").GetString());
    }

    [Fact]
    public async Task New_sessions_go_to_the_least_utilized_account()
    {
        await using var fake = await StartFake();
        var busy = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(70)]));
        var idle = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(5)]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var busyId = await gw.LinkAsync(admin, Claude(busy));
        var idleId = await gw.LinkAsync(admin, Claude(idle));
        await Refresh(admin, busyId, idleId);

        Assert.Equal(HttpStatusCode.OK, await Send(gw.CreateClient(), key, S1));
        Assert.Equal(idle.Id, LastAccount(fake));
    }

    // ---------- routing: maximize utilization ----------

    [Fact]
    public async Task Maximize_routes_to_the_account_resetting_soonest_and_moves_bound_sessions()
    {
        await using var fake = await StartFake();
        var later = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(10, TimeSpan.FromHours(4))]));
        var sooner = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(60, TimeSpan.FromHours(1))]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var laterId = await gw.LinkAsync(admin, Claude(later));
        var soonerId = await gw.LinkAsync(admin, Claude(sooner));
        await Refresh(admin, laterId, soonerId);
        var client = gw.CreateClient();

        // Default mode: least utilized.
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(later.Id, LastAccount(fake));

        var settings = await (await admin.PutAsJsonAsync("/api/settings", new { maximizeUtilization = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(settings.GetProperty("maximizeUtilization").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(sooner.Id, LastAccount(fake));
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S2));
        Assert.Equal(sooner.Id, LastAccount(fake));

        var move = Assert.Single(await Events(admin));
        Assert.Equal(("maximize", laterId, soonerId), (move.GetProperty("reason").GetString(),
            move.GetProperty("fromAccountId").GetInt64(), move.GetProperty("toAccountId").GetInt64()));
    }

    [Fact]
    public async Task Maximize_does_not_flap_between_accounts_with_similar_reset_times()
    {
        await using var fake = await StartFake();
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(10, TimeSpan.FromMinutes(60))]));
        var b = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(10, TimeSpan.FromMinutes(50))]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await admin.PutAsJsonAsync("/api/settings", new { maximizeUtilization = true });
        var aId = await gw.LinkAsync(admin, Claude(a));
        var bId = await gw.LinkAsync(admin, Claude(b));
        await Refresh(admin, aId, bId);
        var client = gw.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(b.Id, LastAccount(fake));

        // A now resets 10 minutes before B: not worth a cache miss.
        fake.Anthropic.Patch(a.Id, new FakeAccountSpec(Windows: [FiveHour(resetsIn: TimeSpan.FromMinutes(40))]));
        await Refresh(admin, aId);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
            Assert.Equal(b.Id, LastAccount(fake));
        }
        Assert.Empty(await Events(admin));

        // A resets 40 minutes sooner: the session moves.
        fake.Anthropic.Patch(a.Id, new FakeAccountSpec(Windows: [FiveHour(resetsIn: TimeSpan.FromMinutes(10))]));
        await Refresh(admin, aId);
        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(a.Id, LastAccount(fake));
        Assert.Equal("maximize", Assert.Single(await Events(admin)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Maximize_prefers_subscription_quota_over_api_keys()
    {
        await using var fake = await StartFake();
        var apiKey = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        var sub = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await admin.PutAsJsonAsync("/api/settings", new { maximizeUtilization = true });
        await gw.LinkAsync(admin, new { apiKey = apiKey.ApiKey, priority = 1 });
        await gw.LinkAsync(admin, Claude(sub, priority: 50));

        Assert.Equal(HttpStatusCode.OK, await Send(gw.CreateClient(), key, S1));
        Assert.Equal(sub.Id, LastAccount(fake));
    }

    [Fact]
    public async Task When_all_accounts_are_exhausted_uses_the_reset_closest_to_expiring()
    {
        await using var fake = await StartFake();
        var now = DateTimeOffset.UtcNow;
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(100, TimeSpan.FromHours(3))],
            Resets: [new("a-reset", now.AddDays(5))]));
        var b = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(100, TimeSpan.FromHours(2))],
            Resets: [new("b-reset", now.AddDays(2)), new("b-reset-2", now.AddDays(8))]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await admin.PutAsJsonAsync("/api/settings", new { maximizeUtilization = true });
        var aId = await gw.LinkAsync(admin, Claude(a));
        var bId = await gw.LinkAsync(admin, Claude(b));
        await Refresh(admin, aId, bId);
        Assert.True((await Limits(admin, aId)).GetProperty("exhausted").GetBoolean());
        Assert.True((await Limits(admin, bId)).GetProperty("exhausted").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, await Send(gw.CreateClient(), key, S1));
        Assert.Equal(b.Id, LastAccount(fake));
        Assert.Equal(0, fake.Anthropic.Get(a.Id)!.RedeemedResets);
        Assert.Equal(1, fake.Anthropic.Get(b.Id)!.RedeemedResets);
        Assert.Equal(["b-reset-2"], fake.Anthropic.Get(b.Id)!.Resets.Select(r => r.Id));

        var ev = Assert.Single(await Events(admin));
        Assert.Equal(("reset_used", bId, S1), (ev.GetProperty("reason").GetString(), ev.GetProperty("toAccountId").GetInt64(), ev.GetProperty("sessionId").GetString()));
        var resets = (await admin.GetFromJsonAsync<JsonElement>($"/api/accounts/{bId}/resets?includeUsed=true")).EnumerateArray();
        Assert.Contains(resets, r => r.GetProperty("usedBy").GetString() == "auto");
        Assert.False((await Limits(admin, bId)).GetProperty("exhausted").GetBoolean());
    }

    [Fact]
    public async Task Reset_on_the_account_that_just_answered_429_is_considered()
    {
        // Exhaustion discovered mid-request: the bound account answers 429, the other one is already known exhausted.
        // The 429 account holds the credit closest to expiring, so it must be redeemed even though it was just excluded.
        await using var fake = await StartFake();
        var now = DateTimeOffset.UtcNow;
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(10, TimeSpan.FromHours(3))],
            Resets: [new("a-reset", now.AddDays(2))]));
        var b = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(100, TimeSpan.FromHours(1))],
            Resets: [new("b-reset", now.AddDays(5))]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await admin.PutAsJsonAsync("/api/settings", new { maximizeUtilization = true });
        var aId = await gw.LinkAsync(admin, Claude(a));
        var bId = await gw.LinkAsync(admin, Claude(b));
        await Refresh(admin, aId, bId);
        var client = gw.CreateClient();

        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(a.Id, LastAccount(fake));
        fake.Anthropic.Patch(a.Id, new FakeAccountSpec(Windows: [new("five_hour", 100)]));

        Assert.Equal(HttpStatusCode.OK, await Send(client, key, S1));
        Assert.Equal(a.Id, LastAccount(fake));
        Assert.Equal(1, fake.Anthropic.Get(a.Id)!.RedeemedResets);
        Assert.Equal(0, fake.Anthropic.Get(b.Id)!.RedeemedResets);
    }

    [Fact]
    public async Task Without_auto_resets_an_exhausted_provider_answers_429()
    {
        await using var fake = await StartFake();
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(100, TimeSpan.FromHours(3))]));
        var b = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [FiveHour(100, TimeSpan.FromHours(2))]));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var aId = await gw.LinkAsync(admin, Claude(a));
        var bId = await gw.LinkAsync(admin, Claude(b));
        await Refresh(admin, aId, bId);
        var client = gw.CreateClient();

        // Default mode: resets are not spent automatically.
        Assert.Equal(HttpStatusCode.TooManyRequests, await Send(client, key, S1));
        // Maximize on but auto-use explicitly off.
        var s = await (await admin.PutAsJsonAsync("/api/settings", new { maximizeUtilization = true, autoUseResets = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(s.GetProperty("autoUseResets").GetBoolean());
        var res = await client.SendAsync(Anthropic(key, AnthropicBody(false, S2, "hi")));
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("rate_limit_error", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(0, fake.Anthropic.Get(a.Id)!.RedeemedResets + fake.Anthropic.Get(b.Id)!.RedeemedResets);
    }

    [Fact]
    public async Task Settings_default_to_off_and_auto_resets_follow_maximize_until_set()
    {
        await using var fake = await StartFake();
        var (gw, admin, _) = await Setup(fake);
        using var __ = gw;
        var s = await admin.GetFromJsonAsync<JsonElement>("/api/settings");
        Assert.Equal((false, false, true), (s.GetProperty("maximizeUtilization").GetBoolean(), s.GetProperty("autoUseResets").GetBoolean(), s.GetProperty("autoUseResetsIsDefault").GetBoolean()));
        s = await (await admin.PutAsJsonAsync("/api/settings", new { maximizeUtilization = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((true, true, true), (s.GetProperty("maximizeUtilization").GetBoolean(), s.GetProperty("autoUseResets").GetBoolean(), s.GetProperty("autoUseResetsIsDefault").GetBoolean()));
        s = await (await admin.PutAsJsonAsync("/api/settings", new { autoUseResets = false })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((true, false, false), (s.GetProperty("maximizeUtilization").GetBoolean(), s.GetProperty("autoUseResets").GetBoolean(), s.GetProperty("autoUseResetsIsDefault").GetBoolean()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.CreateClient().GetAsync("/api/settings")).StatusCode);
    }

    // ---------- derived sessions ----------

    [Fact]
    public void Derived_session_ignores_billing_header_and_moving_cache_breakpoints()
    {
        JsonElement Body(string cch, bool cacheOnFirst, string firstText, int extraTurns) => JsonSerializer.SerializeToElement(new
        {
            model = "claude-sonnet-4-5",
            system = new object[]
            {
                new { type = "text", text = $"x-anthropic-billing-header: cc_version=2.0.30; cc_entrypoint=cli; cch={cch};" },
                new { type = "text", text = "You are Claude Code.", cache_control = new { type = "ephemeral" } },
            },
            messages = new object[]
            {
                new { role = "user", content = new object[] { cacheOnFirst
                    ? new { type = "text", text = firstText, cache_control = (object?)new { type = "ephemeral" } }
                    : new { type = "text", text = firstText, cache_control = (object?)null } } },
            }.Concat(Enumerable.Range(0, extraTurns).Select(i => (object)new { role = i % 2 == 0 ? "assistant" : "user", content = $"turn {i}" })).ToArray(),
        });

        var headers = new HeaderDictionary();
        var first = SessionIdentifier.Identify("anthropic", headers, Body("abc12", true, "fix the bug", 0), 7);
        var later = SessionIdentifier.Identify("anthropic", headers, Body("ffe90", false, "fix the bug", 4), 7);
        Assert.Equal("derived", first.Source);
        Assert.Equal(first.Id, later.Id);
        Assert.NotEqual(first.Id, SessionIdentifier.Identify("anthropic", headers, Body("abc12", true, "write docs", 0), 7).Id);
        Assert.NotEqual(first.Id, SessionIdentifier.Identify("anthropic", headers, Body("abc12", true, "fix the bug", 0), 8).Id);
    }
}
