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
        };
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
