using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Gateway.Accounts;
using Gateway.Infrastructure;

namespace Gateway.Proxy;

/// <summary>
/// Keeps OAuth access tokens of subscription accounts fresh: refreshes ahead of expiry and on demand after an upstream
/// 401, persisting rotated tokens. Refreshes of one account are serialized so a rotated refresh token is used once.
/// </summary>
public sealed class TokenRefresher(
    AccountStore accounts, IHttpClientFactory http, GatewayOptions options, TimeProvider time, ILogger<TokenRefresher> log)
{
    // Public OAuth client ids of Claude Code and Codex; the refresh token was issued to them.
    public const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    public const string CodexClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    public static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    /// <summary>Returns credentials whose access token is not about to expire (refreshing if needed and possible).</summary>
    public async Task<AccountCredentials> EnsureFreshAsync(LinkedAccount account, CancellationToken ct)
    {
        var creds = account.Credentials;
        if (!account.IsOAuth || !NeedsRefresh(creds)) return creds;
        return await RefreshAsync(account.Id, staleAccessToken: creds.AccessToken, force: false, ct) ?? creds;
    }

    /// <summary>Refreshes after the upstream rejected <paramref name="staleAccessToken"/>. Returns null if impossible.</summary>
    public Task<AccountCredentials?> ForceRefreshAsync(LinkedAccount account, string? staleAccessToken, CancellationToken ct) =>
        account.IsOAuth ? RefreshAsync(account.Id, staleAccessToken, force: true, ct) : Task.FromResult<AccountCredentials?>(null);

    /// <summary>
    /// Sends a gateway-originated request (not a proxied one) with fresh credentials, refreshing and retrying once after a
    /// 401 for OAuth accounts. <paramref name="build"/> creates the request for the given credentials (it may be called twice).
    /// </summary>
    public async Task<HttpResponseMessage> SendAuthenticatedAsync(
        LinkedAccount account, Func<AccountCredentials, HttpRequestMessage> build, CancellationToken ct)
    {
        var client = http.CreateClient(ProxyHandler.UpstreamClientName);
        var creds = await EnsureFreshAsync(account, ct);
        var res = await client.SendAsync(build(creds), ct);
        if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized && account.IsOAuth && creds.RefreshToken != null
            && await ForceRefreshAsync(account, creds.AccessToken, ct) is { } fresh)
        {
            res.Dispose();
            res = await client.SendAsync(build(fresh), ct);
        }
        return res;
    }

    private bool NeedsRefresh(AccountCredentials c) =>
        c.AccessToken == null || (c.ExpiresAt is { } exp && exp - RefreshSkew <= time.GetUtcNow());

    private async Task<AccountCredentials?> RefreshAsync(long accountId, string? staleAccessToken, bool force, CancellationToken ct)
    {
        var gate = _locks.GetOrAdd(accountId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var account = accounts.Get(accountId);
            if (account == null) return null;
            var current = account.Credentials;
            // Another request refreshed while we waited.
            if (current.AccessToken != staleAccessToken && !NeedsRefresh(current)) return current;
            if (!force && !NeedsRefresh(current)) return current;
            if (string.IsNullOrEmpty(current.RefreshToken))
            {
                log.LogWarning("Account {Account} has an expired or rejected access token and no refresh token", accountId);
                return null;
            }

            var refreshed = account.Kind == AccountKinds.AnthropicOAuth
                ? await RefreshAnthropicAsync(current, ct)
                : await RefreshChatGptAsync(current, ct);
            if (refreshed == null) return null;
            accounts.UpdateCredentials(accountId, refreshed);
            log.LogInformation("Refreshed OAuth token for account {Account}", accountId);
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<AccountCredentials?> RefreshAnthropicAsync(AccountCredentials c, CancellationToken ct)
    {
        var json = await PostAsync($"{options.Upstreams.AnthropicAuth}/v1/oauth/token",
            new { grant_type = "refresh_token", refresh_token = c.RefreshToken, client_id = ClaudeCodeClientId }, ct);
        if (json is not { } j) return null;
        return c with
        {
            AccessToken = Str(j, "access_token") ?? c.AccessToken,
            RefreshToken = Str(j, "refresh_token") ?? c.RefreshToken,
            ExpiresAt = ExpiresIn(j),
        };
    }

    private async Task<AccountCredentials?> RefreshChatGptAsync(AccountCredentials c, CancellationToken ct)
    {
        var json = await PostAsync($"{options.Upstreams.OpenAIAuth}/oauth/token",
            new { client_id = CodexClientId, grant_type = "refresh_token", refresh_token = c.RefreshToken, scope = "openid profile email" }, ct);
        if (json is not { } j) return null;
        var access = Str(j, "access_token") ?? c.AccessToken;
        var idToken = Str(j, "id_token") ?? c.IdToken;
        return c with
        {
            AccessToken = access,
            RefreshToken = Str(j, "refresh_token") ?? c.RefreshToken,
            IdToken = idToken,
            ExpiresAt = CredentialParser.JwtExpiry(access) ?? ExpiresIn(j),
            ChatGptAccountId = CredentialParser.JwtChatGptAccountId(access) ?? CredentialParser.JwtChatGptAccountId(idToken) ?? c.ChatGptAccountId,
        };
    }

    private async Task<JsonElement?> PostAsync(string url, object body, CancellationToken ct)
    {
        try
        {
            using var res = await http.CreateClient(ProxyHandler.UpstreamClientName).PostAsJsonAsync(url, body, ct);
            if (!res.IsSuccessStatusCode)
            {
                log.LogWarning("Token refresh at {Url} failed with {Status}", url, (int)res.StatusCode);
                return null;
            }
            return await res.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Token refresh at {Url} failed", url);
            return null;
        }
    }

    private DateTimeOffset? ExpiresIn(JsonElement j) =>
        j.TryGetProperty("expires_in", out var e) && e.TryGetInt64(out var s) ? time.GetUtcNow().AddSeconds(s) : null;

    private static string? Str(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
