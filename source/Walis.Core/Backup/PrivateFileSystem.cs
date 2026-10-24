using System.Security.Cryptography;

namespace Walis.Core.Backup;

/// <summary>
///     Creates temporary workspaces and files usable only by the Walis service account.
///     Backup staging contains the database, credentials and decrypted archives, so the system default umask cannot be relied on.
/// </summary>
public static class PrivateFileSystem
{
    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    ///     Creates an unpredictable private workspace inside the application's data directory.
    ///     Windows inherits the data directory ACL, but plaintext is still kept out of the machine-wide shared temp directory.
    /// </summary>
    public static string CreateTemporaryDirectory(string dataDirectory, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        if (purpose.Length > 32 || purpose.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            throw new ArgumentException("暫存工作區用途只能包含 ASCII 英數字、連字號與底線。", nameof(purpose));

        var privateRoot = Path.Combine(Path.GetFullPath(dataDirectory), ".private-temp");
        EnsurePrivateDirectory(privateRoot);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var name = $"{purpose}-{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";
            var path = Path.Combine(privateRoot, name);

            if (File.Exists(path) || Directory.Exists(path)) continue;

            EnsurePrivateDirectory(path);
            return path;
        }

        throw new IOException("無法建立不重複的私有暫存工作區。");
    }

    public static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, PrivateDirectoryMode);

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"私有工作區不可為符號連結或 reparse point：{path}");

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    public static FileStream CreatePrivateFile(
        string path,
        FileMode mode = FileMode.CreateNew,
        FileOptions options = FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        var streamOptions = new FileStreamOptions
        {
            Mode = mode,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 128 * 1024,
            Options = options
        };

        if (!OperatingSystem.IsWindows()) streamOptions.UnixCreateMode = PrivateFileMode;

        var stream = new FileStream(path, streamOptions);

        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, PrivateFileMode);

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public static void RestrictFile(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"私有檔案不可為符號連結或 reparse point：{path}");

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, PrivateFileMode);
    }
}