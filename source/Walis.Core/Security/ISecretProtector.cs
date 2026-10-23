namespace Walis.Core.Security;

/// <summary>
///     Encryption at rest for the secrets Walis stores (AI keys, secret environment variables, settings passwords, password-type
///     build parameters, TOTP secrets, Slack and webhook notification targets). Repositories call it at the storage boundary, so
///     the API and services keep working with plaintext in memory. Values are only ever ciphertext in the database once written
///     through it; plaintext left by an older version is returned as is, so no migration is needed and the next write turns it
///     into ciphertext. Every call names the storage purpose (<see cref="SecretPurposes" />, the table and column): it is bound
///     into the ciphertext, so a value encrypted for one column does not decrypt in another.
/// </summary>
public interface ISecretProtector
{
    /// <summary>Whether the stored value is ciphertext (carries a <see cref="SecretCipher" /> prefix).</summary>
    bool IsProtected(string? value);

    /// <summary>
    ///     Encrypts for storage under the given purpose. Null and empty pass through; a value that is already readable ciphertext
    ///     for this purpose is returned unchanged (or rewritten in the current format when it is in the old one). Anything else,
    ///     including a plaintext that only looks like ciphertext, is encrypted.
    /// </summary>
    string? Protect(string? value, string purpose);

    /// <summary>
    ///     Decrypts after reading. Null, empty and legacy plaintext pass through. A value that carries the prefix but cannot be
    ///     decrypted (the key changed, a backup was restored on a machine without its <c>secret.key</c>, or the ciphertext was
    ///     written for another column) yields null, so the caller sees "unset" instead of feeding ciphertext to a third-party API;
    ///     the startup check and the reset endpoint report it.
    /// </summary>
    string? Unprotect(string? value, string purpose);

    /// <summary>Like <see cref="Unprotect" /> but says whether the value could be read; used by the integrity scan.</summary>
    bool TryUnprotect(string? value, string purpose, out string? plaintext);
}
