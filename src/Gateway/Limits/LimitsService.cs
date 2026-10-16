using Gateway.Accounts;

namespace Gateway.Limits;

public sealed record LimitWindowView(
    string Key, string Label, double? Utilization, double? Used, double? Limit, double? Remaining,
    DateTimeOffset? ResetsAt, int? WindowMinutes, string Status, string Source, DateTimeOffset UpdatedAt, bool Stale);

public sealed record ResetsSummary(int Available, DateTimeOffset? NextExpiresAt);

public sealed record AccountLimitsView(
    IReadOnlyList<LimitWindowView> Windows, bool Exhausted, DateTimeOffset? ExhaustedUntil, string? ExhaustedReason,
    string? UnifiedStatus, string? RepresentativeClaim, string? OverageStatus, string? PlanType,
    DateTimeOffset? RefreshedAt, string? RefreshError, DateTimeOffset? UpdatedAt,
    double MaxUtilization, DateTimeOffset? SoonestResetAt, ResetsSummary Resets);

public sealed record ResetCreditView(
    long Id, string Source, DateTimeOffset? ExpiresAt, string? Note, DateTimeOffset CreatedAt, DateTimeOffset? UsedAt, string? UsedBy,
    bool Available, bool Redeemable);

public sealed record UseResetResult(bool Ok, string? Error, int StatusCode = 200);

/// <summary>
/// Owns the limit picture of every linked account: records what responses and usage endpoints report, tracks exhaustion
/// and reset credits, and redeems resets.
/// </summary>
public sealed class LimitsService(
    LimitsStore store, ProviderUsageClient usage, TimeProvider time, ILogger<LimitsService> log)
{
    public static readonly TimeSpan DefaultExhaustion = TimeSpan.FromSeconds(60);

    private DateTimeOffset Now => time.GetUtcNow();

    public Dictionary<long, AccountLimits> Load(IReadOnlyCollection<long> accountIds) => store.Load(accountIds);

    public AccountLimits Load(long accountId) => store.Load(accountId);

    /// <summary>Records limits reported by an upstream response or usage endpoint.</summary>
    public void Observe(long accountId, LimitObservation obs)
    {
        if (obs.IsEmpty) return;
        if (obs.Windows.Count > 0) store.UpsertWindows(accountId, obs.Windows);
        store.UpdateState(accountId, s => s with
        {
            UnifiedStatus = obs.UnifiedStatus ?? s.UnifiedStatus,
            RepresentativeClaim = obs.RepresentativeClaim ?? s.RepresentativeClaim,
            OverageStatus = obs.OverageStatus ?? s.OverageStatus,
            PlanType = obs.PlanType ?? s.PlanType,
        });
        if (obs.LimitReached)
        {
            var until = obs.Windows.Where(w => w.Status == LimitStatuses.Exhausted && w.ResetsAt > Now).Select(w => w.ResetsAt).Max();
            MarkExhausted(accountId, until ?? Now + DefaultExhaustion, "limit_reached");
        }
    }

    /// <summary>The account cannot serve requests until <paramref name="until"/> (keeps a later existing mark).</summary>
    public void MarkExhausted(long accountId, DateTimeOffset until, string reason)
    {
        store.UpdateState(accountId, s => s.ExhaustedUntil is { } cur && cur > until && cur > Now
            ? s
            : s with { ExhaustedUntil = until, ExhaustedReason = reason });
    }

    public void ClearExhaustion(long accountId)
    {
        if (store.GetState(accountId).ExhaustedUntil == null) return;
        store.UpdateState(accountId, s => s with { ExhaustedUntil = null, ExhaustedReason = null });
    }

    /// <summary>Pulls the usage endpoint and reset credits of an OAuth account. API-key accounts learn limits from responses only.</summary>
    public async Task RefreshAsync(LinkedAccount account, CancellationToken ct)
    {
        if (!ProviderUsageClient.SupportsUsageEndpoint(account)) return;
        var (obs, error) = await usage.GetUsageAsync(account, ct);
        if (obs != null)
        {
            Observe(account.Id, obs);
            // A fresh usage report without any full window means earlier exhaustion marks no longer apply.
            if (!obs.LimitReached && obs.Windows.All(w => w.Status != LimitStatuses.Exhausted)) ClearExhaustion(account.Id);
        }
        await SyncResetsAsync(account, ct);
        store.UpdateState(account.Id, s => s with { RefreshedAt = obs != null ? Now : s.RefreshedAt, RefreshError = error });
        if (error != null) log.LogWarning("Limit refresh for account {Account} failed: {Error}", account.Id, error);
    }

    public async Task SyncResetsAsync(LinkedAccount account, CancellationToken ct)
    {
        var result = await usage.ListResetsAsync(account, ct);
        if (!result.Supported) return;
        store.SyncProviderResets(account.Id, result.Resets);
        store.UpdateState(account.Id, s => s with { ResetsSyncedAt = Now });
    }

    public IReadOnlyList<ResetCredit> AvailableResets(long accountId) =>
        store.ListResets(accountId).Where(r => r.IsAvailable(Now)).ToList();

    public IReadOnlyList<ResetCreditView> ResetViews(long accountId, bool includeUsed) =>
        store.ListResets(accountId, includeUsed)
            .Where(r => includeUsed || r.IsAvailable(Now))
            .Select(r => new ResetCreditView(r.Id, r.Source, r.ExpiresAt, r.Note, r.CreatedAt, r.UsedAt, r.UsedBy,
                r.IsAvailable(Now), r.Source == "provider"))
            .ToList();

    public ResetCredit AddManualReset(long accountId, DateTimeOffset? expiresAt, string? note) => store.AddManualReset(accountId, expiresAt, note);

    public bool DeleteManualReset(long accountId, long resetId)
    {
        var r = store.GetReset(accountId, resetId);
        if (r == null || r.Source != "manual") return false;
        store.DeleteReset(resetId);
        return true;
    }

    /// <summary>
    /// Uses a reset credit. Provider credits are redeemed with the provider; manual credits record that the user reset the
    /// account themselves. Either way the account's windows start over and exhaustion is cleared.
    /// </summary>
    public async Task<UseResetResult> UseResetAsync(LinkedAccount account, long resetId, string usedBy, CancellationToken ct)
    {
        var reset = store.GetReset(account.Id, resetId);
        if (reset == null) return new(false, "Reset not found.", 404);
        if (!reset.IsAvailable(Now)) return new(false, "This reset was already used or has expired.", 409);

        if (reset.Source == "provider")
        {
            var result = await usage.RedeemAsync(account, reset.ProviderResetId!, ct);
            if (!result.Ok)
            {
                if (result.NotFound) store.DeleteReset(reset.Id);
                return new(false, result.Error, result.NotFound ? 410 : 502);
            }
            store.MarkResetUsed(reset.Id, usedBy);
            store.ZeroWindows(account.Id);
            ClearExhaustion(account.Id);
            if (result.Usage != null) Observe(account.Id, result.Usage);
            await SyncResetsAsync(account, ct);
        }
        else
        {
            store.MarkResetUsed(reset.Id, usedBy);
            store.ZeroWindows(account.Id);
            ClearExhaustion(account.Id);
        }
        log.LogInformation("Used reset {Reset} on account {Account} ({By})", reset.Id, account.Id, usedBy);
        return new(true, null);
    }

    public AccountLimitsView View(long accountId) => View(store.Load(accountId), AvailableResets(accountId));

    public Dictionary<long, AccountLimitsView> Views(IReadOnlyCollection<long> accountIds)
    {
        var limits = store.Load(accountIds);
        var resets = store.ListResets(null).Where(r => r.IsAvailable(Now)).GroupBy(r => r.AccountId).ToDictionary(g => g.Key, g => g.ToList());
        return accountIds.ToDictionary(id => id, id => View(limits[id], resets.GetValueOrDefault(id) ?? []));
    }

    private AccountLimitsView View(AccountLimits l, IReadOnlyList<ResetCredit> resets)
    {
        var now = Now;
        var windows = l.Windows.Select(w => new LimitWindowView(
            w.Key, w.Label, w.IsStale(now) && w.Utilization != null ? 0 : w.Utilization, w.IsStale(now) && w.Used != null ? 0 : w.Used,
            w.Limit, w.IsStale(now) ? w.Limit ?? w.Remaining : w.Remaining,
            w.ResetsAt, w.WindowMinutes, w.EffectiveStatus(now), w.Source, w.UpdatedAt, w.IsStale(now))).ToList();
        var until = l.ExhaustedUntil(now);
        return new AccountLimitsView(
            windows, until != null, until, until != null ? l.State.ExhaustedReason ?? "limit_reached" : null,
            l.State.UnifiedStatus, l.State.RepresentativeClaim, l.State.OverageStatus, l.State.PlanType,
            l.State.RefreshedAt, l.State.RefreshError, l.Windows.Select(w => (DateTimeOffset?)w.UpdatedAt).Max(),
            l.MaxUtilization(now), l.SoonestQuotaReset(now),
            new ResetsSummary(resets.Count, resets.Where(r => r.ExpiresAt != null).Select(r => r.ExpiresAt).Min()));
    }
}
