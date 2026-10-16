using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Gateway.Infrastructure;

namespace Gateway.Auth;

/// <summary>
/// The single admin password for the web UI, from (in order): GATEWAY_ADMIN_PASSWORD (managed by configuration, not
/// changeable in the UI); a password set in the UI, stored only as a salted PBKDF2 hash in &lt;data dir&gt;/admin-password.hash;
/// or a random password generated on first start and kept in &lt;data dir&gt;/admin-password.txt until it is changed.
/// </summary>
public sealed class AdminCredentials
{
    public const string PasswordFileName = "admin-password.txt";
    public const string HashFileName = "admin-password.hash";
    public const int MinLength = 12;
    private const int Iterations = 600_000;

    private sealed record StoredHash(string Salt, string Hash, int Iterations, string Stamp);

    private readonly string _dataDir;
    private readonly ILogger<AdminCredentials> _logger;
    private readonly Lock _gate = new();
    private StoredHash _current;

    /// <summary>configured, generated or custom.</summary>
    public string Source { get; private set; }

    /// <summary>Changes whenever the password does; sessions signed in under another stamp are rejected.</summary>
    public string Stamp => _current.Stamp;

    public AdminCredentials(GatewayOptions options, ILogger<AdminCredentials> logger)
    {
        _dataDir = options.DataDir;
        _logger = logger;
        var hashFile = Path.Combine(_dataDir, HashFileName);
        if (options.AdminPassword is { } configured)
        {
            Source = "configured";
            _current = Hash(configured, stamp: "config");
        }
        else if (File.Exists(hashFile) && JsonSerializer.Deserialize<StoredHash>(File.ReadAllText(hashFile)) is { } stored)
        {
            Source = "custom";
            _current = stored;
        }
        else
        {
            Source = "generated";
            var file = Path.Combine(_dataDir, PasswordFileName);
            string password;
            if (File.Exists(file))
            {
                password = File.ReadAllText(file).Trim();
            }
            else
            {
                password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', 'x').Replace('/', 'y');
                WritePrivate(file, password + Environment.NewLine);
                logger.LogWarning("Generated admin password; see {File}", file);
            }
            _current = Hash(password, stamp: "generated");
        }
    }

    public bool Verify(string? candidate)
    {
        if (candidate is null) return false;
        StoredHash h;
        lock (_gate) h = _current;
        var expected = Convert.FromBase64String(h.Hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(candidate), Convert.FromBase64String(h.Salt), h.Iterations,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    /// <summary>Sets a new password (hash only) and removes the generated plaintext file. Null on success, else the reason.</summary>
    public string? Change(string? current, string? next)
    {
        if (Source == "configured")
            return "The admin password is set by GATEWAY_ADMIN_PASSWORD (or Gateway:AdminPassword); change it there and restart the gateway.";
        if (!Verify(current)) return "The current password is not correct.";
        if (string.IsNullOrEmpty(next) || next.Length < MinLength) return $"The new password must be at least {MinLength} characters.";
        if (next.Trim() != next) return "The new password must not start or end with spaces.";

        var stored = Hash(next, stamp: Convert.ToHexString(RandomNumberGenerator.GetBytes(8)));
        lock (_gate)
        {
            WritePrivate(Path.Combine(_dataDir, HashFileName), JsonSerializer.Serialize(stored));
            _current = stored;
            Source = "custom";
        }
        var plain = Path.Combine(_dataDir, PasswordFileName);
        if (File.Exists(plain)) File.Delete(plain);
        _logger.LogInformation("Admin password changed");
        return null;
    }

    private static StoredHash Hash(string password, string stamp)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return new StoredHash(Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations, stamp);
    }

    /// <summary>Writes atomically with owner-only permissions.</summary>
    private static void WritePrivate(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(tmp, path, overwrite: true);
    }
}
