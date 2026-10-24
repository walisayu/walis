using System.Data;
using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;

namespace Walis.Core.Repositories;

public sealed class RunnerRepository(IDbConnectionFactory connectionFactory) : IRunnerRepository
{
    /// <summary>A runner is considered offline after this long without a heartbeat.</summary>
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromMinutes(5);

    private readonly string _columns = $"""
                                        r.Id, r.Name, r.OperatingSystem, r.Version, r.Labels, r.TotalSlots, r.IsPaused, r.IsLocal,
                                        r.LastSeenAt, r.CreatedAt, r.MinimumRunRole,
                                        (SELECT COUNT(1) FROM Builds b WHERE b.Status = 'Running' AND b.RunnerName = r.Name) AS UsedSlots,
                                        (SELECT b.Number FROM Builds b WHERE b.Status = 'Running' AND b.RunnerName = r.Name
                                            ORDER BY b.StartedAt {connectionFactory.Dialect.Limit("1")}) AS CurrentBuildNumber,
                                        (SELECT j.Name FROM Builds b INNER JOIN Jobs j ON j.Id = b.JobId
                                            WHERE b.Status = 'Running' AND b.RunnerName = r.Name
                                            ORDER BY b.StartedAt {connectionFactory.Dialect.Limit("1")}) AS CurrentJobName
                                        """;

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<Runner>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT {_columns} FROM Runners r ORDER BY r.IsLocal DESC, r.Name;";

        using var connection = connectionFactory.Create();
        var runners =
            await connection.QueryAsync<Runner>(new CommandDefinition(sql, cancellationToken: cancellationToken));
        return runners.Select(WithStatus).ToList();
    }

    public async Task<Runner?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT {_columns} FROM Runners r WHERE r.Id = @Id;";

        using var connection = connectionFactory.Create();
        var runner = await connection.QuerySingleOrDefaultAsync<Runner>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
        return runner is null ? null : WithStatus(runner);
    }

    public async Task<Runner?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        var sql = $"SELECT {_columns} FROM Runners r WHERE r.Name = @Name;";

        using var connection = connectionFactory.Create();
        var runner = await connection.QuerySingleOrDefaultAsync<Runner>(
            new CommandDefinition(sql, new { Name = name }, cancellationToken: cancellationToken));
        return runner is null ? null : WithStatus(runner);
    }

    public async Task<Runner> CreateAsync(RunnerDraft draft, CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();
        var now = DateTime.UtcNow;

        const string sql = """
                           INSERT INTO Runners (Id, Name, OperatingSystem, Version, Labels, TotalSlots, IsPaused, IsLocal, LastSeenAt, CreatedAt, MinimumRunRole)
                           VALUES (@Id, @Name, @OperatingSystem, @Version, @Labels, @TotalSlots, @IsPaused, 0, @LastSeenAt, @CreatedAt, @MinimumRunRole);
                           """;

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                Name = draft.Name.Trim(),
                OperatingSystem = draft.OperatingSystem?.Trim() ?? string.Empty,
                Version = draft.Version?.Trim() ?? string.Empty,
                Labels = NormalizeLabels(draft.Labels),
                TotalSlots = Math.Clamp(draft.TotalSlots, 1, 32),
                draft.IsPaused,
                LastSeenAt = now,
                CreatedAt = now,
                MinimumRunRole = NormalizeMinimumRunRole(draft.MinimumRunRole)
            },
            cancellationToken: cancellationToken));

        return (await GetAsync(id, cancellationToken))!;
    }

    public async Task<Runner?> UpdateAsync(Guid id, RunnerDraft draft, CancellationToken cancellationToken = default)
    {
        const string sql = """
                           UPDATE Runners
                           SET Name = @Name,
                               OperatingSystem = @OperatingSystem,
                               Version = @Version,
                               Labels = @Labels,
                               TotalSlots = @TotalSlots,
                               IsPaused = @IsPaused,
                               MinimumRunRole = COALESCE(@MinimumRunRole, MinimumRunRole)
                           WHERE Id = @Id;
                           """;

        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                Name = draft.Name.Trim(),
                OperatingSystem = draft.OperatingSystem?.Trim() ?? string.Empty,
                Version = draft.Version?.Trim() ?? string.Empty,
                Labels = NormalizeLabels(draft.Labels),
                TotalSlots = Math.Clamp(draft.TotalSlots, 1, 32),
                draft.IsPaused,
                // Null keeps the stored value: a runner re-registering itself never touches the administrator's choice.
                MinimumRunRole = draft.MinimumRunRole is null ? null : NormalizeMinimumRunRole(draft.MinimumRunRole)
            },
            cancellationToken: cancellationToken));

        return affected == 0 ? null : await GetAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await DeleteWithKeysAsync(id, cancellationToken) is not null;
    }

    public async Task<RunnerDeletion?> DeleteWithKeysAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        if (connection.State != ConnectionState.Open) connection.Open();

        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);

        var name = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            $"SELECT Name FROM Runners WHERE Id = @Id AND IsLocal = {_dialect.Boolean(false)};",
            new { Id = id },
            transaction,
            cancellationToken: cancellationToken));

        if (name is null) return null;

        var affected = await connection.ExecuteAsync(new CommandDefinition(
            $"DELETE FROM Runners WHERE Id = @Id AND IsLocal = {_dialect.Boolean(false)};",
            new { Id = id },
            transaction,
            cancellationToken: cancellationToken));

        if (affected == 0) return null;

        // "Delete runner" reads as revocation to an admin, so the keys bound to that name go with it; otherwise a key for a
        // decommissioned or compromised machine could simply register the same name again and keep claiming its secrets.
        // Runner names are compared case-insensitively everywhere else, so match the same way here regardless of collation.
        var disabled = await connection.ExecuteAsync(new CommandDefinition(
            $"""
             UPDATE ApiKeys SET IsDisabled = {_dialect.Boolean(true)}
             WHERE RunnerName IS NOT NULL AND LOWER(RunnerName) = LOWER(@Name) AND IsDisabled = {_dialect.Boolean(false)};
             """,
            new { Name = name },
            transaction,
            cancellationToken: cancellationToken));

        transaction.Commit();
        return new RunnerDeletion(name, disabled);
    }

    public async Task<Runner?> SetPausedAsync(Guid id, bool paused, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Runners SET IsPaused = @IsPaused WHERE Id = @Id;",
            new { Id = id, IsPaused = paused },
            cancellationToken: cancellationToken));

        return affected == 0 ? null : await GetAsync(id, cancellationToken);
    }

    public async Task<bool> HeartbeatAsync(string name, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Runners SET LastSeenAt = @Now WHERE Name = @Name;",
            new { Name = name, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public async Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT(1) FROM Runners WHERE Name = @Name;",
            new { Name = name },
            cancellationToken: cancellationToken));
        return count != 0;
    }

    private static Runner WithStatus(Runner runner)
    {
        runner.Status = runner switch
        {
            { IsPaused: true } => RunnerStatus.Paused,
            _ when DateTime.UtcNow - runner.LastSeenAt > OfflineAfter => RunnerStatus.Offline,
            { UsedSlots: > 0 } => RunnerStatus.Busy,
            _ => RunnerStatus.Idle
        };
        return runner;
    }

    private static string NormalizeLabels(string? labels)
    {
        return string.Join(
            ',',
            (labels ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(label => label.ToLowerInvariant())
            .Distinct());
    }

    /// <summary>operator or admin; anything else (viewer, empty, unknown) is the operator floor every job already has.</summary>
    public static string NormalizeMinimumRunRole(string? role)
    {
        return role == UserRole.Admin ? UserRole.Admin : UserRole.Operator;
    }
}
