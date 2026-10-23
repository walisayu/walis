using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Repositories;

/// <remarks>
///     Slack incoming-webhook URLs and generic webhook URLs are credentials in their own right (whoever holds one can post as the
///     channel, and many carry a token), so for those kinds <see cref="NotificationChannel.Target" /> is encrypted at this boundary
///     like any other stored secret. Email addresses are not.
/// </remarks>
public sealed class NotificationRepository(IDbConnectionFactory connectionFactory, ISecretProtector protector)
    : INotificationRepository
{
    private const string ChannelColumns = "Id, Kind, Name, Target, IsEnabled, UpdatedAt";
    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var channels = await connection.QueryAsync<NotificationChannel>(new CommandDefinition(
            $"SELECT {ChannelColumns} FROM NotificationChannels ORDER BY Kind;",
            cancellationToken: cancellationToken));
        return channels.Select(Unprotect).ToList();
    }

    public async Task<IReadOnlyList<NotificationChannel>> ListEnabledChannelsAsync(
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var channels = await connection.QueryAsync<NotificationChannel>(new CommandDefinition(
            $"SELECT {ChannelColumns} FROM NotificationChannels "
            + $"WHERE IsEnabled = {_dialect.Boolean(true)} AND {_dialect.Length("Target")} > 0 ORDER BY Kind;",
            cancellationToken: cancellationToken));
        return channels.Select(Unprotect).ToList();
    }

    public async Task<NotificationChannel?> GetChannelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var channel = await connection.QuerySingleOrDefaultAsync<NotificationChannel>(new CommandDefinition(
            $"SELECT {ChannelColumns} FROM NotificationChannels WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        return channel is null ? null : Unprotect(channel);
    }

    public async Task<NotificationChannel> CreateChannelAsync(NotificationChannelDraft draft,
        CancellationToken cancellationToken = default)
    {
        var id = Guid.NewGuid();

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO NotificationChannels (Id, Kind, Name, Target, IsEnabled, UpdatedAt)
            VALUES (@Id, @Kind, @Name, @Target, @IsEnabled, @UpdatedAt);
            """,
            new
            {
                Id = id,
                draft.Kind,
                Name = draft.Name.Trim(),
                Target = ProtectTarget(draft.Kind, draft.Target.Trim()),
                draft.IsEnabled,
                UpdatedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return (await GetChannelAsync(id, cancellationToken))!;
    }

    public async Task<NotificationChannel?> UpdateChannelAsync(Guid id, NotificationChannelDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            """
            UPDATE NotificationChannels
            SET Kind = @Kind, Name = @Name, Target = @Target, IsEnabled = @IsEnabled, UpdatedAt = @UpdatedAt
            WHERE Id = @Id;
            """,
            new
            {
                Id = id,
                draft.Kind,
                Name = draft.Name.Trim(),
                Target = ProtectTarget(draft.Kind, draft.Target.Trim()),
                draft.IsEnabled,
                UpdatedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return affected == 0 ? null : await GetChannelAsync(id, cancellationToken);
    }

    public async Task<bool> DeleteChannelAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM NotificationChannels WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public async Task<IReadOnlyList<NotificationLogEntry>> ListLogAsync(int limit,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var entries = await connection.QueryAsync<NotificationLogEntry>(new CommandDefinition(
            $"""
             SELECT Id, SentAt, Event, ChannelKind, Target, Succeeded, Detail
             FROM NotificationLogs
             ORDER BY SentAt DESC
             {_dialect.Limit("@Limit")};
             """,
            new { Limit = Math.Clamp(limit, 1, 200) },
            cancellationToken: cancellationToken));
        return entries.AsList();
    }

    public async Task AppendLogAsync(NotificationLogEntry entry, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO NotificationLogs (Id, SentAt, Event, ChannelKind, Target, Succeeded, Detail)
            VALUES (@Id, @SentAt, @Event, @ChannelKind, @Target, @Succeeded, @Detail);
            """,
            new
            {
                Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
                SentAt = entry.SentAt == default ? DateTime.UtcNow : entry.SentAt,
                entry.Event,
                entry.ChannelKind,
                entry.Target,
                entry.Succeeded,
                entry.Detail
            },
            cancellationToken: cancellationToken));
    }

    /// <summary>Whether a channel kind's target is a credential (encrypted at rest and masked in API responses).</summary>
    public static bool IsSecretTarget(string? kind)
    {
        return kind is NotificationChannelKind.Slack or NotificationChannelKind.Webhook;
    }

    private string ProtectTarget(string kind, string target)
    {
        return IsSecretTarget(kind) ? protector.Protect(target, SecretPurposes.NotificationChannelTarget) ?? string.Empty : target;
    }

    /// <summary>A target that no longer decrypts reads as empty: the channel is then skipped instead of posting ciphertext.</summary>
    private NotificationChannel Unprotect(NotificationChannel channel)
    {
        if (!IsSecretTarget(channel.Kind)) return channel;

        return new NotificationChannel
        {
            Id = channel.Id,
            Kind = channel.Kind,
            Name = channel.Name,
            Target = protector.Unprotect(channel.Target, SecretPurposes.NotificationChannelTarget) ?? string.Empty,
            IsEnabled = channel.IsEnabled,
            UpdatedAt = channel.UpdatedAt
        };
    }
}
