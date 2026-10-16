using System.Security.Cryptography;
using System.Text;
using Gateway.Infrastructure;

namespace Gateway.Usage;

/// <summary>
/// USD per million tokens. <see cref="CacheWrite"/> is the 5-minute-TTL write rate; 1-hour-TTL writes cost
/// <see cref="CacheWrite1hRate"/> (2x input unless set). For OpenAI, cache writes are free and cached input is billed at
/// <see cref="CacheRead"/>.
/// </summary>
public sealed record ModelPrice(double Input, double Output, double CacheRead, double CacheWrite, double? CacheWrite1h = null)
{
    public double CacheWrite1hRate => CacheWrite1h ?? (CacheWrite > 0 ? Input * 2 : 0);
}

/// <summary>
/// API list prices used to compute cost — for subscription accounts this is the API-equivalent value of the usage.
/// Approximate; matched by the longest model-name prefix, then by family. Override or extend via configuration:
/// <c>Gateway:Pricing:&lt;model-prefix&gt;:{Input,Output,CacheRead,CacheWrite}</c>.
/// </summary>
public sealed class PricingTable
{
    public static readonly IReadOnlyDictionary<string, ModelPrice> Defaults = new Dictionary<string, ModelPrice>
    {
        // Anthropic (cache write = 5-minute TTL rate, 1.25x input; 1-hour writes are 2x input). Anthropic list prices.
        ["claude-opus-4-5"] = new(5, 25, 0.5, 6.25),
        ["claude-opus-4-6"] = new(5, 25, 0.5, 6.25),
        ["claude-opus-4-7"] = new(5, 25, 0.5, 6.25),
        ["claude-opus-4-8"] = new(5, 25, 0.5, 6.25),
        ["claude-opus-4-1"] = new(15, 75, 1.5, 18.75),
        ["claude-opus-4"] = new(15, 75, 1.5, 18.75),
        ["claude-opus-5-5"] = new(4, 20, 0.2, 5),
        ["claude-opus-5"] = new(5, 25, 0.5, 6.25),
        ["claude-fable-5-1"] = new(10, 50, 0.25, 12.5),
        ["claude-mythos-5-1"] = new(10, 50, 0.25, 12.5),
        ["claude-fable"] = new(10, 50, 1, 12.5),
        ["claude-sonnet-4"] = new(3, 15, 0.3, 3.75),
        ["claude-sonnet-5"] = new(2, 10, 0.2, 2.5),
        ["claude-3-7-sonnet"] = new(3, 15, 0.3, 3.75),
        ["claude-3-5-sonnet"] = new(3, 15, 0.3, 3.75),
        ["claude-haiku-4-5"] = new(1, 5, 0.1, 1.25),
        ["claude-haiku-5"] = new(0.1, 0.5, 0.01, 0.125),
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
        ("sonnet", new(2, 10, 0.2, 2.5)),
        ("haiku", new(1, 5, 0.1, 1.25)),
        ("codex", new(1.25, 10, 0.125, 0)),
    ];

    private readonly KeyValuePair<string, ModelPrice>[] _byPrefix;

    /// <summary>Identifies the effective table; stored costs are recomputed when it changes (<see cref="RepriceIfChanged"/>).</summary>
    public string Fingerprint { get; }

    public PricingTable(IConfiguration configuration)
    {
        var table = new Dictionary<string, ModelPrice>(Defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var section in configuration.GetSection("Gateway:Pricing").GetChildren())
        {
            var baseline = table.GetValueOrDefault(section.Key) ?? new ModelPrice(0, 0, 0, 0);
            table[section.Key] = new ModelPrice(
                section.GetValue("Input", baseline.Input), section.GetValue("Output", baseline.Output),
                section.GetValue("CacheRead", baseline.CacheRead), section.GetValue("CacheWrite", baseline.CacheWrite),
                section.GetValue("CacheWrite1h", baseline.CacheWrite1h));
        }
        _byPrefix = table.OrderByDescending(kv => kv.Key.Length).ToArray();
        var text = string.Join(";", _byPrefix.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value}"))
            + "|" + string.Join(";", Families.Select(f => $"{f.Family}={f.Price}"));
        Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
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
        Cost(price, u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheWriteTokens, u.CacheWrite1hTokens);

    public static double Cost(ModelPrice price, long input, long output, long cacheRead, long cacheWrite, long cacheWrite1h)
    {
        var write1h = Math.Clamp(cacheWrite1h, 0, cacheWrite);
        return (input * price.Input + output * price.Output + cacheRead * price.CacheRead
                + (cacheWrite - write1h) * price.CacheWrite + write1h * price.CacheWrite1hRate) / 1_000_000d;
    }

    public const string FingerprintSetting = "pricing.fingerprint";

    /// <summary>
    /// Recomputes the stored cost of every request from its recorded tokens when the effective price table changed since
    /// the last start (a corrected or newly configured price then applies to history too). Returns the rows updated.
    /// </summary>
    public int RepriceIfChanged(Database db)
    {
        if (db.GetSetting(FingerprintSetting) == Fingerprint) return 0;
        var updated = 0;
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        var models = new List<string?>();
        using (var list = conn.CreateCommand())
        {
            list.Transaction = tx;
            list.CommandText = "SELECT DISTINCT model FROM requests;";
            using var r = list.ExecuteReader();
            while (r.Read()) models.Add(r.IsDBNull(0) ? null : r.GetString(0));
        }
        foreach (var model in models)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            var where = model is null ? "model IS NULL" : "model = $model";
            if (model is not null) cmd.Parameters.AddWithValue("$model", model);
            // Only rows that carry token usage are priced (errors and count_tokens keep cost 0, unpriced).
            if (Find(model) is { } p)
            {
                cmd.CommandText = $"""
                    UPDATE requests SET priced = 1, cost_usd = (input_tokens * $in + output_tokens * $out + cache_read_tokens * $cr
                        + (cache_write_tokens - MIN(cache_write_1h_tokens, cache_write_tokens)) * $cw
                        + MIN(cache_write_1h_tokens, cache_write_tokens) * $cw1h) / 1000000.0
                    WHERE {where} AND (input_tokens + output_tokens + cache_read_tokens + cache_write_tokens) > 0;
                    """;
                cmd.Parameters.AddWithValue("$in", p.Input);
                cmd.Parameters.AddWithValue("$out", p.Output);
                cmd.Parameters.AddWithValue("$cr", p.CacheRead);
                cmd.Parameters.AddWithValue("$cw", p.CacheWrite);
                cmd.Parameters.AddWithValue("$cw1h", p.CacheWrite1hRate);
            }
            else
            {
                cmd.CommandText = $"UPDATE requests SET priced = 0, cost_usd = 0 WHERE {where};";
            }
            updated += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        db.SetSetting(FingerprintSetting, Fingerprint);
        return updated;
    }
}
