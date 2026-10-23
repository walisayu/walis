namespace Walis.Core.Models;

public static class BuildStepStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";

    /// <summary>Never ran because an earlier step failed.</summary>
    public const string Skipped = "Skipped";
}

/// <summary>A step's run condition, matching Jenkins's post { success / failure / always }.</summary>
public static class StepRunCondition
{
    /// <summary>Runs only when nothing before it failed (default).</summary>
    public const string Success = "success";

    /// <summary>Runs regardless of earlier failures, e.g. cleanup and notifications.</summary>
    public const string Always = "always";

    /// <summary>Runs only when an earlier step has already failed.</summary>
    public const string Failure = "failure";

    public static bool IsValid(string? value)
    {
        return value is Success or Always or Failure;
    }

    public static string Normalize(string? value)
    {
        return IsValid(value) ? value! : Success;
    }

    /// <summary>Whether the build has already failed, which decides if a step with this condition runs.</summary>
    public static bool ShouldRun(string? condition, bool buildFailed)
    {
        return Normalize(condition) switch
        {
            Always => true,
            Failure => buildFailed,
            _ => !buildFailed
        };
    }
}

/// <summary>
///     One step in a build. Steps are not stored on the job; they are derived from it when the build starts:
///     each action of an action flow is one step, and a job with a directly edited script has a single step.
/// </summary>
public sealed class BuildStep
{
    public Guid Id { get; init; }
    public Guid BuildId { get; init; }

    /// <summary>Execution order starting at 1.</summary>
    public int Number { get; init; }

    public string Name { get; init; } = string.Empty;
    public string Command { get; init; } = string.Empty;

    /// <summary>Whether to keep going after a failure.</summary>
    public bool ContinueOnError { get; init; }

    /// <summary>Runs together with the previous step when it is also marked parallel (only meaningful without explicit dependencies).</summary>
    public bool Parallel { get; init; }

    /// <summary>Database column: a JSON array of the step numbers this depends on; the API and executor use <see cref="DependsOn" />.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? DependsOnJson { get; init; }

    private IReadOnlyList<int>? _dependsOn;

    /// <summary>The step numbers (1-based) this depends on; when all are empty, <c>BuildStepGraph</c> derives them from order and parallel flags.</summary>
    public IReadOnlyList<int> DependsOn
    {
        get => _dependsOn ??= string.IsNullOrWhiteSpace(DependsOnJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<int>>(DependsOnJson) ?? [];
        init => _dependsOn = value;
    }

    /// <summary>The run condition, see <see cref="StepRunCondition" />.</summary>
    public string RunWhen { get; init; } = StepRunCondition.Success;

    /// <summary>How many retries after failure (not counting the first attempt).</summary>
    public int Retries { get; init; }

    /// <summary>A condition expression evaluated at run time (<c>StepCondition</c>); the step is skipped when false, empty means no condition.</summary>
    public string? Condition { get; init; }

    /// <summary>The key given to this step in the flow; outputs of a keyed step are injected as <c>WALIS_STEP_&lt;key&gt;_&lt;name&gt;</c>.</summary>
    public string? StepKey { get; init; }

    /// <summary>The outputs the step wrote to <c>$WALIS_OUTPUT_FILE</c>, a JSON object; a database column, returned by the API as <see cref="Outputs" />.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? OutputsJson { get; init; }

    public IReadOnlyDictionary<string, string> Outputs => string.IsNullOrWhiteSpace(OutputsJson)
        ? EmptyOutputs
        : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(OutputsJson) ?? EmptyOutputs;

    private static readonly IReadOnlyDictionary<string, string> EmptyOutputs = new Dictionary<string, string>();

    public string Status { get; init; } = BuildStepStatus.Pending;
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public string Log { get; init; } = string.Empty;

    public double? DurationSeconds => StartedAt is null
        ? null
        : ((FinishedAt ?? DateTime.UtcNow) - StartedAt.Value).TotalSeconds;
}

/// <summary>A step planned before the build starts.</summary>
public sealed record BuildStepPlan(
    string Name,
    string Command,
    bool ContinueOnError,
    bool Parallel = false,
    string RunWhen = StepRunCondition.Success,
    int Retries = 0,
    string? Key = null,
    IReadOnlyList<int>? DependsOn = null,
    string? Condition = null)
{
    public const int MaximumRetries = 10;
}