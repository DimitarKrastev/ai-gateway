using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;

namespace Gateway.Fakes;

public static class FakeProvidersExtensions
{
    public static IServiceCollection AddFakeProviders(this IServiceCollection services, Action<FakeOptions>? configure = null)
    {
        var options = new FakeOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);
        services.AddSingleton<FakeStates>();
        return services;
    }

    /// <summary>Adds the request recorder and maps both fake providers plus the <c>/fake-admin</c> control API.</summary>
    public static WebApplication UseFakeProviders(this WebApplication app)
    {
        var states = app.Services.GetRequiredService<FakeStates>();
        app.Use(async (ctx, next) =>
        {
            var state = ProviderFor(ctx, states);
            if (state == null) { await next(); return; }
            ctx.Request.EnableBuffering();
            using (var reader = new StreamReader(ctx.Request.Body, leaveOpen: true))
                ctx.Items["fake.body"] = await reader.ReadToEndAsync();
            ctx.Request.Body.Position = 0;
            try
            {
                await next();
            }
            finally
            {
                var headers = ctx.Request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());
                state.Record(ctx.Request.Method, ctx.Request.Path, ctx.Request.QueryString.Value ?? "", headers,
                    (string)ctx.Items["fake.body"]!, ctx.Items["fake.account"] as string, ctx.Response.StatusCode);
            }
        });

        AnthropicFake.Map(app);
        OpenAIFake.Map(app);
        // Both providers expose GET /v1/models; Anthropic clients always send anthropic-version or x-api-key.
        app.MapGet("/v1/models", ctx =>
            ctx.Request.Headers.ContainsKey("anthropic-version") || ctx.Request.Headers.ContainsKey("x-api-key")
                ? AnthropicModels(ctx)
                : OpenAIFake.ApiModels(ctx));
        FakeAdmin.Map(app);
        app.MapGet("/", () => "Gateway fakes: Anthropic + OpenAI. Control API under /fake-admin.");
        return app;
    }

    private static async Task AnthropicModels(HttpContext ctx)
    {
        var state = ctx.RequestServices.GetRequiredService<FakeStates>().Anthropic;
        if (await AnthropicFake.AuthenticateAsync(ctx, state) == null) return;
        var models = new[] { ("claude-opus-4-1-20250805", "Claude Opus 4.1"), ("claude-sonnet-4-5-20250929", "Claude Sonnet 4.5"), ("claude-haiku-4-5-20251001", "Claude Haiku 4.5") };
        await ctx.Response.WriteAsJsonAsync(new
        {
            data = models.Select(m => new { type = "model", id = m.Item1, display_name = m.Item2, created_at = "2025-01-01T00:00:00Z" }),
            has_more = false, first_id = models[0].Item1, last_id = models[^1].Item1,
        });
    }

    private static FakeProviderState? ProviderFor(HttpContext ctx, FakeStates states)
    {
        var p = ctx.Request.Path;
        if (p.StartsWithSegments("/fake-admin")) return null;
        if (p.StartsWithSegments("/v1/messages") || p.StartsWithSegments("/api/oauth") || p.StartsWithSegments("/v1/oauth")) return states.Anthropic;
        if (p.StartsWithSegments("/v1/responses") || p.StartsWithSegments("/v1/chat") || p.StartsWithSegments("/backend-api") || p.StartsWithSegments("/oauth")) return states.OpenAI;
        if (p.StartsWithSegments("/v1/models"))
            return ctx.Request.Headers.ContainsKey("anthropic-version") || ctx.Request.Headers.ContainsKey("x-api-key") ? states.Anthropic : states.OpenAI;
        return null;
    }
}

/// <summary>
/// Hosts both fake providers. <see cref="StartInMemoryAsync"/> uses TestServer (no ports; use <see cref="CreateHandler"/>
/// as the upstream HttpMessageHandler); <see cref="StartAsync"/> listens on a real loopback port.
/// </summary>
public sealed class FakeProviderServer : IAsyncDisposable
{
    public const string InMemoryBaseUrl = "http://fake-providers.test";
    private readonly WebApplication _app;
    private readonly bool _inMemory;

    private FakeProviderServer(WebApplication app, bool inMemory, string baseUrl)
    {
        _app = app;
        _inMemory = inMemory;
        BaseUrl = baseUrl;
    }

    public string BaseUrl { get; }
    public FakeStates States => _app.Services.GetRequiredService<FakeStates>();
    public FakeProviderState Anthropic => States.Anthropic;
    public FakeProviderState OpenAI => States.OpenAI;

    public static Task<FakeProviderServer> StartInMemoryAsync(Action<FakeOptions>? configure = null) => StartCoreAsync(null, configure);

    /// <summary>Starts on 127.0.0.1:<paramref name="port"/>. The caller is responsible for picking a free port in the allowed range.</summary>
    public static Task<FakeProviderServer> StartAsync(int port, Action<FakeOptions>? configure = null) => StartCoreAsync(port, configure);

    private static async Task<FakeProviderServer> StartCoreAsync(int? port, Action<FakeOptions>? configure)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddFakeProviders(configure);
        if (port == null) builder.WebHost.UseTestServer();
        else builder.WebHost.UseKestrel(k => k.Listen(IPAddress.Loopback, port.Value));
        var app = builder.Build();
        app.UseFakeProviders();
        await app.StartAsync();
        var baseUrl = port == null
            ? InMemoryBaseUrl
            : app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().TrimEnd('/');
        return new FakeProviderServer(app, port == null, baseUrl);
    }

    public HttpMessageHandler CreateHandler() =>
        _inMemory ? _app.GetTestServer().CreateHandler() : new SocketsHttpHandler();

    public HttpClient CreateClient() => new(CreateHandler()) { BaseAddress = new Uri(BaseUrl) };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
