using System.Text;
using Gateway.Usage;
using Microsoft.Extensions.Configuration;

namespace Gateway.Tests;

public class UsageParserTests
{
    private static ResponseUsage Parse(string endpoint, string contentType, string body, int status = 200, int chunkSize = 7)
    {
        var parser = new ResponseUsageParser(endpoint, status, contentType);
        var bytes = Encoding.UTF8.GetBytes(body);
        for (var i = 0; i < bytes.Length; i += chunkSize)
            parser.Feed(bytes.AsSpan(i, Math.Min(chunkSize, bytes.Length - i)));
        return parser.Complete();
    }

    private const string AnthropicStream = """
        event: message_start
        data: {"type":"message_start","message":{"id":"msg_1","model":"claude-sonnet-4-5-20250929","usage":{"input_tokens":12,"cache_creation_input_tokens":100,"cache_read_input_tokens":2000,"output_tokens":1}}}

        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"hi"}}

        event: content_block_start
        data: {"type":"content_block_start","index":1,"content_block":{"type":"tool_use","id":"toolu_1","name":"Bash","input":{}}}

        event: content_block_start
        data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"toolu_2","name":"Bash","input":{}}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"input_tokens":12,"cache_creation_input_tokens":100,"cache_read_input_tokens":2000,"output_tokens":57}}

        event: message_stop
        data: {"type":"message_stop"}


        """;

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public void Anthropic_stream_takes_last_cumulative_usage_and_counts_tool_use(int chunkSize)
    {
        var u = Parse(Endpoints.Messages, "text/event-stream; charset=utf-8", AnthropicStream.Replace("\n", "\r\n"), chunkSize: chunkSize);
        Assert.Equal("claude-sonnet-4-5-20250929", u.Model);
        Assert.Equal(12, u.InputTokens);
        Assert.Equal(57, u.OutputTokens);
        Assert.Equal(2000, u.CacheReadTokens);
        Assert.Equal(100, u.CacheWriteTokens);
        Assert.Equal(2112, u.ContextTokens);
        Assert.Equal(2, u.Tools["Bash"]);
        Assert.Equal("tool_use", u.StopReason);
        Assert.Null(u.ErrorType);
    }

    [Fact]
    public void Anthropic_json_and_error_bodies()
    {
        var u = Parse(Endpoints.Messages, "application/json", """
            {"id":"msg","type":"message","model":"claude-opus-4-5","stop_reason":"end_turn","content":[{"type":"text","text":"x"},{"type":"tool_use","name":"Read","id":"t","input":{}}],
             "usage":{"input_tokens":5,"cache_creation_input_tokens":0,"cache_read_input_tokens":10,"output_tokens":3}}
            """);
        Assert.Equal((5, 3, 10, 15L), (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.ContextTokens));
        Assert.Equal(1, u.Tools["Read"]);

        var err = Parse(Endpoints.Messages, "application/json", """{"type":"error","error":{"type":"rate_limit_error","message":"slow down"}}""", status: 429);
        Assert.Equal("rate_limit_error", err.ErrorType);
        Assert.False(err.HasUsage);

        var sseErr = Parse(Endpoints.Messages, "text/event-stream", "event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\"}}\n\n");
        Assert.Equal("overloaded_error", sseErr.ErrorType);
    }

    [Fact]
    public void Responses_stream_counts_cached_reasoning_and_function_calls_once()
    {
        const string body = """
            event: response.created
            data: {"type":"response.created","response":{"id":"r","model":"gpt-5-codex","status":"in_progress","usage":null}}

            event: response.output_item.added
            data: {"type":"response.output_item.added","item":{"type":"function_call","name":"shell","arguments":""}}

            event: response.function_call_arguments.delta
            data: {"type":"response.function_call_arguments.delta","delta":"{\"cmd\""}

            event: response.output_item.done
            data: {"type":"response.output_item.done","item":{"type":"function_call","name":"shell","arguments":"{}"}}

            event: response.output_item.done
            data: {"type":"response.output_item.done","item":{"type":"message","role":"assistant","content":[]}}

            event: response.completed
            data: {"type":"response.completed","response":{"id":"r","model":"gpt-5-codex","status":"completed","output":[{"type":"function_call","name":"shell"},{"type":"message"}],"usage":{"input_tokens":1000,"input_tokens_details":{"cached_tokens":800},"output_tokens":120,"output_tokens_details":{"reasoning_tokens":64},"total_tokens":1120}}}


            """;
        var u = Parse(Endpoints.Responses, "text/event-stream", body);
        Assert.Equal("gpt-5-codex", u.Model);
        Assert.Equal(200, u.InputTokens);
        Assert.Equal(800, u.CacheReadTokens);
        Assert.Equal(0, u.CacheWriteTokens);
        Assert.Equal(120, u.OutputTokens);
        Assert.Equal(64, u.ReasoningTokens);
        Assert.Equal(1000, u.ContextTokens);
        Assert.Equal(1, u.ToolCalls);
        Assert.Equal("completed", u.StopReason);
    }

    [Fact]
    public void Chat_completions_stream_counts_tool_call_fragments_once_and_reads_usage_chunk()
    {
        const string body = """
            data: {"id":"c","object":"chat.completion.chunk","model":"gpt-4.1","choices":[{"index":0,"delta":{"role":"assistant","tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"get_weather","arguments":""}}]}}],"usage":null}

            data: {"id":"c","object":"chat.completion.chunk","model":"gpt-4.1","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{}"}}]}}],"usage":null}

            data: {"id":"c","object":"chat.completion.chunk","model":"gpt-4.1","choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}],"usage":null}

            data: {"id":"c","object":"chat.completion.chunk","model":"gpt-4.1","choices":[],"usage":{"prompt_tokens":50,"completion_tokens":9,"prompt_tokens_details":{"cached_tokens":20}}}

            data: [DONE]


            """;
        var u = Parse(Endpoints.Chat, "text/event-stream", body);
        Assert.Equal(1, u.Tools["get_weather"]);
        Assert.Equal((30L, 9L, 20L, 50L), (u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.ContextTokens));
        Assert.Equal("tool_calls", u.StopReason);
    }

    [Fact]
    public void ChatGpt_error_detail_and_non_generation_endpoints()
    {
        Assert.Equal("usage_limit_reached", Parse(Endpoints.Responses, "application/json",
            """{"error":{"type":"usage_limit_reached","resets_in_seconds":60}}""", status: 429).ErrorType);
        Assert.Equal("error", Parse(Endpoints.Responses, "application/json", """{"detail":"Stream must be set to true"}""", status: 400).ErrorType);
        var count = Parse(Endpoints.CountTokens, "application/json", """{"input_tokens":1234}""");
        Assert.False(count.HasUsage);
        Assert.Equal(0, count.InputTokens);
    }

    [Fact]
    public void Pricing_matches_longest_prefix_family_and_config_overrides()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gateway:Pricing:my-model:Input"] = "2",
            ["Gateway:Pricing:my-model:Output"] = "4",
            ["Gateway:Pricing:gpt-5:Input"] = "9",
        }).Build();
        var p = new PricingTable(config);
        Assert.Equal(new ModelPrice(5, 25, 0.5, 6.25), p.Find("claude-opus-4-5-20251101"));
        Assert.Equal(new ModelPrice(15, 75, 1.5, 18.75), p.Find("claude-opus-4-1-20250805"));
        Assert.Equal(new ModelPrice(3, 15, 0.3, 3.75), p.Find("us.anthropic.claude-sonnet-4-5-20250929-v1:0"));
        Assert.Equal(new ModelPrice(0.25, 2, 0.025, 0), p.Find("gpt-5.1-codex-mini"));
        Assert.Equal(9, p.Find("gpt-5")!.Input);
        Assert.Equal(new ModelPrice(2, 4, 0, 0), p.Find("my-model-v2"));
        Assert.Equal(new ModelPrice(1, 5, 0.1, 1.25), p.Find("claude-haiku-9")); // family fallback
        Assert.Null(p.Find("totally-unknown"));

        var u = new ResponseUsage { InputTokens = 1_000_000, OutputTokens = 100_000, CacheReadTokens = 2_000_000, CacheWriteTokens = 400_000 };
        Assert.Equal(3 + 1.5 + 0.6 + 1.5, p.Cost(p.Find("claude-sonnet-4-5")!, u), 6);
    }

    [Fact]
    public void Sweep_reports_peak_overlap_per_bucket()
    {
        // Bucket size 100: [0,100) [100,200) [200,300)
        var intervals = new List<(long, long)> { (10, 150), (20, 60), (30, 70), (60, 120), (150, 250), (250, 260) };
        Assert.Equal([3, 2, 1], AnalyticsService.Sweep(intervals, 0, 100, 3));
        // Back-to-back requests do not overlap.
        Assert.Equal([1], AnalyticsService.Sweep([(0, 50), (50, 100)], 0, 100, 1));
        Assert.Equal([0], AnalyticsService.Sweep([], 0, 100, 1));
    }
}
