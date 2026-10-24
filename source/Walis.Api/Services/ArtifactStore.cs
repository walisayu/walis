using System.Security;
using Walis.Core.Execution;

namespace Walis.Api.Services;

/// <summary>Where build artifacts are stored: data/artifacts/{build id}/.</summary>
public sealed class ArtifactStore(DataDirectory data)
{
    public string Root { get; } = data.Combine("artifacts");

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    public string DirectoryFor(Guid buildId)
    {
        return Path.Combine(Root, buildId.ToString("N"));
    }

    public string ResolveFile(Guid buildId, string relativePath)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(DirectoryFor(buildId)));
        var full = Path.GetFullPath(Path.Combine(directory, relativePath));

        if (!full.StartsWith(directory + Path.DirectorySeparatorChar, PathComparison) ||
            BuildIsolation.ContainsLink(directory, full))
            return string.Empty;

        // A stored file may still be replaced with a link by a leftover process of the same account or an external operations action.
        // The download side must not follow a redirect outside the artifact root.
        try
        {
            var info = new FileInfo(full);
            info.Refresh();
            return !info.Exists ||
                   info.LinkTarget is not null ||
                   (info.Attributes &
                    (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                ? string.Empty
                : full;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            return string.Empty;
        }
    }

    public bool TryOpenFile(Guid buildId, string relativePath, out FileStream stream)
    {
        var path = ResolveFile(buildId, relativePath);
        stream = null!;
        return path.Length != 0 && SafeRegularFile.TryOpenRead(path, out stream);
    }

    public IReadOnlyList<CollectedArtifact> Collect(
        Guid buildId,
        string workingDirectory,
        string? artifactPath,
        out string report,
        IReadOnlyList<string>? protectedPaths = null)
    {
        return ArtifactCollector.Collect(
            workingDirectory,
            artifactPath,
            DirectoryFor(buildId),
            out report,
            protectedPaths);
    }

    public void Remove(Guid buildId)
    {
        var directory = DirectoryFor(buildId);

        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}