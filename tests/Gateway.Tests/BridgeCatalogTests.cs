using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.Bridge;
using Gateway.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Gateway.Tests;

public class BridgeCatalogTests
{
    private static readonly Regex DiscoveryFilter = new("(claude|anthropic)", RegexOptions.IgnoreCase);

    private static Task<FakeProviderServer> StartFake() => FakeProviderServer.StartInMemoryAsync(o => o.AutoRegister = false);

    private static string CodexAuthJson(FakeAccount a) => JsonSerializer.Serialize(new
    {
        OPENAI_API_KEY = (string?)null,
        tokens = new { id_token = "unused", access_token = a.AccessToken, refresh_token = a.RefreshToken, account_id = a.ChatGptAccountId },
    });

    private sealed record Setup(ProxyGatewayFactory Gw, HttpClient Admin, string Key, FakeAccount ApiAccount, FakeAccount ChatGpt);

    private static async Task<Setup> LinkOpenAIAsync(FakeProviderServer fake, ProxyGatewayFactory? gw = null)
    {
        gw ??= new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var key = await gw.CreateClientKeyAsync(admin);
        var api = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        var chat = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth"));
        await gw.LinkAsync(admin, new { name = "OpenAI key", kind = "openai_api_key", apiKey = api.ApiKey });
        await gw.LinkAsync(admin, new { name = "ChatGPT", credentialsJson = CodexAuthJson(chat) });
        return new Setup(gw, admin, key, api, chat);
    }

    private static async Task<JsonElement> RefreshAsync(HttpClient admin)
    {
        var res = await admin.PostAsync("/api/bridge/models/refresh", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static JsonElement Model(JsonElement view, string id) =>
        view.GetProperty("models").EnumerateArray().Single(m => m.GetProperty("modelId").GetString() == id);

    private static HttpRequestMessage ModelsRequest(string? key, string path = "/anthropic/v1/models?limit=1000")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (key != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        req.Headers.Add("anthropic-version", "2023-06-01");
        return req;
    }

    private static async Task ExposeAsync(HttpClient admin, params object[] models)
    {
        var res = await admin.PutAsJsonAsync("/api/bridge/selection", new { models });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Fake_lists_api_and_codex_models_with_credentials()
    {
        await using var fake = await StartFake();
        var api = fake.OpenAI.Register(new FakeAccountSpec(Kind: "api_key"));
        var chat = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth"));
        using var http = fake.CreateClient();

        var noAuth = await http.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, noAuth.StatusCode);

        var req = new HttpRequestMessage(HttpMethod.Get, "/v1/models");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.ApiKey);
        var list = await (await http.SendAsync(req)).Content.ReadFromJsonAsync<JsonElement>();
        var ids = list.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        Assert.Contains("gpt-5-codex", ids);
        Assert.Contains("text-embedding-3-small", ids);

        HttpRequestMessage Codex(string query)
        {
            var r = new HttpRequestMessage(HttpMethod.Get, "/backend-api/codex/models" + query);
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", chat.AccessToken);
            r.Headers.Add("chatgpt-account-id", chat.ChatGptAccountId);
            return r;
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await http.SendAsync(Codex(""))).StatusCode);
        var codex = await (await http.SendAsync(Codex("?client_version=0.80.0"))).Content.ReadFromJsonAsync<JsonElement>();
        var first = codex.GetProperty("models")[0];
        Assert.Equal("gpt-5-codex", first.GetProperty("slug").GetString());
        Assert.Equal("medium", first.GetProperty("default_reasoning_level").GetString());
        Assert.Equal("low", first.GetProperty("supported_reasoning_levels")[0].GetProperty("effort").GetString());
        Assert.Equal("list", first.GetProperty("visibility").GetString());
    }

    [Fact]
    public async Task Refresh_merges_account_lists_and_filters_non_chat_models()
    {
        await using var fake = await StartFake();
        var s = await LinkOpenAIAsync(fake);
        using var _ = s.Gw;

        var empty = await (await s.Admin.GetAsync("/api/bridge/models")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Empty(empty.GetProperty("models").EnumerateArray());
        Assert.Equal(2, empty.GetProperty("openaiAccounts").GetArrayLength());

        var view = await RefreshAsync(s.Admin);
        var results = view.GetProperty("refreshResults").EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.GetProperty("ok").GetBoolean(), r.ToString()));

        var ids = view.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("modelId").GetString()!).ToList();
        foreach (var excluded in new[] { "text-embedding-3-small", "tts-1", "whisper-1", "dall-e-3", "gpt-4o-realtime-preview", "gpt-4o-mini-tts",
                     "gpt-4o-transcribe", "gpt-image-1", "omni-moderation-latest", "gpt-4o-search-preview", "davinci-002", "codex-auto-review" })
            Assert.DoesNotContain(excluded, ids);
        Assert.Contains("gpt-5-mini", ids);
        Assert.Contains("o4-mini", ids);

        var gpt5 = Model(view, "gpt-5");
        Assert.Equal(["api", "chatgpt"], gpt5.GetProperty("sources").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(2, gpt5.GetProperty("accounts").GetArrayLength());
        Assert.Equal(["minimal", "low", "medium", "high"], gpt5.GetProperty("efforts").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("anthropic-openai-gpt-5", gpt5.GetProperty("advertisedId").GetString());
        Assert.False(gpt5.GetProperty("exposed").GetBoolean());

        var max = Model(view, "gpt-5.1-codex-max");
        Assert.Equal(["chatgpt"], max.GetProperty("sources").EnumerateArray().Select(x => x.GetString()));
        Assert.Contains("xhigh", max.GetProperty("efforts").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("anthropic-openai-gpt-5.1-codex-max", max.GetProperty("advertisedId").GetString());
        Assert.Equal(["api"], Model(view, "gpt-5-mini").GetProperty("sources").EnumerateArray().Select(x => x.GetString()));

        // The gateway listed with each account's own credential, Codex-style for the subscription.
        var codexCall = fake.OpenAI.Requests.Single(r => r.Path == "/backend-api/codex/models");
        Assert.Contains("client_version=", codexCall.Query);
        Assert.Equal("Bearer " + s.ChatGpt.AccessToken, codexCall.Headers["authorization"]);
        Assert.Equal(s.ChatGpt.ChatGptAccountId, codexCall.Headers["chatgpt-account-id"]);
        var apiCall = fake.OpenAI.Requests.Single(r => r.Path == "/v1/models");
        Assert.Equal("Bearer " + s.ApiAccount.ApiKey, apiCall.Headers["authorization"]);
    }

    [Fact]
    public async Task Refresh_reports_per_account_errors_and_keeps_other_accounts()
    {
        await using var fake = await StartFake();
        using var gw = new ProxyGatewayFactory(fake);
        var admin = await gw.CreateLoggedInClientAsync();
        var chat = fake.OpenAI.Register(new FakeAccountSpec(Kind: "oauth"));
        await gw.LinkAsync(admin, new { name = "bad key", kind = "openai_api_key", apiKey = "sk-proj-not-known-to-the-fake-000000" });
        await gw.LinkAsync(admin, new { name = "ChatGPT", credentialsJson = CodexAuthJson(chat) });

        var view = await RefreshAsync(admin);
        var results = view.GetProperty("refreshResults").EnumerateArray().ToDictionary(r => r.GetProperty("accountName").GetString()!);
        Assert.False(results["bad key"].GetProperty("ok").GetBoolean());
        Assert.Contains("401", results["bad key"].GetProperty("error").GetString());
        Assert.True(results["ChatGPT"].GetProperty("ok").GetBoolean());
        Assert.Equal(3, results["ChatGPT"].GetProperty("models").GetInt32());
        Assert.Equal(3, view.GetProperty("models").GetArrayLength());
    }

    [Fact]
    public async Task Manual_models_can_be_added_and_removed_but_listed_models_cannot_be_removed()
    {
        await using var fake = await StartFake();
        var s = await LinkOpenAIAsync(fake);
        using var _ = s.Gw;
        await RefreshAsync(s.Admin);

        var bad = await s.Admin.PostAsJsonAsync("/api/bridge/models", new { modelId = "gpt 5 with spaces" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var created = await s.Admin.PostAsJsonAsync("/api/bridge/models", new { modelId = "gpt-6-preview", displayName = "GPT-6 preview", efforts = new[] { "low", "high" } });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var entry = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(entry.GetProperty("manual").GetBoolean());
        Assert.True(entry.GetProperty("available").GetBoolean());
        Assert.Equal(["manual"], entry.GetProperty("sources").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal("GPT-6 preview (OpenAI)", entry.GetProperty("advertisedName").GetString());

        // Manual entries survive a refresh that doesn't list them.
        var view = await RefreshAsync(s.Admin);
        Assert.Equal(["low", "high"], Model(view, "gpt-6-preview").GetProperty("efforts").EnumerateArray().Select(x => x.GetString()));

        Assert.Equal(HttpStatusCode.Conflict, (await s.Admin.DeleteAsync("/api/bridge/models/gpt-5")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Admin.DeleteAsync("/api/bridge/models/gpt-6-preview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await s.Admin.DeleteAsync("/api/bridge/models/gpt-6-preview")).StatusCode);
    }

    [Fact]
    public async Task Selection_is_validated_and_nothing_is_saved_on_error()
    {
        await using var fake = await StartFake();
        var s = await LinkOpenAIAsync(fake);
        using var _ = s.Gw;
        await RefreshAsync(s.Admin);

        async Task<JsonElement> Rejected(params object[] models)
        {
            var res = await s.Admin.PutAsJsonAsync("/api/bridge/selection", new { models });
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
            return await res.Content.ReadFromJsonAsync<JsonElement>();
        }

        var noMarker = await Rejected(new { modelId = "gpt-5", exposed = true, advertisedId = "gpt-5" });
        Assert.Contains("claude", noMarker.GetProperty("errors")[0].GetProperty("message").GetString());
        Assert.Equal("advertisedId", noMarker.GetProperty("errors")[0].GetProperty("field").GetString());
        await Rejected(new { modelId = "gpt-5", exposed = true, advertisedId = "anthropic gpt 5" });
        var dup = await Rejected(
            new { modelId = "gpt-5", exposed = true, advertisedId = "claude-gpt" },
            new { modelId = "gpt-5-mini", exposed = true, advertisedId = "CLAUDE-GPT" });
        Assert.Equal(2, dup.GetProperty("errors").GetArrayLength());
        await Rejected(new { modelId = "no-such-model", exposed = true });

        var view = await (await s.Admin.GetAsync("/api/bridge/models")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.DoesNotContain(view.GetProperty("models").EnumerateArray(), m => m.GetProperty("exposed").GetBoolean());

        // An invalid id on a model that is not exposed is not an error (it is never advertised).
        await ExposeAsync(s.Admin, new { modelId = "gpt-5", exposed = false, advertisedId = "not-advertised" });
    }

    [Fact]
    public async Task Selection_persists_across_restart_and_resolves_advertised_ids()
    {
        await using var fake = await StartFake();
        var dataDir = Path.Combine(Path.GetTempPath(), "aigw-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using (var first = new ProxyGatewayFactory(fake) { DataDir = dataDir, KeepDataDir = true })
            {
                var s = await LinkOpenAIAsync(fake, first);
                await RefreshAsync(s.Admin);
                await ExposeAsync(s.Admin,
                    new { modelId = "gpt-5-codex", exposed = true },
                    new { modelId = "gpt-5", exposed = true, advertisedId = "claude-via-openai-gpt-5", displayName = "GPT-5 (ChatGPT)", description = "Big one" });
            }

            using var second = new ProxyGatewayFactory(fake) { DataDir = dataDir };
            var admin = await second.CreateLoggedInClientAsync();
            var view = await (await admin.GetAsync("/api/bridge/models")).Content.ReadFromJsonAsync<JsonElement>();
            var gpt5 = Model(view, "gpt-5");
            Assert.True(gpt5.GetProperty("exposed").GetBoolean());
            Assert.Equal("claude-via-openai-gpt-5", gpt5.GetProperty("advertisedId").GetString());
            Assert.Equal("GPT-5 (ChatGPT)", gpt5.GetProperty("advertisedName").GetString());
            Assert.Equal("Big one", gpt5.GetProperty("advertisedDescription").GetString());
            Assert.True(Model(view, "gpt-5-codex").GetProperty("exposed").GetBoolean());
            Assert.Equal(2, gpt5.GetProperty("accounts").GetArrayLength());

            var catalog = second.Services.GetRequiredService<BridgeModelCatalog>();
            Assert.Equal(["anthropic-openai-gpt-5-codex", "claude-via-openai-gpt-5"], catalog.ListExposed().Select(m => m.AdvertisedId));
            var resolved = catalog.ResolveExposed("Claude-Via-OpenAI-GPT-5[1m]");
            Assert.NotNull(resolved);
            Assert.Equal("gpt-5", resolved.UpstreamModel);
            Assert.Equal(2, resolved.AccountIds.Count);
            Assert.Equal(["minimal", "low", "medium", "high"], resolved.SupportedEfforts);
            Assert.Equal("medium", resolved.DefaultEffort);
            Assert.Null(catalog.ResolveExposed("gpt-5"));
            Assert.Null(catalog.ResolveExposed("anthropic-openai-gpt-5-mini"));
            Assert.Null(catalog.ResolveExposed("claude-sonnet-4-5-20250929"));

            // Unexposing takes effect immediately (cache invalidated on write).
            await ExposeAsync(admin, new { modelId = "gpt-5", exposed = false });
            Assert.Null(catalog.ResolveExposed("claude-via-openai-gpt-5"));
        }
        finally
        {
            try { Directory.Delete(dataDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Models_endpoint_lists_exposed_models_without_an_anthropic_account()
    {
        await using var fake = await StartFake();
        var s = await LinkOpenAIAsync(fake);
        using var _ = s.Gw;
        await RefreshAsync(s.Admin);
        await ExposeAsync(s.Admin, new { modelId = "gpt-5-codex", exposed = true }, new { modelId = "gpt-5.1-codex-max", exposed = true });
        var client = s.Gw.CreateClient();

        var unauth = await client.SendAsync(ModelsRequest(null));
        Assert.Equal(HttpStatusCode.Unauthorized, unauth.StatusCode);
        var err = await unauth.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("error", err.GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(ModelsRequest("aigw_wrong"))).StatusCode);

        var res = await client.SendAsync(ModelsRequest(s.Key));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var data = body.GetProperty("data").EnumerateArray().ToList();
        Assert.Equal(["anthropic-openai-gpt-5-codex", "anthropic-openai-gpt-5.1-codex-max"], data.Select(m => m.GetProperty("id").GetString()));
        Assert.All(data, m =>
        {
            Assert.Matches(DiscoveryFilter, m.GetProperty("id").GetString()!);
            Assert.Equal("model", m.GetProperty("type").GetString());
            Assert.EndsWith("(OpenAI)", m.GetProperty("display_name").GetString());
            Assert.Contains("OpenAI", m.GetProperty("description").GetString());
            Assert.True(m.GetProperty("created_at").GetDateTimeOffset() > DateTimeOffset.UnixEpoch);
        });
        Assert.False(body.GetProperty("has_more").GetBoolean());
        Assert.Equal("anthropic-openai-gpt-5-codex", body.GetProperty("first_id").GetString());
        Assert.Equal("anthropic-openai-gpt-5.1-codex-max", body.GetProperty("last_id").GetString());

        // x-api-key works too, and single-model lookups of exposed ids are answered locally.
        var one = new HttpRequestMessage(HttpMethod.Get, "/anthropic/v1/models/anthropic-openai-gpt-5-codex");
        one.Headers.Add("x-api-key", s.Key);
        var model = await (await client.SendAsync(one)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("anthropic-openai-gpt-5-codex", model.GetProperty("id").GetString());
        Assert.Empty(fake.Anthropic.Requests);
    }

    [Fact]
    public async Task Models_endpoint_keeps_real_claude_models_when_an_anthropic_account_is_linked()
    {
        await using var fake = await StartFake();
        var s = await LinkOpenAIAsync(fake);
        using var _ = s.Gw;
        var ant = fake.Anthropic.Register(new FakeAccountSpec(Kind: "api_key"));
        await s.Gw.LinkAsync(s.Admin, new { credentialsJson = ant.ApiKey });
        var client = s.Gw.CreateClient();

        // Nothing exposed: a plain proxied call.
        var plain = await (await client.SendAsync(ModelsRequest(s.Key))).Content.ReadFromJsonAsync<JsonElement>();
        var claudeIds = plain.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToList();
        Assert.Contains("claude-sonnet-4-5-20250929", claudeIds);

        await RefreshAsync(s.Admin);
        await ExposeAsync(s.Admin, new { modelId = "gpt-5", exposed = true, advertisedId = "claude-openai-gpt-5" });
        var res = await client.SendAsync(ModelsRequest(s.Key));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
        var body = await res.Content.ReadFromJsonAsync<JsonElement>();
        var ids = body.GetProperty("data").EnumerateArray().Select(m => m.GetProperty("id").GetString()!).ToList();
        Assert.Equal(["claude-openai-gpt-5", .. claudeIds], ids);
        Assert.All(ids, id => Assert.Matches(DiscoveryFilter, id));
        Assert.Equal("claude-openai-gpt-5", body.GetProperty("first_id").GetString());
        Assert.Equal(claudeIds[^1], body.GetProperty("last_id").GetString());

        // The upstream call went through the Anthropic account's credential, with the client's query.
        var calls = fake.Anthropic.Requests.Where(r => r.Path == "/v1/models").ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(ant.ApiKey, calls[^1].Headers["x-api-key"]);
        Assert.Contains("limit=1000", calls[^1].Query);
    }

    [Theory]
    [InlineData("gpt-5", true)]
    [InlineData("gpt-5-codex", true)]
    [InlineData("gpt-4.1-mini", true)]
    [InlineData("o3", true)]
    [InlineData("o4-mini", true)]
    [InlineData("codex-mini-latest", true)]
    [InlineData("gpt-5-2025-08-07", false)]
    [InlineData("gpt-4o-audio-preview", false)]
    [InlineData("gpt-realtime", false)]
    [InlineData("text-embedding-3-large", false)]
    [InlineData("o3-deep-research", false)]
    [InlineData("computer-use-preview", false)]
    [InlineData("babbage-002", false)]
    public void Chat_model_filter(string id, bool expected) => Assert.Equal(expected, BridgeModelCatalog.IsChatModel(id));

    [Theory]
    [InlineData("anthropic-openai-gpt-5", null)]
    [InlineData("claude-gpt-5.1:fast", null)]
    [InlineData("gpt-5", "claude")]
    [InlineData("anthropic gpt", "letters")]
    [InlineData("", "required")]
    public void Advertised_id_validation(string id, string? problem)
    {
        var result = BridgeModelIds.Validate(id);
        if (problem == null) Assert.Null(result);
        else Assert.Contains(problem, result);
    }

    [Fact]
    public void Default_ids_match_the_discovery_filter() =>
        Assert.All(new[] { "gpt-5", "ft:gpt-4.1:org:custom:abc", "o4-mini" }, m =>
            Assert.Null(BridgeModelIds.Validate(BridgeModelIds.DefaultFor(m))));
}
