using Walis.Core.Backup;

namespace Walis.Core.Execution;

/// <summary>
///     The workspaces Walis creates itself for jobs that name no working directory. They live at a fixed place under the data
///     directory (one subdirectory per job id) instead of a fresh temp root per process start, so nothing accumulates in the
///     temp directory across restarts and the root can be validated the same way as the rest of the data directory.
/// </summary>
public static class ManagedWorkspaces
{
    public const string DirectoryName = "workspaces";

    private const UnixFileMode PrivateDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Creates or validates the root: a real directory (no link or reparse point), private to the service account.</summary>
    public static string EnsureRoot(string root)
    {
        PrivateFileSystem.EnsurePrivateDirectory(root);
        return Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(root));
    }

    /// <summary>The per-job workspace path; the name is the job id so a job always lands in the same place.</summary>
    public static string PathFor(string root, Guid jobId)
    {
        return Path.Combine(root, jobId.ToString("N"));
    }

    /// <summary>Creates the per-job workspace with private permissions and refuses a pre-planted link in its place.</summary>
    public static string EnsureJobWorkspace(string root, Guid jobId)
    {
        var path = PathFor(root, jobId);

        if (OperatingSystem.IsWindows())
        {
            // Windows inherits the data directory ACL; still a fixed path the job cannot control.
            Directory.CreateDirectory(path);
        }
        else
        {
            Directory.CreateDirectory(path, PrivateDirectory);

            var info = new DirectoryInfo(path);
            info.Refresh();

            if (!info.Exists || info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("受管理的工作目錄不可為符號連結或 reparse point。");

            File.SetUnixFileMode(path, PrivateDirectory);
        }

        return path;
    }

    /// <summary>
    ///     Removes workspaces that belong to no existing job (deleted jobs, or ids from another database). Only entries whose
    ///     name is a job id are considered, links are removed as links, and failures are counted rather than thrown so a stuck
    ///     directory cannot keep the service from starting.
    /// </summary>
    public static int Reclaim(string root, IReadOnlySet<Guid> liveJobIds, Action<string, Exception>? onFailure = null)
    {
        if (!Directory.Exists(root)) return 0;

        var removed = 0;

        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            var name = Path.GetFileName(entry);

            if (!Guid.TryParseExact(name, "N", out var jobId) || liveJobIds.Contains(jobId)) continue;

            try
            {
                var attributes = File.GetAttributes(entry);

                // A link is removed as a link; a recursive delete is never handed something that could point elsewhere.
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    if ((attributes & FileAttributes.Directory) != 0)
                        Directory.Delete(entry, false);
                    else
                        File.Delete(entry);
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    Directory.Delete(entry, true);
                }
                else
                {
                    File.Delete(entry);
                }

                removed++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                onFailure?.Invoke(entry, exception);
            }
        }

        return removed;
    }
}
