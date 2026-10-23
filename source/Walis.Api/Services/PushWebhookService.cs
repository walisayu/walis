using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

public sealed record PushEvent(string? Repository, string Branch, string? Pusher, string? Commit);

public sealed class PushWebhookService(
    IJobRepository jobs,
    BuildLauncher launcher,
    ILogger<PushWebhookService> logger)
{
    public async Task<IReadOnlyList<Build>> TriggerAsync(PushEvent push, CancellationToken cancellationToken)
    {
        var repository = Normalize(push.Repository);
        var started = new List<Build>();

        // The endpoint already requires repository; the service layer still rejects defensively so other callers cannot trigger broadly with only a branch.
        if (repository.Length == 0)
        {
            logger.LogWarning("忽略缺少儲存庫資訊的推送事件（分支：{Branch}）。", AuditText.Clean(push.Branch, 200));
            return started;
        }

        foreach (var job in await jobs.ListJobsAsync(cancellationToken))
        {
            if (job.IsDisabled || job.TriggerKind != TriggerMode.Push) continue;

            // A trusted job runs outside the sandbox and only administrators may change or start it deliberately; the shared webhook
            // secret is not an administrator credential, and a native-format request can name any repository and branch.
            if (job.TrustedUnsandboxed)
            {
                logger.LogWarning("推送事件不會觸發信任任務 {Job}；請由管理員手動或以排程執行。", AuditText.Clean(job.Name, 200));
                continue;
            }

            if (!string.Equals(job.Branch, push.Branch, StringComparison.OrdinalIgnoreCase)) continue;

            // Push jobs are repository-scoped. Legacy rows without a repository
            // remain inert until an administrator fixes their configuration.
            var jobRepository = Normalize(job.Repository);
            if (jobRepository.Length == 0 || jobRepository != repository) continue;

            // BuildLauncher writes the audit record; the branch goes along so the summary says what was pushed.
            var result = await launcher.LaunchAsync(job, "push", cancellationToken,
                new BuildLaunchOptions { AuditNote = AuditText.Clean(push.Branch, 200) });

            if (result.Build is not null)
                started.Add(result.Build);
            else if (result.Error is not null)
                logger.LogWarning("推送無法啟動任務 {Job}：{Error}", AuditText.Clean(job.Name, 200), result.Error);
        }

        return started;
    }

    /// <summary>Normalizes the various ways of writing a repository address into host/path for comparison.</summary>
    public static string Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return string.Empty;

        var value = url.Trim();

        if (value.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
        {
            value = value["git@".Length..].Replace(':', '/');
        }
        else
        {
            var scheme = value.IndexOf("://", StringComparison.Ordinal);

            if (scheme >= 0) value = value[(scheme + 3)..];
        }

        var credentials = value.IndexOf('@');

        if (credentials >= 0) value = value[(credentials + 1)..];

        value = value.TrimEnd('/');

        if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value[..^4];

        return value.ToLowerInvariant();
    }

    /// <summary>refs/heads/main -> main.</summary>
    public static string BranchFromRef(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return string.Empty;

        const string prefix = "refs/heads/";
        return reference.StartsWith(prefix, StringComparison.Ordinal) ? reference[prefix.Length..] : reference;
    }
}