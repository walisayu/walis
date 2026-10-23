using System.Text.Json.Serialization;

namespace Walis.Core.Models;

public static class BuildStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
}

public sealed class Build
{
    public Guid Id { get; init; }
    public Guid JobId { get; init; }
    public int Number { get; init; }
    public string Status { get; init; } = BuildStatus.Queued;
    public string TriggeredBy { get; init; } = "manual";
    public string? RunnerName { get; init; }
    public DateTime QueuedAt { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public string Log { get; init; } = string.Empty;
    public string JobName { get; init; } = string.Empty;

    /// <summary>The label of the matrix combination, e.g. NODE=20, OS=mac; empty for ordinary builds.</summary>
    public string? MatrixLabel { get; init; }

    /// <summary>The variables of that combination, injected as environment variables.</summary>
    public string? MatrixValues { get; init; }

    /// <summary>Build parameter values decided at trigger time (name -> value), injected as environment variables; only non-secret values, safe to show in the UI.</summary>
    public string? ParametersJson { get; set; }

    /// <summary>Values of password-type parameters, stored separately and never serialized into API responses; only the executor gets them.</summary>
    [JsonIgnore]
    public string? SecretParametersJson { get; set; }

    /// <summary>The API node running this build; the machine name on a single-node deployment.</summary>
    public string? Node { get; init; }

    /// <summary>
    ///     The caller's idempotency reference (body externalRef or the Idempotency-Key header). Every build of one matrix trigger shares it;
    ///     a repeated trigger with the same reference returns these builds instead of creating new ones until they are purged.
    /// </summary>
    public string? ExternalRef { get; init; }

    /// <summary>SHA-256 of the trigger content that came with <see cref="ExternalRef" />; internal, never part of API responses.</summary>
    [JsonIgnore]
    public string? TriggerHash { get; init; }

    public string? JobProject { get; init; }

    /// <summary>When a cancel was requested for a build that was already running; null otherwise.</summary>
    public DateTime? CancelRequestedAt { get; init; }

    public double? DurationSeconds => StartedAt is null
        ? null
        : ((FinishedAt ?? DateTime.UtcNow) - StartedAt.Value).TotalSeconds;

    /// <summary>Filled only when querying a single build; list queries omit the steps to stay lightweight.</summary>
    public IReadOnlyList<BuildStep> Steps { get; set; } = [];
}

/// <summary>What <c>CancelBuildAsync</c> did.</summary>
public enum BuildCancellation
{
    /// <summary>The build is already finished (or does not exist); nothing to cancel.</summary>
    NotCancelable,

    /// <summary>The build was still queued and is now Canceled.</summary>
    Canceled,

    /// <summary>The build is running; the executor has been asked to stop it and will record it as Canceled.</summary>
    Requested
}

public sealed record BuildCompletion(string Status, int? ExitCode, string Log)
{
    /// <summary>Whether the build was aborted by a timeout, which decides the kind of notification.</summary>
    public bool TimedOut { get; init; }
}

public sealed record BuildContext(Job Job, Build Build);

/// <summary>
///     A build query. <paramref name="Limit" /> caps the unpaged list; <paramref name="Page" /> and <paramref name="PageSize" /> are used by the paged query.
///     <paramref name="ViewRoles" /> is the set of job ViewRoles the caller can see; null means unrestricted (internal use only).
/// </summary>
public sealed record BuildQuery(
    string? Status,
    string? RunnerName,
    Guid? JobId,
    string? Search,
    int Limit,
    IReadOnlyList<string>? ViewRoles = null,
    int Page = 1,
    int PageSize = BuildQuery.DefaultPageSize)
{
    public const int DefaultPageSize = 30;
    public const int MaximumPageSize = 200;
}

/// <summary>
///     One page of builds. <see cref="Counts" /> is the number of builds per status and deliberately ignores the status filter,
///     so the statistics above the table keep showing the whole picture while a single status is being listed.
/// </summary>
public sealed record BuildPage(
    IReadOnlyList<Build> Items,
    int Total,
    int Page,
    int PageSize,
    IReadOnlyDictionary<string, int> Counts);

/// <summary>The matrix fields needed to enqueue a build atomically; both null for ordinary builds.</summary>
public sealed record BuildQueueItem(
    string? MatrixLabel = null,
    string? MatrixValues = null,
    string? ParametersJson = null,
    string? SecretParametersJson = null);