using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Fakes;
using static Gateway.Tests.ProxyRequests;

namespace Gateway.Tests;

public class ProxyTests
{
    private static Task<FakeProviderServer> StartFake(int chunkDelayMs = 0) =>
        FakeProviderServer.StartInMemoryAsync(o => { o.AutoRegister = false; o.StreamChunkDelayMs = chunkDelayMs; });

    private static string CodexAuthJson(FakeAccount a) => JsonSerializer.Serialize(new
    {
        OPENAI_API_KEY = (string?)null,
        tokens = new { id_token = "unused", access_token = a.AccessToken, refresh_token = a.RefreshToken, account_id = a.ChatGptAccountId },
    });

    private static object ClaudeCredentials(FakeAccount a, DateTimeOffset? expiresAt = null, int priority = 100) => new
    {
        priority,
        credentialsJson = JsonSerializer.Serialize(new
        {
            claudeAiOauth = new { accessToken = a.AccessToken, refreshToken = a.RefreshToken, expiresAt = (expiresAt ?? a.AccessTokenExpiresAt!.Value).ToUnixTimeMilliseconds() },
        }),
    };

    [Fact]
    public async Task Rejects_requests_without_a_valid_gateway_key_in_provider_error_format()
    {
        await using var fake = await StartFake();
        using var gw = new ProxyGatewayFactory(fake);
        var client = gw.CreateClient();

        var res = await client.SendAsync(Anthropic("aigw_nope", AnthropicBody(false)));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        var err = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("error", err.GetProperty("type").GetString());
        Assert.Contains("No gateway client keys", err.GetProperty("error").GetProperty("message").GetString());

        var admin = await gw.CreateLoggedInClientAsync();
        await gw.CreateClientKeyAsync(admin);
        res = await client.SendAsync(OpenAI("aigw_wrong", ResponsesBody()));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        err = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_api_key", err.GetProperty("error").GetProperty("code").GetString());
        Assert.Empty(fake.Anthropic.Requests);
        Assert.Empty(fake.OpenAI.Requests);
    }

    [Fact]
    public async Task Returns_503_when_no_account_is_linked()
    {
        await using var fake = await StartFake();
        using var gw = new ProxyGatewayFactory(fake);
        var key = await gw.CreateClientKeyAsync(await gw.CreateLoggedInClientAsync());
        var res = await gw.CreateClient().SendAsync(Anthropic(key, AnthropicBody(false)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
    }

    [Fact]
    public async Task Injects_credentials_for_all_four_account_kinds()
    {
        await using var fake = await StartFake();
        var antKey = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        var antOauth = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var oaiKey = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        var chatgpt = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        var client = gw.CreateClient();

        // Anthropic API key (client authenticates with x-api-key).
        var keyId = await gw.LinkAsync(admin, new { apiKey = antKey.ApiKey });
        var res = await client.SendAsync(Anthropic(key, AnthropicBody(false), path: "/anthropic/v1/messages?beta=true"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var r = fake.Anthropic.Requests[^1];
        Assert.Equal(antKey.Id, r.AccountId);
        Assert.Equal(antKey.ApiKey, r.Headers["x-api-key"]);
        Assert.False(r.Headers.ContainsKey("authorization"));
        Assert.Equal("?beta=true", r.Query);

        // Claude subscription (client authenticates with Bearer, like ANTHROPIC_AUTH_TOKEN).
        await admin.PatchAsJsonAsync($"/api/accounts/{keyId}", new { enabled = false });
        await gw.LinkAsync(admin, ClaudeCredentials(antOauth));
        res = await client.SendAsync(Anthropic(key, AnthropicBody(false), bearer: true));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        r = fake.Anthropic.Requests[^1];
        Assert.Equal(antOauth.Id, r.AccountId);
        Assert.Equal("Bearer " + antOauth.AccessToken, r.Headers["authorization"]);
        Assert.Contains("oauth-2025-04-20", r.Headers["anthropic-beta"]);
        Assert.False(r.Headers.ContainsKey("x-api-key"));

        // OpenAI API key.
        await gw.LinkAsync(admin, new { kind = "openai_api_key", apiKey = oaiKey.ApiKey });
        res = await client.SendAsync(OpenAI(key, new { model = "gpt-5", messages = new[] { new { role = "user", content = "hi" } } }, "/openai/v1/chat/completions"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        r = fake.OpenAI.Requests[^1];
        Assert.Equal(oaiKey.Id, r.AccountId);
        Assert.Equal("Bearer " + oaiKey.ApiKey, r.Headers["authorization"]);
        Assert.Equal("/v1/chat/completions", r.Path);

        // ChatGPT subscription: Responses calls are mapped to the Codex backend.
        var chatId = await gw.LinkAsync(admin, new { credentialsJson = CodexAuthJson(chatgpt), priority = 1 });
        res = await client.SendAsync(OpenAI(key, ResponsesBody(), session: "codex-session-1"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await res.Content.ReadAsStringAsync();
        r = fake.OpenAI.Requests[^1];
        Assert.Equal(chatgpt.Id, r.AccountId);
        Assert.Equal("/backend-api/codex/responses", r.Path);
        Assert.Equal("Bearer " + chatgpt.AccessToken, r.Headers["authorization"]);
        Assert.Equal(chatgpt.ChatGptAccountId, r.Headers["chatgpt-account-id"]);

        // Chat Completions cannot go to a ChatGPT account, even with a better priority.
        res = await client.SendAsync(OpenAI(key, new { model = "gpt-5", messages = new[] { new { role = "user", content = "hi" } } }, "/openai/v1/chat/completions"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(oaiKey.Id, fake.OpenAI.Requests[^1].AccountId);

        // The gateway key never reaches a provider.
        Assert.DoesNotContain(fake.Anthropic.Requests.Concat(fake.OpenAI.Requests), q => q.Headers.Values.Any(v => v.Contains(key)));
        _ = chatId;
    }

    [Fact]
    public async Task Preserves_body_bytes_query_and_headers_and_returns_upstream_headers()
    {
        await using var fake = await StartFake();
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, ClaudeCredentials(acct));

        var raw = "{\"model\":\"claude-sonnet-4-5-20250929\",  \"max_tokens\":64,\"messages\":[{\"role\":\"user\",\"content\":\"héllo ✓\"}],\"metadata\":{\"user_id\":\"user_x_account_y_session_aaaaaaaa-bbbb\"}}";
        var req = new HttpRequestMessage(HttpMethod.Post, "/anthropic/v1/messages?beta=true") { Content = new StringContent(raw, Encoding.UTF8, "application/json") };
        req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        req.Headers.Add("anthropic-beta", "claude-code-20250219,interleaved-thinking-2025-05-14");
        req.Headers.Add("x-app", "cli");
        req.Headers.Add("User-Agent", "claude-cli/2.0.0 (external, cli)");
        var res = await gw.CreateClient().SendAsync(req);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.Contains("anthropic-ratelimit-unified-5h-utilization"));
        Assert.True(res.Headers.Contains("request-id") || res.Headers.Contains("x-request-id"));
        var r = fake.Anthropic.Requests[^1];
        Assert.Equal(raw, r.Body);
        Assert.Equal("?beta=true", r.Query);
        Assert.Equal("cli", r.Headers["x-app"]);
        Assert.Equal("claude-cli/2.0.0 (external, cli)", r.Headers["user-agent"]);
        Assert.Equal("claude-code-20250219,interleaved-thinking-2025-05-14,oauth-2025-04-20", r.Headers["anthropic-beta"]);
    }

    [Fact]
    public async Task Streams_anthropic_sse_incrementally_and_intact()
    {
        await using var fake = await StartFake(chunkDelayMs: 40);
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, ClaudeCredentials(acct));

        var sw = Stopwatch.StartNew();
        var res = await gw.CreateClient().SendAsync(Anthropic(key, AnthropicBody(true, text: "[[out:12]] write")), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType!.ToString());
        using var reader = new StreamReader(await res.Content.ReadAsStreamAsync());
        var first = await reader.ReadLineAsync();
        var firstAt = sw.ElapsedMilliseconds;
        var rest = await reader.ReadToEndAsync();
        var doneAt = sw.ElapsedMilliseconds;

        Assert.Equal("event: message_start", first);
        Assert.True(doneAt - firstAt >= 150, $"stream was buffered: first line at {firstAt}ms, end at {doneAt}ms");
        var events = ParseSse(first + "\n" + rest);
        Assert.Equal("message_start", events[0].Event);
        Assert.Equal("message_stop", events[^1].Event);
        Assert.Contains(events, e => e.Event == "message_delta" && e.Data.GetProperty("usage").GetProperty("output_tokens").GetInt32() > 0);
        var text = string.Concat(events.Where(e => e.Event == "content_block_delta" && e.Data.GetProperty("delta").TryGetProperty("text", out _))
            .Select(e => e.Data.GetProperty("delta").GetProperty("text").GetString()));
        Assert.False(string.IsNullOrEmpty(text));
    }

    [Fact]
    public async Task Streams_openai_responses_sse_intact()
    {
        await using var fake = await StartFake();
        var acct = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new { apiKey = acct.ApiKey });

        var res = await gw.CreateClient().SendAsync(OpenAI(key, ResponsesBody()), HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.Contains("x-ratelimit-remaining-requests"));
        var events = ParseSse(await res.Content.ReadAsStringAsync());
        Assert.Equal("response.created", events[0].Event);
        Assert.Equal("response.completed", events[^1].Event);
        Assert.Equal(Enumerable.Range(0, events.Count), events.Select(e => e.Data.GetProperty("sequence_number").GetInt32()));
        Assert.True(events[^1].Data.GetProperty("response").GetProperty("usage").GetProperty("input_tokens").GetInt32() > 0);
    }

    [Fact]
    public async Task Refreshes_expired_anthropic_token_before_use_and_persists_it()
    {
        await using var fake = await StartFake();
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var originalRefresh = acct.RefreshToken;
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        var id = await gw.LinkAsync(admin, ClaudeCredentials(acct, expiresAt: DateTimeOffset.UtcNow.AddMinutes(-5)));

        var res = await gw.CreateClient().SendAsync(Anthropic(key, AnthropicBody(false)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(fake.Anthropic.Requests, r => r.Path == "/v1/oauth/token");
        Assert.NotEqual(originalRefresh, acct.RefreshToken);
        Assert.Equal("Bearer " + acct.AccessToken, fake.Anthropic.Requests[^1].Headers["authorization"]);

        var view = await admin.GetFromJsonAsync<JsonElement>($"/api/accounts/{id}");
        Assert.True(view.GetProperty("accessTokenExpiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddHours(1));

        // The rotated refresh token was persisted: a second request needs no refresh and still works.
        var before = fake.Anthropic.Requests.Count(r => r.Path == "/v1/oauth/token");
        res = await gw.CreateClient().SendAsync(Anthropic(key, AnthropicBody(false)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(before, fake.Anthropic.Requests.Count(r => r.Path == "/v1/oauth/token"));
    }

    [Fact]
    public async Task Refreshes_after_upstream_401_for_both_subscription_kinds()
    {
        await using var fake = await StartFake();
        var ant = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var chat = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, ClaudeCredentials(ant, expiresAt: DateTimeOffset.UtcNow.AddHours(6)));
        await gw.LinkAsync(admin, new { credentialsJson = CodexAuthJson(chat) });

        fake.Anthropic.ExpireAccessToken(ant.Id);
        fake.OpenAI.ExpireAccessToken(chat.Id);

        var res = await gw.CreateClient().SendAsync(Anthropic(key, AnthropicBody(false)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(fake.Anthropic.Requests, r => r.Path == "/v1/oauth/token");
        Assert.Equal(401, fake.Anthropic.Requests.First(r => r.Path == "/v1/messages").StatusCode);

        res = await gw.CreateClient().SendAsync(OpenAI(key, ResponsesBody(), session: "s1"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await res.Content.ReadAsStringAsync();
        Assert.Contains(fake.OpenAI.Requests, r => r.Path == "/oauth/token");
        Assert.Equal("Bearer " + chat.AccessToken, fake.OpenAI.Requests[^1].Headers["authorization"]);
    }

    [Fact]
    public async Task Sessions_stick_to_their_account()
    {
        await using var fake = await StartFake();
        var a1 = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var a2 = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, ClaudeCredentials(a1));
        await gw.LinkAsync(admin, ClaudeCredentials(a2));
        var client = gw.CreateClient();

        const string s1 = "aaaaaaaa-0000-0000-0000-000000000001", s2 = "bbbbbbbb-0000-0000-0000-000000000002";
        foreach (var s in new[] { s1, s2, s1, s2, s1, s1 })
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Anthropic(key, AnthropicBody(false, session: s)))).StatusCode);

        string AccountOf(string session) => fake.Anthropic.Requests.Where(r => r.Body.Contains(session)).Select(r => r.AccountId!).Distinct().Single();
        Assert.NotEqual(AccountOf(s1), AccountOf(s2));

        // Codex sessions via the session_id header.
        var o1 = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        var o2 = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        await gw.LinkAsync(admin, new { apiKey = o1.ApiKey });
        await gw.LinkAsync(admin, new { apiKey = o2.ApiKey });
        foreach (var s in new[] { "c1", "c2", "c1", "c1", "c2" })
        {
            var res = await client.SendAsync(OpenAI(key, ResponsesBody(text: "turn for " + s), session: s));
            await res.Content.ReadAsStringAsync();
        }
        string OAccountOf(string s) => fake.OpenAI.Requests.Where(r => r.Headers.GetValueOrDefault("session_id") == s).Select(r => r.AccountId!).Distinct().Single();
        Assert.NotEqual(OAccountOf("c1"), OAccountOf("c2"));
    }

    [Fact]
    public async Task Fails_over_to_another_account_when_rate_limited()
    {
        await using var fake = await StartFake();
        var a1 = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Exhausted: true));
        var a2 = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, ClaudeCredentials(a1, priority: 1));
        await gw.LinkAsync(admin, ClaudeCredentials(a2, priority: 2));

        var res = await gw.CreateClient().SendAsync(Anthropic(key, AnthropicBody(false)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var messages = fake.Anthropic.Requests.Where(r => r.Path == "/v1/messages").ToList();
        Assert.Equal([a1.Id, a2.Id], messages.Select(m => m.AccountId));
        Assert.Equal([429, 200], messages.Select(m => m.StatusCode));

        // The session now sticks to the account that worked.
        res = await gw.CreateClient().SendAsync(Anthropic(key, AnthropicBody(false)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(a2.Id, fake.Anthropic.Requests[^1].AccountId);
    }
}
