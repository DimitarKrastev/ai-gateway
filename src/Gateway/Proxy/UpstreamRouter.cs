using Gateway.Accounts;
using Gateway.Infrastructure;

namespace Gateway.Proxy;

/// <summary>
/// Maps a gateway path (after <c>/anthropic/</c> or <c>/openai/</c>) to the upstream URL for a given account kind.
/// <list type="bullet">
/// <item><c>/anthropic/{path}</c> → Anthropic API <c>/{path}</c> for both API-key and subscription accounts.</item>
/// <item><c>/openai/v1/{path}</c> → OpenAI API <c>/v1/{path}</c> for API-key accounts; for ChatGPT accounts
/// <c>v1/responses*</c> and <c>v1/models</c> map to <c>/backend-api/codex/{path}</c>.</item>
/// <item><c>/openai/backend-api/{path}</c> → ChatGPT backend <c>/backend-api/{path}</c> (ChatGPT accounts only).</item>
/// </list>
/// </summary>
public sealed class UpstreamRouter(GatewayOptions options)
{
    private UpstreamOptions Up => options.Upstreams;

    public static bool IsEligible(string kind, string path)
    {
        path = path.TrimStart('/');
        return kind switch
        {
            AccountKinds.ChatGptOAuth => path.StartsWith("backend-api/") || IsUnder(path, "v1/responses") || IsUnder(path, "v1/models"),
            AccountKinds.OpenAIApiKey => !path.StartsWith("backend-api/"),
            _ => true,
        };
    }

    public Uri BuildUri(string kind, string path, string query)
    {
        path = path.TrimStart('/');
        var url = kind switch
        {
            AccountKinds.AnthropicApiKey or AccountKinds.AnthropicOAuth => $"{Up.AnthropicApi}/{path}",
            AccountKinds.OpenAIApiKey => $"{Up.OpenAIApi}/{path}",
            _ when path.StartsWith("backend-api/") => $"{Up.ChatGptBackend}/{path}",
            _ => $"{Up.ChatGptBackend}/backend-api/codex/{(path.StartsWith("v1/") ? path[3..] : path)}",
        };
        return new Uri(url + query);
    }

    private static bool IsUnder(string path, string prefix) =>
        path == prefix || path.StartsWith(prefix + "/") || path.StartsWith(prefix + "?");
}
