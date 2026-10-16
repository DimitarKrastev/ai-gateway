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
