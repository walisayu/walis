using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface IAuditRepository
{
    Task AppendAsync(AuditEntry entry, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditEntry>> ListAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>Filtered, database-paged query ordered newest first with Id as the tie-breaker, so paging through one instant is stable.</summary>
    Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken = default);

    /// <summary>Summarizes recent security events and 24-hour threat counts for the Security Center.</summary>
    Task<SecurityAuditSummary> GetSecuritySummaryAsync(
        DateTime sinceUtc,
        int limit = 100,
        CancellationToken cancellationToken = default);

    Task<int> PurgeAsync(int retentionDays, CancellationToken cancellationToken = default);
}