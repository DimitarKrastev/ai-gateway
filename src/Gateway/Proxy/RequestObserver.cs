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
    /// <summary>
    /// Set when an Anthropic Messages request was translated for an OpenAI model (see <c>Gateway.Bridge</c>). Then
    /// <see cref="Provider"/>, <see cref="Account"/>, <see cref="UpstreamUri"/>, <see cref="RequestBody"/> and the observed
    /// response are the upstream OpenAI ones, while <see cref="Path"/> and <see cref="RequestHeaders"/> are the client's.
    /// </summary>
    public BridgedRequest? Bridge { get; init; }

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

/// <param name="RequestedModel">The advertised model id the client asked for.</param>
/// <param name="UpstreamModel">The OpenAI model the request was sent to.</param>
/// <param name="ClientBody">The client's original (Anthropic Messages) request body.</param>
public sealed record BridgedRequest(string RequestedModel, string UpstreamModel, byte[] ClientBody)
{
    public const string AnthropicToOpenAI = "anthropic->openai";
    public string Kind => AnthropicToOpenAI;
}

/// <summary>
/// Sees every upstream attempt as it streams. Register any number as <c>IRequestObserver</c> singletons.
/// Calls are sequential per request; exceptions are logged and swallowed. <see cref="OnResponseChunk"/> receives
/// response bytes in order without the proxy buffering the stream.
/// </summary>
public interface IRequestObserver
{
    /// <summary>Called before each upstream attempt is sent.</summary>
    void OnRequestStarted(ProxiedRequestContext ctx) { }
    void OnResponseStarted(ProxiedRequestContext ctx) { }
    void OnResponseChunk(ProxiedRequestContext ctx, ReadOnlySpan<byte> chunk) { }
    Task OnCompletedAsync(ProxiedRequestContext ctx) => Task.CompletedTask;
}
