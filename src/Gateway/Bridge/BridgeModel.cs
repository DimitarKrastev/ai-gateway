using System.Text.RegularExpressions;

namespace Gateway.Bridge;

/// <summary>
/// An OpenAI model exposed to Claude Code under an Anthropic-style id (see <see cref="BridgeModelCatalog"/>).
/// </summary>
/// <param name="AdvertisedId">Id Claude Code sees and sends as <c>model</c> (matches <see cref="BridgeModelIds.DiscoveryFilter"/>).</param>
/// <param name="UpstreamModel">The OpenAI model id sent upstream.</param>
/// <param name="AccountIds">OpenAI accounts whose model list contains it; empty = unknown (manual entry), any OpenAI account.</param>
/// <param name="SupportedEfforts">Reasoning efforts the provider lists for it (empty = unknown).</param>
public sealed record BridgeModel(
    string AdvertisedId, string UpstreamModel, string DisplayName, string? Description,
    IReadOnlyList<long> AccountIds, IReadOnlyList<string> SupportedEfforts, string? DefaultEffort, DateTimeOffset CreatedAt);

public static partial class BridgeModelIds
{
    public const string DefaultPrefix = "anthropic-openai-";
    public const int MaxLength = 128;

    /// <summary>Claude Code's gateway model discovery keeps only ids matching this (case-insensitive).</summary>
    [GeneratedRegex("(claude|anthropic)", RegexOptions.IgnoreCase)]
    public static partial Regex DiscoveryFilter();

    [GeneratedRegex("^[A-Za-z0-9._:-]+$")]
    private static partial Regex Charset();

    [GeneratedRegex("[^A-Za-z0-9._:-]+")]
    private static partial Regex InvalidChars();

    /// <summary>Trailing context-window style suffix Claude Code may append, e.g. <c>[1m]</c>.</summary>
    [GeneratedRegex(@"\[[^\]]*\]\s*$")]
    private static partial Regex Suffix();

    public static string DefaultFor(string upstreamModel) =>
        DefaultPrefix + InvalidChars().Replace(upstreamModel.Trim(), "-").Trim('-');

    public static string Normalize(string requested) => Suffix().Replace(requested.Trim(), "").Trim();

    /// <summary>Null when valid, else a user-facing reason.</summary>
    public static string? Validate(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "The advertised id is required.";
        if (id.Length > MaxLength) return $"The advertised id must be at most {MaxLength} characters.";
        if (!Charset().IsMatch(id)) return "Use only letters, digits, '.', '_', ':' and '-'.";
        if (!DiscoveryFilter().IsMatch(id))
            return "The id must contain \"claude\" or \"anthropic\" — Claude Code's model discovery ignores other ids.";
        return null;
    }
}
