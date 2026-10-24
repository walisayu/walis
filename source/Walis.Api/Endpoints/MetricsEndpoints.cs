using System.Globalization;
using System.Text;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

/// <summary>
///     Metrics in Prometheus format. Living at /metrics instead of /api/metrics only follows the Prometheus convention;
///     AuthenticationMiddleware still lists it as admin-only, so the scraper must carry an admin API key
///     (the content is only aggregated numbers, but job names and runner status still count as internal information).
/// </summary>
public static class MetricsEndpoints
{
    public static IEndpointRouteBuilder MapMetricsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/metrics", async (
            IJobRepository jobs,
            IRunnerRepository runners,
            SecurityMetrics security,
            CancellationToken cancellationToken) =>
        {
            var counts = await jobs.GetBuildCountsAsync(cancellationToken);
            var allJobs = await jobs.ListJobsAsync(cancellationToken);
            var runnerList = await runners.ListAsync(cancellationToken);

            var builder = new StringBuilder();

            Write(builder, "walis_jobs_total", "任務總數", "gauge", allJobs.Count);
            Write(builder, "walis_jobs_enabled", "啟用中的任務數", "gauge", allJobs.Count(job => !job.IsDisabled));

            Write(builder, "walis_builds_queued", "排隊中的建置數", "gauge", counts.QueuedBuilds);
            Write(builder, "walis_builds_running", "執行中的建置數", "gauge", counts.RunningBuilds);

            builder.AppendLine("# HELP walis_builds_total 依狀態統計的建置總數");
            builder.AppendLine("# TYPE walis_builds_total counter");
            builder.AppendLine($"walis_builds_total{{status=\"succeeded\"}} {counts.SuccessfulBuilds}");
            builder.AppendLine($"walis_builds_total{{status=\"failed\"}} {counts.FailedBuilds}");

            Write(builder, "walis_builds_last_7_days", "最近七天的建置數", "gauge", counts.BuildsLast7Days);
            Write(builder, "walis_build_failures_last_7_days", "最近七天的失敗數", "gauge", counts.FailuresLast7Days);
            Write(builder, "walis_build_duration_seconds_avg", "建置平均耗時（秒）", "gauge", counts.AverageDurationSeconds);
            Write(builder, "walis_build_queue_seconds_avg", "建置平均排隊時間（秒）", "gauge", counts.AverageQueueSeconds);

            Write(builder, "walis_runners_total", "執行器總數", "gauge", runnerList.Count);
            builder.AppendLine("# HELP walis_runners 依狀態統計的執行器數");
            builder.AppendLine("# TYPE walis_runners gauge");

            foreach (var status in new[]
                         { RunnerStatus.Idle, RunnerStatus.Busy, RunnerStatus.Offline, RunnerStatus.Paused })
            {
                var count = runnerList.Count(runner => runner.Status == status);
                builder.AppendLine($"walis_runners{{status=\"{status.ToLowerInvariant()}\"}} {count}");
            }

            // Counted in memory by this node since it started; the audit log is the durable record, these feed rate alerts.
            Write(builder, "walis_sign_in_failures_total", "此節點啟動後的登入失敗次數（密碼、一次性密碼與設定碼）", "counter",
                security.SignInFailures);
            Write(builder, "walis_lockouts_total", "此節點啟動後的登入鎖定次數", "counter", security.Lockouts);

            // Every refused request by reason, including the ones the audit log coalesced into one row per minute.
            builder.AppendLine("# HELP walis_access_denied_total 此節點啟動後被拒絕的請求次數（依原因）");
            builder.AppendLine("# TYPE walis_access_denied_total counter");
            foreach (var (reason, count) in security.AccessDenied)
                builder.AppendLine($"walis_access_denied_total{{reason=\"{reason}\"}} {count.ToString(CultureInfo.InvariantCulture)}");

            return Results.Text(builder.ToString(), "text/plain; version=0.0.4", Encoding.UTF8);
        });

        return app;
    }

    private static void Write(StringBuilder builder, string name, string help, string type, double value)
    {
        builder.AppendLine($"# HELP {name} {help}");
        builder.AppendLine($"# TYPE {name} {type}");
        builder.AppendLine($"{name} {value.ToString("0.###", CultureInfo.InvariantCulture)}");
    }
}