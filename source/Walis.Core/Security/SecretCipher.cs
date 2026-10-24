using System.Security.Cryptography;
using System.Text;

namespace Walis.Core.Security;

/// <summary>
///     Encryption at rest for secret strings. Ported from an internal shared security library, changed to a version-prefixed format.
///     AES-256-GCM. The master key is stretched once per process with PBKDF2-SHA512 (so a passphrase-style
///     <c>Security:EncryptionKey</c> still costs an attacker), and every value gets its own key from HKDF-SHA256 over
///     that stretched key plus a fresh random salt, which keeps per-value work cheap enough to decrypt a whole settings
///     payload or a list of environment variables on every read.
///     The current format is <c>enc:v2:</c> followed by Base64(salt[16] + nonce[12] + tag[16] + ciphertext), authenticated
///     with the storage purpose (<see cref="SecretPurposes" />, <c>walis:&lt;table&gt;.&lt;column&gt;</c>) as associated data,
///     so a ciphertext cannot be moved between columns. <c>enc:v1:</c> is the same layout without associated data; it is
///     still read and is rewritten as v2 on the next write. The prefix is what tells "this is ciphertext" apart from "this is
///     old data not yet encrypted", so an upgrade needs no one-shot migration.
/// </summary>
public sealed class SecretCipher
{
    /// <summary>The prefix of the current format (associated data bound to the purpose).</summary>
    public const string Prefix = "enc:v2:";

    /// <summary>The first format: same payload layout, no associated data. Read-only; never written any more.</summary>
    public const string LegacyPrefix = "enc:v1:";

    private const int KeyLength = 32;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int SaltLength = 16;
    private const int Iterations = 100_000;

    /// <summary>Fixed, domain-separated salt for the one-time master key stretch; the per-value salt provides the randomness.</summary>
    private static readonly byte[] MasterSalt = "walis:secret-cipher:v1"u8.ToArray();

    private readonly byte[] _stretchedKey;

    public SecretCipher(string masterKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(masterKey);

        _stretchedKey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(masterKey),
            MasterSalt,
            Iterations,
            HashAlgorithmName.SHA512,
            KeyLength);
    }

    /// <summary>Whether the value carries either ciphertext prefix.</summary>
    public static bool IsEncrypted(string? value)
    {
        return value is not null &&
               (value.StartsWith(Prefix, StringComparison.Ordinal) ||
                value.StartsWith(LegacyPrefix, StringComparison.Ordinal));
    }

    /// <summary>Whether the value is in the v1 format that is rewritten on the next write.</summary>
    public static bool IsLegacyFormat(string? value)
    {
        return value is not null && value.StartsWith(LegacyPrefix, StringComparison.Ordinal);
    }

    /// <summary>Encrypts for the given storage purpose; the purpose is bound as associated data and must be repeated to decrypt.</summary>
    public string Encrypt(string? plaintext, string purpose)
    {
        var associatedData = SecretPurposes.AssociatedData(purpose);

        if (string.IsNullOrEmpty(plaintext)) return string.Empty;

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        var nonce = RandomNumberGenerator.GetBytes(NonceLength);
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var cipherText = new byte[bytes.Length];
        var tag = new byte[TagLength];

        using (var aes = new AesGcm(DeriveKey(salt), TagLength))
        {
            aes.Encrypt(nonce, bytes, cipherText, tag, associatedData);
        }

        var payload = new byte[SaltLength + NonceLength + TagLength + cipherText.Length];
        Buffer.BlockCopy(salt, 0, payload, 0, SaltLength);
        Buffer.BlockCopy(nonce, 0, payload, SaltLength, NonceLength);
        Buffer.BlockCopy(tag, 0, payload, SaltLength + NonceLength, TagLength);
        Buffer.BlockCopy(cipherText, 0, payload, SaltLength + NonceLength + TagLength, cipherText.Length);

        return Prefix + Convert.ToBase64String(payload);
    }

    /// <summary>
    ///     Decrypts. A value without the prefix is plaintext left from before encryption and is returned as is;
    ///     a value with the prefix that cannot be decrypted means the key changed, the data was tampered with, or the ciphertext
    ///     was written for another column, and the caller must know.
    /// </summary>
    public string Decrypt(string? value, string purpose)
    {
        var associatedData = SecretPurposes.AssociatedData(purpose);

        if (string.IsNullOrEmpty(value)) return string.Empty;

        if (!IsEncrypted(value)) return value;

        var legacy = IsLegacyFormat(value);
        var prefixLength = legacy ? LegacyPrefix.Length : Prefix.Length;
        byte[] payload;

        try
        {
            payload = Convert.FromBase64String(value[prefixLength..]);
        }
        catch (FormatException exception)
        {
            throw new CryptographicException("密文不是有效的 Base64，格式無效。", exception);
        }

        if (payload.Length < SaltLength + NonceLength + TagLength) throw new CryptographicException("密文長度不足，格式無效。");

        var salt = payload.AsSpan(0, SaltLength).ToArray();
        var nonce = payload.AsSpan(SaltLength, NonceLength);
        var tag = payload.AsSpan(SaltLength + NonceLength, TagLength);
        var cipherText = payload.AsSpan(SaltLength + NonceLength + TagLength);
        var plaintext = new byte[cipherText.Length];

        using var aes = new AesGcm(DeriveKey(salt), TagLength);

        // v1 was written without associated data; it is accepted so an upgrade needs no migration, and the next write makes it v2.
        if (legacy)
            aes.Decrypt(nonce, cipherText, tag, plaintext);
        else
            aes.Decrypt(nonce, cipherText, tag, plaintext, associatedData);

        return Encoding.UTF8.GetString(plaintext);
    }

    /// <summary>Returns false instead of throwing when decryption fails; useful when reading a whole batch of settings.</summary>
    public bool TryDecrypt(string? value, string purpose, out string plaintext)
    {
        try
        {
            plaintext = Decrypt(value, purpose);
            return true;
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            plaintext = string.Empty;
            return false;
        }
    }

    private byte[] DeriveKey(byte[] salt)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, _stretchedKey, KeyLength, salt, "walis:secret-value"u8.ToArray());
    }
}
