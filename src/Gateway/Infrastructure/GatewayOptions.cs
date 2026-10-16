namespace Gateway.Infrastructure;

/// <summary>
/// Runtime configuration. Each value comes from configuration key <c>Gateway:*</c>
/// first, then the matching environment variable, then a default.
/// </summary>
public sealed class GatewayOptions
{
    public int Port { get; init; } = 6200;
    public string BindAddress { get; init; } = "127.0.0.1";
    public string DataDir { get; init; } = "data";
    public string WebRoot { get; init; } = "";
    public string? AdminPassword { get; init; }
    public UpstreamOptions Upstreams { get; init; } = new();
    /// <summary>How often usage endpoints of OAuth accounts are polled for limits; 0 disables polling.</summary>
    public double LimitsRefreshMinutes { get; init; } = 5;

    public string DatabasePath => Path.Combine(DataDir, "gateway.db");

    public static GatewayOptions FromConfiguration(IConfiguration config, string contentRoot)
    {
        string? Get(string key, string env) =>
            NonEmpty(config[$"Gateway:{key}"]) ?? NonEmpty(Environment.GetEnvironmentVariable(env));

        var port = int.TryParse(Get("Port", "GATEWAY_PORT"), out var p) ? p : 6200;
        var dataDir = Path.GetFullPath(Get("DataDir", "GATEWAY_DATA_DIR") ?? "data");
        var webRoot = Get("WebRoot", "GATEWAY_WEB_ROOT")
            ?? (Directory.Exists(Path.Combine(contentRoot, "wwwroot"))
                ? Path.Combine(contentRoot, "wwwroot")
                : Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        return new GatewayOptions
        {
            Port = port,
            BindAddress = Get("BindAddress", "GATEWAY_BIND") ?? "127.0.0.1",
            DataDir = dataDir,
            WebRoot = Path.GetFullPath(webRoot),
            AdminPassword = Get("AdminPassword", "GATEWAY_ADMIN_PASSWORD"),
            LimitsRefreshMinutes = double.TryParse(Get("LimitsRefreshMinutes", "GATEWAY_LIMITS_REFRESH_MINUTES"),
                System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? Math.Max(0, m) : 5,
            Upstreams = new UpstreamOptions
            {
                AnthropicApi = Url(Get("Upstreams:AnthropicApi", "GATEWAY_UPSTREAM_ANTHROPIC_API"), UpstreamOptions.Defaults.AnthropicApi),
                AnthropicAuth = Url(Get("Upstreams:AnthropicAuth", "GATEWAY_UPSTREAM_ANTHROPIC_AUTH"), UpstreamOptions.Defaults.AnthropicAuth),
                OpenAIApi = Url(Get("Upstreams:OpenAIApi", "GATEWAY_UPSTREAM_OPENAI_API"), UpstreamOptions.Defaults.OpenAIApi),
                ChatGptBackend = Url(Get("Upstreams:ChatGptBackend", "GATEWAY_UPSTREAM_CHATGPT"), UpstreamOptions.Defaults.ChatGptBackend),
                OpenAIAuth = Url(Get("Upstreams:OpenAIAuth", "GATEWAY_UPSTREAM_OPENAI_AUTH"), UpstreamOptions.Defaults.OpenAIAuth),
            },
        };
    }

    private static string Url(string? value, string fallback) => (value ?? fallback).TrimEnd('/');

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>
/// Provider base URLs (no trailing slash). Point all of them at the fake providers in tests and e2e runs.
/// </summary>
public sealed class UpstreamOptions
{
    public static readonly UpstreamOptions Defaults = new();

    /// <summary>Anthropic Messages API (<c>/v1/messages</c>, <c>/api/oauth/usage</c>).</summary>
    public string AnthropicApi { get; init; } = "https://api.anthropic.com";
    /// <summary>Anthropic OAuth token endpoint host (<c>/v1/oauth/token</c>).</summary>
    public string AnthropicAuth { get; init; } = "https://console.anthropic.com";
    /// <summary>OpenAI API (<c>/v1/responses</c>, <c>/v1/chat/completions</c>).</summary>
    public string OpenAIApi { get; init; } = "https://api.openai.com";
    /// <summary>ChatGPT backend used by Codex with a ChatGPT login (<c>/backend-api/codex/responses</c>).</summary>
    public string ChatGptBackend { get; init; } = "https://chatgpt.com";
    /// <summary>OpenAI OAuth token endpoint host (<c>/oauth/token</c>).</summary>
    public string OpenAIAuth { get; init; } = "https://auth.openai.com";
}
