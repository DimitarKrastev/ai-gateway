using Gateway.Usage;
using Microsoft.AspNetCore.SignalR;

namespace Gateway.Live;

/// <summary>What changed; the UI refetches the views that depend on it.</summary>
[Flags]
public enum LiveTopic
{
    None = 0,
    /// <summary>A request started or finished streaming (the in-flight snapshot changed).</summary>
    InFlight = 1,
    /// <summary>A request was recorded (analytics, sessions and the request log changed).</summary>
    Usage = 2,
    /// <summary>Limits, exhaustion state or reset credits changed.</summary>
    Limits = 4,
    /// <summary>A linked account was added, edited or removed.</summary>
    Accounts = 8,
    /// <summary>A routing decision was logged (session moves, resets used).</summary>
    Routing = 16,
}

/// <summary>
/// Collects change signals from the write paths. Cheap and non-blocking for callers on the proxy hot path: it only sets
/// bits; <see cref="LiveBroadcaster"/> pushes them to UI clients, coalesced.
/// </summary>
public sealed class LiveNotifier
{
    private int _pending;
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify(LiveTopic topic)
    {
        int old;
        do old = _pending;
        while (Interlocked.CompareExchange(ref _pending, old | (int)topic, old) != old);
        if (old != 0) return;
        try { _signal.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Waits until something changed and returns everything that changed since the last call.</summary>
    public async Task<LiveTopic> WaitAsync(CancellationToken ct)
    {
        await _signal.WaitAsync(ct);
        return (LiveTopic)Interlocked.Exchange(ref _pending, 0);
    }
}

/// <summary>Admin UI clients connect here (cookie auth, under /api so the login requirement applies). Server push only.</summary>
public sealed class LiveHub : Hub;

/// <summary>
/// Pushes changes to every connected UI client at most every <see cref="MinInterval"/>: the in-flight snapshot itself, and
/// "changed" notices for the other topics (the UI refetches what it shows; a burst of subagent traffic becomes one push).
/// </summary>
public sealed class LiveBroadcaster(
    LiveNotifier notifier, IHubContext<LiveHub> hub, AnalyticsService analytics, TimeProvider time, ILogger<LiveBroadcaster> log)
    : BackgroundService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var topics = await notifier.WaitAsync(ct);
                if (topics.HasFlag(LiveTopic.InFlight)) await hub.Clients.All.SendAsync("inFlight", analytics.Live(), ct);
                var changed = topics & ~LiveTopic.InFlight;
                if (changed != LiveTopic.None)
                    await hub.Clients.All.SendAsync("changed", new
                    {
                        topics = Enum.GetValues<LiveTopic>().Where(t => t != LiveTopic.None && t != LiveTopic.InFlight && changed.HasFlag(t))
                            .Select(t => t.ToString().ToLowerInvariant()),
                        at = time.GetUtcNow().ToUnixTimeMilliseconds(),
                    }, ct);
                await Task.Delay(MinInterval, time, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                log.LogWarning(e, "Pushing live updates failed");
            }
        }
    }
}

public static class LiveExtensions
{
    public const string HubPath = "/api/live";

    public static IServiceCollection AddLiveUpdates(this IServiceCollection services)
    {
        services.AddSignalR();
        services.AddSingleton<LiveNotifier>();
        services.AddHostedService<LiveBroadcaster>();
        return services;
    }

    public static void MapLiveUpdates(this WebApplication app) => app.MapHub<LiveHub>(HubPath).RequireAuthorization();
}
