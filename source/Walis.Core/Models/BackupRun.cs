namespace Walis.Core.Models;

public static class BackupRunStatus
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
}

/// <summary>The record of one backup run; the admin UI's history list reads this directly.</summary>
public sealed class BackupRun
{
    public Guid Id { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public string Status { get; init; } = BackupRunStatus.Running;

    /// <summary>Trigger source: schedule or manual.</summary>
    public string TriggeredBy { get; init; } = "manual";

    public long SizeBytes { get; init; }

    public string? FileName { get; init; }

    public string Log { get; init; } = string.Empty;

    public double? DurationSeconds =>
        FinishedAt is null ? null : (FinishedAt.Value - StartedAt).TotalSeconds;
}