using System.Globalization;
using System.Text.RegularExpressions;

namespace Gateway.Limits;

/// <summary>Limit facts read from one upstream response (or usage-endpoint payload).</summary>
public sealed record LimitObservation
{
    public List<LimitWindow> Windows { get; init; } = [];
    public string? UnifiedStatus { get; init; }
    public string? RepresentativeClaim { get; init; }
    public string? OverageStatus { get; init; }
    public string? PlanType { get; init; }
    /// <summary>The provider says the account cannot serve requests right now.</summary>
    public bool LimitReached { get; init; }

    public bool IsEmpty => Windows.Count == 0 && UnifiedStatus == null && PlanType == null && !LimitReached;
}

/// <summary>
/// Parses provider rate-limit response headers into <see cref="LimitWindow"/>s:
/// <list type="bullet">
/// <item>Claude subscriptions: <c>anthropic-ratelimit-unified-{5h,7d,7d_opus,…}-{utilization,reset,status}</c>
/// (utilization is a 0–1 fraction, reset is unix seconds), plus <c>-unified-status</c>, <c>-representative-claim</c>,
/// <c>-overage-status</c>.</item>
/// <item>Anthropic API keys: <c>anthropic-ratelimit-{requests,tokens,input-tokens,output-tokens}-{limit,remaining,reset}</c>
/// (reset is RFC 3339).</item>
/// <item>Codex with a ChatGPT login: <c>x-codex-{primary,secondary}-{used-percent,window-minutes,reset-after-seconds,reset-at}</c>.</item>
/// <item>OpenAI API keys: <c>x-ratelimit-{limit,remaining,reset}-{requests,tokens}</c> (reset is a duration like <c>1m30s</c>).</item>
/// </list>
/// </summary>
public static partial class LimitHeaderParser
{
    [GeneratedRegex(@"^anthropic-ratelimit-unified-(?<w>[a-z0-9_]+)-(?<f>utilization|reset|status)$")]
    private static partial Regex UnifiedHeader();

    [GeneratedRegex(@"^anthropic-ratelimit-(?<k>requests|tokens|input-tokens|output-tokens)-(?<f>limit|remaining|reset)$")]
    private static partial Regex AnthropicStandardHeader();

    [GeneratedRegex(@"^x-codex-(?<w>[a-z0-9_]+)-(?<f>used-percent|window-minutes|reset-after-seconds|reset-at)$")]
    private static partial Regex CodexHeader();

    [GeneratedRegex(@"^x-ratelimit-(?<f>limit|remaining|reset)-(?<k>requests|tokens)$")]
    private static partial Regex OpenAIHeader();

    [GeneratedRegex(@"(?<n>\d+(?:\.\d+)?)(?<u>ms|h|m|s)")]
    private static partial Regex DurationPart();

    public static LimitObservation Parse(IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        var windows = new List<LimitWindow>();
        var unified = new Dictionary<string, Dictionary<string, string>>();
        var standard = new Dictionary<string, Dictionary<string, string>>();
        var codex = new Dictionary<string, Dictionary<string, string>>();
        var openai = new Dictionary<string, Dictionary<string, string>>();

        foreach (var (rawName, value) in headers)
        {
            var name = rawName.ToLowerInvariant();
            Match m;
            if ((m = UnifiedHeader().Match(name)).Success && m.Groups["w"].Value is not ("overage" or "fallback"))
                Add(unified, m.Groups["w"].Value, m.Groups["f"].Value, value);
            else if ((m = AnthropicStandardHeader().Match(name)).Success)
                Add(standard, m.Groups["k"].Value, m.Groups["f"].Value, value);
            else if ((m = CodexHeader().Match(name)).Success)
                Add(codex, m.Groups["w"].Value, m.Groups["f"].Value, value);
            else if ((m = OpenAIHeader().Match(name)).Success)
                Add(openai, m.Groups["k"].Value, m.Groups["f"].Value, value);
        }

        foreach (var (abbrev, f) in unified)
        {
            var key = UnifiedKey(abbrev);
            var util = Double(f.GetValueOrDefault("utilization")) is { } frac ? Math.Round(frac * 100, 2) : (double?)null;
            windows.Add(new LimitWindow
            {
                Key = key, Label = Label(key, null), Utilization = util,
                ResetsAt = UnixSeconds(f.GetValueOrDefault("reset")),
                WindowMinutes = DefaultWindowMinutes(key),
                Status = LimitStatuses.FromUtilization(util, f.GetValueOrDefault("status")),
                Source = LimitSources.Headers, UpdatedAt = now,
            });
        }

        foreach (var (kind, f) in standard)
            windows.Add(CountWindow(kind.Replace('-', '_'), f, ParseDate(f.GetValueOrDefault("reset")), now));

        foreach (var (name, f) in codex)
        {
            var minutes = Int(f.GetValueOrDefault("window-minutes"));
            var util = Double(f.GetValueOrDefault("used-percent"));
            var resets = UnixSeconds(f.GetValueOrDefault("reset-at"))
                ?? (Double(f.GetValueOrDefault("reset-after-seconds")) is { } s ? now.AddSeconds(s) : null);
            windows.Add(new LimitWindow
            {
                Key = name, Label = Label(name, minutes), Utilization = util, ResetsAt = resets, WindowMinutes = minutes,
                Status = LimitStatuses.FromUtilization(util), Source = LimitSources.Headers, UpdatedAt = now,
            });
        }

        foreach (var (kind, f) in openai)
            windows.Add(CountWindow(kind, f, ParseDuration(f.GetValueOrDefault("reset")) is { } d ? now + d : null, now));

        return new LimitObservation
        {
            Windows = windows,
            UnifiedStatus = headers.GetValueOrDefault("anthropic-ratelimit-unified-status"),
            RepresentativeClaim = headers.GetValueOrDefault("anthropic-ratelimit-unified-representative-claim"),
            OverageStatus = headers.GetValueOrDefault("anthropic-ratelimit-unified-overage-status"),
            LimitReached = headers.GetValueOrDefault("anthropic-ratelimit-unified-status") == "rejected",
        };
    }

    private static LimitWindow CountWindow(string key, Dictionary<string, string> f, DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        var limit = Double(f.GetValueOrDefault("limit"));
        var remaining = Double(f.GetValueOrDefault("remaining"));
        double? used = limit is { } l && remaining is { } r ? Math.Max(0, l - r) : null;
        double? util = limit is > 0 && used is { } u ? Math.Round(u / limit.Value * 100, 2) : null;
        return new LimitWindow
        {
            Key = key, Label = Label(key, 1), Utilization = util, Used = used, Limit = limit, Remaining = remaining,
            ResetsAt = resetsAt, WindowMinutes = 1,
            Status = remaining is <= 0 ? LimitStatuses.Exhausted : LimitStatuses.FromUtilization(util),
            Source = LimitSources.Headers, UpdatedAt = now,
        };
    }

    public static string UnifiedKey(string abbrev) => abbrev switch
    {
        "5h" => "five_hour",
        "7d" => "seven_day",
        _ when abbrev.StartsWith("7d_") => "seven_day_" + abbrev[3..],
        _ => abbrev,
    };

    public static int? DefaultWindowMinutes(string key) => key switch
    {
        "five_hour" => 300,
        _ when key.StartsWith("seven_day") => 10080,
        _ => null,
    };

    public static string Label(string key, int? windowMinutes) => key switch
    {
        "five_hour" => "Session (5h)",
        "seven_day" => "Weekly (all models)",
        "seven_day_opus" => "Weekly (Opus)",
        "seven_day_sonnet" => "Weekly (Sonnet)",
        "seven_day_oauth_apps" => "Weekly (OAuth apps)",
        "requests" => "Requests / min",
        "tokens" => "Tokens / min",
        "input_tokens" => "Input tokens / min",
        "output_tokens" => "Output tokens / min",
        "primary" or "secondary" => (windowMinutes ?? DefaultCodexMinutes(key)) switch
        {
            300 => "Session (5h)",
            10080 => "Weekly",
            { } m when m % 1440 == 0 => $"{m / 1440}-day window",
            { } m when m % 60 == 0 => $"{m / 60}h window",
            { } m => $"{m}-minute window",
        },
        _ => key.Replace('_', ' '),
    };

    private static int DefaultCodexMinutes(string key) => key == "primary" ? 300 : 10080;

    private static void Add(Dictionary<string, Dictionary<string, string>> map, string group, string field, string value)
    {
        if (!map.TryGetValue(group, out var f)) map[group] = f = new Dictionary<string, string>();
        f[field] = value.Split(',')[0].Trim();
    }

    public static double? Double(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;

    public static DateTimeOffset? UnixSeconds(string? s) =>
        long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? DateTimeOffset.FromUnixTimeSeconds(v) : null;

    public static DateTimeOffset? ParseDate(string? s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>Parses OpenAI reset durations such as <c>1m30s</c>, <c>6ms</c>, <c>0.5s</c>, <c>2h</c>.</summary>
    public static TimeSpan? ParseDuration(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var matches = DurationPart().Matches(s);
        if (matches.Count == 0) return null;
        var total = TimeSpan.Zero;
        foreach (Match m in matches)
        {
            var n = double.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture);
            total += m.Groups["u"].Value switch
            {
                "h" => TimeSpan.FromHours(n),
                "m" => TimeSpan.FromMinutes(n),
                "s" => TimeSpan.FromSeconds(n),
                _ => TimeSpan.FromMilliseconds(n),
            };
        }
        return total;
    }
}
