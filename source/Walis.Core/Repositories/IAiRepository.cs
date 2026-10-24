using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface IAiRepository
{
    Task<IReadOnlyList<AiProvider>> ListProvidersAsync(CancellationToken cancellationToken = default);

    Task<AiProvider?> GetProviderAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Gets the provider to use: the default first, otherwise any available one.</summary>
    Task<AiProvider?> ResolveProviderAsync(Guid? id, CancellationToken cancellationToken = default);

    Task<AiProvider?> UpdateProviderAsync(Guid id, AiProviderDraft draft,
        CancellationToken cancellationToken = default);

    Task AppendRequestAsync(AiRequestLogEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiRequestLogEntry>> ListRequestsAsync(int limit, CancellationToken cancellationToken = default);

    Task<int> PurgeRequestsAsync(int retentionDays, CancellationToken cancellationToken = default);
}