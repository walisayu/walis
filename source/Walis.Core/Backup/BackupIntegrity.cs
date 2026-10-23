using System.Security.Cryptography;

namespace Walis.Core.Backup;

/// <summary>
///     A quick corruption checksum for backup files. Cryptographic integrity comes from BackupCipher's AES-GCM;
///     the sidecar uses the <c>shasum -a 256</c> format so ordinary file tools can check for transfer damage first.
/// </summary>
public static class BackupIntegrity
{
    public const string Suffix = ".sha256";

    public static bool IsSidecar(string fileName)
    {
        return fileName.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
    }

    public static string SidecarPath(string archivePath)
    {
        return archivePath + Suffix;
    }

    public static async Task<string> WriteAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var hash = await HashAsync(archivePath, cancellationToken);
        var sidecar = SidecarPath(archivePath);
        var temporary = $"{sidecar}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(8))}.tmp";

        try
        {
            await using (var stream = PrivateFileSystem.CreatePrivateFile(
                             temporary,
                             options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync($"{hash}  {Path.GetFileName(archivePath)}\n".AsMemory(), cancellationToken);
                await writer.FlushAsync(cancellationToken);
            }

            File.Move(temporary, sidecar, true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }

        return hash;
    }

    /// <summary>Returns null when there is no checksum file (old backups), otherwise the comparison result.</summary>
    public static async Task<bool?> VerifyAsync(string archivePath, CancellationToken cancellationToken = default)
    {
        var sidecar = SidecarPath(archivePath);

        if (!File.Exists(sidecar)) return null;

        if ((File.GetAttributes(sidecar) & FileAttributes.ReparsePoint) != 0) return false;

        var info = new FileInfo(sidecar);

        if (info.Length is <= 0 or > 1024) return false;

        string contents;
        await using (var file = new FileStream(
                         sidecar,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        using (var reader = new StreamReader(file))
        {
            contents = await reader.ReadToEndAsync(cancellationToken);
        }

        var recorded = contents
            .Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;

        if (recorded.Length != 64) return false;

        try
        {
            var expected = Convert.FromHexString(recorded);
            await using var stream = File.OpenRead(archivePath);
            var actual = await SHA256.HashDataAsync(stream, cancellationToken);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static async Task<string> HashAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }
}