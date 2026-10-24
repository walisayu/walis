using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Scheduling;

namespace Walis.Api.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/dashboard", async (
                    HttpContext context,
                    DashboardService dashboard,
                    CancellationToken cancellationToken) =>
                Results.Ok(await dashboard.GetAsync(RequestAccess.Role(context), cancellationToken)))
            .WithTags("Dashboard");

        // Schedule timeline: a window starting at 00:00 of a given day in the configured time zone and spanning days days.
        // Cron jobs return their trigger times within the window (for estimates); every job returns the builds that actually ran in the window (click to see the log) and its recent average duration.
        // Manual/push jobs are listed only when they have builds in the window, so a thousand jobs do not all become empty rows.
        app.MapGet("/api/schedules/timeline", async (
                    string? date,
                    int? days,
                    HttpContext context,
                    IJobRepository jobs,
                    ISettingsRepository settings,
                    CancellationToken cancellationToken) =>
            {
                var span = Math.Clamp(days ?? 1, 1, 31);
                var role = RequestAccess.Role(context);
                var configuration = await settings.GetAsync(cancellationToken);
                var zone = TimeZoneResolver.Resolve(configuration.TimeZone);

                DateTime localStart;
                if (string.IsNullOrWhiteSpace(date))
                {
                    localStart = TimeZoneResolver.ToLocal(DateTime.UtcNow, zone).Date;
                }
                else if (DateOnly.TryParseExact(date, "yyyy-MM-dd", out var parsed))
                {
                    localStart = parsed.ToDateTime(TimeOnly.MinValue);
                }
                else
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["date"] = ["日期格式是 yyyy-MM-dd。"] });
                }

                var localEnd = localStart.AddDays(span);
                var from = TimeZoneResolver.ToUtc(localStart, zone);
                var to = TimeZoneResolver.ToUtc(localEnd, zone);

                var averages = await jobs.GetAverageDurationsAsync(DateTime.UtcNow.AddDays(-30), cancellationToken);
                var builds = (await jobs.ListBuildsBetweenAsync(from, to, cancellationToken: cancellationToken))
                    .GroupBy(build => build.JobId)
                    .ToDictionary(group => group.Key, group => group.ToList());

                var rows = (await jobs.ListJobsAsync(cancellationToken))
                    // Paused jobs are listed too (drawn greyed out), otherwise the list has a row but the timeline row vanishes, which looks broken;
                    // jobs whose cron cannot be parsed are listed with an error as well, so the expression problem is visible.
                    .Where(job => JobAccess.CanView(role, job) &&
                                  (job.TriggerKind == TriggerMode.Cron || builds.ContainsKey(job.Id)))
                    .Select(job =>
                    {
                        var isCron = job.TriggerKind == TriggerMode.Cron;
                        CronExpression? cron = null;
                        string? cronError = null;
                        var parsed = isCron && CronExpression.TryParse(job.Cron, out cron, out cronError);
                        var starts = parsed
                            ? cron!.GetOccurrences(localStart, localEnd).Select(local => TimeZoneResolver.ToUtc(local, zone)).ToList()
                            : [];
                        return new
                        {
                            job.Id,
                            job.Name,
                            job.Project,
                            job.TriggerKind,
                            Cron = isCron ? job.Cron : null,
                            job.IsDisabled,
                            CronError = isCron && !parsed ? cronError : null,
                            AverageSeconds = averages.TryGetValue(job.Id, out var seconds) ? seconds : (double?)null,
                            job.LastBuildSeconds,
                            Starts = starts,
                            Builds = builds.TryGetValue(job.Id, out var list)
                                ? list.Select(build => new
                                {
                                    build.Id,
                                    build.Number,
                                    build.Status,
                                    build.TriggeredBy,
                                    build.QueuedAt,
                                    build.StartedAt,
                                    build.FinishedAt,
                                    build.DurationSeconds,
                                    build.MatrixLabel
                                }).ToList()
                                : []
                        };
                    })
                    .OrderBy(row => row.Name, StringComparer.Ordinal)
                    .ToList();

                return Results.Ok(new
                {
                    From = from,
                    To = to,
                    Date = localStart.ToString("yyyy-MM-dd"),
                    Days = span,
                    TimeZone = zone.Id,
                    Jobs = rows
                });
            })
            .WithTags("Dashboard");

        return app;
    }
}