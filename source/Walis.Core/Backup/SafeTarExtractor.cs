using System.Formats.Tar;
using System.Security.Cryptography;

namespace Walis.Core.Backup;

public sealed record SafeTarExtractionLimits(
    int MaximumEntries = 100_000,
    long MaximumFileBytes = 4L * 1024 * 1024 * 1024,
    long MaximumTotalBytes = 8L * 1024 * 1024 * 1024,
    int MaximumPathLength = 1024,
    long MaximumArchiveBytes = 9L * 1024 * 1024 * 1024);

/// <summary>
///     Extracts only the regular files the Walis restore flow uses. Rejects links, FIFOs, devices and other special entries,
///     and checks paths and extraction limits before writing to disk.
/// </summary>
public static class SafeTarExtractor
{
    public static async Task ExtractForRestoreAsync(
        Stream source,
        string destinationDirectory,
        bool includeArtifacts,
        SafeTarExtractionLimits? limits = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        limits ??= new SafeTarExtractionLimits();
        ValidateLimits(limits);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDirectory));
        PrivateFileSystem.EnsurePrivateDirectory(root);
        var rootPrefix = root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var seen = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

        var entries = 0;
        long totalBytes = 0;

        using var boundedSource = new BoundedReadStream(source, limits.MaximumArchiveBytes);
        using var reader = new TarReader(boundedSource, true);

        while (await reader.GetNextEntryAsync(false, cancellationToken) is { } entry)
        {
            cancellationToken.ThrowIfCancellationRequested();

            entries++;
            if (entries > limits.MaximumEntries)
                throw new InvalidDataException($"備份包含超過 {limits.MaximumEntries} 個封存項目。");

            var normalizedName = NormalizeEntryName(entry.Name, entry.EntryType, limits.MaximumPathLength);
            var destination = ResolveDestination(root, rootPrefix, normalizedName, comparison);

            if (!seen.Add(destination)) throw new InvalidDataException($"備份包含重複的封存路徑：{entry.Name}");

            if (entry.EntryType == TarEntryType.Directory)
            {
                if (entry.Length != 0) throw new InvalidDataException($"目錄型封存項目不可包含資料：{entry.Name}");

                if (ShouldExtract(normalizedName, includeArtifacts, true))
                    PrivateFileSystem.EnsurePrivateDirectory(destination);

                continue;
            }

            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                throw new InvalidDataException(
                    $"備份包含不允許的封存項目類型 {entry.EntryType}：{entry.Name}");

            var length = entry.Length;
            if (length < 0 || length > limits.MaximumFileBytes)
                throw new InvalidDataException(
                    $"封存項目 {entry.Name} 超過單檔 {limits.MaximumFileBytes} bytes 的還原上限。");

            if (length > limits.MaximumTotalBytes - totalBytes)
                throw new InvalidDataException(
                    $"備份解壓後超過 {limits.MaximumTotalBytes} bytes 的總量上限。");

            totalBytes += length;

            if (!ShouldExtract(normalizedName, includeArtifacts, false)) continue;

            var parent = Path.GetDirectoryName(destination)
                         ?? throw new InvalidDataException($"封存項目沒有有效的父目錄：{entry.Name}");
            PrivateFileSystem.EnsurePrivateDirectory(parent);
            await ExtractRegularFileAsync(entry, destination, length, cancellationToken);
        }
    }

    private static async Task ExtractRegularFileAsync(
        TarEntry entry,
        string destination,
        long length,
        CancellationToken cancellationToken)
    {
        var temporary = $"{destination}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(12))}.tmp";

        try
        {
            await using (var output = PrivateFileSystem.CreatePrivateFile(temporary))
            {
                await CopyExactlyAsync(entry, output, length, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            File.Move(temporary, destination);
        }
        catch
        {
            try
            {
                File.Delete(temporary);
            }
            catch
            {
                // Preserve the original archive error.
            }

            throw;
        }
    }

    private static async Task CopyExactlyAsync(
        TarEntry entry,
        Stream output,
        long length,
        CancellationToken cancellationToken)
    {
        if (length == 0) return;

        var input = entry.DataStream
                    ?? throw new InvalidDataException($"封存項目沒有資料串流：{entry.Name}");
        var buffer = new byte[128 * 1024];
        var remaining = length;

        try
        {
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await input.ReadAsync(buffer.AsMemory(0, requested), cancellationToken);

                if (read == 0) throw new InvalidDataException($"封存項目遭截斷：{entry.Name}");

                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                remaining -= read;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    internal static string NormalizeEntryName(string name, TarEntryType type, int maximumPathLength)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > maximumPathLength || name.IndexOf('\0') >= 0)
            throw new InvalidDataException("備份包含空白、過長或無效的封存路徑。");

        // Tar always uses '/'; backslashes are rejected so the same file cannot be read as a different path on Windows.
        if (name.Contains('\\')) throw new InvalidDataException($"備份包含不具跨平台安全性的封存路徑：{name}");

        var normalized = type == TarEntryType.Directory ? name.TrimEnd('/') : name;
        if (normalized.Length == 0 || normalized.StartsWith('/') || Path.IsPathRooted(normalized))
            throw new InvalidDataException($"備份包含絕對或無效的封存路徑：{name}");

        var segments = normalized.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." ||
                                    HasUnsafeWindowsName(segment)))
            throw new InvalidDataException($"備份包含會離開還原目錄的封存路徑：{name}");

        return normalized;
    }

    private static bool HasUnsafeWindowsName(string segment)
    {
        if (!OperatingSystem.IsWindows()) return false;

        if (segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            segment.EndsWith(' ') || segment.EndsWith('.'))
            return true;

        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static string ResolveDestination(
        string root,
        string rootPrefix,
        string entryName,
        StringComparison comparison)
    {
        var relative = entryName.Replace('/', Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(root, relative));

        if (!destination.StartsWith(rootPrefix, comparison))
            throw new InvalidDataException($"封存項目會寫出還原目錄之外：{entryName}");

        return destination;
    }

    private static bool ShouldExtract(string entryName, bool includeArtifacts, bool directory)
    {
        if (!directory && string.Equals(entryName, DatabaseDump.EntryName, StringComparison.Ordinal)) return true;

        if (!includeArtifacts) return false;

        const string artifactRoot = "walis/artifacts";
        return directory
            ? string.Equals(entryName, "walis", StringComparison.Ordinal) ||
              string.Equals(entryName, artifactRoot, StringComparison.Ordinal) ||
              entryName.StartsWith(artifactRoot + "/", StringComparison.Ordinal)
            : entryName.StartsWith(artifactRoot + "/", StringComparison.Ordinal);
    }

    internal static void ValidateLimits(SafeTarExtractionLimits limits)
    {
        if (limits.MaximumEntries <= 0 || limits.MaximumFileBytes <= 0 ||
            limits.MaximumTotalBytes <= 0 || limits.MaximumPathLength <= 0 ||
            limits.MaximumArchiveBytes <= 0 ||
            limits.MaximumFileBytes > limits.MaximumTotalBytes ||
            limits.MaximumTotalBytes > limits.MaximumArchiveBytes)
            throw new ArgumentOutOfRangeException(nameof(limits), "封存解壓上限必須是有效的正數。");
    }

    /// <summary>Limits the tar header, padding and PAX metadata together; entry.Length alone cannot be trusted.</summary>
    private sealed class BoundedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _consumed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _consumed;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var allowed = Allowed(count);
            var read = inner.Read(buffer, offset, allowed);
            _consumed += read;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var allowed = Allowed(buffer.Length);
            var read = inner.Read(buffer[..allowed]);
            _consumed += read;
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var allowed = Allowed(buffer.Length);
            var read = await inner.ReadAsync(buffer[..allowed], cancellationToken);
            _consumed += read;
            return read;
        }

        private int Allowed(int requested)
        {
            if (requested == 0) return 0;

            if (_consumed >= maximumBytes) throw new InvalidDataException($"備份的 tar 資料超過 {maximumBytes} bytes 的上限。");

            return (int)Math.Min(requested, maximumBytes - _consumed);
        }

        public override void Flush()
        {
            throw new NotSupportedException();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}