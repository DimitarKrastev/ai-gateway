using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;

namespace Gateway.Bridge;

/// <summary>
/// Writes an OpenAI Responses upstream response to an Anthropic Messages client: error statuses become Anthropic error
/// JSON, SSE is translated event by event as it arrives (never buffered), and a stream for a non-streaming client (the Codex
/// backend always streams) is aggregated into one message. Every upstream byte is handed to <paramref name="observe"/>
/// first, so usage/limits observers see the original Responses stream.
/// </summary>
public static class BridgeResponseWriter
{
    /// <summary>Upstream response headers worth passing on (request ids, rate-limit hints for client retries).</summary>
    private static readonly string[] PassedHeaders = ["retry-after", "x-request-id"];

    public static async Task WriteAsync(
        HttpContext ctx, HttpResponseMessage response, BridgeRequest request, string advertisedId,
        Action<ReadOnlyMemory<byte>> observe, Action<long> countBytes, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (status is < 200 or >= 300)
        {
            var bytes = await ReadAllAsync(response, observe, countBytes, ct);
            var (mapped, error) = AnthropicErrors.FromUpstream(status, Encoding.UTF8.GetString(bytes));
            CopyHeaders(ctx, response);
            await WriteJsonAsync(ctx, mapped, error, ct);
            return;
        }

        if (!request.UpstreamStream)
        {
            var bytes = await ReadAllAsync(response, observe, countBytes, ct);
            JsonObject message;
            try
            {
                message = JsonNode.Parse(bytes) is { } node
                    ? ResponsesToAnthropic.FromResponseJson(node, advertisedId, request.ToolNameMap)
                    : AnthropicErrors.Error("api_error", "The upstream returned an empty response.");
            }
            catch (JsonException)
            {
                message = AnthropicErrors.Error("api_error", "The upstream returned a response that is not valid JSON.");
            }
            var isError = message["type"]?.GetValue<string>() == "error";
            CopyHeaders(ctx, response);
            await WriteJsonAsync(ctx, isError ? AnthropicErrors.StatusForType(message["error"]?["type"]?.GetValue<string>()) : 200, message, ct);
            return;
        }

        await using var upstream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        int n;

        if (!request.ClientStream)
        {
            var aggregator = new AnthropicMessageAggregator(advertisedId, request.ToolNameMap);
            while ((n = await upstream.ReadAsync(buffer, ct)) > 0)
            {
                countBytes(n);
                observe(buffer.AsMemory(0, n));
                aggregator.Feed(buffer.AsSpan(0, n));
            }
            var (st, body) = aggregator.Complete();
            CopyHeaders(ctx, response);
            await WriteJsonAsync(ctx, st, body, ct);
            return;
        }

        var translator = new AnthropicStreamTranslator(advertisedId, request.ToolNameMap);
        try
        {
            while ((n = await upstream.ReadAsync(buffer, ct)) > 0)
            {
                countBytes(n);
                observe(buffer.AsMemory(0, n));
                var events = translator.FeedEvents(buffer.AsSpan(0, n));
                if (await TryFailBeforeStartAsync(ctx, response, translator, events, ct)) return;
                await WriteEventsAsync(ctx, response, events, ct);
            }
            var tail = translator.CompleteEvents();
            if (await TryFailBeforeStartAsync(ctx, response, translator, tail, ct)) return;
            await WriteEventsAsync(ctx, response, tail, ct);
        }
        catch (IOException) when (ctx.Response.HasStarted && !ct.IsCancellationRequested)
        {
            // The upstream connection broke mid-stream: tell the client in Anthropic's terms, then let the caller record it.
            try { await WriteRawAsync(ctx, AnthropicErrors.SseEvent("api_error", "The upstream OpenAI stream was interrupted."), ct); }
            catch (Exception e) when (e is IOException or OperationCanceledException) { }
            throw;
        }
    }

    /// <summary>
    /// An upstream failure before anything was sent (no <c>message_start</c> yet) is answered with an HTTP error status, as
    /// the Anthropic API does for errors it detects before streaming.
    /// </summary>
    private static async Task<bool> TryFailBeforeStartAsync(
        HttpContext ctx, HttpResponseMessage response, AnthropicStreamTranslator translator, IReadOnlyList<JsonObject> events, CancellationToken ct)
    {
        if (ctx.Response.HasStarted || translator.Started || !translator.Failed) return false;
        var error = events.LastOrDefault(e => e["type"]?.GetValue<string>() == "error")
            ?? AnthropicErrors.Error(translator.ErrorType ?? "api_error", translator.ErrorMessage ?? "The upstream request failed.");
        CopyHeaders(ctx, response);
        await WriteJsonAsync(ctx, AnthropicErrors.StatusForType(translator.ErrorType), error, ct);
        return true;
    }

    private static async Task WriteEventsAsync(HttpContext ctx, HttpResponseMessage response, IReadOnlyList<JsonObject> events, CancellationToken ct)
    {
        if (events.Count == 0) return;
        if (!ctx.Response.HasStarted)
        {
            ctx.Response.StatusCode = 200;
            ctx.Response.ContentType = "text/event-stream; charset=utf-8";
            ctx.Response.Headers.CacheControl = "no-cache";
            CopyHeaders(ctx, response);
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
            await ctx.Response.StartAsync(ct);
        }
        await WriteRawAsync(ctx, AnthropicStreamTranslator.Serialize(events), ct);
    }

    private static async Task WriteRawAsync(HttpContext ctx, byte[] bytes, CancellationToken ct)
    {
        await ctx.Response.Body.WriteAsync(bytes, ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static async Task<byte[]> ReadAllAsync(HttpResponseMessage response, Action<ReadOnlyMemory<byte>> observe, Action<long> countBytes, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        countBytes(bytes.Length);
        observe(bytes);
        return bytes;
    }

    private static void CopyHeaders(HttpContext ctx, HttpResponseMessage response)
    {
        if (ctx.Response.HasStarted) return;
        foreach (var name in PassedHeaders)
            if (response.Headers.TryGetValues(name, out var values))
                ctx.Response.Headers[name == "x-request-id" ? "x-upstream-request-id" : name] = values.ToArray();
    }

    private static Task WriteJsonAsync(HttpContext ctx, int status, JsonObject body, CancellationToken ct)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        return ctx.Response.WriteAsync(body.ToJsonString(), ct);
    }
}
