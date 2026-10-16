using System.Security.Cryptography;
using System.Text;
using Gateway.Infrastructure;

namespace Gateway.Auth;

/// <summary>
/// The single admin password for the web UI: GATEWAY_ADMIN_PASSWORD if set, otherwise a
/// random password generated on first start and kept in &lt;data dir&gt;/admin-password.txt.
/// </summary>
public sealed class AdminCredentials
{
    public const string PasswordFileName = "admin-password.txt";
    private readonly byte[] _passwordHash;

    public AdminCredentials(GatewayOptions options, ILogger<AdminCredentials> logger)
    {
        var password = options.AdminPassword;
        if (password is null)
        {
            var file = Path.Combine(options.DataDir, PasswordFileName);
            if (File.Exists(file))
            {
                password = File.ReadAllText(file).Trim();
            }
            else
            {
                password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
                    .Replace('+', 'x').Replace('/', 'y');
                File.WriteAllText(file, password + Environment.NewLine);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                logger.LogWarning("Generated admin password; see {File}", file);
            }
        }
        _passwordHash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    }

    public bool Verify(string? candidate) =>
        candidate is not null &&
        CryptographicOperations.FixedTimeEquals(_passwordHash, SHA256.HashData(Encoding.UTF8.GetBytes(candidate)));
}
