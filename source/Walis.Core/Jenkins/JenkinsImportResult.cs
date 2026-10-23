using Walis.Core.Actions;
using Walis.Core.Models;

namespace Walis.Core.Jenkins;

public static class JenkinsWarningSeverity
{
    /// <summary>Mapped automatically but with slightly different semantics; a glance is enough.</summary>
    public const string Info = "info";

    /// <summary>Mapped, but needs a person to fill something in (secrets, confirming the runner).</summary>
    public const string Warning = "warning";

    /// <summary>Walis has no equivalent; left as a comment.</summary>
    public const string Unsupported = "unsupported";
}

public sealed record JenkinsWarning(string Severity, string Message, int? Line = null, string? Snippet = null);

/// <summary>An entry to create as a Walis environment variable; from environment{}, credentials() and withCredentials.</summary>
public sealed class JenkinsEnvironmentVariable
{
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public bool IsSecret { get; init; }

    /// <summary>A description of the source, e.g. the Jenkins credential id, so the user knows what to fill in.</summary>
    public string? Source { get; init; }
}

/// <summary>The converted job draft; the fields match the Walis job request so the frontend loads it straight into the editor.</summary>
public sealed class JenkinsJobProposal
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Repository { get; set; }
    public string Branch { get; set; } = "main";
    public string? RunnerName { get; set; }
    public int TimeoutMinutes { get; set; } = 30;
    public string TriggerKind { get; set; } = TriggerMode.Manual;
    public string? Cron { get; set; }
    public string? ArtifactPath { get; set; }
    public string? MatrixJson { get; set; }
    public string? ParametersJson { get; set; }
    public bool AllowConcurrentBuilds { get; set; } = true;
    public string? DownstreamJson { get; set; }
    public bool IsDisabled { get; set; }
    public List<ActionStep> Steps { get; } = [];
}

public sealed record JenkinsImportResult(
    string Format,
    JenkinsJobProposal Job,
    IReadOnlyList<JenkinsEnvironmentVariable> Environment,
    IReadOnlyList<JenkinsWarning> Warnings);