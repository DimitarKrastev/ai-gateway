using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Gateway.Bridge.BridgeJson;

namespace Gateway.Bridge;

/// <summary>
/// Incrementally translates an OpenAI Responses API SSE stream into an Anthropic Messages SSE stream. Feed upstream bytes
/// split anywhere; each call returns the Anthropic events completed so far.
/// <para>Event mapping: first upstream event → <c>message_start</c> (model = advertised id, zero usage) + <c>ping</c>;
/// reasoning item → <c>thinking</c> block (<c>thinking_delta</c> from summary deltas, <c>signature_delta</c> carrying the
/// encrypted reasoning when the item is done); message text/refusal → <c>text</c> block; function_call → <c>tool_use</c>
/// block (<c>input_json_delta</c> from argument deltas); <c>response.completed</c>/<c>incomplete</c> →
/// <c>message_delta</c> (stop_reason, usage) + <c>message_stop</c>; <c>response.failed</c>/<c>error</c> → <c>error</c>.
/// Blocks are emitted one at a time (a new block closes the open one), so indices are strictly sequential.</para>
/// </summary>
public sealed class AnthropicStreamTranslator(string advertisedId, IReadOnlyDictionary<string, string>? toolNameMap = null)
{
    private sealed class Block(int index, string kind, string key)
    {
        public int Index { get; } = index;
        public string Kind { get; } = kind;
        public string Key { get; } = key;
        public StringBuilder Emitted { get; } = new();
        public bool Closed { get; set; }
        public int SummaryParts { get; set; }
        public string? FinalArguments { get; set; }
    }

    private readonly List<byte> _line = new();
    private readonly StringBuilder _data = new();
    private bool _hasData;
    private string? _eventName;
    private readonly Dictionary<string, Block> _blocks = new(StringComparer.Ordinal);
    private Block? _open;
    private int _nextIndex;
    private List<JsonObject> _out = new();

    /// <summary>The upstream response id.</summary>
    public string? ResponseId { get; private set; }

    /// <summary>The model the upstream reported (e.g. <c>gpt-5-codex</c>).</summary>
    public string? UpstreamModel { get; private set; }

    /// <summary>The upstream's <c>service_tier</c> (e.g. <c>priority</c>, <c>default</c>), when reported.</summary>
    public string? ServiceTier { get; private set; }

    /// <summary>Usage from <c>response.completed</c>/<c>incomplete</c>/<c>failed</c>.</summary>
    public BridgeUsage? Usage { get; private set; }

    public string? StopReason { get; private set; }

    /// <summary><c>message_start</c> has been emitted.</summary>
    public bool Started { get; private set; }

    /// <summary>The response completed (or was incomplete) and <c>message_stop</c> was emitted.</summary>
    public bool Finished { get; private set; }

    /// <summary>An <c>error</c> event was emitted (upstream failure or truncated stream).</summary>
    public bool Failed { get; private set; }

    public string? ErrorType { get; private set; }
    public string? ErrorMessage { get; private set; }
    public bool HasToolUse { get; private set; }

    /// <summary>Web searches the model ran (<c>web_search_call</c> items).</summary>
    public int WebSearches { get; private set; }

    /// <summary>Number of content blocks emitted.</summary>
    public int BlockCount => _nextIndex;

    public bool Done => Finished || Failed;

    /// <summary>Feeds upstream bytes; returns the Anthropic SSE bytes produced.</summary>
    public byte[] Feed(ReadOnlySpan<byte> chunk) => Serialize(FeedEvents(chunk));

    /// <summary>Ends the upstream stream; returns the final Anthropic SSE bytes (an error event if it ended early).</summary>
    public byte[] Complete() => Serialize(CompleteEvents());

    public IReadOnlyList<JsonObject> FeedEvents(ReadOnlySpan<byte> chunk)
    {
        _out = new List<JsonObject>();
        foreach (var b in chunk)
        {
            if (b == (byte)'\n')
            {
                ProcessLine();
                _line.Clear();
            }
            else _line.Add(b);
        }
        return _out;
    }

    public IReadOnlyList<JsonObject> CompleteEvents()
    {
        _out = new List<JsonObject>();
        if (_line.Count > 0)
        {
            ProcessLine();
            _line.Clear();
        }
        Dispatch();
        if (!Done) Fail("api_error", "The upstream stream ended before the response completed.");
        return _out;
    }

    public static byte[] Serialize(IEnumerable<JsonObject> events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
            sb.Append("event: ").Append(Str(e["type"])).Append("\ndata: ").Append(e.ToJsonString()).Append("\n\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private void ProcessLine()
    {
        ReadOnlySpan<byte> bytes = CollectionsMarshal.AsSpan(_line);
        if (bytes.Length > 0 && bytes[^1] == (byte)'\r') bytes = bytes[..^1];
        var line = Encoding.UTF8.GetString(bytes);
        if (line.Length == 0)
        {
            Dispatch();
            return;
        }
        if (line[0] == ':') return;
        var colon = line.IndexOf(':');
        var field = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? "" : line[(colon + 1)..];
        if (value.StartsWith(' ')) value = value[1..];
        if (field == "event") _eventName = value;
        else if (field == "data")
        {
            if (_hasData) _data.Append('\n');
            _data.Append(value);
            _hasData = true;
        }
    }

    private void Dispatch()
    {
        var eventName = _eventName;
        _eventName = null;
        if (!_hasData) return;
        var data = _data.ToString();
        _data.Clear();
        _hasData = false;
        if (data == "[DONE]") return;
        JsonObject? ev;
        try
        {
            ev = JsonNode.Parse(data) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }
        if (ev is null) return;
        Handle(Str(ev["type"]) ?? eventName, ev);
    }

    private void Handle(string? type, JsonObject ev)
    {
        if (Done) return;
        switch (type)
        {
            case "response.created" or "response.in_progress" or "response.queued":
                Capture(Obj(ev["response"]));
                EnsureStarted();
                break;
            case "response.output_item.added":
                EnsureStarted();
                ItemAdded(Obj(ev["item"]));
                break;
            case "response.content_part.added":
            {
                EnsureStarted();
                var part = Obj(ev["part"]);
                if (part is null || ResponsesToAnthropic.PartText(part) is null) break;
                var b = OpenText(TextKey(ev));
                EmitMissing(b, ResponsesToAnthropic.PartText(part));
                break;
            }
            case "response.output_text.delta" or "response.refusal.delta":
                EnsureStarted();
                EmitDelta(OpenText(TextKey(ev)), Str(ev["delta"]));
                break;
            case "response.output_text.done" or "response.refusal.done":
            {
                var text = Str(ev["text"]) ?? Str(ev["refusal"]);
                if (_blocks.GetValueOrDefault(TextKey(ev)) is { } b) EmitMissing(b, text);
                else if (!string.IsNullOrEmpty(text)) EmitMissing(OpenText(TextKey(ev)), text);
                break;
            }
            case "response.content_part.done":
            {
                var key = TextKey(ev);
                var text = Obj(ev["part"]) is { } p ? ResponsesToAnthropic.PartText(p) : null;
                var b = _blocks.GetValueOrDefault(key);
                if (b is null && string.IsNullOrEmpty(text)) break;
                b ??= OpenText(key);
                EmitMissing(b, text);
                Close(b);
                break;
            }
            case "response.reasoning_summary_part.added":
            {
                EnsureStarted();
                var b = OpenThinking(Str(ev["item_id"]) ?? "");
                if (b.SummaryParts++ > 0 && b.Emitted.Length > 0) EmitDelta(b, "\n\n");
                break;
            }
            case "response.reasoning_summary_text.delta":
                EnsureStarted();
                EmitDelta(OpenThinking(Str(ev["item_id"]) ?? ""), Str(ev["delta"]));
                break;
            case "response.function_call_arguments.delta":
                if (_blocks.GetValueOrDefault(Str(ev["item_id"]) ?? "") is { Kind: "tool_use" } tb) EmitDelta(tb, Str(ev["delta"]));
                break;
            case "response.function_call_arguments.done":
                if (_blocks.GetValueOrDefault(Str(ev["item_id"]) ?? "") is { Kind: "tool_use" } db)
                    db.FinalArguments = Str(ev["arguments"]);
                break;
            case "response.output_item.done":
                EnsureStarted();
                ItemDone(Obj(ev["item"]));
                break;
            case "response.completed":
                Finish(Obj(ev["response"]), incomplete: false);
                break;
            case "response.incomplete":
                Finish(Obj(ev["response"]), incomplete: true);
                break;
            case "response.failed":
            {
                var r = Obj(ev["response"]);
                Capture(r);
                Usage = BridgeUsage.From(r?["usage"]) ?? Usage;
                var err = Obj(r?["error"]);
                Fail(AnthropicErrors.TypeForCode(Str(err?["code"])), Str(err?["message"]) ?? "The upstream response failed.");
                break;
            }
            case "error":
            {
                var err = Obj(ev["error"]) ?? ev;
                var code = Str(err["code"]) ?? Str(err["type"]);
                Fail(AnthropicErrors.TypeForCode(code is "error" ? null : code), Str(err["message"]) ?? "Upstream error.");
                break;
            }
        }
    }

    private static string TextKey(JsonObject ev) => $"{Str(ev["item_id"])}:{Long(ev["content_index"]) ?? 0}";

    private void Capture(JsonObject? response)
    {
        if (response is null) return;
        ResponseId ??= Str(response["id"]);
        UpstreamModel = Str(response["model"]) ?? UpstreamModel;
        ServiceTier = Str(response["service_tier"]) ?? ServiceTier;
    }

    private void EnsureStarted()
    {
        if (Started) return;
        Started = true;
        _out.Add(new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = ResponsesToAnthropic.MessageId(ResponseId),
                ["type"] = "message",
                ["role"] = "assistant",
                ["model"] = advertisedId,
                ["content"] = new JsonArray(),
                ["stop_reason"] = null,
                ["stop_sequence"] = null,
                ["usage"] = BridgeUsage.ZeroAnthropic(),
            },
        });
        _out.Add(new JsonObject { ["type"] = "ping" });
    }

    private void ItemAdded(JsonObject? item)
    {
        // Thinking and text blocks open lazily (first summary part / content part / delta, or the item's done event), so
        // reasoning without a summary or encrypted content leaves no empty block behind.
        if (Str(item?["type"]) == "function_call") OpenTool(item!);
    }

    private void ItemDone(JsonObject? item)
    {
        if (item is null) return;
        var id = Str(item["id"]) ?? "";
        switch (Str(item["type"]))
        {
            case "reasoning":
            {
                var text = ResponsesToAnthropic.SummaryText(item);
                var encrypted = Str(item["encrypted_content"]);
                var b = _blocks.GetValueOrDefault(id);
                if (b is null && text.Length == 0 && encrypted is null) break;
                b ??= OpenThinking(id);
                EmitMissing(b, text);
                if (!b.Closed)
                {
                    _out.Add(new JsonObject
                    {
                        ["type"] = "content_block_delta",
                        ["index"] = b.Index,
                        ["delta"] = new JsonObject { ["type"] = "signature_delta", ["signature"] = ThinkingEnvelope.Encode(Str(item["id"]), encrypted) },
                    });
                }
                Close(b);
                break;
            }
            case "message":
            {
                var parts = Arr(item["content"])?.OfType<JsonObject>().ToList() ?? [];
                for (var i = 0; i < parts.Count; i++)
                {
                    var key = $"{id}:{i}";
                    var text = ResponsesToAnthropic.PartText(parts[i]);
                    var b = _blocks.GetValueOrDefault(key);
                    if (b is null && string.IsNullOrEmpty(text)) continue;
                    b ??= OpenText(key);
                    EmitMissing(b, text);
                    Close(b);
                }
                if (_open is not null && _open.Key.StartsWith(id + ":", StringComparison.Ordinal)) Close(_open);
                break;
            }
            case "web_search_call":
            {
                // The results arrive whole with the finished item: emit the search and its results as two complete blocks.
                if (_blocks.ContainsKey(id) || BridgeWebSearch.Blocks(item) is not var (use, result)) break;
                var input = use["input"]!.ToJsonString();
                use["input"] = new JsonObject();
                var b = Open(id, "server_tool_use", use);
                EmitDelta(b, input);
                Close(b);
                Close(Open(id + ":result", "web_search_tool_result", result));
                WebSearches++;
                break;
            }
            case "function_call":
            {
                var b = _blocks.GetValueOrDefault(id) ?? OpenTool(item);
                var args = Str(item["arguments"]) ?? b.FinalArguments;
                if (b.Emitted.Length == 0 && !string.IsNullOrEmpty(args)) EmitDelta(b, args);
                Close(b);
                break;
            }
        }
    }

    private void Finish(JsonObject? response, bool incomplete)
    {
        Capture(response);
        EnsureStarted();
        // Fallback for upstreams that only report the final output: emit it from the response object.
        if (_nextIndex == 0)
        {
            foreach (var item in Arr(response?["output"])?.OfType<JsonObject>() ?? [])
                ItemDone(item);
        }
        if (_open is not null) Close(_open);
        Usage = BridgeUsage.From(response?["usage"]) ?? Usage;
        if (Usage is not null && WebSearches > 0) Usage = Usage with { WebSearchRequests = WebSearches };
        StopReason = ResponsesToAnthropic.StopReason(incomplete, Str(Obj(response?["incomplete_details"])?["reason"]), HasToolUse);
        _out.Add(new JsonObject
        {
            ["type"] = "message_delta",
            ["delta"] = new JsonObject { ["stop_reason"] = StopReason, ["stop_sequence"] = null },
            ["usage"] = Usage?.ToAnthropic() ?? BridgeUsage.ZeroAnthropic(),
        });
        _out.Add(new JsonObject { ["type"] = "message_stop" });
        Finished = true;
    }

    private void Fail(string type, string message)
    {
        if (Done) return;
        Failed = true;
        ErrorType = type;
        ErrorMessage = message;
        _out.Add(AnthropicErrors.Error(type, message));
    }

    private Block OpenText(string key) =>
        _blocks.GetValueOrDefault(key) ?? Open(key, "text", new JsonObject { ["type"] = "text", ["text"] = "" });

    private Block OpenThinking(string itemId) =>
        _blocks.GetValueOrDefault(itemId)
        ?? Open(itemId, "thinking", new JsonObject { ["type"] = "thinking", ["thinking"] = "", ["signature"] = "" });

    private Block OpenTool(JsonObject item)
    {
        var id = Str(item["id"]) ?? Str(item["call_id"]) ?? "";
        if (_blocks.GetValueOrDefault(id) is { } existing) return existing;
        HasToolUse = true;
        return Open(id, "tool_use", new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = Str(item["call_id"]) ?? Str(item["id"]) ?? "toolu_" + Guid.NewGuid().ToString("N")[..24],
            ["name"] = BridgeToolNames.ToClient(Str(item["name"]) ?? "", toolNameMap),
            ["input"] = new JsonObject(),
        });
    }

    private Block Open(string key, string kind, JsonObject contentBlock)
    {
        EnsureStarted();
        if (_open is not null) Close(_open);
        var b = new Block(_nextIndex++, kind, key);
        _blocks[key] = b;
        _open = b;
        _out.Add(new JsonObject { ["type"] = "content_block_start", ["index"] = b.Index, ["content_block"] = contentBlock });
        return b;
    }

    private void Close(Block b)
    {
        if (b.Closed) return;
        b.Closed = true;
        if (_open == b) _open = null;
        _out.Add(new JsonObject { ["type"] = "content_block_stop", ["index"] = b.Index });
    }

    private void EmitDelta(Block b, string? text)
    {
        if (b.Closed || string.IsNullOrEmpty(text)) return;
        b.Emitted.Append(text);
        var delta = b.Kind switch
        {
            "thinking" => new JsonObject { ["type"] = "thinking_delta", ["thinking"] = text },
            "tool_use" or "server_tool_use" => new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = text },
            _ => new JsonObject { ["type"] = "text_delta", ["text"] = text },
        };
        _out.Add(new JsonObject { ["type"] = "content_block_delta", ["index"] = b.Index, ["delta"] = delta });
    }

    /// <summary>Emits whatever part of the final text was not streamed as deltas.</summary>
    private void EmitMissing(Block b, string? full)
    {
        if (string.IsNullOrEmpty(full)) return;
        var emitted = b.Emitted.ToString();
        if (emitted.Length == 0) EmitDelta(b, full);
        else if (full.Length > emitted.Length && full.StartsWith(emitted, StringComparison.Ordinal)) EmitDelta(b, full[emitted.Length..]);
    }
}

/// <summary>
/// Turns a Responses SSE stream into one Anthropic message (for non-streaming clients when the upstream always streams,
/// as the Codex backend does). Built on <see cref="AnthropicStreamTranslator"/> so both paths agree.
/// </summary>
public sealed class AnthropicMessageAggregator(string advertisedId, IReadOnlyDictionary<string, string>? toolNameMap = null)
{
    private JsonObject? _message;
    private JsonObject? _error;
    private readonly List<JsonObject> _content = new();
    private readonly Dictionary<int, StringBuilder> _json = new();

    public AnthropicStreamTranslator Translator { get; } = new(advertisedId, toolNameMap);

    public void Feed(ReadOnlySpan<byte> chunk) => Apply(Translator.FeedEvents(chunk));

    /// <summary>Status 200 with the message, or the mapped status with an Anthropic error object.</summary>
    public (int Status, JsonObject Body) Complete()
    {
        Apply(Translator.CompleteEvents());
        if (_error is not null) return (AnthropicErrors.StatusForType(Str(Obj(_error["error"])?["type"])), _error);
        foreach (var (index, sb) in _json) FinishToolInput(index, sb);
        _json.Clear();
        var message = _message ?? new JsonObject { ["id"] = ResponsesToAnthropic.MessageId(null), ["type"] = "message", ["role"] = "assistant", ["model"] = advertisedId };
        message["content"] = new JsonArray([.. _content]);
        return (200, message);
    }

    private void Apply(IReadOnlyList<JsonObject> events)
    {
        foreach (var e in events)
        {
            var index = (int)(Long(e["index"]) ?? -1);
            switch (Str(e["type"]))
            {
                case "message_start":
                    _message = Obj(e["message"])?.DeepClone() as JsonObject;
                    break;
                case "content_block_start":
                    var block = (JsonObject)Obj(e["content_block"])!.DeepClone();
                    _content.Add(block);
                    if (Str(block["type"]) is "tool_use" or "server_tool_use") _json[_content.Count - 1] = new StringBuilder();
                    break;
                case "content_block_delta" when index >= 0 && index < _content.Count:
                    var b = _content[index];
                    var d = Obj(e["delta"]);
                    switch (Str(d?["type"]))
                    {
                        case "text_delta": b["text"] = Str(b["text"]) + Str(d!["text"]); break;
                        case "thinking_delta": b["thinking"] = Str(b["thinking"]) + Str(d!["thinking"]); break;
                        case "signature_delta": b["signature"] = Str(d!["signature"]); break;
                        case "input_json_delta": _json.GetValueOrDefault(index)?.Append(Str(d!["partial_json"])); break;
                    }
                    break;
                case "content_block_stop" when _json.Remove(index, out var sb):
                    FinishToolInput(index, sb);
                    break;
                case "message_delta" when _message is not null:
                    var delta = Obj(e["delta"]);
                    _message["stop_reason"] = Str(delta?["stop_reason"]);
                    _message["stop_sequence"] = Str(delta?["stop_sequence"]);
                    if (Obj(e["usage"])?.DeepClone() is JsonObject usage) _message["usage"] = usage;
                    break;
                case "error":
                    _error = (JsonObject)e.DeepClone();
                    break;
            }
        }
    }

    private void FinishToolInput(int index, StringBuilder sb) =>
        _content[index]["input"] = ResponsesToAnthropic.ParseArguments(sb.ToString());
}
