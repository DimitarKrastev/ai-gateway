namespace Gateway.Accounts;

public static class Providers
{
    public const string Anthropic = "anthropic";
    public const string OpenAI = "openai";
}

public static class AccountKinds
{
    public const string AnthropicApiKey = "anthropic_api_key";
    public const string AnthropicOAuth = "anthropic_oauth";
    public const string OpenAIApiKey = "openai_api_key";
    public const string ChatGptOAuth = "chatgpt_oauth";

    public static readonly string[] All = [AnthropicApiKey, AnthropicOAuth, OpenAIApiKey, ChatGptOAuth];

    public static string? ProviderOf(string? kind) => kind switch
    {
        AnthropicApiKey or AnthropicOAuth => Providers.Anthropic,
        OpenAIApiKey or ChatGptOAuth => Providers.OpenAI,
        _ => null,
    };

    public static bool IsOAuth(string kind) => kind is AnthropicOAuth or ChatGptOAuth;
}

/// <summary>Secret material of a linked account. Stored encrypted; never returned by the API.</summary>
public sealed record AccountCredentials
{
    public string? ApiKey { get; init; }
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? IdToken { get; init; }
    public string? ChatGptAccountId { get; init; }

    /// <summary>Masked form of the primary secret, e.g. <c>sk-ant-api…wxyz</c>.</summary>
    public string Hint()
    {
        var s = ApiKey ?? AccessToken ?? RefreshToken;
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= 16 ? "****" : s[..Math.Min(10, s.Length - 8)] + "…" + s[^4..];
    }
}

public sealed record LinkedAccount(
    long Id, string Name, string Provider, string Kind, bool Enabled, int Priority,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, AccountCredentials Credentials)
{
    public bool IsOAuth => AccountKinds.IsOAuth(Kind);
}
