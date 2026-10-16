using System.Collections.Concurrent;
using Gateway.Accounts;

namespace Gateway.Proxy;

public sealed record AccountSelectionRequest(
    string Provider, string SessionId, string UpstreamPath, long ClientKeyId, IReadOnlyCollection<long> ExcludedAccountIds);

/// <param name="Rebound">The session was bound to a different account before this selection (prompt cache miss).</param>
public sealed record AccountSelection(LinkedAccount Account, bool NewSession, bool Rebound);

/// <summary>
/// Chooses the linked account for a proxied request and maintains the session binding.
/// Returns null when no enabled, eligible account is available.
/// </summary>
public interface IAccountSelector
{
    Task<AccountSelection?> SelectAsync(AccountSelectionRequest request, CancellationToken ct);

    /// <summary>The upstream answered 429 for this account; avoid it until <paramref name="retryAfter"/> passes.</summary>
    void ReportRateLimited(LinkedAccount account, TimeSpan? retryAfter);
}

/// <summary>In-memory "do not use until" marks set when an upstream rate-limits an account.</summary>
public sealed class AccountCooldowns(TimeProvider time)
{
    public static readonly TimeSpan DefaultCooldown = TimeSpan.FromSeconds(60);
    private readonly ConcurrentDictionary<long, DateTimeOffset> _until = new();

    public void Mark(long accountId, TimeSpan? duration) => _until[accountId] = time.GetUtcNow() + (duration ?? DefaultCooldown);

    public bool IsCoolingDown(long accountId) => _until.TryGetValue(accountId, out var until) && until > time.GetUtcNow();

    public DateTimeOffset? Until(long accountId) => IsCoolingDown(accountId) ? _until[accountId] : null;
}

/// <summary>
/// Default policy: keep a session on its bound account while that account is enabled, eligible and not cooling down;
/// otherwise bind to the eligible account with the lowest priority number, then the fewest active sessions.
/// </summary>
public sealed class StickyAccountSelector(AccountStore accounts, SessionBindingStore bindings, AccountCooldowns cooldowns, TimeProvider time)
    : IAccountSelector
{
    public static readonly TimeSpan ActiveSessionWindow = TimeSpan.FromHours(1);
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<AccountSelection?> SelectAsync(AccountSelectionRequest req, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var candidates = accounts.List(req.Provider)
                .Where(a => a.Enabled && UpstreamRouter.IsEligible(a.Kind, req.UpstreamPath) && !req.ExcludedAccountIds.Contains(a.Id))
                .ToList();
            var binding = bindings.Get(req.Provider, req.SessionId);

            var bound = binding == null ? null : candidates.FirstOrDefault(a => a.Id == binding.AccountId);
            if (bound != null && !cooldowns.IsCoolingDown(bound.Id))
            {
                bindings.Bind(req.Provider, req.SessionId, bound.Id, req.ClientKeyId);
                return new AccountSelection(bound, NewSession: false, Rebound: false);
            }

            var available = candidates.Where(a => !cooldowns.IsCoolingDown(a.Id)).ToList();
            if (available.Count == 0) return null;
            var load = bindings.ActiveSessionCounts(req.Provider, time.GetUtcNow() - ActiveSessionWindow);
            var chosen = available
                .OrderBy(a => a.Priority)
                .ThenBy(a => load.GetValueOrDefault(a.Id))
                .ThenBy(a => a.Id)
                .First();
            bindings.Bind(req.Provider, req.SessionId, chosen.Id, req.ClientKeyId);
            return new AccountSelection(chosen, NewSession: binding == null, Rebound: binding != null && binding.AccountId != chosen.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ReportRateLimited(LinkedAccount account, TimeSpan? retryAfter) => cooldowns.Mark(account.Id, retryAfter);
}
