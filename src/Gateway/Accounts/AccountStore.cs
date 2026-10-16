using System.Text.Json;
using Gateway.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;

namespace Gateway.Accounts;

/// <summary>Linked accounts in SQLite; credentials are encrypted with ASP.NET Data Protection.</summary>
public sealed class AccountStore(Database db, IDataProtectionProvider dataProtection, TimeProvider time)
{
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Gateway.Accounts.Credentials.v1");

    public IReadOnlyList<LinkedAccount> List(string? provider = null)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts" + (provider != null ? " WHERE provider = $p" : "") + " ORDER BY priority, id;";
        if (provider != null) cmd.Parameters.AddWithValue("$p", provider);
        using var r = cmd.ExecuteReader();
        var list = new List<LinkedAccount>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public LinkedAccount? Get(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM accounts WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public LinkedAccount Create(string name, string kind, bool enabled, int priority, AccountCredentials credentials)
    {
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO accounts (name, provider, kind, enabled, priority, credentials_enc, created_at, updated_at)
            VALUES ($name, $provider, $kind, $enabled, $priority, $cred, $now, $now) RETURNING id;
            """;
        cmd.Parameters.AddWithValue("$name", name);
        cmd.Parameters.AddWithValue("$provider", AccountKinds.ProviderOf(kind)!);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$priority", priority);
        cmd.Parameters.AddWithValue("$cred", Protect(credentials));
        cmd.Parameters.AddWithValue("$now", now);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return Get(id)!;
    }

    public LinkedAccount? Update(long id, string? name, bool? enabled, int? priority, string? kind, AccountCredentials? credentials)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE accounts SET
                name = COALESCE($name, name),
                enabled = COALESCE($enabled, enabled),
                priority = COALESCE($priority, priority),
                kind = COALESCE($kind, kind),
                credentials_enc = COALESCE($cred, credentials_enc),
                updated_at = $now
            WHERE id = $id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$name", (object?)name ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$enabled", enabled is null ? DBNull.Value : enabled.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("$priority", (object?)priority ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$kind", (object?)kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$cred", credentials is null ? DBNull.Value : Protect(credentials));
        cmd.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery() == 0 ? null : Get(id);
    }

    public void UpdateCredentials(long id, AccountCredentials credentials) => Update(id, null, null, null, null, credentials);

    public bool Delete(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM accounts WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    private string Protect(AccountCredentials c) => _protector.Protect(JsonSerializer.Serialize(c));

    private LinkedAccount Read(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")),
        r.GetString(r.GetOrdinal("name")),
        r.GetString(r.GetOrdinal("provider")),
        r.GetString(r.GetOrdinal("kind")),
        r.GetInt64(r.GetOrdinal("enabled")) != 0,
        r.GetInt32(r.GetOrdinal("priority")),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_at"))),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("updated_at"))),
        JsonSerializer.Deserialize<AccountCredentials>(_protector.Unprotect(r.GetString(r.GetOrdinal("credentials_enc"))))!);
}
