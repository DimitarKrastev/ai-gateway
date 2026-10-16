using Gateway.Accounts;

namespace Gateway.Proxy;

public static class ProxyEndpoints
{
    public static void MapProxyEndpoints(this WebApplication app)
    {
        app.Map("/anthropic/{**path}", (HttpContext ctx, string? path, ProxyHandler proxy) =>
            proxy.HandleAsync(ctx, Providers.Anthropic, path ?? ""));
        app.Map("/openai/{**path}", (HttpContext ctx, string? path, ProxyHandler proxy) =>
            proxy.HandleAsync(ctx, Providers.OpenAI, path ?? ""));
    }

    public static IServiceCollection AddGatewayProxy(this IServiceCollection services)
    {
        services.AddHttpClient(ProxyHandler.UpstreamClientName, c => c.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                AutomaticDecompression = System.Net.DecompressionMethods.None,
                UseCookies = false,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            })
            // Streaming responses must not be buffered or logged by handlers.
            .RemoveAllLoggers();
        services.AddSingleton<AccountStore>();
        services.AddSingleton<Gateway.ClientKeys.ClientKeyStore>();
        services.AddSingleton<SessionBindingStore>();
        services.AddSingleton<AccountCooldowns>();
        services.AddSingleton<IAccountSelector, StickyAccountSelector>();
        services.AddSingleton<UpstreamRouter>();
        services.AddSingleton<TokenRefresher>();
        services.AddSingleton<ProxyHandler>();
        return services;
    }
}
