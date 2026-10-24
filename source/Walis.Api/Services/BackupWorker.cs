using Walis.Core.Repositories;
using Walis.Core.Scheduling;

namespace Walis.Api.Services;

/// <summary>Checks the backup schedule every minute; does nothing when the setting is disabled.</summary>
public sealed class BackupWorker(
    ISettingsRepository settings,
    IBackupRepository runs,
    BackupService backups,
    ILeaseRepository leases,
    NodeIdentity node,
    ILogger<BackupWorker> logger) : BackgroundService
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);

    private DateTime _lastPurgeUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "備份排程檢查失敗");
            }

            var now = DateTime.UtcNow;
            var next = now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);

            try
            {
                await Task.Delay(next - now, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        // With several nodes the cluster should back up only once; the lease decides which node does it.
        if (node.IsClustered &&
            !await leases.TryAcquireAsync(LeaseNames.Backup, node.Name, LeaseDuration, cancellationToken))
            return;

        var configuration = await settings.GetAsync(cancellationToken);

        if (DateTime.UtcNow - _lastPurgeUtc > TimeSpan.FromHours(6))
        {
            _lastPurgeUtc = DateTime.UtcNow;
            await runs.PurgeAsync(configuration.LogRetentionDays, cancellationToken);
        }

        if (!configuration.BackupEnabled || backups.IsRunning) return;

        if (!CronExpression.TryParse(configuration.BackupCron, out var cron, out var error))
        {
            logger.LogWarning("備份的 Cron 設定無效：{Error}", error);
            return;
        }

        var zone = TimeZoneResolver.Resolve(configuration.TimeZone);
        var localNow = TimeZoneResolver.ToLocal(DateTime.UtcNow, zone);

        if (!cron!.Matches(localNow)) return;

        logger.LogInformation("排程觸發備份");
        await backups.RunAsync("schedule", cancellationToken);
    }
}