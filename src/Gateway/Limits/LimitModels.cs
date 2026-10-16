namespace Gateway.Limits;

public static class LimitSources
{
    public const string Headers = "headers";
    public const string UsageEndpoint = "usage_endpoint";
    public const string Manual = "manual";
}

public static class LimitStatuses
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Exhausted = "exhausted";

    public const double WarningThreshold = 80;

    public static string FromUtilization(double? utilization, string? providerStatus = null)
    {
        if (providerStatus is "rejected" || utilization >= 100) return Exhausted;
        if (providerStatus is "allowed_warning" || utilization >= WarningThreshold) return Warning;
        return Ok;
    }
}

/// <summary>
/// One provider limit of a linked account, e.g. Claude's 5-hour session window or an API key's requests-per-minute.
/// <see cref="Utilization"/> is a percentage (0–100); count-based limits also carry used/limit/remaining.
/// </summary>
public sealed record LimitWindow
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public double? Utilization { get; init; }
    public double? Used { get; init; }
    public double? Limit { get; init; }
    public double? Remaining { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }
    public int? WindowMinutes { get; init; }
    public string Status { get; init; } = LimitStatuses.Ok;
    public required string Source { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>The window's reset time has passed: the stored utilization no longer applies.</summary>
    public bool IsStale(DateTimeOffset now) => ResetsAt is { } r && r <= now;

    /// <summary>Utilization as of <paramref name="now"/> (0 once the window has reset).</summary>
    public double EffectiveUtilization(DateTimeOffset now) => IsStale(now) ? 0 : Utilization ?? 0;

    public string EffectiveStatus(DateTimeOffset now) => IsStale(now) ? LimitStatuses.Ok : Status;

    public bool IsExhausted(DateTimeOffset now) => EffectiveStatus(now) == LimitStatuses.Exhausted;

    /// <summary>Count-based per-minute limits (API keys) rather than subscription quota windows.</summary>
    public bool IsRateLimit => Key is "requests" or "tokens" or "input_tokens" or "output_tokens";
}

/// <summary>Account-level limit facts that are not per window.</summary>
public sealed record AccountLimitState
{
    public long AccountId { get; init; }
    public DateTimeOffset? ExhaustedUntil { get; init; }
    public string? ExhaustedReason { get; init; }
    public string? UnifiedStatus { get; init; }
    public string? RepresentativeClaim { get; init; }
    public string? OverageStatus { get; init; }
    public string? PlanType { get; init; }
    public DateTimeOffset? RefreshedAt { get; init; }
    public string? RefreshError { get; init; }
    public DateTimeOffset? ResetsSyncedAt { get; init; }
}

public sealed record ResetCredit
{
    public long Id { get; init; }
    public long AccountId { get; init; }
    /// <summary><c>provider</c> (redeemable through the provider) or <c>manual</c> (tracked by hand).</summary>
    public required string Source { get; init; }
    public string? ProviderResetId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Note { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? UsedAt { get; init; }
    public string? UsedBy { get; init; }

    public bool IsAvailable(DateTimeOffset now) => UsedAt == null && (ExpiresAt == null || ExpiresAt > now);
}

/// <summary>Everything the router needs to know about one account's limits at a point in time.</summary>
public sealed record AccountLimits(long AccountId, AccountLimitState State, IReadOnlyList<LimitWindow> Windows)
{
    public bool IsExhausted(DateTimeOffset now) => ExhaustedUntil(now) != null;

    /// <summary>When the account becomes usable again, or null if it is usable now.</summary>
    public DateTimeOffset? ExhaustedUntil(DateTimeOffset now)
    {
        DateTimeOffset? until = State.ExhaustedUntil is { } u && u > now ? u : null;
        foreach (var w in Windows.Where(w => w.IsExhausted(now)))
        {
            var r = w.ResetsAt ?? now.AddMinutes(1);
            if (until == null || r > until) until = r;
        }
        return until;
    }

    /// <summary>Highest current utilization across the account's windows (0 when nothing is known yet).</summary>
    public double MaxUtilization(DateTimeOffset now) =>
        Windows.Select(w => w.EffectiveUtilization(now)).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The soonest upcoming reset of a subscription quota window — the quota that would be lost soonest if unused.
    /// Per-minute API rate limits are ignored (API keys are pay-as-you-go; nothing is lost).
    /// </summary>
    public DateTimeOffset? SoonestQuotaReset(DateTimeOffset now) =>
        Windows.Where(w => !w.IsRateLimit && w.ResetsAt > now).Select(w => w.ResetsAt).Min();
}
