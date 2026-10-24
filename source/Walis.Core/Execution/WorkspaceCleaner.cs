namespace Walis.Core.Execution;

/// <summary>
///     Clears the workspace contents before a build. Deletion is irreversible, so obviously dangerous paths are always refused,
///     and what was actually done is written to the build log.
/// </summary>
public static class WorkspaceCleaner
{
    public static string Clean(string workingDirectory, IReadOnlyList<string>? allowedDirectories = null)
    {
        var explicitlyAllowlisted = false;

        if (allowedDirectories is not null &&
            !BuildIsolation.TryResolveWorkingDirectory(
                workingDirectory,
                allowedDirectories,
                true,
                out workingDirectory,
                out var validationFailure))
            return $"略過清空工作目錄：{validationFailure}";

        if (allowedDirectories is not null) explicitlyAllowlisted = true;

        if (!IsSafe(workingDirectory, explicitlyAllowlisted, out var reason)) return $"略過清空工作目錄：{reason}";

        var removed = 0;
        var failures = new List<string>();

        foreach (var entry in Directory.EnumerateFileSystemEntries(workingDirectory))
            try
            {
                var attributes = File.GetAttributes(entry);

                // Only the link node itself is removed; a recursive delete is never handed a path that could be redirected.
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
            catch (Exception exception)
            {
                failures.Add($"{Path.GetFileName(entry)}（{exception.Message}）");
            }

        var message = $"已清空工作目錄，移除 {removed} 個項目。";
        return failures.Count == 0 ? message : $"{message} 無法移除：{string.Join('、', failures)}";
    }

    /// <summary>Allows only paths that are deep enough and are neither the home directory nor a drive root.</summary>
    public static bool IsSafe(string path, out string reason)
    {
        return IsSafe(path, false, out reason);
    }

    private static bool IsSafe(string path, bool explicitlyAllowlisted, out string reason)
    {
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "未指定工作目錄。";
            return false;
        }

        string full;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception)
        {
            reason = exception.Message;
            return false;
        }

        if (full == Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty))
        {
            reason = "不能清空磁碟根目錄。";
            return false;
        }

        if (Directory.Exists(full))
            try
            {
                var info = new DirectoryInfo(full);
                info.Refresh();

                if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    reason = "工作目錄不可為符號連結或 reparse point。";
                    return false;
                }
            }
            catch (IOException)
            {
                reason = "無法確認工作目錄是否為符號連結。";
                return false;
            }

        var home = Path.TrimEndingDirectorySeparator(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        // Common APFS and Windows volumes are case-insensitive; always ignoring case at worst rejects one similarly named directory
        // on a case-sensitive volume, but never lets the home directory slip through with different casing.
        if (!string.IsNullOrEmpty(home) &&
            string.Equals(
                BuildIsolation.RealPath(full),
                BuildIsolation.RealPath(home),
                StringComparison.OrdinalIgnoreCase))
        {
            reason = "不能清空使用者家目錄。";
            return false;
        }

        var depth = full.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries).Length;

        if (depth < 2 && !explicitlyAllowlisted)
        {
            reason = "工作目錄層級過淺，為避免誤刪不予清空。";
            return false;
        }

        return true;
    }
}