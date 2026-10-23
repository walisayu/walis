using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;

namespace Walis.Core.Repositories;

public sealed class BackupRepository(IDbConnectionFactory connectionFactory) : IBackupRepository
{
    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<BackupRun>> ListRunsAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var runs = await connection.QueryAsync<BackupRun>(new CommandDefinition(
            $"""
             SELECT Id, StartedAt, FinishedAt, Status, TriggeredBy, SizeBytes, FileName, Log
             FROM BackupRuns
             ORDER BY StartedAt DESC
             {_dialect.Limit("@Limit")};
             """,
            new { Limit = Math.Clamp(limit, 1, 200) },
            cancellationToken: cancellationToken));

        return [.. runs];
    }

    public async Task<BackupRun?> GetRunAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<BackupRun>(new CommandDefinition(
            """
            SELECT Id, StartedAt, FinishedAt, Status, TriggeredBy, SizeBytes, FileName, Log
            FROM BackupRuns WHERE Id = @Id;
            """,
            new { Id = id },
            cancellationToken: cancellationToken));
    }

    public async Task<BackupRun> StartRunAsync(string triggeredBy, CancellationToken cancellationToken = default)
    {
        var run = new BackupRun
        {
            Id = Guid.NewGuid(),
            StartedAt = DateTime.UtcNow,
            Status = BackupRunStatus.Running,
            TriggeredBy = triggeredBy
        };

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO BackupRuns (Id, StartedAt, Status, TriggeredBy, SizeBytes, Log)
            VALUES (@Id, @StartedAt, @Status, @TriggeredBy, 0, '');
            """,
            run,
            cancellationToken: cancellationToken));

        return run;
    }

    public async Task CompleteRunAsync(
        Guid id,
        string status,
        long sizeBytes,
        string? fileName,
        string log,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE BackupRuns
            SET FinishedAt = @FinishedAt, Status = @Status, SizeBytes = @SizeBytes,
                FileName = @FileName, Log = @Log
            WHERE Id = @Id;
            """,
            new
            {
                Id = id, FinishedAt = DateTime.UtcNow, Status = status, SizeBytes = sizeBytes, FileName = fileName,
                Log = log
            },
            cancellationToken: cancellationToken));
    }

    public async Task PurgeAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM BackupRuns WHERE StartedAt < @Threshold;",
            new { Threshold = DateTime.UtcNow.AddDays(-Math.Clamp(retentionDays, 1, 3650)) },
            cancellationToken: cancellationToken));
    }
}