using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

public class AdminPasswordTests
{
    private const string Initial = "generated-initial-pw";
    private const string Next = "a much better passphrase";

    /// <summary>A gateway whose password is not configured: it starts from a generated password file.</summary>
    private sealed class GeneratedPasswordFactory : GatewayFactory
    {
        public GeneratedPasswordFactory(string? dataDir = null, bool writeInitial = true)
        {
            if (dataDir is not null) DataDir = dataDir;
            Directory.CreateDirectory(DataDir);
            if (writeInitial) File.WriteAllText(Path.Combine(DataDir, AdminCredentials.PasswordFileName), Initial + "\n");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseSetting("Gateway:AdminPassword", "");
        }
    }

    private static async Task<HttpClient> Login(GatewayFactory gw, string password, HttpStatusCode expect = HttpStatusCode.OK)
    {
        var client = gw.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await client.PostAsJsonAsync("/api/auth/login", new { password });
        Assert.Equal(expect, res.StatusCode);
        return client;
    }

    private static Task<HttpResponseMessage> Change(HttpClient c, string current, string next) =>
        c.PostAsJsonAsync("/api/auth/change-password", new { currentPassword = current, newPassword = next });

    [Fact]
    public async Task Changing_the_password_replaces_the_generated_file_and_signs_out_other_sessions()
    {
        using var gw = new GeneratedPasswordFactory { KeepDataDir = true };
        var me = await Login(gw, Initial);
        var other = await Login(gw, Initial);

        var info = await me.GetFromJsonAsync<JsonElement>("/api/auth/password");
        Assert.Equal("generated", info.GetProperty("source").GetString());
        Assert.True(info.GetProperty("changeable").GetBoolean());

        Assert.Equal(HttpStatusCode.BadRequest, (await Change(me, "wrong", Next)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Change(me, Initial, "short")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Change(me, Initial, Next)).StatusCode);

        // Only a hash remains on disk.
        Assert.False(File.Exists(Path.Combine(gw.DataDir, AdminCredentials.PasswordFileName)));
        var hashFile = await File.ReadAllTextAsync(Path.Combine(gw.DataDir, AdminCredentials.HashFileName));
        Assert.DoesNotContain(Next, hashFile);

        // This session continues; the other one is signed out.
        Assert.Equal(HttpStatusCode.OK, (await me.GetAsync("/api/accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync("/api/accounts")).StatusCode);

        await Login(gw, Initial, HttpStatusCode.Unauthorized);
        await Login(gw, Next);

        // Survives a restart on the same data.
        using var restarted = new GeneratedPasswordFactory(gw.DataDir, writeInitial: false);
        await Login(restarted, Next);
        await Login(restarted, Initial, HttpStatusCode.Unauthorized);
        var after = await (await Login(restarted, Next)).GetFromJsonAsync<JsonElement>("/api/auth/password");
        Assert.Equal("custom", after.GetProperty("source").GetString());
        try { Directory.Delete(gw.DataDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_configured_password_cannot_be_changed_in_the_ui()
    {
        using var gw = new GatewayFactory();
        var me = await gw.CreateLoggedInClientAsync();
        var info = await me.GetFromJsonAsync<JsonElement>("/api/auth/password");
        Assert.Equal("configured", info.GetProperty("source").GetString());
        Assert.False(info.GetProperty("changeable").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Change(me, GatewayFactory.AdminPassword, Next)).StatusCode);
    }

    [Fact]
    public async Task Changing_the_password_requires_login()
    {
        using var gw = new GeneratedPasswordFactory();
        var anon = gw.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await Change(anon, Initial, Next)).StatusCode);
    }
}
