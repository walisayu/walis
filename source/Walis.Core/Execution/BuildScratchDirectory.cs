using System.Security;
using System.Security.Cryptography;

namespace Walis.Core.Execution;

/// <summary>
///     The private directory one build keeps its step files in (<c>$WALIS_ENV_FILE</c>, <c>$WALIS_OUTPUT_FILE</c>). It lives at
///     <c>&lt;workspace&gt;/.walis/&lt;random&gt;</c> rather than in the shared temp directory: every build runs as the same account,
///     and the macOS sandbox does not deny TMPDIR, so a concurrent build of another job could read the plaintext values there.
///     The workspace is denied to other builds whenever it sits under a denied path (home, data directory) and is re-allowed
///     for its own build only. Removed when the build is over; the pre-build clean removes a stale one.
/// </summary>
public sealed class BuildScratchDirectory : IDisposable
{
    public const string DirectoryName = ".walis";

    private const UnixFileMode PrivateMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private bool _disposed;

    private BuildScratchDirectory(string path)
    {
        Path = path;
    }

    public string Path { get; }

    /// <summary>Whether <paramref name="name" /> is the scratch root; cache, artifact and other workspace scans skip it.</summary>
    public static bool IsScratchName(string? name)
    {
        return string.Equals(name, DirectoryName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Creates <c>&lt;workspace&gt;/.walis/&lt;random&gt;</c>, both levels private (0700) and neither a link: a build script
    ///     from an earlier run could have left a symbolic link in its place so that the next build writes its step files elsewhere.
    /// </summary>
    public static BuildScratchDirectory Create(string workspace)
    {
        var root = System.IO.Path.Combine(workspace, DirectoryName);
        EnsurePrivate(root);
        WriteGitIgnore(root);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = System.IO.Path.Combine(
                root,
                $"build-{Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant()}");

            if (Directory.Exists(path) || File.Exists(path)) continue;

            EnsurePrivate(path);
            return new BuildScratchDirectory(path);
        }

        throw new IOException("無法建立建置專用的暫存目錄。");
    }

    /// <summary>
    ///     Recreates the directory when a step removed it (<c>git clean -fdx</c> in the workspace does); called before every step.
    /// </summary>
    public void EnsureExists()
    {
        if (Directory.Exists(Path)) return;

        var root = System.IO.Path.GetDirectoryName(Path)!;
        EnsurePrivate(root);
        WriteGitIgnore(root);
        EnsurePrivate(Path);
    }

    /// <summary>
    ///     Keeps the scratch directory out of <c>git status</c> and <c>git add -A</c> in the workspace. Created with O_EXCL
    ///     (<see cref="FileMode.CreateNew" />), which never follows a link a script may have planted under that name.
    /// </summary>
    private static void WriteGitIgnore(string root)
    {
        try
        {
            using var stream = new FileStream(System.IO.Path.Combine(root, ".gitignore"), FileMode.CreateNew, FileAccess.Write);
            stream.Write("*\n"u8);
        }
        catch (IOException)
        {
            // Already there (or something else is): either way nothing is written through it.
        }
    }

    private static void EnsurePrivate(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, PrivateMode);

        var info = new DirectoryInfo(path);
        info.Refresh();

        if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"建置暫存目錄不可為符號連結或 reparse point：{path}");

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, PrivateMode);
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;

        try
        {
            // A link planted by the script in place of a file is deleted as a link; the recursive delete never follows links out.
            if (Directory.Exists(Path)) Directory.Delete(Path, true);

            var root = System.IO.Path.GetDirectoryName(Path);

            if (root is not null && Directory.Exists(root) && !Directory.EnumerateFileSystemEntries(root).Any())
                Directory.Delete(root, false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // A failed cleanup does not change the build result; the next build's clean or Create removes the leftovers.
        }
    }
}
