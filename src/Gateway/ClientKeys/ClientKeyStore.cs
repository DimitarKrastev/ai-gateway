using System.Security.Cryptography;
using System.Text;
using Gateway.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Gateway.ClientKeys;

public sealed record ClientKey(long Id, string Name, string Prefix, DateTimeOffset CreatedAt, DateTimeOffset? LastUsedAt, DateTimeOffset? RevokedAt);

/// <summary>
/// Keys that agents present to the gateway (instead of a provider key). Only a SHA-256 hash is stored; the plaintext
/// is shown once at creation. Keys are 32 random base62 characters, so a fast hash is sufficient.
/// </summary>
public sealed class ClientKeyStore(Database db, TimeProvider time)
{
    public const string KeyPrefix = "aigw_";
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public (ClientKey Key, string Plaintext) Create(string name)
    {
        var plaintext = KeyPrefix + RandomNumberGenerator.GetString(Alphabet, 32);
        var now = time.GetUtcNow().ToUnixTimeMilliseconds();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO client_keys (name, key_hash, prefix, created_at) VALUES ($n, $h, $p, $now) RETURNING id;";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$h", Hash(plaintext));
        cmd.Parameters.AddWithValue("$p", plaintext[..(KeyPrefix.Length + 4)]);
        cmd.Parameters.AddWithValue("$now", now);
        var id = Convert.ToInt64(cmd.ExecuteScalar());
        return (Get(id)!, plaintext);
    }

    public IReadOnlyList<ClientKey> List()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM client_keys ORDER BY id;";
        using var r = cmd.ExecuteReader();
        var list = new List<ClientKey>();
        while (r.Read()) list.Add(Read(r));
        return list;
    }

    public ClientKey? Get(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM client_keys WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? Read(r) : null;
    }

    public bool Revoke(long id)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE client_keys SET revoked_at = $now WHERE id = $id AND revoked_at IS NULL;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", time.GetUtcNow().ToUnixTimeMilliseconds());
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool AnyActive()
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT EXISTS (SELECT 1 FROM client_keys WHERE revoked_at IS NULL);";
        return Convert.ToInt64(cmd.ExecuteScalar()) != 0;
    }

    /// <summary>Returns the active key matching <paramref name="plaintext"/>, recording its use (at most once a minute).</summary>
    public ClientKey? Validate(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext) || !plaintext.StartsWith(KeyPrefix)) return null;
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM client_keys WHERE key_hash = $h AND revoked_at IS NULL;";
        cmd.Parameters.AddWithValue("$h", Hash(plaintext));
        ClientKey? key;
        using (var r = cmd.ExecuteReader()) key = r.Read() ? Read(r) : null;
        if (key == null) return null;

        var now = time.GetUtcNow();
        if (key.LastUsedAt is null || now - key.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            using var upd = conn.CreateCommand();
            upd.CommandText = "UPDATE client_keys SET last_used_at = $now WHERE id = $id;";
            upd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
            upd.Parameters.AddWithValue("$id", key.Id);
            upd.ExecuteNonQuery();
        }
        return key;
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    private static DateTimeOffset? Time(SqliteDataReader r, string col) =>
        r.IsDBNull(r.GetOrdinal(col)) ? null : DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal(col)));

    private static ClientKey Read(SqliteDataReader r) => new(
        r.GetInt64(r.GetOrdinal("id")), r.GetString(r.GetOrdinal("name")), r.GetString(r.GetOrdinal("prefix")),
        DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(r.GetOrdinal("created_at"))),
        Time(r, "last_used_at"), Time(r, "revoked_at"));
}
