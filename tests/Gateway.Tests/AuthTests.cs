using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

public class AuthTests : IClassFixture<GatewayFactory>
{
    private readonly GatewayFactory _factory;

    public AuthTests(GatewayFactory factory) => _factory = factory;

    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

    [Fact]
    public async Task Health_is_anonymous()
    {
        var res = await NewClient().GetAsync("/api/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await res.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.Equal("ok", body!["status"].ToString());
    }

    [Fact]
    public async Task Login_with_wrong_password_is_rejected()
    {
        var client = NewClient();
        var res = await client.PostAsJsonAsync("/api/auth/login", new { password = "nope" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Login_then_me_then_logout()
    {
        var client = await _factory.CreateLoggedInClientAsync();
        var me = await client.GetFromJsonAsync<Dictionary<string, string>>("/api/auth/me");
        Assert.Equal("admin", me!["user"]);

        (await client.PostAsync("/api/auth/logout", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Protected_api_requires_login()
    {
        // Any non-anonymous /api path is gated before routing, so unknown paths also return 401.
        var anon = await NewClient().GetAsync("/api/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);

        var client = await _factory.CreateLoggedInClientAsync();
        var authed = await client.GetAsync("/api/anything");
        Assert.Equal(HttpStatusCode.NotFound, authed.StatusCode);
    }

    [Fact]
    public async Task Generated_password_is_written_to_data_dir()
    {
        // An env-provided password disables generation.
        if (Environment.GetEnvironmentVariable("GATEWAY_ADMIN_PASSWORD") is not null) return;

        var dir = Path.Combine(Path.GetTempPath(), "aigw-tests", Guid.NewGuid().ToString("N"));
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Gateway:DataDir", dir);
        });
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var file = Path.Combine(dir, "admin-password.txt");
        Assert.True(File.Exists(file));
        var password = File.ReadAllText(file).Trim();
        Assert.False(string.IsNullOrEmpty(password));
        var res = await client.PostAsJsonAsync("/api/auth/login", new { password });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        try { Directory.Delete(dir, true); } catch { }
    }

    [Fact]
    public async Task Unknown_ui_route_serves_spa_or_404_but_proxy_prefixes_do_not()
    {
        var client = NewClient();
        // Proxy prefixes are handled by the proxy (which wants a gateway key), never by the SPA fallback.
        var proxied = await client.GetAsync("/anthropic/unknown-path");
        Assert.Equal(HttpStatusCode.Unauthorized, proxied.StatusCode);
        Assert.Equal("application/json", proxied.Content.Headers.ContentType?.MediaType);
        var ui = await client.GetAsync("/accounts");
        Assert.Contains(ui.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.NotFound });
    }
}
