using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>
///     The shared wrap-up after a build ends: send notifications, trigger downstream jobs. Both the local executor and remote runner reports go through here;
///     remote reports used to send no notifications, now both behave the same.
/// </summary>
public sealed class BuildCompletionService(
    IJobRepository jobs,
    NotificationDispatcher notifications,
    BuildLauncher launcher,
    ILogger<BuildCompletionService> logger)
{
    public async Task HandleAsync(
        Job job,
        Build build,
        string? previousStatus,
        bool timedOut,
        CancellationToken cancellationToken)
    {
        try
        {
            await notifications.DispatchBuildCompletedAsync(job, build, previousStatus, timedOut, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "建置 {BuildId} 的通知發送失敗", build.Id);
        }

        await TriggerDownstreamAsync(job, build, cancellationToken);
    }

    /// <summary>
    ///     Triggers downstream jobs per the job's settings. The chain depth is kept in the build parameters and stops past the cap, so a cycle like A->B->A cannot run forever.
    ///     Downstream receives WALIS_UPSTREAM_JOB/BUILD/STATUS so the script knows who called it.
    /// </summary>
    private async Task TriggerDownstreamAsync(Job job, Build build, CancellationToken cancellationToken)
    {
        var targets = DownstreamJobs.Parse(job.DownstreamJson);
        if (targets.Count == 0) return;

        var values = BuildParameters.ParseValues(build.ParametersJson);
        var depth = values.TryGetValue(BuildLauncher.ChainDepthVariable, out var raw) &&
                    int.TryParse(raw, out var parsed)
            ? parsed
            : 0;

        // Job names are operator-supplied; cleaned before they reach the console so a name cannot forge or break a log line.
        var jobName = AuditText.Clean(job.Name, 200);

        if (depth >= DownstreamJobs.MaximumChainDepth)
        {
            logger.LogWarning(
                "任務 {Job} #{Number} 的下游觸發已達鏈長上限 {Depth}，停止往下觸發",
                jobName, build.Number, DownstreamJobs.MaximumChainDepth);
            return;
        }

        foreach (var target in targets.Where(target => DownstreamJobs.ShouldTrigger(target, build.Status)))
        {
            var downstream = await jobs.GetJobByNameAsync(target.JobName, cancellationToken);
            if (downstream is null)
            {
                logger.LogWarning("任務 {Job} 的下游「{Target}」不存在，略過", jobName, AuditText.Clean(target.JobName, 200));
                continue;
            }

            // Automatic triggers have no interactive identity; permission is limited by the upstream job's run threshold so an operator job
            // cannot become the entry point to an admin deployment job. The upstream job is re-read to cover being downgraded or deleted mid-run.
            var currentUpstream = await jobs.GetJobAsync(job.Id, cancellationToken);
            if (currentUpstream is null ||
                !JobAccess.CanRun(job.RunRole, downstream) || !JobAccess.CanView(job.RunRole, downstream) ||
                !JobAccess.CanRun(currentUpstream.RunRole, downstream) ||
                !JobAccess.CanView(currentUpstream.RunRole, downstream))
            {
                logger.LogWarning("任務 {Job} 沒有觸發下游「{Target}」的權限，略過", jobName, AuditText.Clean(target.JobName, 200));
                continue;
            }

            var result = await launcher.LaunchAsync(
                downstream,
                $"downstream:{job.Name}#{build.Number}",
                cancellationToken,
                new BuildLaunchOptions
                {
                    SystemValues = new Dictionary<string, string>
                    {
                        [DownstreamJobs.UpstreamJobVariable] = job.Name,
                        [DownstreamJobs.UpstreamBuildVariable] = build.Number.ToString(),
                        [DownstreamJobs.UpstreamStatusVariable] = build.Status,
                        [BuildLauncher.ChainDepthVariable] = (depth + 1).ToString()
                    }
                });

            // BuildLauncher writes the audit record (the "downstream:" trigger source names the upstream build).
            if (result.Error is not null)
                logger.LogWarning("任務 {Job} 無法觸發下游「{Target}」：{Error}", jobName,
                    AuditText.Clean(target.JobName, 200), result.Error);
        }
    }
}