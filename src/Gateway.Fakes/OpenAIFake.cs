using System.Globalization;
using System.Text.Json;

namespace Gateway.Fakes;

/// <summary>
/// Fake OpenAI: the public API (api.openai.com /v1/responses, /v1/chat/completions, x-ratelimit-* headers),
/// the ChatGPT Codex backend used by Codex with a ChatGPT subscription (chatgpt.com /backend-api/codex/responses,
/// x-codex-* headers, /backend-api/wham/usage) and the OAuth token endpoint (auth.openai.com /oauth/token).
/// Reset credits (<c>/backend-api/wham/resets</c>) are an assumed capability — no public API exists.
/// </summary>
public static class OpenAIFake
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/responses", ctx => Responses(ctx, chatgpt: false));
        app.MapPost("/v1/chat/completions", ChatCompletions);
        app.MapPost("/backend-api/codex/responses", ctx => Responses(ctx, chatgpt: true));
        app.MapGet("/backend-api/codex/models", CodexModels);
        app.MapGet("/backend-api/wham/usage", Usage);
        app.MapGet("/backend-api/wham/resets", ListResets);
        app.MapPost("/backend-api/wham/resets/{id}/redeem", RedeemReset);
        app.MapPost("/oauth/token", Token);
    }

    private static FakeProviderState State(HttpContext ctx) => ctx.RequestServices.GetRequiredService<FakeStates>().OpenAI;

    // ---------- auth ----------

    internal static async Task<FakeAccount?> AuthenticateAsync(HttpContext ctx, FakeProviderState state, bool chatgpt)
    {
        var auth = ctx.Request.Headers.Authorization.FirstOrDefault();
        var bearer = auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        if (string.IsNullOrEmpty(bearer))
        {
            await ErrorAsync(ctx, 401, "You didn't provide an API key. You need to provide your API key in an Authorization header using Bearer auth.", "invalid_request_error", null);
            return null;
        }
        FakeAccount? acct;
        if (!chatgpt)
        {
            acct = state.FindByApiKey(bearer);
            if (acct == null && state.Options.AutoRegister) acct = state.Register(new FakeAccountSpec(Kind: "api_key", ApiKey: bearer));
            if (acct == null) { await ErrorAsync(ctx, 401, "Incorrect API key provided.", "invalid_request_error", "invalid_api_key"); return null; }
        }
        else
        {
            var accountHeader = ctx.Request.Headers["chatgpt-account-id"].FirstOrDefault();
            acct = state.FindByAccessToken(bearer);
            if (acct == null && state.Options.AutoRegister && !string.IsNullOrEmpty(accountHeader))
                acct = state.Register(new FakeAccountSpec(Kind: "oauth", AccessToken: bearer, ChatGptAccountId: accountHeader));
            if (acct == null) { await ErrorAsync(ctx, 401, "Could not validate your credentials", "invalid_request_error", "invalid_token"); return null; }
            if (acct.AccessTokenExpiresAt <= state.Now)
            {
                await ErrorAsync(ctx, 401, "Provided authentication token is expired. Please try signing in again.", "invalid_request_error", "token_expired");
                return null;
            }
            if (string.IsNullOrEmpty(accountHeader)) { await ErrorAsync(ctx, 401, "Missing chatgpt-account-id header", "invalid_request_error", "missing_account_id"); return null; }
            if (acct.ChatGptAccountId != null && accountHeader != acct.ChatGptAccountId)
            {
                await ErrorAsync(ctx, 403, "chatgpt-account-id does not match the token", "invalid_request_error", "account_mismatch");
                return null;
            }
        }
        ctx.Items["fake.account"] = acct.Id;
        return acct;
    }

    internal static Task ErrorAsync(HttpContext ctx, int status, string message, string? type, string? code)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.Headers["x-request-id"] = FakeProviderState.NewId("req");
        return ctx.Response.WriteAsJsonAsync(new { error = new { message, type, param = (string?)null, code } });
    }

    // ---------- limits ----------

    private static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m{t.Seconds}s" : $"{t.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s";
    }

    private static void AddLimitHeadersLocked(HttpResponse res, FakeProviderState state, FakeAccount acct)
    {
        var now = state.Now;
        if (acct.Kind == FakeAuthKind.OAuth)
        {
            foreach (var w in acct.Windows)
            {
                var p = "x-codex-" + w.Name;
                res.Headers[p + "-used-percent"] = Math.Round(w.Utilization, 2).ToString(CultureInfo.InvariantCulture);
                res.Headers[p + "-window-minutes"] = w.WindowMinutes.ToString(CultureInfo.InvariantCulture);
                res.Headers[p + "-reset-after-seconds"] = Math.Max(0, (long)(w.ResetsAt - now).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                res.Headers[p + "-reset-at"] = w.ResetsAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            }
        }
        else
        {
            var reset = Duration(acct.MinuteWindowStart.AddMinutes(1) - now);
            res.Headers["x-ratelimit-limit-requests"] = acct.RequestsLimit.ToString(CultureInfo.InvariantCulture);
            res.Headers["x-ratelimit-remaining-requests"] = Math.Max(0, acct.RequestsLimit - acct.RequestsUsed).ToString(CultureInfo.InvariantCulture);
            res.Headers["x-ratelimit-reset-requests"] = reset;
            res.Headers["x-ratelimit-limit-tokens"] = acct.TokensLimit.ToString(CultureInfo.InvariantCulture);
            res.Headers["x-ratelimit-remaining-tokens"] = Math.Max(0, acct.TokensLimit - acct.TokensUsed).ToString(CultureInfo.InvariantCulture);
            res.Headers["x-ratelimit-reset-tokens"] = reset;
        }
    }

    private sealed record TokenUsage(int Input, int Cached, int Output, int Reasoning);

    /// <summary>Checks limits, adds headers and consumes quota. On exhaustion writes the 429 and returns false.</summary>
    private static async Task<bool> AdmitAsync(HttpContext ctx, FakeProviderState state, FakeAccount acct, TokenUsage usage, bool chatgpt)
    {
        FakeLimitWindow? blocking;
        lock (state.Gate)
        {
            blocking = state.BlockingWindowLocked(acct);
            if (blocking == null) state.ConsumeLocked(acct, usage.Input + usage.Output);
            AddLimitHeadersLocked(ctx.Response, state, acct);
        }
        ctx.Response.Headers["x-request-id"] = FakeProviderState.NewId("req");
        ctx.Response.Headers["openai-processing-ms"] = "42";
        if (blocking == null) return true;

        var seconds = Math.Max(1, (long)Math.Ceiling((blocking.ResetsAt - state.Now).TotalSeconds));
        ctx.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        ctx.Response.StatusCode = 429;
        if (chatgpt)
            await ctx.Response.WriteAsJsonAsync(new
            {
                error = new
                {
                    type = "usage_limit_reached", message = "The usage limit has been reached", plan_type = acct.PlanType,
                    resets_at = blocking.ResetsAt.ToUnixTimeSeconds(), resets_in_seconds = seconds,
                },
            });
        else
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Rate limit reached for requests", type = "requests", param = (string?)null, code = "rate_limit_exceeded" } });
        return false;
    }

    private static async Task<bool> InjectedErrorAsync(HttpContext ctx, FakeTurn turn)
    {
        if (turn.ErrorStatus is not { } status) return false;
        await ErrorAsync(ctx, status, "Injected fake error", status >= 500 ? "server_error" : "invalid_request_error", null);
        return true;
    }

    // ---------- /v1/responses and /backend-api/codex/responses ----------

    private static async Task Responses(HttpContext ctx, bool chatgpt)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, chatgpt);
        if (acct == null) return;
        if (!AnthropicFake.TryParseBody(ctx, out var doc)) { await ErrorAsync(ctx, 400, "We could not parse the JSON body of your request.", "invalid_request_error", null); return; }
        using var _ = doc;
        var body = doc.RootElement;
        var model = body.Str("model");
        if (string.IsNullOrEmpty(model)) { await ErrorAsync(ctx, 400, "Missing required parameter: 'model'.", "invalid_request_error", "missing_required_parameter"); return; }
        var stream = body.Bool("stream");
        if (chatgpt && !stream)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { detail = "Stream must be set to true" });
            return;
        }

        var input = body.Prop("input");
        var (lastText, lastIsToolResult) = LastInput(input);
        var tools = body.Prop("tools").Arr().Select(t => t.Str("name") ?? t.Str("type")).OfType<string>().ToList();
        var turn = FakeTurn.Plan("openai", acct.Id, lastText, lastIsToolResult, tools, "call_");
        if (await InjectedErrorAsync(ctx, turn)) return;

        var total = Tokens.Estimate(body.Prop("instructions")) + Tokens.Estimate(input) + Tokens.Estimate(body.Prop("tools"));
        var firstItem = input is { ValueKind: JsonValueKind.Array } arr && arr.GetArrayLength() > 0 ? arr[0].GetRawText() : input?.GetRawText();
        var cacheKey = body.Str("prompt_cache_key") ?? Tokens.Hash(model, body.Prop("instructions")?.GetRawText(), firstItem);
        var cached = state.CacheLookupAndStore(acct, cacheKey, total);
        var reasoning = body.Prop("reasoning") is { ValueKind: JsonValueKind.Object } ? Math.Max(8, turn.OutputTokens / 2) : 0;
        var usage = new TokenUsage(total, cached, turn.OutputTokens + reasoning, reasoning);

        if (!await AdmitAsync(ctx, state, acct, usage, chatgpt)) return;

        var respId = "resp_" + FakeProviderState.RandomToken(24).Replace("-", "a").Replace("_", "b");
        var created = state.Now.ToUnixTimeSeconds();
        var items = new List<Dictionary<string, object?>>();
        if (turn.ToolCalls.Count == 0)
            items.Add(new() { ["id"] = "msg_" + FakeProviderState.RandomToken(16), ["type"] = "message", ["status"] = "completed", ["role"] = "assistant",
                ["content"] = new object[] { new { type = "output_text", text = turn.Text, annotations = Array.Empty<object>(), logprobs = Array.Empty<object>() } } });
        foreach (var c in turn.ToolCalls)
            items.Add(new() { ["id"] = "fc_" + FakeProviderState.RandomToken(16), ["type"] = "function_call", ["status"] = "completed", ["name"] = c.Name, ["call_id"] = c.Id, ["arguments"] = c.InputJson });

        object Response(string status, IEnumerable<object> output, bool withUsage) => new Dictionary<string, object?>
        {
            ["id"] = respId, ["object"] = "response", ["created_at"] = created, ["status"] = status, ["error"] = null,
            ["incomplete_details"] = null, ["instructions"] = body.Str("instructions"), ["model"] = model, ["output"] = output,
            ["parallel_tool_calls"] = true, ["previous_response_id"] = body.Str("previous_response_id"),
            ["prompt_cache_key"] = body.Str("prompt_cache_key"), ["store"] = body.Prop("store")?.ValueKind == JsonValueKind.True,
            ["usage"] = withUsage ? new
            {
                input_tokens = usage.Input, input_tokens_details = new { cached_tokens = usage.Cached },
                output_tokens = usage.Output, output_tokens_details = new { reasoning_tokens = usage.Reasoning },
                total_tokens = usage.Input + usage.Output,
            } : null,
        };

        if (!stream)
        {
            await ctx.Response.WriteAsJsonAsync(Response("completed", items, true));
            return;
        }

        SseWriter.Start(ctx.Response);
        var sse = new SseWriter(ctx.Response, turn.DelayMs ?? state.Options.StreamChunkDelayMs);
        var seq = 0;
        Task Emit(string type, Dictionary<string, object?> data)
        {
            data["type"] = type;
            data["sequence_number"] = seq++;
            return sse.EventAsync(type, data);
        }
        await Emit("response.created", new() { ["response"] = Response("in_progress", [], false) });
        await Emit("response.in_progress", new() { ["response"] = Response("in_progress", [], false) });
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var id = (string)item["id"]!;
            if ((string)item["type"]! == "message")
            {
                await Emit("response.output_item.added", new() { ["output_index"] = i, ["item"] = new { id, type = "message", status = "in_progress", role = "assistant", content = Array.Empty<object>() } });
                await Emit("response.content_part.added", new() { ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, ["part"] = new { type = "output_text", text = "", annotations = Array.Empty<object>() } });
                foreach (var chunk in AnthropicFake.Chunks(turn.Text, 24))
                    await Emit("response.output_text.delta", new() { ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, ["delta"] = chunk, ["logprobs"] = Array.Empty<object>() });
                await Emit("response.output_text.done", new() { ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, ["text"] = turn.Text, ["logprobs"] = Array.Empty<object>() });
                await Emit("response.content_part.done", new() { ["item_id"] = id, ["output_index"] = i, ["content_index"] = 0, ["part"] = new { type = "output_text", text = turn.Text, annotations = Array.Empty<object>() } });
            }
            else
            {
                var args = (string)item["arguments"]!;
                await Emit("response.output_item.added", new() { ["output_index"] = i, ["item"] = new { id, type = "function_call", status = "in_progress", name = item["name"], call_id = item["call_id"], arguments = "" } });
                foreach (var part in AnthropicFake.Chunks(args, 12))
                    await Emit("response.function_call_arguments.delta", new() { ["item_id"] = id, ["output_index"] = i, ["delta"] = part });
                await Emit("response.function_call_arguments.done", new() { ["item_id"] = id, ["output_index"] = i, ["arguments"] = args });
            }
            await Emit("response.output_item.done", new() { ["output_index"] = i, ["item"] = item });
        }
        await Emit("response.completed", new() { ["response"] = Response("completed", items, true) });
    }

    private static (string Text, bool IsToolResult) LastInput(JsonElement? input)
    {
        if (input is { ValueKind: JsonValueKind.String } s) return (s.GetString() ?? "", false);
        if (input is not { ValueKind: JsonValueKind.Array } arr || arr.GetArrayLength() == 0) return ("", false);
        var last = arr[arr.GetArrayLength() - 1];
        var type = last.Str("type");
        if (type != null && type.EndsWith("_output", StringComparison.Ordinal)) return ("", true);
        if ((type is null or "message") && last.Str("role") == "user")
        {
            var content = last.Prop("content");
            if (content is { ValueKind: JsonValueKind.String } cs) return (cs.GetString() ?? "", false);
            return (string.Join("\n", content.Arr().Select(p => p.Str("text")).OfType<string>()), false);
        }
        return ("", false);
    }

    // ---------- /v1/chat/completions ----------

    private static async Task ChatCompletions(HttpContext ctx)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, chatgpt: false);
        if (acct == null) return;
        if (!AnthropicFake.TryParseBody(ctx, out var doc)) { await ErrorAsync(ctx, 400, "We could not parse the JSON body of your request.", "invalid_request_error", null); return; }
        using var _ = doc;
        var body = doc.RootElement;
        var model = body.Str("model");
        if (string.IsNullOrEmpty(model)) { await ErrorAsync(ctx, 400, "Missing required parameter: 'model'.", "invalid_request_error", "missing_required_parameter"); return; }
        if (body.Prop("messages") is not { ValueKind: JsonValueKind.Array } msgs || msgs.GetArrayLength() == 0)
        {
            await ErrorAsync(ctx, 400, "Missing required parameter: 'messages'.", "invalid_request_error", "missing_required_parameter");
            return;
        }

        var last = msgs[msgs.GetArrayLength() - 1];
        var lastIsTool = last.Str("role") == "tool";
        var lastText = last.Str("role") == "user"
            ? last.Prop("content") is { ValueKind: JsonValueKind.String } c ? c.GetString() ?? "" : string.Join("\n", last.Prop("content").Arr().Select(p => p.Str("text")).OfType<string>())
            : "";
        var tools = body.Prop("tools").Arr().Select(t => t.Prop("function")?.Str("name")).OfType<string>().ToList();
        var turn = FakeTurn.Plan("openai", acct.Id, lastText, lastIsTool, tools, "call_");
        if (await InjectedErrorAsync(ctx, turn)) return;

        var total = Tokens.Estimate(msgs) + Tokens.Estimate(body.Prop("tools"));
        var cached = state.CacheLookupAndStore(acct, Tokens.Hash(model, msgs[0].GetRawText()), total);
        var usage = new TokenUsage(total, cached, turn.OutputTokens, 0);
        if (!await AdmitAsync(ctx, state, acct, usage, chatgpt: false)) return;

        var id = "chatcmpl-" + FakeProviderState.RandomToken(18).Replace("-", "a").Replace("_", "b");
        var created = state.Now.ToUnixTimeSeconds();
        var finish = turn.ToolCalls.Count > 0 ? "tool_calls" : "stop";
        var usageJson = new
        {
            prompt_tokens = usage.Input, completion_tokens = usage.Output, total_tokens = usage.Input + usage.Output,
            prompt_tokens_details = new { cached_tokens = usage.Cached }, completion_tokens_details = new { reasoning_tokens = usage.Reasoning },
        };

        if (!body.Bool("stream"))
        {
            await ctx.Response.WriteAsJsonAsync(new
            {
                id, @object = "chat.completion", created, model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        message = new
                        {
                            role = "assistant",
                            content = turn.ToolCalls.Count > 0 ? null : turn.Text,
                            tool_calls = turn.ToolCalls.Count > 0 ? turn.ToolCalls.Select(t => new { id = t.Id, type = "function", function = new { name = t.Name, arguments = t.InputJson } }).ToArray() : null,
                        },
                        finish_reason = finish,
                    },
                },
                usage = usageJson,
            });
            return;
        }

        var includeUsage = body.Prop("stream_options") is { } so && so.Bool("include_usage");
        SseWriter.Start(ctx.Response);
        var sse = new SseWriter(ctx.Response, turn.DelayMs ?? state.Options.StreamChunkDelayMs);
        Task Chunk(object delta, string? finishReason) => sse.EventAsync(null, new Dictionary<string, object?>
        {
            ["id"] = id, ["object"] = "chat.completion.chunk", ["created"] = created, ["model"] = model,
            ["choices"] = new[] { new { index = 0, delta, finish_reason = finishReason } },
            ["usage"] = includeUsage ? null : Missing,
        }.Where(kv => kv.Value != Missing).ToDictionary());

        await Chunk(new { role = "assistant", content = "" }, null);
        if (turn.ToolCalls.Count == 0)
            foreach (var part in AnthropicFake.Chunks(turn.Text, 24)) await Chunk(new { content = part }, null);
        for (var i = 0; i < turn.ToolCalls.Count; i++)
        {
            var t = turn.ToolCalls[i];
            await Chunk(new { tool_calls = new[] { new { index = i, id = t.Id, type = "function", function = new { name = t.Name, arguments = "" } } } }, null);
            foreach (var part in AnthropicFake.Chunks(t.InputJson, 12))
                await Chunk(new { tool_calls = new[] { new { index = i, function = new { arguments = part } } } }, null);
        }
        await Chunk(new { }, finish);
        if (includeUsage)
            await sse.EventAsync(null, new { id, @object = "chat.completion.chunk", created, model, choices = Array.Empty<object>(), usage = usageJson });
        await sse.EventAsync(null, "[DONE]");
    }

    private static readonly object Missing = new();

    // ---------- ChatGPT usage, resets, models ----------

    internal static Dictionary<string, object?> UsageBody(FakeProviderState state, FakeAccount acct)
    {
        lock (state.Gate)
        {
            var blocking = state.BlockingWindowLocked(acct);
            var rateLimit = new Dictionary<string, object?> { ["allowed"] = blocking == null, ["limit_reached"] = blocking != null };
            var now = state.Now;
            foreach (var w in acct.Windows)
                rateLimit[w.Name + "_window"] = new
                {
                    used_percent = Math.Round(w.Utilization, 2),
                    limit_window_seconds = w.WindowMinutes * 60,
                    reset_after_seconds = Math.Max(0, (long)(w.ResetsAt - now).TotalSeconds),
                    reset_at = w.ResetsAt.ToUnixTimeSeconds(),
                };
            return new()
            {
                ["plan_type"] = acct.PlanType,
                ["rate_limit"] = rateLimit,
                ["credits"] = new { has_credits = false, unlimited = false, balance = "0" },
            };
        }
    }

    private static async Task Usage(HttpContext ctx)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, chatgpt: true);
        if (acct == null) return;
        await ctx.Response.WriteAsJsonAsync(UsageBody(state, acct));
    }

    private static async Task ListResets(HttpContext ctx)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, chatgpt: true);
        if (acct == null) return;
        await ctx.Response.WriteAsJsonAsync(AnthropicFake.ResetsBody(state, acct));
    }

    private static async Task RedeemReset(HttpContext ctx, string id)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, chatgpt: true);
        if (acct == null) return;
        if (!state.RedeemReset(acct, id)) { await ErrorAsync(ctx, 404, "Reset not found or expired", "invalid_request_error", "not_found"); return; }
        await ctx.Response.WriteAsJsonAsync(new { redeemed = id, usage = UsageBody(state, acct) });
    }

    private static async Task CodexModels(HttpContext ctx)
    {
        var state = State(ctx);
        if (await AuthenticateAsync(ctx, state, chatgpt: true) == null) return;
        await ctx.Response.WriteAsJsonAsync(new { models = new[] { new { slug = "gpt-5-codex", display_name = "gpt-5-codex" }, new { slug = "gpt-5", display_name = "gpt-5" } } });
    }

    internal static async Task ApiModels(HttpContext ctx)
    {
        var state = State(ctx);
        if (await AuthenticateAsync(ctx, state, chatgpt: false) == null) return;
        var created = state.Now.ToUnixTimeSeconds();
        await ctx.Response.WriteAsJsonAsync(new
        {
            @object = "list",
            data = new[] { "gpt-5-codex", "gpt-5", "gpt-5-mini" }.Select(m => new { id = m, @object = "model", created, owned_by = "openai" }),
        });
    }

    // ---------- OAuth token refresh ----------

    private static async Task Token(HttpContext ctx)
    {
        var state = State(ctx);
        var form = await AnthropicFake.ReadTokenRequestAsync(ctx);
        if (form.GetValueOrDefault("grant_type") != "refresh_token")
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "unsupported_grant_type", error_description = "Only refresh_token is supported by the fake" });
            return;
        }
        var acct = state.Refresh(form.GetValueOrDefault("refresh_token") ?? "");
        if (acct == null)
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = new { message = "Your refresh token has already been used or is invalid.", type = "invalid_request_error", code = "refresh_token_invalidated" } });
            return;
        }
        ctx.Items["fake.account"] = acct.Id;
        var exp = (state.Now + state.Options.AccessTokenLifetime).ToUnixTimeSeconds();
        var idToken = FakeProviderState.ChatGptJwt(acct.Id, exp, acct.ChatGptAccountId, acct.PlanType, acct.Id + "@example.test");
        await ctx.Response.WriteAsJsonAsync(new
        {
            id_token = idToken,
            access_token = acct.AccessToken,
            refresh_token = acct.RefreshToken,
            expires_in = (int)state.Options.AccessTokenLifetime.TotalSeconds,
            token_type = "Bearer",
            scope = "openid profile email offline_access",
        });
    }
}
