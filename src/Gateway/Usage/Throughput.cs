namespace Gateway.Usage;

/// <summary>
/// Output throughput of one request: output tokens per second of generation time.
/// <list type="bullet">
/// <item>Streaming: generation runs from the first output event (first token delta; the first response byte when no delta
/// was seen) to the end of the stream, so queueing and prompt processing are excluded.</item>
/// <item>Non-streaming: there is no observable generation phase, so the whole latency is used. This is an approximation
/// that understates the speed (it includes time to first token).</item>
/// </list>
/// Only successful, non-cancelled requests with output count towards throughput analytics (<see cref="EligibleSql"/>).
/// </summary>
public static class Throughput
{
    public static long? GenerationMs(bool stream, long startedAt, long? firstByteAt, long? firstTokenAt, long completedAt)
    {
        var start = stream ? firstTokenAt ?? firstByteAt : startedAt;
        if (start is not { } s) return null;
        var ms = completedAt - s;
        return ms > 0 ? ms : null;
    }

    public static double? TokensPerSecond(long outputTokens, long? generationMs) =>
        outputTokens > 0 && generationMs is > 0 ? outputTokens * 1000.0 / generationMs.Value : null;

    /// <summary>Rows that have a meaningful throughput.</summary>
    public static string EligibleSql(string p = "") =>
        $"({p}generation_ms > 0 AND {p}output_tokens > 0 AND {p}status < 400 AND {p}cancelled = 0 AND {p}error_type IS NULL)";

    /// <summary>Per-request tokens/second, NULL when not eligible.</summary>
    public static string PerRequestSql(string p = "") =>
        $"(CASE WHEN {EligibleSql(p)} THEN {p}output_tokens * 1000.0 / {p}generation_ms END)";

    /// <summary>Output-token-weighted mean of per-request tokens/second (long, fast answers weigh more than one-liners).</summary>
    public static string WeightedAvgSql(string p = "") =>
        $"(SUM(CASE WHEN {EligibleSql(p)} THEN {p}output_tokens * ({p}output_tokens * 1000.0 / {p}generation_ms) END)"
        + $" / SUM(CASE WHEN {EligibleSql(p)} THEN {p}output_tokens END))";

    // End to end: from the moment the gateway sends the request upstream to the moment the last byte of the response has
    // arrived. This is the provider's speed as a client experiences it: queueing, prompt processing (time to first token)
    // and generation, for streaming and non-streaming requests alike.

    /// <summary>Rows with an end-to-end speed: successful, not cancelled, with output.</summary>
    public static string EndToEndEligibleSql(string p = "") =>
        $"({p}completed_at > {p}started_at AND {p}output_tokens > 0 AND {p}status < 400 AND {p}cancelled = 0 AND {p}error_type IS NULL)";

    /// <summary>Per-request output tokens per second, request sent to response received; NULL when not eligible.</summary>
    public static string EndToEndPerRequestSql(string p = "") =>
        $"(CASE WHEN {EndToEndEligibleSql(p)} THEN {p}output_tokens * 1000.0 / ({p}completed_at - {p}started_at) END)";

    /// <summary>Total output tokens over total elapsed time (= the output-token-weighted mean of per-request speeds).</summary>
    public static string EndToEndWeightedAvgSql(string p = "") =>
        $"(SUM(CASE WHEN {EndToEndEligibleSql(p)} THEN {p}output_tokens END) * 1000.0"
        + $" / SUM(CASE WHEN {EndToEndEligibleSql(p)} THEN {p}completed_at - {p}started_at END))";

    /// <summary>Linear-interpolated percentile (0..1) of an unsorted list; null when empty.</summary>
    public static double? Percentile(List<double> values, double q)
    {
        if (values.Count == 0) return null;
        values.Sort();
        var pos = (values.Count - 1) * q;
        var lo = (int)Math.Floor(pos);
        var hi = (int)Math.Ceiling(pos);
        return values[lo] + (values[hi] - values[lo]) * (pos - lo);
    }
}

/// <summary>
/// Speed modes, from the raw request/response values: Anthropic request <c>speed</c> (<c>"fast"</c>; absent = standard) and
/// response <c>usage.speed</c>; OpenAI/Codex request and response <c>service_tier</c> (<c>fast</c>/<c>priority</c> = fast,
/// <c>flex</c>, <c>auto</c>/<c>default</c>/absent = standard). The reported value wins over the requested one because it is
/// what the provider actually served.
/// </summary>
public static class SpeedModes
{
    public const string Standard = "standard";
    public const string Fast = "fast";
    public const string Flex = "flex";
    public const string Unknown = "unknown";
    /// <summary>Non-generation endpoints (count_tokens, models) have no speed mode.</summary>
    public const string NotApplicable = "n/a";

    public static string? Label(string? raw) => raw?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "fast" or "priority" => Fast,
        "flex" => Flex,
        "standard" or "default" or "auto" or "normal" => Standard,
        var other => other,
    };

    public static string Resolve(string? requested, string? reported) => Label(reported) ?? Label(requested) ?? Standard;

    /// <summary>SQL for a row's mode; generation rows recorded before modes were tracked read as 'unknown'.</summary>
    public static string Sql(string p = "") =>
        $"COALESCE({p}speed_mode, CASE WHEN {p}endpoint IN ('{Endpoints.Messages}', '{Endpoints.Responses}', '{Endpoints.Chat}') "
        + $"THEN '{Unknown}' ELSE '{NotApplicable}' END)";

    /// <summary>Same as <see cref="Sql"/> for an in-flight request.</summary>
    public static string Of(string? mode, string endpoint) => mode ?? (Endpoints.IsGeneration(endpoint) ? Unknown : NotApplicable);
}
