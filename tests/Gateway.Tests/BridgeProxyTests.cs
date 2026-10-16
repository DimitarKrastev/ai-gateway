using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Bridge;
using Gateway.Fakes;
using Gateway.Proxy;

namespace Gateway.Tests;

/// <summary>
/// Claude Code → gateway → OpenAI (fake) → Claude Code: Anthropic Messages requests for an exposed OpenAI model are routed to
/// an OpenAI account, translated both ways and recorded in analytics.
/// </summary>
public class BridgeProxyTests
{
    private const string Advertised = "anthropic-openai-gpt-5-codex";
    private const string Session = "6f1c2d3e-4a5b-4c6d-8e9f-0a1b2c3d4e5f";

    private static string CodexAuthJson(FakeAccount a) => JsonSerializer.Serialize(new
    {
        tokens = new { id_token = "unused", access_token = a.AccessToken, refresh_token = a.RefreshToken, account_id = a.ChatGptAccountId },
    });

    private sealed record Setup(FakeProviderServer Fake, ProxyGatewayFactory Gw, HttpClient Admin, HttpClient Client, string Key,
        FakeAccount? Api, FakeAccount? ChatGpt, long? ApiId, long? ChatGptId) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            Admin.Dispose();
            await Gw.DisposeAsync();
            await Fake.DisposeAsync();
        }
    }

    /// <summary>Links the requested OpenAI account kinds, refreshes the catalog and exposes gpt-5-codex.</summary>
    private static async Task<Setup> StartAsync(bool api, bool chatgpt, double tokensPerSecond = 0, int apiPriority = 100, int chatPriority = 100,
        bool apiExhausted = false, bool chatExhausted = false)
    {
        var fake = await FakeProviderServer.StartInMemoryAsync(o =>
        {
            o.AutoRegister = false;
            o.TokensPerSecond = tokensPerSecond;
        });
        var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        FakeAccount? a = null, c = null;
        long? aId = null, cId = null;
        if (api)
        {
            a = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key", Exhausted: apiExhausted));
            aId = await gw.LinkAsync(admin, new { name = "OpenAI key", kind = "openai_api_key", apiKey = a.ApiKey, priority = apiPriority });
        }
        if (chatgpt)
        {
            c = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth", Exhausted: chatExhausted));
            cId = await gw.LinkAsync(admin, new { name = "ChatGPT", credentialsJson = CodexAuthJson(c), priority = chatPriority });
        }
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/bridge/models/refresh", null)).StatusCode);
        var sel = await admin.PutAsJsonAsync("/api/bridge/selection", new { models = new[] { new { modelId = "gpt-5-codex", exposed = true } } });
        Assert.True(sel.IsSuccessStatusCode, await sel.Content.ReadAsStringAsync());
        fake.OpenAI.ClearRequests();
        return new Setup(fake, gw, admin, gw.CreateClient(), key, a, c, aId, cId);
    }

    private static readonly object[] Tools =
    [
        new { name = "Bash", description = "Run a shell command", input_schema = new { type = "object", properties = new { command = new { type = "string" } }, required = new[] { "command" } } },
        new { name = "Task", description = "Launch a subagent", input_schema = new { type = "object", properties = new { prompt = new { type = "string" } } } },
    ];

    private static object Body(object[] messages, bool stream = true, string model = Advertised) => new
    {
        model,
        max_tokens = 32000,
        stream,
        system = new object[]
        {
            new { type = "text", text = "x-anthropic-billing-header: cc_version=2.0.14; cc_entrypoint=cli" },
            new { type = "text", text = "You are Claude Code, Anthropic's official CLI for Claude.", cache_control = new { type = "ephemeral" } },
        },
        messages,
        tools = Tools,
        thinking = new { type = "enabled", budget_tokens = 8000 },
        metadata = new { user_id = $"user_abc_account_def_session_{Session}" },
    };

    private static object[] FirstTurn(string text = "List the files [[tool:Bash]]") =>
        [new { role = "user", content = new object[] { new { type = "text", text } } }];

    private static HttpRequestMessage Request(string key, object body, string path = "/anthropic/v1/messages?beta=true")
    {
        var req = ProxyRequests.Anthropic(key, body, path);
        req.Headers.TryAddWithoutValidation("user-agent", "claude-cli/2.0.14 (external, cli)");
        req.Headers.Add("anthropic-beta", "claude-code-20250219,interleaved-thinking-2025-05-14");
        return req;
    }

    /// <summary>Asserts a well-formed Anthropic stream and returns its content blocks (type, start payload, accumulated deltas).</summary>
    private static (List<(string Type, JsonElement Start, StringBuilder Text, StringBuilder Json, string? Signature)> Blocks, JsonElement Delta, JsonElement Usage)
        AssertValidStream(List<(string? Event, JsonElement Data)> events)
    {
        Assert.NotEmpty(events);
        Assert.Equal("message_start", events[0].Event);
        Assert.Equal(Advertised, events[0].Data.GetProperty("message").GetProperty("model").GetString());
        Assert.Equal("message_stop", events[^1].Event);
        var blocks = new List<(string, JsonElement, StringBuilder, StringBuilder, string?)>();
        int? open = null;
        JsonElement? delta = null, usage = null;
        foreach (var (ev, data) in events.Skip(1).SkipLast(1))
        {
            Assert.Equal(ev, data.GetProperty("type").GetString());
            switch (ev)
            {
                case "ping": break;
                case "content_block_start":
                    Assert.Null(open);
                    var index = data.GetProperty("index").GetInt32();
                    Assert.Equal(blocks.Count, index);
                    var cb = data.GetProperty("content_block");
                    blocks.Add((cb.GetProperty("type").GetString()!, cb.Clone(), new StringBuilder(), new StringBuilder(), null));
                    open = index;
                    break;
                case "content_block_delta":
                    Assert.Equal(open, data.GetProperty("index").GetInt32());
                    var d = data.GetProperty("delta");
                    var b = blocks[open!.Value];
                    switch (d.GetProperty("type").GetString())
                    {
                        case "text_delta": Assert.Equal("text", b.Item1); b.Item3.Append(d.GetProperty("text").GetString()); break;
                        case "thinking_delta": Assert.Equal("thinking", b.Item1); b.Item3.Append(d.GetProperty("thinking").GetString()); break;
                        case "input_json_delta": Assert.Equal("tool_use", b.Item1); b.Item4.Append(d.GetProperty("partial_json").GetString()); break;
                        case "signature_delta": blocks[open.Value] = b with { Item5 = d.GetProperty("signature").GetString() }; break;
                        default: Assert.Fail("unexpected delta " + d); break;
                    }
                    break;
                case "content_block_stop":
                    Assert.Equal(open, data.GetProperty("index").GetInt32());
                    open = null;
                    break;
                case "message_delta":
                    Assert.Null(open);
                    delta = data.GetProperty("delta").Clone();
                    usage = data.GetProperty("usage").Clone();
                    break;
                default:
                    Assert.Fail($"unexpected event {ev}: {data}");
                    break;
            }
        }
        Assert.Null(open);
        Assert.NotNull(delta);
        return (blocks, delta!.Value, usage!.Value);
    }

    private static async Task<List<(string? Event, JsonElement Data)>> StreamAsync(Setup s, object body)
    {
        using var res = await s.Client.SendAsync(Request(s.Key, body));
        var text = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.OK, text);
        Assert.StartsWith("text/event-stream", res.Content.Headers.ContentType?.ToString());
        return ProxyRequests.ParseSse(text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Streaming_tool_call_round_trip_through_openai(bool chatgpt)
    {
        await using var s = await StartAsync(api: !chatgpt, chatgpt: chatgpt);

        // Turn 1: thinking + a Bash tool call, streamed back as Anthropic events.
        var (blocks, delta, usage) = AssertValidStream(await StreamAsync(s, Body(FirstTurn())));
        Assert.Equal(["thinking", "tool_use"], blocks.Select(b => b.Type));
        var thinking = blocks[0];
        Assert.Contains("Thinking about the request", thinking.Text.ToString());
        Assert.StartsWith(ThinkingEnvelope.Prefix, thinking.Signature);
        var tool = blocks[1];
        Assert.Equal("Bash", tool.Start.GetProperty("name").GetString());
        var toolId = tool.Start.GetProperty("id").GetString()!;
        Assert.StartsWith("call_", toolId);
        Assert.Equal("""{"command":"echo fake-0"}""", tool.Json.ToString());
        Assert.Equal("tool_use", delta.GetProperty("stop_reason").GetString());
        Assert.True(usage.GetProperty("output_tokens").GetInt64() > 0);
        Assert.True(usage.GetProperty("input_tokens").GetInt64() > 0);
        Assert.Equal(0, usage.GetProperty("cache_read_input_tokens").GetInt64());

        // What reached the provider: a Responses request with the account's credential, never the client key.
        var first = Assert.Single(s.Fake.OpenAI.Requests);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(chatgpt ? "/backend-api/codex/responses" : "/v1/responses", first.Path);
        Assert.Equal(chatgpt ? s.ChatGpt!.Id : s.Api!.Id, first.AccountId);
        Assert.DoesNotContain(first.Headers.Values, v => v.Contains(s.Key));
        Assert.False(first.Headers.ContainsKey("anthropic-beta"));
        var up = JsonDocument.Parse(first.Body).RootElement;
        Assert.Equal("gpt-5-codex", up.GetProperty("model").GetString());
        Assert.Equal("You are Claude Code, Anthropic's official CLI for Claude.", up.GetProperty("instructions").GetString());
        Assert.False(up.GetProperty("store").GetBoolean());
        Assert.True(up.GetProperty("stream").GetBoolean());
        Assert.Equal("medium", up.GetProperty("reasoning").GetProperty("effort").GetString());
        Assert.Equal(["Bash", "Task"], up.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(chatgpt, !up.TryGetProperty("max_output_tokens", out _));
        if (chatgpt)
        {
            Assert.Equal("codex_cli_rs", first.Headers["originator"]);
            Assert.Equal(Session, first.Headers["session_id"]);
        }

        // Turn 2: Claude Code sends the thinking block (with the envelope) and the tool result back.
        var history = new object[]
        {
            FirstTurn()[0],
            new
            {
                role = "assistant",
                content = new object[]
                {
                    new { type = "thinking", thinking = thinking.Text.ToString(), signature = thinking.Signature },
                    new { type = "tool_use", id = toolId, name = "Bash", input = new { command = "echo fake-0" } },
                },
            },
            new
            {
                role = "user",
                content = new object[]
                {
                    new { type = "tool_result", tool_use_id = toolId, content = "fake-0" },
                    new { type = "text", text = "Thanks, now summarize. [[no-tool]]" },
                },
            },
        };
        var (blocks2, delta2, usage2) = AssertValidStream(await StreamAsync(s, Body(history)));
        Assert.Equal("text", blocks2[^1].Type);
        Assert.Contains("Fake openai response", blocks2[^1].Text.ToString());
        Assert.Equal("end_turn", delta2.GetProperty("stop_reason").GetString());
        Assert.True(usage2.GetProperty("cache_read_input_tokens").GetInt64() > 0, usage2.ToString()); // same prompt_cache_key

        var second = s.Fake.OpenAI.Requests[^1];
        Assert.Equal(200, second.StatusCode); // the fake verified the replayed encrypted reasoning
        var input = JsonDocument.Parse(second.Body).RootElement.GetProperty("input").EnumerateArray().ToArray();
        Assert.Equal(["message", "reasoning", "function_call", "function_call_output", "message"], input.Select(i => i.GetProperty("type").GetString()));
        Assert.Equal(toolId, input[3].GetProperty("call_id").GetString());
        Assert.Equal(first.AccountId, second.AccountId);
    }

    [Fact]
    public async Task Non_streaming_request_on_the_codex_backend_is_aggregated()
    {
        await using var s = await StartAsync(api: false, chatgpt: true);
        using var res = await s.Client.SendAsync(Request(s.Key, Body(FirstTurn(), stream: false)));
        var text = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var msg = JsonDocument.Parse(text).RootElement;
        Assert.Equal("message", msg.GetProperty("type").GetString());
        Assert.Equal(Advertised, msg.GetProperty("model").GetString());
        Assert.Equal("tool_use", msg.GetProperty("stop_reason").GetString());
        var content = msg.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(["thinking", "tool_use"], content.Select(c => c.GetProperty("type").GetString()));
        Assert.Equal("echo fake-0", content[1].GetProperty("input").GetProperty("command").GetString());
        Assert.True(msg.GetProperty("usage").GetProperty("output_tokens").GetInt64() > 0);
        // The Codex backend only streams; the gateway streamed upstream and aggregated.
        Assert.True(JsonDocument.Parse(Assert.Single(s.Fake.OpenAI.Requests).Body).RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Non_streaming_request_on_an_api_key_is_translated_from_json()
    {
        await using var s = await StartAsync(api: true, chatgpt: false);
        using var res = await s.Client.SendAsync(Request(s.Key, Body(FirstTurn("Hello [[no-tool]]"), stream: false)));
        var msg = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("end_turn", msg.GetProperty("stop_reason").GetString());
        Assert.Equal("text", msg.GetProperty("content").EnumerateArray().Last().GetProperty("type").GetString());
        Assert.False(JsonDocument.Parse(Assert.Single(s.Fake.OpenAI.Requests).Body).RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task Rate_limited_account_fails_over_and_all_exhausted_returns_anthropic_error()
    {
        await using (var s = await StartAsync(api: true, chatgpt: true, apiPriority: 1, chatPriority: 2, apiExhausted: true))
        {
            AssertValidStream(await StreamAsync(s, Body(FirstTurn())));
            var reqs = s.Fake.OpenAI.Requests;
            Assert.Equal([429, 200], reqs.Select(r => r.StatusCode));
            Assert.Equal([s.Api!.Id, s.ChatGpt!.Id], reqs.Select(r => r.AccountId));
        }

        await using (var s = await StartAsync(api: true, chatgpt: true, apiExhausted: true, chatExhausted: true))
        {
            using var res = await s.Client.SendAsync(Request(s.Key, Body(FirstTurn())));
            Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
            Assert.NotNull(res.Headers.RetryAfter);
            var err = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("error", err.GetProperty("type").GetString());
            Assert.Equal("rate_limit_error", err.GetProperty("error").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Upstream_errors_map_to_anthropic_errors_before_and_during_the_stream()
    {
        await using var s = await StartAsync(api: true, chatgpt: false);

        using (var res = await s.Client.SendAsync(Request(s.Key, Body(FirstTurn("boom [[error:500]]")))))
        {
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
            var err = await res.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal("api_error", err.GetProperty("error").GetProperty("type").GetString());
        }

        using (var res = await s.Client.SendAsync(Request(s.Key, Body(FirstTurn("go [[no-tool]] [[fail-midstream]]")))))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var events = ProxyRequests.ParseSse(await res.Content.ReadAsStringAsync());
            Assert.Equal("message_start", events[0].Event);
            Assert.Equal("error", events[^1].Event);
            Assert.Equal("api_error", events[^1].Data.GetProperty("error").GetProperty("type").GetString());
            Assert.Contains("Injected mid-stream failure", events[^1].Data.GetProperty("error").GetProperty("message").GetString());
            Assert.DoesNotContain(events, e => e.Event == "message_stop");
        }

        // A request the translator cannot handle is the client's error.
        using (var res = await s.Client.SendAsync(Request(s.Key, new { model = Advertised, max_tokens = 10, messages = "not-an-array" })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            Assert.Equal("invalid_request_error", (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task Other_models_still_go_to_anthropic_unchanged_and_no_openai_account_is_a_clear_error()
    {
        await using var s = await StartAsync(api: true, chatgpt: false);
        var ant = s.Fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        await s.Gw.LinkAsync(s.Admin, new { credentialsJson = ant.ApiKey });

        using (var res = await s.Client.SendAsync(Request(s.Key, Body(FirstTurn("hi [[no-tool]]"), model: "claude-sonnet-4-5-20250929"))))
        {
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            await res.Content.ReadAsStringAsync();
        }
        var r = Assert.Single(s.Fake.Anthropic.Requests, x => x.Path == "/v1/messages");
        Assert.Equal("claude-sonnet-4-5-20250929", JsonDocument.Parse(r.Body).RootElement.GetProperty("model").GetString());
        Assert.Equal("?beta=true", r.Query);
        Assert.Empty(s.Fake.OpenAI.Requests);

        // Disable the only OpenAI account: the bridged model has nowhere to go.
        await s.Admin.PatchAsJsonAsync($"/api/accounts/{s.ApiId}", new { enabled = false });
        using var none = await s.Client.SendAsync(Request(s.Key, Body(FirstTurn())));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, none.StatusCode);
        var err = await none.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("overloaded_error", err.GetProperty("error").GetProperty("type").GetString());
        Assert.Contains("gpt-5-codex", err.GetProperty("error").GetProperty("message").GetString());
    }

    [Fact]
    public async Task Count_tokens_is_estimated_locally_and_logged()
    {
        await using var s = await StartAsync(api: true, chatgpt: false);
        using var res = await s.Client.SendAsync(Request(s.Key, new
        {
            model = Advertised, messages = FirstTurn("Count me please"), tools = Tools,
            system = "You are Claude Code, Anthropic's official CLI for Claude.",
        }, "/anthropic/v1/messages/count_tokens?beta=true"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("input_tokens").GetInt32() > 10);
        Assert.Empty(s.Fake.OpenAI.Requests);

        var row = (await s.Admin.GetFromJsonAsync<JsonElement>("/api/analytics/requests?limit=10")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal("count_tokens", row.GetProperty("endpoint").GetString());
        Assert.Equal(Advertised, row.GetProperty("requestedModel").GetString());
        Assert.Equal("gpt-5-codex", row.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("accountId").ValueKind);
        Assert.Equal(JsonValueKind.Null, row.GetProperty("sessionId").ValueKind);
    }

    [Fact]
    public async Task Sessions_stay_on_one_openai_account_and_analytics_record_the_bridge()
    {
        await using var s = await StartAsync(api: true, chatgpt: true, tokensPerSecond: 4000);

        var (blocks, _, _) = AssertValidStream(await StreamAsync(s, Body(FirstTurn())));
        var toolId = blocks[1].Start.GetProperty("id").GetString()!;
        var turn2 = new object[]
        {
            FirstTurn()[0],
            new { role = "assistant", content = new object[] { new { type = "tool_use", id = toolId, name = "Bash", input = new { command = "ls" } } } },
            new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolId, content = "a.txt" } } },
        };
        AssertValidStream(await StreamAsync(s, Body(turn2)));
        AssertValidStream(await StreamAsync(s, Body(FirstTurn("Another question [[no-tool]]"))));
        Assert.Single(s.Fake.OpenAI.Requests.Select(r => r.AccountId).Distinct());

        var items = (await s.Admin.GetFromJsonAsync<JsonElement>("/api/analytics/requests?limit=10")).GetProperty("items").EnumerateArray().Reverse().ToArray();
        Assert.Equal(3, items.Length);
        foreach (var r in items)
        {
            Assert.Equal("openai", r.GetProperty("provider").GetString());
            Assert.Equal("gpt-5-codex", r.GetProperty("model").GetString());
            Assert.Equal(Advertised, r.GetProperty("requestedModel").GetString());
            Assert.Equal(BridgedRequest.AnthropicToOpenAI, r.GetProperty("bridge").GetString());
            Assert.Equal("claude-code", r.GetProperty("client").GetString());
            Assert.Equal(Session, r.GetProperty("sessionId").GetString());
            Assert.Equal("v1/messages", r.GetProperty("path").GetString());
            Assert.Equal("main", r.GetProperty("agentRole").GetString()); // offered the Task tool
            Assert.True(r.GetProperty("reasoningTokens").GetInt64() > 0);
            Assert.True(r.GetProperty("tokensPerSecond").GetDouble() > 0, r.ToString());
            Assert.True(r.GetProperty("costUsd").GetDouble() > 0);
        }
        // Same conversation → same agent instance; a new first message is another agent (Claude Code heuristics).
        Assert.Equal(items[0].GetProperty("agentId").GetString(), items[1].GetProperty("agentId").GetString());
        Assert.NotEqual(items[0].GetProperty("agentId").GetString(), items[2].GetProperty("agentId").GetString());
        Assert.Equal(1, items[0].GetProperty("toolCalls").GetInt64());
        Assert.Equal("Bash", items[0].GetProperty("tools").EnumerateObject().Single().Name);
        Assert.True(items[1].GetProperty("cacheReadTokens").GetInt64() > 0);

        // Breakdown by the requested (advertised) model and per-agent series cover bridged traffic.
        var range = $"from=0&to={DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeMilliseconds()}";
        var by = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/analytics/breakdown?{range}&by=requestedModel");
        var row = by.GetProperty("rows").EnumerateArray().Single();
        Assert.Equal(Advertised, row.GetProperty("key").GetString());
        Assert.Equal(3, row.GetProperty("requests").GetInt64());
        var filtered = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/analytics/summary?{range}&requestedModel={Advertised}");
        Assert.Equal(3, filtered.GetProperty("requests").GetInt64());
        var series = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/analytics/agent-series?{range}&metric=tokensPerSecond");
        var agents = series.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal([2, 1], agents.Select(a => a.GetProperty("points").GetArrayLength()));
        var sessions = await s.Admin.GetFromJsonAsync<JsonElement>($"/api/analytics/sessions?{range}");
        Assert.Contains(sessions.GetProperty("items").EnumerateArray(), x => x.GetProperty("sessionId").GetString() == Session);
    }
}
