using System.Globalization;
using System.Text.Json;

namespace Gateway.Fakes;

/// <summary>
/// Fake Anthropic API. Mimics api.anthropic.com (Messages API, SSE streaming, rate-limit headers),
/// the OAuth token endpoint (console.anthropic.com/v1/oauth/token) and the Claude subscription usage endpoint.
/// Reset credits (<c>/api/oauth/usage/resets</c>) are an assumed capability — no public Anthropic API exists.
/// </summary>
public static class AnthropicFake
{
    public const string OAuthBeta = "oauth-2025-04-20";

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/v1/messages", Messages);
        app.MapPost("/v1/messages/count_tokens", CountTokens);
        app.MapGet("/api/oauth/usage", Usage);
        app.MapGet("/api/oauth/usage/resets", ListResets);
        app.MapPost("/api/oauth/usage/resets/{id}/redeem", RedeemReset);
        app.MapPost("/v1/oauth/token", Token);
    }

    private static FakeProviderState State(HttpContext ctx) => ctx.RequestServices.GetRequiredService<FakeStates>().Anthropic;

    // ---------- auth ----------

    /// <summary>Authenticates the request; on failure writes the error response and returns null.</summary>
    internal static async Task<FakeAccount?> AuthenticateAsync(HttpContext ctx, FakeProviderState state, bool oauthOnly = false)
    {
        var req = ctx.Request;
        string? apiKey = req.Headers["x-api-key"].FirstOrDefault();
        string? auth = req.Headers.Authorization.FirstOrDefault();
        string? bearer = auth != null && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? auth[7..].Trim() : null;
        FakeAccount? acct;

        if (!string.IsNullOrEmpty(apiKey) && !oauthOnly)
        {
            acct = state.FindByApiKey(apiKey);
            if (acct == null && state.Options.AutoRegister) acct = state.Register(new FakeAccountSpec(Kind: "api_key", ApiKey: apiKey));
            if (acct == null) { await ErrorAsync(ctx, 401, "authentication_error", "invalid x-api-key"); return null; }
        }
        else if (!string.IsNullOrEmpty(bearer))
        {
            var betas = string.Join(",", req.Headers["anthropic-beta"].ToArray());
            if (!oauthOnly && !betas.Contains(OAuthBeta, StringComparison.Ordinal))
            {
                await ErrorAsync(ctx, 401, "authentication_error", "OAuth authentication is currently not supported.");
                return null;
            }
            acct = state.FindByAccessToken(bearer);
            if (acct == null && state.Options.AutoRegister) acct = state.Register(new FakeAccountSpec(Kind: "oauth", AccessToken: bearer));
            if (acct == null) { await ErrorAsync(ctx, 401, "authentication_error", "Invalid bearer token"); return null; }
            if (acct.AccessTokenExpiresAt <= state.Now)
            {
                await ErrorAsync(ctx, 401, "authentication_error", "OAuth token has expired. Please obtain a new token or refresh your existing token.");
                return null;
            }
        }
        else
        {
            await ErrorAsync(ctx, 401, "authentication_error", oauthOnly ? "OAuth token required" : "x-api-key header is required");
            return null;
        }
        ctx.Items["fake.account"] = acct.Id;
        return acct;
    }

    internal static Task ErrorAsync(HttpContext ctx, int status, string type, string message)
    {
        var requestId = FakeProviderState.NewId("req");
        ctx.Response.StatusCode = status;
        ctx.Response.Headers["request-id"] = requestId;
        return ctx.Response.WriteAsJsonAsync(new { type = "error", error = new { type, message }, request_id = requestId });
    }

    // ---------- limits ----------

    private static string Abbrev(string window) => window switch
    {
        "five_hour" => "5h",
        "seven_day" => "7d",
        "seven_day_opus" => "7d_opus",
        _ => window,
    };

    private static string Status(double util) => util >= 100 ? "rejected" : util >= 80 ? "allowed_warning" : "allowed";

    private static string Fraction(double percent) => (percent / 100.0).ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Adds rate-limit headers for the account's current state. Call under the state gate.</summary>
    private static void AddLimitHeadersLocked(HttpResponse res, FakeProviderState state, FakeAccount acct, FakeLimitWindow? blocking)
    {
        res.Headers["anthropic-organization-id"] = "org-" + acct.Id;
        if (acct.Kind == FakeAuthKind.OAuth)
        {
            var claimable = acct.Windows.Where(w => w.Name is "five_hour" or "seven_day").ToList();
            var representative = blocking ?? claimable.OrderByDescending(w => w.Utilization).FirstOrDefault();
            res.Headers["anthropic-ratelimit-unified-status"] = blocking != null ? "rejected" : Status(acct.Windows.Select(w => w.Utilization).DefaultIfEmpty(0).Max());
            foreach (var w in acct.Windows)
            {
                var p = "anthropic-ratelimit-unified-" + Abbrev(w.Name);
                res.Headers[p + "-status"] = Status(w.Utilization);
                res.Headers[p + "-reset"] = w.ResetsAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
                res.Headers[p + "-utilization"] = Fraction(w.Utilization);
            }
            if (representative != null)
            {
                res.Headers["anthropic-ratelimit-unified-representative-claim"] = representative.Name;
                res.Headers["anthropic-ratelimit-unified-reset"] = representative.ResetsAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            }
            res.Headers["anthropic-ratelimit-unified-fallback-percentage"] = "0.5";
            res.Headers["anthropic-ratelimit-unified-overage-status"] = "rejected";
            res.Headers["anthropic-ratelimit-unified-overage-disabled-reason"] = "org_level_disabled";
        }
        else
        {
            var reset = acct.MinuteWindowStart.AddMinutes(1).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var tokensRemaining = Math.Max(0, acct.TokensLimit - acct.TokensUsed).ToString(CultureInfo.InvariantCulture);
            var tokensLimit = acct.TokensLimit.ToString(CultureInfo.InvariantCulture);
            res.Headers["anthropic-ratelimit-requests-limit"] = acct.RequestsLimit.ToString(CultureInfo.InvariantCulture);
            res.Headers["anthropic-ratelimit-requests-remaining"] = Math.Max(0, acct.RequestsLimit - acct.RequestsUsed).ToString(CultureInfo.InvariantCulture);
            res.Headers["anthropic-ratelimit-requests-reset"] = reset;
            foreach (var kind in new[] { "tokens", "input-tokens", "output-tokens" })
            {
                res.Headers[$"anthropic-ratelimit-{kind}-limit"] = tokensLimit;
                res.Headers[$"anthropic-ratelimit-{kind}-remaining"] = tokensRemaining;
                res.Headers[$"anthropic-ratelimit-{kind}-reset"] = reset;
            }
        }
    }

    // ---------- /v1/messages ----------

    private sealed record TokenUsage(int Input, int CacheWrite, int CacheRead, int Output);

    private static async Task Messages(HttpContext ctx)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state);
        if (acct == null) return;
        if (string.IsNullOrEmpty(ctx.Request.Headers["anthropic-version"]))
        {
            await ErrorAsync(ctx, 400, "invalid_request_error", "anthropic-version: header is required");
            return;
        }
        if (!TryParseBody(ctx, out var doc)) { await ErrorAsync(ctx, 400, "invalid_request_error", "Request body is not valid JSON"); return; }
        using var _ = doc;
        var body = doc.RootElement;
        var model = body.Str("model");
        var messages = body.Prop("messages");
        if (string.IsNullOrEmpty(model)) { await ErrorAsync(ctx, 400, "invalid_request_error", "model: Field required"); return; }
        if (body.Prop("max_tokens") is not { ValueKind: JsonValueKind.Number } maxTok) { await ErrorAsync(ctx, 400, "invalid_request_error", "max_tokens: Field required"); return; }
        if (messages is not { ValueKind: JsonValueKind.Array } msgs || msgs.GetArrayLength() == 0) { await ErrorAsync(ctx, 400, "invalid_request_error", "messages: Field required"); return; }

        var (lastText, lastIsToolResult) = LastUser(msgs);
        var tools = body.Prop("tools").Arr().Select(t => t.Str("name")).OfType<string>().ToList();
        var turn = FakeTurn.Plan("anthropic", acct.Id, lastText, lastIsToolResult, tools, "toolu_01");
        if (turn.ErrorStatus is { } errStatus)
        {
            var type = errStatus switch { 529 => "overloaded_error", 429 => "rate_limit_error", 400 => "invalid_request_error", 404 => "not_found_error", _ => "api_error" };
            await ErrorAsync(ctx, errStatus, type, type == "overloaded_error" ? "Overloaded" : "Injected fake error");
            return;
        }

        // Token accounting with a simulated prompt cache (prefix up to the last message is cacheable when cache_control is used).
        var total = Tokens.Estimate(body.Prop("system")) + Tokens.Estimate(messages) + Tokens.Estimate(body.Prop("tools"));
        var usage = new TokenUsage(total, 0, 0, Math.Min(turn.OutputTokens, maxTok.GetInt32()));
        if (ctx.Items["fake.body"] is string raw && raw.Contains("\"cache_control\"", StringComparison.Ordinal))
        {
            var tail = Math.Min(total, Math.Max(1, Tokens.Estimate(msgs[msgs.GetArrayLength() - 1].GetRawText())));
            var prefix = total - tail;
            var key = Tokens.Hash(model, body.Prop("system")?.GetRawText(), msgs[0].GetRawText());
            var read = state.CacheLookupAndStore(acct, key, prefix);
            usage = usage with { Input = tail, CacheRead = read, CacheWrite = prefix - read };
        }
        var truncated = turn.OutputTokens > maxTok.GetInt32();
        var stopReason = truncated ? "max_tokens" : turn.ToolCalls.Count > 0 ? "tool_use" : "end_turn";
        var toolCalls = truncated ? [] : turn.ToolCalls;

        lock (state.Gate)
        {
            var blocking = state.BlockingWindowLocked(acct);
            if (blocking == null) state.ConsumeLocked(acct, usage.Input + usage.CacheWrite + usage.CacheRead + usage.Output);
            AddLimitHeadersLocked(ctx.Response, state, acct, blocking);
            if (blocking != null)
            {
                var retry = Math.Max(1, (int)Math.Ceiling((blocking.ResetsAt - state.Now).TotalSeconds));
                ctx.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
                ctx.Items["fake.blocked"] = true;
            }
        }
        if (ctx.Items.ContainsKey("fake.blocked"))
        {
            await ErrorAsync(ctx, 429, "rate_limit_error", "This request would exceed your account's rate limit. Please try again later.");
            return;
        }

        var msgId = "msg_01" + FakeProviderState.RandomToken(16).Replace("-", "x").Replace("_", "y");
        var requestId = FakeProviderState.NewId("req");
        ctx.Response.Headers["request-id"] = requestId;

        if (body.Bool("stream"))
            await StreamAsync(ctx, state, model, msgId, turn, toolCalls, stopReason, usage);
        else
        {
            var content = new List<object> { new { type = "text", text = turn.Text } };
            content.AddRange(toolCalls.Select(c => (object)new { type = "tool_use", id = c.Id, name = c.Name, input = JsonDocument.Parse(c.InputJson).RootElement }));
            await ctx.Response.WriteAsJsonAsync(new
            {
                id = msgId, type = "message", role = "assistant", model, content,
                stop_reason = stopReason, stop_sequence = (string?)null,
                usage = UsageJson(usage),
            });
        }
    }

    private static object UsageJson(TokenUsage u) => new
    {
        input_tokens = u.Input,
        cache_creation_input_tokens = u.CacheWrite,
        cache_read_input_tokens = u.CacheRead,
        cache_creation = new { ephemeral_5m_input_tokens = u.CacheWrite, ephemeral_1h_input_tokens = 0 },
        output_tokens = u.Output,
        service_tier = "standard",
    };

    private static async Task StreamAsync(HttpContext ctx, FakeProviderState state, string model, string msgId, FakeTurn turn,
        List<FakeToolCall> toolCalls, string stopReason, TokenUsage usage)
    {
        SseWriter.Start(ctx.Response);
        var sse = new SseWriter(ctx.Response, turn.DelayMs ?? state.Options.StreamChunkDelayMs);
        await sse.EventAsync("message_start", new
        {
            type = "message_start",
            message = new
            {
                id = msgId, type = "message", role = "assistant", model, content = Array.Empty<object>(),
                stop_reason = (string?)null, stop_sequence = (string?)null,
                usage = new
                {
                    input_tokens = usage.Input, cache_creation_input_tokens = usage.CacheWrite, cache_read_input_tokens = usage.CacheRead,
                    cache_creation = new { ephemeral_5m_input_tokens = usage.CacheWrite, ephemeral_1h_input_tokens = 0 },
                    output_tokens = 1, service_tier = "standard",
                },
            },
        });
        await sse.EventAsync("content_block_start", new { type = "content_block_start", index = 0, content_block = new { type = "text", text = "" } });
        await sse.EventAsync("ping", new { type = "ping" });
        foreach (var chunk in Chunks(turn.Text, 24))
            await sse.EventAsync("content_block_delta", new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = chunk } });
        await sse.EventAsync("content_block_stop", new { type = "content_block_stop", index = 0 });

        var index = 1;
        foreach (var call in toolCalls)
        {
            await sse.EventAsync("content_block_start", new { type = "content_block_start", index, content_block = new { type = "tool_use", id = call.Id, name = call.Name, input = new { } } });
            foreach (var part in Chunks(call.InputJson, 12))
                await sse.EventAsync("content_block_delta", new { type = "content_block_delta", index, delta = new { type = "input_json_delta", partial_json = part } });
            await sse.EventAsync("content_block_stop", new { type = "content_block_stop", index });
            index++;
        }
        // message_delta usage is cumulative (current API includes the input counts too) — consumers must not sum it with message_start.
        await sse.EventAsync("message_delta", new
        {
            type = "message_delta",
            delta = new { stop_reason = stopReason, stop_sequence = (string?)null },
            usage = new { input_tokens = usage.Input, cache_creation_input_tokens = usage.CacheWrite, cache_read_input_tokens = usage.CacheRead, output_tokens = usage.Output },
        });
        await sse.EventAsync("message_stop", new { type = "message_stop" });
    }

    internal static IEnumerable<string> Chunks(string s, int size)
    {
        for (var i = 0; i < s.Length; i += size) yield return s.Substring(i, Math.Min(size, s.Length - i));
    }

    private static (string Text, bool IsToolResult) LastUser(JsonElement messages)
    {
        var last = messages[messages.GetArrayLength() - 1];
        if (last.Str("role") != "user") return ("", false);
        var content = last.Prop("content");
        if (content is { ValueKind: JsonValueKind.String } s) return (s.GetString() ?? "", false);
        var texts = new List<string>();
        var toolResult = false;
        foreach (var block in content.Arr())
        {
            var type = block.Str("type");
            if (type == "text") texts.Add(block.Str("text") ?? "");
            else if (type == "tool_result") toolResult = true;
        }
        return (string.Join("\n", texts), toolResult);
    }

    internal static bool TryParseBody(HttpContext ctx, out JsonDocument doc)
    {
        try
        {
            doc = JsonDocument.Parse(ctx.Items["fake.body"] as string ?? "");
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            doc = null!;
            return false;
        }
    }

    // ---------- count_tokens ----------

    private static async Task CountTokens(HttpContext ctx)
    {
        var state = State(ctx);
        if (await AuthenticateAsync(ctx, state) == null) return;
        if (!TryParseBody(ctx, out var doc)) { await ErrorAsync(ctx, 400, "invalid_request_error", "Request body is not valid JSON"); return; }
        using var _ = doc;
        var b = doc.RootElement;
        await ctx.Response.WriteAsJsonAsync(new { input_tokens = Tokens.Estimate(b.Prop("system")) + Tokens.Estimate(b.Prop("messages")) + Tokens.Estimate(b.Prop("tools")) });
    }

    // ---------- subscription usage & resets ----------

    internal static Dictionary<string, object?> UsageBody(FakeProviderState state, FakeAccount acct)
    {
        lock (state.Gate)
        {
            state.RollLocked(acct);
            var body = new Dictionary<string, object?>();
            foreach (var w in acct.Windows)
                body[w.Name] = new { utilization = Math.Round(w.Utilization, 2), resets_at = w.ResetsAt.ToString("yyyy-MM-ddTHH:mm:ss.ffffffzzz", CultureInfo.InvariantCulture) };
            body["seven_day_oauth_apps"] = null;
            body["extra_usage"] = new { is_enabled = false, monthly_limit = (int?)null, used_credits = (int?)null, utilization = (double?)null };
            return body;
        }
    }

    private static async Task Usage(HttpContext ctx)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, oauthOnly: true);
        if (acct == null) return;
        await ctx.Response.WriteAsJsonAsync(UsageBody(state, acct));
    }

    internal static object ResetsBody(FakeProviderState state, FakeAccount acct)
    {
        lock (state.Gate)
        {
            state.RollLocked(acct);
            return new
            {
                resets = acct.Resets.OrderBy(r => r.ExpiresAt).Select(r => new { id = r.Id, expires_at = r.ExpiresAt.ToString("O", CultureInfo.InvariantCulture) }).ToList(),
            };
        }
    }

    private static async Task ListResets(HttpContext ctx)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, oauthOnly: true);
        if (acct == null) return;
        await ctx.Response.WriteAsJsonAsync(ResetsBody(state, acct));
    }

    private static async Task RedeemReset(HttpContext ctx, string id)
    {
        var state = State(ctx);
        var acct = await AuthenticateAsync(ctx, state, oauthOnly: true);
        if (acct == null) return;
        if (!state.RedeemReset(acct, id)) { await ErrorAsync(ctx, 404, "not_found_error", "Reset not found or expired"); return; }
        await ctx.Response.WriteAsJsonAsync(new { redeemed = id, usage = UsageBody(state, acct) });
    }

    // ---------- OAuth token refresh ----------

    private static async Task Token(HttpContext ctx)
    {
        var state = State(ctx);
        var form = await ReadTokenRequestAsync(ctx);
        if (form.GetValueOrDefault("grant_type") != "refresh_token")
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "unsupported_grant_type", error_description = "Only refresh_token is supported by the fake" });
            return;
        }
        var acct = state.Refresh(form.GetValueOrDefault("refresh_token") ?? "");
        if (acct == null)
        {
            ctx.Response.StatusCode = 400;
            await ctx.Response.WriteAsJsonAsync(new { error = "invalid_grant", error_description = "Refresh token not found or invalid" });
            return;
        }
        ctx.Items["fake.account"] = acct.Id;
        await ctx.Response.WriteAsJsonAsync(new
        {
            token_type = "Bearer",
            access_token = acct.AccessToken,
            expires_in = (int)state.Options.AccessTokenLifetime.TotalSeconds,
            refresh_token = acct.RefreshToken,
            scope = "user:inference user:profile",
            organization = new { uuid = "org-" + acct.Id, name = "Fake Org" },
            account = new { uuid = acct.Id, email_address = acct.Id + "@example.test" },
        });
    }

    /// <summary>Reads a token request sent as JSON or as application/x-www-form-urlencoded.</summary>
    internal static async Task<Dictionary<string, string?>> ReadTokenRequestAsync(HttpContext ctx)
    {
        var raw = ctx.Items["fake.body"] as string ?? "";
        if (ctx.Request.HasFormContentType)
        {
            var form = await ctx.Request.ReadFormAsync();
            return form.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());
        }
        try
        {
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText());
        }
        catch (JsonException)
        {
            return new();
        }
    }
}
