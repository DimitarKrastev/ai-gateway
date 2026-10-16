using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Gateway.Tests;

/// <summary>In-memory gateway with an isolated temp data dir and a known admin password.</summary>
public class GatewayFactory : WebApplicationFactory<Program>
{
    public const string AdminPassword = "test-admin-password";
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "aigw-tests", Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Gateway:DataDir", DataDir);
        builder.UseSetting("Gateway:AdminPassword", AdminPassword);
    }

    public async Task<HttpClient> CreateLoggedInClientAsync()
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
        var res = await client.PostAsJsonAsync("/api/auth/login", new { password = AdminPassword });
        res.EnsureSuccessStatusCode();
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(DataDir, recursive: true); } catch { /* best effort */ }
    }
}
