namespace Walis.Core.Execution;

public sealed record CollectedArtifact(string RelativePath, long SizeBytes);

/// <summary>
///     Copies files from the workspace into the build-specific artifact directory according to the job's artifact paths.
///     An artifact path can be a directory (copied whole) or a file name with wildcards; paths must stay inside the workspace.
///     This runs inside the API process, outside the build sandbox, so symbolic links are never followed:
///     otherwise a script could <c>ln -s &lt;Walis data directory&gt; out</c> and the database would be copied out as an artifact.
/// </summary>
public static class ArtifactCollector
{
    public const int MaximumFiles = 200;
    public const long MaximumTotalBytes = 64L * 1024 * 1024;
    private const int MaximumCandidates = MaximumFiles * 10;

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public static IReadOnlyList<CollectedArtifact> Collect(
        string workingDirectory,
        string? artifactPath,
        string destination,
        out string report,
        IReadOnlyList<string>? protectedPaths = null)
    {
        report = string.Empty;

        if (string.IsNullOrWhiteSpace(artifactPath)) return [];

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        var target = Path.GetFullPath(Path.Combine(root, artifactPath));
        var fullDestination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));

        // The collector runs outside the sandbox as the service/runner identity; if the source workspace contains protected
        // paths such as the database, cache or artifact destination, a script could pick them as an ArtifactPath and have the host read them.
        if (BuildIsolation.PathsOverlap(root, fullDestination) ||
            !BuildIsolation.IsDisjointFrom(root, protectedPaths ?? []))
        {
            report = "工作目錄與產出保存位置或受保護儲存路徑重疊，已略過產出收集。";
            return [];
        }

        try
        {
            var rootInfo = new DirectoryInfo(root);
            rootInfo.Refresh();

            if (!rootInfo.Exists ||
                rootInfo.LinkTarget is not null ||
                (rootInfo.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                report = "工作目錄不存在或已被替換成符號連結，已略過產出收集。";
                return [];
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            report = "無法安全確認工作目錄，已略過產出收集。";
            return [];
        }

        if (!IsInside(target, root))
        {
            report = "產出路徑超出工作目錄，已略過。";
            return [];
        }

        // Being inside the workspace literally is not enough; it must still be inside after resolving symbolic links.
        if (BuildIsolation.ContainsLink(root, target) ||
            !IsInside(BuildIsolation.RealPath(target), BuildIsolation.RealPath(root)))
        {
            report = "產出路徑經符號連結指向工作目錄之外，已略過。";
            return [];
        }

        var sources = Resolve(target, root, out var baseDirectory);
        using var enumerator = sources.GetEnumerator();

        if (!enumerator.MoveNext())
        {
            report = $"找不到符合「{artifactPath}」的產出檔案。";
            return [];
        }

        EnsurePrivateDirectory(fullDestination);

        var collected = new List<CollectedArtifact>();
        var totalBytes = 0L;
        var skipped = 0;
        var candidates = 0;

        do
        {
            var source = enumerator.Current;
            candidates++;

            if (collected.Count >= MaximumFiles ||
                totalBytes >= MaximumTotalBytes ||
                candidates > MaximumCandidates)
            {
                skipped++;
                break;
            }

            var remainingBytes = MaximumTotalBytes - totalBytes;

            if (!IsInside(BuildIsolation.RealPath(source), BuildIsolation.RealPath(root)))
            {
                skipped++;
                continue;
            }

            // The build's scratch directory (step env/output files) is gone by the time artifacts are collected; should a file
            // linger there anyway, it is not an artifact.
            if (IsUnderScratchDirectory(source, root)) continue;

            var relative = Path.GetRelativePath(baseDirectory, source).Replace('\\', '/');
            var copy = Path.Combine(fullDestination, relative.Replace('/', Path.DirectorySeparatorChar));

            EnsurePrivateDirectory(Path.GetDirectoryName(copy)!);

            if (!TryCopyBounded(source, copy, remainingBytes, out var size))
            {
                skipped++;
                continue;
            }

            totalBytes += size;
            collected.Add(new CollectedArtifact(relative, size));
        } while (enumerator.MoveNext());

        report = skipped == 0
            ? $"已保存 {collected.Count} 個產出檔案（{FormatSize(totalBytes)}）。"
            : $"已保存 {collected.Count} 個產出檔案（{FormatSize(totalBytes)}），另有 {skipped} 個因超過上限未保存。";

        return collected;
    }

    private static bool IsInside(string path, string root)
    {
        return string.Equals(path, root, PathComparison) ||
               path.StartsWith(WithTrailingSeparator(root), PathComparison);
    }

    private static bool IsUnderScratchDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        var first = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2)[0];
        return BuildScratchDirectory.IsScratchName(first);
    }

    private static string WithTrailingSeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }

    /// <summary>Skips every symbolic link (files and directories alike) while enumerating and never follows them.</summary>
    private static EnumerationOptions Options(bool recursive)
    {
        return new EnumerationOptions
        {
            RecurseSubdirectories = recursive,
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = true
        };
    }

    private static IEnumerable<string> Resolve(string target, string root, out string baseDirectory)
    {
        if (Directory.Exists(target))
        {
            baseDirectory = target;
            return Directory.EnumerateFiles(target, "*", Options(true));
        }

        var directory = Path.GetDirectoryName(target);
        var pattern = Path.GetFileName(target);

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory) || string.IsNullOrEmpty(pattern))
        {
            baseDirectory = root;
            return Enumerable.Empty<string>();
        }

        baseDirectory = directory;
        return Directory.EnumerateFiles(directory, pattern, Options(false));
    }

    private static string FormatSize(long bytes)
    {
        return bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
            _ => $"{bytes / (1024.0 * 1024.0):0.#} MB"
        };
    }

    /// <summary>
    ///     The source may keep growing after the pre-check, so the copy loop itself must enforce a byte budget.
    ///     Write to a random temporary file in the same directory first and atomically replace the real artifact only on full success; no partial file is left on failure.
    /// </summary>
    private static bool TryCopyBounded(string source, string destination, long maximumBytes, out long copied)
    {
        copied = 0;
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".walis-artifact-{Guid.NewGuid():N}.tmp");

        try
        {
            // Background processes left by the build can still swap the pathname for a symlink/FIFO/device after the pre-check.
            // The open itself must be O_NOFOLLOW, and the regular-file type must be verified on the opened handle.
            if (!SafeRegularFile.TryOpenRead(source, out var input)) return false;

            using (input)
            {
                if (input.Length > maximumBytes) return false;

                var outputOptions = new FileStreamOptions
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.SequentialScan
                };

                if (!OperatingSystem.IsWindows()) outputOptions.UnixCreateMode = PrivateFileMode;

                using (var output = new FileStream(temporary, outputOptions))
                {
                    var buffer = new byte[64 * 1024];

                    while (true)
                    {
                        var count = input.Read(buffer, 0, (int)Math.Min(buffer.Length, maximumBytes - copied + 1));

                        if (count == 0) break;

                        if (copied + count > maximumBytes) return false;

                        output.Write(buffer, 0, count);
                        copied += count;
                    }

                    output.Flush(true);
                }
            }

            File.Move(temporary, destination, true);

            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(destination, PrivateFileMode);

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort cleanup of the temporary file; never let a cleanup failure override the original collection result.
            }
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(path, PrivateDirectoryMode);

        var info = new DirectoryInfo(path);
        info.Refresh();

        if (!info.Exists || info.LinkTarget is not null ||
            (info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("產出保存目錄不可為符號連結或 reparse point。");

        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, PrivateDirectoryMode);
    }
}