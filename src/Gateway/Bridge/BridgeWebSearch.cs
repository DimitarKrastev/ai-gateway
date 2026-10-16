using System.Text.Json.Nodes;
using static Gateway.Bridge.BridgeJson;

namespace Gateway.Bridge;

/// <summary>
/// Anthropic's server-side web search on OpenAI models. Claude Code's WebSearch tool sends a side request to the session's
/// model with the server tool <c>{type: "web_search_20250305", name: "web_search", allowed_domains, blocked_domains,
/// user_location, max_uses}</c> and reads the searches back from <c>server_tool_use</c> + <c>web_search_tool_result</c> blocks.
/// Upstream that is the Responses API's built-in <c>{type: "web_search"}</c> tool, whose <c>web_search_call</c> output items
/// (<c>action.query</c>, <c>action.sources</c>) are translated back. OpenAI has no blocked-domains list or use limit.
/// </summary>
public static class BridgeWebSearch
{
    public const string ToolName = "web_search";

    /// <summary>An Anthropic server web search tool (<c>web_search_YYYYMMDD</c>).</summary>
    public static bool IsServerTool(JsonObject tool) =>
        Str(tool["type"]) is { } type && type.StartsWith("web_search_", StringComparison.Ordinal);

    /// <summary>The Responses API tool for an Anthropic web search tool; unsupported options are listed in <paramref name="dropped"/>.</summary>
    public static JsonObject UpstreamTool(JsonObject tool, List<string> dropped)
    {
        var result = new JsonObject { ["type"] = "web_search" };
        if (Arr(tool["allowed_domains"]) is { Count: > 0 } allowed)
            result["filters"] = new JsonObject { ["allowed_domains"] = allowed.DeepClone() };
        if (Arr(tool["blocked_domains"]) is { Count: > 0 }) dropped.Add("web_search blocked_domains");
        if (Obj(tool["user_location"]) is { } loc)
        {
            var location = new JsonObject { ["type"] = "approximate" };
            foreach (var field in new[] { "city", "region", "country", "timezone" })
                if (Str(loc[field]) is { } v) location[field] = v;
            result["user_location"] = location;
        }
        return result;
    }

    /// <summary>A search the model ran, as <c>server_tool_use</c> + <c>web_search_tool_result</c>; null for other actions.</summary>
    public static (JsonObject Use, JsonObject Result)? Blocks(JsonObject item, IReadOnlyDictionary<string, string>? titles = null)
    {
        var action = Obj(item["action"]);
        if (action is not null && Str(action["type"]) is { } kind && kind != "search") return null;
        var query = Str(action?["query"]) ?? Arr(action?["queries"])?.Select(q => Str(q)).FirstOrDefault(q => q is not null) ?? "";
        var id = ToolUseId(Str(item["id"]));
        var use = new JsonObject
        {
            ["type"] = "server_tool_use",
            ["id"] = id,
            ["name"] = ToolName,
            ["input"] = new JsonObject { ["query"] = query },
        };

        JsonNode content;
        if (Str(item["status"]) is "failed" or "incomplete")
        {
            content = new JsonObject { ["type"] = "web_search_tool_result_error", ["error_code"] = "unavailable" };
        }
        else
        {
            var results = new JsonArray();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in Arr(action?["sources"])?.OfType<JsonObject>() ?? [])
            {
                if (Str(source["url"]) is not { Length: > 0 } url || !seen.Add(Normalize(url))) continue;
                results.Add(new JsonObject
                {
                    ["type"] = "web_search_result",
                    ["url"] = url,
                    ["title"] = titles?.GetValueOrDefault(Normalize(url)) ?? Str(source["title"]) ?? TitleFromUrl(url),
                    ["encrypted_content"] = "",
                    ["page_age"] = null,
                });
            }
            content = results;
        }
        return (use, new JsonObject { ["type"] = "web_search_tool_result", ["tool_use_id"] = id, ["content"] = content });
    }

    /// <summary>Page titles from the message's <c>url_citation</c> annotations, keyed by normalized URL.</summary>
    public static Dictionary<string, string> TitlesFrom(IEnumerable<JsonObject> outputItems)
    {
        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in outputItems.Where(i => Str(i["type"]) == "message"))
            foreach (var part in Arr(item["content"])?.OfType<JsonObject>() ?? [])
                foreach (var a in Arr(part["annotations"])?.OfType<JsonObject>() ?? [])
                    if (Str(a["type"]) == "url_citation" && Str(a["url"]) is { } url && Str(a["title"]) is { Length: > 0 } title)
                        titles.TryAdd(Normalize(url), title);
        return titles;
    }

    private static string ToolUseId(string? itemId) =>
        "srvtoolu_" + (string.IsNullOrEmpty(itemId) ? Guid.NewGuid().ToString("N") : itemId.StartsWith("ws_", StringComparison.Ordinal) ? itemId[3..] : itemId);

    /// <summary>Same page regardless of trailing slash, fragment or the <c>utm_source=openai</c> OpenAI appends.</summary>
    private static string Normalize(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var query = string.Join("&", u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => !p.StartsWith("utm_source=", StringComparison.OrdinalIgnoreCase)));
        return $"{u.Host}{u.AbsolutePath.TrimEnd('/')}{(query.Length > 0 ? "?" + query : "")}";
    }

    private static string TitleFromUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.Host + u.AbsolutePath.TrimEnd('/')).Replace("www.", "") : url;
}
