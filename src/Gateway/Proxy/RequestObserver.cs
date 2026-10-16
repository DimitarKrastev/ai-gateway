using Gateway.Accounts;

namespace Gateway.Proxy;

/// <summary>
/// One upstream attempt of a proxied request. A client request normally has one attempt; when the gateway fails over
/// after a 429 the earlier attempts have <see cref="RetriedByGateway"/> set and their bodies never reach the client.
/// </summary>
public sealed class ProxiedRequestContext
{
    public required string RequestId { get; init; }
    public required int Attempt { get; init; }
    public required string Provider { get; init; }
    public required LinkedAccount Account { get; init; }
    public required string SessionId { get; init; }
    public required string SessionSource { get; init; }
    public bool NewSession { get; init; }
    public bool Rebound { get; init; }
    public required long ClientKeyId { get; init; }
    public required string Method { get; init; }
    /// <summary>Path after the provider prefix, e.g. <c>v1/messages</c>.</summary>
    public required string Path { get; init; }
    public required string Query { get; init; }
    public required Uri UpstreamUri { get; init; }
    public required IReadOnlyDictionary<string, string> RequestHeaders { get; init; }
    public required byte[] RequestBody { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    public int StatusCode { get; set; }
    public DateTimeOffset? ResponseStartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    /// <summary>Upstream response headers (lower-cased names, comma-joined values).</summary>
    public IReadOnlyDictionary<string, string> ResponseHeaders { get; set; } = new Dictionary<string, string>();
    public string? ResponseContentType => ResponseHeaders.GetValueOrDefault("content-type");
    public long ResponseBytes { get; set; }
    public bool RetriedByGateway { get; set; }
    public bool ClientCancelled { get; set; }
    /// <summary>Set when the upstream could not be reached or the stream broke.</summary>
    public string? Error { get; set; }

    /// <summary>Per-request scratch space for observers (e.g. an SSE parser).</summary>
    public Dictionary<string, object> Items { get; } = new();
}

/// <summary>
/// Sees every upstream attempt as it streams. Register any number as <c>IRequestObserver</c> singletons.
/// Calls are sequential per request; exceptions are logged and swallowed. <see cref="OnResponseChunk"/> receives
/// response bytes in order without the proxy buffering the stream.
/// </summary>
public interface IRequestObserver
{
    void OnResponseStarted(ProxiedRequestContext ctx) { }
    void OnResponseChunk(ProxiedRequestContext ctx, ReadOnlySpan<byte> chunk) { }
    Task OnCompletedAsync(ProxiedRequestContext ctx) => Task.CompletedTask;
}
