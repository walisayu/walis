namespace Walis.Core.Models;

/// <summary>How a job is triggered.</summary>
public static class ScriptModes
{
    /// <summary>Edit the shell script directly.</summary>
    public const string Source = "source";

    /// <summary>Compose the script from an action flow.</summary>
    public const string Actions = "actions";

    public static bool IsValid(string? value)
    {
        return value is Source or Actions;
    }
}

public static class TriggerMode
{
    public const string Manual = "manual";
    public const string Push = "push";
    public const string Cron = "cron";

    public static bool IsValid(string? value)
    {
        return value is Manual or Push or Cron;
    }
}

public sealed class Job
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? Project { get; init; }
    public string? Description { get; init; }
    public string? Repository { get; init; }
    public string Branch { get; init; } = "main";
    public string? RunnerName { get; init; }
    public string? WorkingDirectory { get; init; }
    public string Command { get; init; } = string.Empty;
    public int TimeoutMinutes { get; init; } = 30;
    public string? ArtifactPath { get; init; }
    public string TriggerKind { get; init; } = TriggerMode.Manual;
    public string? Cron { get; init; }
    public bool NotifyOnFailure { get; init; } = true;
    public bool IsDisabled { get; init; }

    /// <summary>Where the command comes from: a directly edited script or an action flow.</summary>
    public string ScriptMode { get; init; } = ScriptModes.Source;

    /// <summary>The action flow as JSON; used to restore the steps when switching back to flow mode.</summary>
    public string? ActionsJson { get; init; }

    /// <summary>Paths to keep between builds, one per line, relative to the workspace.</summary>
    public string? CachePaths { get; init; }

    /// <summary>
    ///     Whether the workspace contents are cleared before the build. Null follows the system setting, which is what
    ///     every job did before this was per job; true and false decide it for this job alone.
    /// </summary>
    public bool? CleanWorkspace { get; init; }

    /// <summary>Whether the workspace is cleared after the build ends; null follows the system setting.</summary>
    public bool? CleanWorkspaceAfterBuild { get; init; }

    /// <summary>Matrix configuration in the format of <see cref="BuildMatrix" />; when set, one trigger produces several builds.</summary>
    public string? MatrixJson { get; init; }

    /// <summary>The minimum role needed to see this job.</summary>
    public string ViewRole { get; init; } = UserRole.Viewer;

    /// <summary>The minimum role needed to trigger this job.</summary>
    public string RunRole { get; init; } = UserRole.Operator;

    /// <summary>Build parameter declarations in the format of <see cref="BuildParameters" />; values are filled at trigger time.</summary>
    public string? ParametersJson { get; init; }

    /// <summary>When off, only one build of this job runs at a time and the rest wait in the queue (Jenkins's disableConcurrentBuilds).</summary>
    public bool AllowConcurrentBuilds { get; init; } = true;

    /// <summary>Downstream jobs to trigger on completion, in the format of <see cref="DownstreamJobs" />.</summary>
    public string? DownstreamJson { get; init; }

    /// <summary>Adds <c>(deny network*)</c> to the sandbox profile where a sandbox exists; no effect on unsandboxed runners.</summary>
    public bool DenyNetwork { get; init; }

    /// <summary>
    ///     The job runs without the sandbox on purpose (the self-update job writes the installation). Only an administrator can
    ///     save a job with this on; the first line of every step log says so.
    /// </summary>
    public bool TrustedUnsandboxed { get; init; }

    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }

    /// <summary>The following fields are computed at query time and do not exist in the table.</summary>
    public int TotalBuilds { get; set; }

    public int SucceededBuilds { get; set; }
    public int SuccessRate { get; set; }
    public int? LastBuildNumber { get; set; }
    public string? LastBuildStatus { get; set; }
    public DateTime? LastBuildAt { get; set; }
    public double? LastBuildSeconds { get; set; }
    public DateTime? NextRunAt { get; set; }
}

public sealed record JobDraft(
    string Name,
    string? Project,
    string? Description,
    string? Repository,
    string? Branch,
    string? RunnerName,
    string? WorkingDirectory,
    string Command,
    int TimeoutMinutes,
    string? ArtifactPath,
    string TriggerKind,
    string? Cron,
    bool NotifyOnFailure,
    bool IsDisabled,
    string ScriptMode,
    string? ActionsJson,
    string? CachePaths = null,
    string? MatrixJson = null,
    string? ViewRole = null,
    string? RunRole = null,
    string? ParametersJson = null,
    bool AllowConcurrentBuilds = true,
    string? DownstreamJson = null,

    // Appended rather than grouped with CachePaths on purpose: inserting a nullable parameter in the middle silently
    // shifts every positional call whose remaining arguments still type-check.
    bool? CleanWorkspace = null,
    bool DenyNetwork = false,
    bool TrustedUnsandboxed = false,
    bool? CleanWorkspaceAfterBuild = null);
