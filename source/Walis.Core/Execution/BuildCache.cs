using System.Buffers;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Walis.Core.Execution;

public sealed record BuildCacheQuota(
    int MaximumFiles,
    long MaximumTotalBytes,
    long MaximumSingleFileBytes,
    int MaximumEntries);

/// <summary>
///     Directories kept between builds. Every copy is first written into a private temporary tree on the same file system and
///     committed with a rename only after passing the quota and file-type checks; a partial restore is never treated as a valid cache.
/// </summary>
public static class BuildCache
{
    public const int MaximumPaths = 32;
    public const int MaximumFiles = 20_000;
    public const int MaximumEntries = 40_000;
    public const long MaximumSingleFileBytes = 128L * 1024 * 1024;
    public const long MaximumTotalBytes = 512L * 1024 * 1024;

    public static BuildCacheQuota DefaultQuota { get; } = new(
        MaximumFiles,
        MaximumTotalBytes,
        MaximumSingleFileBytes,
        MaximumEntries);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;

    /// <summary>Normalizes the path list, removes duplicates and caps the count.</summary>
    public static IReadOnlyList<string> ParsePaths(string? value)
    {
        var paths = new HashSet<string>(PathComparer);

        foreach (var line in (value ?? string.Empty)
                 .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = line.TrimEnd('/', '\\');

            if (line.StartsWith('#') || !IsSafe(normalized)) continue;

            // The build's own scratch directory holds step files for the duration of a build and is never cache material.
            if (BuildScratchDirectory.IsScratchName(normalized.Split('/', '\\')[0])) continue;

            paths.Add(normalized.Replace('\\', '/'));

            if (paths.Count >= MaximumPaths) break;
        }

        return [.. paths];
    }

    /// <summary>Accepts only non-empty relative paths under the workspace.</summary>
    public static bool IsSafe(string path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               !Path.IsPathRooted(path) &&
               !path.StartsWith('~') &&
               path.Split('/', '\\').All(segment => segment is not ("" or "." or ".."));
    }

    public static int Restore(
        string cacheRoot,
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default,
        BuildCacheQuota? quota = null)
    {
        quota ??= DefaultQuota;

        if (!IsValidQuota(quota)) return 0;

        var restored = 0;
        var budget = new QuotaTracker(quota);
        var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        var literalCacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        var realWorkspaceRoot = Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(workspaceRoot));
        var fullCacheRoot = Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(literalCacheRoot));

        if (IsDirectoryLink(workspaceRoot) ||
            IsDirectoryLink(literalCacheRoot) ||
            !Directory.Exists(realWorkspaceRoot) ||
            !Directory.Exists(fullCacheRoot) ||
            BuildIsolation.ContainsLink(realWorkspaceRoot, realWorkspaceRoot) ||
            BuildIsolation.ContainsLink(fullCacheRoot, fullCacheRoot) ||
            BuildIsolation.PathsOverlap(realWorkspaceRoot, fullCacheRoot))
            return 0;

        foreach (var path in paths.Take(MaximumPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSafe(path)) continue;

            var normalizedPath = NormalizeRelativePath(path);
            var source = Path.GetFullPath(Path.Combine(fullCacheRoot, CacheKey(normalizedPath)));
            var destination = Path.GetFullPath(Path.Combine(realWorkspaceRoot, normalizedPath));

            if (!IsInside(destination, realWorkspaceRoot) ||
                !IsInside(source, fullCacheRoot) ||
                !Directory.Exists(source) ||
                File.Exists(destination) ||
                Directory.Exists(destination) ||
                BuildIsolation.ContainsLink(realWorkspaceRoot, destination) ||
                BuildIsolation.ContainsLink(fullCacheRoot, source))
                continue;

            var temporary = string.Empty;

            try
            {
                temporary = CreatePrivateTemporaryDirectory(realWorkspaceRoot, ".walis-cache-restore");

                if (!TryCopyTree(source, temporary, budget, cancellationToken)) continue;

                var parent = Path.GetDirectoryName(destination)!;
                Directory.CreateDirectory(parent);

                // Re-validate between CreateDirectory and commit so the restore is never renamed into a link target.
                if (BuildIsolation.ContainsLink(realWorkspaceRoot, parent) ||
                    File.Exists(destination) ||
                    Directory.Exists(destination))
                    continue;

                Directory.Move(temporary, destination);
                temporary = string.Empty;
                restored++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // A cache failure is a cache miss; never let a partial tree enter the workspace.
            }
            finally
            {
                TryDeleteTree(temporary);
            }
        }

        return restored;
    }

    public static int Save(
        string cacheRoot,
        string workingDirectory,
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken = default,
        BuildCacheQuota? quota = null)
    {
        quota ??= DefaultQuota;

        if (!IsValidQuota(quota)) return 0;

        var saved = 0;
        var budget = new QuotaTracker(quota);
        var workspaceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        var literalCacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
        var realWorkspaceRoot = Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(workspaceRoot));
        var fullCacheRoot = Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(literalCacheRoot));

        if (IsDirectoryLink(workspaceRoot) ||
            (Directory.Exists(literalCacheRoot) && IsDirectoryLink(literalCacheRoot)) ||
            !Directory.Exists(realWorkspaceRoot) ||
            BuildIsolation.ContainsLink(realWorkspaceRoot, realWorkspaceRoot) ||
            BuildIsolation.PathsOverlap(realWorkspaceRoot, fullCacheRoot) ||
            (Directory.Exists(fullCacheRoot) && BuildIsolation.ContainsLink(fullCacheRoot, fullCacheRoot)))
            return 0;

        try
        {
            CreatePrivateDirectory(fullCacheRoot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return 0;
        }

        foreach (var path in paths.Take(MaximumPaths))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsSafe(path)) continue;

            var normalizedPath = NormalizeRelativePath(path);
            var source = Path.GetFullPath(Path.Combine(realWorkspaceRoot, normalizedPath));
            var destination = Path.GetFullPath(Path.Combine(fullCacheRoot, CacheKey(normalizedPath)));

            if (!IsInside(source, realWorkspaceRoot) ||
                !IsInside(destination, fullCacheRoot) ||
                !Directory.Exists(source) ||
                !IsRealDirectoryInside(source, realWorkspaceRoot) ||
                BuildIsolation.ContainsLink(realWorkspaceRoot, source) ||
                BuildIsolation.ContainsLink(fullCacheRoot, destination))
                continue;

            var temporary = string.Empty;

            try
            {
                temporary = CreatePrivateTemporaryDirectory(fullCacheRoot, ".walis-cache-save");

                if (!TryCopyTree(source, temporary, budget, cancellationToken)) continue;

                CommitCacheTree(temporary, destination);
                temporary = string.Empty;
                saved++;
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                // An existing old cache is restored by CommitCacheTree; the new partial tree is never published.
            }
            finally
            {
                TryDeleteTree(temporary);
            }
        }

        return saved;
    }

    /// <summary>Human-readable prefix plus the full SHA-256; foo/bar and foo_bar no longer share a cache.</summary>
    public static string Sanitize(string path)
    {
        return CacheKey(path);
    }

    public static string CacheKey(string path)
    {
        var normalized = NormalizeRelativePath(path).Replace(Path.DirectorySeparatorChar, '/');

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) normalized = normalized.ToUpperInvariant();

        var prefix = string.Join('_', normalized.Split('/'));
        prefix = new string(prefix
            .Take(64)
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'
                ? character
                : '_')
            .ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
        return $"{prefix}--{hash}";
    }

    private static string NormalizeRelativePath(string path)
    {
        return string.Join(
            Path.DirectorySeparatorChar,
            path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool IsValidQuota(BuildCacheQuota quota)
    {
        return quota.MaximumFiles > 0 &&
               quota.MaximumTotalBytes > 0 &&
               quota.MaximumSingleFileBytes > 0 &&
               quota.MaximumEntries > 0;
    }

    private static bool TryCopyTree(
        string source,
        string destination,
        QuotaTracker budget,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<string>();
        pending.Enqueue(source);

        while (pending.Count != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = pending.Dequeue();

            foreach (var entry in Directory.EnumerateFileSystemEntries(current))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!budget.TryAddEntry()) return false;

                FileAttributes attributes;

                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or SecurityException)
                {
                    return false;
                }

                // Any link/reparse node or node explicitly marked as a device makes the whole path a cache miss.
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return false;

                var relative = Path.GetRelativePath(source, entry);
                var target = Path.Combine(destination, relative);

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (IsDirectoryLink(entry)) return false;

                    CreatePrivateDirectory(target);
                    pending.Enqueue(entry);
                    continue;
                }

                if (!TryCopyRegularFile(entry, target, budget, cancellationToken)) return false;
            }
        }

        return true;
    }

    private static bool TryCopyRegularFile(
        string source,
        string destination,
        QuotaTracker budget,
        CancellationToken cancellationToken)
    {
        if (!SafeRegularFile.TryOpenRead(source, out var input)) return false;

        using (input)
        {
            long length;

            try
            {
                length = input.Length;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (!budget.CanAddFile(length)) return false;

            CreatePrivateDirectory(Path.GetDirectoryName(destination)!);
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.SequentialScan
            };

            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using var output = new FileStream(destination, options);
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            var copied = 0L;
            var remaining = Math.Min(
                budget.RemainingTotalBytes,
                budget.Quota.MaximumSingleFileBytes);

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var requested = (int)Math.Min(buffer.Length, remaining - copied + 1);
                    var count = input.Read(buffer, 0, requested);

                    if (count == 0) break;

                    copied += count;

                    if (copied > remaining) return false;

                    output.Write(buffer, 0, count);
                }

                if (copied != length || !budget.TryAddFile(copied)) return false;

                output.Flush(true);
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private static void CommitCacheTree(string temporary, string destination)
    {
        string? previous = null;

        if (Directory.Exists(destination))
        {
            previous = $"{destination}.previous-{Guid.NewGuid():N}";
            Directory.Move(destination, previous);
        }

        try
        {
            Directory.Move(temporary, destination);
        }
        catch
        {
            if (previous is not null &&
                Directory.Exists(previous) &&
                !Directory.Exists(destination))
                Directory.Move(previous, destination);

            throw;
        }

        TryDeleteTree(previous);
    }

    private static string CreatePrivateTemporaryDirectory(string parent, string prefix)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var path = Path.Combine(parent, $"{prefix}-{Guid.NewGuid():N}");

            if (Directory.Exists(path) || File.Exists(path)) continue;

            CreatePrivateDirectory(path);
            return path;
        }

        throw new IOException("無法建立私有 cache 暫存目錄。");
    }

    /// <summary>
    ///     Creates the directory private to this account, or takes over an existing one: its mode is reset to 0700, a link or
    ///     reparse point in its place is refused, and so is a directory owned by someone else. .NET exposes no portable owner
    ///     lookup, so ownership is established by the chmod itself: only the owner (or root) may change a directory's mode, and
    ///     a pre-planted directory of another account makes the call fail. A runner host whose cache root was created by another
    ///     local user therefore never uses it.
    /// </summary>
    internal static void CreatePrivateDirectory(string path)
    {
        Backup.PrivateFileSystem.EnsurePrivateDirectory(path);
    }

    private static void TryDeleteTree(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;

        try
        {
            Directory.Delete(path, true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Temporary cleanup is best effort; random names are never treated by restore as a real cache key.
        }
    }

    private static bool IsRealDirectoryInside(string directory, string workingDirectory)
    {
        if (IsDirectoryLink(directory)) return false;

        var real = BuildIsolation.RealPath(Path.GetFullPath(directory));
        var root = BuildIsolation.RealPath(Path.GetFullPath(workingDirectory));
        return IsInside(real, root);
    }

    private static bool IsInside(string path, string root)
    {
        return string.Equals(path, root, PathComparison) ||
               path.StartsWith(WithTrailingSeparator(root), PathComparison);
    }

    private static string WithTrailingSeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }

    private static bool IsDirectoryLink(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            info.Refresh();
            return info.LinkTarget is not null ||
                   (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return true;
        }
    }

    private sealed class QuotaTracker(BuildCacheQuota quota)
    {
        private int _entries;
        private int _files;
        private long _totalBytes;

        public BuildCacheQuota Quota => quota;
        public long RemainingTotalBytes => Math.Max(0, quota.MaximumTotalBytes - _totalBytes);

        public bool TryAddEntry()
        {
            return ++_entries <= quota.MaximumEntries;
        }

        public bool CanAddFile(long bytes)
        {
            return bytes >= 0 &&
                   _files < quota.MaximumFiles &&
                   bytes <= quota.MaximumSingleFileBytes &&
                   bytes <= RemainingTotalBytes;
        }

        public bool TryAddFile(long bytes)
        {
            if (!CanAddFile(bytes)) return false;

            _files++;
            _totalBytes += bytes;
            return true;
        }
    }
}