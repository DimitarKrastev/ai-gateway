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

/// <summary>One second of traffic: tokens generated (output) and sent (prompt, incl. cache) through the gateway.</summary>
public sealed record MeterSample(long T, double Output, double Prompt);

/// <summary>
/// Per-second token throughput for the live throughput chart, kept in memory for the last <see cref="Seconds"/> seconds.
/// Output is counted while it streams (an estimate from the streamed text, corrected to the reported total when the
/// request completes); prompt tokens when the provider reports them.
/// </summary>
public sealed class LiveMeter(TimeProvider time)
{
    public const int Seconds = 600;
    private readonly long[] _second = new long[Seconds];
    private readonly double[] _output = new double[Seconds];
    private readonly double[] _prompt = new double[Seconds];
    private readonly Lock _gate = new();

    public void AddOutput(double tokens) => Add(tokens, 0);
    public void AddPrompt(double tokens) => Add(0, tokens);

    private void Add(double output, double prompt)
    {
        if (output <= 0 && prompt <= 0) return;
        var sec = time.GetUtcNow().ToUnixTimeSeconds();
        var i = (int)(sec % Seconds);
        lock (_gate)
        {
            if (_second[i] != sec) { _second[i] = sec; _output[i] = 0; _prompt[i] = 0; }
            if (output > 0) _output[i] += output;
            if (prompt > 0) _prompt[i] += prompt;
        }
    }

    /// <summary>Completed seconds from <paramref name="fromSec"/> up to (not including) the current second; quiet seconds are 0.</summary>
    public IReadOnlyList<MeterSample> Range(long fromSec)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        fromSec = Math.Max(fromSec, now - Seconds + 1);
        var result = new List<MeterSample>((int)Math.Max(0, now - fromSec));
        lock (_gate)
        {
            for (var sec = fromSec; sec < now; sec++)
            {
                var i = (int)(sec % Seconds);
                result.Add(_second[i] == sec ? new MeterSample(sec * 1000, Math.Round(_output[i], 1), _prompt[i]) : new MeterSample(sec * 1000, 0, 0));
            }
        }
        return result;
    }
}

/// <summary>Admin UI clients connect here (cookie auth, under /api so the login requirement applies). Server push only.</summary>
public sealed class LiveHub : Hub;

/// <summary>
/// Pushes changes to every connected UI client at most every <see cref="MinInterval"/>: the in-flight snapshot itself, and
/// "changed" notices for the other topics (the UI refetches what it shows; a burst of subagent traffic becomes one push).
/// </summary>
public sealed class LiveBroadcaster(
    LiveNotifier notifier, IHubContext<LiveHub> hub, AnalyticsService analytics, LiveMeter meter, LiveRequests live,
    TimeProvider time, ILogger<LiveBroadcaster> log)
    : BackgroundService
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Every second, just after the second ends: that second's throughput sample and the in-flight count.</summary>
    private async Task TickMeterAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = time.GetUtcNow().ToUnixTimeMilliseconds();
                await Task.Delay(TimeSpan.FromMilliseconds(1000 - now % 1000 + 20), time, ct);
                var sec = time.GetUtcNow().ToUnixTimeSeconds() - 1;
                if (meter.Range(sec) is [var sample, ..])
                    await hub.Clients.All.SendAsync("meter", new { sample.T, sample.Output, sample.Prompt, inFlight = live.Count }, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                log.LogWarning(e, "Pushing the throughput meter failed");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var ticker = TickMeterAsync(ct);
        await PushChangesAsync(ct);
        await ticker;
    }

    private async Task PushChangesAsync(CancellationToken ct)
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
        services.AddSingleton<LiveMeter>();
        services.AddHostedService<LiveBroadcaster>();
        return services;
    }

    public static void MapLiveUpdates(this WebApplication app)
    {
        app.MapHub<LiveHub>(HubPath).RequireAuthorization();
        // History for the live throughput chart (the hub pushes one sample per second after that).
        app.MapGet("/api/analytics/meter", (int? seconds, LiveMeter meter, LiveRequests live, TimeProvider time) =>
        {
            var span = Math.Clamp(seconds ?? 300, 1, LiveMeter.Seconds);
            return Results.Ok(new { samples = meter.Range(time.GetUtcNow().ToUnixTimeSeconds() - span), inFlight = live.Count });
        });
    }
}
