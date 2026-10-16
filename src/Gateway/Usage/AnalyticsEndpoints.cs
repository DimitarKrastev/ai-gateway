using Gateway.Proxy;

namespace Gateway.Usage;

public static class AnalyticsEndpoints
{
    public static IServiceCollection AddUsageAnalytics(this IServiceCollection services)
    {
        services.AddSingleton<PricingTable>();
        services.AddSingleton<LiveRequests>();
        services.AddSingleton<AgentIdentifier>();
        services.AddSingleton<UsageStore>();
        services.AddSingleton<AnalyticsService>();
        services.AddSingleton<IRequestObserver, UsageObserver>();
        return services;
    }

    /// <summary>Admin-only (cookie) analytics API; see .claude/ledger/analytics.md for shapes.</summary>
    public static void MapAnalyticsEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/analytics");

        g.MapGet("/summary", (HttpRequest req, AnalyticsService a, TimeProvider t) =>
            Results.Ok(a.Summary(AnalyticsFilter.FromQuery(req.Query, t.GetUtcNow()))));

        g.MapGet("/timeseries", (HttpRequest req, AnalyticsService a, TimeProvider t) =>
        {
            var q = req.Query;
            var metrics = (q["metric"].ToString() is { Length: > 0 } m ? m : "requests")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var groupBy = q["groupBy"].ToString() is { Length: > 0 } gb ? gb : "none";
            var limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 50) : 8;
            var result = a.Timeseries(AnalyticsFilter.FromQuery(q, t.GetUtcNow()), metrics, groupBy, q["bucket"], limit);
            return result != null
                ? Results.Ok(result)
                : Results.BadRequest(new
                {
                    error = "Unknown metric, groupBy or bucket.",
                    metrics = AnalyticsService.MetricNames,
                    groupBy = AnalyticsService.GroupNames,
                    buckets = new[] { "auto", "minute", "5min", "15min", "hour", "6hour", "day" },
                });
        });

        g.MapGet("/agent-series", (HttpRequest req, AnalyticsService a, TimeProvider t) =>
        {
            var q = req.Query;
            var metric = q["metric"].ToString() is { Length: > 0 } m ? m : "contextMax";
            var limit = int.TryParse(q["limit"], out var l) ? Math.Clamp(l, 1, 50) : 12;
            var result = a.AgentSeries(AnalyticsFilter.FromQuery(q, t.GetUtcNow()), metric, q["cumulative"] == "true", limit);
            return result != null
                ? Results.Ok(result)
                : Results.BadRequest(new { error = "Metric has no per-request value; use /timeseries?groupBy=agentId.", metrics = AnalyticsService.MetricNames });
        });

        g.MapGet("/breakdown", (HttpRequest req, AnalyticsService a, TimeProvider t) =>
        {
            var by = req.Query["by"].ToString() is { Length: > 0 } b ? b : "model";
            var limit = int.TryParse(req.Query["limit"], out var l) ? Math.Clamp(l, 1, 500) : 50;
            var rows = by == "none" ? null : a.Breakdown(AnalyticsFilter.FromQuery(req.Query, t.GetUtcNow()), by, limit);
            return rows != null
                ? Results.Ok(new { by, rows })
                : Results.BadRequest(new { error = "Unknown breakdown.", by = AnalyticsService.GroupNames.Skip(1).Append("tool") });
        });

        g.MapGet("/sessions", (HttpRequest req, AnalyticsService a, TimeProvider t) =>
        {
            var limit = int.TryParse(req.Query["limit"], out var l) ? Math.Clamp(l, 1, 500) : 50;
            var offset = int.TryParse(req.Query["offset"], out var o) ? Math.Max(0, o) : 0;
            return Results.Ok(a.Sessions(AnalyticsFilter.FromQuery(req.Query, t.GetUtcNow()), limit, offset));
        });

        g.MapGet("/requests", (HttpRequest req, AnalyticsService a, TimeProvider t) =>
        {
            var limit = int.TryParse(req.Query["limit"], out var l) ? Math.Clamp(l, 1, 500) : 50;
            long? before = long.TryParse(req.Query["before"], out var b) ? b : null;
            return Results.Ok(a.Requests(AnalyticsFilter.FromQuery(req.Query, t.GetUtcNow()), limit, before));
        });

        g.MapGet("/live", (AnalyticsService a) => Results.Ok(a.Live()));

        g.MapGet("/pricing", (PricingTable p) => Results.Ok(PricingTable.Defaults));
    }
}
