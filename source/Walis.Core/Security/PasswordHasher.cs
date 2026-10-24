using System.Security.Cryptography;

namespace Walis.Core.Security;

/// <summary>Stores passwords with PBKDF2-SHA256 in the format pbkdf2$iterations$salt$hash.</summary>
public static class PasswordHasher
{
    /// <summary>
    ///     OWASP's 2023 figure for PBKDF2-HMAC-SHA256. Hashes written with fewer iterations (210,000 before) still verify and are
    ///     rewritten on the next successful sign-in (<see cref="NeedsRehash" />).
    /// </summary>
    public const int Iterations = 600_000;

    /// <summary>
    ///     Recovery codes carry 40 random bits and up to ten of them are tried per attempt; the lower cost keeps one attempt from
    ///     costing seconds while the entropy, not the iteration count, is what protects them.
    /// </summary>
    public const int RecoveryCodeIterations = 210_000;
    private const int MinimumAcceptedIterations = 100_000;
    private const int MaximumAcceptedIterations = 1_000_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password)
    {
        return Hash(password, Iterations);
    }

    public static string Hash(string password, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);

        try
        {
            return $"pbkdf2${iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Whether a stored password hash uses fewer iterations than the current setting and should be rewritten.</summary>
    public static bool NeedsRehash(string? stored)
    {
        var parts = stored?.Split('$');
        return parts is { Length: 4 } && parts[0] == "pbkdf2" &&
               int.TryParse(parts[1], out var iterations) && iterations < Iterations;
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored) || stored.Length > 512) return false;

        var parts = stored.Split('$');

        if (parts.Length != 4 || parts[0] != "pbkdf2" ||
            !int.TryParse(parts[1], out var iterations) ||
            iterations is < MinimumAcceptedIterations or > MaximumAcceptedIterations ||
            parts[2].Length > 128 || parts[3].Length > 128)
            return false;

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);

            if (salt.Length is < 16 or > 64 || expected.Length is < 16 or > 64)
            {
                CryptographicOperations.ZeroMemory(expected);
                return false;
            }

            var actual =
                Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

            try
            {
                return CryptographicOperations.FixedTimeEquals(actual, expected);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(actual);
                CryptographicOperations.ZeroMemory(expected);
            }
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string CreateToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}