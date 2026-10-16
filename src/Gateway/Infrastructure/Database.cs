using Microsoft.Data.Sqlite;

namespace Gateway.Infrastructure;

/// <summary>
/// SQLite access plus a forward-only migration runner. To change the schema, append a new
/// entry to <see cref="Migrations"/>; never edit an entry that has shipped.
/// </summary>
public sealed class Database
{
    private readonly string _connectionString;

    public static readonly IReadOnlyList<(int Version, string Sql)> Migrations =
    [
        (1, """
            CREATE TABLE settings (
                key   TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """),
        // Timestamps are unix milliseconds (INTEGER).
        (2, """
            CREATE TABLE accounts (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                name            TEXT NOT NULL,
                provider        TEXT NOT NULL,
                kind            TEXT NOT NULL,
                enabled         INTEGER NOT NULL DEFAULT 1,
                priority        INTEGER NOT NULL DEFAULT 100,
                credentials_enc TEXT NOT NULL,
                created_at      INTEGER NOT NULL,
                updated_at      INTEGER NOT NULL
            );
            CREATE TABLE client_keys (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                name         TEXT NOT NULL,
                key_hash     TEXT NOT NULL UNIQUE,
                prefix       TEXT NOT NULL,
                created_at   INTEGER NOT NULL,
                last_used_at INTEGER,
                revoked_at   INTEGER
            );
            CREATE TABLE session_bindings (
                provider      TEXT NOT NULL,
                session_id    TEXT NOT NULL,
                account_id    INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                client_key_id INTEGER,
                first_seen    INTEGER NOT NULL,
                last_seen     INTEGER NOT NULL,
                request_count INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (provider, session_id)
            );
            CREATE INDEX ix_session_bindings_account ON session_bindings(account_id, last_seen);
            """),
        // One row per upstream attempt (see Gateway.Usage). account_id has no FK: history outlives deleted accounts.
        (3, """
            CREATE TABLE requests (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                request_id          TEXT NOT NULL,
                attempt             INTEGER NOT NULL,
                retried             INTEGER NOT NULL DEFAULT 0,
                provider            TEXT NOT NULL,
                account_id          INTEGER,
                account_kind        TEXT,
                session_id          TEXT,
                session_source      TEXT,
                client_key_id       INTEGER,
                client              TEXT NOT NULL,
                client_version      TEXT,
                agent_id            TEXT,
                agent_role          TEXT,
                method              TEXT NOT NULL,
                path                TEXT NOT NULL,
                endpoint            TEXT NOT NULL,
                model               TEXT,
                stream              INTEGER NOT NULL DEFAULT 0,
                status              INTEGER NOT NULL DEFAULT 0,
                error_type          TEXT,
                stop_reason         TEXT,
                cancelled           INTEGER NOT NULL DEFAULT 0,
                input_tokens        INTEGER NOT NULL DEFAULT 0,
                output_tokens       INTEGER NOT NULL DEFAULT 0,
                cache_read_tokens   INTEGER NOT NULL DEFAULT 0,
                cache_write_tokens  INTEGER NOT NULL DEFAULT 0,
                reasoning_tokens    INTEGER NOT NULL DEFAULT 0,
                context_tokens      INTEGER NOT NULL DEFAULT 0,
                tool_calls          INTEGER NOT NULL DEFAULT 0,
                cost_usd            REAL NOT NULL DEFAULT 0,
                priced              INTEGER NOT NULL DEFAULT 0,
                started_at          INTEGER NOT NULL,
                response_started_at INTEGER,
                first_byte_at       INTEGER,
                completed_at        INTEGER NOT NULL,
                request_bytes       INTEGER NOT NULL DEFAULT 0,
                response_bytes      INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX ix_requests_time ON requests(started_at);
            CREATE INDEX ix_requests_account ON requests(account_id, started_at);
            CREATE INDEX ix_requests_session ON requests(session_id, started_at);
            CREATE INDEX ix_requests_model ON requests(model, started_at);
            CREATE INDEX ix_requests_agent ON requests(agent_id, started_at);
            CREATE TABLE request_tools (
                request_row_id INTEGER NOT NULL REFERENCES requests(id) ON DELETE CASCADE,
                started_at     INTEGER NOT NULL,
                tool           TEXT NOT NULL,
                count          INTEGER NOT NULL
            );
            CREATE INDEX ix_request_tools_time ON request_tools(started_at);
            CREATE INDEX ix_request_tools_request ON request_tools(request_row_id);
            """),
        // Limits, reset credits and routing decisions (see Gateway.Limits / Gateway.Routing).
        (4, """
            CREATE TABLE account_limits (
                account_id     INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                key            TEXT NOT NULL,
                label          TEXT NOT NULL,
                utilization    REAL,
                used           REAL,
                lim            REAL,
                remaining      REAL,
                resets_at      INTEGER,
                window_minutes INTEGER,
                status         TEXT NOT NULL,
                source         TEXT NOT NULL,
                updated_at     INTEGER NOT NULL,
                PRIMARY KEY (account_id, key)
            );
            CREATE TABLE account_limit_state (
                account_id           INTEGER PRIMARY KEY REFERENCES accounts(id) ON DELETE CASCADE,
                exhausted_until      INTEGER,
                exhausted_reason     TEXT,
                unified_status       TEXT,
                representative_claim TEXT,
                overage_status       TEXT,
                plan_type            TEXT,
                refreshed_at         INTEGER,
                refresh_error        TEXT,
                resets_synced_at     INTEGER
            );
            CREATE TABLE account_resets (
                id                INTEGER PRIMARY KEY AUTOINCREMENT,
                account_id        INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                source            TEXT NOT NULL,
                provider_reset_id TEXT,
                expires_at        INTEGER,
                note              TEXT,
                created_at        INTEGER NOT NULL,
                used_at           INTEGER,
                used_by           TEXT,
                UNIQUE (account_id, provider_reset_id)
            );
            CREATE TABLE routing_events (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                at              INTEGER NOT NULL,
                provider        TEXT NOT NULL,
                session_id      TEXT,
                from_account_id INTEGER,
                to_account_id   INTEGER,
                reason          TEXT NOT NULL,
                detail          TEXT
            );
            CREATE INDEX ix_routing_events_time ON routing_events(at);
            CREATE INDEX ix_routing_events_session ON routing_events(session_id, at);
            """),
        // Output throughput and speed mode (see Gateway.Usage.Throughput / SpeedModes). Older rows keep NULLs: they have no
        // throughput and their speed mode reads as 'unknown'.
        (5, """
            ALTER TABLE requests ADD COLUMN first_token_at INTEGER;
            ALTER TABLE requests ADD COLUMN generation_ms INTEGER;
            ALTER TABLE requests ADD COLUMN requested_speed TEXT;
            ALTER TABLE requests ADD COLUMN reported_speed TEXT;
            ALTER TABLE requests ADD COLUMN speed_mode TEXT;
            """),
        // OpenAI models offered to Claude Code through the Anthropic→OpenAI bridge (see Gateway.Bridge.BridgeModelCatalog):
        // the merged model lists of the linked OpenAI accounts plus manual entries, and which ones are exposed under which id.
        (6, """
            CREATE TABLE bridge_models (
                model_id               TEXT PRIMARY KEY,
                display_name           TEXT,
                description            TEXT,
                efforts                TEXT,
                default_effort         TEXT,
                manual                 INTEGER NOT NULL DEFAULT 0,
                exposed                INTEGER NOT NULL DEFAULT 0,
                advertised_id          TEXT,
                advertised_name        TEXT,
                advertised_description TEXT,
                first_seen             INTEGER NOT NULL,
                last_seen              INTEGER,
                updated_at             INTEGER NOT NULL
            );
            CREATE TABLE bridge_model_accounts (
                model_id   TEXT NOT NULL REFERENCES bridge_models(model_id) ON DELETE CASCADE,
                account_id INTEGER NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
                source     TEXT NOT NULL,
                last_seen  INTEGER NOT NULL,
                PRIMARY KEY (model_id, account_id)
            );
            """),
        // Bridged requests (Claude Code → OpenAI): the advertised model id the client asked for and the translation applied.
        // NULL for pass-through traffic, where the requested model is the recorded model.
        (7, """
            ALTER TABLE requests ADD COLUMN requested_model TEXT;
            ALTER TABLE requests ADD COLUMN bridge TEXT;
            """),
    ];

    public Database(GatewayOptions options)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = options.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            DefaultTimeout = 30,
        }.ToString();
    }

    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 30000;";
        cmd.ExecuteNonQuery();
        return conn;
    }

    public void Migrate()
    {
        using var conn = Open();
        using (var wal = conn.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            wal.ExecuteNonQuery();
        }
        Execute(conn, "CREATE TABLE IF NOT EXISTS schema_version (version INTEGER NOT NULL);");
        var current = Convert.ToInt32(Scalar(conn, "SELECT COALESCE(MAX(version), 0) FROM schema_version;"));

        foreach (var (version, sql) in Migrations.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = conn.BeginTransaction();
            Execute(conn, sql, tx);
            Execute(conn, $"INSERT INTO schema_version (version) VALUES ({version});", tx);
            tx.Commit();
        }
    }

    public string? GetSetting(string key)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    public void SetSetting(string key, string value)
    {
        using var conn = Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO settings (key, value) VALUES ($k, $v) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    private static void Execute(SqliteConnection conn, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
