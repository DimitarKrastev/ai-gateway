using System.Collections.Concurrent;
using Gateway.Live;
using Gateway.Proxy;

namespace Gateway.Usage;

/// <summary>One upstream attempt as persisted in the <c>requests</c> table.</summary>
public sealed record RequestRecord
{
    public required string RequestId { get; init; }
    public required int Attempt { get; init; }
    public bool Retried { get; init; }
    public required string Provider { get; init; }
    public long? AccountId { get; init; }
    public string? AccountKind { get; init; }
    public string? SessionId { get; init; }
    public string? SessionSource { get; init; }
    public long ClientKeyId { get; init; }
    public required string Client { get; init; }
    public string? ClientVersion { get; init; }
    public string? AgentId { get; init; }
    public string? AgentRole { get; init; }
    public required string Method { get; init; }
    public required string Path { get; init; }
    public required string Endpoint { get; init; }
    public string? Model { get; init; }
    /// <summary>The model id the client asked for when it differs from what served it (bridged requests: the advertised id).</summary>
    public string? RequestedModel { get; init; }
    /// <summary>Protocol translation applied, e.g. <c>anthropic-&gt;openai</c>; null for pass-through traffic.</summary>
    public string? Bridge { get; init; }
    public bool Stream { get; init; }
    public int Status { get; init; }
    public string? ErrorType { get; init; }
    public string? StopReason { get; init; }
    public bool Cancelled { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheWriteTokens { get; init; }
    public long ReasoningTokens { get; init; }
    public long ContextTokens { get; init; }
    public int ToolCalls { get; init; }
    public IReadOnlyDictionary<string, int> Tools { get; init; } = new Dictionary<string, int>();
    public double CostUsd { get; init; }
    public bool Priced { get; init; }
    public long StartedAt { get; init; }
    public long? ResponseStartedAt { get; init; }
    public long? FirstByteAt { get; init; }
    /// <summary>First output (token delta) event of a stream.</summary>
    public long? FirstTokenAt { get; init; }
    public long CompletedAt { get; init; }
    /// <summary>Generation time used for throughput, see <see cref="Throughput"/>.</summary>
    public long? GenerationMs { get; init; }
    public string? RequestedSpeed { get; init; }
    public string? ReportedSpeed { get; init; }
    /// <summary>Normalized mode (<see cref="SpeedModes"/>); null for non-generation endpoints.</summary>
    public string? SpeedMode { get; init; }
    public long RequestBytes { get; init; }
    public long ResponseBytes { get; init; }
}

/// <summary>A request currently waiting on or streaming from an upstream.</summary>
public sealed record LiveRequest(
    string Key, string RequestId, string Provider, long AccountId, string AccountName, string SessionId,
    string? AgentId, string? AgentRole, string Client, string? Model, bool Stream, string Endpoint, long StartedAt,
    string? SpeedMode = null, string? RequestedModel = null);

public sealed class LiveRequests(LiveNotifier notifier)
{
    private readonly ConcurrentDictionary<string, LiveRequest> _items = new();
    public void Add(LiveRequest r) { _items[r.Key] = r; notifier.Notify(LiveTopic.InFlight); }
    public void Remove(string key) { if (_items.TryRemove(key, out _)) notifier.Notify(LiveTopic.InFlight); }
    public IReadOnlyList<LiveRequest> Snapshot() => _items.Values.OrderBy(r => r.StartedAt).ToList();
    public int Count => _items.Count;
}

/// <summary>Captures usage of every proxied attempt and records it; see <see cref="ResponseUsageParser"/>.</summary>
public sealed class UsageObserver(
    UsageStore store, AgentIdentifier agents, PricingTable pricing, LiveRequests live, LiveNotifier notifier, LiveMeter meter,
    TimeProvider time, ILogger<UsageObserver> log)
    : IRequestObserver
{
    private const string StateKey = "usage.state";

    private sealed class State
    {
        public required RequestInfo Info;
        public AgentIdentity? Agent;
        public ResponseUsageParser? Parser;
        public long? FirstByteAt;
        public long? FirstTokenAt;
        public string LiveKey = "";
        // Tokens already counted on the live throughput meter.
        public double MeteredOutput;
        public double MeteredPrompt;
    }

    public void OnRequestStarted(ProxiedRequestContext ctx)
    {
        var info = RequestInfo.Parse(ctx);
        var state = new State { Info = info, Agent = agents.Identify(ctx, info), LiveKey = $"{ctx.RequestId}:{ctx.Attempt}" };
        ctx.Items[StateKey] = state;
        live.Add(new LiveRequest(state.LiveKey, ctx.RequestId, ctx.Provider, ctx.Account.Id, ctx.Account.Name, ctx.SessionId,
            state.Agent?.Id, state.Agent?.Role, info.Client, info.Model, info.Stream, info.Endpoint, ctx.StartedAt.ToUnixTimeMilliseconds(),
            Endpoints.IsGeneration(info.Endpoint) ? SpeedModes.Resolve(info.RequestedSpeed, null) : null, ctx.Bridge?.RequestedModel));
    }

    public void OnResponseStarted(ProxiedRequestContext ctx)
    {
        if (GetState(ctx) is { } s) s.Parser = new ResponseUsageParser(s.Info.Endpoint, ctx.StatusCode, ctx.ResponseContentType);
    }

    public void OnResponseChunk(ProxiedRequestContext ctx, ReadOnlySpan<byte> chunk)
    {
        if (GetState(ctx) is not { } s) return;
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        s.FirstByteAt ??= now;
        s.Parser?.Feed(chunk);
        if (s.FirstTokenAt == null && s.Parser is { OutputStarted: true }) s.FirstTokenAt = now;
        if (s.Parser is { } p) Meter(s, p.StreamedChars / 4.0, p.Usage.ContextTokens, ctx.StartedAt.ToUnixTimeMilliseconds());
    }

    public Task OnCompletedAsync(ProxiedRequestContext ctx)
    {
        var s = GetState(ctx);
        if (s != null) live.Remove(s.LiveKey);
        try
        {
            var info = s?.Info ?? RequestInfo.Parse(ctx);
            var usage = s?.Parser?.Complete() ?? new ResponseUsage();
            // Settle the live meter on the reported totals: output not seen streaming (hidden reasoning, estimate error) is
            // spread over the generation time, prompt tokens reported only now are placed when the request was sent.
            if (s != null)
            {
                var now = time.GetUtcNow().ToUnixTimeMilliseconds();
                if (usage.OutputTokens > s.MeteredOutput)
                {
                    meter.AddOutputSpread(usage.OutputTokens - s.MeteredOutput, s.FirstByteAt ?? ctx.StartedAt.ToUnixTimeMilliseconds(), now);
                    s.MeteredOutput = usage.OutputTokens;
                }
                if (usage.ContextTokens > s.MeteredPrompt)
                {
                    meter.AddPrompt(usage.ContextTokens - s.MeteredPrompt, ctx.StartedAt.ToUnixTimeMilliseconds());
                    s.MeteredPrompt = usage.ContextTokens;
                }
            }
            var model = usage.Model ?? info.Model;
            var price = usage.HasUsage ? pricing.Find(model) : null;
            var agent = s != null ? s.Agent : agents.Identify(ctx, info);
            var stream = info.Stream || (ctx.ResponseContentType?.Contains("event-stream") ?? false);
            var startedAt = ctx.StartedAt.ToUnixTimeMilliseconds();
            var completedAt = (ctx.CompletedAt ?? time.GetUtcNow()).ToUnixTimeMilliseconds();
            var generation = Endpoints.IsGeneration(info.Endpoint)
                ? Throughput.GenerationMs(stream, startedAt, s?.FirstByteAt, s?.FirstTokenAt, completedAt)
                : null;
            store.Insert(new RequestRecord
            {
                RequestId = ctx.RequestId, Attempt = ctx.Attempt, Retried = ctx.RetriedByGateway, Provider = ctx.Provider,
                AccountId = ctx.Account.Id, AccountKind = ctx.Account.Kind,
                // count_tokens/models calls carry no session marker (Claude Code sends no metadata there); a derived id would
                // show up as a phantom session, so such rows are kept out of session analytics.
                SessionId = Endpoints.IsGeneration(info.Endpoint) || ctx.SessionSource != "derived" ? ctx.SessionId : null,
                SessionSource = ctx.SessionSource,
                ClientKeyId = ctx.ClientKeyId, Client = info.Client, ClientVersion = info.ClientVersion,
                AgentId = agent?.Id, AgentRole = agent?.Role, Method = ctx.Method, Path = ctx.Path, Endpoint = info.Endpoint,
                Model = model, RequestedModel = ctx.Bridge?.RequestedModel, Bridge = ctx.Bridge?.Kind, Stream = stream,
                Status = ctx.StatusCode, ErrorType = usage.ErrorType ?? (ctx.Error != null ? "upstream_error" : null),
                StopReason = usage.StopReason, Cancelled = ctx.ClientCancelled,
                InputTokens = usage.InputTokens, OutputTokens = usage.OutputTokens, CacheReadTokens = usage.CacheReadTokens,
                CacheWriteTokens = usage.CacheWriteTokens, ReasoningTokens = usage.ReasoningTokens, ContextTokens = usage.ContextTokens,
                ToolCalls = usage.ToolCalls, Tools = usage.Tools,
                CostUsd = price != null ? pricing.Cost(price, usage) : 0, Priced = price != null,
                StartedAt = startedAt, ResponseStartedAt = ctx.ResponseStartedAt?.ToUnixTimeMilliseconds(),
                FirstByteAt = s?.FirstByteAt, FirstTokenAt = s?.FirstTokenAt, CompletedAt = completedAt, GenerationMs = generation,
                RequestedSpeed = Cap(info.RequestedSpeed), ReportedSpeed = Cap(usage.ReportedSpeed),
                SpeedMode = Endpoints.IsGeneration(info.Endpoint) ? Cap(SpeedModes.Resolve(info.RequestedSpeed, usage.ReportedSpeed)) : null,
                RequestBytes = ctx.RequestBody.Length, ResponseBytes = ctx.ResponseBytes,
            });
            notifier.Notify(LiveTopic.Usage);
        }
        catch (Exception e)
        {
            log.LogError(e, "Failed to record usage for request {RequestId}", ctx.RequestId);
        }
        return Task.CompletedTask;
    }

    /// <summary>Streamed output counts now; prompt tokens count at the second the request was sent, whenever reported.</summary>
    private void Meter(State s, double output, double prompt, long startedAtMs)
    {
        if (output > s.MeteredOutput) { meter.AddOutput(output - s.MeteredOutput); s.MeteredOutput = output; }
        if (prompt > s.MeteredPrompt) { meter.AddPrompt(prompt - s.MeteredPrompt, startedAtMs); s.MeteredPrompt = prompt; }
    }

    /// <summary>Speed values come from client/provider payloads; keep them short.</summary>
    private static string? Cap(string? v) => v is { Length: > 32 } ? v[..32] : v;

    private static State? GetState(ProxiedRequestContext ctx) =>
        ctx.Items.TryGetValue(StateKey, out var s) ? (State)s : null;
}
