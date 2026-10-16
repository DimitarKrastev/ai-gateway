using Gateway.Accounts;
using Gateway.Proxy;
using Gateway.Routing;

namespace Gateway.Limits;

public sealed record ManualResetInput(DateTimeOffset? ExpiresAt, string? Note);

public static class LimitsEndpoints
{
    public static IServiceCollection AddLimitsAndRouting(this IServiceCollection services)
    {
        services.AddSingleton<LimitsStore>();
        services.AddSingleton<ProviderUsageClient>();
        services.AddSingleton<LimitsService>();
        services.AddSingleton<IRequestObserver, LimitsObserver>();
        services.AddSingleton<LimitsRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<LimitsRefreshService>());
        services.AddSingleton<RoutingSettings>();
        services.AddSingleton<RoutingEventStore>();
        services.AddSingleton<IAccountSelector, PolicyAccountSelector>();
        return services;
    }

    /// <summary>Admin-only (cookie) limits, resets, routing settings and routing events; shapes in .claude/ledger/limits.md.</summary>
    public static void MapLimitsEndpoints(this WebApplication app)
    {
        var acc = app.MapGroup("/api/accounts/{id:long}");

        acc.MapGet("/limits", (long id, AccountStore store, LimitsService limits) =>
            store.Get(id) is null ? Results.NotFound() : Results.Ok(limits.View(id)));

        acc.MapPost("/limits/refresh", async (long id, AccountStore store, LimitsService limits, CancellationToken ct) =>
        {
            if (store.Get(id) is not { } account) return Results.NotFound();
            await limits.RefreshAsync(account, ct);
            return Results.Ok(limits.View(id));
        });

        acc.MapGet("/resets", async (long id, bool? includeUsed, AccountStore store, LimitsService limits, CancellationToken ct) =>
        {
            if (store.Get(id) is not { } account) return Results.NotFound();
            await limits.SyncResetsAsync(account, ct);
            return Results.Ok(limits.ResetViews(id, includeUsed ?? false));
        });

        acc.MapPost("/resets", (long id, ManualResetInput input, AccountStore store, LimitsService limits) =>
        {
            if (store.Get(id) is null) return Results.NotFound();
            var r = limits.AddManualReset(id, input.ExpiresAt, string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim());
            return Results.Created($"/api/accounts/{id}/resets/{r.Id}", limits.ResetViews(id, includeUsed: true).First(v => v.Id == r.Id));
        });

        acc.MapPost("/resets/{resetId:long}/use", async (long id, long resetId, AccountStore store, LimitsService limits,
            RoutingEventStore events, CancellationToken ct) =>
        {
            if (store.Get(id) is not { } account) return Results.NotFound();
            var result = await limits.UseResetAsync(account, resetId, "manual", ct);
            if (!result.Ok) return Results.Json(new { error = result.Error }, statusCode: result.StatusCode);
            events.Record(account.Provider, null, null, account.Id, RoutingReasons.ResetUsed, $"Reset {resetId} used from the UI");
            return Results.Ok(new { ok = true, limits = limits.View(id) });
        });

        acc.MapDelete("/resets/{resetId:long}", (long id, long resetId, LimitsService limits) =>
            limits.DeleteManualReset(id, resetId) ? Results.NoContent() : Results.NotFound());

        app.MapGet("/api/settings", (RoutingSettings s) => s.View());
        app.MapPut("/api/settings", (RoutingSettingsInput input, RoutingSettings s) => s.Update(input));

        app.MapGet("/api/routing/events", (int? limit, string? sessionId, long? accountId, RoutingEventStore events) =>
            events.List(limit ?? 100, sessionId, accountId));
    }
}
