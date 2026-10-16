using System.Text;
using System.Text.Json;

namespace Gateway.Usage;

/// <summary>Normalized usage of one response. Input excludes cached tokens; reasoning is a subset of output.</summary>
public sealed class ResponseUsage
{
    public string? Model { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    /// <summary>Of <see cref="CacheWriteTokens"/>, those written with the 1-hour TTL (Anthropic <c>cache_creation</c>).</summary>
    public long CacheWrite1hTokens { get; set; }
    public long ReasoningTokens { get; set; }
    /// <summary>Prompt size the model saw: Anthropic input + cache read + cache write; OpenAI total input tokens.</summary>
    public long ContextTokens { get; set; }
    public bool HasUsage { get; set; }
    public string? StopReason { get; set; }
    public string? ErrorType { get; set; }
    /// <summary>Raw speed the provider reports it served: Anthropic <c>usage.speed</c>, OpenAI <c>service_tier</c>.</summary>
    public string? ReportedSpeed { get; set; }
    public Dictionary<string, int> Tools { get; } = new(StringComparer.Ordinal);
    public int ToolCalls => Tools.Values.Sum();
}

/// <summary>
/// Incrementally extracts usage from a provider response as it streams past, without holding the stream: SSE is
/// processed event by event (token delta events are skipped unparsed); JSON bodies are buffered up to a cap and parsed at
/// the end. Understands Anthropic Messages, OpenAI Responses (API and ChatGPT Codex backend) and Chat Completions.
/// </summary>
public sealed class ResponseUsageParser
{
    private const int MaxBufferedBytes = 16 * 1024 * 1024;

    // Unknown: no Content-Type (the ChatGPT Codex backend streams without one); decided from the first body bytes.
    private enum Format { Sse, Json, Ignore, Unknown }

    private readonly string _endpoint;
    private readonly int _status;
    private Format _format;
    private readonly MemoryStream _buffer = new();   // JSON body, or the current incomplete SSE line
    private readonly MemoryStream _data = new();     // data lines of the current SSE event
    private bool _overflow;
    private bool _skipEvent;
    private int _streamedToolItems;
    private readonly HashSet<(int, int)> _chatToolCalls = new();

    public ResponseUsage Usage { get; } = new();

    /// <summary>True once the stream has carried its first output (token delta) event.</summary>
    public bool OutputStarted { get; private set; }

    /// <summary>
    /// Characters of output streamed so far (text, thinking and tool-argument deltas), for a live estimate of output
    /// tokens before the final usage arrives. Hidden reasoning is not streamed and only shows up in the final usage.
    /// </summary>
    public long StreamedChars { get; private set; }

    private bool _deltaEvent;

    public ResponseUsageParser(string endpoint, int status, string? contentType)
    {
        _endpoint = endpoint;
        _status = status;
        var ct = contentType?.ToLowerInvariant() ?? "";
        _format = ct.Contains("text/event-stream") ? Format.Sse : ct.Contains("json") ? Format.Json
            : ct.Length == 0 ? Format.Unknown : Format.Ignore;
        if (!Endpoints.IsGeneration(endpoint) && status < 400) _format = Format.Ignore;
    }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (_format == Format.Unknown)
        {
            // Hold the first bytes until the format is clear, then replay them.
            _sniffed.Write(chunk);
            var held = _sniffed.GetBuffer().AsSpan(0, (int)_sniffed.Length);
            _format = Sniff(held);
            if (_format == Format.Unknown && _sniffed.Length < 64) return;
            if (_format == Format.Unknown) _format = Format.Ignore;
            var bytes = held.ToArray();
            _sniffed.SetLength(0);
            Feed(bytes);
            return;
        }
        if (_format == Format.Ignore || chunk.IsEmpty) return;
        if (_format == Format.Json)
        {
            if (_overflow) return;
            if (_buffer.Length + chunk.Length > MaxBufferedBytes) { _overflow = true; _buffer.SetLength(0); return; }
            _buffer.Write(chunk);
            return;
        }

        while (!chunk.IsEmpty)
        {
            var nl = chunk.IndexOf((byte)'\n');
            if (nl < 0) { AppendLine(chunk); return; }
            AppendLine(chunk[..nl]);
            ProcessLine();
            chunk = chunk[(nl + 1)..];
        }
    }

    public ResponseUsage Complete()
    {
        if (_format == Format.Unknown && _sniffed.Length > 0)
        {
            var held = _sniffed.GetBuffer().AsSpan(0, (int)_sniffed.Length).ToArray();
            _format = Sniff(held) is var f && f != Format.Unknown ? f : Format.Ignore;
            _sniffed.SetLength(0);
            Feed(held);
        }
        if (_format == Format.Json && !_overflow && _buffer.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(_buffer.GetBuffer().AsMemory(0, (int)_buffer.Length));
                HandleJsonBody(doc.RootElement);
            }
            catch (JsonException) { }
        }
        else if (_format == Format.Sse)
        {
            if (_buffer.Length > 0) ProcessLine();
            DispatchEvent();
        }
        if (_status >= 400 && Usage.ErrorType == null) Usage.ErrorType = $"http_{_status}";
        return Usage;
    }

    /// <summary>
    /// SSE when the body starts with an SSE field or comment, JSON when it starts with an object or array, Unknown while
    /// the bytes seen so far are too short to tell.
    /// </summary>
    private static Format Sniff(ReadOnlySpan<byte> held)
    {
        var body = held.TrimStart(" \t\r\n"u8);
        if (body.IsEmpty) return Format.Unknown;
        if (body[0] is (byte)'{' or (byte)'[') return Format.Json;
        if (body[0] == (byte)':') return Format.Sse;
        foreach (var field in SseFields)
        {
            if (body.StartsWith(field)) return Format.Sse;
            if (field.AsSpan().StartsWith(body)) return Format.Unknown;
        }
        return Format.Ignore;
    }

    private static readonly byte[][] SseFields = ["event:"u8.ToArray(), "data:"u8.ToArray(), "id:"u8.ToArray(), "retry:"u8.ToArray()];
    private readonly MemoryStream _sniffed = new();

    private void AppendLine(ReadOnlySpan<byte> part)
    {
        if (_overflow) return;
        if (_buffer.Length + part.Length > MaxBufferedBytes) { _overflow = true; return; }
        _buffer.Write(part);
    }

    private void ProcessLine()
    {
        var overflow = _overflow;
        _overflow = false;
        var line = _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length);
        if (line.Length > 0 && line[^1] == '\r') line = line[..^1];
        try
        {
            if (overflow) { _skipEvent = true; return; }
            if (line.IsEmpty) { DispatchEvent(); return; }
            if (line.StartsWith("event:"u8))
            {
                var name = Encoding.UTF8.GetString(line[6..]).Trim();
                // Token deltas are the bulk of a stream and never carry usage.
                _skipEvent = name.EndsWith(".delta") || name is "content_block_delta" or "ping";
                _deltaEvent = _skipEvent && name != "ping";
                if (_deltaEvent) OutputStarted = true;
                return;
            }
            if (_deltaEvent && line.StartsWith("data:"u8))
            {
                StreamedChars += DeltaChars(line[5..]);
                return;
            }
            if (line.StartsWith("data:"u8) && !_skipEvent)
            {
                var data = line[5..];
                if (!data.IsEmpty && data[0] == ' ') data = data[1..];
                if (_data.Length > 0) _data.WriteByte((byte)'\n');
                if (_data.Length + data.Length > MaxBufferedBytes) { _skipEvent = true; _data.SetLength(0); return; }
                _data.Write(data);
            }
        }
        finally { _buffer.SetLength(0); }
    }

    private void DispatchEvent()
    {
        var skip = _skipEvent;
        _skipEvent = false;
        _deltaEvent = false;
        if (skip || _data.Length == 0) { _data.SetLength(0); return; }
        var data = _data.GetBuffer().AsMemory(0, (int)_data.Length);
        try
        {
            if (data.Span.SequenceEqual("[DONE]"u8)) return;
            using var doc = JsonDocument.Parse(data);
            HandleStreamEvent(doc.RootElement);
        }
        catch (JsonException) { }
        finally { _data.SetLength(0); }
    }

    private static readonly byte[][] DeltaTextFields =
        ["text"u8.ToArray(), "delta"u8.ToArray(), "thinking"u8.ToArray(), "partial_json"u8.ToArray(), "arguments"u8.ToArray()];

    /// <summary>Length of the streamed text in a delta event (Anthropic <c>delta.text</c>/<c>thinking</c>/<c>partial_json</c>,
    /// OpenAI <c>delta</c>), read with a forward-only reader; the event is not otherwise parsed.</summary>
    private static int DeltaChars(ReadOnlySpan<byte> data)
    {
        var total = 0;
        try
        {
            var reader = new Utf8JsonReader(data.TrimStart(" "u8));
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName) continue;
                var isText = false;
                foreach (var f in DeltaTextFields)
                    if (reader.ValueTextEquals(f)) { isText = true; break; }
                if (!reader.Read()) break;
                if (isText && reader.TokenType == JsonTokenType.String) total += reader.ValueSpan.Length;
            }
        }
        catch (JsonException) { }
        return total;
    }

    private void HandleStreamEvent(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return;
        var type = Str(e, "type");
        if (type == "error") { Usage.ErrorType = ErrorTypeOf(e) ?? Str(e, "code") ?? "error"; return; }
        // Streams without event: lines carry the type in the data only.
        if (type != null && (type == "content_block_delta" || type.EndsWith(".delta"))) OutputStarted = true;

        switch (_endpoint)
        {
            case Endpoints.Messages:
                switch (type)
                {
                    case "message_start" when Obj(e, "message") is { } msg:
                        Usage.Model = Str(msg, "model") ?? Usage.Model;
                        if (Obj(msg, "usage") is { } u0) ApplyAnthropicUsage(u0);
                        break;
                    case "content_block_start" when Obj(e, "content_block") is { } block:
                        CountAnthropicToolBlock(block);
                        break;
                    case "message_delta":
                        if (Obj(e, "delta") is { } delta && Str(delta, "stop_reason") is { } stop) Usage.StopReason = stop;
                        if (Obj(e, "usage") is { } u1) ApplyAnthropicUsage(u1);
                        break;
                }
                break;

            case Endpoints.Responses:
                switch (type)
                {
                    case "response.created" or "response.in_progress" when Obj(e, "response") is { } r0:
                        Usage.Model = Str(r0, "model") ?? Usage.Model;
                        Usage.ReportedSpeed = Str(r0, "service_tier") ?? Usage.ReportedSpeed;
                        break;
                    case "response.output_item.done" when Obj(e, "item") is { } item:
                        if (CountResponsesToolItem(item)) _streamedToolItems++;
                        break;
                    case "response.completed" or "response.done" or "response.incomplete" or "response.failed"
                        when Obj(e, "response") is { } r1:
                        HandleResponseObject(r1, countTools: _streamedToolItems == 0);
                        break;
                }
                break;

            case Endpoints.Chat:
                HandleChatChunk(e);
                break;
        }
    }

    private void HandleJsonBody(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (_status >= 400 || Str(root, "type") == "error" || root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            Usage.ErrorType = ErrorTypeOf(root) ?? (_status >= 400 ? $"http_{_status}" : "error");
            if (_status >= 400) return;
        }
        switch (_endpoint)
        {
            case Endpoints.Messages:
                Usage.Model = Str(root, "model") ?? Usage.Model;
                Usage.StopReason = Str(root, "stop_reason") ?? Usage.StopReason;
                if (Obj(root, "usage") is { } u) ApplyAnthropicUsage(u);
                if (root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray()) CountAnthropicToolBlock(block);
                break;
            case Endpoints.Responses:
                HandleResponseObject(root, countTools: true);
                break;
            case Endpoints.Chat:
                HandleChatChunk(root);
                break;
        }
    }

    private void HandleResponseObject(JsonElement r, bool countTools)
    {
        Usage.Model = Str(r, "model") ?? Usage.Model;
        Usage.ReportedSpeed = Str(r, "service_tier") ?? Usage.ReportedSpeed;
        if (Str(r, "status") is { } status) Usage.StopReason = status;
        if (Obj(r, "error") is { } err) Usage.ErrorType = Str(err, "code") ?? Str(err, "type") ?? "error";
        if (Obj(r, "incomplete_details") is { } inc && Str(inc, "reason") is { } reason) Usage.StopReason = reason;
        if (Obj(r, "usage") is { } u)
        {
            var input = Long(u, "input_tokens");
            var cached = Obj(u, "input_tokens_details") is { } d ? Long(d, "cached_tokens") : 0;
            Usage.InputTokens = Math.Max(0, input - cached);
            Usage.CacheReadTokens = cached;
            Usage.OutputTokens = Long(u, "output_tokens");
            Usage.ReasoningTokens = Obj(u, "output_tokens_details") is { } od ? Long(od, "reasoning_tokens") : 0;
            Usage.ContextTokens = input;
            Usage.HasUsage = true;
        }
        if (countTools && r.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            foreach (var item in output.EnumerateArray()) CountResponsesToolItem(item);
    }

    private void HandleChatChunk(JsonElement c)
    {
        Usage.Model = Str(c, "model") ?? Usage.Model;
        Usage.ReportedSpeed = Str(c, "service_tier") ?? Usage.ReportedSpeed;
        if (c.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            if (_format == Format.Sse && ChatChunkHasOutput(choices)) OutputStarted = true;
            foreach (var choice in choices.EnumerateArray())
            {
                if (choice.ValueKind != JsonValueKind.Object) continue;
                var choiceIndex = (int)Long(choice, "index");
                if (Str(choice, "finish_reason") is { } finish) Usage.StopReason = finish;
                var holder = Obj(choice, "delta") ?? Obj(choice, "message");
                if (holder is not { } h || !h.TryGetProperty("tool_calls", out var calls) || calls.ValueKind != JsonValueKind.Array) continue;
                var position = 0;
                foreach (var call in calls.EnumerateArray())
                {
                    var toolIndex = call.TryGetProperty("index", out var ti) && ti.ValueKind == JsonValueKind.Number ? ti.GetInt32() : position;
                    position++;
                    // In streams the first fragment of each call carries its id and name; later fragments only arguments.
                    if (Str(call, "id") == null || !_chatToolCalls.Add((choiceIndex, toolIndex))) continue;
                    var name = Obj(call, "function") is { } f ? Str(f, "name") : null;
                    AddTool(name ?? Str(call, "type") ?? "function");
                }
            }
        }
        if (Obj(c, "usage") is { } u)
        {
            var prompt = Long(u, "prompt_tokens");
            var cached = Obj(u, "prompt_tokens_details") is { } pd ? Long(pd, "cached_tokens") : 0;
            Usage.InputTokens = Math.Max(0, prompt - cached);
            Usage.CacheReadTokens = cached;
            Usage.OutputTokens = Long(u, "completion_tokens");
            Usage.ReasoningTokens = Obj(u, "completion_tokens_details") is { } cd ? Long(cd, "reasoning_tokens") : 0;
            Usage.ContextTokens = prompt;
            Usage.HasUsage = true;
        }
    }

    private static bool ChatChunkHasOutput(JsonElement choices)
    {
        foreach (var choice in choices.EnumerateArray())
            if (Obj(choice, "delta") is { } d
                && (Str(d, "content") is { Length: > 0 } || Str(d, "reasoning_content") is { Length: > 0 } || d.TryGetProperty("tool_calls", out _)))
                return true;
        return false;
    }

    /// <summary>Anthropic usage objects are cumulative snapshots: later values replace earlier ones.</summary>
    private void ApplyAnthropicUsage(JsonElement u)
    {
        if (Str(u, "speed") is { } speed) Usage.ReportedSpeed = speed;
        if (Num(u, "input_tokens") is { } i) Usage.InputTokens = i;
        if (Num(u, "output_tokens") is { } o) Usage.OutputTokens = o;
        if (Num(u, "cache_read_input_tokens") is { } cr) Usage.CacheReadTokens = cr;
        if (Num(u, "cache_creation_input_tokens") is { } cw) Usage.CacheWriteTokens = cw;
        if (Obj(u, "cache_creation") is { } cc && Num(cc, "ephemeral_1h_input_tokens") is { } cw1h) Usage.CacheWrite1hTokens = cw1h;
        Usage.ContextTokens = Usage.InputTokens + Usage.CacheReadTokens + Usage.CacheWriteTokens;
        Usage.HasUsage = true;
    }

    private void CountAnthropicToolBlock(JsonElement block)
    {
        if (block.ValueKind == JsonValueKind.Object && Str(block, "type") is "tool_use" or "server_tool_use" or "mcp_tool_use")
            AddTool(Str(block, "name") ?? "tool");
    }

    private bool CountResponsesToolItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || Str(item, "type") is not { } type || !type.EndsWith("_call")) return false;
        AddTool(Str(item, "name") ?? type);
        return true;
    }

    private void AddTool(string name) => Usage.Tools[name] = Usage.Tools.GetValueOrDefault(name) + 1;

    private static string? ErrorTypeOf(JsonElement root)
    {
        if (Obj(root, "error") is { } err) return Str(err, "code") ?? Str(err, "type") ?? "error";
        if (Obj(root, "response") is { } r && Obj(r, "error") is { } rerr) return Str(rerr, "code") ?? Str(rerr, "type");
        if (root.TryGetProperty("detail", out _)) return "error";
        return null;
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static long Long(JsonElement e, string name) => Num(e, name) ?? 0;
}
