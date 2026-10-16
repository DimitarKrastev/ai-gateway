namespace Gateway.Usage;

/// <summary>USD per million tokens. For OpenAI, cache writes are free and cached input is billed at <see cref="CacheRead"/>.</summary>
public sealed record ModelPrice(double Input, double Output, double CacheRead, double CacheWrite);

/// <summary>
/// API list prices used to compute cost — for subscription accounts this is the API-equivalent value of the usage.
/// Approximate; matched by the longest model-name prefix, then by family. Override or extend via configuration:
/// <c>Gateway:Pricing:&lt;model-prefix&gt;:{Input,Output,CacheRead,CacheWrite}</c>.
/// </summary>
public sealed class PricingTable
{
    public static readonly IReadOnlyDictionary<string, ModelPrice> Defaults = new Dictionary<string, ModelPrice>
    {
        // Anthropic (cache write = 5-minute TTL rate).
        ["claude-opus-4-5"] = new(5, 25, 0.5, 6.25),
        ["claude-opus-4-6"] = new(5, 25, 0.5, 6.25),
        ["claude-opus-4-1"] = new(15, 75, 1.5, 18.75),
        ["claude-opus-4"] = new(15, 75, 1.5, 18.75),
        ["claude-opus-5"] = new(5, 25, 0.5, 6.25),
        ["claude-fable"] = new(5, 25, 0.5, 6.25),
        ["claude-sonnet-4"] = new(3, 15, 0.3, 3.75),
        ["claude-sonnet-5"] = new(3, 15, 0.3, 3.75),
        ["claude-3-7-sonnet"] = new(3, 15, 0.3, 3.75),
        ["claude-3-5-sonnet"] = new(3, 15, 0.3, 3.75),
        ["claude-haiku-4-5"] = new(1, 5, 0.1, 1.25),
        ["claude-haiku-5"] = new(1, 5, 0.1, 1.25),
        ["claude-3-5-haiku"] = new(0.8, 4, 0.08, 1),
        ["claude-3-haiku"] = new(0.25, 1.25, 0.03, 0.3),
        ["claude-3-opus"] = new(15, 75, 1.5, 18.75),
        // OpenAI.
        ["gpt-5.2"] = new(1.75, 14, 0.175, 0),
        ["gpt-5.1-codex-mini"] = new(0.25, 2, 0.025, 0),
        ["gpt-5.1"] = new(1.25, 10, 0.125, 0),
        ["gpt-5-codex"] = new(1.25, 10, 0.125, 0),
        ["gpt-5-mini"] = new(0.25, 2, 0.025, 0),
        ["gpt-5-nano"] = new(0.05, 0.4, 0.005, 0),
        ["gpt-5-pro"] = new(15, 120, 15, 0),
        ["gpt-5"] = new(1.25, 10, 0.125, 0),
        ["codex-mini"] = new(1.5, 6, 0.375, 0),
        ["gpt-4.1-nano"] = new(0.1, 0.4, 0.025, 0),
        ["gpt-4.1-mini"] = new(0.4, 1.6, 0.1, 0),
        ["gpt-4.1"] = new(2, 8, 0.5, 0),
        ["gpt-4o-mini"] = new(0.15, 0.6, 0.075, 0),
        ["gpt-4o"] = new(2.5, 10, 1.25, 0),
        ["o4-mini"] = new(1.1, 4.4, 0.275, 0),
        ["o3-mini"] = new(1.1, 4.4, 0.55, 0),
        ["o3"] = new(2, 8, 0.5, 0),
    };

    private static readonly (string Family, ModelPrice Price)[] Families =
    [
        ("opus", new(5, 25, 0.5, 6.25)),
        ("sonnet", new(3, 15, 0.3, 3.75)),
        ("haiku", new(1, 5, 0.1, 1.25)),
        ("codex", new(1.25, 10, 0.125, 0)),
    ];

    private readonly KeyValuePair<string, ModelPrice>[] _byPrefix;

    public PricingTable(IConfiguration configuration)
    {
        var table = new Dictionary<string, ModelPrice>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var section in configuration.GetSection("Gateway:Pricing").GetChildren())
        {
            var baseline = table.GetValueOrDefault(section.Key) ?? new ModelPrice(0, 0, 0, 0);
            table[section.Key] = new ModelPrice(
                section.GetValue("Input", baseline.Input), section.GetValue("Output", baseline.Output),
                section.GetValue("CacheRead", baseline.CacheRead), section.GetValue("CacheWrite", baseline.CacheWrite));
        }
        _byPrefix = table.OrderByDescending(kv => kv.Key.Length).ToArray();
    }

    public ModelPrice? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var m = model.Trim().ToLowerInvariant();
        // Tolerate vendor prefixes such as "anthropic/claude-…" or "us.anthropic.claude-…".
        var slash = m.LastIndexOf('/');
        if (slash >= 0) m = m[(slash + 1)..];
        var claude = m.IndexOf("claude-", StringComparison.Ordinal);
        if (claude > 0) m = m[claude..];

        foreach (var (prefix, price) in _byPrefix)
            if (m.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return price;
        foreach (var (family, price) in Families)
            if (m.Contains(family)) return price;
        return null;
    }

    public double Cost(ModelPrice price, ResponseUsage u) =>
        (u.InputTokens * price.Input + u.OutputTokens * price.Output
         + u.CacheReadTokens * price.CacheRead + u.CacheWriteTokens * price.CacheWrite) / 1_000_000d;
}
