using Gateway.Accounts;
using Gateway.Limits;
using Gateway.Proxy;

namespace Gateway.Routing;

/// <summary>
/// Chooses the linked account for each request from the account limits and the routing settings.
/// <para><b>Default (maximize off):</b> a session stays on its bound account while that account is enabled, eligible and not
/// exhausted. Otherwise — and for new sessions — it goes to the eligible non-exhausted account with the lowest priority
/// number, then the lowest utilization (in 10 % steps), then the fewest active sessions.</para>
/// <para><b>Maximize utilization:</b> subscription accounts are preferred over pay-as-you-go API keys; among them the account
/// whose quota window resets soonest wins (then lower utilization, then priority), so quota that would expire unused gets
/// used. A bound session moves only when another account resets at least <see cref="RebindMargin"/> sooner (or its own
/// account is exhausted), which keeps sessions from flapping between accounts with similar reset times; decisions are made at
/// request boundaries only.</para>
/// <para><b>All exhausted:</b> with auto-use resets on (default when maximizing), the provider reset credit closest to
/// expiring is redeemed and the session routes to that account. Otherwise the request goes to the account that recovers
/// soonest and the provider's own 429 reaches the agent.</para>
/// Every session move and reset use is logged in <see cref="RoutingEventStore"/>.
/// </summary>
public sealed class PolicyAccountSelector(
    AccountStore accounts, SessionBindingStore bindings, LimitsService limits, RoutingSettings settings, RoutingEventStore events,
    TimeProvider time, ILogger<PolicyAccountSelector> log) : IAccountSelector
{
    public static readonly TimeSpan RebindMargin = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan ResetSyncMaxAge = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AccountSelection?> SelectAsync(AccountSelectionRequest req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow();
            var eligible = accounts.List(req.Provider)
                .Where(a => a.Enabled && UpstreamRouter.IsEligible(a.Kind, req.UpstreamPath))
                .ToList();
            if (req.AllowedAccountIds is { Count: > 0 } allowed && eligible.Any(a => allowed.Contains(a.Id)))
                eligible = eligible.Where(a => allowed.Contains(a.Id)).ToList();
            // Accounts excluded for this request just answered 429; they can't serve it unless a reset is used on them,
            // so they stay in the reset pool (otherwise the credit closest to expiring could be skipped).
            var candidates = eligible.Where(a => !req.ExcludedAccountIds.Contains(a.Id)).ToList();
            if (candidates.Count == 0 && !(settings.AutoUseResets && eligible.Count > 0)) return null;

            var lim = limits.Load(eligible.Select(a => a.Id).ToList());
            var binding = bindings.Get(req.Provider, req.SessionId);
            var bound = binding == null ? null : candidates.FirstOrDefault(a => a.Id == binding.AccountId);
            var available = candidates.Where(a => !lim[a.Id].IsExhausted(now)).ToList();

            LinkedAccount? chosen = null;
            string? reason = null, detail = null;
            LinkedAccount? resetAccount = null;

            if (settings.MaximizeUtilization)
            {
                var pool = available.Where(a => a.IsOAuth).ToList();
                if (pool.Count == 0 && eligible.Any(a => a.IsOAuth) && settings.AutoUseResets)
                {
                    resetAccount = await TryUseResetAsync(req, eligible.Where(a => a.IsOAuth).ToList(), ct);
                    if (resetAccount != null) pool = [resetAccount];
                }
                if (pool.Count == 0) pool = available;
                var best = pool.OrderBy(a => lim[a.Id].SoonestQuotaReset(now) ?? DateTimeOffset.MaxValue)
                    .ThenBy(a => lim[a.Id].MaxUtilization(now)).ThenBy(a => a.Priority).ThenBy(a => a.Id)
                    .FirstOrDefault();
                if (bound != null && pool.Contains(bound) && (best == null || !ShouldMove(lim[bound.Id], lim[best.Id], now)))
                    chosen = bound;
                else
                {
                    chosen = best;
                    if (chosen != null && bound != null && pool.Contains(bound))
                    {
                        reason = RoutingReasons.Maximize;
                        detail = $"Account {chosen.Id} resets at {lim[chosen.Id].SoonestQuotaReset(now):O}, account {bound.Id} at {lim[bound.Id].SoonestQuotaReset(now):O}";
                    }
                    else if (chosen != null && bound != null && !bound.IsOAuth && chosen.IsOAuth)
                    {
                        reason = RoutingReasons.Maximize;
                        detail = "Subscription quota available again; moved off the API key";
                    }
                }
            }
            else if (bound != null && available.Contains(bound))
            {
                chosen = bound;
            }
            else
            {
                var load = bindings.ActiveSessionCounts(req.Provider, now - ActiveSessionWindow);
                chosen = available
                    .OrderBy(a => a.Priority)
                    .ThenBy(a => Math.Floor(lim[a.Id].MaxUtilization(now) / 10))
                    .ThenBy(a => load.GetValueOrDefault(a.Id))
                    .ThenBy(a => a.Id)
                    .FirstOrDefault();
                if (chosen == null && settings.AutoUseResets)
                    chosen = resetAccount = await TryUseResetAsync(req, eligible, ct);
            }

            if (resetAccount != null && chosen?.Id == resetAccount.Id)
            {
                reason = RoutingReasons.ResetUsed;
                detail = $"All {req.Provider} accounts were exhausted; used a reset on account {resetAccount.Id}";
            }

            if (chosen == null)
            {
                // Everything is exhausted and no reset could be used: send the request to the account expected to recover
                // first. If our picture is stale the request succeeds; otherwise the provider's 429 reaches the agent.
                if (candidates.Count == 0) return null;
                chosen = bound ?? candidates
                    .OrderBy(a => lim[a.Id].ExhaustedUntil(now) ?? now)
                    .ThenBy(a => a.Priority).ThenBy(a => a.Id)
                    .First();
            }

            if (reason == null && binding != null && binding.AccountId != chosen.Id)
                reason = req.ExcludedAccountIds.Contains(binding.AccountId) || (bound != null && lim[bound.Id].IsExhausted(now))
                    ? RoutingReasons.Exhausted
                    : RoutingReasons.Unavailable;

            bindings.Bind(req.Provider, req.SessionId, chosen.Id, req.ClientKeyId);
            if (reason != null && (binding != null || reason == RoutingReasons.ResetUsed))
            {
                events.Record(req.Provider, req.SessionId, binding?.AccountId, chosen.Id, reason, detail);
                log.LogInformation("Routing {Provider} session {Session}: {From} → {To} ({Reason})",
                    req.Provider, req.SessionId, binding?.AccountId, chosen.Id, reason);
            }
            return new AccountSelection(chosen, NewSession: binding == null, Rebound: binding != null && binding.AccountId != chosen.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ReportRateLimited(LinkedAccount account, TimeSpan? retryAfter) =>
        limits.MarkExhausted(account.Id, time.GetUtcNow() + (retryAfter ?? LimitsService.DefaultExhaustion), "rate_limited");

    /// <summary>Only move a healthy session when the other account's quota resets clearly sooner.</summary>
    private static bool ShouldMove(AccountLimits bound, AccountLimits best, DateTimeOffset now)
    {
        if (bound.AccountId == best.AccountId) return false;
        var boundReset = bound.SoonestQuotaReset(now) ?? DateTimeOffset.MaxValue;
        var bestReset = best.SoonestQuotaReset(now) ?? DateTimeOffset.MaxValue;
        return bestReset < boundReset - RebindMargin;
    }

    /// <summary>Redeems the provider reset credit closest to expiring among <paramref name="pool"/>; returns its account.</summary>
    private async Task<LinkedAccount?> TryUseResetAsync(AccountSelectionRequest req, List<LinkedAccount> pool, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        foreach (var a in pool.Where(a => ProviderUsageClient.SupportsUsageEndpoint(a)))
        {
            var state = limits.Load(a.Id).State;
            if (state.ResetsSyncedAt is not { } synced || now - synced > ResetSyncMaxAge)
            {
                try { await limits.SyncResetsAsync(a, ct); }
                catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Reset sync for account {Account} failed", a.Id); }
            }
        }

        var credits = pool
            .SelectMany(a => limits.AvailableResets(a.Id).Where(r => r.Source == "provider").Select(r => (Account: a, Reset: r)))
            .OrderBy(x => x.Reset.ExpiresAt ?? DateTimeOffset.MaxValue)
            .ThenBy(x => x.Account.Priority)
            .ToList();
        foreach (var (account, reset) in credits)
        {
            var result = await limits.UseResetAsync(account, reset.Id, "auto", ct);
            if (result.Ok)
            {
                log.LogInformation("All {Provider} accounts exhausted; used reset {Reset} (expires {Expires:O}) on account {Account} for session {Session}",
                    req.Provider, reset.Id, reset.ExpiresAt, account.Id, req.SessionId);
                return account;
            }
            log.LogWarning("Auto-using reset {Reset} on account {Account} failed: {Error}", reset.Id, account.Id, result.Error);
        }
        return null;
    }
}
