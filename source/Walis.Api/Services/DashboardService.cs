using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

public sealed class DashboardService(
    IJobRepository jobs,
    IRunnerRepository runners,
    ISettingsRepository settings)
{
    public async Task<DashboardSummary> GetAsync(string role, CancellationToken cancellationToken)
    {
        var configuration = await settings.GetAsync(cancellationToken);
        var zone = TimeZoneResolver.Resolve(configuration.TimeZone);

        var allJobs = await jobs.ListJobsAsync(cancellationToken);
        var visibleJobs = allJobs.Where(job => JobAccess.CanView(role, job)).ToList();
        JobScheduleEnricher.Apply(visibleJobs, zone);

        var (counts, trend, recent) = role == UserRole.Admin
            ? await LoadAdminBuildSummaryAsync(cancellationToken)
            : await LoadVisibleBuildSummaryAsync(visibleJobs, cancellationToken);
        var allRunners = await runners.ListAsync(cancellationToken);
        var runnerList = role == UserRole.Admin
            ? allRunners
            : allRunners.Select(PresentLimitedRunner).ToList();

        var finishedLast7Days = counts.SucceededLast7Days + counts.FailuresLast7Days;

        return new DashboardSummary
        {
            TotalJobs = visibleJobs.Count,
            EnabledJobs = visibleJobs.Count(job => !job.IsDisabled),
            QueuedBuilds = counts.QueuedBuilds,
            RunningBuilds = counts.RunningBuilds,
            SuccessfulBuilds = counts.SuccessfulBuilds,
            FailedBuilds = counts.FailedBuilds,
            BuildsLast7Days = counts.BuildsLast7Days,
            FailuresLast7Days = counts.FailuresLast7Days,
            SuccessRateLast7Days = finishedLast7Days == 0
                ? 100
                : (int)Math.Round(counts.SucceededLast7Days * 100.0 / finishedLast7Days, MidpointRounding.AwayFromZero),
            AverageDurationSeconds = Math.Round(counts.AverageDurationSeconds, 1),
            AverageQueueSeconds = Math.Round(counts.AverageQueueSeconds, 1),
            ActiveSchedules = visibleJobs.Count(job => !job.IsDisabled && job.TriggerKind == TriggerMode.Cron),
            Trend = trend,
            RecentBuilds = recent,
            Activity = BuildActivity(recent),
            Runners = runnerList,
            Jobs = visibleJobs
        };
    }

    private async Task<(BuildCounts Counts, IReadOnlyList<DailyBuildStat> Trend, IReadOnlyList<Build> Recent)>
        LoadAdminBuildSummaryAsync(CancellationToken cancellationToken)
    {
        return (await jobs.GetBuildCountsAsync(cancellationToken),
            await jobs.GetTrendAsync(7, cancellationToken),
            await jobs.ListBuildsAsync(new BuildQuery(null, null, null, null, 8), cancellationToken));
    }

    /// <summary>
    ///     Every dashboard number for non-admins is derived from the builds of visible jobs, so hidden jobs cannot leak through statistics, trends or recent activity.
    ///     At most 200 builds are queried per job, matching the existing limit of the build list endpoint.
    /// </summary>
    private async Task<(BuildCounts Counts, IReadOnlyList<DailyBuildStat> Trend, IReadOnlyList<Build> Recent)>
        LoadVisibleBuildSummaryAsync(IReadOnlyList<Job> visibleJobs, CancellationToken cancellationToken)
    {
        var perJob = await Task.WhenAll(visibleJobs.Select(job =>
            jobs.ListBuildsAsync(new BuildQuery(null, null, job.Id, null, 200), cancellationToken)));
        var builds = perJob.SelectMany(list => list).OrderByDescending(build => build.QueuedAt).ToList();
        var since = DateTime.UtcNow.AddDays(-7);
        var recentBuilds = builds.Where(build => build.QueuedAt >= since).ToList();
        var durations = recentBuilds
            .Where(build => build.StartedAt is not null && build.FinishedAt is not null)
            .Select(build => (build.FinishedAt!.Value - build.StartedAt!.Value).TotalSeconds)
            .ToList();
        var queues = recentBuilds
            .Where(build => build.StartedAt is not null)
            .Select(build => (build.StartedAt!.Value - build.QueuedAt).TotalSeconds)
            .ToList();

        var counts = new BuildCounts(
            builds.Count(build => build.Status == BuildStatus.Queued),
            builds.Count(build => build.Status == BuildStatus.Running),
            builds.Count(build => build.Status == BuildStatus.Succeeded),
            builds.Count(build => build.Status == BuildStatus.Failed),
            recentBuilds.Count,
            recentBuilds.Count(build => build.Status == BuildStatus.Failed),
            recentBuilds.Count(build => build.Status == BuildStatus.Succeeded),
            durations.Count == 0 ? 0 : durations.Average(),
            queues.Count == 0 ? 0 : queues.Average());

        var firstDay = DateTime.UtcNow.Date.AddDays(-6);
        var byDay = recentBuilds
            .Where(build => build.QueuedAt >= firstDay)
            .GroupBy(build => build.QueuedAt.ToString("yyyy-MM-dd"))
            .ToDictionary(group => group.Key, group => new DailyBuildStat(
                group.Key,
                group.Count(),
                group.Count(build => build.Status == BuildStatus.Succeeded),
                group.Count(build => build.Status == BuildStatus.Failed)), StringComparer.Ordinal);
        var trend = Enumerable.Range(0, 7)
            .Select(offset => firstDay.AddDays(offset).ToString("yyyy-MM-dd"))
            .Select(date => byDay.GetValueOrDefault(date) ?? new DailyBuildStat(date, 0, 0, 0))
            .ToList();

        return (counts, trend, builds.Take(8).ToList());
    }

    private static IReadOnlyList<ActivityEntry> BuildActivity(IReadOnlyList<Build> builds)
    {
        return builds
            .Take(6)
            .Select(build => new ActivityEntry(
                build.Status,
                build.JobName,
                build.Number,
                build.RunnerName,
                build.FinishedAt ?? build.StartedAt ?? build.QueuedAt))
            .ToList();
    }

    /// <summary>
    ///     CurrentJobName directly reveals the name of an ACL-protected job; OS, version and tags are management topology too.
    ///     DashboardSummary uses the strongly typed Runner, so a copy with the sensitive fields cleared is returned.
    /// </summary>
    private static Runner PresentLimitedRunner(Runner runner)
    {
        return new Runner
        {
            Id = runner.Id,
            Name = runner.Name,
            TotalSlots = runner.TotalSlots,
            IsPaused = runner.IsPaused,
            IsLocal = runner.IsLocal,
            LastSeenAt = runner.LastSeenAt,
            CreatedAt = runner.CreatedAt,
            UsedSlots = runner.UsedSlots,
            Status = runner.Status
        };
    }
}