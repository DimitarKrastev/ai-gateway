using System.Text;
using System.Text.Json;

namespace Gateway.Accounts;

/// <summary>Create/update body for <c>/api/accounts</c>. Secrets can be given as fields or as a pasted blob.</summary>
public sealed record AccountInput(
    string? Name = null, string? Kind = null, bool? Enabled = null, int? Priority = null,
    string? ApiKey = null, string? AccessToken = null, string? RefreshToken = null, DateTimeOffset? ExpiresAt = null,
    string? ChatGptAccountId = null, string? CredentialsJson = null)
{
    public bool HasSecrets =>
        !string.IsNullOrWhiteSpace(ApiKey) || !string.IsNullOrWhiteSpace(AccessToken) ||
        !string.IsNullOrWhiteSpace(RefreshToken) || !string.IsNullOrWhiteSpace(CredentialsJson);
}

/// <summary>
/// Turns what a user pastes into credentials: an API key, a long-lived <c>claude setup-token</c> token, the Claude Code
/// credentials file (<c>{"claudeAiOauth":{...}}</c>), the Codex <c>~/.codex/auth.json</c>, or individual fields.
/// </summary>
public static class CredentialParser
{
    public sealed record Result(string? Kind, AccountCredentials? Credentials, string? Error);

    public static Result Parse(string? kind, AccountInput input)
    {
        if (kind != null && !AccountKinds.All.Contains(kind))
            return new(null, null, $"Unknown kind '{kind}'. Expected one of: {string.Join(", ", AccountKinds.All)}.");

        string? apiKey = null, access = null, refresh = null, idToken = null, accountId = null;
        DateTimeOffset? expires = null;

        var blob = input.CredentialsJson?.Trim();
        if (!string.IsNullOrEmpty(blob))
        {
            if (!blob.StartsWith('{'))
            {
                kind ??= InferKindFromSecret(blob);
                if (kind == null) return new(null, null, "Could not tell what kind of credential this is; choose a kind.");
                if (AccountKinds.IsOAuth(kind)) access = blob; else apiKey = blob;
            }
            else
            {
                JsonElement root;
                try { root = JsonDocument.Parse(blob).RootElement; }
                catch (JsonException e) { return new(null, null, "Credentials JSON is not valid JSON: " + e.Message); }

                var obj = root;
                if (root.TryGetProperty("claudeAiOauth", out var claude) && claude.ValueKind == JsonValueKind.Object)
                {
                    obj = claude;
                    kind ??= AccountKinds.AnthropicOAuth;
                }
                else if (root.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
                {
                    obj = tokens;
                    kind ??= AccountKinds.ChatGptOAuth;
                }
                else if (Str(root, "OPENAI_API_KEY") is { Length: > 0 } codexKey)
                {
                    apiKey = codexKey;
                    kind ??= AccountKinds.OpenAIApiKey;
                }

                access = Str(obj, "accessToken", "access_token");
                refresh = Str(obj, "refreshToken", "refresh_token");
                idToken = Str(obj, "idToken", "id_token");
                accountId = Str(obj, "account_id", "accountId", "chatgpt_account_id");
                apiKey ??= Str(obj, "apiKey", "api_key");
                expires = Time(obj, "expiresAt", "expires_at");
            }
        }

        apiKey = NonEmpty(input.ApiKey) ?? apiKey;
        access = NonEmpty(input.AccessToken) ?? access;
        refresh = NonEmpty(input.RefreshToken) ?? refresh;
        accountId = NonEmpty(input.ChatGptAccountId) ?? accountId;
        expires = input.ExpiresAt ?? expires;

        kind ??= apiKey != null ? InferKindFromSecret(apiKey) : access != null ? InferKindFromSecret(access) : null;
        if (kind == null) return new(null, null, "Choose an account kind.");

        if (!AccountKinds.IsOAuth(kind))
        {
            apiKey ??= access;
            if (string.IsNullOrEmpty(apiKey)) return new(kind, null, "An API key is required.");
            return new(kind, new AccountCredentials { ApiKey = apiKey }, null);
        }

        if (access == null && refresh == null) return new(kind, null, "An access token or refresh token is required.");
        expires ??= access != null ? JwtExpiry(access) : null;
        if (access == null) expires = DateTimeOffset.UnixEpoch; // force a refresh before first use

        if (kind == AccountKinds.ChatGptOAuth)
        {
            accountId ??= JwtChatGptAccountId(access) ?? JwtChatGptAccountId(idToken);
            if (string.IsNullOrEmpty(accountId))
                return new(kind, null, "ChatGPT account id not found: paste the whole ~/.codex/auth.json or give chatGptAccountId.");
        }

        return new(kind, new AccountCredentials
        {
            AccessToken = access, RefreshToken = refresh, ExpiresAt = expires, IdToken = idToken,
            ChatGptAccountId = kind == AccountKinds.ChatGptOAuth ? accountId : null,
        }, null);
    }

    public static string? InferKindFromSecret(string s) =>
        s.StartsWith("sk-ant-oat") ? AccountKinds.AnthropicOAuth
        : s.StartsWith("sk-ant-") ? AccountKinds.AnthropicApiKey
        : s.StartsWith("sk-") ? AccountKinds.OpenAIApiKey
        : JwtChatGptAccountId(s) != null ? AccountKinds.ChatGptOAuth
        : null;

    public static DateTimeOffset? JwtExpiry(string? jwt) =>
        JwtPayload(jwt) is { } p && p.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s) : null;

    public static string? JwtChatGptAccountId(string? jwt) =>
        JwtPayload(jwt) is { } p && p.TryGetProperty("https://api.openai.com/auth", out var auth) && auth.ValueKind == JsonValueKind.Object
            ? Str(auth, "chatgpt_account_id") : null;

    private static JsonElement? JwtPayload(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 }) return null;
        try
        {
            var b64 = parts[1].Replace('-', '+').Replace('_', '/');
            b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4) % 4, '=');
            var root = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(b64))).RootElement;
            return root.ValueKind == JsonValueKind.Object ? root : null;
        }
        catch { return null; }
    }

    private static string? Str(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
            if (obj.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s)
                return s;
        return null;
    }

    private static DateTimeOffset? Time(JsonElement obj, params string[] names)
    {
        foreach (var n in names)
        {
            if (!obj.TryGetProperty(n, out var v)) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var num))
                return num > 100_000_000_000 ? DateTimeOffset.FromUnixTimeMilliseconds(num) : DateTimeOffset.FromUnixTimeSeconds(num);
            if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var t)) return t;
        }
        return null;
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
