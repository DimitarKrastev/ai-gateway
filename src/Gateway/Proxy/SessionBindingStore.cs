using Gateway.Infrastructure;

namespace Gateway.Proxy;

public sealed record SessionBinding(string Provider, string SessionId, long AccountId, long? ClientKeyId,
    DateTimeOffset FirstSeen, DateTimeOffset LastSeen, long RequestCount);

/// <summary>Persistent session → linked account bindings (sticky sessions).</summary>
public sealed class SessionBindingStore(Database db, TimeProvider time)
{
    public SessionBinding? Get(string provider, string sessionId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM session_bindings WHERE provider = $p AND session_id = $s;";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$s", sessionId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new SessionBinding(
            r.GetString(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetInt64(3),
            DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(4)), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(5)), r.GetInt64(6));
    }

    /// <summary>Binds (or rebinds) the session to <paramref name="accountId"/> and counts the request.</summary>
    public void Bind(string provider, string sessionId, long accountId, long? clientKeyId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_bindings (provider, session_id, account_id, client_key_id, first_seen, last_seen, request_count)
            VALUES ($p, $s, $a, $k, $now, $now, 1)
            ON CONFLICT(provider, session_id) DO UPDATE SET
                account_id = excluded.account_id, last_seen = excluded.last_seen, request_count = request_count + 1;
            """;
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$s", sessionId);
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$k", (object?)clientKeyId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    /// <summary>Number of sessions per account seen since <paramref name="since"/>.</summary>
    public Dictionary<long, int> ActiveSessionCounts(string provider, DateTimeOffset since)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT account_id, COUNT(*) FROM session_bindings WHERE provider = $p AND last_seen >= $since GROUP BY account_id;";
        cmd.Parameters.AddWithValue("$p", provider);
        cmd.Parameters.AddWithValue("$since", since.ToUnixTimeMilliseconds());
        using var r = cmd.ExecuteReader();
        var counts = new Dictionary<long, int>();
        while (r.Read()) counts[r.GetInt64(0)] = r.GetInt32(1);
        return counts;
    }
}
