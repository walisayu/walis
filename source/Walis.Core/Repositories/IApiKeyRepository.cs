using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface IApiKeyRepository
{
    Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a key and returns the plaintext token; the plaintext is never stored, only its hash.</summary>
    Task<ApiKeyIssued> CreateAsync(ApiKeyDraft draft, string? createdBy, CancellationToken cancellationToken = default);

    /// <summary>Finds a usable key by token; disabled or expired keys always return null.</summary>
    Task<ApiKey?> AuthenticateAsync(string token, CancellationToken cancellationToken = default);

    Task<ApiKey?> SetDisabledAsync(Guid id, bool disabled, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}