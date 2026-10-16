using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Accounts;
using Gateway.Bridge;
using Gateway.ClientKeys;
using Gateway.Usage;
using Microsoft.AspNetCore.Http.Features;

namespace Gateway.Proxy;

/// <summary>
/// Transparent reverse proxy for <c>/anthropic/*</c> and <c>/openai/*</c>: authenticates the agent's gateway key, picks a
/// linked account (sticky per session), swaps in that account's credentials and streams the upstream response back
/// unchanged. On a 429 it fails over to another account if one is available.
/// <para>Anthropic Messages requests for an OpenAI model exposed through the bridge (<see cref="BridgeModelCatalog"/>) take
/// the same path — client key, session, account selection, credentials, failover, observers — but go to an OpenAI account
/// as a translated Responses request, and the response is translated back (<see cref="BridgeResponseWriter"/>).</para>
/// </summary>
public sealed class ProxyHandler(
    IHttpClientFactory http,
    ClientKeyStore clientKeys,
    IAccountSelector selector,
    UpstreamRouter router,
    TokenRefresher refresher,
    BridgeModelCatalog bridgeCatalog,
    UsageStore usage,
    IEnumerable<IRequestObserver> observers,
    TimeProvider time,
    ILogger<ProxyHandler> log)
{
    public const string UpstreamClientName = "upstream";
    public const int MaxAttempts = 3;
    public const string OAuthBeta = "oauth-2025-04-20";

    private static readonly HashSet<string> SkippedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "connection", "keep-alive", "proxy-connection", "proxy-authorization", "te", "trailer", "transfer-encoding",
        "upgrade", "content-length", "authorization", "x-api-key", "cookie", "chatgpt-account-id",
        // Upstream must answer uncompressed so observers can parse the stream.
        "accept-encoding",
    };

    private static readonly HashSet<string> SkippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "connection", "keep-alive", "proxy-connection", "te", "trailer", "transfer-encoding", "upgrade", "set-cookie",
    };

    private readonly IRequestObserver[] _observers = observers.ToArray();

    public async Task HandleAsync(HttpContext ctx, string provider, string path)
    {
        var ct = ctx.RequestAborted;
        var key = clientKeys.Validate(PresentedKey(ctx.Request, provider));
        if (key == null)
        {
            var message = clientKeys.AnyActive()
                ? "Invalid gateway key. Use a key created in the gateway UI (Settings → Client keys)."
                : "No gateway client keys exist yet. Create one in the gateway UI (Settings → Client keys) and configure your agent with it.";
            await ErrorAsync(ctx, provider, 401, "authentication_error", message);
            return;
        }

        var body = await ReadBodyAsync(ctx.Request, ct);
        var json = TryParseJson(body);
        var session = SessionIdentifier.Identify(provider, ctx.Request.Headers, json, key.Id);
        var requestId = Guid.NewGuid().ToString("N");
        var requestHeaders = ctx.Request.Headers
            .Where(h => !SkippedRequestHeaders.Contains(h.Key))
            .ToDictionary(h => h.Key.ToLowerInvariant(), h => h.Value.ToString());

        // Anthropic Messages request for an exposed OpenAI model → translated to the Responses API of an OpenAI account.
        var bridged = provider == Providers.Anthropic && HttpMethods.IsPost(ctx.Request.Method) ? ResolveBridged(json) : null;
        if (bridged != null && IsPath(path, "v1/messages/count_tokens"))
        {
            await BridgeCountTokensAsync(ctx, body, bridged.Value.Requested, session, key.Id, requestHeaders, path);
            return;
        }
        if (bridged != null && !IsPath(path, "v1/messages")) bridged = null;
        JsonNode? clientNode = null;
        if (bridged != null)
        {
            try { clientNode = JsonNode.Parse(body); }
            catch (JsonException e)
            {
                await ErrorAsync(ctx, provider, 400, "invalid_request_error", $"Invalid JSON body: {e.Message}");
                return;
            }
        }
        var routeProvider = bridged != null ? Providers.OpenAI : provider;
        var upstreamPath = bridged != null ? BridgeUpstreamPath : path;
        var allowed = bridged?.Model.AccountIds;

        var excluded = new List<long>();
        var selection = await selector.SelectAsync(new AccountSelectionRequest(routeProvider, session.Id, upstreamPath, key.Id, excluded, allowed), ct);
        if (selection == null)
        {
            var message = bridged != null
                ? $"No enabled linked OpenAI account is available for {bridged.Value.Model.UpstreamModel} (requested as {bridged.Value.Requested}). Link or enable one in the gateway UI."
                : $"No enabled linked {provider} account is available for this request. Link or enable one in the gateway UI.";
            await ErrorAsync(ctx, provider, 503, provider == Providers.Anthropic ? "overloaded_error" : "server_error", message);
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var account = selection.Account;
            BridgeRequest? bridgeRequest = null;
            if (bridged is { } b)
            {
                try
                {
                    bridgeRequest = AnthropicToResponses.Translate(clientNode!, new BridgeTranslateOptions
                    {
                        AdvertisedModelId = b.Requested, UpstreamModel = b.Model.UpstreamModel,
                        CodexBackend = account.Kind == AccountKinds.ChatGptOAuth,
                        SupportedEfforts = b.Model.SupportedEfforts.Count > 0 ? b.Model.SupportedEfforts : null,
                        SessionId = session.Id,
                    });
                }
                catch (BridgeTranslationException e)
                {
                    await ErrorAsync(ctx, provider, 400, "invalid_request_error", e.Message);
                    return;
                }
                if (attempt == 1 && bridgeRequest.DroppedTools.Count > 0)
                    log.LogInformation("Bridge request {RequestId} for {Model}: dropped tools without an OpenAI equivalent: {Tools}",
                        requestId, b.Model.UpstreamModel, string.Join(", ", bridgeRequest.DroppedTools));
            }
            var pctx = new ProxiedRequestContext
            {
                RequestId = requestId, Attempt = attempt, Provider = routeProvider, Account = account,
                SessionId = session.Id, SessionSource = session.Source, NewSession = selection.NewSession, Rebound = selection.Rebound,
                ClientKeyId = key.Id, Method = ctx.Request.Method, Path = path, Query = ctx.Request.QueryString.Value ?? "",
                UpstreamUri = bridgeRequest != null
                    ? router.BuildUri(account.Kind, BridgeUpstreamPath, "")
                    : router.BuildUri(account.Kind, path, ctx.Request.QueryString.Value ?? ""),
                RequestHeaders = requestHeaders,
                RequestBody = bridgeRequest != null ? JsonSerializer.SerializeToUtf8Bytes(bridgeRequest.Body) : body,
                StartedAt = time.GetUtcNow(),
                Bridge = bridged is { } bb ? new BridgedRequest(bb.Requested, bb.Model.UpstreamModel, body) : null,
            };

            HttpResponseMessage? response = null;
            try
            {
                Notify(o => o.OnRequestStarted(pctx));
                response = await SendWithAuthAsync(ctx.Request, pctx, ct);
                pctx.StatusCode = (int)response.StatusCode;
                pctx.ResponseStartedAt = time.GetUtcNow();
                pctx.ResponseHeaders = response.Headers.Concat(response.Content.Headers)
                    .GroupBy(h => h.Key.ToLowerInvariant())
                    .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(h => h.Value)));
                Notify(o => o.OnResponseStarted(pctx));

                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < MaxAttempts)
                {
                    selector.ReportRateLimited(account, RetryAfter(response));
                    excluded.Add(account.Id);
                    var next = await selector.SelectAsync(new AccountSelectionRequest(routeProvider, session.Id, upstreamPath, key.Id, excluded, allowed), ct);
                    if (next != null)
                    {
                        log.LogInformation("Account {Account} rate-limited; failing over session {Session} to {Next}", account.Id, session.Id, next.Account.Id);
                        pctx.RetriedByGateway = true;
                        await DrainAsync(response, pctx, ct);
                        selection = next with { Rebound = true };
                        continue;
                    }
                }
                else if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    selector.ReportRateLimited(account, RetryAfter(response));
                }

                if (bridgeRequest != null)
                    await BridgeResponseWriter.WriteAsync(ctx, response, bridgeRequest, pctx.Bridge!.RequestedModel,
                        chunk => Notify(o => o.OnResponseChunk(pctx, chunk.Span)), n => pctx.ResponseBytes += n, ct);
                else
                    await StreamToClientAsync(ctx, response, pctx, ct);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pctx.ClientCancelled = true;
                return;
            }
            catch (HttpRequestException e)
            {
                pctx.Error = e.Message;
                if (!ctx.Response.HasStarted)
                {
                    pctx.StatusCode = 502;
                    await ErrorAsync(ctx, provider, 502, "api_error", $"Upstream {provider} request failed: {e.Message}");
                }
                return;
            }
            catch (IOException e)
            {
                // Upstream stream broke mid-response; the client sees a truncated stream like it would directly.
                pctx.Error = e.Message;
                return;
            }
            finally
            {
                pctx.CompletedAt = time.GetUtcNow();
                response?.Dispose();
                foreach (var o in _observers)
                {
                    try { await o.OnCompletedAsync(pctx); }
                    catch (Exception e) { log.LogError(e, "Request observer {Observer} failed", o.GetType().Name); }
                }
            }
        }
    }

    private async Task<HttpResponseMessage> SendWithAuthAsync(HttpRequest incoming, ProxiedRequestContext pctx, CancellationToken ct)
    {
        var client = http.CreateClient(UpstreamClientName);
        var creds = await refresher.EnsureFreshAsync(pctx.Account, ct);
        var response = await client.SendAsync(Build(creds), HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && pctx.Account.IsOAuth && creds.RefreshToken != null)
        {
            var refreshed = await refresher.ForceRefreshAsync(pctx.Account, creds.AccessToken, ct);
            if (refreshed != null)
            {
                response.Dispose();
                response = await client.SendAsync(Build(refreshed), HttpCompletionOption.ResponseHeadersRead, ct);
            }
        }
        return response;

        HttpRequestMessage Build(AccountCredentials c) =>
            pctx.Bridge != null ? BuildBridgeRequest(pctx, c) : BuildUpstreamRequest(incoming, pctx, c);
    }

    /// <summary>
    /// A translated Responses request. The client's Anthropic headers (versions, betas, SDK headers) mean nothing to
    /// OpenAI and are not forwarded. The ChatGPT Codex backend gets the headers Codex sends.
    /// </summary>
    private static HttpRequestMessage BuildBridgeRequest(ProxiedRequestContext pctx, AccountCredentials creds)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, pctx.UpstreamUri) { Content = new ByteArrayContent(pctx.RequestBody) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        req.Headers.Accept.ParseAdd("text/event-stream");
        req.Headers.Accept.ParseAdd("application/json");
        if (pctx.Account.Kind == AccountKinds.ChatGptOAuth)
        {
            req.Headers.TryAddWithoutValidation("OpenAI-Beta", "responses=experimental");
            req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
            req.Headers.TryAddWithoutValidation("session_id", pctx.SessionId);
            req.Headers.TryAddWithoutValidation("user-agent", $"codex_cli_rs/{BridgeModelCatalog.CodexClientVersion} (ai-coding-gateway bridge)");
        }
        else
        {
            req.Headers.TryAddWithoutValidation("user-agent", "ai-coding-gateway-bridge");
        }
        InjectCredentials(req, pctx.Account.Kind, creds);
        return req;
    }

    /// <summary>Upstream path of bridged requests (Responses API; the router maps it to the Codex backend for ChatGPT accounts).</summary>
    public const string BridgeUpstreamPath = "v1/responses";

    private static bool IsPath(string path, string expected) => string.Equals(path.Trim('/'), expected, StringComparison.OrdinalIgnoreCase);

    private (BridgeModel Model, string Requested)? ResolveBridged(JsonElement? json)
    {
        if (json is not { ValueKind: JsonValueKind.Object } j || !j.TryGetProperty("model", out var m) || m.ValueKind != JsonValueKind.String)
            return null;
        var requested = m.GetString()!;
        return bridgeCatalog.ResolveExposed(requested) is { } model ? (model, requested) : null;
    }

    /// <summary>
    /// <c>count_tokens</c> for a bridged model: OpenAI has no equivalent on the Codex backend, so the gateway estimates
    /// locally (never calls a provider) and logs the call like other count_tokens requests (no account, no tokens).
    /// </summary>
    private async Task BridgeCountTokensAsync(
        HttpContext ctx, byte[] body, string requested, SessionIdentity session, long clientKeyId, IReadOnlyDictionary<string, string> headers, string path)
    {
        var started = time.GetUtcNow();
        int estimate;
        try
        {
            estimate = JsonNode.Parse(body) is { } node ? BridgeTokenEstimator.Estimate(node) : 0;
        }
        catch (JsonException e)
        {
            await ErrorAsync(ctx, Providers.Anthropic, 400, "invalid_request_error", $"Invalid JSON body: {e.Message}");
            return;
        }
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { input_tokens = estimate });
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.Body.WriteAsync(payload, ctx.RequestAborted);

        try
        {
            var (client, version) = RequestInfo.ParseClient(headers);
            var at = time.GetUtcNow().ToUnixTimeMilliseconds();
            var bridge = bridgeCatalog.ResolveExposed(requested);
            usage.Insert(new RequestRecord
            {
                RequestId = Guid.NewGuid().ToString("N"), Attempt = 1, Provider = Providers.OpenAI, AccountId = null,
                SessionId = session.Source != "derived" ? session.Id : null, SessionSource = session.Source,
                ClientKeyId = clientKeyId, Client = client, ClientVersion = version, Method = ctx.Request.Method, Path = path,
                Endpoint = Endpoints.CountTokens, Model = bridge?.UpstreamModel, RequestedModel = requested,
                Bridge = BridgedRequest.AnthropicToOpenAI, Status = 200, StartedAt = started.ToUnixTimeMilliseconds(),
                ResponseStartedAt = at, FirstByteAt = at, CompletedAt = at, RequestBytes = body.Length, ResponseBytes = payload.Length,
            });
        }
        catch (Exception e)
        {
            log.LogError(e, "Failed to record a bridged count_tokens call");
        }
    }

    private static HttpRequestMessage BuildUpstreamRequest(HttpRequest incoming, ProxiedRequestContext pctx, AccountCredentials creds)
    {
        var req = new HttpRequestMessage(new HttpMethod(pctx.Method), pctx.UpstreamUri);
        if (pctx.RequestBody.Length > 0 || !(HttpMethods.IsGet(pctx.Method) || HttpMethods.IsHead(pctx.Method)))
            req.Content = new ByteArrayContent(pctx.RequestBody);

        foreach (var (name, value) in incoming.Headers)
        {
            if (SkippedRequestHeaders.Contains(name)) continue;
            if (name.StartsWith("content-", StringComparison.OrdinalIgnoreCase))
                req.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)value!);
            else
                req.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)value!);
        }

        InjectCredentials(req, pctx.Account.Kind, creds, incoming.Headers["anthropic-beta"].ToString());
        return req;
    }

    /// <summary>
    /// Puts the account's credential on an upstream request (API key / OAuth bearer, <c>chatgpt-account-id</c>, Anthropic
    /// OAuth beta flag). <paramref name="anthropicBeta"/> is the client's <c>anthropic-beta</c> header, if any.
    /// </summary>
    public static void InjectCredentials(HttpRequestMessage req, string kind, AccountCredentials creds, string? anthropicBeta = null)
    {
        switch (kind)
        {
            case AccountKinds.AnthropicApiKey:
                req.Headers.TryAddWithoutValidation("x-api-key", creds.ApiKey);
                SetBeta(req, anthropicBeta, add: false);
                break;
            case AccountKinds.AnthropicOAuth:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
                SetBeta(req, anthropicBeta, add: true);
                break;
            case AccountKinds.OpenAIApiKey:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.ApiKey);
                break;
            case AccountKinds.ChatGptOAuth:
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", creds.AccessToken);
                req.Headers.TryAddWithoutValidation("chatgpt-account-id", creds.ChatGptAccountId);
                break;
        }
    }

    /// <summary>Subscription tokens need the OAuth beta flag; API keys must not send it.</summary>
    private static void SetBeta(HttpRequestMessage req, string? anthropicBeta, bool add)
    {
        var flags = (anthropicBeta ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(f => f != OAuthBeta).ToList();
        if (add) flags.Add(OAuthBeta);
        req.Headers.Remove("anthropic-beta");
        if (flags.Count > 0) req.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", flags));
    }

    private async Task StreamToClientAsync(HttpContext ctx, HttpResponseMessage response, ProxiedRequestContext pctx, CancellationToken ct)
    {
        ctx.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            if (!SkippedResponseHeaders.Contains(name))
                ctx.Response.Headers[name] = values.ToArray();
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        await ctx.Response.StartAsync(ct);

        await using var upstream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await upstream.ReadAsync(buffer, ct)) > 0)
        {
            pctx.ResponseBytes += n;
            var chunk = buffer.AsMemory(0, n);
            Notify(o => o.OnResponseChunk(pctx, chunk.Span));
            await ctx.Response.Body.WriteAsync(chunk, ct);
            await ctx.Response.Body.FlushAsync(ct);
        }
    }

    private async Task DrainAsync(HttpResponseMessage response, ProxiedRequestContext pctx, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        pctx.ResponseBytes = bytes.Length;
        Notify(o => o.OnResponseChunk(pctx, bytes));
    }

    private delegate void ObserverCall(IRequestObserver observer);

    private void Notify(ObserverCall call)
    {
        foreach (var o in _observers)
        {
            try { call(o); }
            catch (Exception e) { log.LogError(e, "Request observer {Observer} failed", o.GetType().Name); }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var ra = response.Headers.RetryAfter;
        if (ra?.Delta is { } d) return d;
        if (ra?.Date is { } date) return date - DateTimeOffset.UtcNow;
        return null;
    }

    /// <summary>The gateway client key the agent presented (Bearer, or <c>x-api-key</c> for Anthropic).</summary>
    public static string? PresentedKey(HttpRequest req, string provider)
    {
        var auth = req.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return auth[7..].Trim();
        if (provider == Providers.Anthropic && req.Headers.TryGetValue("x-api-key", out var k)) return k.ToString().Trim();
        return null;
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest req, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        await req.Body.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static JsonElement? TryParseJson(byte[] body)
    {
        if (body.Length == 0 || body[0] is not ((byte)'{' or (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t')) return null;
        try { return JsonDocument.Parse(body).RootElement; }
        catch (JsonException) { return null; }
    }

    /// <summary>Writes an error in the provider's own error format so agents surface it normally.</summary>
    public static Task ErrorAsync(HttpContext ctx, string provider, int status, string type, string message)
    {
        ctx.Response.StatusCode = status;
        object payload = provider == Providers.Anthropic
            ? new { type = "error", error = new { type, message } }
            : new { error = new { message, type, param = (string?)null, code = status == 401 ? "invalid_api_key" : null } };
        return ctx.Response.WriteAsJsonAsync(payload);
    }
}
