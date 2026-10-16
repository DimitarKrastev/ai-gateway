using System.Text.Json;
using System.Text.RegularExpressions;
using Gateway.Accounts;
using Gateway.Infrastructure;
using Gateway.Proxy;
using Microsoft.Data.Sqlite;

namespace Gateway.Bridge;

public static class BridgeModelSources
{
    public const string Api = "api";
    public const string ChatGpt = "chatgpt";
    public const string Manual = "manual";
}

public sealed record BridgeModelAccountView(long Id, string Name, string Kind, bool Enabled, string Source, DateTimeOffset LastSeenAt);

/// <summary>Admin view of one catalog entry: the upstream model, who lists it, and how it is exposed to Claude Code.</summary>
public sealed record BridgeCatalogEntry(
    string ModelId, string DisplayName, string? Description, IReadOnlyList<string> Sources,
    IReadOnlyList<BridgeModelAccountView> Accounts, IReadOnlyList<string> Efforts, string? DefaultEffort, bool Manual,
    DateTimeOffset FirstSeenAt, DateTimeOffset? LastSeenAt, bool Available,
    bool Exposed, string AdvertisedId, string AdvertisedName, string AdvertisedDescription,
    string DefaultAdvertisedId, string DefaultAdvertisedName, string DefaultAdvertisedDescription);

public sealed record BridgeRefreshResult(long AccountId, string AccountName, string Kind, bool Ok, int Models, string? Error);

public sealed record BridgeSelectionInput(string ModelId, bool Exposed, string? AdvertisedId, string? DisplayName, string? Description);

public sealed record BridgeValidationError(string ModelId, string Field, string Message);

/// <summary>
/// Catalog of OpenAI models that can be offered to Claude Code through the Anthropic→OpenAI bridge: the merged model
/// lists of the linked OpenAI accounts (API key: <c>GET /v1/models</c>, chat-capable ids only; ChatGPT subscription:
/// the Codex backend's <c>GET /backend-api/codex/models</c>), manual entries, and the user's selection (exposed flag,
/// advertised id, display name, description). Provider lists are fetched only on <see cref="RefreshAsync"/> (UI button).
/// Reads are served from an in-memory snapshot that every write invalidates.
/// </summary>
public sealed partial class BridgeModelCatalog(
    Database db, AccountStore accounts, TokenRefresher refresher, UpstreamRouter router, TimeProvider time, ILogger<BridgeModelCatalog> log)
{
    /// <summary>Codex CLI version sent to the Codex backend's model list (it filters models by client version).</summary>
    public const string CodexClientVersion = "0.80.0";
    public const string LastRefreshSetting = "bridge.lastRefreshAt";
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 1000;

    private sealed record Row(
        string ModelId, string? DisplayName, string? Description, string[] Efforts, string? DefaultEffort, bool Manual, bool Exposed,
        string? AdvertisedId, string? AdvertisedName, string? AdvertisedDescription, DateTimeOffset FirstSeen, DateTimeOffset? LastSeen,
        List<(long AccountId, string Source, DateTimeOffset LastSeen)> Accounts)
    {
        public string Display => string.IsNullOrWhiteSpace(DisplayName) ? ModelId : DisplayName!;
        public string DefaultAdvertisedId => BridgeModelIds.DefaultFor(ModelId);
        public string DefaultAdvertisedName => $"{Display} (OpenAI)";
        public string DefaultAdvertisedDescription =>
            $"OpenAI {ModelId} via the AI Coding Gateway" + (string.IsNullOrWhiteSpace(Description) ? "" : $" — {Description}");
        public string EffectiveId => string.IsNullOrWhiteSpace(AdvertisedId) ? DefaultAdvertisedId : AdvertisedId!;
        public string EffectiveName => string.IsNullOrWhiteSpace(AdvertisedName) ? DefaultAdvertisedName : AdvertisedName!;
        public string EffectiveDescription => string.IsNullOrWhiteSpace(AdvertisedDescription) ? DefaultAdvertisedDescription : AdvertisedDescription!;
    }

    private sealed record Snapshot(IReadOnlyList<Row> Rows, IReadOnlyDictionary<string, BridgeModel> Exposed, IReadOnlyList<BridgeModel> ExposedList);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Snapshot? _snapshot;

    // ---------- reads (hot path for the proxy) ----------

    /// <summary>
    /// The exposed model Claude Code asked for, matching the advertised id case-insensitively and ignoring a trailing
    /// <c>[…]</c> suffix (e.g. <c>[1m]</c>); null when <paramref name="requestedModel"/> is not an exposed OpenAI model.
    /// </summary>
    public BridgeModel? ResolveExposed(string? requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel)) return null;
        var snap = Load();
        if (snap.Exposed.Count == 0) return null;
        return snap.Exposed.GetValueOrDefault(BridgeModelIds.Normalize(requestedModel));
    }

    /// <summary>Exposed models in display order (advertised id).</summary>
    public IReadOnlyList<BridgeModel> ListExposed() => Load().ExposedList;

    /// <summary>All catalog entries for the admin UI, with account names resolved now.</summary>
    public IReadOnlyList<BridgeCatalogEntry> List()
    {
        var snap = Load();
        var byId = accounts.List(Providers.OpenAI).ToDictionary(a => a.Id);
        return snap.Rows.Select(r => ToEntry(r, byId)).ToList();
    }

    public BridgeCatalogEntry? Get(string modelId)
    {
        var row = Load().Rows.FirstOrDefault(r => r.ModelId == modelId);
        return row == null ? null : ToEntry(row, accounts.List(Providers.OpenAI).ToDictionary(a => a.Id));
    }

    public DateTimeOffset? LastRefreshAt =>
        long.TryParse(db.GetSetting(LastRefreshSetting), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static BridgeCatalogEntry ToEntry(Row r, IReadOnlyDictionary<long, LinkedAccount> byId)
    {
        var accountViews = r.Accounts
            .Where(a => byId.ContainsKey(a.AccountId))
            .Select(a => new BridgeModelAccountView(a.AccountId, byId[a.AccountId].Name, byId[a.AccountId].Kind, byId[a.AccountId].Enabled, a.Source, a.LastSeen))
            .OrderBy(a => a.Name).ToList();
        var sources = accountViews.Select(a => a.Source).Distinct().OrderBy(s => s).ToList();
        if (r.Manual) sources.Add(BridgeModelSources.Manual);
        return new BridgeCatalogEntry(
            r.ModelId, r.Display, r.Description, sources, accountViews, r.Efforts, r.DefaultEffort, r.Manual,
            r.FirstSeen, r.LastSeen, Available: r.Manual || accountViews.Count > 0,
            r.Exposed, r.EffectiveId, r.EffectiveName, r.EffectiveDescription,
            r.DefaultAdvertisedId, r.DefaultAdvertisedName, r.DefaultAdvertisedDescription);
    }

    private Snapshot Load()
    {
        var snap = Volatile.Read(ref _snapshot);
        if (snap != null) return snap;
        lock (_gate)
        {
            return _snapshot ??= ReadSnapshot();
        }
    }

    private void Invalidate()
    {
        lock (_gate) _snapshot = null;
    }

    private Snapshot ReadSnapshot()
    {
        using var conn = db.Open();
        var rows = new Dictionary<string, Row>(StringComparer.Ordinal);
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT * FROM bridge_models ORDER BY model_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var row = new Row(
                    r.GetString(r.GetOrdinal("model_id")), Str(r, "display_name"), Str(r, "description"),
                    ParseEfforts(Str(r, "efforts")), Str(r, "default_effort"),
                    r.GetInt64(r.GetOrdinal("manual")) != 0, r.GetInt64(r.GetOrdinal("exposed")) != 0,
                    Str(r, "advertised_id"), Str(r, "advertised_name"), Str(r, "advertised_description"),
                    DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("first_seen"))),
                    r.IsDBNull(r.GetOrdinal("last_seen")) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("last_seen"))),
                    []);
                rows[row.ModelId] = row;
            }
        }
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT model_id, account_id, source, last_seen FROM bridge_model_accounts ORDER BY account_id;";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                if (rows.TryGetValue(r.GetString(0), out var row))
                    row.Accounts.Add((r.GetInt64(1), r.GetString(2), DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(3))));
        }

        var exposed = new Dictionary<string, BridgeModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.Values.Where(r => r.Exposed))
        {
            var model = new BridgeModel(
                row.EffectiveId, row.ModelId, row.EffectiveName, row.EffectiveDescription,
                row.Accounts.Select(a => a.AccountId).Distinct().ToList(), row.Efforts, row.DefaultEffort, row.FirstSeen);
            exposed.TryAdd(model.AdvertisedId, model);
        }
        return new Snapshot(rows.Values.ToList(), exposed, exposed.Values.OrderBy(m => m.AdvertisedId, StringComparer.OrdinalIgnoreCase).ToList());
    }

    // ---------- refresh from the providers ----------

    /// <summary>Lists models from every enabled OpenAI account and merges them into the catalog. One result per account.</summary>
    public async Task<IReadOnlyList<BridgeRefreshResult>> RefreshAsync(CancellationToken ct)
    {
        await _refreshGate.WaitAsync(ct);
        try
        {
            var openai = accounts.List(Providers.OpenAI).Where(a => a.Enabled).ToList();
            var fetched = await Task.WhenAll(openai.Select(a => FetchAsync(a, ct)));
            var now = time.GetUtcNow().ToUnixTimeMilliseconds();

            using var conn = db.Open();
            using var tx = conn.BeginTransaction();
            foreach (var (account, models, _) in fetched)
            {
                if (models == null) continue;
                Exec(conn, tx, "DELETE FROM bridge_model_accounts WHERE account_id = $a;", ("$a", account.Id));
                var source = account.Kind == AccountKinds.ChatGptOAuth ? BridgeModelSources.ChatGpt : BridgeModelSources.Api;
                foreach (var m in models)
                {
                    Exec(conn, tx, """
                        INSERT INTO bridge_models (model_id, display_name, description, efforts, default_effort, first_seen, last_seen, updated_at)
                        VALUES ($id, $name, $desc, $efforts, $def, $now, $now, $now)
                        ON CONFLICT(model_id) DO UPDATE SET
                            display_name = COALESCE(excluded.display_name, display_name),
                            description = COALESCE(excluded.description, description),
                            efforts = COALESCE(excluded.efforts, efforts),
                            default_effort = COALESCE(excluded.default_effort, default_effort),
                            last_seen = excluded.last_seen,
                            updated_at = excluded.updated_at;
                        """,
                        ("$id", m.Id), ("$name", m.DisplayName), ("$desc", m.Description),
                        ("$efforts", m.Efforts.Count > 0 ? JsonSerializer.Serialize(m.Efforts) : null), ("$def", m.DefaultEffort), ("$now", now));
                    Exec(conn, tx, """
                        INSERT INTO bridge_model_accounts (model_id, account_id, source, last_seen) VALUES ($id, $a, $src, $now)
                        ON CONFLICT(model_id, account_id) DO UPDATE SET source = excluded.source, last_seen = excluded.last_seen;
                        """, ("$id", m.Id), ("$a", account.Id), ("$src", source), ("$now", now));
                }
            }
            // Models nobody lists any more disappear unless the user added them by hand or exposes them (kept, shown unavailable).
            Exec(conn, tx, """
                DELETE FROM bridge_models WHERE manual = 0 AND exposed = 0
                  AND NOT EXISTS (SELECT 1 FROM bridge_model_accounts a WHERE a.model_id = bridge_models.model_id);
                """);
            tx.Commit();
            db.SetSetting(LastRefreshSetting, now.ToString());
            Invalidate();
            return fetched.Select(f => new BridgeRefreshResult(
                f.Account.Id, f.Account.Name, f.Account.Kind, f.Models != null, f.Models?.Count ?? 0, f.Error)).ToList();
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public sealed record ListedModel(string Id, string? DisplayName, string? Description, IReadOnlyList<string> Efforts, string? DefaultEffort);

    private async Task<(LinkedAccount Account, List<ListedModel>? Models, string? Error)> FetchAsync(LinkedAccount account, CancellationToken ct)
    {
        var chatgpt = account.Kind == AccountKinds.ChatGptOAuth;
        var uri = router.BuildUri(account.Kind, "v1/models", chatgpt ? $"?client_version={CodexClientVersion}" : "");
        try
        {
            using var res = await refresher.SendAuthenticatedAsync(account, creds =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, uri);
                req.Headers.Accept.ParseAdd("application/json");
                if (chatgpt) req.Headers.TryAddWithoutValidation("originator", "codex_cli_rs");
                ProxyHandler.InjectCredentials(req, account.Kind, creds);
                return req;
            }, ct);
            var text = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode) return (account, null, $"{uri.GetLeftPart(UriPartial.Path)} returned {(int)res.StatusCode}{ErrorDetail(text)}");
            using var doc = JsonDocument.Parse(text);
            return (account, chatgpt ? ParseCodexModels(doc.RootElement) : ParseApiModels(doc.RootElement), null);
        }
        catch (JsonException)
        {
            return (account, null, "The model list was not valid JSON.");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            log.LogWarning(e, "Listing models for account {Account} failed", account.Id);
            return (account, null, e.Message);
        }
    }

    private static string ErrorDetail(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
            {
                var msg = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var m) ? m.GetString()
                    : e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (!string.IsNullOrEmpty(msg)) return $": {msg}";
            }
        }
        catch (JsonException) { }
        return "";
    }

    /// <summary>OpenAI API <c>{data:[{id}]}</c>, keeping models usable with the Responses API for text + tools.</summary>
    public static List<ListedModel> ParseApiModels(JsonElement root)
    {
        var list = new List<ListedModel>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in data.EnumerateArray())
            if (m.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String && IsChatModel(id.GetString()!))
                list.Add(new ListedModel(id.GetString()!, null, null, [], null));
        return list;
    }

    /// <summary>Codex backend <c>{models:[{slug, display_name, description, supported_reasoning_levels, visibility…}]}</c>.</summary>
    public static List<ListedModel> ParseCodexModels(JsonElement root)
    {
        var list = new List<ListedModel>();
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return list;
        foreach (var m in models.EnumerateArray())
        {
            var slug = JStr(m, "slug") ?? JStr(m, "id");
            if (string.IsNullOrEmpty(slug)) continue;
            if (JStr(m, "visibility") is "hide" or "none") continue;
            var efforts = new List<string>();
            if (m.TryGetProperty("supported_reasoning_levels", out var levels) && levels.ValueKind == JsonValueKind.Array)
                foreach (var l in levels.EnumerateArray())
                    if ((l.ValueKind == JsonValueKind.String ? l.GetString() : JStr(l, "effort")) is { Length: > 0 } effort)
                        efforts.Add(effort);
            list.Add(new ListedModel(slug, JStr(m, "display_name"), JStr(m, "description"), efforts, JStr(m, "default_reasoning_level")));
        }
        return list;
    }

    private static readonly string[] NonChatMarkers =
    [
        "audio", "realtime", "tts", "transcribe", "whisper", "image", "dall-e", "embedding", "moderation", "search",
        "instruct", "davinci", "babbage", "computer-use", "deep-research", "sora",
    ];

    [GeneratedRegex(@"^(gpt-|o\d|codex|chatgpt-)")]
    private static partial Regex ChatFamily();

    [GeneratedRegex(@"-\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DatedSnapshot();

    /// <summary>Chat/reasoning model families; audio, realtime, image, embedding, moderation, search and dated snapshots are left out.</summary>
    public static bool IsChatModel(string id)
    {
        var s = id.ToLowerInvariant();
        return ChatFamily().IsMatch(s) && !DatedSnapshot().IsMatch(s) && !NonChatMarkers.Any(s.Contains);
    }

    // ---------- manual entries and selection ----------

    [GeneratedRegex(@"^[A-Za-z0-9._:/-]{1,200}$")]
    private static partial Regex ModelIdPattern();

    /// <summary>Adds (or marks as manual) a model the provider lists don't show. Returns an error message on invalid input.</summary>
    public (BridgeCatalogEntry? Entry, string? Error) AddManual(string? modelId, string? displayName, string? description, IReadOnlyList<string>? efforts)
    {
        modelId = modelId?.Trim();
        if (string.IsNullOrEmpty(modelId) || !ModelIdPattern().IsMatch(modelId))
            return (null, "Enter the OpenAI model id exactly as the API expects it (letters, digits, '.', '_', ':', '/', '-').");
        if (displayName?.Length > MaxNameLength || description?.Length > MaxDescriptionLength) return (null, "Name or description is too long.");
        var effortList = efforts?.Select(e => e.Trim().ToLowerInvariant()).Where(e => e.Length > 0).Distinct().ToList();
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        using (var conn = db.Open())
            Exec(conn, null, """
                INSERT INTO bridge_models (model_id, display_name, description, efforts, manual, first_seen, updated_at)
                VALUES ($id, $name, $desc, $efforts, 1, $now, $now)
                ON CONFLICT(model_id) DO UPDATE SET
                    manual = 1,
                    display_name = COALESCE(excluded.display_name, display_name),
                    description = COALESCE(excluded.description, description),
                    efforts = COALESCE(excluded.efforts, efforts),
                    updated_at = excluded.updated_at;
                """,
                ("$id", modelId), ("$name", Blank(displayName)), ("$desc", Blank(description)),
                ("$efforts", effortList is { Count: > 0 } ? JsonSerializer.Serialize(effortList) : null), ("$now", now));
        Invalidate();
        return (Get(modelId), null);
    }

    public enum RemoveResult { Removed, NotFound, ListedByAccounts }

    /// <summary>Removes a manual entry. Models an account lists can't be removed (untick them instead).</summary>
    public RemoveResult RemoveManual(string modelId)
    {
        var row = Load().Rows.FirstOrDefault(r => r.ModelId == modelId);
        if (row == null) return RemoveResult.NotFound;
        if (row.Accounts.Count > 0) return RemoveResult.ListedByAccounts;
        using (var conn = db.Open())
            Exec(conn, null, "DELETE FROM bridge_models WHERE model_id = $id;", ("$id", modelId));
        Invalidate();
        return RemoveResult.Removed;
    }

    /// <summary>
    /// Applies selection changes (exposed flag, advertised id/name/description; blank = default) atomically. Every
    /// exposed model must end up with a valid, unique advertised id; otherwise nothing is saved and the errors are returned.
    /// </summary>
    public IReadOnlyList<BridgeValidationError> SaveSelection(IReadOnlyList<BridgeSelectionInput> inputs)
    {
        var rows = Load().Rows.ToDictionary(r => r.ModelId);
        var errors = new List<BridgeValidationError>();
        var final = new Dictionary<string, Row>(rows);
        foreach (var input in inputs)
        {
            if (!rows.TryGetValue(input.ModelId ?? "", out var row))
            {
                errors.Add(new(input.ModelId ?? "", "modelId", "Unknown model — refresh the catalog or add it manually."));
                continue;
            }
            var id = Blank(input.AdvertisedId?.Trim());
            var name = Blank(input.DisplayName?.Trim());
            var desc = Blank(input.Description?.Trim());
            if (name?.Length > MaxNameLength) errors.Add(new(row.ModelId, "displayName", $"At most {MaxNameLength} characters."));
            if (desc?.Length > MaxDescriptionLength) errors.Add(new(row.ModelId, "description", $"At most {MaxDescriptionLength} characters."));
            final[row.ModelId] = row with
            {
                Exposed = input.Exposed,
                // Store only overrides so defaults follow later display-name changes.
                AdvertisedId = id == row.DefaultAdvertisedId ? null : id,
                AdvertisedName = name == row.DefaultAdvertisedName ? null : name,
                AdvertisedDescription = desc == row.DefaultAdvertisedDescription ? null : desc,
            };
        }

        foreach (var row in final.Values.Where(r => r.Exposed))
            if (BridgeModelIds.Validate(row.EffectiveId) is { } problem)
                errors.Add(new(row.ModelId, "advertisedId", problem));
        foreach (var group in final.Values.Where(r => r.Exposed).GroupBy(r => r.EffectiveId, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var row in group)
                errors.Add(new(row.ModelId, "advertisedId", $"\"{row.EffectiveId}\" is used by more than one exposed model."));
        if (errors.Count > 0) return errors;

        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            foreach (var input in inputs)
            {
                var row = final[input.ModelId];
                Exec(conn, tx, """
                    UPDATE bridge_models SET exposed = $exp, advertised_id = $aid, advertised_name = $aname,
                        advertised_description = $adesc, updated_at = $now WHERE model_id = $id;
                    """,
                    ("$id", row.ModelId), ("$exp", row.Exposed ? 1 : 0), ("$aid", row.AdvertisedId), ("$aname", row.AdvertisedName),
                    ("$adesc", row.AdvertisedDescription), ("$now", now));
            }
            tx.Commit();
        }
        Invalidate();
        return [];
    }

    // ---------- helpers ----------

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static string[] ParseEfforts(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? Str(SqliteDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static string? JStr(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }
}
