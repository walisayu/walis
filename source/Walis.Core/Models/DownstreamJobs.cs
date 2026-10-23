using System.Text.Json;

namespace Walis.Core.Models;

/// <summary>When a downstream job is triggered.</summary>
public static class DownstreamWhen
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Always = "always";

    public static bool IsValid(string? value)
    {
        return value is Success or Failure or Always;
    }
}

/// <summary>Another job to trigger when this one finishes; referenced by name so it still matches after export/import to another machine.</summary>
public sealed class DownstreamTarget
{
    public string JobName { get; init; } = string.Empty;
    public string When { get; init; } = DownstreamWhen.Success;
}

/// <summary>
///     A job's downstream list (stored in <c>Jobs.DownstreamJson</c>), matching Jenkins's <c>build job:</c> and upstream triggers.
///     The chain length is capped, so a cycle like A->B->A stops after a few levels.
/// </summary>
public static class DownstreamJobs
{
    public const int MaximumTargets = 20;
    public const int MaximumJsonLength = 8_192;
    public const int MaximumChainDepth = 10;

    /// <summary>The parameter names passed when triggering downstream, so the script knows who called it.</summary>
    public const string UpstreamJobVariable = "WALIS_UPSTREAM_JOB";

    public const string UpstreamBuildVariable = "WALIS_UPSTREAM_BUILD";
    public const string UpstreamStatusVariable = "WALIS_UPSTREAM_STATUS";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    public static IReadOnlyList<DownstreamTarget> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<DownstreamTarget>>(json, SerializerOptions)?
                       .Where(target => target is not null && !string.IsNullOrWhiteSpace(target.JobName))
                       .ToList()
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static bool IsValid(string? json, string? ownName, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json)) return true;

        if (json.Length > MaximumJsonLength)
        {
            error = $"下游任務設定不可超過 {MaximumJsonLength:N0} 個字元。";
            return false;
        }

        List<DownstreamTarget>? targets;
        try
        {
            targets = JsonSerializer.Deserialize<List<DownstreamTarget>>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            error = "下游任務設定不是有效的 JSON 陣列。";
            return false;
        }

        if (targets is null) return true;

        if (targets.Count > MaximumTargets)
        {
            error = $"下游任務最多 {MaximumTargets} 個。";
            return false;
        }

        foreach (var target in targets)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.JobName) || target.JobName.Length > 80)
            {
                error = "下游任務要指定名稱，且不可超過 80 個字元。";
                return false;
            }

            if (!DownstreamWhen.IsValid(target.When))
            {
                error = $"下游任務 {target.JobName} 的觸發時機只能是 success、failure 或 always。";
                return false;
            }

            if (ownName is not null && string.Equals(target.JobName.Trim(), ownName.Trim(), StringComparison.Ordinal))
            {
                error = "任務不能把自己設成下游。";
                return false;
            }
        }

        return true;
    }

    public static bool ShouldTrigger(DownstreamTarget target, string buildStatus)
    {
        return target.When switch
        {
            DownstreamWhen.Always => true,
            DownstreamWhen.Failure => buildStatus == BuildStatus.Failed,
            _ => buildStatus == BuildStatus.Succeeded
        };
    }
}