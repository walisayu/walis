namespace Walis.Core.Models;

public sealed class EnvironmentVariable
{
    public const string Mask = "••••••••••••";

    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public bool IsSecret { get; init; }

    /// <summary>Null means it applies globally, otherwise only to the given job.</summary>
    public Guid? JobId { get; init; }

    public DateTime UpdatedAt { get; init; }

    /// <summary>The job name attached at query time; null for global variables.</summary>
    public string? JobName { get; set; }
}

public sealed record EnvironmentVariableDraft(string Name, string Value, bool IsSecret, Guid? JobId);