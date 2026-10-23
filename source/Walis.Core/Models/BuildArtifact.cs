namespace Walis.Core.Models;

public sealed class BuildArtifact
{
    public Guid Id { get; init; }
    public Guid BuildId { get; init; }
    public string RelativePath { get; init; } = string.Empty;
    public long SizeBytes { get; init; }
    public DateTime CreatedAt { get; init; }
}