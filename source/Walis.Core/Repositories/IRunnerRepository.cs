using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface IRunnerRepository
{
    Task<IReadOnlyList<Runner>> ListAsync(CancellationToken cancellationToken = default);

    Task<Runner?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Runner?> GetByNameAsync(string name, CancellationToken cancellationToken = default);

    Task<Runner> CreateAsync(RunnerDraft draft, CancellationToken cancellationToken = default);

    Task<Runner?> UpdateAsync(Guid id, RunnerDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Deletes a remote runner and, in the same transaction, disables every API key bound to its name.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Same as <see cref="DeleteAsync" /> but reports how many keys were disabled; null when nothing was deleted.</summary>
    Task<RunnerDeletion?> DeleteWithKeysAsync(Guid id, CancellationToken cancellationToken = default);

    Task<Runner?> SetPausedAsync(Guid id, bool paused, CancellationToken cancellationToken = default);

    Task<bool> HeartbeatAsync(string name, CancellationToken cancellationToken = default);

    Task<bool> ExistsAsync(string name, CancellationToken cancellationToken = default);
}