namespace Walis.Core.Repositories;

/// <summary>
///     When several nodes share a database, work that "the whole cluster should do only once", like scheduling and cleanup, is assigned by lease.
///     Leases expire, so another node naturally takes over after the holder dies, without an extra coordination service.
/// </summary>
public interface ILeaseRepository
{
    /// <summary>Acquires or renews; true means this node is responsible for this round.</summary>
    Task<bool> TryAcquireAsync(string name, string owner, TimeSpan duration,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Renews only a lease that is still valid and still held by the same owner. Unlike TryAcquireAsync, an expired lease
    ///     is not revived even with the same owner string, so a long-running operation can prove the lease was never interrupted.
    /// </summary>
    Task<bool> RenewAsync(string name, string owner, TimeSpan duration, CancellationToken cancellationToken = default);

    Task ReleaseAsync(string name, string owner, CancellationToken cancellationToken = default);

    Task<string?> GetOwnerAsync(string name, CancellationToken cancellationToken = default);
}

public static class LeaseNames
{
    /// <summary>Cron triggers, offline detection and old-data cleanup.</summary>
    public const string Scheduler = "scheduler";

    /// <summary>Scheduled backups.</summary>
    public const string Backup = "backup";

    /// <summary>Blocks any build from entering Running while the database is being restored.</summary>
    public const string Maintenance = "maintenance";

    /// <summary>The cluster-wide mutual exclusion shared by manual/scheduled backups and restores.</summary>
    public const string BackupOperation = "backup-operation";
}