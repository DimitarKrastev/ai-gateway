using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Fakes;
using Gateway.Infrastructure;
using Gateway.Usage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using static Gateway.Tests.ProxyRequests;

namespace Gateway.Tests;

/// <summary>Output throughput (tokens/second), speed modes and their analytics.</summary>
public class ThroughputTests
{
    // ---------------------------------------------------------------- unit: computation and mode detection

    [Fact]
    public void Generation_time_streams_from_first_token_and_falls_back_to_latency_for_json()
    {
        // Streaming: first token at +400 ms, end at +900 ms → 500 ms of generation, regardless of time to first byte.
        Assert.Equal(500, Throughput.GenerationMs(stream: true, startedAt: 0, firstByteAt: 100, firstTokenAt: 400, completedAt: 900));
        // No delta seen (e.g. one-chunk stream): from the first byte.
        Assert.Equal(800, Throughput.GenerationMs(true, 0, 100, null, 900));
        Assert.Null(Throughput.GenerationMs(true, 0, null, null, 900));
        Assert.Null(Throughput.GenerationMs(true, 0, 900, 900, 900));
        // Non-streaming: the whole latency (an approximation).
        Assert.Equal(900, Throughput.GenerationMs(false, 0, 850, null, 900));

        Assert.Equal(200, Throughput.TokensPerSecond(100, 500));
        Assert.Null(Throughput.TokensPerSecond(0, 500));
        Assert.Null(Throughput.TokensPerSecond(100, null));
    }

    [Fact]
    public void Percentile_interpolates_linearly()
    {
        Assert.Null(Throughput.Percentile([], 0.5));
        Assert.Equal(7, Throughput.Percentile([7], 0.95));
        Assert.Equal(25, Throughput.Percentile([40, 10, 30, 20], 0.5));
        Assert.Equal(38.5, Throughput.Percentile([10, 20, 30, 40], 0.95)!.Value, 6);
    }

    [Theory]
    // Anthropic: request `speed`, response usage.speed.
    [InlineData("fast", null, "fast")]
    [InlineData(null, null, "standard")]
    [InlineData("fast", "fast", "fast")]
    [InlineData("fast", "standard", "standard")] // the provider served standard: reported wins
    // OpenAI / Codex: service_tier in request and response.
    [InlineData("priority", "priority", "fast")]
    [InlineData("fast", "priority", "fast")]
    [InlineData("flex", "flex", "flex")]
    [InlineData("auto", "default", "standard")]
    [InlineData(null, "default", "standard")]
    [InlineData("scale", null, "scale")]
    public void Speed_mode_normalizes_requested_and_reported_values(string? requested, string? reported, string expected) =>
        Assert.Equal(expected, SpeedModes.Resolve(requested, reported));

    private static ResponseUsageParser Feed(string endpoint, string contentType, string body)
    {
        var parser = new ResponseUsageParser(endpoint, 200, contentType);
        foreach (var chunk in body.Split("\n\n"))
            parser.Feed(Encoding.UTF8.GetBytes(chunk + "\n\n"));
        return parser;
    }

    [Fact]
    public void Parser_reports_speed_and_first_output_event()
    {
        var head = """
            event: message_start
            data: {"type":"message_start","message":{"model":"claude-opus-4-6","usage":{"input_tokens":5,"output_tokens":1,"speed":"fast"}}}

            event: ping
            data: {"type":"ping"}
            """;
        var parser = Feed(Endpoints.Messages, "text/event-stream", head);
        Assert.False(parser.OutputStarted); // message_start and ping are not output
        parser.Feed("event: content_block_delta\ndata: {\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"hi\"}}\n\n"u8);
        Assert.True(parser.OutputStarted);
        Assert.Equal("fast", parser.Complete().ReportedSpeed);

        var responses = Feed(Endpoints.Responses, "text/event-stream", """
            event: response.created
            data: {"type":"response.created","response":{"model":"gpt-5-codex","service_tier":"auto"}}

            event: response.output_text.delta
            data: {"type":"response.output_text.delta","delta":"x"}

            event: response.completed
            data: {"type":"response.completed","response":{"model":"gpt-5-codex","service_tier":"priority","usage":{"input_tokens":10,"output_tokens":4}}}
            """);
        Assert.True(responses.OutputStarted);
        Assert.Equal("priority", responses.Complete().ReportedSpeed);

        var chat = Feed(Endpoints.Chat, "text/event-stream", """
            data: {"model":"gpt-5","service_tier":"flex","choices":[{"index":0,"delta":{"role":"assistant","content":""}}]}
            """);
        Assert.False(chat.OutputStarted); // the role-only first chunk carries no output
        chat.Feed("data: {\"model\":\"gpt-5\",\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hi\"}}]}\n\n"u8);
        Assert.True(chat.OutputStarted);
        Assert.Equal("flex", chat.Complete().ReportedSpeed);

        var json = new ResponseUsageParser(Endpoints.Messages, 200, "application/json");
        json.Feed("""{"model":"claude-opus-4-6","usage":{"input_tokens":3,"output_tokens":9,"speed":"fast"}}"""u8);
        Assert.Equal("fast", json.Complete().ReportedSpeed);
    }

    // ---------------------------------------------------------------- integration through the proxy and fakes

    private static async Task<JsonElement> Get(HttpClient admin, string url)
    {
        var res = await admin.GetAsync(url);
        Assert.True(res.IsSuccessStatusCode, $"{url}: {(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement[]> WaitForRequests(HttpClient admin, int count)
    {
        for (var i = 0; ; i++)
        {
            var items = (await Get(admin, "/api/analytics/requests?limit=500")).GetProperty("items").EnumerateArray().Reverse().ToArray();
            if (items.Length >= count || i > 100) return items;
            await Task.Delay(50);
        }
    }

    private static object Claude(string text, bool stream, string? speed) => speed == null
        ? AnthropicBody(stream, text: text)
        : new
        {
            model = "claude-sonnet-4-5-20250929", max_tokens = 1024, stream, speed,
            messages = new object[] { new { role = "user", content = text } },
            metadata = new { user_id = "user_abc_account_def_session_11111111-2222-3333-4444-555555555555" },
        };

    private static double Num(JsonElement e, string name) => e.GetProperty(name).GetDouble();
    private static string? Str(JsonElement e, string name) => e.GetProperty(name).GetString();

    [Fact]
    public async Task Measures_anthropic_throughput_and_fast_mode_for_streaming_and_json()
    {
        // 200 tok/s standard, fast mode 2.5× → 500 tok/s; 250 ms before the first token (excluded for streams).
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => { o.AutoRegister = false; o.FastSpeedFactor = 2.5; });
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new { name = "k", apiKey = acct.ApiKey });
        var client = gw.CreateClient();

        const string prompt = "[[no-tool]] [[out:120]] [[tps:200]] [[ttft:250]] write";
        foreach (var (stream, speed) in new[] { (true, (string?)null), (true, "fast"), (false, null), (false, "fast") })
        {
            var res = await client.SendAsync(Anthropic(key, Claude(prompt, stream, speed)));
            var body = await res.Content.ReadAsStringAsync();
            Assert.True(res.IsSuccessStatusCode, body);
            if (speed != null && !stream) Assert.Equal("fast", JsonDocument.Parse(body).RootElement.GetProperty("usage").GetProperty("speed").GetString());
        }
        var rows = await WaitForRequests(admin, 4);
        var (std, fast, stdJson, fastJson) = (rows[0], rows[1], rows[2], rows[3]);

        Assert.Equal((null, null, "standard"), (Str(std, "requestedSpeed"), Str(std, "reportedSpeed"), Str(std, "speedMode")));
        Assert.Equal(("fast", "fast", "fast"), (Str(fast, "requestedSpeed"), Str(fast, "reportedSpeed"), Str(fast, "speedMode")));
        Assert.Equal("fast", Str(fastJson, "speedMode"));

        // Streams: generation starts at the first token, after the 250 ms wait, and matches the simulated speed.
        foreach (var (row, tps) in new[] { (std, 200.0), (fast, 500.0) })
        {
            var output = Num(row, "outputTokens");
            Assert.True(output >= 120, row.ToString());
            Assert.True(Num(row, "firstTokenMs") >= 240, row.ToString());
            Assert.True(Num(row, "generationMs") < Num(row, "latencyMs") - 200, row.ToString());
            Assert.InRange(Num(row, "tokensPerSecond"), tps * 0.7, tps * 1.15);
            Assert.Equal(output * 1000 / Num(row, "generationMs"), Num(row, "tokensPerSecond"), 0.1);
        }
        // JSON: no generation phase is visible, so the whole latency is used and the speed reads lower (approximation).
        foreach (var (row, tps) in new[] { (stdJson, 200.0), (fastJson, 500.0) })
        {
            Assert.Equal(Num(row, "latencyMs"), Num(row, "generationMs"));
            Assert.True(Num(row, "tokensPerSecond") < tps, row.ToString());
            Assert.Equal(JsonValueKind.Null, row.GetProperty("firstTokenMs").ValueKind);
        }
        Assert.True(Num(fast, "tokensPerSecond") > 1.8 * Num(std, "tokensPerSecond"));

        var modes = (await Get(admin, "/api/analytics/breakdown?by=modelMode")).GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!);
        Assert.Equal(["claude-sonnet-4-5-20250929|fast", "claude-sonnet-4-5-20250929|standard"], modes.Keys.Order().ToArray());
        Assert.Equal("claude-sonnet-4-5-20250929 · fast", Str(modes["claude-sonnet-4-5-20250929|fast"], "label"));
        Assert.True(Num(modes["claude-sonnet-4-5-20250929|fast"], "tokensPerSecond") > Num(modes["claude-sonnet-4-5-20250929|standard"], "tokensPerSecond"));
    }

    [Fact]
    public async Task Detects_codex_service_tier_modes()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var acct = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new { name = "o", apiKey = acct.ApiKey });
        var client = gw.CreateClient();

        foreach (var tier in new[] { null, "fast", "priority", "flex" })
        {
            var body = new Dictionary<string, object?>
            {
                ["model"] = "gpt-5-codex", ["instructions"] = "You are Codex.", ["stream"] = true, ["store"] = false,
                ["input"] = new object[] { new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "[[no-tool]] [[out:40]] hi" } } } },
            };
            if (tier != null) body["service_tier"] = tier;
            var res = await client.SendAsync(OpenAI(key, body, session: "codex-tiers"));
            Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        }
        var rows = await WaitForRequests(admin, 4);
        var seen = rows.Select(r => (Str(r, "requestedSpeed"), Str(r, "reportedSpeed"), Str(r, "speedMode"))).ToArray();
        Assert.Equal([(null, "default", "standard"), ("fast", "priority", "fast"), ("priority", "priority", "fast"), ("flex", "flex", "flex")], seen);

        var bySpeed = (await Get(admin, "/api/analytics/breakdown?by=speedMode")).GetProperty("rows").EnumerateArray()
            .Select(r => $"{r.GetProperty("key").GetString()}={r.GetProperty("requests").GetInt64()}").Order().ToArray();
        Assert.Equal(["fast=2", "flex=1", "standard=1"], bySpeed);
        Assert.Equal(2, (await Get(admin, "/api/analytics/summary?speedMode=fast")).GetProperty("requests").GetInt64());
    }

    // ---------------------------------------------------------------- aggregation over recorded rows

    private static RequestRecord Row(long at, string model, string? mode, long output, long? generationMs, bool stream = true,
        int status = 200, string? agent = null, string? role = null, long context = 0, string session = "s1") => new()
    {
        RequestId = Guid.NewGuid().ToString("N"), Attempt = 1, Provider = "anthropic", AccountId = 1, SessionId = session,
        Client = "claude-code", AgentId = agent, AgentRole = role, Method = "POST", Path = "v1/messages", Endpoint = Endpoints.Messages,
        Model = model, Stream = stream, Status = status, OutputTokens = output, ContextTokens = context, StartedAt = at,
        CompletedAt = at + (generationMs ?? 0) + 100, GenerationMs = generationMs, SpeedMode = mode,
        ErrorType = status >= 400 ? "http_" + status : null,
    };

    [Fact]
    public async Task Aggregates_tokens_per_second_by_mode_with_weighted_mean_and_percentiles()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var store = gw.Services.GetRequiredService<UsageStore>();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() / 60_000 * 60_000;
        const string opus = "claude-opus-4-6";

        // standard: 100 tok in 1 s (100 tok/s) and 300 tok in 1.5 s (200 tok/s) → weighted (100·100 + 300·200) / 400 = 175
        store.Insert(Row(t0, opus, "standard", 100, 1000));
        store.Insert(Row(t0 + 1000, opus, "standard", 300, 1500));
        // fast: 200 tok/s, 400 tok/s, 600 tok/s with 100 tokens each → mean 400, p50 400, p95 578.9
        store.Insert(Row(t0 + 2000, opus, "fast", 100, 500));
        store.Insert(Row(t0 + 3000, opus, "fast", 100, 250));
        store.Insert(Row(t0 + 4000, opus, "fast", 100, 167));
        // Excluded from throughput: an error, a row without generation time, a row without output.
        store.Insert(Row(t0 + 5000, opus, "fast", 50, 10, status: 529));
        store.Insert(Row(t0 + 5000, opus, "fast", 0, 300));
        store.Insert(Row(t0 + 5000, opus, "standard", 80, null));
        // A row recorded before the migration: no speed mode, no generation time.
        using (var conn = gw.Services.GetRequiredService<Database>().Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO requests (request_id, attempt, provider, client, method, path, endpoint, model, status, output_tokens, started_at, completed_at)
                VALUES ('old', 1, 'anthropic', 'claude-code', 'POST', 'v1/messages', 'messages', 'claude-opus-4-6', 200, 500, $t, $t + 2000);
                """;
            cmd.Parameters.AddWithValue("$t", t0 + 6000);
            cmd.ExecuteNonQuery();
        }
        var range = $"from={t0}&to={t0 + 3_600_000}";

        var rows = (await Get(admin, $"/api/analytics/breakdown?{range}&by=speedMode")).GetProperty("rows").EnumerateArray()
            .ToDictionary(r => r.GetProperty("key").GetString()!);
        Assert.Equal(175, Num(rows["standard"], "tokensPerSecond"), 0.5);
        Assert.Equal(2, rows["standard"].GetProperty("throughputRequests").GetInt64());
        Assert.Equal(400, Num(rows["fast"], "tokensPerSecond"), 1.0);
        Assert.Equal(400, Num(rows["fast"], "tokensPerSecondP50"), 0.5);
        Assert.Equal(578.9, Num(rows["fast"], "tokensPerSecondP95"), 1);
        Assert.Equal(3, rows["fast"].GetProperty("throughputRequests").GetInt64());
        Assert.Equal(5, rows["fast"].GetProperty("requests").GetInt64());
        // Old rows still count everywhere else and read as mode 'unknown' without a throughput.
        Assert.Equal(1, rows["unknown"].GetProperty("requests").GetInt64());
        Assert.Equal(500, rows["unknown"].GetProperty("outputTokens").GetInt64());
        Assert.Equal(JsonValueKind.Null, rows["unknown"].GetProperty("tokensPerSecond").ValueKind);

        var summary = await Get(admin, $"/api/analytics/summary?{range}");
        Assert.Equal((100 * 100 + 300 * 200 + 100 * 200 + 100 * 400 + 100 * 598.8) / 700, Num(summary, "tokensPerSecond"), 0.5);
        Assert.Equal(9, summary.GetProperty("requests").GetInt64());

        // Timeseries: weighted mean per mode, percentiles per bucket; empty buckets have no value.
        var ts = await Get(admin, $"/api/analytics/timeseries?{range}&metric=tokensPerSecond&groupBy=speedMode&bucket=minute");
        var series = ts.GetProperty("series").EnumerateArray().ToDictionary(s => s.GetProperty("id").GetString()!);
        Assert.Equal(175, Num(series["standard"], "total"), 0.5);
        var first = ts.GetProperty("points")[0];
        Assert.Equal(400, first.GetProperty(series["fast"].GetProperty("key").GetString()!).GetDouble(), 1.0);
        Assert.False(ts.GetProperty("points")[5].TryGetProperty(series["fast"].GetProperty("key").GetString()!, out _));
        Assert.False(series.ContainsKey("unknown") && series["unknown"].GetProperty("total").GetDouble() > 0);

        var p95 = await Get(admin, $"/api/analytics/timeseries?{range}&metric=tokensPerSecondP95&groupBy=modelMode&bucket=minute");
        var fastSeries = p95.GetProperty("series").EnumerateArray().Single(s => s.GetProperty("id").GetString() == $"{opus}|fast");
        Assert.Equal(578.9, Num(fastSeries, "total"), 1);
        Assert.Equal(578.9, p95.GetProperty("points")[0].GetProperty(fastSeries.GetProperty("key").GetString()!).GetDouble(), 1);

        var p50 = await Get(admin, $"/api/analytics/timeseries?{range}&metric=tokensPerSecondP50&bucket=hour");
        Assert.Equal(200, Num(p50.GetProperty("series")[0], "total"), 0.5); // over 100, 200, 200, 400, 598.8
    }

    // ---------------------------------------------------------------- per-agent series

    [Fact]
    public async Task Agent_series_plot_each_agent_instance_from_its_first_request()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync();
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var store = gw.Services.GetRequiredService<UsageStore>();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds();
        const string main = "anthropic:sess1234abcd:aaaaaaaaaaaa", sub1 = "anthropic:sess1234abcd:bbbbbbbbbbbb", sub2 = "anthropic:sess1234abcd:cccccccccccc";

        store.Insert(Row(t0, "m", "standard", 10, 100, agent: main, role: "main", context: 1000, session: "sess1234abcd"));
        store.Insert(Row(t0 + 1000, "m", "standard", 10, 100, agent: sub1, role: "subagent", context: 300, session: "sess1234abcd"));
        store.Insert(Row(t0 + 1100, "m", "standard", 10, 100, agent: sub2, role: "subagent", context: 350, session: "sess1234abcd"));
        store.Insert(Row(t0 + 2000, "m", "standard", 10, 100, agent: sub1, role: "subagent", context: 900, session: "sess1234abcd"));
        store.Insert(Row(t0 + 2100, "m", "standard", 10, 100, agent: sub2, role: "subagent", context: 700, session: "sess1234abcd"));
        store.Insert(Row(t0 + 3000, "m", "standard", 10, 100, agent: main, role: "main", context: 2500, session: "sess1234abcd"));
        store.Insert(Row(t0 + 3500, "m", "standard", 10, 100, agent: null, context: 50, session: "sess1234abcd")); // no agent (count_tokens-like)
        var range = $"from={t0 - 1000}&to={t0 + 3_600_000}";

        var res = await Get(admin, $"/api/analytics/agent-series?{range}&metric=contextMax");
        Assert.Equal(3, res.GetProperty("totalAgents").GetInt32());
        Assert.Equal(0, res.GetProperty("hidden").GetInt32());
        var agents = res.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal([main, sub1, sub2], agents.Select(a => a.GetProperty("id").GetString()!).ToArray()); // by first appearance
        Assert.Equal(["sess1234 · main · aaaaaa", "sess1234 · subagent · bbbbbb", "sess1234 · subagent · cccccc"],
            agents.Select(a => a.GetProperty("label").GetString()!).ToArray());
        Assert.Equal(t0 + 1000, agents[1].GetProperty("firstSeen").GetInt64());
        double[][] Points(JsonElement a) => a.GetProperty("points").EnumerateArray().Select(p => p.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray();
        Assert.Equal([[t0, 1000], [t0 + 3000, 2500]], Points(agents[0]));
        Assert.Equal([[t0 + 1000, 300], [t0 + 2000, 900]], Points(agents[1]));

        // Sum metrics can be running totals; request counts always are.
        var cumulative = await Get(admin, $"/api/analytics/agent-series?{range}&metric=outputTokens&cumulative=true");
        Assert.True(cumulative.GetProperty("cumulative").GetBoolean());
        Assert.Equal([[t0, 10], [t0 + 3000, 20]], Points(cumulative.GetProperty("agents")[0]));
        var requests = await Get(admin, $"/api/analytics/agent-series?{range}&metric=requests");
        Assert.Equal([[t0 + 1100, 1], [t0 + 2100, 2]], Points(requests.GetProperty("agents")[2]));
        // Averages are never accumulated.
        Assert.False((await Get(admin, $"/api/analytics/agent-series?{range}&metric=latencyAvg&cumulative=true")).GetProperty("cumulative").GetBoolean());

        // Top-N by request count; the rest are reported as hidden. Filters apply (e.g. one session, one mode).
        var top = await Get(admin, $"/api/analytics/agent-series?{range}&metric=contextMax&limit=2");
        Assert.Equal(1, top.GetProperty("hidden").GetInt32());
        Assert.Equal(2, top.GetProperty("agents").GetArrayLength());
        Assert.Equal(0, (await Get(admin, $"/api/analytics/agent-series?{range}&metric=contextMax&sessionId=other")).GetProperty("agents").GetArrayLength());

        // Throughput per agent, per request.
        var tps = await Get(admin, $"/api/analytics/agent-series?{range}&metric=tokensPerSecond");
        Assert.Equal(100, Points(tps.GetProperty("agents")[0])[0][1]);

        // Metrics without a per-request value are served by /timeseries?groupBy=agentId instead.
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await admin.GetAsync($"/api/analytics/agent-series?{range}&metric=concurrentAgents")).StatusCode);
        var byAgent = await Get(admin, $"/api/analytics/timeseries?{range}&metric=concurrentAgents&groupBy=agentId");
        Assert.Contains(byAgent.GetProperty("series").EnumerateArray(), s => s.GetProperty("label").GetString() == "sess1234 · subagent · bbbbbb");
    }

    [Fact]
    public async Task Parallel_claude_subagents_get_their_own_series_and_explicit_agent_ids_split_instances()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var acct = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new { name = "k", apiKey = acct.ApiKey });
        var client = gw.CreateClient();
        var session = Guid.NewGuid().ToString();

        object Body(string system, string firstUser, int turns, string[] tools) => new
        {
            model = "claude-sonnet-4-5-20250929", max_tokens = 1024, stream = true,
            system = new object[] { new { type = "text", text = system } },
            messages = Enumerable.Range(0, turns).SelectMany(i => new object[]
            {
                new { role = "user", content = i == 0 ? firstUser : $"[[no-tool]] step {i} " + new string('x', 400 * i) },
                new { role = "assistant", content = "ok" },
            }).SkipLast(1).ToArray(),
            tools = tools.Select(t => new { name = t, description = t, input_schema = new { type = "object" } }).ToArray(),
            metadata = new { user_id = $"user_abc_account_def_session_{session}" },
        };
        async Task Send(object body, string? agentHeader = null)
        {
            var req = Anthropic(key, body);
            req.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.0.14 (external, cli)");
            if (agentHeader != null) req.Headers.Add("x-claude-code-agent-id", agentHeader);
            Assert.True((await client.SendAsync(req)).IsSuccessStatusCode);
        }

        await Send(Body("You are Claude Code.", "[[no-tool]] Refactor", 1, ["Task", "Bash"]));
        // Two subagents in parallel, each growing its own context over three requests.
        await Task.WhenAll(new[] { "auth", "billing" }.Select(async m =>
        {
            for (var turn = 1; turn <= 3; turn++) await Send(Body("You are a subagent.", $"[[no-tool]] Explore {m}", turn, ["Read"]));
        }));
        // Identical prompts from two instances are told apart by an explicit agent id header.
        await Send(Body("You are a subagent.", "[[no-tool]] Same task", 1, ["Read"]), "agent-one");
        await Send(Body("You are a subagent.", "[[no-tool]] Same task", 1, ["Read"]), "agent-two");
        await WaitForRequests(admin, 9);

        var res = await Get(admin, $"/api/analytics/agent-series?metric=contextMax&sessionId={session}");
        var agents = res.GetProperty("agents").EnumerateArray().ToArray();
        Assert.Equal(5, agents.Length);
        Assert.Equal(["main", "subagent", "subagent", "subagent", "subagent"], agents.Select(a => a.GetProperty("role").GetString()!).ToArray());
        foreach (var sub in agents.Skip(1).Take(2))
        {
            var values = sub.GetProperty("points").EnumerateArray().Select(p => p[1].GetDouble()).ToArray();
            Assert.Equal(3, values.Length);
            Assert.True(values[0] < values[1] && values[1] < values[2], string.Join(",", values));
            Assert.True(sub.GetProperty("firstSeen").GetInt64() >= agents[0].GetProperty("firstSeen").GetInt64());
        }
        Assert.All(agents, a => Assert.StartsWith(session[..8] + " · ", a.GetProperty("label").GetString()));
    }

    [Fact]
    public void Migration_adds_throughput_columns_to_an_existing_database()
    {
        var dir = Path.Combine(Path.GetTempPath(), "aigw-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var db = new Database(new GatewayOptions { DataDir = dir });
            // Build a version-4 database with one row, as shipped before this change.
            using (var conn = db.Open())
            {
                void Exec(string sql) { using var c = conn.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
                Exec("CREATE TABLE schema_version (version INTEGER NOT NULL);");
                foreach (var (version, sql) in Database.Migrations.Where(m => m.Version <= 4))
                {
                    Exec(sql);
                    Exec($"INSERT INTO schema_version (version) VALUES ({version});");
                }
                Exec("""
                    INSERT INTO requests (request_id, attempt, provider, client, method, path, endpoint, started_at, completed_at)
                    VALUES ('old', 1, 'anthropic', 'claude-code', 'POST', 'v1/messages', 'messages', 1, 2);
                    """);
            }
            db.Migrate();
            using var check = db.Open();
            using var cmd = check.CreateCommand();
            cmd.CommandText = "SELECT MAX(version) FROM schema_version; SELECT generation_ms, speed_mode, " + SpeedModes.Sql() + " FROM requests;";
            using var r = cmd.ExecuteReader();
            Assert.True(r.Read());
            Assert.True(r.GetInt32(0) >= 5);
            Assert.True(r.NextResult() && r.Read());
            Assert.True(r.IsDBNull(0) && r.IsDBNull(1));
            Assert.Equal("unknown", r.GetString(2));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
