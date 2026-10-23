using System.Text.Json;
using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;
using Walis.Core.Security;

namespace Walis.Core.Repositories;

/// <summary>
///     The secret fields of the settings (see <see cref="SettingsSecrets" />) are encrypted one by one inside the JSON payload,
///     so callers always work with plaintext and the rest of the payload stays readable in the database and in backups.
/// </summary>
public sealed class SettingsRepository(IDbConnectionFactory connectionFactory, ISecretProtector protector)
    : ISettingsRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<WalisSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var payload = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Payload FROM Settings WHERE Id = 1;",
            cancellationToken: cancellationToken));

        if (string.IsNullOrWhiteSpace(payload)) return new WalisSettings();

        var settings = JsonSerializer.Deserialize<WalisSettings>(payload, SerializerOptions) ?? new WalisSettings();
        return SettingsSecrets.Unprotect(settings, protector);
    }

    public async Task<WalisSettings> SaveAsync(WalisSettings settings, CancellationToken cancellationToken = default)
    {
        settings.MaxConcurrentBuilds = Math.Clamp(settings.MaxConcurrentBuilds, 1, 16);
        settings.DefaultTimeoutMinutes = Math.Clamp(settings.DefaultTimeoutMinutes, 1, 1440);
        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 3650);
        settings.AuditRetentionDays = Math.Clamp(
            settings.AuditRetentionDays,
            WalisSettings.MinimumAuditRetentionDays,
            WalisSettings.MaximumAuditRetentionDays);
        settings.SmtpPort = Math.Clamp(settings.SmtpPort, 1, 65535);

        // Encrypt a copy: the caller keeps using its plaintext instance after saving.
        var stored = SettingsSecrets.Protect(
            JsonSerializer.Deserialize<WalisSettings>(JsonSerializer.Serialize(settings, SerializerOptions), SerializerOptions)!,
            protector);

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            _dialect.Upsert(
                "Settings",
                [
                    UpsertColumn.Literal("Id", "1"),
                    UpsertColumn.Parameter("Payload"),
                    UpsertColumn.Parameter("UpdatedAt")
                ],
                ["Id"],
                ["Payload", "UpdatedAt"]),
            new { Payload = JsonSerializer.Serialize(stored, SerializerOptions), UpdatedAt = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        return settings;
    }
}