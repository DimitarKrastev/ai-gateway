using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Fakes;

namespace Gateway.Tests;

public class FakeProvidersTests
{
    private static HttpRequestMessage Json(HttpMethod method, string path, object body, params (string, string)[] headers)
    {
        var req = new HttpRequestMessage(method, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        return req;
    }

    private static object AnthropicBody(bool stream, string text = "hello", bool tools = false, bool cache = false) => new
    {
        model = "claude-sonnet-4-5-20250929",
        max_tokens = 1024,
        stream,
        system = cache ? new object[] { new { type = "text", text = new string('s', 4000), cache_control = new { type = "ephemeral" } } } : new object[] { new { type = "text", text = "sys" } },
        messages = new object[] { new { role = "user", content = text } },
        tools = tools ? new object[] { new { name = "Bash", description = "run", input_schema = new { type = "object" } } } : null,
        metadata = new { user_id = "user_abc_account_def_session_1234" },
    };

    private static List<(string? Event, JsonElement Data)> ParseSse(string body)
    {
        var events = new List<(string?, JsonElement)>();
        foreach (var block in body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            string? ev = null, data = null;
            foreach (var line in block.Split('\n'))
            {
                if (line.StartsWith("event: ")) ev = line[7..];
                else if (line.StartsWith("data: ")) data = line[6..];
            }
            if (data == null || data == "[DONE]") continue;
            events.Add((ev, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return events;
    }

    [Fact]
    public async Task Anthropic_api_key_non_streaming_returns_usage_and_standard_limit_headers()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key", ApiKey: "sk-ant-api03-test"));
        using var client = fake.CreateClient();

        var res = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false), ("x-api-key", "sk-ant-api03-test"), ("anthropic-version", "2023-06-01")));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("4000", res.Headers.GetValues("anthropic-ratelimit-requests-limit").Single());
        Assert.Equal("3999", res.Headers.GetValues("anthropic-ratelimit-requests-remaining").Single());
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("message", json.GetProperty("type").GetString());
        Assert.Equal("end_turn", json.GetProperty("stop_reason").GetString());
        Assert.True(json.GetProperty("usage").GetProperty("input_tokens").GetInt32() > 0);
        Assert.True(json.GetProperty("usage").GetProperty("output_tokens").GetInt32() > 0);

        var recorded = Assert.Single(fake.Anthropic.Requests);
        Assert.Equal(acct.Id, recorded.AccountId);
        Assert.Equal("sk-ant-api03-test", recorded.Headers["x-api-key"]);
        Assert.Contains("session_1234", recorded.Body);
    }

    [Fact]
    public async Task Anthropic_oauth_streaming_emits_sse_sequence_tool_use_and_unified_headers()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [new("five_hour", Utilization: 40)]));
        using var client = fake.CreateClient();

        var res = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(true, tools: true),
            ("authorization", "Bearer " + acct.AccessToken), ("anthropic-beta", "claude-code-20250219,oauth-2025-04-20"), ("anthropic-version", "2023-06-01")), HttpCompletionOption.ResponseHeadersRead);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType!.ToString());
        Assert.Equal("allowed", res.Headers.GetValues("anthropic-ratelimit-unified-status").Single());
        Assert.Equal("0.42", res.Headers.GetValues("anthropic-ratelimit-unified-5h-utilization").Single()); // 40 + 2 per request
        Assert.True(long.Parse(res.Headers.GetValues("anthropic-ratelimit-unified-7d-reset").Single()) > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Assert.Equal("five_hour", res.Headers.GetValues("anthropic-ratelimit-unified-representative-claim").Single());

        var events = ParseSse(await res.Content.ReadAsStringAsync());
        var names = events.Select(e => e.Event).Distinct().ToList();
        Assert.Equal(["message_start", "content_block_start", "ping", "content_block_delta", "content_block_stop", "message_delta", "message_stop"], names);
        Assert.Contains(events, e => e.Event == "content_block_start" && e.Data.GetProperty("content_block").GetProperty("type").GetString() == "tool_use"
                                     && e.Data.GetProperty("content_block").GetProperty("name").GetString() == "Bash");
        var delta = events.Single(e => e.Event == "message_delta").Data;
        Assert.Equal("tool_use", delta.GetProperty("delta").GetProperty("stop_reason").GetString());
        Assert.True(delta.GetProperty("usage").GetProperty("output_tokens").GetInt32() > 1);
    }

    [Fact]
    public async Task Anthropic_prompt_cache_hits_on_same_account_and_misses_on_another()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        var a = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key", ApiKey: "key-a"));
        fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key", ApiKey: "key-b"));
        using var client = fake.CreateClient();

        async Task<JsonElement> Send(string key)
        {
            var res = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false, cache: true), ("x-api-key", key), ("anthropic-version", "2023-06-01")));
            return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("usage");
        }

        var first = await Send("key-a");
        Assert.Equal(0, first.GetProperty("cache_read_input_tokens").GetInt32());
        Assert.True(first.GetProperty("cache_creation_input_tokens").GetInt32() > 900);
        var second = await Send("key-a");
        Assert.Equal(first.GetProperty("cache_creation_input_tokens").GetInt32(), second.GetProperty("cache_read_input_tokens").GetInt32());
        Assert.Equal(0, second.GetProperty("cache_creation_input_tokens").GetInt32());
        var other = await Send("key-b");
        Assert.Equal(0, other.GetProperty("cache_read_input_tokens").GetInt32());
        Assert.Equal(2, fake.Anthropic.Get(a.Id)!.RequestCount);
    }

    [Fact]
    public async Task Anthropic_rejects_bad_auth_and_missing_version()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        using var client = fake.CreateClient();

        var unknown = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false), ("x-api-key", "nope"), ("anthropic-version", "2023-06-01")));
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("authentication_error", (await unknown.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());

        var noBeta = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false), ("authorization", "Bearer " + acct.AccessToken), ("anthropic-version", "2023-06-01")));
        Assert.Equal(HttpStatusCode.Unauthorized, noBeta.StatusCode);

        var noVersion = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false), ("authorization", "Bearer " + acct.AccessToken), ("anthropic-beta", AnthropicFake.OAuthBeta)));
        Assert.Equal(HttpStatusCode.BadRequest, noVersion.StatusCode);
    }

    [Fact]
    public async Task Anthropic_exhausted_account_returns_429_with_retry_after()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        var resetsAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Windows: [new("five_hour", Utilization: 100, ResetsAt: resetsAt)]));
        using var client = fake.CreateClient();

        var res = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false),
            ("authorization", "Bearer " + acct.AccessToken), ("anthropic-beta", AnthropicFake.OAuthBeta), ("anthropic-version", "2023-06-01")));

        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("rejected", res.Headers.GetValues("anthropic-ratelimit-unified-status").Single());
        Assert.InRange(int.Parse(res.Headers.GetValues("retry-after").Single()), 1700, 1801);
        Assert.Equal("rate_limit_error", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());
        Assert.Equal(0, fake.Anthropic.Get(acct.Id)!.RequestCount);
    }

    [Fact]
    public async Task Anthropic_token_refresh_rotates_tokens_and_expired_tokens_are_rejected()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", AccessToken: "old-at", RefreshToken: "old-rt"));
        fake.Anthropic.ExpireAccessToken(acct.Id);
        using var client = fake.CreateClient();
        HttpRequestMessage Msg(string token) => Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false),
            ("authorization", "Bearer " + token), ("anthropic-beta", AnthropicFake.OAuthBeta), ("anthropic-version", "2023-06-01"));

        var expired = await client.SendAsync(Msg("old-at"));
        Assert.Equal(HttpStatusCode.Unauthorized, expired.StatusCode);

        var refresh = await client.PostAsJsonAsync("/v1/oauth/token", new { grant_type = "refresh_token", refresh_token = "old-rt", client_id = "x" });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var tokens = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        var newAt = tokens.GetProperty("access_token").GetString()!;
        Assert.StartsWith("sk-ant-oat01-", newAt);
        Assert.True(tokens.GetProperty("expires_in").GetInt32() > 0);

        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Msg(newAt))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(Msg("old-at"))).StatusCode);
        var reuse = await client.PostAsJsonAsync("/v1/oauth/token", new { grant_type = "refresh_token", refresh_token = "old-rt" });
        Assert.Equal(HttpStatusCode.BadRequest, reuse.StatusCode);
    }

    [Fact]
    public async Task Anthropic_usage_and_resets_redeem_zeroes_windows()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth",
            Windows: [new("five_hour", Utilization: 100), new("seven_day", Utilization: 70)],
            Resets: [new("rst_late", DateTimeOffset.UtcNow.AddDays(5)), new("rst_soon", DateTimeOffset.UtcNow.AddDays(1))]));
        using var client = fake.CreateClient();
        HttpRequestMessage Req(HttpMethod m, string path)
        {
            var r = new HttpRequestMessage(m, path);
            r.Headers.Add("authorization", "Bearer " + acct.AccessToken);
            r.Headers.Add("anthropic-beta", AnthropicFake.OAuthBeta);
            return r;
        }

        var usage = await (await client.SendAsync(Req(HttpMethod.Get, "/api/oauth/usage"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, usage.GetProperty("five_hour").GetProperty("utilization").GetDouble());
        Assert.Equal(70, usage.GetProperty("seven_day").GetProperty("utilization").GetDouble());
        Assert.True(DateTimeOffset.Parse(usage.GetProperty("five_hour").GetProperty("resets_at").GetString()!) > DateTimeOffset.UtcNow);

        var resets = await (await client.SendAsync(Req(HttpMethod.Get, "/api/oauth/usage/resets"))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(new[] { "rst_soon", "rst_late" }, resets.GetProperty("resets").EnumerateArray().Select(r => r.GetProperty("id").GetString()!).ToArray());

        var redeem = await client.SendAsync(Req(HttpMethod.Post, "/api/oauth/usage/resets/rst_soon/redeem"));
        Assert.Equal(HttpStatusCode.OK, redeem.StatusCode);
        var after = (await redeem.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("usage");
        Assert.Equal(0, after.GetProperty("five_hour").GetProperty("utilization").GetDouble());
        Assert.Equal(0, after.GetProperty("seven_day").GetProperty("utilization").GetDouble());
        Assert.Single(fake.Anthropic.Get(acct.Id)!.Resets);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Req(HttpMethod.Post, "/api/oauth/usage/resets/rst_soon/redeem"))).StatusCode);
    }

    [Fact]
    public async Task OpenAI_responses_stream_emits_function_call_and_completed_usage_with_cache()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key", ApiKey: "sk-proj-1"));
        using var client = fake.CreateClient();
        var body = new
        {
            model = "gpt-5-codex", instructions = new string('i', 2000), stream = true, prompt_cache_key = "conv-1",
            reasoning = new { effort = "medium" },
            input = new object[] { new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "list files" } } } },
            tools = new object[] { new { type = "function", name = "shell", parameters = new { type = "object" } } },
        };

        async Task<List<(string? Event, JsonElement Data)>> Send()
        {
            var res = await client.SendAsync(Json(HttpMethod.Post, "/v1/responses", body, ("authorization", "Bearer sk-proj-1")));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.Equal("4000", res.Headers.GetValues("x-ratelimit-limit-requests").Single());
            return ParseSse(await res.Content.ReadAsStringAsync());
        }

        var events = await Send();
        Assert.Equal("response.created", events[0].Event);
        Assert.All(events, e => Assert.Equal(e.Event, e.Data.GetProperty("type").GetString()));
        Assert.Contains(events, e => e.Event == "response.function_call_arguments.done");
        var item = events.First(e => e.Event == "response.output_item.done").Data.GetProperty("item");
        Assert.Equal("function_call", item.GetProperty("type").GetString());
        Assert.Equal("shell", item.GetProperty("name").GetString());
        var completed = events.Last();
        Assert.Equal("response.completed", completed.Event);
        var usage = completed.Data.GetProperty("response").GetProperty("usage");
        Assert.Equal(0, usage.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
        Assert.True(usage.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32() > 0);

        var second = (await Send()).Last().Data.GetProperty("response").GetProperty("usage");
        Assert.Equal(second.GetProperty("input_tokens").GetInt32(), second.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32());
    }

    [Fact]
    public async Task OpenAI_chat_completions_stream_with_usage_and_done()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        using var client = fake.CreateClient();
        var res = await client.SendAsync(Json(HttpMethod.Post, "/v1/chat/completions", new
        {
            model = "gpt-5", stream = true, stream_options = new { include_usage = true },
            messages = new object[] { new { role = "user", content = "hi" } },
        }, ("authorization", "Bearer sk-auto")));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var raw = await res.Content.ReadAsStringAsync();
        Assert.EndsWith("data: [DONE]\n\n", raw);
        var chunks = ParseSse(raw);
        Assert.All(chunks, c => Assert.Equal("chat.completion.chunk", c.Data.GetProperty("object").GetString()));
        Assert.Equal("stop", chunks[^2].Data.GetProperty("choices")[0].GetProperty("finish_reason").GetString());
        Assert.True(chunks[^1].Data.GetProperty("usage").GetProperty("prompt_tokens").GetInt32() > 0);
        Assert.Single(fake.OpenAI.Accounts); // auto-registered
    }

    [Fact]
    public async Task Codex_backend_requires_account_header_reports_codex_headers_and_limits()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var acct = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth", ChatGptAccountId: "acc-1",
            Windows: [new("primary", Utilization: 97, IncrementPerRequest: 2), new("secondary", Utilization: 10)]));
        using var client = fake.CreateClient();
        var body = new { model = "gpt-5-codex", stream = true, store = false, instructions = "x", input = new object[] { new { type = "message", role = "user", content = "hi" } } };

        var noHeader = await client.SendAsync(Json(HttpMethod.Post, "/backend-api/codex/responses", body, ("authorization", "Bearer " + acct.AccessToken)));
        Assert.Equal(HttpStatusCode.Unauthorized, noHeader.StatusCode);

        var ok = await client.SendAsync(Json(HttpMethod.Post, "/backend-api/codex/responses", body, ("authorization", "Bearer " + acct.AccessToken), ("chatgpt-account-id", "acc-1")));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("99", ok.Headers.GetValues("x-codex-primary-used-percent").Single());
        Assert.Equal("300", ok.Headers.GetValues("x-codex-primary-window-minutes").Single());
        Assert.Equal("10080", ok.Headers.GetValues("x-codex-secondary-window-minutes").Single());
        Assert.True(long.Parse(ok.Headers.GetValues("x-codex-primary-reset-after-seconds").Single()) > 0);
        Assert.Equal("response.completed", ParseSse(await ok.Content.ReadAsStringAsync()).Last().Event);

        await client.SendAsync(Json(HttpMethod.Post, "/backend-api/codex/responses", body, ("authorization", "Bearer " + acct.AccessToken), ("chatgpt-account-id", "acc-1")));
        var limited = await client.SendAsync(Json(HttpMethod.Post, "/backend-api/codex/responses", body, ("authorization", "Bearer " + acct.AccessToken), ("chatgpt-account-id", "acc-1")));
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("usage_limit_reached", (await limited.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());

        var usageReq = new HttpRequestMessage(HttpMethod.Get, "/backend-api/wham/usage");
        usageReq.Headers.Add("authorization", "Bearer " + acct.AccessToken);
        usageReq.Headers.Add("chatgpt-account-id", "acc-1");
        var usage = await (await client.SendAsync(usageReq)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(usage.GetProperty("rate_limit").GetProperty("limit_reached").GetBoolean());
        Assert.Equal(100, usage.GetProperty("rate_limit").GetProperty("primary_window").GetProperty("used_percent").GetDouble());
        Assert.Equal(604800, usage.GetProperty("rate_limit").GetProperty("secondary_window").GetProperty("limit_window_seconds").GetInt32());
    }

    [Fact]
    public async Task Codex_token_refresh_returns_jwt_tokens_and_rejects_non_streaming()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var acct = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth", ChatGptAccountId: "acc-2", RefreshToken: "rt_1"));
        using var client = fake.CreateClient();

        var nonStream = await client.SendAsync(Json(HttpMethod.Post, "/backend-api/codex/responses", new { model = "gpt-5-codex", input = "hi" },
            ("authorization", "Bearer " + acct.AccessToken), ("chatgpt-account-id", "acc-2")));
        Assert.Equal(HttpStatusCode.BadRequest, nonStream.StatusCode);

        var refresh = await client.PostAsJsonAsync("/oauth/token", new { client_id = "app_x", grant_type = "refresh_token", refresh_token = "rt_1", scope = "openid profile email" });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        var tokens = await refresh.Content.ReadFromJsonAsync<JsonElement>();
        var access = tokens.GetProperty("access_token").GetString()!;
        Assert.Equal(3, access.Split('.').Length);
        var payload = access.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
        var claims = JsonDocument.Parse(Convert.FromBase64String(payload)).RootElement;
        Assert.Equal("acc-2", claims.GetProperty("https://api.openai.com/auth").GetProperty("chatgpt_account_id").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/oauth/token", new { grant_type = "refresh_token", refresh_token = "rt_1" })).StatusCode);
    }

    [Fact]
    public async Task Admin_api_registers_patches_and_lists_recorded_requests()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        using var client = fake.CreateClient();

        var created = await client.PostAsJsonAsync("/fake-admin/openai/accounts", new { id = "oa1", kind = "oauth", chatGptAccountId = "acc-x" });
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var acct = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("oauth", acct.GetProperty("kind").GetString());
        Assert.Equal(2, acct.GetProperty("resets").GetArrayLength());

        var patched = await client.PatchAsJsonAsync("/fake-admin/openai/accounts/oa1", new { exhausted = true, windows = new[] { new { name = "primary", utilization = 55.5 } } });
        var p = await patched.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(p.GetProperty("exhausted").GetBoolean());
        Assert.Equal(55.5, p.GetProperty("windows").EnumerateArray().First(w => w.GetProperty("name").GetString() == "primary").GetProperty("utilization").GetDouble());

        var token = acct.GetProperty("accessToken").GetString()!;
        await client.SendAsync(Json(HttpMethod.Post, "/backend-api/codex/responses", new { model = "m", stream = true, input = "hi" },
            ("authorization", "Bearer " + token), ("chatgpt-account-id", "acc-x"), ("session_id", "sess-9")));
        var requests = await client.GetFromJsonAsync<JsonElement>("/fake-admin/openai/requests?accountId=oa1");
        var r = Assert.Single(requests.EnumerateArray());
        Assert.Equal(429, r.GetProperty("statusCode").GetInt32());
        Assert.Equal("sess-9", r.GetProperty("headers").GetProperty("session_id").GetString());

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/fake-admin/reset", null)).StatusCode);
        Assert.Empty(fake.OpenAI.Accounts);
    }

    [Fact]
    public async Task Injected_error_trigger_returns_requested_status()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        using var client = fake.CreateClient();
        var res = await client.SendAsync(Json(HttpMethod.Post, "/v1/messages", AnthropicBody(false, "boom [[error:529]]"), ("x-api-key", "k"), ("anthropic-version", "2023-06-01")));
        Assert.Equal((HttpStatusCode)529, res.StatusCode);
        Assert.Equal("overloaded_error", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());
    }
}
