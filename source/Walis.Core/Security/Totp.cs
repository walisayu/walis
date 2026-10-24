using System.Security.Cryptography;
using System.Text;

namespace Walis.Core.Security;

/// <summary>
///     RFC 6238 time-based one-time passwords (TOTP): HMAC-SHA1, 30-second period, 6 digits,
///     compatible with Google Authenticator, 1Password, Authy and other authenticators.
/// </summary>
public static class Totp
{
    public const int Digits = 6;
    public const int PeriodSeconds = 30;

    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string CreateSecret(int byteCount = 20)
    {
        return ToBase32(RandomNumberGenerator.GetBytes(byteCount));
    }

    /// <summary>Builds the otpauth URL an authenticator can scan.</summary>
    public static string BuildUri(string issuer, string account, string secret)
    {
        var label = Uri.EscapeDataString($"{issuer}:{account}");
        var parameters = string.Join('&',
            $"secret={secret}",
            $"issuer={Uri.EscapeDataString(issuer)}",
            "algorithm=SHA1",
            $"digits={Digits}",
            $"period={PeriodSeconds}");

        return $"otpauth://totp/{label}?{parameters}";
    }

    public static string Compute(string secret, long counter)
    {
        var key = FromBase32(secret);
        var buffer = new byte[8];

        for (var index = 7; index >= 0; index--)
        {
            buffer[index] = (byte)(counter & 0xFF);
            counter >>= 8;
        }

        var hash = HMACSHA1.HashData(key, buffer);
        var offset = hash[^1] & 0x0F;

        var binary = ((hash[offset] & 0x7F) << 24)
                     | ((hash[offset + 1] & 0xFF) << 16)
                     | ((hash[offset + 2] & 0xFF) << 8)
                     | (hash[offset + 3] & 0xFF);

        return (binary % (int)Math.Pow(10, Digits)).ToString(new string('0', Digits));
    }

    /// <summary>Verifies a code, allowing one time window before and after by default to tolerate clock drift.</summary>
    public static bool Verify(string? secret, string? code, int window = 1, DateTimeOffset? now = null)
    {
        return TryVerify(secret, code, out _, window, now);
    }

    /// <summary>Verifies and returns the time-step counter that actually matched, for atomic replay protection in the database.</summary>
    public static bool TryVerify(
        string? secret,
        string? code,
        out long matchedCounter,
        int window = 1,
        DateTimeOffset? now = null)
    {
        matchedCounter = 0;
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(code)) return false;

        var normalized = new string(code.Where(char.IsDigit).ToArray());

        if (normalized.Length != Digits) return false;

        var counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / PeriodSeconds;

        for (var offset = -window; offset <= window; offset++)
        {
            var candidate = Compute(secret, counter + offset);

            if (CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(candidate),
                    Encoding.ASCII.GetBytes(normalized)))
            {
                matchedCounter = counter + offset;
                return true;
            }
        }

        return false;
    }

    /// <summary>Backup codes for signing in when the authenticator is unavailable, in the format xxxx-xxxx.</summary>
    public static IReadOnlyList<string> CreateRecoveryCodes(int count = 8)
    {
        var codes = new List<string>(count);

        for (var index = 0; index < count; index++)
        {
            var value = ToBase32(RandomNumberGenerator.GetBytes(5))[..8].ToLowerInvariant();
            codes.Add($"{value[..4]}-{value[4..]}");
        }

        return codes;
    }

    public static string NormalizeRecoveryCode(string code)
    {
        return new string(code.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }

    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder();
        var buffer = 0;
        var bits = 0;

        foreach (var value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;

            while (bits >= 5)
            {
                builder.Append(Base32Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0) builder.Append(Base32Alphabet[(buffer << (5 - bits)) & 31]);

        return builder.ToString();
    }

    public static byte[] FromBase32(string value)
    {
        var buffer = 0;
        var bits = 0;
        var bytes = new List<byte>();

        foreach (var character in value.Where(char.IsLetterOrDigit))
        {
            var index = Base32Alphabet.IndexOf(char.ToUpperInvariant(character));

            if (index < 0) continue;

            buffer = (buffer << 5) | index;
            bits += 5;

            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}