using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface IEnvironmentVariableRepository
{
    Task<IReadOnlyList<EnvironmentVariable>> ListAsync(CancellationToken cancellationToken = default);

    Task<EnvironmentVariable?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<EnvironmentVariable> CreateAsync(EnvironmentVariableDraft draft,
        CancellationToken cancellationToken = default);

    Task<EnvironmentVariable?> UpdateAsync(Guid id, EnvironmentVariableDraft draft,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Gets the variables to inject for a job. Non-secret global variables apply automatically; secret variables must be bound explicitly to the job
    ///     and be at least four characters, so they can be masked reliably without replacing common single characters with lots of masks.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveForJobAsync(Guid jobId,
        CancellationToken cancellationToken = default);

    /// <summary>Gets global values for trusted service integrations; the result must never be handed to user-provided scripts.</summary>
    Task<IReadOnlyDictionary<string, string>> ResolveGlobalsForTrustedServiceAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Gets every secret value, for masking build output.</summary>
    Task<IReadOnlyList<string>> ListSecretValuesAsync(CancellationToken cancellationToken = default);

    /// <summary>Gets only the secret values explicitly bound to the job, for masking output on local and remote runners.</summary>
    Task<IReadOnlyList<string>> ListSecretValuesForJobAsync(Guid jobId, CancellationToken cancellationToken = default);
}