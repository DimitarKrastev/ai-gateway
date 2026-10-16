using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Fakes;
using static Gateway.Tests.ProxyRequests;

namespace Gateway.Tests;

/// <summary>
/// Both providers through one gateway: Claude Code (subscription + API key) and Codex (API key + ChatGPT) traffic with
/// tool round trips; analytics per provider must equal what the fake providers reported to the client.
/// The fuller scenario (maximize mode, resets, UI) runs out of process via scripts/e2e.sh.
/// </summary>
public class EndToEndTests
{
    private sealed record Totals(int Requests, long Input, long Output, long CacheRead, long CacheWrite, int ToolCalls);

    [Fact]
    public async Task Claude_code_and_codex_traffic_is_proxied_and_accounted_per_provider()
    {
        await using var fake = await FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);
        var claude = fake.Anthropic.Register(new FakeAccountSpec(Kind: "oauth"));
        var chatgpt = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth"));
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        await gw.LinkAsync(admin, new
        {
            name = "Claude",
            credentialsJson = JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = claude.AccessToken, refreshToken = claude.RefreshToken, expiresAt = claude.AccessTokenExpiresAt!.Value.ToUnixTimeMilliseconds() } }),
        });
        await gw.LinkAsync(admin, new
        {
            name = "ChatGPT",
            credentialsJson = JsonSerializer.Serialize(new { tokens = new { id_token = "x", access_token = chatgpt.AccessToken, refresh_token = chatgpt.RefreshToken, account_id = chatgpt.ChatGptAccountId } }),
        });
        var client = gw.CreateClient();

        // Claude Code: tool call, then tool result -> text, with prompt caching on the conversation prefix.
        var ccSession = Guid.NewGuid().ToString();
        var claudeTotals = new List<Totals>();
        object CcBody(params object[] messages) => new
        {
            model = "claude-sonnet-4-5-20250929", max_tokens = 4096, stream = true,
            system = new object[] { new { type = "text", text = "You are Claude Code, Anthropic's official CLI for Claude.", cache_control = new { type = "ephemeral" } } },
            tools = new object[] { new { name = "Bash", description = "Run a command", input_schema = new { type = "object" } } },
            messages,
            metadata = new { user_id = $"user_x_account_y_session_{ccSession}" },
        };
        var first = new { role = "user", content = new object[] { new { type = "text", text = "run tests", cache_control = new { type = "ephemeral" } } } };
        // Claude Code's count_tokens carries no session metadata: logged as a request, but must not become a session of its own.
        var count = await client.SendAsync(Anthropic(key, new { model = "claude-sonnet-4-5-20250929", messages = new[] { first } },
            path: "/anthropic/v1/messages/count_tokens?beta=true", bearer: true));
        Assert.Equal(HttpStatusCode.OK, count.StatusCode);
        claudeTotals.Add(new Totals(1, 0, 0, 0, 0, 0));
        var (t1, toolId) = await SendClaude(client, key, CcBody(first));
        claudeTotals.Add(t1);
        Assert.NotNull(toolId);
        var (t2, _) = await SendClaude(client, key, CcBody(first,
            new { role = "assistant", content = new object[] { new { type = "tool_use", id = toolId, name = "Bash", input = new { } } } },
            new { role = "user", content = new object[] { new { type = "tool_result", tool_use_id = toolId, content = "ok" } } }));
        claudeTotals.Add(t2);

        // Codex with a ChatGPT login: function call, then function output -> text.
        var cxSession = Guid.NewGuid().ToString();
        var input = new List<object> { new { type = "message", role = "user", content = new object[] { new { type = "input_text", text = "list files" } } } };
        var codexTotals = new List<Totals>();
        var (c1, output) = await SendCodex(client, key, cxSession, input);
        codexTotals.Add(c1);
        var call = output.First(o => o.GetProperty("type").GetString() == "function_call");
        input.Add(call);
        input.Add(new { type = "function_call_output", call_id = call.GetProperty("call_id").GetString(), output = "README.md" });
        var (c2, _) = await SendCodex(client, key, cxSession, input);
        codexTotals.Add(c2);

        Assert.Contains(fake.OpenAI.Requests, r => r.Path == "/backend-api/codex/responses" && r.AccountId == chatgpt.Id);
        Assert.True(claudeTotals.Sum(t => t.CacheWrite) > 0);
        Assert.True(claudeTotals.Sum(t => t.CacheRead) > 0);

        foreach (var (provider, totals) in new[] { ("anthropic", claudeTotals), ("openai", codexTotals) })
        {
            var s = await admin.GetFromJsonAsync<JsonElement>($"/api/analytics/summary?provider={provider}");
            Assert.Equal(totals.Count, s.GetProperty("requests").GetInt32());
            Assert.Equal(totals.Sum(t => t.Input), s.GetProperty("inputTokens").GetInt64());
            Assert.Equal(totals.Sum(t => t.Output), s.GetProperty("outputTokens").GetInt64());
            Assert.Equal(totals.Sum(t => t.CacheRead), s.GetProperty("cacheReadTokens").GetInt64());
            Assert.Equal(totals.Sum(t => t.CacheWrite), s.GetProperty("cacheWriteTokens").GetInt64());
            Assert.Equal(totals.Sum(t => t.ToolCalls), s.GetProperty("toolCalls").GetInt32());
            Assert.Equal(1, s.GetProperty("sessions").GetInt32());
            Assert.True(s.GetProperty("costUsd").GetDouble() > 0);
        }

        var sessions = (await admin.GetFromJsonAsync<JsonElement>("/api/analytics/sessions")).GetProperty("items").EnumerateArray()
            .ToDictionary(x => x.GetProperty("sessionId").GetString()!, x => x);
        Assert.Equal(2, sessions.Count);
        Assert.Equal("claude-code", sessions[ccSession].GetProperty("client").GetString());
        Assert.Equal("codex", sessions[cxSession].GetProperty("client").GetString());
        Assert.Equal("Claude", sessions[ccSession].GetProperty("accountName").GetString());
        Assert.Equal("ChatGPT", sessions[cxSession].GetProperty("accountName").GetString());
    }

    private static async Task<(Totals, string?)> SendClaude(HttpClient client, string key, object body)
    {
        var req = Anthropic(key, body, path: "/anthropic/v1/messages?beta=true", bearer: true);
        req.Headers.TryAddWithoutValidation("User-Agent", "claude-cli/2.0.14 (external, cli)");
        req.Headers.Add("x-app", "cli");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        JsonElement usage = default;
        string? toolId = null;
        var tools = 0;
        foreach (var (_, data) in ParseSse(await res.Content.ReadAsStringAsync()))
        {
            var type = data.GetProperty("type").GetString();
            if (type == "message_start") usage = data.GetProperty("message").GetProperty("usage");
            else if (type == "message_delta") usage = data.GetProperty("usage");
            else if (type == "content_block_start" && data.GetProperty("content_block").GetProperty("type").GetString() == "tool_use")
            {
                tools++;
                toolId = data.GetProperty("content_block").GetProperty("id").GetString();
            }
        }
        long Get(string name) => usage.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        return (new Totals(1, Get("input_tokens"), Get("output_tokens"), Get("cache_read_input_tokens"), Get("cache_creation_input_tokens"), tools), toolId);
    }

    private static async Task<(Totals, List<JsonElement>)> SendCodex(HttpClient client, string key, string session, List<object> input)
    {
        var req = OpenAI(key, new
        {
            model = "gpt-5-codex", instructions = "You are Codex.", input, stream = true, store = false, prompt_cache_key = session,
            tools = new object[] { new { type = "function", name = "shell", parameters = new { type = "object" } } },
        }, session: session);
        req.Headers.TryAddWithoutValidation("User-Agent", "codex_cli_rs/0.46.0 (Mac OS 15.6.0; arm64)");
        req.Headers.Add("originator", "codex_cli_rs");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var done = ParseSse(await res.Content.ReadAsStringAsync()).Single(e => e.Data.GetProperty("type").GetString() == "response.completed").Data.GetProperty("response");
        var u = done.GetProperty("usage");
        var cached = u.GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt64();
        var output = done.GetProperty("output").EnumerateArray().ToList();
        var calls = output.Count(o => o.GetProperty("type").GetString()!.EndsWith("_call"));
        return (new Totals(1, u.GetProperty("input_tokens").GetInt64() - cached, u.GetProperty("output_tokens").GetInt64(), cached, 0, calls), output);
    }
}
