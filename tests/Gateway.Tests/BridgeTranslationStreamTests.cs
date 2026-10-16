using System.Text;
using System.Text.Json.Nodes;
using Gateway.Bridge;

namespace Gateway.Tests;

public class BridgeTranslationStreamTests
{
    private const string Advertised = "anthropic-openai-gpt-5-codex";

    /// <summary>Builds an upstream Responses SSE stream (with <c>event:</c> lines, as the OpenAI API sends).</summary>
    private static string Sse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var e in events)
        {
            var type = JsonNode.Parse(e)!["type"]!.GetValue<string>();
            sb.Append("event: ").Append(type).Append('\n').Append("data: ").Append(e).Append("\n\n");
        }
        return sb.ToString();
    }

    private const string BashArgs = """{"command":"ls -la \"my dir\"","description":"List files ✓"}""";

    /// <summary>Reasoning (two summary parts) → text (with multi-byte chars) → two parallel function calls.</summary>
    private static readonly string ToolStream = Sse(
        """{"type":"response.created","sequence_number":0,"response":{"id":"resp_42","object":"response","status":"in_progress","model":"gpt-5-codex","output":[]}}""",
        """{"type":"response.in_progress","sequence_number":1,"response":{"id":"resp_42","status":"in_progress","model":"gpt-5-codex"}}""",
        """{"type":"response.output_item.added","sequence_number":2,"output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[]}}""",
        """{"type":"response.reasoning_summary_part.added","sequence_number":3,"item_id":"rs_1","output_index":0,"summary_index":0,"part":{"type":"summary_text","text":""}}""",
        """{"type":"response.reasoning_summary_text.delta","sequence_number":4,"item_id":"rs_1","output_index":0,"summary_index":0,"delta":"**Planning** the "}""",
        """{"type":"response.reasoning_summary_text.delta","sequence_number":5,"item_id":"rs_1","output_index":0,"summary_index":0,"delta":"listing"}""",
        """{"type":"response.reasoning_summary_text.done","sequence_number":6,"item_id":"rs_1","output_index":0,"summary_index":0,"text":"**Planning** the listing"}""",
        """{"type":"response.reasoning_summary_part.done","sequence_number":7,"item_id":"rs_1","output_index":0,"summary_index":0,"part":{"type":"summary_text","text":"**Planning** the listing"}}""",
        """{"type":"response.reasoning_summary_part.added","sequence_number":8,"item_id":"rs_1","output_index":0,"summary_index":1,"part":{"type":"summary_text","text":""}}""",
        """{"type":"response.reasoning_summary_text.delta","sequence_number":9,"item_id":"rs_1","output_index":0,"summary_index":1,"delta":"Second part"}""",
        """{"type":"response.output_item.done","sequence_number":10,"output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"**Planning** the listing"},{"type":"summary_text","text":"Second part"}],"encrypted_content":"gAAAA_enc+/="}}""",
        """{"type":"response.output_item.added","sequence_number":11,"output_index":1,"item":{"id":"msg_1","type":"message","status":"in_progress","role":"assistant","content":[]}}""",
        """{"type":"response.content_part.added","sequence_number":12,"item_id":"msg_1","output_index":1,"content_index":0,"part":{"type":"output_text","text":"","annotations":[]}}""",
        """{"type":"response.output_text.delta","sequence_number":13,"item_id":"msg_1","output_index":1,"content_index":0,"delta":"héllo 🌍 "}""",
        """{"type":"response.output_text.delta","sequence_number":14,"item_id":"msg_1","output_index":1,"content_index":0,"delta":"wörld"}""",
        """{"type":"response.output_text.done","sequence_number":15,"item_id":"msg_1","output_index":1,"content_index":0,"text":"héllo 🌍 wörld"}""",
        """{"type":"response.content_part.done","sequence_number":16,"item_id":"msg_1","output_index":1,"content_index":0,"part":{"type":"output_text","text":"héllo 🌍 wörld","annotations":[]}}""",
        """{"type":"response.output_item.done","sequence_number":17,"output_index":1,"item":{"id":"msg_1","type":"message","status":"completed","role":"assistant","content":[{"type":"output_text","text":"héllo 🌍 wörld","annotations":[]}]}}""",
        """{"type":"response.output_item.added","sequence_number":18,"output_index":2,"item":{"id":"fc_1","type":"function_call","status":"in_progress","arguments":"","call_id":"call_A","name":"Bash"}}""",
        """{"type":"response.function_call_arguments.delta","sequence_number":19,"item_id":"fc_1","output_index":2,"delta":"{\"command\":\"ls -la \\\"my"}""",
        """{"type":"response.function_call_arguments.delta","sequence_number":20,"item_id":"fc_1","output_index":2,"delta":" dir\\\"\",\"description\":\"List files ✓\"}"}""",
        """{"type":"response.function_call_arguments.done","sequence_number":21,"item_id":"fc_1","output_index":2,"arguments":"{\"command\":\"ls -la \\\"my dir\\\"\",\"description\":\"List files ✓\"}"}""",
        """{"type":"response.output_item.done","sequence_number":22,"output_index":2,"item":{"id":"fc_1","type":"function_call","status":"completed","arguments":"{\"command\":\"ls -la \\\"my dir\\\"\",\"description\":\"List files ✓\"}","call_id":"call_A","name":"Bash"}}""",
        """{"type":"response.output_item.added","sequence_number":23,"output_index":3,"item":{"id":"fc_2","type":"function_call","status":"in_progress","arguments":"","call_id":"call_B","name":"Read"}}""",
        """{"type":"response.function_call_arguments.delta","sequence_number":24,"item_id":"fc_2","output_index":3,"delta":"{\"file_path\":"}""",
        """{"type":"response.function_call_arguments.delta","sequence_number":25,"item_id":"fc_2","output_index":3,"delta":"\"/a.txt\"}"}""",
        """{"type":"response.output_item.done","sequence_number":26,"output_index":3,"item":{"id":"fc_2","type":"function_call","status":"completed","arguments":"{\"file_path\":\"/a.txt\"}","call_id":"call_B","name":"Read"}}""",
        """{"type":"response.completed","sequence_number":27,"response":{"id":"resp_42","status":"completed","model":"gpt-5-codex","service_tier":"priority","output":[],"usage":{"input_tokens":1200,"input_tokens_details":{"cached_tokens":1024},"output_tokens":90,"output_tokens_details":{"reasoning_tokens":40},"total_tokens":1290}}}""");

    private sealed record Ev(string Name, JsonObject Data)
    {
        public string Type => Data["type"]!.GetValue<string>();
    }

    private static List<Ev> ParseAnthropic(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes);
        Assert.EndsWith("\n\n", text);
        var events = new List<Ev>();
        foreach (var frame in text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = frame.Split('\n');
            Assert.Equal(2, lines.Length);
            Assert.StartsWith("event: ", lines[0]);
            Assert.StartsWith("data: ", lines[1]);
            var ev = new Ev(lines[0]["event: ".Length..], JsonNode.Parse(lines[1]["data: ".Length..])!.AsObject());
            Assert.Equal(ev.Name, ev.Type);
            events.Add(ev);
        }
        return events;
    }

    /// <summary>Asserts the Anthropic streaming grammar and returns the reconstructed blocks (tool inputs as raw JSON).</summary>
    private static List<JsonObject> AssertValidStream(List<Ev> events, bool expectStop = true)
    {
        Assert.Equal("message_start", events[0].Type);
        var msg = events[0].Data["message"]!;
        Assert.Equal(Advertised, msg["model"]!.GetValue<string>());
        Assert.Equal("assistant", msg["role"]!.GetValue<string>());
        Assert.Empty(msg["content"]!.AsArray());

        var blocks = new List<JsonObject>();
        int? open = null;
        var sawDelta = false;
        foreach (var e in events.Skip(1))
        {
            switch (e.Type)
            {
                case "ping":
                    break;
                case "content_block_start":
                    Assert.Null(open);
                    Assert.False(sawDelta);
                    var idx = e.Data["index"]!.GetValue<int>();
                    Assert.Equal(blocks.Count, idx);
                    var cb = e.Data["content_block"]!.AsObject().DeepClone().AsObject();
                    if (cb["type"]!.GetValue<string>() == "tool_use")
                    {
                        Assert.Empty(cb["input"]!.AsObject());
                        cb["partial"] = "";
                    }
                    blocks.Add(cb);
                    open = idx;
                    break;
                case "content_block_delta":
                    Assert.Equal(open, e.Data["index"]!.GetValue<int>());
                    var b = blocks[open!.Value];
                    var d = e.Data["delta"]!;
                    var bt = b["type"]!.GetValue<string>();
                    switch (d["type"]!.GetValue<string>())
                    {
                        case "text_delta": Assert.Equal("text", bt); Append(b, "text", d["text"]!); break;
                        case "thinking_delta": Assert.Equal("thinking", bt); Append(b, "thinking", d["thinking"]!); break;
                        case "signature_delta": Assert.Equal("thinking", bt); b["signature"] = d["signature"]!.GetValue<string>(); break;
                        case "input_json_delta": Assert.Equal("tool_use", bt); Append(b, "partial", d["partial_json"]!); break;
                        default: Assert.Fail("unexpected delta " + d.ToJsonString()); break;
                    }
                    break;
                case "content_block_stop":
                    Assert.Equal(open, e.Data["index"]!.GetValue<int>());
                    open = null;
                    break;
                case "message_delta":
                    Assert.Null(open);
                    sawDelta = true;
                    break;
                case "message_stop":
                    Assert.True(sawDelta);
                    Assert.Same(events[^1], e);
                    break;
                case "error":
                    Assert.Same(events[^1], e);
                    break;
                default:
                    Assert.Fail("unexpected event " + e.Type);
                    break;
            }
        }
        if (expectStop) Assert.Equal("message_stop", events[^1].Type);
        return blocks;
    }

    private static byte[] FeedInChunks(AnthropicStreamTranslator t, byte[] upstream, Func<int, int> nextSize)
    {
        var output = new List<byte>();
        var i = 0;
        while (i < upstream.Length)
        {
            var n = Math.Min(Math.Max(1, nextSize(i)), upstream.Length - i);
            output.AddRange(t.Feed(upstream.AsSpan(i, n)));
            i += n;
        }
        output.AddRange(t.Complete());
        return [.. output];
    }

    [Fact]
    public void Tool_call_stream_translates_identically_for_any_chunking()
    {
        var upstream = Encoding.UTF8.GetBytes(ToolStream);
        var whole = new AnthropicStreamTranslator(Advertised);
        var reference = FeedInChunks(whole, upstream, _ => int.MaxValue);

        var rng = new Random(1234);
        var splitters = new List<Func<int, int>> { _ => 1, _ => 2, _ => 3, _ => 7, _ => 13, _ => 64, _ => 4096 };
        for (var k = 0; k < 20; k++) splitters.Add(_ => rng.Next(1, 200));
        foreach (var split in splitters)
        {
            var t = new AnthropicStreamTranslator(Advertised);
            var outBytes = FeedInChunks(t, upstream, split);
            Assert.Equal(Encoding.UTF8.GetString(reference), Encoding.UTF8.GetString(outBytes));
        }

        Assert.Equal("resp_42", whole.ResponseId);
        Assert.Equal("gpt-5-codex", whole.UpstreamModel);
        Assert.Equal("priority", whole.ServiceTier);
        Assert.Equal(new BridgeUsage(1200, 1024, 90, 40), whole.Usage);
        Assert.Equal("tool_use", whole.StopReason);
        Assert.True(whole.Finished);
        Assert.False(whole.Failed);
        Assert.True(whole.HasToolUse);
        Assert.Equal(4, whole.BlockCount);
    }

    private static void Append(JsonObject block, string key, JsonNode value) =>
        block[key] = (block[key]?.GetValue<string>() ?? "") + value.GetValue<string>();

    [Fact]
    public void Tool_call_stream_is_a_valid_anthropic_stream_with_expected_content()
    {
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(ToolStream), _ => 5));
        Assert.Equal("msg_42", events[0].Data["message"]!["id"]!.GetValue<string>());
        Assert.Equal("ping", events[1].Type);
        var blocks = AssertValidStream(events);

        Assert.Equal(["thinking", "text", "tool_use", "tool_use"], blocks.Select(b => b["type"]!.GetValue<string>()).ToArray());
        Assert.Equal("**Planning** the listing\n\nSecond part", blocks[0]["thinking"]!.GetValue<string>());
        Assert.True(ThinkingEnvelope.TryDecode(blocks[0]["signature"]!.GetValue<string>(), out var rid, out var enc));
        Assert.Equal("rs_1", rid);
        Assert.Equal("gAAAA_enc+/=", enc);
        Assert.Equal("héllo 🌍 wörld", blocks[1]["text"]!.GetValue<string>());
        Assert.Equal("call_A", blocks[2]["id"]!.GetValue<string>());
        Assert.Equal("Bash", blocks[2]["name"]!.GetValue<string>());
        Assert.Equal(BashArgs, blocks[2]["partial"]!.GetValue<string>());
        Assert.Equal("call_B", blocks[3]["id"]!.GetValue<string>());
        Assert.Equal("""{"file_path":"/a.txt"}""", blocks[3]["partial"]!.GetValue<string>());

        var delta = events.Single(e => e.Type == "message_delta").Data;
        Assert.Equal("tool_use", delta["delta"]!["stop_reason"]!.GetValue<string>());
        var u = delta["usage"]!;
        Assert.Equal(176, u["input_tokens"]!.GetValue<long>());
        Assert.Equal(1024, u["cache_read_input_tokens"]!.GetValue<long>());
        Assert.Equal(0, u["cache_creation_input_tokens"]!.GetValue<long>());
        Assert.Equal(90, u["output_tokens"]!.GetValue<long>());
    }

    [Fact]
    public void Aggregator_builds_the_same_message_for_non_streaming_clients()
    {
        var agg = new AnthropicMessageAggregator(Advertised);
        var bytes = Encoding.UTF8.GetBytes(ToolStream);
        for (var i = 0; i < bytes.Length; i += 11) agg.Feed(bytes.AsSpan(i, Math.Min(11, bytes.Length - i)));
        var (status, m) = agg.Complete();
        Assert.Equal(200, status);
        Assert.Equal("message", m["type"]!.GetValue<string>());
        Assert.Equal(Advertised, m["model"]!.GetValue<string>());
        Assert.Equal("tool_use", m["stop_reason"]!.GetValue<string>());
        var c = m["content"]!.AsArray();
        Assert.Equal(4, c.Count);
        Assert.Equal("héllo 🌍 wörld", c[1]!["text"]!.GetValue<string>());
        Assert.Equal("ls -la \"my dir\"", c[2]!["input"]!["command"]!.GetValue<string>());
        Assert.Equal("/a.txt", c[3]!["input"]!["file_path"]!.GetValue<string>());
        Assert.True(ThinkingEnvelope.TryDecode(c[0]!["signature"]!.GetValue<string>(), out _, out var enc));
        Assert.Equal("gAAAA_enc+/=", enc);
        Assert.Equal(1024, m["usage"]!["cache_read_input_tokens"]!.GetValue<long>());
        Assert.Equal("gpt-5-codex", agg.Translator.UpstreamModel);
    }

    [Fact]
    public void Function_call_without_argument_deltas_emits_arguments_once_and_empty_arguments_none()
    {
        var upstream = Sse(
            """{"type":"response.created","response":{"id":"resp_1","model":"gpt-5"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","arguments":"","call_id":"call_1","name":"Bash"}}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"id":"fc_1","type":"function_call","arguments":"{\"command\":\"pwd\"}","call_id":"call_1","name":"Bash"}}""",
            """{"type":"response.output_item.added","output_index":1,"item":{"id":"fc_2","type":"function_call","arguments":"","call_id":"call_2","name":"TodoRead"}}""",
            """{"type":"response.output_item.done","output_index":1,"item":{"id":"fc_2","type":"function_call","arguments":"","call_id":"call_2","name":"TodoRead"}}""",
            """{"type":"response.completed","response":{"id":"resp_1","status":"completed","usage":{"input_tokens":10,"output_tokens":5}}}""");
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 9));
        var blocks = AssertValidStream(events);
        Assert.Equal("""{"command":"pwd"}""", blocks[0]["partial"]!.GetValue<string>());
        Assert.Equal("", blocks[1]["partial"]!.GetValue<string>());
        Assert.Single(events, e => e.Type == "content_block_delta");

        var agg = new AnthropicMessageAggregator(Advertised);
        agg.Feed(Encoding.UTF8.GetBytes(upstream));
        var (_, m) = agg.Complete();
        Assert.Empty(m["content"]![1]!["input"]!.AsObject());
        Assert.Equal("pwd", m["content"]![0]!["input"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void Text_only_stream_without_event_lines_crlf_and_comments()
    {
        // Data-only frames (type inside the JSON), CRLF line endings, a keepalive comment and [DONE].
        var upstream =
            ": keepalive\r\n\r\n" +
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_t\",\"model\":\"gpt-5\"}}\r\n\r\n" +
            "data:{\"type\":\"response.output_text.delta\",\"item_id\":\"m1\",\"output_index\":0,\"content_index\":0,\"delta\":\"Hello\"}\r\n\r\n" +
            "data: {\"type\":\"response.output_text.delta\",\"item_id\":\"m1\",\"output_index\":0,\"content_index\":0,\"delta\":\" there\"}\r\n\r\n" +
            "data: {\"type\":\"response.output_item.done\",\"output_index\":0,\"item\":{\"id\":\"m1\",\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"Hello there\"}]}}\r\n\r\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_t\",\"status\":\"completed\",\"usage\":{\"input_tokens\":3,\"output_tokens\":2}}}\r\n\r\n" +
            "data: [DONE]\r\n\r\n";
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 4));
        var blocks = AssertValidStream(events);
        Assert.Single(blocks);
        Assert.Equal("Hello there", blocks[0]["text"]!.GetValue<string>());
        Assert.Equal("end_turn", events.Single(e => e.Type == "message_delta").Data["delta"]!["stop_reason"]!.GetValue<string>());
        Assert.Null(t.ServiceTier);
    }

    [Fact]
    public void Incomplete_stream_reports_max_tokens()
    {
        var upstream = Sse(
            """{"type":"response.created","response":{"id":"resp_i","model":"gpt-5"}}""",
            """{"type":"response.output_text.delta","item_id":"m1","output_index":0,"content_index":0,"delta":"partial"}""",
            """{"type":"response.incomplete","response":{"id":"resp_i","status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"usage":{"input_tokens":3,"output_tokens":100}}}""");
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 1000));
        AssertValidStream(events);
        Assert.Equal("max_tokens", t.StopReason);
        Assert.Equal("max_tokens", events.Single(e => e.Type == "message_delta").Data["delta"]!["stop_reason"]!.GetValue<string>());
    }

    [Fact]
    public void Failed_response_becomes_error_event()
    {
        var upstream = Sse(
            """{"type":"response.created","response":{"id":"resp_f","model":"gpt-5"}}""",
            """{"type":"response.output_text.delta","item_id":"m1","output_index":0,"content_index":0,"delta":"par"}""",
            """{"type":"response.failed","response":{"id":"resp_f","status":"failed","error":{"code":"rate_limit_exceeded","message":"Rate limit reached"}}}""",
            """{"type":"response.output_text.delta","item_id":"m1","output_index":0,"content_index":0,"delta":"ignored after failure"}""");
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 17));
        AssertValidStream(events, expectStop: false);
        var err = events[^1];
        Assert.Equal("error", err.Type);
        Assert.Equal("rate_limit_error", err.Data["error"]!["type"]!.GetValue<string>());
        Assert.Equal("Rate limit reached", err.Data["error"]!["message"]!.GetValue<string>());
        Assert.True(t.Failed);
        Assert.DoesNotContain(events, e => e.Type == "message_stop");
        Assert.DoesNotContain("ignored", string.Concat(events.Select(e => e.Data.ToJsonString())));

        var agg = new AnthropicMessageAggregator(Advertised);
        agg.Feed(Encoding.UTF8.GetBytes(upstream));
        var (status, body) = agg.Complete();
        Assert.Equal(429, status);
        Assert.Equal("rate_limit_error", body["error"]!["type"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"type":"error","code":"server_error","message":"boom","param":null,"sequence_number":3}""", "api_error", "boom")]
    [InlineData("""{"type":"error","error":{"type":"server_is_overloaded","code":"server_is_overloaded","message":"busy"}}""", "overloaded_error", "busy")]
    [InlineData("""{"type":"error","error":{"type":"invalid_request_error","code":"context_length_exceeded","message":"too long"}}""", "invalid_request_error", "too long")]
    public void Error_events_map_to_anthropic_error_types(string errorEvent, string type, string message)
    {
        var upstream = Sse("""{"type":"response.created","response":{"id":"r","model":"gpt-5"}}""", errorEvent);
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 1000));
        Assert.Equal("error", events[^1].Type);
        Assert.Equal(type, events[^1].Data["error"]!["type"]!.GetValue<string>());
        Assert.Equal(message, events[^1].Data["error"]!["message"]!.GetValue<string>());
        Assert.Equal(type, t.ErrorType);
    }

    [Fact]
    public void Truncated_stream_ends_with_error_event()
    {
        var upstream = Sse(
            """{"type":"response.created","response":{"id":"resp_x","model":"gpt-5"}}""",
            """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","arguments":"","call_id":"call_1","name":"Bash"}}""",
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","output_index":0,"delta":"{\"comm"}""");
        // Cut the last frame in half as well.
        var bytes = Encoding.UTF8.GetBytes(upstream + "data: {\"type\":\"response.function_call_arg");
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, bytes, _ => 8));
        AssertValidStream(events, expectStop: false);
        Assert.Equal("error", events[^1].Type);
        Assert.Equal("api_error", events[^1].Data["error"]!["type"]!.GetValue<string>());
        Assert.True(t.Failed);
        Assert.False(t.Finished);

        var agg = new AnthropicMessageAggregator(Advertised);
        agg.Feed(bytes);
        Assert.Equal(500, agg.Complete().Status);
    }

    [Fact]
    public void Empty_upstream_stream_yields_only_an_error()
    {
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(t.Complete());
        Assert.Single(events);
        Assert.Equal("error", events[0].Type);
        Assert.False(t.Started);
    }

    [Fact]
    public void Completed_response_without_item_events_is_emitted_from_its_output()
    {
        var upstream = Sse(
            """{"type":"response.created","response":{"id":"resp_o","model":"gpt-5"}}""",
            """{"type":"response.completed","response":{"id":"resp_o","status":"completed","output":[{"type":"reasoning","id":"rs_9","summary":[],"encrypted_content":"E9"},{"type":"message","id":"m","content":[{"type":"output_text","text":"Done."}]},{"type":"function_call","id":"fc","call_id":"call_9","name":"Bash","arguments":"{\"command\":\"true\"}"}],"usage":{"input_tokens":5,"output_tokens":7}}}""");
        var t = new AnthropicStreamTranslator(Advertised);
        var events = ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 1000));
        var blocks = AssertValidStream(events);
        Assert.Equal(["thinking", "text", "tool_use"], blocks.Select(b => b["type"]!.GetValue<string>()).ToArray());
        Assert.Equal("", blocks[0]["thinking"]!.GetValue<string>());
        Assert.True(ThinkingEnvelope.TryDecode(blocks[0]["signature"]!.GetValue<string>(), out _, out var enc));
        Assert.Equal("E9", enc);
        Assert.Equal("Done.", blocks[1]["text"]!.GetValue<string>());
        Assert.Equal("""{"command":"true"}""", blocks[2]["partial"]!.GetValue<string>());
        Assert.Equal("tool_use", t.StopReason);
    }

    [Fact]
    public void Shortened_tool_names_are_restored_in_the_stream()
    {
        var map = new Dictionary<string, string> { ["short_abc"] = "mcp__very.long.original" };
        var upstream = Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"id":"fc_1","type":"function_call","arguments":"","call_id":"call_1","name":"short_abc"}}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"id":"fc_1","type":"function_call","arguments":"{}","call_id":"call_1","name":"short_abc"}}""",
            """{"type":"response.completed","response":{"id":"r","status":"completed"}}""");
        var t = new AnthropicStreamTranslator(Advertised, map);
        var blocks = AssertValidStream(ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 1000)));
        Assert.Equal("mcp__very.long.original", blocks[0]["name"]!.GetValue<string>());
    }

    [Fact]
    public void Reasoning_without_summary_or_encrypted_content_leaves_no_block()
    {
        var upstream = Sse(
            """{"type":"response.output_item.added","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[]}}""",
            """{"type":"response.output_item.done","output_index":0,"item":{"id":"rs_1","type":"reasoning","summary":[]}}""",
            """{"type":"response.output_text.delta","item_id":"m1","output_index":1,"content_index":0,"delta":"ok"}""",
            """{"type":"response.completed","response":{"id":"r","status":"completed"}}""");
        var t = new AnthropicStreamTranslator(Advertised);
        var blocks = AssertValidStream(ParseAnthropic(FeedInChunks(t, Encoding.UTF8.GetBytes(upstream), _ => 1000)));
        Assert.Equal("text", Assert.Single(blocks)["type"]!.GetValue<string>());
        Assert.Equal("end_turn", t.StopReason);
    }
}
