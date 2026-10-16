using Gateway.Accounts;

namespace Gateway.Proxy;

/// <param name="AllowedAccountIds">When non-empty, only these accounts may serve the request (e.g. the OpenAI accounts that
/// list a bridged model); if none of them is enabled and eligible the restriction is ignored.</param>
public sealed record AccountSelectionRequest(
    string Provider, string SessionId, string UpstreamPath, long ClientKeyId, IReadOnlyCollection<long> ExcludedAccountIds,
    IReadOnlyCollection<long>? AllowedAccountIds = null);

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
