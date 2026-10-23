using System.Text.Json;
using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;

namespace Walis.Core.Security;

/// <summary>One stored secret the current key cannot read: where it lives and which row, never the value.</summary>
public sealed record UndecryptableSecret(string Table, string Item);

/// <summary>What the scan found; <see cref="Count" /> is zero when every stored secret opens with the current key.</summary>
public sealed record StoredSecretReport(IReadOnlyList<UndecryptableSecret> Undecryptable)
{
    public int Count => Undecryptable.Count;
}

/// <summary>
///     Walks every column that holds encrypted secrets and tries to decrypt each value with the current key. Startup logs the
///     result so a restore on a machine without the original <c>secret.key</c> (or a changed <c>Security:EncryptionKey</c>)
///     is noticed at once, and the admin-only reset clears exactly those values so they can be entered again.
///     Only database-neutral SQL; the dialect is used for the boolean literal alone.
/// </summary>
public sealed class StoredSecretScanner(IDbConnectionFactory connectionFactory, ISecretProtector protector)
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private const string NotificationTargetsSql =
        "SELECT Id, Name, Target AS Value FROM NotificationChannels " +
        "WHERE Kind IN ('slack', 'webhook') AND Target IS NOT NULL AND Target <> '';";

    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<StoredSecretReport> ScanAsync(CancellationToken cancellationToken = default)
    {
        var failures = new List<UndecryptableSecret>();
        using var connection = connectionFactory.Create();

        foreach (var provider in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     "SELECT Id, Name, ApiKey AS Value FROM AiProviders WHERE ApiKey IS NOT NULL;",
                     cancellationToken: cancellationToken)))
            if (!protector.TryUnprotect(provider.Value, SecretPurposes.AiProviderApiKey, out _))
                failures.Add(new UndecryptableSecret("AiProviders", provider.Name));

        foreach (var variable in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     $"SELECT Id, Name, Value FROM EnvironmentVariables WHERE IsSecret = {_dialect.Boolean(true)};",
                     cancellationToken: cancellationToken)))
            if (!protector.TryUnprotect(variable.Value, SecretPurposes.EnvironmentVariableValue, out _))
                failures.Add(new UndecryptableSecret("EnvironmentVariables", variable.Name));

        foreach (var user in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     "SELECT Id, Username AS Name, TotpSecret AS Value FROM Users WHERE TotpSecret IS NOT NULL AND TotpSecret <> '';",
                     cancellationToken: cancellationToken)))
            if (!protector.TryUnprotect(user.Value, SecretPurposes.UserTotpSecret, out _))
                failures.Add(new UndecryptableSecret("Users", user.Name));

        foreach (var build in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     "SELECT Id, '' AS Name, SecretParametersJson AS Value FROM Builds WHERE SecretParametersJson IS NOT NULL;",
                     cancellationToken: cancellationToken)))
            if (!protector.TryUnprotect(build.Value, SecretPurposes.BuildSecretParameters, out _))
                failures.Add(new UndecryptableSecret("Builds", build.Id.ToString()));

        foreach (var channel in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     NotificationTargetsSql, cancellationToken: cancellationToken)))
            if (!protector.TryUnprotect(channel.Value, SecretPurposes.NotificationChannelTarget, out _))
                failures.Add(new UndecryptableSecret("NotificationChannels", channel.Name));

        var settings = await ReadSettingsAsync(connection, cancellationToken);

        if (settings is not null)
            failures.AddRange(SettingsSecrets.FindUndecryptable(settings, protector)
                .Select(field => new UndecryptableSecret("Settings", field)));

        return new StoredSecretReport(failures);
    }

    /// <summary>
    ///     Clears every value the current key cannot read. A TOTP factor is removed entirely (secret, counter and recovery codes),
    ///     because a secret nobody can read only locks the user out; the admin tells them to enrol again. A notification channel
    ///     whose target cannot be read is disabled with an empty target, so the admin re-enters the URL instead of the dispatcher
    ///     silently skipping it.
    /// </summary>
    public async Task<StoredSecretReport> ResetUndecryptableAsync(CancellationToken cancellationToken = default)
    {
        var cleared = new List<UndecryptableSecret>();
        using var connection = connectionFactory.Create();

        foreach (var provider in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     "SELECT Id, Name, ApiKey AS Value FROM AiProviders WHERE ApiKey IS NOT NULL;",
                     cancellationToken: cancellationToken)))
        {
            if (protector.TryUnprotect(provider.Value, SecretPurposes.AiProviderApiKey, out _)) continue;

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE AiProviders SET ApiKey = NULL, UpdatedAt = @Now WHERE Id = @Id;",
                new { provider.Id, Now = DateTime.UtcNow },
                cancellationToken: cancellationToken));
            cleared.Add(new UndecryptableSecret("AiProviders", provider.Name));
        }

        foreach (var variable in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     $"SELECT Id, Name, Value FROM EnvironmentVariables WHERE IsSecret = {_dialect.Boolean(true)};",
                     cancellationToken: cancellationToken)))
        {
            if (protector.TryUnprotect(variable.Value, SecretPurposes.EnvironmentVariableValue, out _)) continue;

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE EnvironmentVariables SET Value = '', UpdatedAt = @Now WHERE Id = @Id;",
                new { variable.Id, Now = DateTime.UtcNow },
                cancellationToken: cancellationToken));
            cleared.Add(new UndecryptableSecret("EnvironmentVariables", variable.Name));
        }

        foreach (var user in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     "SELECT Id, Username AS Name, TotpSecret AS Value FROM Users WHERE TotpSecret IS NOT NULL AND TotpSecret <> '';",
                     cancellationToken: cancellationToken)))
        {
            if (protector.TryUnprotect(user.Value, SecretPurposes.UserTotpSecret, out _)) continue;

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Users SET TotpSecret = NULL, TotpEnabledAt = NULL, LastTotpCounter = NULL, RecoveryCodes = NULL WHERE Id = @Id;",
                new { user.Id },
                cancellationToken: cancellationToken));
            cleared.Add(new UndecryptableSecret("Users", user.Name));
        }

        foreach (var build in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     "SELECT Id, '' AS Name, SecretParametersJson AS Value FROM Builds WHERE SecretParametersJson IS NOT NULL;",
                     cancellationToken: cancellationToken)))
        {
            if (protector.TryUnprotect(build.Value, SecretPurposes.BuildSecretParameters, out _)) continue;

            await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE Builds SET SecretParametersJson = NULL WHERE Id = @Id;",
                new { build.Id },
                cancellationToken: cancellationToken));
            cleared.Add(new UndecryptableSecret("Builds", build.Id.ToString()));
        }

        foreach (var channel in await connection.QueryAsync<KeyedValue>(new CommandDefinition(
                     NotificationTargetsSql, cancellationToken: cancellationToken)))
        {
            if (protector.TryUnprotect(channel.Value, SecretPurposes.NotificationChannelTarget, out _)) continue;

            await connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE NotificationChannels SET Target = '', IsEnabled = {_dialect.Boolean(false)}, UpdatedAt = @Now WHERE Id = @Id;",
                new { channel.Id, Now = DateTime.UtcNow },
                cancellationToken: cancellationToken));
            cleared.Add(new UndecryptableSecret("NotificationChannels", channel.Name));
        }

        var settings = await ReadSettingsAsync(connection, cancellationToken);

        if (settings is not null)
        {
            var fields = SettingsSecrets.FindUndecryptable(settings, protector);

            if (fields.Count != 0)
            {
                SettingsSecrets.ClearUndecryptable(settings, protector);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE Settings SET Payload = @Payload, UpdatedAt = @Now WHERE Id = 1;",
                    new { Payload = JsonSerializer.Serialize(settings, SerializerOptions), Now = DateTime.UtcNow },
                    cancellationToken: cancellationToken));
                cleared.AddRange(fields.Select(field => new UndecryptableSecret("Settings", field)));
            }
        }

        return new StoredSecretReport(cleared);
    }

    /// <summary>The raw payload, without decryption, so the readable ciphertext fields stay exactly as stored when written back.</summary>
    private static async Task<WalisSettings?> ReadSettingsAsync(
        System.Data.IDbConnection connection,
        CancellationToken cancellationToken)
    {
        var payload = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Payload FROM Settings WHERE Id = 1;",
            cancellationToken: cancellationToken));

        return string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize<WalisSettings>(payload, SerializerOptions);
    }

    // Dapper needs a parameterless type: with zero rows SQLite reports every column as BLOB and matches no record constructor.
    private sealed class KeyedValue
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Value { get; set; }
    }
}
