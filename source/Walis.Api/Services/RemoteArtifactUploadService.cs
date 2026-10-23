using System.Buffers;
using System.Data;
using System.Text;
using Dapper;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Walis.Core.Database;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Api.Services;

public enum RemoteArtifactUploadStatus
{
    Saved,
    BadRequest,
    PayloadTooLarge,
    NotFound,
    Forbidden,
    Conflict
}

public sealed record RemoteArtifactUploadResult(
    RemoteArtifactUploadStatus Status,
    int Saved,
    string Message);

/// <summary>
///     Receives multipart artifacts from a remote runner. The request does not go through <c>ReadFormAsync</c>, so the framework never buffers
///     the whole uncontrolled request first; files are streamed into a random 0700 staging directory and only at the end, in a transaction
///     that locks the build row, is the whole directory atomically moved into place and the index written.
/// </summary>
public sealed class RemoteArtifactUploadService(
    ArtifactStore store,
    IDbConnectionFactory connectionFactory,
    ILogger<RemoteArtifactUploadService> logger)
{
    public const long MaximumFileBytes = ArtifactCollector.MaximumTotalBytes;
    public const long MaximumRequestBytes = ArtifactCollector.MaximumTotalBytes + 2L * 1024 * 1024;

    private const int MaximumConcurrentUploads = 4;
    private const int MaximumBoundaryLength = 128;
    private const int MaximumHeaderCount = 16;
    private const int MaximumHeaderBytes = 8 * 1024;
    private const int MaximumRelativePathLength = 500;
    private const int CopyBufferSize = 64 * 1024;

    private static readonly HashSet<string> WindowsDeviceNames = new(
        [
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        ],
        StringComparer.OrdinalIgnoreCase);

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;
    private readonly SemaphoreSlim _uploadSlots = new(MaximumConcurrentUploads, MaximumConcurrentUploads);

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public async Task<RemoteArtifactUploadResult> UploadAsync(
        Guid buildId,
        string runnerName,
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaximumRequestBytes)
            return Rejected(
                RemoteArtifactUploadStatus.PayloadTooLarge,
                $"上傳請求超過 {MaximumRequestBytes} bytes 上限。");

        if (!TryGetBoundary(request.ContentType, out var boundary))
            return Rejected(
                RemoteArtifactUploadStatus.BadRequest,
                "請以有效的 multipart/form-data 上傳檔案。");

        await _uploadSlots.WaitAsync(cancellationToken);

        try
        {
            var staging = CreatePrivateStagingDirectory();

            try
            {
                IReadOnlyList<CollectedArtifact> artifacts;

                try
                {
                    artifacts = await StageAsync(request.Body, boundary, staging, cancellationToken);
                }
                catch (ArtifactUploadRejectedException exception)
                {
                    return Rejected(exception.Status, exception.Message);
                }
                catch (InvalidDataException)
                {
                    return Rejected(
                        RemoteArtifactUploadStatus.BadRequest,
                        "multipart 內容不完整或格式不正確。");
                }

                if (artifacts.Count == 0) return Rejected(RemoteArtifactUploadStatus.BadRequest, "請至少上傳一個檔案。");

                return await CommitAsync(buildId, runnerName, staging, artifacts, cancellationToken);
            }
            finally
            {
                TryDeleteDirectory(staging);
            }
        }
        finally
        {
            _uploadSlots.Release();
        }
    }

    private async Task<IReadOnlyList<CollectedArtifact>> StageAsync(
        Stream requestBody,
        string boundary,
        string staging,
        CancellationToken cancellationToken)
    {
        var limitedBody = new LimitedReadStream(requestBody, MaximumRequestBytes);
        var reader = new MultipartReader(boundary, limitedBody)
        {
            HeadersCountLimit = MaximumHeaderCount,
            HeadersLengthLimit = MaximumHeaderBytes,
            BodyLengthLimit = MaximumFileBytes
        };
        var artifacts = new List<CollectedArtifact>();
        var paths = new HashSet<string>(PathComparer);
        long totalBytes = 0;

        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition) ||
                !string.Equals(disposition.DispositionType.Value, "form-data", StringComparison.OrdinalIgnoreCase) ||
                (!disposition.FileName.HasValue && !disposition.FileNameStar.HasValue))
                throw new ArtifactUploadRejectedException(
                    RemoteArtifactUploadStatus.BadRequest,
                    "multipart 只能包含檔案欄位。");

            if (artifacts.Count >= ArtifactCollector.MaximumFiles)
                throw new ArtifactUploadRejectedException(
                    RemoteArtifactUploadStatus.PayloadTooLarge,
                    $"每次最多上傳 {ArtifactCollector.MaximumFiles} 個檔案。");

            var rawName = HeaderUtilities.RemoveQuotes(
                disposition.FileNameStar.HasValue ? disposition.FileNameStar : disposition.FileName).Value;
            var relativePath = NormalizeRelativePath(rawName);

            if (relativePath is null ||
                paths.Any(existing => IsAncestor(existing, relativePath) || IsAncestor(relativePath, existing)) ||
                !paths.Add(relativePath))
                throw new ArtifactUploadRejectedException(
                    RemoteArtifactUploadStatus.BadRequest,
                    "產出檔名無效、重複，或與另一個檔案路徑衝突。");

            var target = Path.Combine(staging, relativePath.Replace('/', Path.DirectorySeparatorChar));
            CreatePrivateDirectory(Path.GetDirectoryName(target)!);

            long size;

            try
            {
                size = await CopyBoundedAsync(
                    section.Body,
                    target,
                    MaximumFileBytes,
                    ArtifactCollector.MaximumTotalBytes - totalBytes,
                    cancellationToken);
            }
            catch (InvalidDataException)
            {
                throw new ArtifactUploadRejectedException(
                    RemoteArtifactUploadStatus.PayloadTooLarge,
                    $"單一檔案不得超過 {MaximumFileBytes} bytes，全部檔案不得超過 {ArtifactCollector.MaximumTotalBytes} bytes。");
            }

            totalBytes += size;
            artifacts.Add(new CollectedArtifact(relativePath, size));
        }

        return artifacts;
    }

    private async Task<RemoteArtifactUploadResult> CommitAsync(
        Guid buildId,
        string runnerName,
        string staging,
        IReadOnlyList<CollectedArtifact> artifacts,
        CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.Create();

        if (connection.State != ConnectionState.Open) connection.Open();

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var moved = false;
        var commitStarted = false;
        var destination = store.DirectoryFor(buildId);

        try
        {
            var state = await connection.QuerySingleOrDefaultAsync<ArtifactBuildState>(new CommandDefinition(
                $"SELECT Status, RunnerName FROM Builds{_dialect.ForUpdateHint} WHERE Id = @Id{_dialect.ForUpdateSuffix};",
                new { Id = buildId },
                transaction,
                cancellationToken: cancellationToken));

            if (state is null)
            {
                transaction.Rollback();
                return Rejected(RemoteArtifactUploadStatus.NotFound, "找不到這筆建置。");
            }

            if (!string.Equals(state.RunnerName, runnerName, StringComparison.OrdinalIgnoreCase))
            {
                transaction.Rollback();
                return Rejected(RemoteArtifactUploadStatus.Forbidden, "這筆建置已不屬於目前的執行器。");
            }

            if (!string.Equals(state.Status, BuildStatus.Running, StringComparison.Ordinal))
            {
                transaction.Rollback();
                return Rejected(RemoteArtifactUploadStatus.Conflict, "只有執行中的建置可以上傳產出檔案。");
            }

            var existing = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "SELECT COUNT(1) FROM BuildArtifacts WHERE BuildId = @BuildId;",
                new { BuildId = buildId },
                transaction,
                cancellationToken: cancellationToken));

            if (existing != 0 || Directory.Exists(destination) || File.Exists(destination))
            {
                transaction.Rollback();
                return Rejected(RemoteArtifactUploadStatus.Conflict, "這筆建置已經提交過產出檔案。");
            }

            Directory.Move(staging, destination);
            moved = true;

            var now = DateTime.UtcNow;
            var rows = artifacts.Select(artifact => new
            {
                Id = Guid.NewGuid(),
                BuildId = buildId,
                artifact.RelativePath,
                artifact.SizeBytes,
                CreatedAt = now,
                BuildStatus.Running,
                // The API compares runner names case-insensitively; the database dialect/collation may not.
                // After locking the row, use the canonical value from the database so the conditional INSERT does not misjudge.
                state.RunnerName
            });
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO BuildArtifacts (Id, BuildId, RelativePath, SizeBytes, CreatedAt)
                SELECT @Id, @BuildId, @RelativePath, @SizeBytes, @CreatedAt
                FROM Builds
                WHERE Id = @BuildId AND Status = @Running AND RunnerName = @RunnerName;
                """,
                rows,
                transaction,
                cancellationToken: cancellationToken));

            if (affected != artifacts.Count)
            {
                transaction.Rollback();
                TryDeleteDirectory(destination);
                moved = false;
                return Rejected(RemoteArtifactUploadStatus.Conflict, "建置狀態在上傳期間已改變，未儲存產出檔案。");
            }

            // A failed commit may mean "the server committed but the response was lost"; from this moment the final directory must not be deleted,
            // otherwise we create a state where the DB index exists but the files were wiped by the rollback.
            commitStarted = true;
            transaction.Commit();
            return new RemoteArtifactUploadResult(RemoteArtifactUploadStatus.Saved, artifacts.Count, string.Empty);
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch
            {
                // The original error matters more; the transaction dispose does the final cleanup anyway.
            }

            if (moved && !commitStarted)
                TryDeleteDirectory(destination);
            else if (moved)
                logger.LogWarning(
                    "Artifact transaction commit 結果不明，保留 {Path} 避免刪除可能已提交的檔案",
                    destination);

            throw;
        }
    }

    private static async Task<long> CopyBoundedAsync(
        Stream source,
        string target,
        long maximumFileBytes,
        long remainingTotalBytes,
        CancellationToken cancellationToken)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = CopyBufferSize,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        };

        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        await using var destination = new FileStream(target, options);
        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);
        long written = 0;

        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);

                if (read == 0) break;

                if (written > maximumFileBytes - read || written > remainingTotalBytes - read)
                    throw new InvalidDataException("artifact quota exceeded");

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                written += read;
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(true);
            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string? NormalizeRelativePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOf('\0') >= 0) return null;

        var normalized = fileName.Replace('\\', '/').Normalize(NormalizationForm.FormC);

        if (normalized.Length > MaximumRelativePathLength ||
            normalized.StartsWith('/') ||
            normalized.EndsWith('/') ||
            normalized.Contains("//", StringComparison.Ordinal))
            return null;

        var segments = normalized.Split('/');

        if (segments.Any(segment => !IsPortableFileNameSegment(segment))) return null;

        return normalized;
    }

    private static bool IsPortableFileNameSegment(string segment)
    {
        if (segment is "" or "." or ".." ||
            segment.Length > 255 ||
            segment.EndsWith(' ') ||
            segment.EndsWith('.') ||
            segment.Any(character => character < ' ' || "<>:\"|?*".Contains(character)))
            return false;

        // Windows resolves names like CON.txt and NUL.json as devices; even though the API runs on Unix right now,
        // the artifact may later be extracted/synced by a Windows client, so a portable allowlist is used.
        var stem = segment.Split('.', 2)[0];
        return !WindowsDeviceNames.Contains(stem);
    }

    private static bool IsAncestor(string candidate, string path)
    {
        return path.StartsWith(candidate + "/", PathComparison);
    }

    private static bool TryGetBoundary(string? contentType, out string boundary)
    {
        boundary = string.Empty;

        if (!MediaTypeHeaderValue.TryParse(contentType, out var parsed) ||
            !string.Equals(parsed.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase))
            return false;

        boundary = HeaderUtilities.RemoveQuotes(parsed.Boundary).Value ?? string.Empty;
        return boundary.Length is > 0 and <= MaximumBoundaryLength;
    }

    private string CreatePrivateStagingDirectory()
    {
        CreatePrivateDirectory(store.Root);
        var staging = Path.Combine(store.Root, $".upload-{Guid.NewGuid():N}");
        CreatePrivateDirectory(staging);
        return staging;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);
        else
            Directory.CreateDirectory(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "無法清理 artifact 暫存或回滾目錄 {Path}", path);
        }
    }

    private static RemoteArtifactUploadResult Rejected(RemoteArtifactUploadStatus status, string message)
    {
        return new RemoteArtifactUploadResult(status, 0, message);
    }

    private sealed class ArtifactBuildState
    {
        public string Status { get; init; } = string.Empty;
        public string? RunnerName { get; init; }
    }

    private sealed class ArtifactUploadRejectedException(
        RemoteArtifactUploadStatus status,
        string message) : Exception(message)
    {
        public RemoteArtifactUploadStatus Status { get; } = status;
    }

    private sealed class LimitedReadStream(Stream inner, long maximumBytes) : Stream
    {
        private long _read;

        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            Count(read);
            return read;
        }

        public override async Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            var read = await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken);
            Count(read);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            Count(read);
            return read;
        }

        private void Count(int count)
        {
            if (_read > maximumBytes - count)
                throw new ArtifactUploadRejectedException(
                    RemoteArtifactUploadStatus.PayloadTooLarge,
                    $"上傳請求超過 {maximumBytes} bytes 上限。");

            _read += count;
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