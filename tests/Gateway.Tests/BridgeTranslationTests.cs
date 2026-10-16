using System.Text.Json.Nodes;
using Gateway.Bridge;

namespace Gateway.Tests;

public class BridgeTranslationTests
{
    private static readonly BridgeTranslateOptions Api = new()
    {
        AdvertisedModelId = "anthropic-openai-gpt-5",
        UpstreamModel = "gpt-5",
        SessionId = "sess-1",
    };

    private static readonly BridgeTranslateOptions Codex = Api with { CodexBackend = true, UpstreamModel = "gpt-5-codex" };

    private static BridgeRequest Translate(string json, BridgeTranslateOptions? o = null) =>
        AnthropicToResponses.Translate(JsonNode.Parse(json)!, o ?? Api);

    private static JsonArray Input(BridgeRequest r) => r.Body["input"]!.AsArray();

    private static string S(JsonNode? n) => n?.GetValue<string>()!;

    // ---------- request ----------

    [Fact]
    public void System_blocks_become_instructions_and_unsupported_fields_are_dropped()
    {
        var r = Translate("""
            {"model":"anthropic-openai-gpt-5","max_tokens":32000,"stream":true,"temperature":1,"top_p":0.9,"top_k":5,
             "stop_sequences":["X"],"metadata":{"user_id":"u_session_abc"},"context_management":{"edits":[]},
             "system":[{"type":"text","text":"x-anthropic-billing-header: cc_version=1.2.3; cch=abc"},
                       {"type":"text","text":"You are Claude Code.","cache_control":{"type":"ephemeral"}},
                       {"type":"text","text":"Be brief."}],
             "messages":[{"role":"user","content":"hi"}]}
            """);
        var b = r.Body;
        Assert.Equal("gpt-5", S(b["model"]));
        Assert.Equal("You are Claude Code.\n\nBe brief.", S(b["instructions"]));
        foreach (var dropped in new[] { "temperature", "top_p", "top_k", "stop_sequences", "metadata", "context_management", "system", "messages", "max_tokens" })
            Assert.False(b.ContainsKey(dropped), dropped);
        Assert.False(b["store"]!.GetValue<bool>());
        Assert.Equal("reasoning.encrypted_content", S(b["include"]![0]));
        Assert.True(b["stream"]!.GetValue<bool>());
        Assert.True(r.ClientStream);
        Assert.Equal(32000, b["max_output_tokens"]!.GetValue<long>());
        Assert.Equal("sess-1", S(b["prompt_cache_key"]));
        Assert.False(b.ContainsKey("tools"));
        Assert.False(b.ContainsKey("reasoning"));
        Assert.DoesNotContain("cache_control", b.ToJsonString());

        var msg = Input(r).Single()!;
        Assert.Equal("message", S(msg["type"]));
        Assert.Equal("user", S(msg["role"]));
        Assert.Equal("input_text", S(msg["content"]![0]!["type"]));
        Assert.Equal("hi", S(msg["content"]![0]!["text"]));
    }

    [Fact]
    public void Codex_backend_always_streams_and_omits_max_output_tokens()
    {
        var r = Translate("""{"max_tokens":1000,"stream":false,"messages":[{"role":"user","content":"hi"}]}""", Codex);
        var b = r.Body;
        Assert.False(r.ClientStream);
        Assert.True(r.UpstreamStream);
        Assert.False(b.ContainsKey("max_output_tokens"));
        Assert.Equal("gpt-5-codex", S(b["model"]));
        Assert.Equal(AnthropicToResponses.DefaultInstructions, S(b["instructions"]));
        Assert.Empty(b["tools"]!.AsArray());
        Assert.Equal("auto", S(b["tool_choice"]));
        Assert.False(b["parallel_tool_calls"]!.GetValue<bool>());
        Assert.False(b["store"]!.GetValue<bool>());

        var api = Translate("""{"max_tokens":1000,"messages":[{"role":"user","content":"hi"}]}""");
        Assert.False(api.UpstreamStream);
        Assert.False(api.Body.ContainsKey("instructions"));
    }

    [Fact]
    public void Images_and_documents_map_to_input_parts()
    {
        var r = Translate("""
            {"messages":[{"role":"user","content":[
              {"type":"text","text":"look"},
              {"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"AAAA"}},
              {"type":"image","source":{"type":"url","url":"https://example.com/a.png"}},
              {"type":"document","title":"spec.pdf","source":{"type":"base64","media_type":"application/pdf","data":"JVBER"}},
              {"type":"document","title":"Notes","source":{"type":"text","media_type":"text/plain","data":"plain text"}},
              {"type":"text","text":""}
            ]}]}
            """);
        var parts = Input(r).Single()!["content"]!.AsArray();
        Assert.Equal(5, parts.Count);
        Assert.Equal("look", S(parts[0]!["text"]));
        Assert.Equal("input_image", S(parts[1]!["type"]));
        Assert.Equal("data:image/jpeg;base64,AAAA", S(parts[1]!["image_url"]));
        Assert.Equal("https://example.com/a.png", S(parts[2]!["image_url"]));
        Assert.Equal("input_file", S(parts[3]!["type"]));
        Assert.Equal("spec.pdf", S(parts[3]!["filename"]));
        Assert.Equal("data:application/pdf;base64,JVBER", S(parts[3]!["file_data"]));
        Assert.Equal("input_text", S(parts[4]!["type"]));
        Assert.Equal("Notes\n\nplain text", S(parts[4]!["text"]));
    }

    [Fact]
    public void Tool_use_and_tool_results_become_function_calls_and_outputs_in_order()
    {
        var r = Translate("""
            {"messages":[
              {"role":"user","content":"list files"},
              {"role":"assistant","content":[
                {"type":"text","text":"Running two commands."},
                {"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}},
                {"type":"tool_use","id":"toolu_2","name":"Read","input":{}}]},
              {"role":"user","content":[
                {"type":"text","text":"(note before results)"},
                {"type":"tool_result","tool_use_id":"toolu_1","content":"a.txt\nb.txt"},
                {"type":"tool_result","tool_use_id":"toolu_2","is_error":true,"content":[
                   {"type":"text","text":"cannot read"},
                   {"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBO"}}]},
                {"type":"text","text":"continue","cache_control":{"type":"ephemeral"}}]}
            ]}
            """);
        var input = Input(r);
        Assert.Equal(["message", "message", "function_call", "function_call", "function_call_output", "function_call_output", "message"],
            input.Select(i => S(i!["type"])).ToArray());

        Assert.Equal("assistant", S(input[1]!["role"]));
        Assert.Equal("output_text", S(input[1]!["content"]![0]!["type"]));
        Assert.Equal("Running two commands.", S(input[1]!["content"]![0]!["text"]));

        Assert.Equal("toolu_1", S(input[2]!["call_id"]));
        Assert.Equal("Bash", S(input[2]!["name"]));
        Assert.Equal("""{"command":"ls"}""", S(input[2]!["arguments"]));
        Assert.Equal("{}", S(input[3]!["arguments"]));

        Assert.Equal("toolu_1", S(input[4]!["call_id"]));
        Assert.Equal("a.txt\nb.txt", S(input[4]!["output"]));

        var errOut = input[5]!["output"]!.AsArray();
        Assert.Equal("toolu_2", S(input[5]!["call_id"]));
        Assert.Equal("Error: cannot read", S(errOut[0]!["text"]));
        Assert.Equal("input_image", S(errOut[1]!["type"]));
        Assert.Equal("data:image/png;base64,iVBO", S(errOut[1]!["image_url"]));

        var last = input[6]!["content"]!.AsArray();
        Assert.Equal(["(note before results)", "continue"], last.Select(p => S(p!["text"])).ToArray());
    }

    [Fact]
    public void Error_tool_result_without_text_still_signals_error()
    {
        var r = Translate("""
            {"messages":[{"role":"user","content":[{"type":"tool_result","tool_use_id":"t","is_error":true}]}]}
            """);
        Assert.Equal("Error", S(Input(r).Single()!["output"]));
    }

    [Fact]
    public void Bridge_thinking_blocks_replay_as_encrypted_reasoning_items_others_are_dropped()
    {
        var sig = ThinkingEnvelope.Encode("rs_123", "ENCRYPTED==");
        var redacted = ThinkingEnvelope.Encode("rs_456", "ENC2");
        var r = Translate($$$"""
            {"messages":[
              {"role":"user","content":"q"},
              {"role":"assistant","content":[
                {"type":"thinking","thinking":"I should list files.","signature":"{{{sig}}}"},
                {"type":"thinking","thinking":"real claude thought","signature":"EqQBCkYIBxgCKkD..."},
                {"type":"redacted_thinking","data":"{{{redacted}}}"},
                {"type":"tool_use","id":"toolu_1","name":"Bash","input":{"command":"ls"}}]}
            ]}
            """);
        var input = Input(r);
        Assert.Equal(["message", "reasoning", "reasoning", "function_call"], input.Select(i => S(i!["type"])).ToArray());
        var reasoning = input[1]!.AsObject();
        Assert.Equal("ENCRYPTED==", S(reasoning["encrypted_content"]));
        Assert.False(reasoning.ContainsKey("id"));
        Assert.Equal("I should list files.", S(reasoning["summary"]![0]!["text"]));
        Assert.Equal("summary_text", S(reasoning["summary"]![0]!["type"]));
        Assert.Equal("ENC2", S(input[2]!["encrypted_content"]));
        Assert.Empty(input[2]!["summary"]!.AsArray());
        Assert.DoesNotContain("real claude thought", r.Body.ToJsonString());
    }

    [Fact]
    public void Tools_and_tool_choice_are_mapped_and_server_tools_dropped()
    {
        var r = Translate("""
            {"messages":[{"role":"user","content":"x"}],
             "tools":[
               {"name":"Bash","description":"Run a command","input_schema":{"type":"object","properties":{"command":{"type":"string"}},"required":["command"]},"cache_control":{"type":"ephemeral"}},
               {"name":"NoArgs","input_schema":{"type":"object"}},
               {"type":"web_search_20250305","name":"web_search","max_uses":5}],
             "tool_choice":{"type":"any","disable_parallel_tool_use":true}}
            """);
        var tools = r.Body["tools"]!.AsArray();
        Assert.Equal(2, tools.Count);
        Assert.Equal("function", S(tools[0]!["type"]));
        Assert.Equal("Bash", S(tools[0]!["name"]));
        Assert.Equal("Run a command", S(tools[0]!["description"]));
        Assert.Equal("string", S(tools[0]!["parameters"]!["properties"]!["command"]!["type"]));
        Assert.False(tools[0]!["strict"]!.GetValue<bool>());
        Assert.NotNull(tools[1]!["parameters"]!["properties"]);
        Assert.Equal("required", S(r.Body["tool_choice"]));
        Assert.False(r.Body["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Equal(["web_search (web_search_20250305)"], r.DroppedTools);

        Assert.True(Translate("""{"messages":[],"tools":[{"name":"A","input_schema":{"type":"object"}}]}""").Body["parallel_tool_calls"]!.GetValue<bool>());
        Assert.Equal("auto", S(Translate("""{"messages":[],"tools":[{"name":"A","input_schema":{}}],"tool_choice":{"type":"auto"}}""").Body["tool_choice"]));
        Assert.Equal("none", S(Translate("""{"messages":[],"tools":[{"name":"A","input_schema":{}}],"tool_choice":{"type":"none"}}""").Body["tool_choice"]));
        var forced = Translate("""{"messages":[],"tools":[{"name":"A","input_schema":{}}],"tool_choice":{"type":"tool","name":"A"}}""").Body["tool_choice"]!;
        Assert.Equal("function", S(forced["type"]));
        Assert.Equal("A", S(forced["name"]));
    }

    [Fact]
    public void Long_or_invalid_tool_names_are_shortened_consistently_and_mapped_back()
    {
        var longName = "mcp__" + new string('x', 70) + ".tool";
        var r = Translate($$$"""
            {"messages":[{"role":"assistant","content":[{"type":"tool_use","id":"t1","name":"{{{longName}}}","input":{}}]}],
             "tools":[{"name":"{{{longName}}}","input_schema":{"type":"object"}}]}
            """);
        var upstream = S(r.Body["tools"]![0]!["name"])!;
        Assert.Matches("^[a-zA-Z0-9_-]{1,64}$", upstream);
        Assert.Equal(upstream, S(Input(r)[0]!["name"]));
        Assert.Equal(longName, r.ToolNameMap[upstream]);

        var resp = JsonNode.Parse($$$"""
            {"id":"resp_1","status":"completed","output":[{"type":"function_call","id":"fc_1","call_id":"call_1","name":"{{{upstream}}}","arguments":"{}"}]}
            """)!;
        var msg = ResponsesToAnthropic.FromResponseJson(resp, "m", r.ToolNameMap);
        Assert.Equal(longName, S(msg["content"]![0]!["name"]));
    }

    [Theory]
    [InlineData("low", null, null, "low")]
    [InlineData("medium", null, null, "medium")]
    [InlineData("high", null, null, "high")]
    [InlineData("max", null, null, "high")]
    [InlineData("max", null, "low,medium,high,xhigh", "xhigh")]
    [InlineData("max", null, "low,medium,high", "high")]
    [InlineData("high", null, "minimal,low", "low")]
    [InlineData("low", null, "medium,high", "medium")]
    [InlineData(null, 2000L, null, "low")]
    [InlineData(null, 10000L, null, "medium")]
    [InlineData(null, 32000L, null, "high")]
    [InlineData(null, 32000L, "low,medium", "medium")]
    [InlineData(null, null, null, null)]
    [InlineData("bogus", null, null, null)]
    public void Effort_mapping_and_clamping(string? effort, long? budget, string? supported, string? expected) =>
        Assert.Equal(expected, AnthropicToResponses.MapEffort(effort, budget, supported?.Split(',')));

    [Fact]
    public void Thinking_settings_map_to_reasoning()
    {
        var enabled = Translate("""{"messages":[],"thinking":{"type":"enabled","budget_tokens":10000}}""").Body["reasoning"]!;
        Assert.Equal("medium", S(enabled["effort"]));
        Assert.Equal("auto", S(enabled["summary"]));

        var adaptive = Translate("""{"messages":[],"thinking":{"type":"adaptive"},"output_config":{"effort":"max"}}""",
            Api with { SupportedEfforts = ["low", "medium", "high", "xhigh"] }).Body["reasoning"]!;
        Assert.Equal("xhigh", S(adaptive["effort"]));
        Assert.Equal("auto", S(adaptive["summary"]));

        Assert.False(Translate("""{"messages":[],"thinking":{"type":"disabled"}}""").Body.ContainsKey("reasoning"));
        var effortOnly = Translate("""{"messages":[],"output_config":{"effort":"low"}}""").Body["reasoning"]!.AsObject();
        Assert.Equal("low", S(effortOnly["effort"]));
        Assert.False(effortOnly.ContainsKey("summary"));
    }

    [Fact]
    public void Fast_speed_maps_to_priority_tier()
    {
        Assert.Equal("priority", S(Translate("""{"messages":[],"speed":"fast"}""").Body["service_tier"]));
        Assert.False(Translate("""{"messages":[]}""").Body.ContainsKey("service_tier"));
        Assert.False(Translate("""{"messages":[]}""", Api with { SessionId = null }).Body.ContainsKey("prompt_cache_key"));
    }

    [Fact]
    public void Missing_messages_is_a_translation_error()
    {
        Assert.Throws<BridgeTranslationException>(() => Translate("""{"model":"x"}"""));
        Assert.Throws<BridgeTranslationException>(() => Translate("""[1,2]"""));
    }

    // ---------- non-streaming response ----------

    [Fact]
    public void Response_maps_reasoning_text_and_function_calls_with_usage()
    {
        var resp = JsonNode.Parse("""
            {"id":"resp_abc","object":"response","status":"completed","model":"gpt-5-2025-08-07",
             "output":[
               {"type":"reasoning","id":"rs_1","summary":[{"type":"summary_text","text":"Think A"},{"type":"summary_text","text":"Think B"}],"encrypted_content":"ENC"},
               {"type":"message","id":"msg_1","role":"assistant","content":[{"type":"output_text","text":"Listing.","annotations":[]}]},
               {"type":"function_call","id":"fc_1","call_id":"call_1","name":"Bash","arguments":"{\"command\":\"ls\"}"},
               {"type":"function_call","id":"fc_2","call_id":"call_2","name":"Read","arguments":""}],
             "usage":{"input_tokens":100,"input_tokens_details":{"cached_tokens":80},"output_tokens":50,"output_tokens_details":{"reasoning_tokens":30},"total_tokens":150}}
            """)!;
        var m = ResponsesToAnthropic.FromResponseJson(resp, "anthropic-openai-gpt-5");
        Assert.Equal("msg_abc", S(m["id"]));
        Assert.Equal("message", S(m["type"]));
        Assert.Equal("assistant", S(m["role"]));
        Assert.Equal("anthropic-openai-gpt-5", S(m["model"]));
        Assert.Equal("tool_use", S(m["stop_reason"]));
        var c = m["content"]!.AsArray();
        Assert.Equal(["thinking", "text", "tool_use", "tool_use"], c.Select(b => S(b!["type"])).ToArray());
        Assert.Equal("Think A\n\nThink B", S(c[0]!["thinking"]));
        Assert.True(ThinkingEnvelope.TryDecode(S(c[0]!["signature"]), out var id, out var enc));
        Assert.Equal(("rs_1", "ENC"), (id, enc));
        Assert.Equal("Listing.", S(c[1]!["text"]));
        Assert.Equal("call_1", S(c[2]!["id"]));
        Assert.Equal("Bash", S(c[2]!["name"]));
        Assert.Equal("ls", S(c[2]!["input"]!["command"]));
        Assert.Empty(c[3]!["input"]!.AsObject());

        var u = m["usage"]!;
        Assert.Equal(20, u["input_tokens"]!.GetValue<long>());
        Assert.Equal(80, u["cache_read_input_tokens"]!.GetValue<long>());
        Assert.Equal(0, u["cache_creation_input_tokens"]!.GetValue<long>());
        Assert.Equal(50, u["output_tokens"]!.GetValue<long>());

        var usage = BridgeUsage.From(resp["usage"])!;
        Assert.Equal(new BridgeUsage(100, 80, 50, 30), usage);
    }

    [Theory]
    [InlineData("""{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"hi"}]}]}""", "end_turn")]
    [InlineData("""{"status":"incomplete","incomplete_details":{"reason":"max_output_tokens"},"output":[{"type":"function_call","call_id":"c","name":"A","arguments":"{"}]}""", "max_tokens")]
    [InlineData("""{"status":"incomplete","incomplete_details":{"reason":"content_filter"},"output":[]}""", "refusal")]
    public void Response_stop_reasons(string json, string expected) =>
        Assert.Equal(expected, S(ResponsesToAnthropic.FromResponseJson(JsonNode.Parse(json)!, "m")["stop_reason"]));

    [Fact]
    public void Refusal_parts_become_text_and_invalid_arguments_are_preserved()
    {
        var m = ResponsesToAnthropic.FromResponseJson(JsonNode.Parse("""
            {"status":"completed","output":[
              {"type":"message","content":[{"type":"refusal","refusal":"I can't help with that."}]},
              {"type":"function_call","call_id":"c","name":"A","arguments":"not json"}]}
            """)!, "m");
        Assert.Equal("I can't help with that.", S(m["content"]![0]!["text"]));
        Assert.Equal("not json", S(m["content"]![1]!["input"]!["_raw_arguments"]));
        Assert.StartsWith("msg_", S(m["id"]));
    }

    [Fact]
    public void Failed_response_becomes_anthropic_error()
    {
        var m = ResponsesToAnthropic.FromResponseJson(JsonNode.Parse("""
            {"status":"failed","error":{"code":"rate_limit_exceeded","message":"Slow down"},"output":[]}
            """)!, "m");
        Assert.Equal("error", S(m["type"]));
        Assert.Equal("rate_limit_error", S(m["error"]!["type"]));
        Assert.Equal("Slow down", S(m["error"]!["message"]));
    }

    // ---------- errors ----------

    [Theory]
    [InlineData(400, """{"error":{"message":"Unsupported parameter: 'max_output_tokens'","type":"invalid_request_error","param":"max_output_tokens","code":"unsupported_parameter"}}""", "invalid_request_error", "Unsupported parameter")]
    [InlineData(401, """{"error":{"message":"Incorrect API key provided","code":"invalid_api_key"}}""", "authentication_error", "Incorrect API key")]
    [InlineData(403, "forbidden", "permission_error", "forbidden")]
    [InlineData(404, """{"detail":"Not Found"}""", "not_found_error", "Not Found")]
    [InlineData(413, "", "request_too_large", "HTTP 413")]
    [InlineData(429, """{"error":{"type":"usage_limit_reached","message":"The usage limit has been reached","plan_type":"plus","resets_in_seconds":3600}}""", "rate_limit_error", "usage limit has been reached")]
    [InlineData(500, "upstream exploded", "api_error", "upstream exploded")]
    [InlineData(502, null, "api_error", "HTTP 502")]
    [InlineData(503, """{"error":{"message":"overloaded"}}""", "overloaded_error", "overloaded")]
    public void Upstream_errors_map_to_anthropic_format(int status, string? body, string type, string messagePart)
    {
        var (s, e) = AnthropicErrors.FromUpstream(status, body);
        Assert.Equal(status, s);
        Assert.Equal("error", S(e["type"]));
        Assert.Equal(type, S(e["error"]!["type"]));
        Assert.Contains(messagePart, S(e["error"]!["message"]));
        Assert.Contains("OpenAI", S(e["error"]!["message"]));
    }

    [Fact]
    public void Sse_error_event_has_anthropic_shape()
    {
        var text = System.Text.Encoding.UTF8.GetString(AnthropicErrors.SseEvent("overloaded_error", "busy"));
        Assert.StartsWith("event: error\ndata: ", text);
        Assert.EndsWith("\n\n", text);
        var data = JsonNode.Parse(text["event: error\ndata: ".Length..].Trim())!;
        Assert.Equal("overloaded_error", S(data["error"]!["type"]));
        Assert.Equal(529, AnthropicErrors.StatusForType("overloaded_error"));
        Assert.Equal(429, AnthropicErrors.StatusForType("rate_limit_error"));
        Assert.Equal(500, AnthropicErrors.StatusForType("api_error"));
    }

    // ---------- envelope, estimator ----------

    [Fact]
    public void Thinking_envelope_round_trips_and_rejects_foreign_signatures()
    {
        var sig = ThinkingEnvelope.Encode("rs_1", "gAAAAAB+/==");
        Assert.StartsWith("bridge1:", sig);
        Assert.DoesNotContain("+", sig[8..]);
        Assert.True(ThinkingEnvelope.TryDecode(sig, out var id, out var enc));
        Assert.Equal("rs_1", id);
        Assert.Equal("gAAAAAB+/==", enc);
        Assert.False(ThinkingEnvelope.TryDecode("EqQBCkYIBxgCKkD", out _, out _));
        Assert.False(ThinkingEnvelope.TryDecode("bridge1:!!!notbase64", out _, out _));
        Assert.False(ThinkingEnvelope.TryDecode(null, out _, out _));
    }

    [Fact]
    public void Token_estimate_scales_with_content()
    {
        var small = BridgeTokenEstimator.Estimate(JsonNode.Parse("""{"messages":[{"role":"user","content":"hi"}]}""")!);
        var text = new string('a', 4000);
        var big = BridgeTokenEstimator.Estimate(JsonNode.Parse($$$"""
            {"system":"{{{text}}}","messages":[{"role":"user","content":[{"type":"text","text":"{{{text}}}"},
              {"type":"image","source":{"type":"base64","media_type":"image/png","data":"AAAA"}}]},
              {"role":"assistant","content":[{"type":"tool_use","id":"t","name":"Bash","input":{"command":"ls"}}]},
              {"role":"user","content":[{"type":"tool_result","tool_use_id":"t","content":"{{{text}}}"}]}],
             "tools":[{"name":"Bash","description":"run","input_schema":{"type":"object"}}]}
            """)!);
        Assert.InRange(small, 1, 10);
        Assert.InRange(big, 3000 + BridgeTokenEstimator.ImageTokens, 3200 + BridgeTokenEstimator.ImageTokens);
    }
}
