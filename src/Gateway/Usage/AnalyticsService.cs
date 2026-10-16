using System.Globalization;
using Gateway.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Gateway.Usage;

/// <summary>
/// Filters shared by all analytics queries. Time range is [From, To) in unix ms on request start. Every other value equals
/// the corresponding breakdown key, so a breakdown row can be clicked into a filter. Gateway-retried attempts (429
/// failovers the client never saw) are excluded unless <see cref="IncludeRetried"/>.
/// </summary>
public sealed record AnalyticsFilter(
    long From, long To, string? Provider = null, long? AccountId = null, string? SessionId = null, string? Agent = null,
    string? AgentId = null, string? Model = null, string? Client = null, bool IncludeRetried = false, string? SpeedMode = null,
    string? RequestedModel = null)
{
    public static AnalyticsFilter FromQuery(IQueryCollection q, DateTimeOffset now)
    {
        var to = ParseTime(q["to"]) ?? now.ToUnixTimeMilliseconds();
        var from = ParseTime(q["from"]) ?? to - (long)TimeSpan.FromHours(24).TotalMilliseconds;
        string? S(string k) => string.IsNullOrWhiteSpace(q[k]) ? null : q[k].ToString();
        return new AnalyticsFilter(from, Math.Max(to, from + 1), S("provider"),
            long.TryParse(q["accountId"], out var a) ? a : null, S("sessionId"), S("agent"), S("agentId"), S("model"), S("client"),
            q["includeRetried"] == "true", S("speedMode"), S("requestedModel"));
    }

    public static long? ParseTime(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (long.TryParse(s, out var ms)) return ms;
        return DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.ToUnixTimeMilliseconds() : null;
    }

    /// <summary>SQL predicate for these filters. With <paramref name="overlap"/>, matches requests in flight during the range.</summary>
    public string Where(SqliteCommand cmd, string alias = "", bool overlap = false)
    {
        var p = alias.Length > 0 ? alias + "." : "";
        var parts = new List<string>
        {
            overlap ? $"{p}started_at < $to AND {p}completed_at >= $from" : $"{p}started_at >= $from AND {p}started_at < $to",
        };
        cmd.Parameters.AddWithValue("$from", From);
        cmd.Parameters.AddWithValue("$to", To);
        void Add(string sql, string name, object? value)
        {
            if (value == null) return;
            parts.Add(sql);
            cmd.Parameters.AddWithValue(name, value);
        }
        if (!IncludeRetried) parts.Add($"{p}retried = 0");
        Add($"{p}provider = $f_provider", "$f_provider", Provider);
        Add($"{p}account_id = $f_account", "$f_account", AccountId);
        Add($"{p}session_id = $f_session", "$f_session", SessionId);
        Add($"{AnalyticsService.AgentKindSql(p)} = $f_agent", "$f_agent", Agent);
        Add($"{p}agent_id = $f_agent_id", "$f_agent_id", AgentId);
        Add($"COALESCE({p}model, 'unknown') = $f_model", "$f_model", Model);
        Add($"{p}client = $f_client", "$f_client", Client);
        Add($"{SpeedModes.Sql(p)} = $f_speed", "$f_speed", SpeedMode);
        Add($"{AnalyticsService.RequestedModelSql(p)} = $f_requested_model", "$f_requested_model", RequestedModel);
        return string.Join(" AND ", parts);
    }

    public bool Matches(LiveRequest r) =>
        (Provider == null || r.Provider == Provider) && (AccountId == null || r.AccountId == AccountId)
        && (SessionId == null || r.SessionId == SessionId) && (AgentId == null || r.AgentId == AgentId)
        && (Agent == null || $"{r.Client}:{r.AgentRole ?? "none"}" == Agent) && (Model == null || (r.Model ?? "unknown") == Model)
        && (Client == null || r.Client == Client) && (SpeedMode == null || SpeedModes.Of(r.SpeedMode, r.Endpoint) == SpeedMode)
        && (RequestedModel == null || (r.RequestedModel ?? r.Model ?? "unknown") == RequestedModel);
}

public sealed record SeriesInfo(string Key, string Id, string Label, double Total);

public sealed record TimeseriesResult(
    IReadOnlyList<string> Metrics, string GroupBy, string Bucket, long BucketMs, long From, long To,
    IReadOnlyList<SeriesInfo> Series, IReadOnlyList<Dictionary<string, object>> Points);

public sealed record BreakdownRow(
    string Key, string Label, long Requests, long Errors, double CostUsd, long InputTokens, long OutputTokens,
    long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens, long ToolCalls, double AvgContextTokens,
    long MaxContextTokens, double AvgLatencyMs, long Sessions, long Agents, long? FirstSeen, long? LastSeen)
{
    /// <summary>Output-token-weighted mean tokens/second over <see cref="ThroughputRequests"/>; null without eligible rows.</summary>
    public double? TokensPerSecond { get; init; }
    public double? TokensPerSecondP50 { get; init; }
    public double? TokensPerSecondP95 { get; init; }
    /// <summary>Requests that have a throughput (successful, with output and a generation time).</summary>
    public long ThroughputRequests { get; init; }
}

/// <summary>One agent instance's per-request values (see <see cref="AnalyticsService.AgentSeries"/>).</summary>
public sealed record AgentSeriesInfo(
    string Key, string Id, string Label, string? SessionId, string? Role, string? Client, long FirstSeen, long LastSeen,
    long Requests, IReadOnlyList<double[]> Points);

public sealed record AgentSeriesResult(
    string Metric, bool Cumulative, long From, long To, int TotalAgents, int Hidden, IReadOnlyList<AgentSeriesInfo> Agents);

/// <summary>Read side of usage analytics. Response shapes are documented in .claude/ledger/analytics.md.</summary>
public sealed class AnalyticsService(Database db, LiveRequests live, TimeProvider time)
{
    private enum Agg { Sum, Avg, Max, Distinct, Concurrency, Percentile }

    private static readonly Dictionary<string, (string Sql, Agg Kind)> MetricSql = new(StringComparer.OrdinalIgnoreCase)
    {
        ["requests"] = ("COUNT(*)", Agg.Sum),
        ["errors"] = ("SUM(CASE WHEN status >= 400 OR error_type IS NOT NULL THEN 1 ELSE 0 END)", Agg.Sum),
        ["cost"] = ("SUM(cost_usd)", Agg.Sum),
        ["inputTokens"] = ("SUM(input_tokens)", Agg.Sum),
        ["outputTokens"] = ("SUM(output_tokens)", Agg.Sum),
        ["cacheReadTokens"] = ("SUM(cache_read_tokens)", Agg.Sum),
        ["cacheWriteTokens"] = ("SUM(cache_write_tokens)", Agg.Sum),
        ["reasoningTokens"] = ("SUM(reasoning_tokens)", Agg.Sum),
        ["totalTokens"] = ("SUM(input_tokens + output_tokens + cache_read_tokens + cache_write_tokens)", Agg.Sum),
        ["toolCalls"] = ("SUM(tool_calls)", Agg.Sum),
        ["contextAvg"] = ("AVG(CASE WHEN context_tokens > 0 THEN context_tokens END)", Agg.Avg),
        ["contextMax"] = ("MAX(context_tokens)", Agg.Max),
        ["latencyAvg"] = ("AVG(completed_at - started_at)", Agg.Avg),
        ["ttfbAvg"] = ("AVG(first_byte_at - started_at)", Agg.Avg),
        ["tokensPerSecond"] = (Throughput.WeightedAvgSql(), Agg.Avg),
        // Percentile metrics: the SQL is the per-request value; percentiles are computed in memory.
        ["tokensPerSecondP50"] = (Throughput.PerRequestSql(), Agg.Percentile),
        ["tokensPerSecondP95"] = (Throughput.PerRequestSql(), Agg.Percentile),
        ["activeAgents"] = ("COUNT(DISTINCT agent_id)", Agg.Distinct),
        ["activeSessions"] = ("COUNT(DISTINCT session_id)", Agg.Distinct),
        ["concurrentRequests"] = ("", Agg.Concurrency),
        ["concurrentAgents"] = ("", Agg.Concurrency),
    };

    public static IReadOnlyCollection<string> MetricNames => MetricSql.Keys;
    public static readonly string[] GroupNames =
        ["none", "provider", "account", "session", "agent", "agentId", "model", "requestedModel", "speedMode", "modelMode", "client", "endpoint"];

    /// <summary>The model the client asked for: the advertised id of bridged requests, else the recorded model.</summary>
    public static string RequestedModelSql(string p = "") => $"COALESCE({p}requested_model, {p}model, 'unknown')";

    /// <summary>Per-request value of each metric for <see cref="AgentSeries"/>; null = metric has no per-request meaning.</summary>
    private static string? PerRequestSql(string metric) => metric switch
    {
        "requests" => "1",
        "errors" => "CASE WHEN status >= 400 OR error_type IS NOT NULL THEN 1 ELSE 0 END",
        "cost" => "cost_usd",
        "inputTokens" => "input_tokens",
        "outputTokens" => "output_tokens",
        "cacheReadTokens" => "cache_read_tokens",
        "cacheWriteTokens" => "cache_write_tokens",
        "reasoningTokens" => "reasoning_tokens",
        "totalTokens" => "input_tokens + output_tokens + cache_read_tokens + cache_write_tokens",
        "toolCalls" => "tool_calls",
        "contextAvg" or "contextMax" => "CASE WHEN context_tokens > 0 THEN context_tokens END",
        "latencyAvg" => "completed_at - started_at",
        "ttfbAvg" => "first_byte_at - started_at",
        "tokensPerSecond" or "tokensPerSecondP50" or "tokensPerSecondP95" => Throughput.PerRequestSql(),
        _ => null,
    };

    /// <summary>Metrics whose per-agent view is a running total (a per-request value of 1 says nothing).</summary>
    private static bool AlwaysCumulative(string metric) => metric is "requests" or "errors";

    private static readonly (string Name, long Ms)[] Buckets =
    [
        ("minute", 60_000), ("5min", 300_000), ("15min", 900_000), ("hour", 3_600_000), ("6hour", 21_600_000), ("day", 86_400_000),
    ];

    public static string AgentKindSql(string p = "") => $"({p}client || ':' || COALESCE({p}agent_role, 'none'))";

    private static string? GroupSql(string groupBy, string p = "") => groupBy switch
    {
        "none" => "''",
        "provider" => $"{p}provider",
        "account" => $"CAST({p}account_id AS TEXT)",
        "session" => $"{p}session_id",
        "agent" => AgentKindSql(p),
        "agentId" => $"COALESCE({p}agent_id, 'none')",
        "model" => $"COALESCE({p}model, 'unknown')",
        "requestedModel" => RequestedModelSql(p),
        "speedMode" => SpeedModes.Sql(p),
        "modelMode" => $"(COALESCE({p}model, 'unknown') || '|' || {SpeedModes.Sql(p)})",
        "client" => $"{p}client",
        "endpoint" => $"{p}endpoint",
        _ => null,
    };

    public static (string Name, long Ms)? ResolveBucket(string? name, long from, long to)
    {
        if (string.IsNullOrEmpty(name) || name == "auto")
        {
            foreach (var b in Buckets)
                if ((to - from) / b.Ms <= 240) return b;
            return Buckets[^1];
        }
        foreach (var b in Buckets) if (b.Name == name) return b;
        return null;
    }

    // ---------------------------------------------------------------- timeseries

    public TimeseriesResult? Timeseries(AnalyticsFilter f, IReadOnlyList<string> metrics, string groupBy, string? bucketName, int limit = 8)
    {
        if (metrics.Count == 0 || metrics.Any(m => !MetricSql.ContainsKey(m)) || GroupSql(groupBy) == null) return null;
        if (groupBy != "none") metrics = [metrics[0]];
        if (ResolveBucket(bucketName, f.From, f.To) is not { } bucket) return null;
        metrics = metrics.Select(m => MetricSql.Keys.First(k => k.Equals(m, StringComparison.OrdinalIgnoreCase))).ToList();

        var start = f.From / bucket.Ms * bucket.Ms;
        var count = (int)Math.Max(1, (f.To - start + bucket.Ms - 1) / bucket.Ms);
        var labels = Labels(groupBy, f);

        // values[seriesId][bucketIndex]
        var columns = new List<(string Id, string Label, double?[] Values, double Total)>();
        foreach (var metric in metrics)
        {
            var (sql, kind) = MetricSql[metric];
            Dictionary<string, double>? percentileTotals = null;
            var perGroup = kind switch
            {
                Agg.Concurrency => Concurrency(f, groupBy, metric == "concurrentAgents", start, bucket.Ms, count),
                Agg.Percentile => Percentiles(f, groupBy, sql, Quantile(metric), start, bucket.Ms, count, out percentileTotals),
                _ => Aggregate(f, groupBy, sql, start, bucket.Ms, count),
            };

            if (groupBy == "none")
            {
                var values = perGroup.GetValueOrDefault("") ?? new double?[count];
                columns.Add((metric, metric, values, percentileTotals?.GetValueOrDefault("") ?? Total(values, kind, f, sql, groupBy, "")));
                continue;
            }

            var totals = perGroup.ToDictionary(g => g.Key,
                g => percentileTotals != null ? percentileTotals.GetValueOrDefault(g.Key) : Total(g.Value, kind, f, sql, groupBy, g.Key));
            var ranked = totals.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
            foreach (var (id, total) in ranked.Take(limit))
                columns.Add((id, labels(id), perGroup[id], total));
            var rest = ranked.Skip(limit).ToList();
            if (rest.Count > 0 && kind == Agg.Sum)
            {
                var other = new double?[count];
                foreach (var (id, _) in rest)
                    for (var i = 0; i < count; i++)
                        if (perGroup[id][i] is { } v) other[i] = (other[i] ?? 0) + v;
                columns.Add(("__other__", $"Other ({rest.Count})", other, rest.Sum(r => r.Value)));
            }
        }

        var series = columns.Select((c, i) => new SeriesInfo($"s{i}", c.Id, c.Label, Math.Round(c.Total, 6))).ToList();
        var points = new List<Dictionary<string, object>>(count);
        for (var b = 0; b < count; b++)
        {
            var point = new Dictionary<string, object> { ["t"] = start + b * bucket.Ms };
            for (var i = 0; i < columns.Count; i++)
            {
                var kind = MetricSql[metrics[groupBy == "none" ? i : 0]].Kind;
                // Sums and peaks are 0 in an empty bucket; averages are absent there so lines skip the gap.
                var v = columns[i].Values[b] ?? (kind is Agg.Avg or Agg.Percentile ? null : 0d);
                if (v is { } value) point[$"s{i}"] = Math.Round(value, 6);
            }
            points.Add(point);
        }
        return new TimeseriesResult(metrics, groupBy, bucket.Name, bucket.Ms, f.From, f.To, series, points);
    }

    private Dictionary<string, double?[]> Aggregate(AnalyticsFilter f, string groupBy, string metricSql, long start, long bucketMs, int count)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = f.Where(cmd);
        cmd.CommandText = $"""
            SELECT (started_at - $start) / $bucket AS b, {GroupSql(groupBy)} AS g, {metricSql} AS v
            FROM requests WHERE {where} GROUP BY b, g;
            """;
        cmd.Parameters.AddWithValue("$start", start);
        cmd.Parameters.AddWithValue("$bucket", bucketMs);
        var result = new Dictionary<string, double?[]>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var b = r.GetInt64(0);
            if (b < 0 || b >= count || r.IsDBNull(2)) continue;
            var g = r.IsDBNull(1) ? "none" : r.GetString(1);
            if (!result.TryGetValue(g, out var arr)) result[g] = arr = new double?[count];
            arr[b] = r.GetDouble(2);
        }
        return result;
    }

    private static double Quantile(string metric) => metric.EndsWith("P95", StringComparison.OrdinalIgnoreCase) ? 0.95 : 0.5;

    /// <summary>Per-bucket percentile of a per-request value (NULLs skipped), plus each group's percentile over the range.</summary>
    private Dictionary<string, double?[]> Percentiles(AnalyticsFilter f, string groupBy, string valueSql, double q, long start, long bucketMs,
        int count, out Dictionary<string, double> totals)
    {
        var values = new Dictionary<string, Dictionary<long, List<double>>>();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            var where = f.Where(cmd);
            cmd.CommandText = $"""
                SELECT (started_at - $start) / $bucket AS b, {GroupSql(groupBy)} AS g, {valueSql} AS v
                FROM requests WHERE {where} AND {valueSql} IS NOT NULL;
                """;
            cmd.Parameters.AddWithValue("$start", start);
            cmd.Parameters.AddWithValue("$bucket", bucketMs);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var b = r.GetInt64(0);
                if (b < 0 || b >= count) continue;
                var g = r.IsDBNull(1) ? "none" : r.GetString(1);
                if (!values.TryGetValue(g, out var byBucket)) values[g] = byBucket = new();
                if (!byBucket.TryGetValue(b, out var list)) byBucket[b] = list = new();
                list.Add(r.GetDouble(2));
            }
        }
        var result = new Dictionary<string, double?[]>();
        totals = new Dictionary<string, double>();
        foreach (var (g, byBucket) in values)
        {
            var arr = result[g] = new double?[count];
            foreach (var (b, list) in byBucket) arr[b] = Throughput.Percentile([.. list], q);
            totals[g] = Throughput.Percentile(byBucket.Values.SelectMany(l => l).ToList(), q) ?? 0;
        }
        return result;
    }

    /// <summary>Range total for a series: sums add up, peaks take the max, averages and distinct counts are re-queried.</summary>
    private double Total(double?[] values, Agg kind, AnalyticsFilter f, string metricSql, string groupBy, string group) => kind switch
    {
        Agg.Sum => values.Sum(v => v ?? 0),
        Agg.Max or Agg.Concurrency => values.Max(v => v ?? 0),
        _ => ScalarForGroup(f, metricSql, groupBy, group),
    };

    private double ScalarForGroup(AnalyticsFilter f, string metricSql, string groupBy, string group)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = f.Where(cmd);
        var groupFilter = groupBy == "none" ? "" : $" AND {GroupSql(groupBy)} = $group";
        cmd.Parameters.AddWithValue("$group", group);
        cmd.CommandText = $"SELECT {metricSql} FROM requests WHERE {where}{groupFilter};";
        return cmd.ExecuteScalar() is { } v and not DBNull ? Convert.ToDouble(v) : 0;
    }

    /// <summary>
    /// Peak number of simultaneously in-flight requests (or distinct agents with an in-flight request) per bucket,
    /// from request start/end intervals plus requests still in flight.
    /// </summary>
    private Dictionary<string, double?[]> Concurrency(AnalyticsFilter f, string groupBy, bool agents, long start, long bucketMs, int count)
    {
        var intervals = Intervals(f, groupBy);
        var result = new Dictionary<string, double?[]>();
        foreach (var group in intervals.GroupBy(i => i.Group))
        {
            var list = agents ? MergePerAgent(group) : group.Select(i => (i.Start, i.End)).ToList();
            if (list.Count == 0) continue;
            result[group.Key] = Sweep(list, start, bucketMs, count).Select(v => (double?)v).ToArray();
        }
        return result;
    }

    private List<(long Start, long End, string Group, string? Agent)> Intervals(AnalyticsFilter f, string groupBy)
    {
        var list = new List<(long, long, string, string?)>();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            var where = f.Where(cmd, overlap: true);
            cmd.CommandText = $"SELECT started_at, completed_at, {GroupSql(groupBy)}, agent_id FROM requests WHERE {where};";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? "none" : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
        }
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        foreach (var l in live.Snapshot().Where(l => f.Matches(l) && l.StartedAt < f.To))
            list.Add((l.StartedAt, now, LiveGroup(l, groupBy), l.AgentId));
        return list;
    }

    private static string LiveGroup(LiveRequest l, string groupBy) => groupBy switch
    {
        "provider" => l.Provider,
        "account" => l.AccountId.ToString(CultureInfo.InvariantCulture),
        "session" => l.SessionId,
        "agent" => $"{l.Client}:{l.AgentRole ?? "none"}",
        "agentId" => l.AgentId ?? "none",
        "model" => l.Model ?? "unknown",
        "requestedModel" => l.RequestedModel ?? l.Model ?? "unknown",
        "speedMode" => SpeedModes.Of(l.SpeedMode, l.Endpoint),
        "modelMode" => $"{l.Model ?? "unknown"}|{SpeedModes.Of(l.SpeedMode, l.Endpoint)}",
        "client" => l.Client,
        "endpoint" => l.Endpoint,
        _ => "",
    };

    /// <summary>Collapses each agent's overlapping requests into busy periods so an agent counts once at any instant.</summary>
    private static List<(long Start, long End)> MergePerAgent(IEnumerable<(long Start, long End, string Group, string? Agent)> intervals)
    {
        var merged = new List<(long, long)>();
        foreach (var agent in intervals.Where(i => i.Agent != null).GroupBy(i => i.Agent))
        {
            long? s = null, e = null;
            foreach (var i in agent.OrderBy(i => i.Start))
            {
                if (s == null) { s = i.Start; e = i.End; continue; }
                if (i.Start <= e) { e = Math.Max(e!.Value, i.End); continue; }
                merged.Add((s.Value, e!.Value));
                s = i.Start; e = i.End;
            }
            if (s != null) merged.Add((s.Value, e!.Value));
        }
        return merged;
    }

    /// <summary>Peak overlap per bucket. An interval ending exactly when another starts does not overlap it.</summary>
    public static int[] Sweep(IReadOnlyList<(long Start, long End)> intervals, long start, long bucketMs, int count)
    {
        var events = new List<(long T, int D)>(intervals.Count * 2);
        foreach (var (s, e) in intervals)
        {
            events.Add((s, 1));
            events.Add((Math.Max(e, s + 1), -1));
        }
        events.Sort((a, b) => a.T != b.T ? a.T.CompareTo(b.T) : a.D.CompareTo(b.D));
        var peaks = new int[count];
        int active = 0, ei = 0;
        for (var b = 0; b < count; b++)
        {
            long bs = start + b * bucketMs, be = bs + bucketMs;
            while (ei < events.Count && events[ei].T <= bs && !(events[ei].T == bs && events[ei].D > 0)) active += events[ei++].D;
            var peak = active;
            while (ei < events.Count && events[ei].T < be) { active += events[ei++].D; peak = Math.Max(peak, active); }
            peaks[b] = peak;
        }
        return peaks;
    }

    // ---------------------------------------------------------------- summary & breakdown

    public object Summary(AnalyticsFilter f)
    {
        var row = Breakdown(f, "none", 1)!.FirstOrDefault();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = (f with { IncludeRetried = true }).Where(cmd);
        var counted = f.IncludeRetried ? "1 = 1" : "retried = 0";
        cmd.CommandText = $"""
            SELECT SUM(retried), COUNT(DISTINCT CASE WHEN {counted} THEN model END),
                   SUM(CASE WHEN {counted} AND priced = 0 AND input_tokens + output_tokens > 0 THEN 1 ELSE 0 END),
                   SUM(CASE WHEN {counted} AND status = 429 THEN 1 ELSE 0 END)
            FROM requests WHERE {where};
            """;
        long retried = 0, models = 0, unpriced = 0, rateLimited = 0;
        using (var r = cmd.ExecuteReader())
            if (r.Read())
            {
                retried = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                models = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                unpriced = r.IsDBNull(2) ? 0 : r.GetInt64(2);
                rateLimited = r.IsDBNull(3) ? 0 : r.GetInt64(3);
            }

        var intervals = Intervals(f, "none");
        var span = f.To - f.From;
        var peakRequests = intervals.Count == 0 ? 0 : Sweep(intervals.Select(i => (i.Start, i.End)).ToList(), f.From, span, 1)[0];
        var peakAgents = intervals.Count == 0 ? 0 : Sweep(MergePerAgent(intervals), f.From, span, 1)[0];
        var promptTokens = (row?.InputTokens ?? 0) + (row?.CacheReadTokens ?? 0) + (row?.CacheWriteTokens ?? 0);

        return new
        {
            from = f.From, to = f.To,
            requests = row?.Requests ?? 0, errors = row?.Errors ?? 0, rateLimited, gatewayRetries = retried,
            costUsd = row?.CostUsd ?? 0, unpricedRequests = unpriced,
            inputTokens = row?.InputTokens ?? 0, outputTokens = row?.OutputTokens ?? 0,
            cacheReadTokens = row?.CacheReadTokens ?? 0, cacheWriteTokens = row?.CacheWriteTokens ?? 0,
            reasoningTokens = row?.ReasoningTokens ?? 0,
            totalTokens = promptTokens + (row?.OutputTokens ?? 0),
            cacheHitRate = promptTokens == 0 ? 0 : Math.Round((double)(row?.CacheReadTokens ?? 0) / promptTokens, 4),
            toolCalls = row?.ToolCalls ?? 0,
            avgContextTokens = row?.AvgContextTokens ?? 0, maxContextTokens = row?.MaxContextTokens ?? 0,
            avgLatencyMs = row?.AvgLatencyMs ?? 0,
            tokensPerSecond = row?.TokensPerSecond, tokensPerSecondP50 = row?.TokensPerSecondP50,
            tokensPerSecondP95 = row?.TokensPerSecondP95, throughputRequests = row?.ThroughputRequests ?? 0,
            sessions = row?.Sessions ?? 0, agents = row?.Agents ?? 0, models,
            peakConcurrentRequests = peakRequests, peakConcurrentAgents = peakAgents,
            firstSeen = row?.FirstSeen, lastSeen = row?.LastSeen,
        };
    }

    public IReadOnlyList<BreakdownRow>? Breakdown(AnalyticsFilter f, string by, int limit = 50)
    {
        if (by == "tool") return ToolBreakdown(f, limit);
        if (GroupSql(by) is not { } groupSql) return null;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = f.Where(cmd);
        cmd.CommandText = $"""
            SELECT {groupSql} AS g, COUNT(*),
                   SUM(CASE WHEN status >= 400 OR error_type IS NOT NULL THEN 1 ELSE 0 END),
                   COALESCE(SUM(cost_usd), 0), COALESCE(SUM(input_tokens), 0), COALESCE(SUM(output_tokens), 0),
                   COALESCE(SUM(cache_read_tokens), 0), COALESCE(SUM(cache_write_tokens), 0), COALESCE(SUM(reasoning_tokens), 0),
                   COALESCE(SUM(tool_calls), 0), COALESCE(AVG(CASE WHEN context_tokens > 0 THEN context_tokens END), 0),
                   COALESCE(MAX(context_tokens), 0), COALESCE(AVG(completed_at - started_at), 0),
                   COUNT(DISTINCT session_id), COUNT(DISTINCT agent_id), MIN(started_at), MAX(completed_at),
                   {Throughput.WeightedAvgSql()}, SUM(CASE WHEN {Throughput.EligibleSql()} THEN 1 ELSE 0 END)
            FROM requests WHERE {where}
            GROUP BY g HAVING COUNT(*) > 0
            ORDER BY 4 DESC, 2 DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var labels = Labels(by, f);
        var rows = new List<BreakdownRow>();
        using (var r = cmd.ExecuteReader())
            while (r.Read())
            {
                var key = r.IsDBNull(0) ? "none" : r.GetString(0);
                rows.Add(new BreakdownRow(key, by == "none" ? "Total" : labels(key), r.GetInt64(1), r.GetInt64(2),
                    Math.Round(r.GetDouble(3), 6), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6), r.GetInt64(7), r.GetInt64(8), r.GetInt64(9),
                    Math.Round(r.GetDouble(10), 1), r.GetInt64(11), Math.Round(r.GetDouble(12), 1), r.GetInt64(13), r.GetInt64(14),
                    r.IsDBNull(15) ? null : r.GetInt64(15), r.IsDBNull(16) ? null : r.GetInt64(16))
                {
                    TokensPerSecond = r.IsDBNull(17) ? null : Math.Round(r.GetDouble(17), 1),
                    ThroughputRequests = r.IsDBNull(18) ? 0 : r.GetInt64(18),
                });
            }
        if (rows.Any(row => row.ThroughputRequests > 0)) rows = WithThroughputPercentiles(f, groupSql, rows);
        return rows;
    }

    private List<BreakdownRow> WithThroughputPercentiles(AnalyticsFilter f, string groupSql, List<BreakdownRow> rows)
    {
        var values = new Dictionary<string, List<double>>();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            var where = f.Where(cmd);
            cmd.CommandText = $"SELECT {groupSql}, {Throughput.PerRequestSql()} AS v FROM requests WHERE {where} AND {Throughput.EligibleSql()};";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.IsDBNull(0) ? "none" : r.GetString(0);
                if (!values.TryGetValue(key, out var list)) values[key] = list = new();
                list.Add(r.GetDouble(1));
            }
        }
        return rows.Select(row => values.TryGetValue(row.Key, out var list)
            ? row with
            {
                TokensPerSecondP50 = Math.Round(Throughput.Percentile(list, 0.5)!.Value, 1),
                TokensPerSecondP95 = Math.Round(Throughput.Percentile(list, 0.95)!.Value, 1),
            }
            : row).ToList();
    }

    private List<BreakdownRow> ToolBreakdown(AnalyticsFilter f, int limit)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = f.Where(cmd, "r");
        cmd.CommandText = $"""
            SELECT t.tool, COUNT(DISTINCT r.id), SUM(t.count), COUNT(DISTINCT r.session_id), COUNT(DISTINCT r.agent_id),
                   MIN(r.started_at), MAX(r.completed_at)
            FROM request_tools t JOIN requests r ON r.id = t.request_row_id
            WHERE {where} GROUP BY t.tool ORDER BY 3 DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var rows = new List<BreakdownRow>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            rows.Add(new BreakdownRow(r.GetString(0), r.GetString(0), r.GetInt64(1), 0, 0, 0, 0, 0, 0, 0, r.GetInt64(2), 0, 0, 0,
                r.GetInt64(3), r.GetInt64(4), r.GetInt64(5), r.GetInt64(6)));
        return rows;
    }

    // ---------------------------------------------------------------- sessions, requests, live

    public object Sessions(AnalyticsFilter f, int limit, int offset)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        var where = f.Where(cmd);
        cmd.CommandText = $"""
            SELECT provider, session_id, MIN(started_at), MAX(completed_at), COUNT(*), COALESCE(SUM(cost_usd), 0),
                   SUM(input_tokens), SUM(output_tokens), SUM(cache_read_tokens), SUM(cache_write_tokens), SUM(tool_calls),
                   MAX(context_tokens), COUNT(DISTINCT agent_id), COUNT(DISTINCT account_id),
                   SUM(CASE WHEN status >= 400 OR error_type IS NOT NULL THEN 1 ELSE 0 END),
                   (SELECT account_id FROM requests x WHERE x.provider = requests.provider AND x.session_id = requests.session_id
                    ORDER BY x.started_at DESC LIMIT 1),
                   MAX(client), MAX(model), COUNT(*) OVER ()
            FROM requests WHERE {where} AND session_id IS NOT NULL
            GROUP BY provider, session_id ORDER BY MAX(completed_at) DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);
        var accountName = Labels("account");
        var liveSessions = live.Snapshot().Select(l => (l.Provider, l.SessionId)).ToHashSet();
        var items = new List<object>();
        long total = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            total = r.GetInt64(18);
            var accountId = r.IsDBNull(15) ? (long?)null : r.GetInt64(15);
            items.Add(new
            {
                provider = r.GetString(0), sessionId = r.IsDBNull(1) ? "" : r.GetString(1),
                firstSeen = r.GetInt64(2), lastSeen = r.GetInt64(3), requests = r.GetInt64(4), costUsd = Math.Round(r.GetDouble(5), 6),
                inputTokens = r.GetInt64(6), outputTokens = r.GetInt64(7), cacheReadTokens = r.GetInt64(8), cacheWriteTokens = r.GetInt64(9),
                toolCalls = r.GetInt64(10), maxContextTokens = r.GetInt64(11), agents = r.GetInt64(12), accountsUsed = r.GetInt64(13),
                errors = r.GetInt64(14), accountId,
                accountName = accountId is { } id ? accountName(id.ToString(CultureInfo.InvariantCulture)) : null,
                client = r.IsDBNull(16) ? null : r.GetString(16), model = r.IsDBNull(17) ? null : r.GetString(17),
                live = liveSessions.Contains((r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1))),
            });
        }
        return new { total, limit, offset, items };
    }

    public object Requests(AnalyticsFilter f, int limit, long? beforeId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        // The log shows every upstream attempt, including failed-over ones (flagged "retried").
        var where = (f with { IncludeRetried = true }).Where(cmd, "r");
        if (beforeId != null) { where += " AND r.id < $before"; cmd.Parameters.AddWithValue("$before", beforeId); }
        cmd.CommandText = $"""
            SELECT r.id, r.request_id, r.attempt, r.retried, r.provider, r.account_id, r.session_id, r.client, r.client_version,
                   r.agent_id, r.agent_role, r.method, r.path, r.endpoint, r.model, r.stream, r.status, r.error_type, r.stop_reason,
                   r.cancelled, r.input_tokens, r.output_tokens, r.cache_read_tokens, r.cache_write_tokens, r.reasoning_tokens,
                   r.context_tokens, r.tool_calls, r.cost_usd, r.started_at, r.completed_at, r.first_byte_at,
                   (SELECT GROUP_CONCAT(t.tool || ':' || t.count, ',') FROM request_tools t WHERE t.request_row_id = r.id),
                   r.first_token_at, r.generation_ms, r.requested_speed, r.reported_speed, r.speed_mode, {Throughput.PerRequestSql("r.")},
                   r.requested_model, r.bridge
            FROM requests r WHERE {where} ORDER BY r.id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        var accountName = Labels("account");
        var items = new List<object>();
        long lastId = 0;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            lastId = r.GetInt64(0);
            string? S(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var tools = S(31)?.Split(',').Select(t => t.Split(':')).ToDictionary(p => p[0], p => int.Parse(p[^1], CultureInfo.InvariantCulture))
                ?? new Dictionary<string, int>();
            var accountId = r.IsDBNull(5) ? (long?)null : r.GetInt64(5);
            items.Add(new
            {
                id = r.GetInt64(0), requestId = r.GetString(1), attempt = r.GetInt32(2), retried = r.GetInt64(3) == 1,
                provider = r.GetString(4), accountId, accountName = accountId is { } a ? accountName(a.ToString(CultureInfo.InvariantCulture)) : null,
                sessionId = S(6), client = S(7), clientVersion = S(8), agentId = S(9), agentRole = S(10),
                method = r.GetString(11), path = r.GetString(12), endpoint = r.GetString(13), model = S(14), stream = r.GetInt64(15) == 1,
                status = r.GetInt32(16), errorType = S(17), stopReason = S(18), cancelled = r.GetInt64(19) == 1,
                inputTokens = r.GetInt64(20), outputTokens = r.GetInt64(21), cacheReadTokens = r.GetInt64(22),
                cacheWriteTokens = r.GetInt64(23), reasoningTokens = r.GetInt64(24), contextTokens = r.GetInt64(25),
                toolCalls = r.GetInt64(26), tools, costUsd = Math.Round(r.GetDouble(27), 6),
                startedAt = r.GetInt64(28), completedAt = r.GetInt64(29),
                latencyMs = r.GetInt64(29) - r.GetInt64(28),
                ttfbMs = r.IsDBNull(30) ? (long?)null : r.GetInt64(30) - r.GetInt64(28),
                firstTokenMs = r.IsDBNull(32) ? (long?)null : r.GetInt64(32) - r.GetInt64(28),
                generationMs = r.IsDBNull(33) ? (long?)null : r.GetInt64(33),
                requestedSpeed = S(34), reportedSpeed = S(35), speedMode = S(36),
                tokensPerSecond = r.IsDBNull(37) ? (double?)null : Math.Round(r.GetDouble(37), 1),
                requestedModel = S(38), bridge = S(39),
            });
        }
        var nextBefore = items.Count == limit ? lastId : (long?)null;
        return new { items, nextBefore };
    }

    public object Live()
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        var inFlight = live.Snapshot();
        long recentAgents = 0, recentSessions = 0;
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(DISTINCT agent_id), COUNT(DISTINCT session_id) FROM requests WHERE completed_at >= $since AND retried = 0;";
            cmd.Parameters.AddWithValue("$since", now - 5 * 60_000);
            using var r = cmd.ExecuteReader();
            if (r.Read()) { recentAgents = r.GetInt64(0); recentSessions = r.GetInt64(1); }
        }
        return new
        {
            now,
            inFlightRequests = inFlight.Count,
            activeAgents = inFlight.Where(l => l.AgentId != null).Select(l => l.AgentId).Distinct().Count(),
            activeSessions = inFlight.Select(l => (l.Provider, l.SessionId)).Distinct().Count(),
            agentsLast5Min = recentAgents, sessionsLast5Min = recentSessions,
            requests = inFlight.Select(l => new
            {
                l.RequestId, l.Provider, l.AccountId, l.AccountName, l.SessionId, l.AgentId, l.AgentRole, l.Client, l.Model,
                l.RequestedModel, l.Stream, l.Endpoint, l.StartedAt, elapsedMs = now - l.StartedAt,
            }),
        };
    }

    // ---------------------------------------------------------------- per-agent series

    /// <summary>
    /// One series per agent instance with the per-request value of <paramref name="metric"/> at each request start, so an agent's
    /// line begins at its first request and shows e.g. how its context grew. Sum metrics can be plotted as running totals.
    /// The <paramref name="limit"/> agents with the most requests are returned (ordered by first appearance); the rest are
    /// counted in <see cref="AgentSeriesResult.Hidden"/>. Each series is thinned to at most <paramref name="maxPoints"/>
    /// points, keeping each slice's peak (or last value when cumulative). Returns null for metrics without a per-request value.
    /// </summary>
    public AgentSeriesResult? AgentSeries(AnalyticsFilter f, string metric, bool cumulative, int limit = 12, int maxPoints = 600)
    {
        var name = MetricSql.Keys.FirstOrDefault(k => k.Equals(metric, StringComparison.OrdinalIgnoreCase));
        if (name == null || PerRequestSql(name) is not { } valueSql) return null;
        cumulative = cumulative && MetricSql[name].Kind == Agg.Sum || AlwaysCumulative(name);

        var rows = new Dictionary<string, (string? Session, string? Role, string? Client, List<(long T, double V)> Points, long Requests)>();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            var where = f.Where(cmd);
            cmd.CommandText = $"""
                SELECT agent_id, session_id, agent_role, client, started_at, {valueSql} AS v
                FROM requests WHERE {where} AND agent_id IS NOT NULL ORDER BY started_at, id;
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetString(0);
                if (!rows.TryGetValue(id, out var a))
                    rows[id] = a = (r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), r.GetString(3), new(), 0);
                a.Requests++;
                if (!r.IsDBNull(5)) a.Points.Add((r.GetInt64(4), r.GetDouble(5)));
                rows[id] = a;
            }
        }

        var chosen = rows.OrderByDescending(a => a.Value.Requests).ThenBy(a => a.Key, StringComparer.Ordinal).Take(limit)
            .Where(a => a.Value.Points.Count > 0)
            .OrderBy(a => a.Value.Points[0].T).ThenBy(a => a.Key, StringComparer.Ordinal).ToList();
        var agents = chosen.Select((a, i) =>
        {
            var points = a.Value.Points;
            if (cumulative)
            {
                double run = 0;
                points = points.Select(p => (p.T, run += p.V)).ToList();
            }
            var thinned = Thin(points, maxPoints, cumulative).Select(p => new[] { p.T, Math.Round(p.V, 6) }).ToList();
            return new AgentSeriesInfo($"a{i}", a.Key, AgentLabel(a.Key, a.Value.Role), a.Value.Session, a.Value.Role, a.Value.Client,
                a.Value.Points[0].T, a.Value.Points[^1].T, a.Value.Requests, thinned);
        }).ToList();
        return new AgentSeriesResult(name, cumulative, f.From, f.To, rows.Count, rows.Count - agents.Count, agents);
    }

    private static List<(long T, double V)> Thin(List<(long T, double V)> points, int max, bool keepLast)
    {
        if (points.Count <= max || max < 2) return points;
        var result = new List<(long, double)>(max);
        var size = (double)points.Count / max;
        for (var i = 0; i < max; i++)
        {
            var slice = points.Skip((int)(i * size)).Take(Math.Max(1, (int)((i + 1) * size) - (int)(i * size))).ToList();
            if (slice.Count == 0) continue;
            result.Add(keepLast ? slice[^1] : slice.MaxBy(p => p.V));
        }
        return result;
    }

    /// <summary>
    /// "session8 · role · instance" for an agent id <c>provider:session:key</c>, e.g. <c>1a2b3c4d · subagent · 9f3e2a</c> or
    /// <c>5e6f7a8b · main</c> (Codex, whose key is the role).
    /// </summary>
    public static string AgentLabel(string agentId, string? role)
    {
        var parts = agentId.Split(':', 3);
        if (parts.Length < 3) return agentId;
        var session = parts[1].Length > 8 ? parts[1][..8] : parts[1];
        var key = parts[2];
        role ??= "agent";
        string? instance = key == role || key == "utility" ? null
            : key.StartsWith(role + "@", StringComparison.Ordinal) ? key[(role.Length + 1)..]
            : key.StartsWith("a-", StringComparison.Ordinal) ? key[2..8]
            : key[..Math.Min(6, key.Length)];
        return instance != null ? $"{session} · {role} · {instance}" : $"{session} · {role}";
    }

    /// <summary>Human-readable labels for group keys (account names; short agent ids).</summary>
    private Func<string, string> Labels(string groupBy, AnalyticsFilter? f = null)
    {
        switch (groupBy)
        {
            case "account":
            {
                var names = new Dictionary<string, string>();
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT id, name FROM accounts;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) names[r.GetInt64(0).ToString(CultureInfo.InvariantCulture)] = r.GetString(1);
                return k => names.TryGetValue(k, out var n) ? n : $"Account #{k} (deleted)";
            }
            case "agent":
                return k => k.Replace(':', ' ');
            case "agentId":
            {
                var roles = new Dictionary<string, string?>();
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                var where = f != null ? f.Where(cmd) : "1 = 1";
                cmd.CommandText = $"SELECT agent_id, MAX(agent_role) FROM requests WHERE {where} AND agent_id IS NOT NULL GROUP BY agent_id;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) roles[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetString(1);
                return k => AgentLabel(k, roles.GetValueOrDefault(k));
            }
            case "modelMode":
                return k => k.Replace("|", " · ");
            default:
                return k => k;
        }
    }
}
