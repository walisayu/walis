using System.Security.Cryptography;
using System.Text;
using Walis.Core.Backup;

namespace Walis.Core.Security;

/// <summary>Where the master key came from; carries no key material, so it is safe to register in DI and show to admins.</summary>
public sealed record SecretKeySource(string Description, string? FilePath, bool Generated);

/// <summary>The master key and where it came from. Only <see cref="SecretKeyStore" /> hands this out; keep it off the DI container.</summary>
public sealed record SecretKey(string Value, SecretKeySource Source);

/// <summary>
///     Resolves the master key for <see cref="SecretCipher" />. A configured <c>Security:EncryptionKey</c> wins (that is how a
///     multi-node cluster and a restore on another machine share one key); otherwise a random 32-byte key is generated on first
///     start into <c>&lt;data directory&gt;/secret.key</c> with owner-only permissions and reused from then on.
/// </summary>
public static class SecretKeyStore
{
    public const string ConfigurationKey = "Security:EncryptionKey";
    public const string FileName = "secret.key";
    public const int MinimumConfiguredLength = 32;
    public const int MaximumConfiguredLength = 1024;

    public static SecretKey LoadOrCreate(string? configuredKey, string dataDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredKey))
        {
            var key = configuredKey.Trim();

            if (key.Length is < MinimumConfiguredLength or > MaximumConfiguredLength)
                throw new InvalidOperationException(
                    $"{ConfigurationKey} 必須是 {MinimumConfiguredLength} 到 {MaximumConfiguredLength} 個字元；" +
                    "請用 openssl rand -base64 32 這類方式產生，或留空讓 Walis 在資料目錄產生 secret.key。");

            return new SecretKey(key, new SecretKeySource($"設定 {ConfigurationKey}", null, false));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var path = Path.Combine(Path.GetFullPath(dataDirectory), FileName);

        if (File.Exists(path))
        {
            // Never follow a link: a build or another account could point the name at a file it controls.
            PrivateFileSystem.RestrictFile(path);
            var existing = File.ReadAllText(path, Encoding.UTF8).Trim();

            if (existing.Length < MinimumConfiguredLength)
                throw new InvalidOperationException($"{path} 的內容不是有效的機密加密金鑰；請還原原本的檔案，或刪除它讓 Walis 重新產生（既有的機密將無法解密）。");

            return new SecretKey(existing, new SecretKeySource($"資料目錄 {FileName}", path, false));
        }

        PrivateFileSystem.EnsurePrivateDirectory(Path.GetDirectoryName(path)!);
        var generated = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        try
        {
            using var stream = PrivateFileSystem.CreatePrivateFile(path, FileMode.CreateNew, FileOptions.None);
            stream.Write(Encoding.UTF8.GetBytes(generated + Environment.NewLine));
        }
        catch (IOException) when (File.Exists(path))
        {
            // Another node of the same data directory, or a second process, won the race: use what it wrote.
            PrivateFileSystem.RestrictFile(path);
            var written = File.ReadAllText(path, Encoding.UTF8).Trim();
            return new SecretKey(written, new SecretKeySource($"資料目錄 {FileName}", path, false));
        }

        return new SecretKey(generated, new SecretKeySource($"資料目錄 {FileName}", path, true));
    }
}
