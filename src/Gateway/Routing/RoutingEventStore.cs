using Gateway.Infrastructure;

namespace Gateway.Routing;

public static class RoutingReasons
{
    /// <summary>The bound account hit its limits (or was rate-limited) and the session moved.</summary>
    public const string Exhausted = "exhausted";
    /// <summary>The bound account was disabled, deleted or cannot serve this endpoint.</summary>
    public const string Unavailable = "unavailable";
    /// <summary>Maximize utilization moved the session to an account whose quota resets sooner.</summary>
    public const string Maximize = "maximize";
    /// <summary>All accounts were exhausted and a reset credit was used.</summary>
    public const string ResetUsed = "reset_used";
}

public sealed record RoutingEvent(long Id, DateTimeOffset At, string Provider, string? SessionId, long? FromAccountId, long? ToAccountId,
    string Reason, string? Detail);

/// <summary>Log of routing decisions that moved a session between accounts or used a reset.</summary>
public sealed class RoutingEventStore(Database db, TimeProvider time)
{
    public void Record(string provider, string? sessionId, long? from, long? to, string reason, string? detail = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO routing_events (at, provider, session_id, from_account_id, to_account_id, reason, detail)
            VALUES ($at, $p, $s, $f, $t, $r, $d);
            """;
        cmd.Parameters.AddWithValue("$at", time.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$s", (object?)sessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$f", (object?)from ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$t", (object?)to ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$r", reason);
        cmd.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<RoutingEvent> List(int limit, string? sessionId = null, long? accountId = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (sessionId != null) { where.Add("session_id = $s"); cmd.Parameters.AddWithValue("$s", sessionId); }
        if (accountId != null) { where.Add("(from_account_id = $a OR to_account_id = $a)"); cmd.Parameters.AddWithValue("$a", accountId.Value); }
        cmd.CommandText = "SELECT * FROM routing_events" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY at DESC, id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 1000));
        using var r = cmd.ExecuteReader();
        var list = new List<RoutingEvent>();
        while (r.Read())
            list.Add(new RoutingEvent(
                r.GetInt64(0), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetInt64(5),
                r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }
}
