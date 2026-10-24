using Walis.Core.Models;
using Walis.Core.Scheduling;

namespace Walis.Api.Services;

/// <summary>Fills in each job's next trigger time from its Cron setting (returned as UTC).</summary>
public static class JobScheduleEnricher
{
    public static void Apply(IEnumerable<Job> jobs, TimeZoneInfo zone)
    {
        var localNow = TimeZoneResolver.ToLocal(DateTime.UtcNow, zone);

        foreach (var job in jobs) job.NextRunAt = GetNextRun(job, zone, localNow);
    }

    public static DateTime? GetNextRun(Job job, TimeZoneInfo zone, DateTime localNow)
    {
        if (job.IsDisabled ||
            job.TriggerKind != TriggerMode.Cron ||
            !CronExpression.TryParse(job.Cron, out var cron, out _))
            return null;

        var next = cron!.GetNextOccurrence(localNow);
        return next is null ? null : TimeZoneResolver.ToUtc(next.Value, zone);
    }
}