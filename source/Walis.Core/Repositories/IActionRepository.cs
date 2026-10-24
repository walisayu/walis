using Walis.Core.Actions;

namespace Walis.Core.Repositories;

public interface IActionRepository
{
    /// <summary>Built-in actions plus the installed custom actions.</summary>
    Task<IReadOnlyList<ActionDefinition>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyDictionary<string, ActionDefinition>> MapAsync(CancellationToken cancellationToken = default);

    Task<ActionDefinition> InstallAsync(ActionDefinition definition, CancellationToken cancellationToken = default);

    Task<bool> UninstallAsync(string id, CancellationToken cancellationToken = default);
}