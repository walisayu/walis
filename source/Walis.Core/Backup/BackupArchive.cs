using System.Formats.Tar;
using System.IO.Compression;

namespace Walis.Core.Backup;

/// <summary>Packs several sources into one tar.gz without copying them to a temporary directory first.</summary>
public sealed class BackupArchive(string path, SafeTarExtractionLimits? limits = null) : IAsyncDisposable
{
    private readonly HashSet<string> _entryNames = new(StringComparer.Ordinal);
    private readonly FileStream _file = PrivateFileSystem.CreatePrivateFile(path);
    private readonly SafeTarExtractionLimits _limits = ValidateLimits(limits);
    private int _entries;
    private GZipStream? _gzip;
    private long _totalBytes;
    private TarWriter? _writer;

    public string Path { get; } = path;

    private TarWriter Writer
    {
        get
        {
            _gzip ??= new GZipStream(_file, CompressionLevel.Optimal);
            return _writer ??= new TarWriter(_gzip, TarEntryFormat.Pax, true);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.DisposeAsync();

        if (_gzip is not null) await _gzip.DisposeAsync();

        await _file.DisposeAsync();
    }

    public async Task AddFileAsync(string sourcePath, string entryName, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) return;

        await AddRegularFileAsync(sourcePath, entryName, cancellationToken);
    }

    /// <summary>Adds a whole directory; symbolic-link directories are skipped to avoid looping back into itself.</summary>
    public async Task<int> AddDirectoryAsync(
        string sourceDirectory,
        string entryPrefix,
        CancellationToken cancellationToken = default,
        IReadOnlyCollection<string>? excludedDirectories = null,
        IReadOnlyCollection<string>? excludedFiles = null)
    {
        if (!Directory.Exists(sourceDirectory)) return 0;

        var root = System.IO.Path.GetFullPath(sourceDirectory);
        var excluded = (excludedDirectories ?? [])
            .Select(path => System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)))
            .ToArray();
        var count = 0;

        foreach (var file in EnumerateFiles(root, excluded))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (PathsEqual(file, _file.Name))
                // When the extra paths include the data directory, the archive being produced may sit under it too;
                // the output file must never be read back into itself.
                continue;

            if (excludedFiles is not null && excludedFiles.Any(excludedFile => PathsEqual(file, excludedFile))) continue;

            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            await AddRegularFileAsync(file, $"{entryPrefix}/{relative}", cancellationToken);
            count++;
        }

        return count;
    }

    private async Task AddRegularFileAsync(
        string sourcePath,
        string entryName,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(sourcePath);

        if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null)
            throw new InvalidDataException($"備份來源不可為符號連結或 reparse point：{sourcePath}");

        var normalized = SafeTarExtractor.NormalizeEntryName(
            entryName,
            TarEntryType.RegularFile,
            _limits.MaximumPathLength);

        if (!_entryNames.Add(normalized)) throw new InvalidDataException($"備份包含重複的封存路徑：{entryName}");

        if (++_entries > _limits.MaximumEntries)
            throw new InvalidDataException($"備份包含超過 {_limits.MaximumEntries} 個封存項目。");

        var length = info.Length;
        if (length < 0 || length > _limits.MaximumFileBytes)
            throw new InvalidDataException(
                $"備份來源 {sourcePath} 超過單檔 {_limits.MaximumFileBytes} bytes 的上限。");

        if (length > _limits.MaximumTotalBytes - _totalBytes)
            throw new InvalidDataException(
                $"備份來源合計超過 {_limits.MaximumTotalBytes} bytes 的上限。");

        _totalBytes += length;
        await Writer.WriteEntryAsync(sourcePath, normalized, cancellationToken);
    }

    private static SafeTarExtractionLimits ValidateLimits(SafeTarExtractionLimits? limits)
    {
        var result = limits ?? new SafeTarExtractionLimits();
        SafeTarExtractor.ValidateLimits(result);
        return result;
    }

    private static IEnumerable<string> EnumerateFiles(
        string directory,
        IReadOnlyCollection<string> excludedDirectories)
    {
        IEnumerable<string> entries;

        try
        {
            entries = Directory.EnumerateFileSystemEntries(directory);
        }
        catch (UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (var entry in entries)
        {
            var fullEntry = System.IO.Path.GetFullPath(entry);

            if (excludedDirectories.Any(excluded => IsInside(fullEntry, excluded))) continue;

            var info = new FileInfo(entry);

            if (info.LinkTarget is not null)
                // Symbolic links are not followed: the data entry in a release directory is a link back to the data directory.
                continue;

            if (info.Attributes.HasFlag(FileAttributes.Directory))
            {
                foreach (var nested in EnumerateFiles(entry, excludedDirectories)) yield return nested;

                continue;
            }

            yield return entry;
        }
    }

    private static bool IsInside(string path, string root)
    {
        return PathsEqual(path, root) ||
               path.StartsWith(
                   System.IO.Path.EndsInDirectorySeparator(root) ? root : root + System.IO.Path.DirectorySeparatorChar,
                   PathComparison);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(left)),
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(right)),
            PathComparison);
    }
}