namespace Walis.Core.Models;

public sealed record DailyBuildStat(string Date, int Total, int Succeeded, int Failed);

public sealed record ActivityEntry(
    string Kind,
    string JobName,
    int? BuildNumber,
    string? RunnerName,
    DateTime OccurredAt);

public sealed class DashboardSummary
{
    public long TotalJobs { get; init; }
    public long EnabledJobs { get; init; }
    public long QueuedBuilds { get; init; }
    public long RunningBuilds { get; init; }
    public long SuccessfulBuilds { get; init; }
    public long FailedBuilds { get; init; }

    public long BuildsLast7Days { get; init; }
    public long FailuresLast7Days { get; init; }
    public int SuccessRateLast7Days { get; init; }
    public double AverageDurationSeconds { get; init; }
    public double AverageQueueSeconds { get; init; }
    public long ActiveSchedules { get; init; }

    public IReadOnlyList<DailyBuildStat> Trend { get; init; } = [];
    public IReadOnlyList<Build> RecentBuilds { get; init; } = [];
    public IReadOnlyList<ActivityEntry> Activity { get; init; } = [];
    public IReadOnlyList<Runner> Runners { get; init; } = [];
    public IReadOnlyList<Job> Jobs { get; init; } = [];
}