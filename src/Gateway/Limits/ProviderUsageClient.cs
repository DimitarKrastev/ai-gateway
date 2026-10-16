using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Gateway.Accounts;
using Gateway.Infrastructure;
using Gateway.Proxy;

namespace Gateway.Limits;

public sealed record ProviderResetsResult(bool Supported, IReadOnlyList<(string Id, DateTimeOffset? ExpiresAt)> Resets, string? Error);

public sealed record ProviderRedeemResult(bool Ok, LimitObservation? Usage, string? Error, bool NotFound = false);

/// <summary>
/// Talks to the subscription usage endpoints of the providers (OAuth accounts only):
/// <list type="bullet">
/// <item>Claude: <c>GET {AnthropicApi}/api/oauth/usage</c> → <c>{five_hour|seven_day|seven_day_opus…: {utilization %, resets_at}}</c>.</item>
/// <item>ChatGPT/Codex: <c>GET {ChatGptBackend}/backend-api/wham/usage</c> → <c>{plan_type, rate_limit:{limit_reached, primary_window, secondary_window}}</c>.</item>
/// <item>Reset credits (assumed capability, no public API is documented): <c>GET …/resets</c> → <c>{resets:[{id, expires_at}]}</c>
/// and <c>POST …/resets/{id}/redeem</c> under <c>/api/oauth/usage</c> (Claude) or <c>/backend-api/wham</c> (ChatGPT).
/// A 404 means the provider does not offer it.</item>
/// </list>
/// </summary>
public sealed class ProviderUsageClient(IHttpClientFactory http, GatewayOptions options, TokenRefresher refresher, TimeProvider time)
{
    public static bool SupportsUsageEndpoint(LinkedAccount a) => a.IsOAuth;

    private string UsageBase(LinkedAccount a) => a.Kind == AccountKinds.AnthropicOAuth
        ? $"{options.Upstreams.AnthropicApi}/api/oauth/usage"
        : $"{options.Upstreams.ChatGptBackend}/backend-api/wham";

    public async Task<(LimitObservation? Usage, string? Error)> GetUsageAsync(LinkedAccount account, CancellationToken ct)
    {
        if (!SupportsUsageEndpoint(account)) return (null, null);
        var url = account.Kind == AccountKinds.AnthropicOAuth ? UsageBase(account) : UsageBase(account) + "/usage";
        var (status, json, error) = await SendAsync(account, HttpMethod.Get, url, ct);
        if (json is not { } j) return (null, error ?? $"Usage endpoint returned {status}");
        return (ParseUsage(account.Kind, j, time.GetUtcNow()), null);
    }

    public async Task<ProviderResetsResult> ListResetsAsync(LinkedAccount account, CancellationToken ct)
    {
        if (!SupportsUsageEndpoint(account)) return new(false, [], null);
        var (status, json, error) = await SendAsync(account, HttpMethod.Get, UsageBase(account) + "/resets", ct);
        if (status == 404) return new(false, [], null);
        if (json is not { } j) return new(false, [], error ?? $"Resets endpoint returned {status}");
        var list = new List<(string, DateTimeOffset?)>();
        if (j.TryGetProperty("resets", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var r in arr.EnumerateArray())
                if (r.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                    list.Add((id.GetString()!, r.TryGetProperty("expires_at", out var e) ? Date(e) : null));
        return new(true, list, null);
    }

    public async Task<ProviderRedeemResult> RedeemAsync(LinkedAccount account, string providerResetId, CancellationToken ct)
    {
        if (!SupportsUsageEndpoint(account)) return new(false, null, "This account type has no provider reset API.");
        var (status, json, error) = await SendAsync(account, HttpMethod.Post,
            $"{UsageBase(account)}/resets/{Uri.EscapeDataString(providerResetId)}/redeem", ct);
        if (status == 404) return new(false, null, "The provider no longer offers this reset (expired or already used).", NotFound: true);
        if (json is not { } j || status is < 200 or >= 300) return new(false, null, error ?? $"Redeem returned {status}");
        var usage = j.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? ParseUsage(account.Kind, u, time.GetUtcNow()) : null;
        return new(true, usage, null);
    }

    private async Task<(int Status, JsonElement? Json, string? Error)> SendAsync(LinkedAccount account, HttpMethod method, string url, CancellationToken ct)
    {
        try
        {
            var client = http.CreateClient(ProxyHandler.UpstreamClientName);
            var creds = await refresher.EnsureFreshAsync(account, ct);
            var res = await client.SendAsync(Build(account, method, url, creds), ct);
            if (res.StatusCode == HttpStatusCode.Unauthorized && creds.RefreshToken != null
                && await refresher.ForceRefreshAsync(account, creds.AccessToken, ct) is { } fresh)
            {
                res.Dispose();
                res = await client.SendAsync(Build(account, method, url, fresh), ct);
            }
            using (res)
            {
                var status = (int)res.StatusCode;
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode) return (status, null, $"{url} returned {status}");
                try { return (status, JsonDocument.Parse(text).RootElement.Clone(), null); }
                catch (JsonException) { return (status, null, $"{url} returned invalid JSON"); }
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return (0, null, e.Message);
        }
    }

    private static HttpRequestMessage Build(LinkedAccount account, HttpMethod method, string url, AccountCredentials creds)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
        req.Headers.Accept.ParseAdd("application/json");
        if (account.Kind == AccountKinds.AnthropicOAuth)
            req.Headers.TryAddWithoutValidation("anthropic-beta", ProxyHandler.OAuthBeta);
        else
            req.Headers.TryAddWithoutValidation("chatgpt-account-id", creds.ChatGptAccountId);
        if (method == HttpMethod.Post) req.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return req;
    }

    public static LimitObservation ParseUsage(string kind, JsonElement j, DateTimeOffset now) =>
        kind == AccountKinds.AnthropicOAuth ? ParseClaudeUsage(j, now) : ParseChatGptUsage(j, now);

    private static LimitObservation ParseClaudeUsage(JsonElement j, DateTimeOffset now)
    {
        var windows = new List<LimitWindow>();
        foreach (var p in j.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.Object
                || !p.Value.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number
                || !p.Value.TryGetProperty("resets_at", out var r))
                continue;
            var util = Math.Round(u.GetDouble(), 2);
            windows.Add(new LimitWindow
            {
                Key = p.Name, Label = LimitHeaderParser.Label(p.Name, null), Utilization = util, ResetsAt = Date(r),
                WindowMinutes = LimitHeaderParser.DefaultWindowMinutes(p.Name), Status = LimitStatuses.FromUtilization(util),
                Source = LimitSources.UsageEndpoint, UpdatedAt = now,
            });
        }
        return new LimitObservation { Windows = windows };
    }

    private static LimitObservation ParseChatGptUsage(JsonElement j, DateTimeOffset now)
    {
        var windows = new List<LimitWindow>();
        var limitReached = false;
        if (j.TryGetProperty("rate_limit", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            limitReached = rl.TryGetProperty("limit_reached", out var lr) && lr.ValueKind == JsonValueKind.True;
            foreach (var p in rl.EnumerateObject())
            {
                if (!p.Name.EndsWith("_window") || p.Value.ValueKind != JsonValueKind.Object) continue;
                var key = p.Name[..^"_window".Length];
                var w = p.Value;
                double? util = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number ? Math.Round(up.GetDouble(), 2) : null;
                int? minutes = w.TryGetProperty("limit_window_seconds", out var lws) && lws.TryGetInt64(out var secs) ? (int)(secs / 60) : null;
                DateTimeOffset? resets = w.TryGetProperty("reset_at", out var ra) && ra.TryGetInt64(out var at) ? DateTimeOffset.FromUnixTimeSeconds(at)
                    : w.TryGetProperty("reset_after_seconds", out var ras) && ras.TryGetInt64(out var after) ? now.AddSeconds(after) : null;
                windows.Add(new LimitWindow
                {
                    Key = key, Label = LimitHeaderParser.Label(key, minutes), Utilization = util, ResetsAt = resets, WindowMinutes = minutes,
                    Status = LimitStatuses.FromUtilization(util), Source = LimitSources.UsageEndpoint, UpdatedAt = now,
                });
            }
        }
        var plan = j.TryGetProperty("plan_type", out var pt) && pt.ValueKind == JsonValueKind.String ? pt.GetString() : null;
        return new LimitObservation { Windows = windows, PlanType = plan, LimitReached = limitReached };
    }

    private static DateTimeOffset? Date(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null,
        JsonValueKind.Number when e.TryGetInt64(out var s) => DateTimeOffset.FromUnixTimeSeconds(s),
        _ => null,
    };
}
