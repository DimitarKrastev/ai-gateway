using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using static Gateway.Bridge.BridgeJson;

namespace Gateway.Bridge;

/// <summary>Errors in the Anthropic format (<c>{"type":"error","error":{"type","message"}}</c>) for bridged requests.</summary>
public static class AnthropicErrors
{
    public static string TypeForStatus(int status) => status switch
    {
        401 => "authentication_error",
        403 => "permission_error",
        404 => "not_found_error",
        413 => "request_too_large",
        429 => "rate_limit_error",
        503 or 529 => "overloaded_error",
        >= 500 => "api_error",
        _ => "invalid_request_error",
    };

    public static int StatusForType(string? type) => type switch
    {
        "invalid_request_error" => 400,
        "authentication_error" => 401,
        "permission_error" => 403,
        "not_found_error" => 404,
        "request_too_large" => 413,
        "rate_limit_error" => 429,
        "overloaded_error" => 529,
        _ => 500,
    };

    /// <summary>Anthropic error type for an OpenAI error code/type (stream <c>error</c> / <c>response.failed</c>).</summary>
    public static string TypeForCode(string? code) => code?.ToLowerInvariant() switch
    {
        "rate_limit_exceeded" or "insufficient_quota" or "usage_limit_reached" or "usage_not_included" => "rate_limit_error",
        "server_is_overloaded" or "slow_down" or "overloaded" => "overloaded_error",
        "invalid_request_error" or "invalid_prompt" or "context_length_exceeded" or "invalid_value"
            or "unsupported_parameter" or "invalid_image" or "invalid_base64_image" => "invalid_request_error",
        "invalid_api_key" or "authentication_error" or "token_expired" => "authentication_error",
        "permission_denied" or "model_not_found" => "permission_error",
        _ => "api_error",
    };

    public static JsonObject Error(string type, string message) => new()
    {
        ["type"] = "error",
        ["error"] = new JsonObject { ["type"] = type, ["message"] = message },
    };

    /// <summary>Maps an upstream (OpenAI / Codex backend) HTTP error to an Anthropic error, keeping the status code.</summary>
    public static (int Status, JsonObject Body) FromUpstream(int status, string? body)
    {
        var message = ExtractMessage(body) ?? $"HTTP {status}";
        return (status, Error(TypeForStatus(status), $"OpenAI upstream error ({status}): {message}"));
    }

    /// <summary>An SSE <c>error</c> event (sent mid-stream, like Anthropic does).</summary>
    public static byte[] SseEvent(string type, string message) => SseEvent(Error(type, message));

    public static byte[] SseEvent(JsonObject error) =>
        Encoding.UTF8.GetBytes($"event: error\ndata: {error.ToJsonString()}\n\n");

    /// <summary>The human-readable message of an OpenAI-style error body (JSON or text).</summary>
    public static string? ExtractMessage(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            if (JsonNode.Parse(body) is JsonObject o)
            {
                var err = o["error"];
                var msg = Str(Obj(err)?["message"]) ?? Str(err) ?? Str(Obj(o["detail"])?["message"]) ?? Str(o["detail"]) ?? Str(o["message"]);
                var code = Str(Obj(err)?["code"]) ?? Str(Obj(err)?["type"]);
                if (!string.IsNullOrWhiteSpace(msg)) return code is null || msg.Contains(code) ? msg : $"{msg} [{code}]";
                if (code is not null) return code;
            }
        }
        catch (JsonException)
        {
        }
        var text = body.Trim();
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}
