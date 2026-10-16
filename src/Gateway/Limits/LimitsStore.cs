using Gateway.Infrastructure;
using Microsoft.Data.Sqlite;
using Gateway.Live;

namespace Gateway.Limits;

/// <summary>Latest limit snapshot, exhaustion marks and reset credits per linked account (SQLite).</summary>
public sealed class LimitsStore(Database db, TimeProvider time, LiveNotifier notifier)
{
    // ---------- windows ----------

    /// <summary>Upserts the observed windows; windows not mentioned keep their previous values.</summary>
    public void UpsertWindows(long accountId, IEnumerable<LimitWindow> windows)
    {
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var w in windows)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO account_limits (account_id, key, label, utilization, used, lim, remaining, resets_at, window_minutes, status, source, updated_at)
                VALUES ($a, $k, $label, $u, $used, $lim, $rem, $reset, $wm, $status, $source, $at)
                ON CONFLICT(account_id, key) DO UPDATE SET
                    label = excluded.label, utilization = excluded.utilization, used = excluded.used, lim = excluded.lim,
                    remaining = excluded.remaining, resets_at = excluded.resets_at,
                    window_minutes = COALESCE(excluded.window_minutes, window_minutes),
                    status = excluded.status, source = excluded.source, updated_at = excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$k", w.Key);
            cmd.Parameters.AddWithValue("$label", w.Label);
            cmd.Parameters.AddWithValue("$u", Db(w.Utilization));
            cmd.Parameters.AddWithValue("$used", Db(w.Used));
            cmd.Parameters.AddWithValue("$lim", Db(w.Limit));
            cmd.Parameters.AddWithValue("$rem", Db(w.Remaining));
            cmd.Parameters.AddWithValue("$reset", Db(w.ResetsAt?.ToUnixTimeMilliseconds()));
            cmd.Parameters.AddWithValue("$wm", Db(w.WindowMinutes));
            cmd.Parameters.AddWithValue("$status", w.Status);
            cmd.Parameters.AddWithValue("$source", w.Source);
            cmd.Parameters.AddWithValue("$at", w.UpdatedAt.ToUnixTimeMilliseconds());
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
        notifier.Notify(LiveTopic.Limits);
    }

    /// <summary>A reset was applied: every quota window starts over at 0 %.</summary>
    public void ZeroWindows(long accountId)
    {
        var now = time.GetUtcNow();
        Execute("""
            UPDATE account_limits SET
                utilization = CASE WHEN utilization IS NULL THEN NULL ELSE 0 END,
                used = CASE WHEN used IS NULL THEN NULL ELSE 0 END,
                remaining = COALESCE(lim, remaining),
                resets_at = CASE WHEN window_minutes IS NULL THEN resets_at ELSE $now + window_minutes * 60000 END,
                status = 'ok', source = 'manual', updated_at = $now
            WHERE account_id = $a;
            """, ("$a", accountId), ("$now", now.ToUnixTimeMilliseconds()));
    }

    // ---------- account state ----------

    private readonly object _stateGate = new();

    public void UpdateState(long accountId, Func<AccountLimitState, AccountLimitState> change)
    {
        lock (_stateGate) UpdateStateLocked(accountId, change);
    }

    private void UpdateStateLocked(long accountId, Func<AccountLimitState, AccountLimitState> change)
    {
        var current = GetState(accountId);
        var next = change(current);
        Execute("""
            INSERT INTO account_limit_state (account_id, exhausted_until, exhausted_reason, unified_status, representative_claim,
                overage_status, plan_type, refreshed_at, refresh_error, resets_synced_at)
            VALUES ($a, $eu, $er, $us, $rc, $os, $pt, $ra, $re, $rs)
            ON CONFLICT(account_id) DO UPDATE SET
                exhausted_until = excluded.exhausted_until, exhausted_reason = excluded.exhausted_reason,
                unified_status = excluded.unified_status, representative_claim = excluded.representative_claim,
                overage_status = excluded.overage_status, plan_type = excluded.plan_type, refreshed_at = excluded.refreshed_at,
                refresh_error = excluded.refresh_error, resets_synced_at = excluded.resets_synced_at;
            """,
            ("$a", accountId), ("$eu", next.ExhaustedUntil?.ToUnixTimeMilliseconds()), ("$er", next.ExhaustedReason),
            ("$us", next.UnifiedStatus), ("$rc", next.RepresentativeClaim), ("$os", next.OverageStatus), ("$pt", next.PlanType),
            ("$ra", next.RefreshedAt?.ToUnixTimeMilliseconds()), ("$re", next.RefreshError), ("$rs", next.ResetsSyncedAt?.ToUnixTimeMilliseconds()));
    }

    public AccountLimitState GetState(long accountId) => LoadStates(accountId).GetValueOrDefault(accountId) ?? new AccountLimitState { AccountId = accountId };

    /// <summary>Limits of the given accounts (all accounts when null).</summary>
    public Dictionary<long, AccountLimits> Load(IReadOnlyCollection<long>? accountIds = null)
    {
        var states = LoadStates(null);
        var windows = new Dictionary<long, List<LimitWindow>>();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM account_limits ORDER BY account_id, COALESCE(window_minutes, 0) DESC, key;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(r.GetOrdinal("account_id"));
                if (!windows.TryGetValue(id, out var list)) windows[id] = list = [];
                list.Add(ReadWindow(r));
            }
        }
        var ids = accountIds ?? states.Keys.Union(windows.Keys).ToList();
        return ids.ToDictionary(id => id, id => new AccountLimits(
            id, states.GetValueOrDefault(id) ?? new AccountLimitState { AccountId = id },
            (IReadOnlyList<LimitWindow>?)windows.GetValueOrDefault(id)?.OrderBy(WindowOrder).ToList() ?? []));
    }

    public AccountLimits Load(long accountId) => Load([accountId])[accountId];

    /// <summary>Session windows first, then weekly, then per-minute rate limits.</summary>
    private static int WindowOrder(LimitWindow w) => w.IsRateLimit ? 1_000_000 + (w.Key == "requests" ? 0 : 1) : w.WindowMinutes ?? 999_999;

    private Dictionary<long, AccountLimitState> LoadStates(long? accountId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM account_limit_state" + (accountId != null ? " WHERE account_id = $a;" : ";");
        if (accountId != null) cmd.Parameters.AddWithValue("$a", accountId.Value);
        using var r = cmd.ExecuteReader();
        var result = new Dictionary<long, AccountLimitState>();
        while (r.Read())
        {
            var s = new AccountLimitState
            {
                AccountId = r.GetInt64(r.GetOrdinal("account_id")),
                ExhaustedUntil = Time(r, "exhausted_until"),
                ExhaustedReason = Str(r, "exhausted_reason"),
                UnifiedStatus = Str(r, "unified_status"),
                RepresentativeClaim = Str(r, "representative_claim"),
                OverageStatus = Str(r, "overage_status"),
                PlanType = Str(r, "plan_type"),
                RefreshedAt = Time(r, "refreshed_at"),
                RefreshError = Str(r, "refresh_error"),
                ResetsSyncedAt = Time(r, "resets_synced_at"),
            };
            result[s.AccountId] = s;
        }
        return result;
    }

    // ---------- resets ----------

    public IReadOnlyList<ResetCredit> ListResets(long? accountId, bool includeUsed = false)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = new List<string>();
        if (accountId != null) { where.Add("account_id = $a"); cmd.Parameters.AddWithValue("$a", accountId.Value); }
        if (!includeUsed) where.Add("used_at IS NULL");
        cmd.CommandText = "SELECT * FROM account_resets" + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY expires_at IS NULL, expires_at, id;";
        using var r = cmd.ExecuteReader();
        var list = new List<ResetCredit>();
        while (r.Read()) list.Add(ReadReset(r));
        return list;
    }

    public ResetCredit? GetReset(long accountId, long resetId) =>
        ListResets(accountId, includeUsed: true).FirstOrDefault(r => r.Id == resetId);

    public ResetCredit AddManualReset(long accountId, DateTimeOffset? expiresAt, string? note)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO account_resets (account_id, source, expires_at, note, created_at) VALUES ($a, 'manual', $e, $n, $now) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$a", accountId);
        cmd.Parameters.AddWithValue("$e", Db(expiresAt?.ToUnixTimeMilliseconds()));
        cmd.Parameters.AddWithValue("$n", Db(note));
        cmd.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        notifier.Notify(LiveTopic.Limits);
        return GetReset(accountId, id)!;
    }

    /// <summary>
    /// Replaces the provider-backed credits of an account with the provider's current list. Unused credits the provider no
    /// longer lists (redeemed elsewhere or expired) are removed; used ones are kept as history.
    /// </summary>
    public void SyncProviderResets(long accountId, IReadOnlyList<(string Id, DateTimeOffset? ExpiresAt)> resets)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = db.Open();
        using var tx = conn.BeginTransaction();
        foreach (var (id, expires) in resets)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO account_resets (account_id, source, provider_reset_id, expires_at, created_at) VALUES ($a, 'provider', $p, $e, $now)
                ON CONFLICT(account_id, provider_reset_id) DO UPDATE SET expires_at = excluded.expires_at;
                """;
            cmd.Parameters.AddWithValue("$a", accountId);
            cmd.Parameters.AddWithValue("$p", id);
            cmd.Parameters.AddWithValue("$e", Db(expires?.ToUnixTimeMilliseconds()));
            cmd.Parameters.AddWithValue("$now", now);
            cmd.ExecuteNonQuery();
        }
        using (var del = conn.CreateCommand())
        {
            del.Transaction = tx;
            var ids = resets.Select((r, i) => (r.Id, Name: "$id" + i)).ToList();
            del.CommandText = "DELETE FROM account_resets WHERE account_id = $a AND source = 'provider' AND used_at IS NULL"
                + (ids.Count > 0 ? $" AND provider_reset_id NOT IN ({string.Join(",", ids.Select(i => i.Name))})" : "") + ";";
            del.Parameters.AddWithValue("$a", accountId);
            foreach (var (id, name) in ids) del.Parameters.AddWithValue(name, id);
            del.ExecuteNonQuery();
        }
        tx.Commit();
        notifier.Notify(LiveTopic.Limits);
    }

    public void MarkResetUsed(long resetId, string usedBy) =>
        Execute("UPDATE account_resets SET used_at = $now, used_by = $by WHERE id = $id;",
            ("$id", resetId), ("$by", usedBy), ("$now", time.GetUtcNow().ToUnixTimeMilliseconds()));

    public void DeleteReset(long resetId) => Execute("DELETE FROM account_resets WHERE id = $id;", ("$id", resetId));

    // ---------- helpers ----------

    private void Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, Db(value));
        cmd.ExecuteNonQuery();
        notifier.Notify(LiveTopic.Limits);
    }

    private static object Db(object? v) => v ?? DBNull.Value;

    private static LimitWindow ReadWindow(SqliteDataReader r) => new()
    {
        Key = r.GetString(r.GetOrdinal("key")),
        Label = r.GetString(r.GetOrdinal("label")),
        Utilization = Dbl(r, "utilization"),
        Used = Dbl(r, "used"),
        Limit = Dbl(r, "lim"),
        Remaining = Dbl(r, "remaining"),
        ResetsAt = Time(r, "resets_at"),
        WindowMinutes = r.IsDBNull(r.GetOrdinal("window_minutes")) ? null : r.GetInt32(r.GetOrdinal("window_minutes")),
        Status = r.GetString(r.GetOrdinal("status")),
        Source = r.GetString(r.GetOrdinal("source")),
        UpdatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("updated_at"))),
    };

    private static ResetCredit ReadReset(SqliteDataReader r) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        AccountId = r.GetInt64(r.GetOrdinal("account_id")),
        Source = r.GetString(r.GetOrdinal("source")),
        ProviderResetId = Str(r, "provider_reset_id"),
        ExpiresAt = Time(r, "expires_at"),
        Note = Str(r, "note"),
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_at"))),
        UsedAt = Time(r, "used_at"),
        UsedBy = Str(r, "used_by"),
    };

    private static string? Str(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetString(r.GetOrdinal(col));
    private static double? Dbl(SqliteDataReader r, string col) => r.IsDBNull(r.GetOrdinal(col)) ? null : r.GetDouble(r.GetOrdinal(col));
    private static DateTimeOffset? Time(SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal(col)));
}
