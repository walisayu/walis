using Dapper;
using Walis.Core.Database;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Repositories;

/// <summary>
///     Secret values are ciphertext in the database (<see cref="ISecretProtector" />), so the length rules that decide whether a
///     secret is injected and masked (at least 4, at most <see cref="ShellCommandExecutor.MaximumSecretLength" /> characters)
///     are applied here after decryption rather than in SQL.
/// </summary>
public sealed class EnvironmentVariableRepository(IDbConnectionFactory connectionFactory, ISecretProtector protector)
    : IEnvironmentVariableRepository
{
    private const int MinimumSecretLength = 4;

    private const string Columns = """
                                   e.Id, e.Name, e.Value, e.IsSecret, e.JobId, e.UpdatedAt, j.Name AS JobName
                                   """;

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<EnvironmentVariable>> ListAsync(CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT {Columns}
                   FROM EnvironmentVariables e
                   LEFT JOIN Jobs j ON j.Id = e.JobId
                   ORDER BY CASE WHEN e.JobId IS NULL THEN 0 ELSE 1 END, e.Name;
                   """;

        using var connection = connectionFactory.Create();
        var variables = await connection.QueryAsync<EnvironmentVariable>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return [.. variables.Select(Unprotect)];
    }

    public async Task<EnvironmentVariable?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sql = $"""
                   SELECT {Columns}
                   FROM EnvironmentVariables e
                   LEFT JOIN Jobs j ON j.Id = e.JobId
                   WHERE e.Id = @Id;
                   """;

        using var connection = connectionFactory.Create();
        var variable = await connection.QuerySingleOrDefaultAsync<EnvironmentVariable>(
            new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
        return variable is null ? null : Unprotect(variable);
    }

    private EnvironmentVariable Unprotect(EnvironmentVariable variable)
    {
        if (!variable.IsSecret) return variable;

        return new EnvironmentVariable
        {
            Id = variable.Id,
            Name = variable.Name,
            Value = protector.Unprotect(variable.Value, SecretPurposes.EnvironmentVariableValue) ?? string.Empty,
            IsSecret = variable.IsSecret,
            JobId = variable.JobId,
            UpdatedAt = variable.UpdatedAt,
            JobName = variable.JobName
        };
    }

    /// <summary>Only secret values are encrypted; plain variables stay readable in the database and in backups.</summary>
    private string StoredValue(EnvironmentVariableDraft draft)
    {
        return draft.IsSecret
            ? protector.Protect(draft.Value, SecretPurposes.EnvironmentVariableValue) ?? string.Empty
            : draft.Value;
    }

    private static bool IsMaskable(string value)
    {
        return value.Length is >= MinimumSecretLength and <= ShellCommandExecutor.MaximumSecretLength;
    }

    public async Task<EnvironmentVariable> CreateAsync(EnvironmentVariableDraft draft,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();

        const string sql = """
                           INSERT INTO EnvironmentVariables (Id, Name, Value, IsSecret, JobId, UpdatedAt)
                           VALUES (@Id, @Name, @Value, @IsSecret, @JobId, @UpdatedAt);
                           """;

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                Name = draft.Name.Trim(),
                Value = StoredValue(draft),
                draft.IsSecret,
                draft.JobId,
                UpdatedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return (await GetAsync(id, cancellationToken))!;
    }

    public async Task<EnvironmentVariable?> UpdateAsync(Guid id, EnvironmentVariableDraft draft,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
                           UPDATE EnvironmentVariables
                           SET Name = @Name, Value = @Value, IsSecret = @IsSecret, JobId = @JobId, UpdatedAt = @UpdatedAt
                           WHERE Id = @Id;
                           """;

        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new
            {
                Id = id,
                Name = draft.Name.Trim(),
                Value = StoredValue(draft),
                draft.IsSecret,
                draft.JobId,
                UpdatedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return affected == 0 ? null : await GetAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM EnvironmentVariables WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveForJobAsync(Guid jobId,
        CancellationToken cancellationToken = default)
    {
        // Secret values are ciphertext, so the length rules are applied after decryption below.
        var sql = $"""
                   SELECT Name, Value, IsSecret, JobId
                   FROM EnvironmentVariables
                   WHERE JobId = @JobId
                      OR (JobId IS NULL AND IsSecret = {_dialect.Boolean(false)})
                   ORDER BY CASE WHEN JobId IS NULL THEN 0 ELSE 1 END;
                   """;

        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ResolvedVariable>(
            new CommandDefinition(sql, new { JobId = jobId }, cancellationToken: cancellationToken));

        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (row.IsSecret)
            {
                var value = protector.Unprotect(row.Value, SecretPurposes.EnvironmentVariableValue) ?? string.Empty;
                if (IsMaskable(value)) resolved[row.Name] = value;
            }
            else if (row.Value.Length <= ShellCommandExecutor.MaximumSecretLength)
            {
                resolved[row.Name] = row.Value;
            }
        }

        return resolved;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveGlobalsForTrustedServiceAsync(
        CancellationToken cancellationToken = default)
    {
        const string sql = """
                           SELECT Name, Value, IsSecret, JobId
                           FROM EnvironmentVariables
                           WHERE JobId IS NULL
                           ORDER BY Name;
                           """;

        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ResolvedVariable>(
            new CommandDefinition(sql, cancellationToken: cancellationToken));
        return rows.ToDictionary(
            row => row.Name,
            row => row.IsSecret
                ? protector.Unprotect(row.Value, SecretPurposes.EnvironmentVariableValue) ?? string.Empty
                : row.Value,
            StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<string>> ListSecretValuesAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var values = await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT Value FROM EnvironmentVariables WHERE IsSecret = {_dialect.Boolean(true)};",
            cancellationToken: cancellationToken));
        return MaskableValues(values);
    }

    public async Task<IReadOnlyList<string>> ListSecretValuesForJobAsync(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var values = await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT Value FROM EnvironmentVariables WHERE JobId = @JobId AND IsSecret = {_dialect.Boolean(true)};",
            new { JobId = jobId },
            cancellationToken: cancellationToken));
        return MaskableValues(values);
    }

    private List<string> MaskableValues(IEnumerable<string> stored)
    {
        return
        [
            .. stored
                .Select(value => protector.Unprotect(value, SecretPurposes.EnvironmentVariableValue) ?? string.Empty)
                .Where(IsMaskable)
        ];
    }

    // Dapper's target type must have a parameterless constructor. With a positional record, when a query returns zero rows
    // Microsoft.Data.Sqlite cannot determine the column types and reports them all as BLOB, so Dapper finds no
    // matching constructor and throws; a brand-new database is exactly the zero-row case.
    private sealed class ResolvedVariable
    {
        public string Name { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool IsSecret { get; set; }
        public Guid? JobId { get; set; }
    }
}