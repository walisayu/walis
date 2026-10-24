using System.Security;
using System.Text;

namespace Walis.Core.Execution;

/// <summary>
///     Build isolation. Walis runs user-written scripts, and by default they run as the same identity as the service,
///     which means they can read Walis's own database (including password hashes and cloud credentials). Two defenses live here:
///     1. Workspace allowlist: jobs may only run under permitted directories.
///     2. macOS sandbox: <c>sandbox-exec</c> blocks reads and writes to the listed paths.
///     Neither is full isolation (same user, same machine), but they are enough to stop the most realistic risk:
///     a build script casually reading the database or credentials. Stronger isolation needs containers or a separate account.
/// </summary>
/// <summary>What the sandbox profile denies besides the listed paths.</summary>
public sealed record SandboxProfileOptions
{
    /// <summary>Denies every network operation; the job option "拒絕網路".</summary>
    public bool DenyNetwork { get; init; }

    /// <summary>Adds <see cref="BuildIsolation.ProcessControlRules" />; the system setting SandboxDenyProcessControl.</summary>
    public bool DenyProcessControl { get; init; } = true;

    /// <summary>The workspace, allowed again after the denials so a workspace under a denied directory keeps working.</summary>
    public string? AllowedWorkspace { get; init; }

    /// <summary>
    ///     Further paths inside a denied one that the step may still use, e.g. the managed workspace under the data directory.
    ///     In SBPL the last matching rule wins, so these allows are appended after the denies.
    /// </summary>
    public IReadOnlyList<string> AllowedPaths { get; init; } = [];

    /// <summary>
    ///     Paths a build may read and execute but never write: the Homebrew prefix, <c>/usr/local</c> and every directory on the
    ///     service's PATH that the service account can write to. A build that could replace <c>node</c> or <c>git</c> there would
    ///     run as the service account the next time a trusted job or the service itself calls the tool (CWE-426).
    /// </summary>
    public IReadOnlyList<string> WriteDeniedPaths { get; init; } = [];
}

public static class BuildIsolation
{
    public const string SandboxExecutable = "/usr/bin/sandbox-exec";

    public static bool IsSandboxAvailable => OperatingSystem.IsMacOS() && File.Exists(SandboxExecutable);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    /// <summary>
    ///     Resolves and validates the workspace before running a command or letting the host process touch the workspace.
    ///     Host-side cleanup and caching are not inside the build sandbox, so this check must happen before that I/O.
    /// </summary>
    public static bool TryResolveWorkingDirectory(
        string workingDirectory,
        IReadOnlyList<string> allowed,
        bool requireAllowlist,
        out string resolved,
        out string reason)
    {
        resolved = string.Empty;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            reason = "未指定工作目錄。";
            return false;
        }

        string full;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory.Trim()));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                              or PathTooLongException)
        {
            reason = $"工作目錄路徑無效：{exception.Message}";
            return false;
        }

        if (!Directory.Exists(full))
        {
            reason = $"工作目錄不存在：{full}";
            return false;
        }

        if (IsLink(new DirectoryInfo(full)))
        {
            reason = "工作目錄本身不可為符號連結或 reparse point。";
            return false;
        }

        // Always use the physical path from here on, never the original pathname that may contain system aliases
        // (for example /tmp -> /private/tmp) or intermediate links a user could replace.
        try
        {
            resolved = Path.TrimEndingDirectorySeparator(RealPath(full));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            reason = $"無法安全解析工作目錄：{exception.Message}";
            return false;
        }

        if (requireAllowlist && allowed.Count == 0)
        {
            reason = "使用此工作目錄前必須設定允許的工作目錄。";
            resolved = string.Empty;
            return false;
        }

        if (allowed.Count == 0) return true;

        foreach (var configuredRoot in allowed.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            string root;

            try
            {
                root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configuredRoot.Trim()));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException
                                                  or PathTooLongException)
            {
                continue;
            }

            string realRoot;

            try
            {
                realRoot = Path.TrimEndingDirectorySeparator(RealPath(root));
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SecurityException)
            {
                continue;
            }

            var literalPathIsSafe = IsInside(full, root) && !ContainsLink(root, full);
            var canonicalPathIsSafe = IsInside(full, realRoot) && !ContainsLink(realRoot, full);

            // The allowlist entry itself and every segment below it must not be made of links. Checking the literal path first
            // stops the "link still points inside the allowlist" case that realpath containment cannot see.
            // The second branch lets a canonical path normalized in a previous round pass the check again.
            if ((literalPathIsSafe || canonicalPathIsSafe) && IsInside(resolved, realRoot)) return true;
        }

        reason = "工作目錄不在系統設定允許的範圍內，或路徑包含符號連結/reparse point。";
        resolved = string.Empty;
        return false;
    }

    /// <summary>
    ///     Rules that stop a build from reaching outside its own process tree: signalling the controller or other builds,
    ///     looking up Mach services (launchd, keychain and other daemons) and reading other processes' arguments and environment
    ///     (<c>ps -E</c>). Probed on macOS 15/26: zsh, bash, git, dotnet, node, python and curl all still run, and a script may still
    ///     kill its own children. <c>(deny sysctl-read)</c> is deliberately absent: it stops even the shell from starting.
    /// </summary>
    public const string ProcessControlRules =
        "(deny signal (target others))(deny mach-lookup)(deny process-info* (target others))";

    public const string NetworkRule = "(deny network*)";

    /// <summary>Builds a profile that only denies the listed paths; null when there is nothing to deny.</summary>
    public static string? CreateProfile(IEnumerable<string> deniedPaths)
    {
        return CreateProfile(deniedPaths, new SandboxProfileOptions { DenyProcessControl = false });
    }

    /// <summary>Denies the listed paths and allows <paramref name="allowedPaths" /> again (the managed workspace under the data directory).</summary>
    public static string? CreateProfile(IEnumerable<string> deniedPaths, IEnumerable<string> allowedPaths)
    {
        return CreateProfile(deniedPaths, new SandboxProfileOptions { DenyProcessControl = false, AllowedPaths = allowedPaths.ToList() });
    }

    /// <summary>
    ///     Builds the sandbox-exec profile. Paths are always resolved to their real locations:
    ///     <c>/tmp</c> is a symbolic link to <c>/private/tmp</c>, and a rule written with the former would not apply.
    ///     Later rules win in sandbox profiles, so the workspace and the other allowed paths come last: a job whose workspace
    ///     sits under a denied directory (the service account's home, the installation, the data directory) keeps access to it alone.
    /// </summary>
    public static string? CreateProfile(IEnumerable<string> deniedPaths, SandboxProfileOptions options)
    {
        var resolved = ResolveAll(deniedPaths);
        var writeDenied = ResolveAll(options.WriteDeniedPaths);

        if (resolved.Count == 0 && writeDenied.Count == 0 && !options.DenyNetwork && !options.DenyProcessControl) return null;

        var profile = new StringBuilder("(version 1)(allow default)");

        if (options.DenyProcessControl) profile.Append(ProcessControlRules);

        if (options.DenyNetwork) profile.Append(NetworkRule);

        foreach (var path in resolved) profile.Append($"(deny file-read* file-write* (subpath \"{Escape(path)}\"))");

        // Tool prefixes stay readable (builds run node/git/dotnet from there) but not writable; the workspace below re-allows
        // writes only for a workspace that happens to sit under one of them.
        foreach (var path in writeDenied) profile.Append($"(deny file-write* (subpath \"{Escape(path)}\"))");

        var allowed = new List<string>(options.AllowedPaths);
        if (!string.IsNullOrWhiteSpace(options.AllowedWorkspace)) allowed.Add(options.AllowedWorkspace);

        foreach (var path in ResolveAll(allowed))
            profile.Append($"(allow file-read* file-write* (subpath \"{Escape(path)}\"))");

        return profile.ToString();
    }

    /// <summary>Lists both the path and its symbolic-link target; both must be blocked.</summary>
    public static IReadOnlyList<string> ResolveAll(IEnumerable<string> paths)
    {
        var resolved = new List<string>();

        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;

            var full = Path.GetFullPath(path.Trim());
            Add(resolved, full);
            Add(resolved, RealPath(full));
        }

        return resolved;
    }

    /// <summary>The workspace must sit under one of the allowlisted directories; an empty allowlist means no restriction.</summary>
    public static bool IsWorkingDirectoryAllowed(string workingDirectory, IReadOnlyList<string> allowed)
    {
        if (allowed.Count == 0) return true;

        var target = Normalize(workingDirectory);

        return allowed
            .Select(Normalize)
            .Any(root => string.Equals(target, root, PathComparison) ||
                         target.StartsWith(WithTrailingSeparator(root), PathComparison));
    }

    /// <summary>
    ///     The path must neither be inside any protected path nor contain one. The latter matters: if workspace=/app were allowed
    ///     while the database lives in /app/data, the artifact/cache host process could still be used by a script to move data out.
    /// </summary>
    public static bool IsDisjointFrom(string path, IEnumerable<string> protectedPaths)
    {
        foreach (var protectedPath in protectedPaths)
        {
            if (string.IsNullOrWhiteSpace(protectedPath)) continue;

            try
            {
                if (PathsOverlap(path, protectedPath)) return false;
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Fail closed when a path in the protected list cannot be resolved reliably.
                return false;
            }
        }

        return true;
    }

    public static bool PathsOverlap(string first, string second)
    {
        var literalFirst = Path.TrimEndingDirectorySeparator(Path.GetFullPath(first.Trim()));
        var literalSecond = Path.TrimEndingDirectorySeparator(Path.GetFullPath(second.Trim()));
        var realFirst = Normalize(literalFirst);
        var realSecond = Normalize(literalSecond);

        return IsInside(literalFirst, literalSecond) || IsInside(literalSecond, literalFirst) ||
               IsInside(realFirst, realSecond) || IsInside(realSecond, realFirst);
    }

    public static bool PathsEqual(string first, string second)
    {
        return string.Equals(Normalize(first), Normalize(second), PathComparison);
    }

    private static string Normalize(string path)
    {
        return Path.TrimEndingDirectorySeparator(RealPath(Path.GetFullPath(path.Trim())));
    }

    /// <summary>Checks whether any redirectable file-system node exists from root to target (both inclusive).</summary>
    public static bool ContainsLink(string root, string target)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullTarget = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));

        if (!IsInside(fullTarget, fullRoot)) return true;

        var current = fullRoot;

        if (TryGetInfo(current, out var rootInfo) && IsLink(rootInfo)) return true;

        var relative = Path.GetRelativePath(fullRoot, fullTarget);

        if (relative == ".") return false;

        foreach (var segment in relative.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            if (TryGetInfo(current, out var info) && IsLink(info)) return true;
        }

        return false;
    }

    /// <summary>
    ///     Resolves symbolic links segment by segment. Looking only at the last segment is not enough: on macOS <c>/var/folders/…</c>
    ///     is linked at <c>/var</c>, and a sandbox rule written with the unresolved path would have no effect at all.
    /// </summary>
    public static string RealPath(string path)
    {
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full) ?? Path.DirectorySeparatorChar.ToString();
        var remainder = full[current.Length..];

        foreach (var segment in remainder.Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);

            FileSystemInfo info = Directory.Exists(current)
                ? new DirectoryInfo(current)
                : new FileInfo(current);

            try
            {
                if (info.ResolveLinkTarget(true) is { } target)
                    current = Path.IsPathRooted(target.FullName)
                        ? target.FullName
                        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current) ?? "/", target.FullName));
            }
            catch (Exception exception) when (
                exception is FileNotFoundException or DirectoryNotFoundException)
            {
                // A trailing node that does not exist yet has no link to resolve; errors on nodes that exist but cannot be inspected must not be swallowed.
            }
        }

        return current;
    }

    private static bool IsInside(string path, string root)
    {
        return string.Equals(path, root, PathComparison) ||
               path.StartsWith(WithTrailingSeparator(root), PathComparison);
    }

    private static string WithTrailingSeparator(string path)
    {
        return Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;
    }

    private static bool TryGetInfo(string path, out FileSystemInfo info)
    {
        info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);

        try
        {
            info.Refresh();
            return info.Exists || info.LinkTarget is not null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Callers treat true as "this node is unsafe to use"; a failed check must fail closed.
            return true;
        }
    }

    private static bool IsLink(FileSystemInfo info)
    {
        try
        {
            return info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            // Fail closed when the node cannot be read reliably during validation.
            return true;
        }
    }

    private static void Add(List<string> paths, string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar);

        if (trimmed.Length != 0 && !paths.Contains(trimmed, StringComparer.Ordinal)) paths.Add(trimmed);
    }

    private static string Escape(string path)
    {
        return path.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
