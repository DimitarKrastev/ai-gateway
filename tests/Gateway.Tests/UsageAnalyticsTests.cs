using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Fakes;
using Gateway.Proxy;
using Gateway.Usage;
using Microsoft.Extensions.DependencyInjection;
using static Gateway.Tests.ProxyRequests;

namespace Gateway.Tests;

public class UsageAnalyticsTests
{
    private const string ClaudeUa = "claude-cli/2.0.14 (external, cli)";
    private const string CodexUa = "codex_cli_rs/0.50.0 (Mac OS 15.0; arm64)";

    private static Task<FakeProviderServer> StartFake(int chunkDelayMs = 0) =>
        FakeProviderServer.StartInMemoryAsync(o => { o.AutoRegister = false; o.StreamChunkDelayMs = chunkDelayMs; });

    private static object ClaudeCreds(FakeAccount a, int priority = 100, string? name = null) => new
    {
        name,
        priority,
        credentialsJson = JsonSerializer.Serialize(new
        {
            claudeAiOauth = new { accessToken = a.AccessToken, refreshToken = a.RefreshToken, expiresAt = a.AccessTokenExpiresAt!.Value.ToUnixTimeMilliseconds() },
        }),
    };

    /// <summary>A Claude Code-shaped request: billing header + identity system blocks, cached conversation prefix.</summary>
    private static object ClaudeBody(string session, string firstUser, string last, string[] tools, bool stream = true,
        string model = "claude-sonnet-4-5-20250929") => new
    {
        model,
        max_tokens = 4096,
        stream,
        system = new object[]
        {
            new { type = "text", text = "x-anthropic-billing-header: cc_version=2.0.14; cc_entrypoint=cli;" },
            new { type = "text", text = "You are Claude Code, Anthropic's official CLI for Claude.", cache_control = new { type = "ephemeral" } },
        },
        messages = new object[]
        {
            new { role = "user", content = new object[] { new { type = "text", text = firstUser, cache_control = new { type = "ephemeral" } } } },
            new { role = "assistant", content = "Sure." },
            new { role = "user", content = last },
        },
        tools = tools.Select(t => new { name = t, description = t, input_schema = new { type = "object" } }).ToArray(),
        metadata = new { user_id = $"user_abc_account_def_session_{session}" },
    };

    private static HttpRequestMessage Claude(string key, object body)
    {
        var req = Anthropic(key, body, bearer: true);
        req.Headers.TryAddWithoutValidation("User-Agent", ClaudeUa);
        return req;
    }

    private static HttpRequestMessage Codex(string key, object body, string session, string? subagent = null)
    {
        var req = OpenAI(key, body, session: session);
        req.Headers.TryAddWithoutValidation("User-Agent", CodexUa);
        req.Headers.Add("originator", "codex_cli_rs");
        if (subagent != null) req.Headers.Add("x-openai-subagent", subagent);
        return req;
    }

    private static async Task<JsonElement> Get(HttpClient admin, string url)
    {
        var res = await admin.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"{url}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>Request log, newest first; waits until <paramref name="count"/> attempts have been recorded.</summary>
    private static async Task<JsonElement[]> WaitForRequests(HttpClient admin, int count)
    {
        for (var i = 0; ; i++)
        {
            var items = (await Get(admin, "/api/analytics/requests?limit=500")).GetProperty("items").EnumerateArray().ToArray();
            if (items.Length >= count || i > 100) return items;
            await Task.Delay(50);
        }
    }

    private static async Task<(ProxyGatewayFactory Gw, HttpClient Admin, string Key)> Setup(FakeProviderServer fake, Action<IServiceCollection>? services = null)
    {
        var gw = new ProxyGatewayFactory(fake) { ExtraServices = services };
        var admin = await gw.CreateLoggedInClientAsync();
        return (gw, admin, await gw.CreateClientKeyAsync(admin));
    }

    private static double Cost(string model, long input, long output, long cacheRead, long cacheWrite)
    {
        var p = new PricingTable(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()).Find(model)!;
        return (input * p.Input + output * p.Output + cacheRead * p.CacheRead + cacheWrite * p.CacheWrite) / 1e6;
    }

    [Fact]
    public async Task Records_anthropic_stream_and_json_usage_with_cache_tool_calls_and_cost()
    {
        await using var fake = await StartFake();
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await gw.LinkAsync(admin, ClaudeCreds(acct));
        var client = gw.CreateClient();
        var session = Guid.NewGuid().ToString();

        var res = await client.SendAsync(Claude(key, ClaudeBody(session, "Fix the bug", "[[tools:2]] go", ["Bash", "Task"])));
        var sse = ParseSse(await res.Content.ReadAsStringAsync());
        var finalUsage = sse.Last(e => e.Event == "message_delta").Data.GetProperty("usage");

        res = await client.SendAsync(Claude(key, ClaudeBody(session, "Fix the bug", "[[no-tool]] done", ["Bash", "Task"], stream: false)));
        var json = await res.Content.ReadFromJsonAsync<JsonElement>();
        var jsonUsage = json.GetProperty("usage");

        var items = await WaitForRequests(admin, 2);
        var (streamed, plain) = (items[1], items[0]);

        long L(JsonElement e, string p) => e.GetProperty(p).GetInt64();
        Assert.True(streamed.GetProperty("stream").GetBoolean());
        Assert.Equal("claude-sonnet-4-5-20250929", streamed.GetProperty("model").GetString());
        Assert.Equal(L(finalUsage, "input_tokens"), L(streamed, "inputTokens"));
        Assert.Equal(L(finalUsage, "output_tokens"), L(streamed, "outputTokens"));
        Assert.Equal(L(finalUsage, "cache_creation_input_tokens"), L(streamed, "cacheWriteTokens"));
        Assert.True(L(streamed, "cacheWriteTokens") > 0);
        Assert.Equal(L(streamed, "inputTokens") + L(streamed, "cacheReadTokens") + L(streamed, "cacheWriteTokens"), L(streamed, "contextTokens"));
        Assert.Equal(2, L(streamed, "toolCalls"));
        Assert.Equal(2, streamed.GetProperty("tools").EnumerateObject().Sum(t => t.Value.GetInt32()));
        Assert.Equal("tool_use", streamed.GetProperty("stopReason").GetString());
        Assert.Equal(Cost("claude-sonnet-4-5", L(streamed, "inputTokens"), L(streamed, "outputTokens"), L(streamed, "cacheReadTokens"), L(streamed, "cacheWriteTokens")),
            streamed.GetProperty("costUsd").GetDouble(), 6);
        Assert.Equal("claude-code", streamed.GetProperty("client").GetString());
        Assert.Equal("2.0.14", streamed.GetProperty("clientVersion").GetString());
        Assert.Equal("main", streamed.GetProperty("agentRole").GetString());
        Assert.Equal(session, streamed.GetProperty("sessionId").GetString());

        Assert.False(plain.GetProperty("stream").GetBoolean());
        Assert.Equal(L(jsonUsage, "cache_read_input_tokens"), L(plain, "cacheReadTokens"));
        Assert.True(L(plain, "cacheReadTokens") > 0, "second request should hit the prompt cache");
        Assert.Equal(L(jsonUsage, "output_tokens"), L(plain, "outputTokens"));
        Assert.Equal(0, L(plain, "toolCalls"));
        Assert.Equal(streamed.GetProperty("agentId").GetString(), plain.GetProperty("agentId").GetString());

        var summary = await Get(admin, "/api/analytics/summary");
        Assert.Equal(2, L(summary, "requests"));
        Assert.Equal(L(streamed, "outputTokens") + L(plain, "outputTokens"), L(summary, "outputTokens"));
        Assert.Equal(2, L(summary, "toolCalls"));
        Assert.Equal(1, L(summary, "sessions"));
        Assert.Equal(1, L(summary, "agents"));
        Assert.True(summary.GetProperty("cacheHitRate").GetDouble() > 0);
        Assert.Equal(streamed.GetProperty("costUsd").GetDouble() + plain.GetProperty("costUsd").GetDouble(), summary.GetProperty("costUsd").GetDouble(), 6);
    }

    [Fact]
    public async Task Records_openai_responses_and_chat_completions_usage()
    {
        await using var fake = await StartFake();
        var acct = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await gw.LinkAsync(admin, new { apiKey = acct.ApiKey });
        var client = gw.CreateClient();

        var body = new
        {
            model = "gpt-5-codex", instructions = "You are Codex.", stream = true, store = false, prompt_cache_key = "conv-1",
            reasoning = new { effort = "medium" },
            input = new object[] { new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "[[tool:shell]] list files" } } } },
            tools = new object[] { new { type = "function", name = "shell", parameters = new { type = "object" } } },
        };
        var res = await client.SendAsync(Codex(key, body, "conv-1"));
        var completed = ParseSse(await res.Content.ReadAsStringAsync()).Last(e => e.Data.GetProperty("type").GetString() == "response.completed")
            .Data.GetProperty("response").GetProperty("usage");

        var chat = new
        {
            model = "gpt-4.1", stream = true, stream_options = new { include_usage = true },
            messages = new object[] { new { role = "user", content = "[[tools:2]] weather?" } },
            tools = new object[] { new { type = "function", function = new { name = "get_weather", parameters = new { type = "object" } } } },
        };
        res = await client.SendAsync(OpenAI(key, chat, "/openai/v1/chat/completions"));
        var chatUsage = ParseSse(await res.Content.ReadAsStringAsync()).Last(e => e.Data.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            .Data.GetProperty("usage");

        var items = await WaitForRequests(admin, 2);
        var (responses, completions) = (items[1], items[0]);
        long L(JsonElement e, string p) => e.GetProperty(p).GetInt64();

        var input = L(completed, "input_tokens");
        var cached = completed.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt64();
        Assert.Equal("responses", responses.GetProperty("endpoint").GetString());
        Assert.Equal(input - cached, L(responses, "inputTokens"));
        Assert.Equal(cached, L(responses, "cacheReadTokens"));
        Assert.Equal(input, L(responses, "contextTokens"));
        Assert.Equal(L(completed, "output_tokens"), L(responses, "outputTokens"));
        Assert.Equal(completed.GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt64(), L(responses, "reasoningTokens"));
        Assert.True(L(responses, "reasoningTokens") > 0);
        Assert.Equal(1, responses.GetProperty("tools").GetProperty("shell").GetInt32());
        Assert.Equal("codex", responses.GetProperty("client").GetString());
        Assert.Equal("main", responses.GetProperty("agentRole").GetString());
        Assert.True(responses.GetProperty("costUsd").GetDouble() > 0);

        Assert.Equal("chat", completions.GetProperty("endpoint").GetString());
        Assert.Equal(L(chatUsage, "prompt_tokens"), L(completions, "contextTokens"));
        Assert.Equal(L(chatUsage, "completion_tokens"), L(completions, "outputTokens"));
        Assert.Equal(2, L(completions, "toolCalls"));
        Assert.Equal(Cost("gpt-4.1", L(completions, "inputTokens"), L(completions, "outputTokens"), L(completions, "cacheReadTokens"), 0),
            completions.GetProperty("costUsd").GetDouble(), 6);
    }

    [Fact]
    public async Task Records_chatgpt_codex_backend_usage_and_codex_subagents()
    {
        await using var fake = await StartFake();
        var acct = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await gw.LinkAsync(admin, new
        {
            name = "ChatGPT Pro",
            credentialsJson = JsonSerializer.Serialize(new { tokens = new { access_token = acct.AccessToken, refresh_token = acct.RefreshToken, account_id = acct.ChatGptAccountId } }),
        });
        var client = gw.CreateClient();

        var res = await client.SendAsync(Codex(key, ResponsesBody(text: "[[no-tool]] hi"), "sess-codex"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await res.Content.ReadAsStringAsync();
        res = await client.SendAsync(Codex(key, ResponsesBody(text: "[[no-tool]] review"), "sess-codex", subagent: "review"));
        await res.Content.ReadAsStringAsync();

        var items = await WaitForRequests(admin, 2);
        Assert.All(items, i => Assert.Equal("v1/responses", i.GetProperty("path").GetString()));
        Assert.All(items, i => Assert.True(i.GetProperty("outputTokens").GetInt64() > 0));
        Assert.All(items, i => Assert.Equal("ChatGPT Pro", i.GetProperty("accountName").GetString()));
        Assert.Equal("subagent:review", items[0].GetProperty("agentRole").GetString());
        Assert.Equal("main", items[1].GetProperty("agentRole").GetString());

        var agents = (await Get(admin, "/api/analytics/breakdown?by=agent")).GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("key").GetString()!).Order().ToArray();
        Assert.Equal(["codex:main", "codex:subagent:review"], agents);
    }

    [Fact]
    public async Task Gateway_retried_attempts_are_logged_but_not_counted_as_client_requests()
    {
        await using var fake = await StartFake();
        var exhausted = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth", Exhausted: true));
        var healthy = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await gw.LinkAsync(admin, ClaudeCreds(exhausted, priority: 1));
        await gw.LinkAsync(admin, ClaudeCreds(healthy, priority: 2));

        var res = await gw.CreateClient().SendAsync(Claude(key, ClaudeBody(Guid.NewGuid().ToString(), "task", "[[no-tool]] hi", ["Bash"])));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        await res.Content.ReadAsStringAsync();

        var items = await WaitForRequests(admin, 2);
        Assert.Equal(items[0].GetProperty("requestId").GetString(), items[1].GetProperty("requestId").GetString());
        var retried = items.Single(i => i.GetProperty("retried").GetBoolean());
        Assert.Equal(429, retried.GetProperty("status").GetInt32());
        Assert.Equal("rate_limit_error", retried.GetProperty("errorType").GetString());
        Assert.Equal(2, items.Single(i => !i.GetProperty("retried").GetBoolean()).GetProperty("attempt").GetInt32());

        var summary = await Get(admin, "/api/analytics/summary");
        Assert.Equal(1, summary.GetProperty("requests").GetInt64());
        Assert.Equal(0, summary.GetProperty("errors").GetInt64());
        Assert.Equal(1, summary.GetProperty("gatewayRetries").GetInt64());
        var withRetries = await Get(admin, "/api/analytics/summary?includeRetried=true");
        Assert.Equal(2, withRetries.GetProperty("requests").GetInt64());
        Assert.Equal(1, withRetries.GetProperty("rateLimited").GetInt64());
    }

    private sealed class CaptureObserver : IRequestObserver
    {
        public readonly ConcurrentDictionary<string, MemoryStream> Bodies = new();
        public void OnResponseChunk(ProxiedRequestContext ctx, ReadOnlySpan<byte> chunk) =>
            Bodies.GetOrAdd(ctx.RequestId, _ => new MemoryStream()).Write(chunk);
    }

    [Fact]
    public async Task Client_receives_upstream_stream_byte_for_byte_while_usage_is_captured()
    {
        await using var fake = await StartFake();
        var ant = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var oai = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        var capture = new CaptureObserver();
        var (gw, admin, key) = await Setup(fake, s => s.AddSingleton<IRequestObserver>(capture));
        using var _ = gw;
        await gw.LinkAsync(admin, ClaudeCreds(ant));
        await gw.LinkAsync(admin, new { apiKey = oai.ApiKey });
        var client = gw.CreateClient();

        var received = new List<byte[]>
        {
            await (await client.SendAsync(Claude(key, ClaudeBody("s-bytes", "héllo ✓", "[[tools:3]] go", ["Bash"])))).Content.ReadAsByteArrayAsync(),
            await (await client.SendAsync(Codex(key, ResponsesBody(text: "[[out:200]] long"), "c-bytes"))).Content.ReadAsByteArrayAsync(),
        };
        await WaitForRequests(admin, 2);

        var upstream = capture.Bodies.Values.Select(m => m.ToArray()).ToList();
        Assert.Equal(2, upstream.Count);
        foreach (var body in received)
            Assert.Contains(upstream, u => u.AsSpan().SequenceEqual(body));
        var summary = await Get(admin, "/api/analytics/summary");
        Assert.Equal(3, summary.GetProperty("toolCalls").GetInt64());
        Assert.True(summary.GetProperty("outputTokens").GetInt64() >= 200);
    }

    [Fact]
    public async Task Identifies_claude_code_main_subagents_and_utility_calls_and_measures_concurrency()
    {
        await using var fake = await StartFake(chunkDelayMs: 0);
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        await gw.LinkAsync(admin, ClaudeCreds(acct));
        var client = gw.CreateClient();
        var session = Guid.NewGuid().ToString();
        string[] mainTools = ["Bash", "Read", "Task"], subTools = ["Bash", "Read"];

        async Task Send(object body) => await (await client.SendAsync(Claude(key, body))).Content.ReadAsStringAsync();

        // Main agent starts, then spawns two subagents that run in parallel with it.
        await Send(ClaudeBody(session, "Refactor the app", "[[no-tool]] plan", mainTools));
        var parallel = Task.WhenAll(
            Send(ClaudeBody(session, "Refactor the app", "[[slow:40]] [[out:20]] wait", mainTools)),
            Send(ClaudeBody(session, "Explore the auth module", "[[slow:40]] [[out:20]] look", subTools)),
            Send(ClaudeBody(session, "Explore the billing module", "[[slow:40]] [[out:20]] look", subTools)));

        // While streams are in flight the live view shows them.
        JsonElement live = default;
        for (var i = 0; i < 100; i++)
        {
            live = await Get(admin, "/api/analytics/live");
            if (live.GetProperty("inFlightRequests").GetInt32() >= 3) break;
            await Task.Delay(10);
        }
        Assert.True(live.GetProperty("inFlightRequests").GetInt32() >= 2, live.ToString());
        Assert.True(live.GetProperty("activeAgents").GetInt32() >= 2, live.ToString());
        await parallel;

        // Utility call (no tools), e.g. a title generator on Haiku.
        await Send(new
        {
            model = "claude-haiku-4-5-20251001", max_tokens = 64, stream = false,
            system = new object[] { new { type = "text", text = "Summarize this conversation in 5 words." } },
            messages = new object[] { new { role = "user", content = "[[no-tool]] title" } },
            metadata = new { user_id = $"user_abc_account_def_session_{session}" },
        });
        await WaitForRequests(admin, 5);
        Assert.Equal(0, (await Get(admin, "/api/analytics/live")).GetProperty("inFlightRequests").GetInt32());

        var byAgentId = (await Get(admin, "/api/analytics/breakdown?by=agentId")).GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(4, byAgentId.Length);
        var byAgent = (await Get(admin, "/api/analytics/breakdown?by=agent")).GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!, r => r);
        Assert.Equal(2, byAgent["claude-code:main"].GetProperty("requests").GetInt64());
        Assert.Equal(1, byAgent["claude-code:main"].GetProperty("agents").GetInt64());
        Assert.Equal(2, byAgent["claude-code:subagent"].GetProperty("agents").GetInt64());
        Assert.Equal(1, byAgent["claude-code:utility"].GetProperty("requests").GetInt64());

        var summary = await Get(admin, "/api/analytics/summary");
        Assert.Equal(4, summary.GetProperty("agents").GetInt64());
        Assert.Equal(3, summary.GetProperty("peakConcurrentRequests").GetInt32());
        Assert.Equal(3, summary.GetProperty("peakConcurrentAgents").GetInt32());

        var ts = await Get(admin, "/api/analytics/timeseries?metric=concurrentAgents,concurrentRequests,activeAgents&bucket=minute");
        Assert.Equal("minute", ts.GetProperty("bucket").GetString());
        var points = ts.GetProperty("points").EnumerateArray().ToArray();
        Assert.Equal(3, points.Max(p => p.GetProperty("s0").GetDouble()));
        Assert.Equal(3, points.Max(p => p.GetProperty("s1").GetDouble()));
        Assert.Equal(4, points.Max(p => p.GetProperty("s2").GetDouble()), 0);

        var sessions = await Get(admin, "/api/analytics/sessions");
        var s = sessions.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(session, s.GetProperty("sessionId").GetString());
        Assert.Equal(5, s.GetProperty("requests").GetInt64());
        Assert.Equal(4, s.GetProperty("agents").GetInt64());
    }

    [Fact]
    public async Task Analytics_endpoints_filter_group_and_require_login()
    {
        await using var fake = await StartFake();
        var ant = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        var oai = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        var (gw, admin, key) = await Setup(fake);
        using var _ = gw;
        var antId = await gw.LinkAsync(admin, new { name = "Work Claude", apiKey = ant.ApiKey });
        await gw.LinkAsync(admin, new { name = "Personal OpenAI", apiKey = oai.ApiKey });
        var client = gw.CreateClient();

        for (var i = 0; i < 3; i++)
            await (await client.SendAsync(Claude(key, ClaudeBody($"ant-session-{i % 2}", "task", "[[tools:1]] go", ["Bash"])))).Content.ReadAsStringAsync();
        for (var i = 0; i < 2; i++)
            await (await client.SendAsync(Codex(key, ResponsesBody(text: "[[no-tool]] hi"), "codex-1"))).Content.ReadAsStringAsync();
        await (await client.SendAsync(Anthropic(key, new { model = "claude-sonnet-4-5", messages = new[] { new { role = "user", content = "hi" } } },
            path: "/anthropic/v1/messages/count_tokens"))).Content.ReadAsStringAsync();
        await WaitForRequests(admin, 6);

        var all = await Get(admin, "/api/analytics/summary");
        Assert.Equal(6, all.GetProperty("requests").GetInt64());
        Assert.Equal(4, all.GetProperty("sessions").GetInt64()); // 2 Claude sessions, codex-1 + the derived session of count_tokens

        var openai = await Get(admin, "/api/analytics/summary?provider=openai");
        Assert.Equal(2, openai.GetProperty("requests").GetInt64());
        var work = await Get(admin, $"/api/analytics/summary?accountId={antId}");
        Assert.Equal(4, work.GetProperty("requests").GetInt64());
        Assert.Equal(3, work.GetProperty("toolCalls").GetInt64());
        Assert.Equal(2, (await Get(admin, "/api/analytics/summary?sessionId=ant-session-0")).GetProperty("requests").GetInt64());
        Assert.Equal(0, (await Get(admin, $"/api/analytics/summary?to={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(-1).ToString("O"))}")).GetProperty("requests").GetInt64());

        var byAccount = await Get(admin, "/api/analytics/timeseries?metric=requests&groupBy=account");
        var series = byAccount.GetProperty("series").EnumerateArray().ToArray();
        Assert.Equal(["Personal OpenAI", "Work Claude"], series.Select(s => s.GetProperty("label").GetString()!).Order().ToArray());
        var work0 = series.Single(s => s.GetProperty("label").GetString() == "Work Claude").GetProperty("key").GetString()!;
        Assert.Equal(4, byAccount.GetProperty("points").EnumerateArray().Sum(p => p.TryGetProperty(work0, out var v) ? v.GetDouble() : 0));
        Assert.Equal(4, series.Single(s => s.GetProperty("key").GetString() == work0).GetProperty("total").GetDouble());

        var tokens = await Get(admin, "/api/analytics/timeseries?metric=inputTokens,outputTokens,cost&bucket=hour");
        Assert.Equal(["inputTokens", "outputTokens", "cost"], tokens.GetProperty("series").EnumerateArray().Select(s => s.GetProperty("id").GetString()!).ToArray());
        Assert.Equal(all.GetProperty("outputTokens").GetInt64(), tokens.GetProperty("points").EnumerateArray().Sum(p => p.GetProperty("s1").GetDouble()));

        var byModel = (await Get(admin, "/api/analytics/breakdown?by=model")).GetProperty("rows").EnumerateArray().ToArray();
        Assert.Contains(byModel, r => r.GetProperty("key").GetString() == "gpt-5-codex" && r.GetProperty("requests").GetInt64() == 2);
        var byTool = (await Get(admin, "/api/analytics/breakdown?by=tool")).GetProperty("rows").EnumerateArray().Single();
        Assert.Equal(("Bash", 3L), (byTool.GetProperty("key").GetString(), byTool.GetProperty("toolCalls").GetInt64()));
        var byEndpoint = (await Get(admin, "/api/analytics/breakdown?by=endpoint")).GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!, r => r.GetProperty("requests").GetInt64());
        Assert.Equal(1, byEndpoint["count_tokens"]);

        var page = await Get(admin, "/api/analytics/requests?limit=4");
        Assert.Equal(4, page.GetProperty("items").GetArrayLength());
        var next = await Get(admin, $"/api/analytics/requests?limit=4&before={page.GetProperty("nextBefore").GetInt64()}");
        Assert.Equal(2, next.GetProperty("items").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, next.GetProperty("nextBefore").ValueKind);

        var sessions = await Get(admin, "/api/analytics/sessions?provider=anthropic&limit=1");
        Assert.Equal(3, sessions.GetProperty("total").GetInt64());
        Assert.Equal("Work Claude", sessions.GetProperty("items")[0].GetProperty("accountName").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/analytics/timeseries?metric=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/analytics/breakdown?by=bogus")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await gw.CreateClient().GetAsync("/api/analytics/summary")).StatusCode);
    }
}
