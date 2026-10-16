using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Gateway.Accounts;
using Gateway.ClientKeys;
using Microsoft.AspNetCore.Http.Features;

namespace Gateway.Proxy;

/// <summary>
/// Transparent reverse proxy for <c>/anthropic/*</c> and <c>/openai/*</c>: authenticates the agent's gateway key, picks a
/// linked account (sticky per session), swaps in that account's credentials and streams the upstream response back
/// unchanged. On a 429 it fails over to another account if one is available.
/// </summary>
public sealed class ProxyHandler(
    IHttpClientFactory http,
    ClientKeyStore clientKeys,
    IAccountSelector selector,
    UpstreamRouter router,
    TokenRefresher refresher,
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

        var excluded = new List<long>();
        var selection = await selector.SelectAsync(new AccountSelectionRequest(provider, session.Id, path, key.Id, excluded), ct);
        if (selection == null)
        {
            await ErrorAsync(ctx, provider, 503, provider == Providers.Anthropic ? "overloaded_error" : "server_error",
                $"No enabled linked {provider} account is available for this request. Link or enable one in the gateway UI.");
            return;
        }

        for (var attempt = 1; ; attempt++)
        {
            var account = selection.Account;
            var pctx = new ProxiedRequestContext
            {
                RequestId = requestId, Attempt = attempt, Provider = provider, Account = account,
                SessionId = session.Id, SessionSource = session.Source, NewSession = selection.NewSession, Rebound = selection.Rebound,
                ClientKeyId = key.Id, Method = ctx.Request.Method, Path = path, Query = ctx.Request.QueryString.Value ?? "",
                UpstreamUri = router.BuildUri(account.Kind, path, ctx.Request.QueryString.Value ?? ""),
                RequestHeaders = requestHeaders, RequestBody = body, StartedAt = time.GetUtcNow(),
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
                    var next = await selector.SelectAsync(new AccountSelectionRequest(provider, session.Id, path, key.Id, excluded), ct);
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
        var response = await client.SendAsync(BuildUpstreamRequest(incoming, pctx, creds), HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.Unauthorized && pctx.Account.IsOAuth && creds.RefreshToken != null)
        {
            var refreshed = await refresher.ForceRefreshAsync(pctx.Account, creds.AccessToken, ct);
            if (refreshed != null)
            {
                response.Dispose();
                response = await client.SendAsync(BuildUpstreamRequest(incoming, pctx, refreshed), HttpCompletionOption.ResponseHeadersRead, ct);
            }
        }
        return response;
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
