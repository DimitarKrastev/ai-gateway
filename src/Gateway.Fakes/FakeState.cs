using System.Security.Cryptography;
using System.Text;

namespace Gateway.Fakes;

public sealed class FakeOptions
{
    /// <summary>Unknown credentials create a new account on first use instead of returning 401.</summary>
    public bool AutoRegister { get; set; } = true;
    public TimeProvider Time { get; set; } = TimeProvider.System;
    public int MaxRecordedRequests { get; set; } = 2000;
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromHours(8);
    /// <summary>Delay between streamed SSE events (ms). <c>[[slow:N]]</c> in a prompt overrides it per request.</summary>
    public int StreamChunkDelayMs { get; set; }
    /// <summary>
    /// Generation speed in output tokens/second for standard mode; 0 = unpaced (as fast as possible). <c>[[tps:N]]</c> in a
    /// prompt overrides it per request. Streams spread the output deltas over the generation time; JSON responses wait for it.
    /// </summary>
    public double TokensPerSecond { get; set; }
    /// <summary>Speed multiplier for fast mode (Anthropic <c>speed: "fast"</c>, OpenAI <c>service_tier</c> fast/priority).</summary>
    public double FastSpeedFactor { get; set; } = 2.5;
    /// <summary>Speed multiplier for OpenAI <c>service_tier: "flex"</c>.</summary>
    public double FlexSpeedFactor { get; set; } = 0.5;
    /// <summary>Model ids listed by the OpenAI API <c>GET /v1/models</c> (includes non-chat models, like the real list).</summary>
    public List<string> OpenAIApiModels { get; set; } =
    [
        "gpt-5", "gpt-5-codex", "gpt-5-mini", "gpt-5-nano", "o4-mini", "gpt-4.1", "gpt-4o-realtime-preview", "gpt-4o-mini-tts",
        "gpt-4o-transcribe", "gpt-image-1", "text-embedding-3-small", "tts-1", "whisper-1", "dall-e-3", "omni-moderation-latest",
        "gpt-4o-search-preview", "davinci-002",
    ];
    /// <summary>Models listed by the ChatGPT Codex backend <c>GET /backend-api/codex/models</c>.</summary>
    public List<FakeCodexModel> CodexModels { get; set; } =
    [
        new("gpt-5-codex", "gpt-5-codex", "Optimized for agentic coding.", ["low", "medium", "high"], "medium"),
        new("gpt-5", "gpt-5", "Broad world knowledge with strong general reasoning.", ["minimal", "low", "medium", "high"], "medium"),
        new("gpt-5.1-codex-max", "gpt-5.1-codex-max", "Codex-optimized flagship for deep and fast reasoning.", ["low", "medium", "high", "xhigh"], "medium"),
        new("codex-auto-review", "codex-auto-review", "Internal review model.", ["medium"], "medium", Visibility: "hide"),
    ];
}

/// <summary>One entry of the fake Codex backend model list (mirrors codex-rs <c>ModelInfo</c>).</summary>
public sealed record FakeCodexModel(
    string Slug, string DisplayName, string Description, string[] Efforts, string DefaultEffort, string Visibility = "list", int Priority = 0);

public enum FakeAuthKind { ApiKey, OAuth }

public sealed class FakeLimitWindow
{
    public required string Name { get; init; }
    /// <summary>Percent, 0–100.</summary>
    public double Utilization { get; set; }
    public int WindowMinutes { get; set; }
    public DateTimeOffset ResetsAt { get; set; }
    /// <summary>Percent added per successful request.</summary>
    public double IncrementPerRequest { get; set; }
}

public sealed class FakeReset
{
    public required string Id { get; init; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class FakeAccount
{
    public required string Id { get; init; }
    public FakeAuthKind Kind { get; set; }
    public string? ApiKey { get; set; }
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTimeOffset? AccessTokenExpiresAt { get; set; }
    public string? ChatGptAccountId { get; set; }
    public string PlanType { get; set; } = "max";
    public List<FakeLimitWindow> Windows { get; } = [];
    public List<FakeReset> Resets { get; } = [];
    public bool ForceExhausted { get; set; }
    // API-key style per-minute limits.
    public int RequestsLimit { get; set; } = 4000;
    public int RequestsUsed { get; set; }
    public long TokensLimit { get; set; } = 2_000_000;
    public long TokensUsed { get; set; }
    public DateTimeOffset MinuteWindowStart { get; set; }
    public int RequestCount { get; set; }
    public int RedeemedResets { get; set; }
}

public sealed record RecordedRequest(
    long Seq, DateTimeOffset Time, string Provider, string Method, string Path, string Query,
    Dictionary<string, string> Headers, string Body, string? AccountId, int StatusCode);

public sealed record FakeWindowSpec(string Name, double? Utilization = null, int? WindowMinutes = null,
    DateTimeOffset? ResetsAt = null, double? IncrementPerRequest = null);

public sealed record FakeResetSpec(string? Id, DateTimeOffset ExpiresAt);

/// <summary>Create/patch body for <c>/fake-admin/{provider}/accounts</c>. Null fields are left unchanged (patch) or defaulted (create).</summary>
public sealed record FakeAccountSpec(
    string? Id = null, string? Kind = null, string? ApiKey = null, string? AccessToken = null, string? RefreshToken = null,
    DateTimeOffset? AccessTokenExpiresAt = null, string? ChatGptAccountId = null, string? PlanType = null,
    List<FakeWindowSpec>? Windows = null, List<FakeResetSpec>? Resets = null, bool? ReplaceResets = null,
    int? RequestsLimit = null, long? TokensLimit = null, bool? Exhausted = null);

/// <summary>State of one fake provider (accounts, prompt cache simulation, recorded requests). Thread-safe.</summary>
public sealed class FakeProviderState
{
    private readonly object _gate = new();
    private readonly Dictionary<string, FakeAccount> _accounts = new();
    private readonly Dictionary<string, (int Tokens, DateTimeOffset LastUsed)> _cache = new();
    private readonly LinkedList<RecordedRequest> _requests = new();
    private long _seq;

    public FakeProviderState(string provider, FakeOptions options)
    {
        Provider = provider;
        Options = options;
    }

    public string Provider { get; }
    public FakeOptions Options { get; }
    public DateTimeOffset Now => Options.Time.GetUtcNow();
    internal object Gate => _gate;

    // ---------- accounts ----------

    public FakeAccount Register(FakeAccountSpec spec)
    {
        lock (_gate)
        {
            var kind = ParseKind(spec.Kind) ?? (spec.ApiKey != null ? FakeAuthKind.ApiKey : FakeAuthKind.OAuth);
            var id = spec.Id ?? NewId("acct");
            var acct = new FakeAccount { Id = id, Kind = kind, MinuteWindowStart = Now };
            if (kind == FakeAuthKind.ApiKey)
            {
                acct.ApiKey = spec.ApiKey ?? (Provider == "anthropic" ? "sk-ant-api03-" : "sk-proj-") + RandomToken(24);
                acct.PlanType = "api";
            }
            else
            {
                acct.AccessToken = spec.AccessToken ?? NewAccessToken(acct, spec.ChatGptAccountId);
                acct.RefreshToken = spec.RefreshToken ?? NewRefreshToken();
                acct.AccessTokenExpiresAt = spec.AccessTokenExpiresAt ?? Now + Options.AccessTokenLifetime;
                acct.ChatGptAccountId = spec.ChatGptAccountId ?? (Provider == "openai" ? Guid.NewGuid().ToString() : null);
                acct.PlanType = Provider == "anthropic" ? "max" : "plus";
                foreach (var w in DefaultWindows()) acct.Windows.Add(w);
                acct.Resets.Add(new FakeReset { Id = NewId("rst"), ExpiresAt = Now.AddDays(3) });
                acct.Resets.Add(new FakeReset { Id = NewId("rst"), ExpiresAt = Now.AddDays(10) });
            }
            if (spec.Resets != null) acct.Resets.Clear();
            ApplyLocked(acct, spec with { Kind = null, ApiKey = null, AccessToken = null, RefreshToken = null, AccessTokenExpiresAt = null, ChatGptAccountId = null });
            _accounts[id] = acct;
            return acct;
        }
    }

    public FakeAccount? Patch(string id, FakeAccountSpec spec)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var acct)) return null;
            ApplyLocked(acct, spec);
            return acct;
        }
    }

    private void ApplyLocked(FakeAccount acct, FakeAccountSpec spec)
    {
        if (ParseKind(spec.Kind) is { } k) acct.Kind = k;
        if (spec.ApiKey != null) acct.ApiKey = spec.ApiKey;
        if (spec.AccessToken != null) acct.AccessToken = spec.AccessToken;
        if (spec.RefreshToken != null) acct.RefreshToken = spec.RefreshToken;
        if (spec.AccessTokenExpiresAt != null) acct.AccessTokenExpiresAt = spec.AccessTokenExpiresAt;
        if (spec.ChatGptAccountId != null) acct.ChatGptAccountId = spec.ChatGptAccountId;
        if (spec.PlanType != null) acct.PlanType = spec.PlanType;
        if (spec.RequestsLimit != null) acct.RequestsLimit = spec.RequestsLimit.Value;
        if (spec.TokensLimit != null) acct.TokensLimit = spec.TokensLimit.Value;
        if (spec.Exhausted != null) acct.ForceExhausted = spec.Exhausted.Value;
        foreach (var ws in spec.Windows ?? [])
        {
            var w = acct.Windows.FirstOrDefault(x => x.Name == ws.Name);
            if (w == null)
            {
                var minutes = ws.WindowMinutes ?? DefaultWindowMinutes(ws.Name);
                w = new FakeLimitWindow { Name = ws.Name, WindowMinutes = minutes, ResetsAt = Now.AddMinutes(minutes), IncrementPerRequest = 1 };
                acct.Windows.Add(w);
            }
            if (ws.Utilization != null) w.Utilization = Math.Clamp(ws.Utilization.Value, 0, 100);
            if (ws.WindowMinutes != null) w.WindowMinutes = ws.WindowMinutes.Value;
            if (ws.ResetsAt != null) w.ResetsAt = ws.ResetsAt.Value;
            if (ws.IncrementPerRequest != null) w.IncrementPerRequest = ws.IncrementPerRequest.Value;
        }
        if (spec.Resets != null)
        {
            if (spec.ReplaceResets == true) acct.Resets.Clear();
            foreach (var r in spec.Resets) acct.Resets.Add(new FakeReset { Id = r.Id ?? NewId("rst"), ExpiresAt = r.ExpiresAt });
        }
    }

    public IReadOnlyList<FakeAccount> Accounts { get { lock (_gate) return _accounts.Values.ToList(); } }

    public FakeAccount? Get(string id) { lock (_gate) return _accounts.GetValueOrDefault(id); }

    public FakeAccount? FindByApiKey(string key) { lock (_gate) return _accounts.Values.FirstOrDefault(a => a.Kind == FakeAuthKind.ApiKey && a.ApiKey == key); }

    public FakeAccount? FindByAccessToken(string token) { lock (_gate) return _accounts.Values.FirstOrDefault(a => a.Kind == FakeAuthKind.OAuth && a.AccessToken == token); }

    /// <summary>Rotates both tokens (old ones stop working). Returns null for an unknown/rotated refresh token.</summary>
    public FakeAccount? Refresh(string refreshToken)
    {
        lock (_gate)
        {
            var acct = _accounts.Values.FirstOrDefault(a => a.Kind == FakeAuthKind.OAuth && a.RefreshToken == refreshToken);
            if (acct == null) return null;
            acct.AccessToken = NewAccessToken(acct, acct.ChatGptAccountId);
            acct.RefreshToken = NewRefreshToken();
            acct.AccessTokenExpiresAt = Now + Options.AccessTokenLifetime;
            return acct;
        }
    }

    public bool ExpireAccessToken(string id)
    {
        lock (_gate)
        {
            if (!_accounts.TryGetValue(id, out var acct)) return false;
            acct.AccessTokenExpiresAt = Now.AddSeconds(-1);
            return true;
        }
    }

    // ---------- limits ----------

    /// <summary>Rolls over windows whose reset time has passed. Call under <see cref="Gate"/>.</summary>
    internal void RollLocked(FakeAccount acct)
    {
        var now = Now;
        foreach (var w in acct.Windows)
        {
            if (w.ResetsAt > now) continue;
            w.Utilization = 0;
            var span = TimeSpan.FromMinutes(Math.Max(1, w.WindowMinutes));
            while (w.ResetsAt <= now) w.ResetsAt += span;
        }
        if (now - acct.MinuteWindowStart >= TimeSpan.FromMinutes(1))
        {
            acct.MinuteWindowStart = now;
            acct.RequestsUsed = 0;
            acct.TokensUsed = 0;
        }
        acct.Resets.RemoveAll(r => r.ExpiresAt <= now);
    }

    /// <summary>Returns the blocking window (or a synthetic one for API-key limits) when the account cannot serve requests.</summary>
    internal FakeLimitWindow? BlockingWindowLocked(FakeAccount acct)
    {
        RollLocked(acct);
        var full = acct.Windows.Where(w => w.Utilization >= 100).OrderByDescending(w => w.ResetsAt).FirstOrDefault();
        if (full != null) return full;
        if (acct.Kind == FakeAuthKind.ApiKey && (acct.RequestsUsed >= acct.RequestsLimit || acct.TokensUsed >= acct.TokensLimit))
            return new FakeLimitWindow { Name = "requests", Utilization = 100, WindowMinutes = 1, ResetsAt = acct.MinuteWindowStart.AddMinutes(1) };
        if (acct.ForceExhausted)
        {
            var w = acct.Windows.OrderByDescending(x => x.Utilization).FirstOrDefault();
            return w ?? new FakeLimitWindow { Name = "requests", Utilization = 100, WindowMinutes = 1, ResetsAt = Now.AddMinutes(1) };
        }
        return null;
    }

    internal void ConsumeLocked(FakeAccount acct, long tokens)
    {
        acct.RequestCount++;
        acct.RequestsUsed++;
        acct.TokensUsed += tokens;
        foreach (var w in acct.Windows) w.Utilization = Math.Min(100, w.Utilization + w.IncrementPerRequest);
    }

    /// <summary>Redeems a reset credit: zeroes every window and restarts it. Returns false if the id is unknown/expired.</summary>
    public bool RedeemReset(FakeAccount acct, string resetId)
    {
        lock (_gate)
        {
            RollLocked(acct);
            var reset = acct.Resets.FirstOrDefault(r => r.Id == resetId);
            if (reset == null) return false;
            acct.Resets.Remove(reset);
            foreach (var w in acct.Windows)
            {
                w.Utilization = 0;
                w.ResetsAt = Now.AddMinutes(w.WindowMinutes);
            }
            acct.ForceExhausted = false;
            acct.RedeemedResets++;
            return true;
        }
    }

    // ---------- prompt cache simulation ----------

    /// <summary>
    /// Records <paramref name="tokens"/> as the cached prefix for (account, key) and returns how many tokens of it
    /// were already cached (0 on a miss or after the 5-minute TTL).
    /// </summary>
    internal int CacheLookupAndStore(FakeAccount acct, string key, int tokens)
    {
        lock (_gate)
        {
            var k = acct.Id + "|" + key;
            var now = Now;
            var hit = _cache.TryGetValue(k, out var prev) && now - prev.LastUsed <= TimeSpan.FromMinutes(5) ? Math.Min(prev.Tokens, tokens) : 0;
            _cache[k] = (Math.Max(tokens, hit), now);
            return hit;
        }
    }

    // ---------- encrypted reasoning (store:false round trip) ----------

    private readonly HashSet<string> _encryptedReasoning = new();

    /// <summary>Issues an opaque encrypted reasoning blob that a later request may send back as a reasoning input item.</summary>
    internal string IssueEncryptedReasoning()
    {
        var blob = "gAAAAfake" + RandomToken(32);
        lock (_gate) _encryptedReasoning.Add(blob);
        return blob;
    }

    /// <summary>True when the blob was issued by this fake (like the real API, which rejects reasoning it cannot decrypt).</summary>
    internal bool KnowsEncryptedReasoning(string blob) { lock (_gate) return _encryptedReasoning.Contains(blob); }

    // ---------- recorded requests ----------

    internal void Record(string method, string path, string query, Dictionary<string, string> headers, string body, string? accountId, int status)
    {
        lock (_gate)
        {
            _requests.AddLast(new RecordedRequest(++_seq, Now, Provider, method, path, query, headers, body, accountId, status));
            while (_requests.Count > Options.MaxRecordedRequests) _requests.RemoveFirst();
        }
    }

    public IReadOnlyList<RecordedRequest> Requests { get { lock (_gate) return _requests.ToList(); } }

    public void ClearRequests() { lock (_gate) _requests.Clear(); }

    public void Clear()
    {
        lock (_gate)
        {
            _accounts.Clear();
            _cache.Clear();
            _requests.Clear();
        }
    }

    // ---------- helpers ----------

    private IEnumerable<FakeLimitWindow> DefaultWindows()
    {
        if (Provider == "anthropic")
        {
            yield return new FakeLimitWindow { Name = "five_hour", WindowMinutes = 300, ResetsAt = Now.AddMinutes(300), IncrementPerRequest = 2 };
            yield return new FakeLimitWindow { Name = "seven_day", WindowMinutes = 10080, ResetsAt = Now.AddMinutes(10080), IncrementPerRequest = 0.5 };
            yield return new FakeLimitWindow { Name = "seven_day_opus", WindowMinutes = 10080, ResetsAt = Now.AddMinutes(10080), IncrementPerRequest = 0.25 };
        }
        else
        {
            yield return new FakeLimitWindow { Name = "primary", WindowMinutes = 300, ResetsAt = Now.AddMinutes(300), IncrementPerRequest = 2 };
            yield return new FakeLimitWindow { Name = "secondary", WindowMinutes = 10080, ResetsAt = Now.AddMinutes(10080), IncrementPerRequest = 0.5 };
        }
    }

    private static int DefaultWindowMinutes(string name) => name is "five_hour" or "primary" ? 300 : 10080;

    private static FakeAuthKind? ParseKind(string? kind) => kind?.ToLowerInvariant() switch
    {
        "api_key" or "apikey" or "api" => FakeAuthKind.ApiKey,
        "oauth" or "subscription" => FakeAuthKind.OAuth,
        _ => null,
    };

    private string NewAccessToken(FakeAccount acct, string? chatGptAccountId)
    {
        if (Provider == "anthropic") return "sk-ant-oat01-" + RandomToken(32);
        // ChatGPT access tokens are JWTs; clients read exp and the account id claim from them.
        var exp = (Now + Options.AccessTokenLifetime).ToUnixTimeSeconds();
        return ChatGptJwt(acct.Id, exp, chatGptAccountId, acct.PlanType, null);
    }

    internal static string ChatGptJwt(string sub, long exp, string? accountId, string plan, string? email)
    {
        var payload = new Dictionary<string, object?>
        {
            ["sub"] = sub,
            ["exp"] = exp,
            ["https://api.openai.com/auth"] = new Dictionary<string, object?> { ["chatgpt_account_id"] = accountId, ["chatgpt_plan_type"] = plan },
        };
        if (email != null) payload["email"] = email;
        return Jwt(System.Text.Json.JsonSerializer.Serialize(payload));
    }

    internal static string Jwt(string payloadJson) =>
        B64Url("""{"alg":"none","typ":"JWT"}""") + "." + B64Url(payloadJson) + "." + RandomToken(16);

    private string NewRefreshToken() => Provider == "anthropic" ? "sk-ant-ort01-" + RandomToken(32) : "rt_" + RandomToken(32);

    internal static string NewId(string prefix) => prefix + "_" + RandomToken(20);

    internal static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string B64Url(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The two fake providers hosted by one app.</summary>
public sealed class FakeStates(FakeOptions options)
{
    public FakeOptions Options { get; } = options;
    public FakeProviderState Anthropic { get; } = new("anthropic", options);
    public FakeProviderState OpenAI { get; } = new("openai", options);

    public FakeProviderState? ByName(string name) => name.ToLowerInvariant() switch
    {
        "anthropic" => Anthropic,
        "openai" => OpenAI,
        _ => null,
    };
}
