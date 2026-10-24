using System.Text.Json;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

public sealed record LaunchResult(Build? Build, string? Error, bool JobNotFound)
{
    public const string ExternalRefConflictCode = "external_ref_conflict";

    /// <summary>A matrix job produces several rows at once; an ordinary job produces one, same as <see cref="Build" />.</summary>
    public IReadOnlyList<Build> Builds { get; init; } = [];

    /// <summary>True when the external reference already had builds with the same content and those were returned instead of new ones.</summary>
    public bool Deduplicated { get; init; }

    /// <summary>A machine-readable reason next to <see cref="Error" />; null for the ordinary rejections.</summary>
    public string? ErrorCode { get; init; }

    public static LaunchResult NotFound()
    {
        return new LaunchResult(null, null, true);
    }

    public static LaunchResult Rejected(string error)
    {
        return new LaunchResult(null, error, false);
    }

    public static LaunchResult Started(Build build)
    {
        return new LaunchResult(build, null, false) { Builds = [build] };
    }

    public static LaunchResult Started(IReadOnlyList<Build> builds)
    {
        return new LaunchResult(builds[0], null, false) { Builds = builds };
    }

    public static LaunchResult Existing(IReadOnlyList<Build> builds)
    {
        return new LaunchResult(builds[0], null, false) { Builds = builds, Deduplicated = true };
    }

    public static LaunchResult ExternalRefConflict()
    {
        return new LaunchResult(null, "這個 externalRef 已用於參數不同的另一次觸發。", false)
        {
            ErrorCode = ExternalRefConflictCode
        };
    }
}

/// <summary>What comes with a trigger: the build parameters the user filled in, and the values the system injects (upstream info, chain depth).</summary>
public sealed record BuildLaunchOptions
{
    /// <summary>User-provided parameter values; missing ones take the job's declared defaults, unknown names are rejected.</summary>
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }

    /// <summary>System-injected values, not bound by the parameter declarations, also provided to the script as environment variables.</summary>
    public IReadOnlyDictionary<string, string>? SystemValues { get; init; }

    /// <summary>
    ///     The caller's idempotency reference, already validated. A repeat with the same reference and the same <see cref="Parameters" />
    ///     returns the original builds; different parameters are a conflict. Only builds still kept by the retention policy count.
    /// </summary>
    public string? ExternalRef { get; init; }

    /// <summary>Extra context for the audit summary (the pushed branch, the upstream build); appended in parentheses.</summary>
    public string? AuditNote { get; init; }
}

/// <summary>
///     The single entry point for creating builds; manual runs, schedules, webhooks, downstream chains and MCP all go through here,
///     which is also why the audit record for a trigger is written here and nowhere else: no caller can forget it.
/// </summary>
public sealed class BuildLauncher(
    IJobRepository jobs,
    IRunnerRepository runners,
    BuildQueue queue,
    AuditService audit)
{
    /// <summary>The downstream chain depth, stored in the build parameters; nothing further is triggered past <see cref="DownstreamJobs.MaximumChainDepth" />.</summary>
    public const string ChainDepthVariable = "WALIS_CHAIN_DEPTH";

    public async Task<LaunchResult> LaunchAsync(
        Guid jobId,
        string triggeredBy,
        CancellationToken cancellationToken,
        BuildLaunchOptions? launch = null)
    {
        var job = await jobs.GetJobAsync(jobId, cancellationToken);
        if (job is null) return LaunchResult.NotFound();

        return await LaunchAsync(job, triggeredBy, cancellationToken, launch);
    }

    public async Task<LaunchResult> LaunchAsync(
        Job job,
        string triggeredBy,
        CancellationToken cancellationToken,
        BuildLaunchOptions? launch = null)
    {
        BuildExternalReference? externalReference = null;
        if (launch?.ExternalRef is { } externalRef)
        {
            externalReference = new BuildExternalReference(
                externalRef,
                BuildExternalRefs.ComputeTriggerHash(
                    launch.Parameters,
                    BuildParameters.Parse(job.ParametersJson)
                        .Where(definition => definition.IsSecret)
                        .Select(definition => definition.Name)
                        .ToHashSet(StringComparer.Ordinal)));

            // A retry must not fail just because the job was disabled, a runner paused or the queue filled up after the first call,
            // so the reference is resolved before any of those checks. The repository repeats the check under the job lock for races.
            var existing = await jobs.FindBuildsByExternalRefAsync(job.Id, externalRef, cancellationToken);
            if (existing.Count != 0) return Resolve(existing, externalReference);
        }

        if (job.IsDisabled) return LaunchResult.Rejected("此任務已停用，無法執行。");

        // Build parameters: missing ones get defaults; unknown names and values outside the choices are rejected here.
        var definitions = BuildParameters.Parse(job.ParametersJson);
        if (!BuildParameters.TryResolve(definitions, launch?.Parameters, out var parameterValues,
                out var parameterError)) return LaunchResult.Rejected(parameterError!);

        var secretNames = definitions.Where(definition => definition.IsSecret).Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        var plainValues = parameterValues
            .Where(pair => !secretNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var secretValues = parameterValues
            .Where(pair => secretNames.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        foreach (var (name, value) in launch?.SystemValues ?? new Dictionary<string, string>())
            plainValues[name] = value;

        var parametersJson = BuildParameters.SerializeValues(plainValues);
        var secretParametersJson = BuildParameters.SerializeValues(secretValues);

        var runnerName = job.RunnerName;
        if (!string.IsNullOrWhiteSpace(runnerName))
        {
            var runner = await runners.GetByNameAsync(runnerName, cancellationToken);
            if (runner is null) return LaunchResult.Rejected($"找不到執行器「{runnerName}」。");

            if (runner.IsPaused) return LaunchResult.Rejected($"執行器「{runnerName}」目前暫停接單。");

            // Checked again here because a job saved before the runner was reserved, or edited directly in the database, must
            // still not reach a host kept for administrator jobs.
            if (JobAccess.Rank(JobAccess.NormalizeRunRole(job.RunRole)) < JobAccess.Rank(runner.MinimumRunRole))
                return LaunchResult.Rejected(
                    $"執行器「{runnerName}」只接受執行角色 {runner.MinimumRunRole} 以上的任務；請管理員調整任務的執行角色或執行器。");
        }
        else
        {
            runnerName = Runner.LocalName;
        }

        // The database may be old, hand-edited or corrupt; an invalid matrix must not silently degrade into a single build.
        if (!BuildMatrix.IsValid(job.MatrixJson, out var matrixError))
            return LaunchResult.Rejected($"矩陣設定無效：{matrixError}");

        var combinations = BuildMatrix.Expand(job.MatrixJson);
        IReadOnlyList<BuildQueueItem> requested = combinations.Count == 0
            ? [new BuildQueueItem(null, null, parametersJson, secretParametersJson)]
            :
            [
                .. combinations.Select(combination => new BuildQueueItem(
                    combination.Label,
                    JsonSerializer.Serialize(combination.Values),
                    parametersJson,
                    secretParametersJson))
            ];

        // The capacity check and the batch INSERT happen in one repository transaction, so a matrix is never half enqueued.
        var queued = await jobs.QueueBuildsAsync(
            job.Id,
            triggeredBy,
            runnerName,
            requested,
            externalReference,
            cancellationToken);

        switch (queued.Outcome)
        {
            case BuildQueueOutcome.CapacityExceeded:
                return LaunchResult.Rejected(
                    $"待處理建置已達容量上限（全系統 {JobRepository.MaximumActiveBuilds}、每個任務 {JobRepository.MaximumActiveBuildsPerJob}）。");
            case BuildQueueOutcome.JobNotFound:
                return LaunchResult.NotFound();
            case BuildQueueOutcome.Deduplicated:
                return LaunchResult.Existing(queued.Builds);
            case BuildQueueOutcome.ExternalRefConflict:
                return LaunchResult.ExternalRefConflict();
        }

        foreach (var build in queued.Builds) queue.TryEnqueue(build.Id);

        // One record per trigger (a matrix is one decision), carrying the first build's id and the job so a job's history is queryable.
        // A deduplicated retry returned above was audited the first time and is deliberately not recorded again.
        var first = queued.Builds[0];
        await audit.RecordAsync(
            new AuditEvent(AuditAction.Run, "build", first.Id.ToString(), DescribeTrigger(job, first, triggeredBy, queued.Builds.Count, launch?.AuditNote))
            {
                JobId = job.Id
            },
            cancellationToken);

        return LaunchResult.Started(queued.Builds);
    }

    /// <summary>The audit summary names the source of the trigger; the names come from the database, but are cleaned once more where the record is written.</summary>
    public static string DescribeTrigger(Job job, Build build, string triggeredBy, int count, string? note)
    {
        var target = $"{job.Name} #{build.Number}";
        var summary = triggeredBy switch
        {
            "manual" => $"執行 {target}",
            "schedule" => $"排程觸發 {target}",
            "push" => $"推送觸發 {target}",
            "mcp" => $"經 MCP 執行 {target}",
            _ when triggeredBy.StartsWith("manual:", StringComparison.Ordinal) =>
                $"執行 {target}（呼叫端註記：{triggeredBy["manual:".Length..]}）",
            _ when triggeredBy.StartsWith("downstream:", StringComparison.Ordinal) =>
                $"由 {triggeredBy["downstream:".Length..]} 觸發下游 {target}",
            _ => $"執行 {target}（觸發來源：{triggeredBy}）"
        };

        if (count > 1) summary += $"（矩陣 {count} 組）";
        if (!string.IsNullOrWhiteSpace(note)) summary += $"（{note}）";

        return summary;
    }

    private static LaunchResult Resolve(IReadOnlyList<Build> existing, BuildExternalReference reference)
    {
        return existing.All(build => build.TriggerHash == reference.TriggerHash)
            ? LaunchResult.Existing(existing)
            : LaunchResult.ExternalRefConflict();
    }
}