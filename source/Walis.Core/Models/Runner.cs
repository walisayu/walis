namespace Walis.Core.Models;

public static class RunnerStatus
{
    public const string Idle = "Idle";
    public const string Busy = "Busy";
    public const string Offline = "Offline";
    public const string Paused = "Paused";
}

public sealed class Runner
{
    /// <summary>The name of the API's own built-in runner; always exists and cannot be deleted.</summary>
    public const string LocalName = "local";

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Labels { get; init; } = string.Empty;
    public int TotalSlots { get; init; } = 1;
    public bool IsPaused { get; init; }
    public bool IsLocal { get; init; }
    public DateTime LastSeenAt { get; init; }
    public DateTime CreatedAt { get; init; }

    /// <summary>
    ///     The lowest job run role this runner accepts (operator or admin). A remote runner executes without Walis's sandbox, so a
    ///     host that holds deployment credentials can be reserved for administrator jobs; operators then cannot point a job at it.
    /// </summary>
    public string MinimumRunRole { get; init; } = UserRole.Operator;

    /// <summary>The following fields are computed at query time.</summary>
    public int UsedSlots { get; set; }

    public string Status { get; set; } = RunnerStatus.Offline;
    public int? CurrentBuildNumber { get; set; }
    public string? CurrentJobName { get; set; }

    public IReadOnlyList<string> LabelList => Labels
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed record RunnerDraft(
    string Name,
    string? OperatingSystem,
    string? Version,
    string? Labels,
    int TotalSlots,
    bool IsPaused,
    string? MinimumRunRole = null);

/// <summary>What deleting a runner did: the runner row is gone and every API key bound to its name is disabled.</summary>
public sealed record RunnerDeletion(string Name, int DisabledKeyCount);
