using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Repositories;

public sealed class AiRepository(IDbConnectionFactory connectionFactory, ISecretProtector protector) : IAiRepository
{
    private const string ProviderColumns =
        "Id, Kind, Name, ApiKey, Model, BaseUrl, IsEnabled, IsDefault, UpdatedAt";

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<AiProvider>> ListProvidersAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var providers = await connection.QueryAsync<AiProvider>(new CommandDefinition(
            $"SELECT {ProviderColumns} FROM AiProviders ORDER BY IsDefault DESC, Name;",
            cancellationToken: cancellationToken));
        return [.. providers.Select(Unprotect)];
    }

    public async Task<AiProvider?> GetProviderAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var provider = await connection.QuerySingleOrDefaultAsync<AiProvider>(new CommandDefinition(
            $"SELECT {ProviderColumns} FROM AiProviders WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        return provider is null ? null : Unprotect(provider);
    }

    /// <summary>The key is ciphertext in the database; callers get the plaintext (or null when it cannot be decrypted).</summary>
    private AiProvider Unprotect(AiProvider provider)
    {
        return new AiProvider
        {
            Id = provider.Id,
            Kind = provider.Kind,
            Name = provider.Name,
            ApiKey = protector.Unprotect(provider.ApiKey, SecretPurposes.AiProviderApiKey),
            Model = provider.Model,
            BaseUrl = provider.BaseUrl,
            IsEnabled = provider.IsEnabled,
            IsDefault = provider.IsDefault,
            UpdatedAt = provider.UpdatedAt
        };
    }

    public async Task<AiProvider?> ResolveProviderAsync(Guid? id, CancellationToken cancellationToken = default)
    {
        if (id is { } requested)
        {
            var provider = await GetProviderAsync(requested, cancellationToken);
            return provider?.IsUsable == true ? provider : null;
        }

        var usable = (await ListProvidersAsync(cancellationToken)).Where(provider => provider.IsUsable).ToList();
        return usable.FirstOrDefault(provider => provider.IsDefault) ?? usable.FirstOrDefault();
    }

    public async Task<AiProvider?> UpdateProviderAsync(Guid id, AiProviderDraft draft,
        CancellationToken cancellationToken = default)
    {
        var provider = await GetProviderAsync(id, cancellationToken);

        if (provider is null) return null;

        using var connection = connectionFactory.Create();

        if (draft.IsDefault)
            await connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE AiProviders SET IsDefault = {_dialect.Boolean(false)};",
                cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE AiProviders
            SET ApiKey = @ApiKey,
                Model = @Model,
                BaseUrl = @BaseUrl,
                IsEnabled = @IsEnabled,
                IsDefault = @IsDefault,
                UpdatedAt = @UpdatedAt
            WHERE Id = @Id;
            """,
            new
            {
                Id = id,
                // Sending an empty string means "leave the key unchanged", so a masked value never overwrites the key.
                // Stored encrypted; a legacy plaintext key read back above is re-encrypted by this write.
                ApiKey = protector.Protect(draft.ApiKey is null ? provider.ApiKey : EmptyToNull(draft.ApiKey),
                    SecretPurposes.AiProviderApiKey),
                Model = string.IsNullOrWhiteSpace(draft.Model)
                    ? AiProviderKind.DefaultModel(provider.Kind)
                    : draft.Model.Trim(),
                BaseUrl = EmptyToNull(draft.BaseUrl),
                draft.IsEnabled,
                draft.IsDefault,
                UpdatedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return await GetProviderAsync(id, cancellationToken);
    }

    public async Task AppendRequestAsync(AiRequestLogEntry entry, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO AiRequests (Id, StartedAt, ProviderKind, Model, Purpose, BuildId, JobId, Actor,
                                    PromptChars, InputTokens, OutputTokens, DurationMs, Succeeded, Error)
            VALUES (@Id, @StartedAt, @ProviderKind, @Model, @Purpose, @BuildId, @JobId, @Actor,
                    @PromptChars, @InputTokens, @OutputTokens, @DurationMs, @Succeeded, @Error);
            """,
            new
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                StartedAt = entry.StartedAt == default ? DateTime.UtcNow : entry.StartedAt,
                entry.ProviderKind,
                entry.Model,
                entry.Purpose,
                entry.BuildId,
                entry.JobId,
                entry.Actor,
                entry.PromptChars,
                entry.InputTokens,
                entry.OutputTokens,
                entry.DurationMs,
                entry.Succeeded,
                entry.Error
            },
            cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<AiRequestLogEntry>> ListRequestsAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var entries = await connection.QueryAsync<AiRequestLogEntry>(new CommandDefinition(
            $"""
             SELECT r.Id, r.StartedAt, r.ProviderKind, r.Model, r.Purpose, r.BuildId, r.JobId, r.Actor,
                    r.PromptChars, r.InputTokens, r.OutputTokens, r.DurationMs, r.Succeeded, r.Error,
                    j.Name AS JobName
             FROM AiRequests r
             LEFT JOIN Jobs j ON j.Id = r.JobId
             ORDER BY r.StartedAt DESC
             {_dialect.Limit("@Limit")};
             """,
            new { Limit = Math.Clamp(limit, 1, 500) },
            cancellationToken: cancellationToken));
        return entries.AsList();
    }

    public async Task<int> PurgeRequestsAsync(int retentionDays, CancellationToken cancellationToken = default)
    {
        if (retentionDays <= 0) return 0;

        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM AiRequests WHERE StartedAt < @Threshold;",
            new { Threshold = DateTime.UtcNow.AddDays(-retentionDays) },
            cancellationToken: cancellationToken));
    }

    private static string? EmptyToNull(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}