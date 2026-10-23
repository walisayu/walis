using System.Text.Json;
using Dapper;
using Walis.Core.Actions;
using Walis.Core.Database;
using Walis.Core.Plugins;

namespace Walis.Core.Repositories;

public sealed class ActionRepository(
    IDbConnectionFactory connectionFactory,
    IPluginRepository plugins,
    IActionPackSource? packs = null)
    : IActionRepository
{
    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<ActionDefinition>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var manifests = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT Manifest FROM ActionDefinitions ORDER BY Id;",
            cancellationToken: cancellationToken));

        var custom = new List<ActionDefinition>();

        foreach (var manifest in manifests)
        {
            var definition = JsonSerializer.Deserialize<ActionDefinition>(manifest, ActionManifest.SerializerOptions);

            if (definition is not null)
            {
                definition.IsBuiltIn = false;
                custom.Add(definition);
            }
        }

        // Plugin-provided actions go into the same list so the flow editor does not have to handle two sources.
        var fromPlugins = (await plugins.ListEnabledAsync(cancellationToken))
            .Where(plugin => plugin.Manifest.Kind == PluginKinds.Action)
            .Select(plugin => plugin.Manifest.ToActionDefinition());

        // Order is precedence: built-in > pack > database-installed > plugin; only the first of a duplicate id is kept so the flow editor never shows two identical ones.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var merged = new List<ActionDefinition>();
        foreach (var definition in BuiltInActions.All.Concat(packs?.Definitions ?? []).Concat(custom).Concat(fromPlugins))
        {
            if (seen.Add(definition.Id))
            {
                merged.Add(definition);
            }
        }

        return merged;
    }

    public async Task<IReadOnlyDictionary<string, ActionDefinition>> MapAsync(
        CancellationToken cancellationToken = default)
    {
        return (await ListAsync(cancellationToken)).ToDictionary(definition => definition.Id, StringComparer.Ordinal);
    }

    public async Task<ActionDefinition> InstallAsync(ActionDefinition definition,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            _dialect.Upsert(
                "ActionDefinitions",
                [
                    UpsertColumn.Parameter("Id"),
                    UpsertColumn.Parameter("Manifest"),
                    UpsertColumn.Parameter("InstalledAt")
                ],
                ["Id"],
                ["Manifest", "InstalledAt"]),
            new
            {
                definition.Id,
                Manifest = JsonSerializer.Serialize(definition, ActionManifest.SerializerOptions),
                InstalledAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return definition;
    }

    public async Task<bool> UninstallAsync(string id, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM ActionDefinitions WHERE Id = @Id;",
            new { Id = id },
            cancellationToken: cancellationToken));
        return affected != 0;
    }
}