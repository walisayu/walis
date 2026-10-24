using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Scheduling;

namespace Walis.Api.Services;

/// <summary>
///     Runs every minute: reports the local runner heartbeat, detects action pack directory changes, triggers due Cron jobs,
///     detects offline runners, and periodically cleans up old build records by retention days.
/// </summary>
public sealed class SchedulerWorker(
    IJobRepository jobs,
    IRunnerRepository runners,
    ISettingsRepository settings,
    ISecurityRepository security,
    IAuditRepository audit,
    IAiRepository ai,
    ArtifactStore artifacts,
    BuildLauncher launcher,
    NotificationDispatcher notifications,
    CorsOriginStore corsOrigins,
    McpAccessStore mcpAccess,
    ILeaseRepository leases,
    NodeIdentity node,
    ActionPackProvider actionPacks,
    ILogger<SchedulerWorker> logger) : BackgroundService
{
    /// <summary>The lease duration. Longer than the one-minute check cycle, so the job does not change hands every round.</summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(3);

    private readonly HashSet<string> _reportedOffline = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastPurgeUtc = DateTime.MinValue;
    private bool _wasLeader = true;

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
                logger.LogError(exception, "排程檢查失敗");
            }

            await DelayToNextMinuteAsync(stoppingToken);
        }
    }

    private async Task TickAsync(CancellationToken cancellationToken)
    {
        // The heartbeat is each node's own business and is reported even without the lease.
        await runners.HeartbeatAsync(Runner.LocalName, cancellationToken);

        // The action pack directory is local to each node; after a git pull the change is picked up here and reloaded without an API call.
        try
        {
            if (actionPacks.ReloadIfChanged()) logger.LogInformation("動作套件目錄有變動，已重新載入");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "重新載入動作套件失敗");
        }

        var configuration = await settings.GetAsync(cancellationToken);

        // The CORS policy is registered at startup, and a settings change only updates the node that handled that request;
        // every node re-reads it itself, so a setting changed on another node catches up within a minute.
        corsOrigins.Update(configuration.CorsOrigins);
        mcpAccess.Update(configuration.McpAllowedAddresses);

        if (!await IsLeaderAsync(cancellationToken)) return;
        var zone = TimeZoneResolver.Resolve(configuration.TimeZone);
        var localNow = TimeZoneResolver.ToLocal(DateTime.UtcNow, zone);

        await TriggerDueJobsAsync(localNow, cancellationToken);
        await ReportOfflineRunnersAsync(cancellationToken);
        await PurgeAsync(configuration.LogRetentionDays, configuration.AuditRetentionDays, cancellationToken);
    }

    /// <summary>
    ///     Cron triggers, offline detection and cleanup should happen once per cluster, otherwise the same minute would spawn several builds.
    ///     A single node (SQLite) does not compete and simply counts itself as the owner.
    /// </summary>
    private async Task<bool> IsLeaderAsync(CancellationToken cancellationToken)
    {
        if (!node.IsClustered) return true;

        var acquired = await leases.TryAcquireAsync(
            LeaseNames.Scheduler, node.Name, LeaseDuration, cancellationToken);

        if (acquired != _wasLeader)
        {
            logger.LogInformation(
                acquired ? "節點 {Node} 接手排程" : "節點 {Node} 交出排程", node.Name);
            _wasLeader = acquired;
        }

        return acquired;
    }

    private async Task TriggerDueJobsAsync(DateTime localNow, CancellationToken cancellationToken)
    {
        var minuteStartUtc = DateTime.UtcNow.AddSeconds(-DateTime.UtcNow.Second)
            .AddTicks(-(DateTime.UtcNow.Ticks % TimeSpan.TicksPerSecond));

        foreach (var job in await jobs.ListJobsAsync(cancellationToken))
        {
            if (job.IsDisabled || job.TriggerKind != TriggerMode.Cron) continue;

            // The name is operator-supplied; cleaned so it cannot forge or break a console log line.
            var jobName = AuditText.Clean(job.Name, 200);

            if (!CronExpression.TryParse(job.Cron, out var cron, out var error))
            {
                logger.LogWarning("任務 {Job} 的 Cron 設定無效：{Error}", jobName, error);
                continue;
            }

            if (!cron!.Matches(localNow)) continue;

            if (await jobs.HasBuildQueuedSinceAsync(job.Id, minuteStartUtc, cancellationToken)) continue;

            // BuildLauncher writes the audit record (actor "system", summary says it was the schedule).
            var result = await launcher.LaunchAsync(job, "schedule", cancellationToken);
            if (result.Error is not null)
                logger.LogWarning("排程無法啟動任務 {Job}：{Error}", jobName, result.Error);
            else
                logger.LogInformation("排程已啟動任務 {Job} #{Number}", jobName, result.Build!.Number);
        }
    }

    private async Task ReportOfflineRunnersAsync(CancellationToken cancellationToken)
    {
        foreach (var runner in await runners.ListAsync(cancellationToken))
            if (runner.Status == RunnerStatus.Offline)
            {
                if (_reportedOffline.Add(runner.Name))
                    await notifications.DispatchAsync(
                        new NotificationMessage(
                            NotificationEventKind.RunnerOffline,
                            $"執行器離線：{runner.Name}",
                            $"執行器 {runner.Name} 已超過 {RunnerRepository.OfflineAfter.TotalMinutes:0} 分鐘沒有回報心跳。",
                            null),
                        cancellationToken);
            }
            else
            {
                _reportedOffline.Remove(runner.Name);
            }
    }

    private async Task PurgeAsync(int retentionDays, int auditRetentionDays, CancellationToken cancellationToken)
    {
        if (DateTime.UtcNow - _lastPurgeUtc < TimeSpan.FromHours(6)) return;

        var owner = $"purge:{Guid.NewGuid():N}";
        var operationLease = await RenewingDatabaseLease.TryAcquireAsync(
            leases,
            LeaseNames.BackupOperation,
            owner,
            cancellationToken);

        // Backup and restore need a stable database/artifact set. Skipping this maintenance pass is
        // harmless; the scheduler retries later instead of deleting files underneath an archive.
        if (operationLease is null) return;

        await using (operationLease)
        {
            _lastPurgeUtc = DateTime.UtcNow;
            var removed = await jobs.PurgeBuildsAsync(retentionDays, operationLease.Token);

            foreach (var buildId in removed)
                try
                {
                    artifacts.Remove(buildId);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "刪除建置 {BuildId} 的產出檔案失敗", buildId);
                }

            var expiredSessions = await security.PurgeExpiredSessionsAsync(operationLease.Token);
            // The audit log has its own, longer retention: it must outlive the build history it describes.
            var oldAudit = await audit.PurgeAsync(auditRetentionDays, operationLease.Token);
            var oldAiRequests = await ai.PurgeRequestsAsync(retentionDays, operationLease.Token);

            if (removed.Count != 0 || expiredSessions != 0 || oldAudit != 0 || oldAiRequests != 0)
                logger.LogInformation(
                    "已清除 {Builds} 筆建置、{Sessions} 個過期登入、{Audit} 筆稽核與 {Ai} 筆 AI 呼叫紀錄",
                    removed.Count,
                    expiredSessions,
                    oldAudit,
                    oldAiRequests);
        }
    }

    private static async Task DelayToNextMinuteAsync(CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        var next = now.AddMinutes(1).AddSeconds(-now.Second).AddMilliseconds(-now.Millisecond);
        var delay = next - now;

        if (delay < TimeSpan.FromSeconds(1)) delay = TimeSpan.FromSeconds(1);

        try
        {
            await Task.Delay(delay, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The service is stopping; just leave.
        }
    }
}