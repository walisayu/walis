using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface ISettingsRepository
{
    Task<WalisSettings> GetAsync(CancellationToken cancellationToken = default);

    Task<WalisSettings> SaveAsync(WalisSettings settings, CancellationToken cancellationToken = default);
}