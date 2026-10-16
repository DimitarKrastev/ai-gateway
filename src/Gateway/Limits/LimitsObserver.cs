using System.Globalization;
using Gateway.Proxy;

namespace Gateway.Limits;

/// <summary>
/// Reads rate-limit headers from every proxied response. A 429 marks the account exhausted immediately (so a failover in
/// the same request already sees it); other observations are persisted when the attempt completes, off the streaming path.
/// A successful response clears an earlier exhaustion mark.
/// </summary>
public sealed class LimitsObserver(LimitsService limits, TimeProvider time) : IRequestObserver
{
    private const string ItemKey = "limits.observation";

    public void OnResponseStarted(ProxiedRequestContext ctx)
    {
        var now = time.GetUtcNow();
        var obs = LimitHeaderParser.Parse(ctx.ResponseHeaders, now);
        if (ctx.StatusCode == 429)
        {
            limits.Observe(ctx.Account.Id, obs);
            limits.MarkExhausted(ctx.Account.Id, ExhaustedUntil(ctx.ResponseHeaders, obs, now), "rate_limited");
            return;
        }
        ctx.Items[ItemKey] = obs;
    }

    public Task OnCompletedAsync(ProxiedRequestContext ctx)
    {
        if (ctx.Items.GetValueOrDefault(ItemKey) is LimitObservation obs)
        {
            limits.Observe(ctx.Account.Id, obs);
            if (ctx.StatusCode is >= 200 and < 300 && !obs.LimitReached) limits.ClearExhaustion(ctx.Account.Id);
        }
        return Task.CompletedTask;
    }

    /// <summary>Retry-After, else the reset of the window that is full, else the unified reset, else a short default.</summary>
    public static DateTimeOffset ExhaustedUntil(IReadOnlyDictionary<string, string> headers, LimitObservation obs, DateTimeOffset now)
    {
        if (headers.GetValueOrDefault("retry-after") is { } ra)
        {
            if (LimitHeaderParser.Double(ra) is { } secs) return now.AddSeconds(Math.Max(1, secs));
            if (DateTimeOffset.TryParse(ra, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) && date > now) return date;
        }
        var full = obs.Windows.Where(w => w.Status == LimitStatuses.Exhausted && w.ResetsAt > now).Select(w => w.ResetsAt).Max();
        if (full != null) return full.Value;
        if (LimitHeaderParser.UnixSeconds(headers.GetValueOrDefault("anthropic-ratelimit-unified-reset")) is { } u && u > now) return u;
        return now + LimitsService.DefaultExhaustion;
    }
}
