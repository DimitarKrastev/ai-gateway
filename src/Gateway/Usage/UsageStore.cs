using Gateway.Infrastructure;

namespace Gateway.Usage;

/// <summary>Writes request records; reads live in <see cref="AnalyticsService"/>.</summary>
public sealed class UsageStore(Database db)
{
    public long Insert(RequestRecord r)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO requests (
                request_id, attempt, retried, provider, account_id, account_kind, session_id, session_source, client_key_id,
                client, client_version, agent_id, agent_role, method, path, endpoint, model, stream, status, error_type,
                stop_reason, cancelled, input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,
                context_tokens, tool_calls, cost_usd, priced, started_at, response_started_at, first_byte_at, completed_at,
                request_bytes, response_bytes)
            VALUES (
                $request_id, $attempt, $retried, $provider, $account_id, $account_kind, $session_id, $session_source, $client_key_id,
                $client, $client_version, $agent_id, $agent_role, $method, $path, $endpoint, $model, $stream, $status, $error_type,
                $stop_reason, $cancelled, $input_tokens, $output_tokens, $cache_read_tokens, $cache_write_tokens, $reasoning_tokens,
                $context_tokens, $tool_calls, $cost_usd, $priced, $started_at, $response_started_at, $first_byte_at, $completed_at,
                $request_bytes, $response_bytes)
            RETURNING id;
            """;
        void P(string name, object? value) => cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        P("$request_id", r.RequestId); P("$attempt", r.Attempt); P("$retried", r.Retried ? 1 : 0); P("$provider", r.Provider);
        P("$account_id", r.AccountId); P("$account_kind", r.AccountKind); P("$session_id", r.SessionId); P("$session_source", r.SessionSource);
        P("$client_key_id", r.ClientKeyId); P("$client", r.Client); P("$client_version", r.ClientVersion); P("$agent_id", r.AgentId);
        P("$agent_role", r.AgentRole); P("$method", r.Method); P("$path", r.Path); P("$endpoint", r.Endpoint); P("$model", r.Model);
        P("$stream", r.Stream ? 1 : 0); P("$status", r.Status); P("$error_type", r.ErrorType); P("$stop_reason", r.StopReason);
        P("$cancelled", r.Cancelled ? 1 : 0); P("$input_tokens", r.InputTokens); P("$output_tokens", r.OutputTokens);
        P("$cache_read_tokens", r.CacheReadTokens); P("$cache_write_tokens", r.CacheWriteTokens); P("$reasoning_tokens", r.ReasoningTokens);
        P("$context_tokens", r.ContextTokens); P("$tool_calls", r.ToolCalls); P("$cost_usd", r.CostUsd); P("$priced", r.Priced ? 1 : 0);
        P("$started_at", r.StartedAt); P("$response_started_at", r.ResponseStartedAt); P("$first_byte_at", r.FirstByteAt);
        P("$completed_at", r.CompletedAt); P("$request_bytes", r.RequestBytes); P("$response_bytes", r.ResponseBytes);
        var id = Convert.ToInt64(cmd.ExecuteScalar());

        foreach (var (tool, count) in r.Tools)
        {
            using var t = conn.CreateCommand();
            t.Transaction = tx;
            t.CommandText = "INSERT INTO request_tools (request_row_id, started_at, tool, count) VALUES ($id, $at, $tool, $count);";
            t.Parameters.AddWithValue("$id", id);
            t.Parameters.AddWithValue("$at", r.StartedAt);
            t.Parameters.AddWithValue("$tool", tool);
            t.Parameters.AddWithValue("$count", count);
            t.ExecuteNonQuery();
        }
        tx.Commit();
        return id;
    }
}
