using System.Globalization;
using System.Text.Json;
using Dapper;
using Walis.Core.Database;
using Walis.Core.Plugins;

namespace Walis.Core.Repositories;

public sealed class PluginRepository(IDbConnectionFactory connectionFactory) : IPluginRepository
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<Plugin>> ListAsync(CancellationToken cancellationToken = default)
    {
        return await QueryAsync("SELECT * FROM Plugins ORDER BY PluginId;", null, cancellationToken);
    }

    public async Task<IReadOnlyList<Plugin>> ListEnabledAsync(CancellationToken cancellationToken = default)
    {
        return await QueryAsync(
            $"SELECT * FROM Plugins WHERE IsDisabled = {_dialect.Boolean(false)} ORDER BY PluginId;",
            null,
            cancellationToken);
    }

    public async Task<Plugin?> GetAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        return (await QueryAsync(
            "SELECT * FROM Plugins WHERE PluginId = @PluginId;",
            new { PluginId = pluginId },
            cancellationToken)).FirstOrDefault();
    }

    public async Task<Plugin> InstallAsync(PluginDraft draft, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;

        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            _dialect.Upsert(
                "Plugins",
                [
                    UpsertColumn.Parameter("Id"),
                    UpsertColumn.Parameter("PluginId"),
                    UpsertColumn.Parameter("Manifest"),
                    UpsertColumn.Parameter("Code"),
                    UpsertColumn.Parameter("IsDisabled"),
                    UpsertColumn.Parameter("IsTrusted"),
                    new UpsertColumn("InstalledAt", "@Now"),
                    new UpsertColumn("UpdatedAt", "@Now")
                ],
                ["PluginId"],
                ["Manifest", "Code", "IsTrusted", "UpdatedAt"]),
            new
            {
                Id = Guid.NewGuid(),
                PluginId = draft.Manifest.Id,
                IsDisabled = false,
                Manifest = JsonSerializer.Serialize(draft.Manifest, SerializerOptions),
                draft.Code,
                draft.IsTrusted,
                Now = now
            },
            cancellationToken: cancellationToken));

        return (await GetAsync(draft.Manifest.Id, cancellationToken))!;
    }

    public async Task<Plugin?> SetDisabledAsync(
        string pluginId,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Plugins SET IsDisabled = @Disabled, UpdatedAt = @Now WHERE PluginId = @PluginId;",
            new { PluginId = pluginId, Disabled = disabled, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        return await GetAsync(pluginId, cancellationToken);
    }

    public async Task<Plugin?> SetTrustedAsync(
        string pluginId,
        bool trusted,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE Plugins SET IsTrusted = @Trusted, UpdatedAt = @Now WHERE PluginId = @PluginId;",
            new { PluginId = pluginId, Trusted = trusted, Now = DateTime.UtcNow },
            cancellationToken: cancellationToken));

        return await GetAsync(pluginId, cancellationToken);
    }

    public async Task<bool> UninstallAsync(string pluginId, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Plugins WHERE PluginId = @PluginId;",
            new { PluginId = pluginId },
            cancellationToken: cancellationToken));

        return affected != 0;
    }

    private async Task<List<Plugin>> QueryAsync(string sql, object? parameters, CancellationToken cancellationToken)
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters,
            cancellationToken: cancellationToken));

        var plugins = new List<Plugin>();

        foreach (var row in rows)
        {
            var manifest = JsonSerializer.Deserialize<PluginManifest>((string)row.Manifest, SerializerOptions);

            if (manifest is null) continue;

            plugins.Add(new Plugin
            {
                Id = Guid.Parse((string)row.Id),
                PluginId = (string)row.PluginId,
                Manifest = manifest,
                Code = (string)row.Code,
                IsDisabled = Convert.ToInt64(row.IsDisabled) != 0,
                IsTrusted = Convert.ToInt64(row.IsTrusted) != 0,
                InstalledAt = DateTime.Parse((string)row.InstalledAt, null, DateTimeStyles.RoundtripKind),
                UpdatedAt = DateTime.Parse((string)row.UpdatedAt, null, DateTimeStyles.RoundtripKind)
            });
        }

        return plugins;
    }
}