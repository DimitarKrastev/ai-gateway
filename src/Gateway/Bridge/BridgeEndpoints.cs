using System.Text.Json;
using System.Text.Json.Nodes;
using Gateway.Accounts;
using Gateway.ClientKeys;
using Gateway.Proxy;
using Microsoft.AspNetCore.Http.Features;

namespace Gateway.Bridge;

public static class BridgeEndpoints
{
    public sealed record ManualModelInput(string? ModelId, string? DisplayName, string? Description, List<string>? Efforts);
    public sealed record SelectionRequest(List<BridgeSelectionInput>? Models);

    public static IServiceCollection AddBridge(this IServiceCollection services)
    {
        services.AddSingleton<BridgeModelCatalog>();
        return services;
    }

    public static void MapBridgeEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/bridge");

        group.MapGet("/models", (BridgeModelCatalog catalog, AccountStore accounts) => CatalogView(catalog, accounts, null));

        group.MapPost("/models/refresh", async (BridgeModelCatalog catalog, AccountStore accounts, CancellationToken ct) =>
            CatalogView(catalog, accounts, await catalog.RefreshAsync(ct)));

        group.MapPost("/models", (ManualModelInput input, BridgeModelCatalog catalog) =>
        {
            var (entry, error) = catalog.AddManual(input.ModelId, input.DisplayName, input.Description, input.Efforts);
            return entry == null
                ? Results.BadRequest(new { error })
                : Results.Created($"/api/bridge/models/{Uri.EscapeDataString(entry.ModelId)}", entry);
        });

        group.MapDelete("/models/{**modelId}", (string modelId, BridgeModelCatalog catalog) => catalog.RemoveManual(modelId) switch
        {
            BridgeModelCatalog.RemoveResult.Removed => Results.NoContent(),
            BridgeModelCatalog.RemoveResult.NotFound => Results.NotFound(new { error = "Unknown model." }),
            _ => Results.Conflict(new { error = "A linked account lists this model; untick it to stop exposing it." }),
        });

        group.MapPut("/selection", (SelectionRequest body, BridgeModelCatalog catalog, AccountStore accounts) =>
        {
            var errors = catalog.SaveSelection(body.Models ?? []);
            if (errors.Count > 0)
                return Results.BadRequest(new { error = string.Join(" ", errors.Select(e => $"{e.ModelId}: {e.Message}").Distinct()), errors });
            return Results.Ok(CatalogView(catalog, accounts, null));
        });

        // Claude Code's gateway model discovery (CLAUDE_CODE_ENABLE_GATEWAY_MODEL_DISCOVERY=1) and model lookups.
        app.MapGet("/anthropic/v1/models", ModelsAsync);
        app.MapGet("/anthropic/v1/models/{id}", ModelAsync);
    }

    private static object CatalogView(BridgeModelCatalog catalog, AccountStore accounts, IReadOnlyList<BridgeRefreshResult>? results)
    {
        var all = accounts.List();
        return new
        {
            models = catalog.List(),
            openaiAccounts = all.Where(a => a.Provider == Providers.OpenAI).Select(a => new { a.Id, a.Name, a.Kind, a.Enabled }),
            anthropicAccounts = all.Count(a => a.Provider == Providers.Anthropic && a.Enabled),
            lastRefreshAt = catalog.LastRefreshAt,
            refreshResults = results,
        };
    }

    private static JsonObject ModelJson(BridgeModel m) => new()
    {
        ["type"] = "model",
        ["id"] = m.AdvertisedId,
        ["display_name"] = m.DisplayName,
        ["created_at"] = m.CreatedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        ["description"] = m.Description,
    };

    /// <summary>
    /// <c>GET /anthropic/v1/models</c>: the real Claude models (proxied through an Anthropic account like any request) with
    /// the exposed OpenAI models first. Without exposed models it is a plain proxied call; without a usable Anthropic
    /// account (none linked, or the upstream fails) it lists the exposed models only. Later pages (<c>after_id</c> /
    /// <c>before_id</c>) are passed through unchanged.
    /// </summary>
    private static async Task ModelsAsync(HttpContext ctx, ProxyHandler proxy, ClientKeyStore keys, BridgeModelCatalog catalog, AccountStore accounts)
    {
        var exposed = catalog.ListExposed();
        var paging = ctx.Request.Query.ContainsKey("after_id") || ctx.Request.Query.ContainsKey("before_id");
        if (exposed.Count == 0 || paging || keys.Validate(ProxyHandler.PresentedKey(ctx.Request, Providers.Anthropic)) == null)
        {
            await proxy.HandleAsync(ctx, Providers.Anthropic, "v1/models");
            return;
        }

        JsonArray? upstream = null;
        bool hasMore = false;
        string? upstreamLastId = null;
        if (accounts.List(Providers.Anthropic).Any(a => a.Enabled))
        {
            var (status, body) = await CaptureAsync(ctx, () => proxy.HandleAsync(ctx, Providers.Anthropic, "v1/models"));
            if (ctx.RequestAborted.IsCancellationRequested) return;
            if (status is >= 200 and < 300)
            {
                try
                {
                    if (JsonNode.Parse(body) is JsonObject o && o["data"] is JsonArray data)
                    {
                        upstream = data;
                        hasMore = o["has_more"]?.GetValueKind() == JsonValueKind.True;
                        upstreamLastId = o["last_id"]?.GetValue<string>();
                    }
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException) { }
            }
        }

        var ids = new HashSet<string>(exposed.Select(m => m.AdvertisedId), StringComparer.OrdinalIgnoreCase);
        var merged = new JsonArray();
        foreach (var m in exposed) merged.Add(ModelJson(m));
        if (upstream != null)
            foreach (var node in upstream.ToList())
            {
                // An exposed id wins over an upstream model of the same id (requests for it go to OpenAI).
                if (node?["id"]?.GetValue<string>() is { } id && ids.Contains(id)) continue;
                upstream.Remove(node);
                merged.Add(node);
            }

        ctx.Response.Clear();
        ctx.Response.StatusCode = 200;
        await ctx.Response.WriteAsJsonAsync(new JsonObject
        {
            ["data"] = merged,
            ["has_more"] = hasMore,
            ["first_id"] = merged.Count > 0 ? merged[0]!["id"]!.GetValue<string>() : null,
            // Keep the upstream cursor so a client paging with after_id continues where Anthropic's list left off.
            ["last_id"] = hasMore && upstreamLastId != null ? upstreamLastId : merged.Count > 0 ? merged[^1]!["id"]!.GetValue<string>() : null,
        }, ctx.RequestAborted);
    }

    /// <summary><c>GET /anthropic/v1/models/{id}</c>: exposed OpenAI models are answered locally, others are proxied.</summary>
    private static async Task ModelAsync(HttpContext ctx, string id, ProxyHandler proxy, ClientKeyStore keys, BridgeModelCatalog catalog)
    {
        if (catalog.ResolveExposed(id) is { } model && keys.Validate(ProxyHandler.PresentedKey(ctx.Request, Providers.Anthropic)) != null)
        {
            await ctx.Response.WriteAsJsonAsync(ModelJson(model), ctx.RequestAborted);
            return;
        }
        await proxy.HandleAsync(ctx, Providers.Anthropic, "v1/models/" + id);
    }

    /// <summary>Runs <paramref name="send"/> with the response body redirected into memory (headers are discarded after).</summary>
    private static async Task<(int Status, byte[] Body)> CaptureAsync(HttpContext ctx, Func<Task> send)
    {
        var original = ctx.Features.Get<IHttpResponseBodyFeature>()!;
        using var buffer = new MemoryStream();
        var capture = new StreamResponseBodyFeature(buffer);
        ctx.Features.Set<IHttpResponseBodyFeature>(capture);
        try
        {
            await send();
            await capture.CompleteAsync();
        }
        finally
        {
            ctx.Features.Set(original);
        }
        return (ctx.Response.StatusCode, buffer.ToArray());
    }
}
