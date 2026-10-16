using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Gateway.Fakes;
using Gateway.Proxy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

/// <summary>Gateway whose upstreams all point at an in-memory <see cref="FakeProviderServer"/>.</summary>
public sealed class ProxyGatewayFactory(FakeProviderServer fake) : GatewayFactory
{
    public FakeProviderServer Fake { get; } = fake;
    /// <summary>Extra test service registrations, e.g. additional request observers.</summary>
    public Action<IServiceCollection>? ExtraServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        foreach (var name in new[] { "AnthropicApi", "AnthropicAuth", "OpenAIApi", "ChatGptBackend", "OpenAIAuth" })
            builder.UseSetting($"Gateway:Upstreams:{name}", Fake.BaseUrl);
        builder.ConfigureTestServices(s =>
        {
            s.AddHttpClient(ProxyHandler.UpstreamClientName).ConfigurePrimaryHttpMessageHandler(() => Fake.CreateHandler());
            ExtraServices?.Invoke(s);
        });
    }

    public async Task<string> CreateClientKeyAsync(HttpClient admin, string name = "test")
    {
        var res = await admin.PostAsJsonAsync("/api/client-keys", new { name });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("key").GetString()!;
    }

    public async Task<long> LinkAsync(HttpClient admin, object body)
    {
        var res = await admin.PostAsJsonAsync("/api/accounts", body);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();
    }
}

public static class ProxyRequests
{
    public static object AnthropicBody(bool stream, string session = "11111111-2222-3333-4444-555555555555", string text = "hello") => new
    {
        model = "claude-sonnet-4-5-20250929",
        max_tokens = 1024,
        stream,
        system = new object[] { new { type = "text", text = "You are Claude Code, Anthropic's official CLI for Claude." } },
        messages = new object[] { new { role = "user", content = text } },
        metadata = new { user_id = $"user_abc_account_def_session_{session}" },
    };

    public static object ResponsesBody(bool stream = true, string? cacheKey = null, string text = "hello") => new
    {
        model = "gpt-5-codex",
        instructions = "You are Codex.",
        input = new object[] { new { type = "message", role = "user", content = new object[] { new { type = "input_text", text } } } },
        stream,
        store = false,
        prompt_cache_key = cacheKey,
    };

    public static HttpRequestMessage Anthropic(string key, object body, string path = "/anthropic/v1/messages", bool bearer = false)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        if (bearer) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        else req.Headers.Add("x-api-key", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        return req;
    }

    public static HttpRequestMessage OpenAI(string key, object body, string path = "/openai/v1/responses", string? session = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = Json(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (session != null) req.Headers.Add("session_id", session);
        return req;
    }

    public static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    public static List<(string? Event, JsonElement Data)> ParseSse(string body)
    {
        var events = new List<(string?, JsonElement)>();
        foreach (var block in body.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
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
}
