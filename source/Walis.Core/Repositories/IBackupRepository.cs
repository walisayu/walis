using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface IBackupRepository
{
    Task<IReadOnlyList<BackupRun>> ListRunsAsync(int limit, CancellationToken cancellationToken = default);

    Task<BackupRun?> GetRunAsync(Guid id, CancellationToken cancellationToken = default);

    Task<BackupRun> StartRunAsync(string triggeredBy, CancellationToken cancellationToken = default);

    Task CompleteRunAsync(
        Guid id,
        string status,
        long sizeBytes,
        string? fileName,
        string log,
        CancellationToken cancellationToken = default);

    /// <summary>Removes old records according to the retention days so the history cannot grow forever.</summary>
    Task PurgeAsync(int retentionDays, CancellationToken cancellationToken = default);
}