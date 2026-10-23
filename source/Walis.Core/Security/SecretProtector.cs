namespace Walis.Core.Security;

/// <summary>The production <see cref="ISecretProtector" />, backed by <see cref="SecretCipher" /> and one master key per process.</summary>
public sealed class SecretProtector(SecretCipher cipher) : ISecretProtector
{
    public SecretProtector(string masterKey) : this(new SecretCipher(masterKey))
    {
    }

    public bool IsProtected(string? value)
    {
        return SecretCipher.IsEncrypted(value);
    }

    /// <summary>
    ///     A value is only treated as "already ciphertext" when it actually decrypts for this purpose with the current key: a
    ///     plaintext that merely starts with the prefix (pasted by a user, or forged) is encrypted like any other value instead
    ///     of being stored as is and reading back as null later. Readable v1 ciphertext is rewritten in the v2 format.
    /// </summary>
    public string? Protect(string? value, string purpose)
    {
        if (string.IsNullOrEmpty(value)) return value;

        if (SecretCipher.IsEncrypted(value) && cipher.TryDecrypt(value, purpose, out var plaintext))
            return SecretCipher.IsLegacyFormat(value) ? cipher.Encrypt(plaintext, purpose) : value;

        return cipher.Encrypt(value, purpose);
    }

    public string? Unprotect(string? value, string purpose)
    {
        return TryUnprotect(value, purpose, out var plaintext) ? plaintext : null;
    }

    public bool TryUnprotect(string? value, string purpose, out string? plaintext)
    {
        if (string.IsNullOrEmpty(value) || !SecretCipher.IsEncrypted(value))
        {
            plaintext = value;
            return true;
        }

        if (cipher.TryDecrypt(value, purpose, out var decrypted))
        {
            plaintext = decrypted;
            return true;
        }

        plaintext = null;
        return false;
    }
}
