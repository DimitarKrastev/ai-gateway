using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace Gateway.Tests;

public class AccountsApiTests
{
    private static string FakeJwt(object payload)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\"}")}.{B64(JsonSerializer.Serialize(payload))}.sig";
    }

    [Fact]
    public async Task Accounts_api_requires_login()
    {
        using var factory = new GatewayFactory();
        var res = await factory.CreateClient().GetAsync("/api/accounts");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Links_all_kinds_from_pasted_formats_and_never_returns_secrets()
    {
        using var factory = new GatewayFactory();
        var admin = await factory.CreateLoggedInClientAsync();
        var exp = DateTimeOffset.UtcNow.AddHours(5);
        var chatgptAccess = FakeJwt(new Dictionary<string, object>
        {
            ["exp"] = exp.ToUnixTimeSeconds(),
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = "acc-123" },
        });
        var secrets = new[] { "sk-ant-api03-SECRETSECRETSECRET", "sk-ant-oat01-ACCESSACCESSACCESS", "sk-ant-ort01-REFRESHREFRESH", "sk-proj-OPENAIOPENAIOPENAI", chatgptAccess, "rt_CODEXREFRESHCODEX" };

        var bodies = new object[]
        {
            new { name = "anthropic key", credentialsJson = secrets[0] },
            new { credentialsJson = JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = secrets[1], refreshToken = secrets[2], expiresAt = exp.ToUnixTimeMilliseconds(), scopes = new[] { "user:inference" } } }) },
            new { kind = "openai_api_key", apiKey = secrets[3] },
            new { credentialsJson = JsonSerializer.Serialize(new { OPENAI_API_KEY = (string?)null, tokens = new { id_token = "x", access_token = secrets[4], refresh_token = secrets[5] }, last_refresh = "2026-10-01T00:00:00Z" }) },
        };
        var created = new List<JsonElement>();
        foreach (var body in bodies)
        {
            var res = await admin.PostAsJsonAsync("/api/accounts", body);
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            created.Add(await res.Content.ReadFromJsonAsync<JsonElement>());
        }

        Assert.Equal(["anthropic_api_key", "anthropic_oauth", "openai_api_key", "chatgpt_oauth"], created.Select(c => c.GetProperty("kind").GetString()));
        Assert.Equal(["anthropic", "anthropic", "openai", "openai"], created.Select(c => c.GetProperty("provider").GetString()));
        Assert.True(created[1].GetProperty("hasRefreshToken").GetBoolean());
        Assert.Equal(exp.ToUnixTimeSeconds(), created[1].GetProperty("accessTokenExpiresAt").GetDateTimeOffset().ToUnixTimeSeconds());
        Assert.Equal(exp.ToUnixTimeSeconds(), created[3].GetProperty("accessTokenExpiresAt").GetDateTimeOffset().ToUnixTimeSeconds());

        var list = await admin.GetStringAsync("/api/accounts");
        foreach (var s in secrets) Assert.DoesNotContain(s, list);
        Assert.Contains("…", list); // masked hints
        Assert.Equal(4, JsonDocument.Parse(list).RootElement.GetArrayLength());

        // Secrets are encrypted at rest too.
        var dbBytes = File.ReadAllBytes(Path.Combine(factory.DataDir, "gateway.db"));
        var dbText = Encoding.UTF8.GetString(dbBytes);
        foreach (var s in secrets) Assert.DoesNotContain(s, dbText);
    }

    [Fact]
    public async Task Setup_token_without_refresh_and_validation_errors()
    {
        using var factory = new GatewayFactory();
        var admin = await factory.CreateLoggedInClientAsync();

        var res = await admin.PostAsJsonAsync("/api/accounts", new { kind = "anthropic_oauth", accessToken = "sk-ant-oat01-LONGLIVEDTOKENLONGLIVED" });
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var acct = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(acct.GetProperty("hasRefreshToken").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/accounts", new { kind = "nope", apiKey = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/accounts", new { kind = "anthropic_api_key" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/accounts", new { credentialsJson = "{not json" })).StatusCode);
        // ChatGPT token without an account id anywhere.
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/accounts", new { kind = "chatgpt_oauth", accessToken = "opaque" })).StatusCode);
    }

    [Fact]
    public async Task Patch_and_delete_account()
    {
        using var factory = new GatewayFactory();
        var admin = await factory.CreateLoggedInClientAsync();
        var res = await admin.PostAsJsonAsync("/api/accounts", new { name = "a", apiKey = "sk-ant-api03-AAAAAAAAAAAAAAAAAAAA" });
        var id = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt64();

        var patched = await (await admin.PatchAsJsonAsync($"/api/accounts/{id}", new { name = "renamed", enabled = false, priority = 5 }))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("renamed", patched.GetProperty("name").GetString());
        Assert.False(patched.GetProperty("enabled").GetBoolean());
        Assert.Equal(5, patched.GetProperty("priority").GetInt32());

        var rotated = await admin.PatchAsJsonAsync($"/api/accounts/{id}", new { apiKey = "sk-ant-api03-BBBBBBBBBBBBBBBBBBBBzzzz" });
        Assert.EndsWith("zzzz", (await rotated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("credentialHint").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsJsonAsync($"/api/accounts/{id}", new { kind = "openai_api_key", apiKey = "sk-proj-x" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/accounts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync($"/api/accounts/{id}")).StatusCode);
    }

    [Fact]
    public async Task Client_keys_shown_once_and_revocable()
    {
        using var factory = new GatewayFactory();
        var admin = await factory.CreateLoggedInClientAsync();
        var created = await (await admin.PostAsJsonAsync("/api/client-keys", new { name = "laptop" })).Content.ReadFromJsonAsync<JsonElement>();
        var key = created.GetProperty("key").GetString()!;
        Assert.StartsWith("aigw_", key);

        var list = await admin.GetStringAsync("/api/client-keys");
        Assert.DoesNotContain(key, list);
        Assert.Contains("laptop", list);

        var id = created.GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/client-keys/{id}")).StatusCode);
        var revoked = JsonDocument.Parse(await admin.GetStringAsync("/api/client-keys")).RootElement[0];
        Assert.Equal(JsonValueKind.String, revoked.GetProperty("revokedAt").ValueKind);
    }
}
