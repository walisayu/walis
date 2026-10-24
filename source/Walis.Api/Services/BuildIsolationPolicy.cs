using System.Net;
using Microsoft.Data.Sqlite;
using Walis.Core.Backup;
using Walis.Core.Database;
using Walis.Core.Execution;
using Walis.Core.Models;

namespace Walis.Api.Services;

/// <summary>
///     Turns the system settings into the isolation parameters the executor needs. Walis's own data directory is always forbidden,
///     because no build script has a legitimate reason to read it: it holds the database, password hashes and cloud keys.
///     The sandbox additionally denies the installation (a build that can replace the executable or wwwroot owns the next
///     restart) and the service account's home directory (SSH keys, credential helpers, keychain files).
/// </summary>
public sealed class BuildIsolationPolicy(DataDirectory data, IHostEnvironment environment, IConfiguration configuration)
{
    /// <summary>The prefixes package managers install tools into; writable by the installing account on a developer machine.</summary>
    public static readonly string[] ToolPrefixes = ["/opt/homebrew", "/usr/local"];

    // The service's PATH does not change while it runs, and probing every directory costs a few stat/chmod calls per entry.
    private readonly Lazy<IReadOnlyList<string>> _writableSearchPath =
        new(() => WritableSearchPathDirectories(Environment.GetEnvironmentVariable("PATH")));

    public string? CreateProfile(WalisSettings settings)
    {
        return CreateProfile(settings, null, null);
    }

    /// <summary>
    ///     The profile for one build. The workspace is allowed again after every denial, so a job whose workspace sits under the
    ///     service account's home or inside the installation keeps access to the workspace alone. A trusted job gets no profile.
    /// </summary>
    public string? CreateProfile(WalisSettings settings, Job? job, string? workspace, IEnumerable<string>? allowedPaths = null)
    {
        if (!settings.IsolateBuilds || !BuildIsolation.IsSandboxAvailable) return null;

        if (job?.TrustedUnsandboxed == true) return null;

        return BuildIsolation.CreateProfile(SandboxDeniedPaths(settings), new SandboxProfileOptions
        {
            DenyNetwork = job?.DenyNetwork ?? false,
            DenyProcessControl = settings.SandboxDenyProcessControl,
            AllowedWorkspace = workspace,
            AllowedPaths = allowedPaths?.ToList() ?? [],
            WriteDeniedPaths = SandboxWriteDeniedPaths().ToList()
        });
    }

    /// <param name="allowedPaths">The managed workspace of this build when it lives under the (denied) data directory.</param>
    public string? CreateProfile(WalisSettings settings, IEnumerable<string> allowedPaths)
    {
        return CreateProfile(settings, null, null, allowedPaths);
    }

    /// <summary>
    ///     The protected paths for a build that runs in the managed workspace under the data directory. The data root itself
    ///     would overlap that workspace, so it is replaced by its other entries (database, artifacts, cache, backups…), which
    ///     keeps every one of them off limits to clean/cache/artifact I/O while the workspace passes the overlap check.
    /// </summary>
    public IReadOnlyList<string> ProtectedPathsForManagedWorkspace(WalisSettings settings, string workspacesRoot)
    {
        var carveOut = Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(workspacesRoot));
        var result = new List<string>();

        foreach (var path in DeniedPaths(settings))
        {
            string realPath;

            try
            {
                realPath = Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(path));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                result.Add(path);
                continue;
            }

            if (IsAncestor(realPath, carveOut) && Directory.Exists(realPath))
                AddEntriesExcept(realPath, carveOut, result);
            else
                result.Add(path);
        }

        return result.Distinct().ToList();
    }

    /// <summary>Lists a directory's entries except the one leading to <paramref name="carveOut" />, descending into it when it is an intermediate directory.</summary>
    private static void AddEntriesExcept(string directory, string carveOut, List<string> result)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var trimmed = Path.TrimEndingDirectorySeparator(entry);

            if (string.Equals(trimmed, carveOut, PathComparison)) continue;

            if (IsAncestor(trimmed, carveOut) && Directory.Exists(trimmed))
                AddEntriesExcept(trimmed, carveOut, result);
            else
                result.Add(entry);
        }
    }

    private static bool IsAncestor(string ancestor, string path)
    {
        return path.StartsWith(ancestor + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    ///     Storage the host process must keep builds away from: the workspace may neither contain nor sit inside any of these,
    ///     and they are denied in the sandbox. Deliberately excludes the home directory and the installation, which workspaces
    ///     legitimately live under; those are denied by the sandbox alone (<see cref="SandboxDeniedPaths" />).
    /// </summary>
    public IEnumerable<string> DeniedPaths(WalisSettings settings)
    {
        // This directory covers the default SQLite, artifacts, cache and default backups.
        yield return data.Root;

        // ConnectionStrings__Walis may place SQLite outside the content root (a production install does exactly that);
        // the journal/WAL sit in the same directory as the DB, so protect the whole directory rather than only the main file.
        foreach (var path in DatabaseDirectories()) yield return path;

        if (!string.IsNullOrWhiteSpace(settings.BackupDirectory))
            yield return Path.IsPathRooted(settings.BackupDirectory.Trim())
                ? settings.BackupDirectory.Trim()
                : Path.GetFullPath(settings.BackupDirectory.Trim(), environment.ContentRootPath);

        foreach (var target in settings.BackupTargets.Where(target =>
                     !target.IsDisabled &&
                     target.Provider == BackupProviders.Local &&
                     !string.IsNullOrWhiteSpace(target.Path)))
            yield return target.Path!.Trim();

        foreach (var path in SplitLines(settings.SandboxDeniedPaths)) yield return path;
    }

    /// <summary>
    ///     Everything the sandbox denies: the storage paths plus the installation, the service account's home and the action
    ///     pack directories (their JSON is shell that every job using the action runs; a build must not be able to edit it).
    /// </summary>
    public IEnumerable<string> SandboxDeniedPaths(WalisSettings settings)
    {
        foreach (var path in DeniedPaths(settings)) yield return path;

        foreach (var path in InstallationPaths(environment.ContentRootPath)) yield return path;

        if (HomeDirectory() is { } home) yield return home;

        foreach (var path in ActionPackDirectories()) yield return path;
    }

    /// <summary>
    ///     The directories <c>Walis:ActionPacks</c> lists. The pack directory under the data directory is covered by the data
    ///     root already; the configured ones usually sit outside it (the macOS install puts them at <c>$PREFIX/action-packs</c>
    ///     so the trusted "action pack update" job can git pull there), so they are denied one by one.
    /// </summary>
    public IEnumerable<string> ActionPackDirectories()
    {
        var configured = configuration.GetSection("Walis:ActionPacks").Get<string[]>() ?? [];

        foreach (var root in configured)
        {
            if (string.IsNullOrWhiteSpace(root)) continue;

            yield return Path.IsPathRooted(root.Trim())
                ? root.Trim()
                : Path.GetFullPath(root.Trim(), environment.ContentRootPath);
        }
    }

    /// <summary>
    ///     Paths the sandbox keeps readable but not writable: the package-manager prefixes that exist on this machine and every
    ///     directory on the service's PATH the service account can write to. The list is computed once per process.
    /// </summary>
    public IEnumerable<string> SandboxWriteDeniedPaths()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var prefix in ToolPrefixes)
            if (Directory.Exists(prefix) && seen.Add(prefix)) yield return prefix;

        foreach (var directory in _writableSearchPath.Value)
            if (seen.Add(directory)) yield return directory;
    }

    /// <summary>
    ///     The existing directories on <paramref name="searchPath" /> (PATH syntax) that the current account can write to.
    ///     World- or group-writable counts as writable without further checks (a false positive only costs a build the ability to
    ///     write there, which it never needs); owner-writable counts when the account owns the directory, which is probed with a
    ///     no-op chmod: chmod succeeds only for the owner (or root), and .NET has no portable owner lookup.
    /// </summary>
    public static IReadOnlyList<string> WritableSearchPathDirectories(string? searchPath)
    {
        var result = new List<string>();

        if (string.IsNullOrWhiteSpace(searchPath) || OperatingSystem.IsWindows()) return result;

        foreach (var entry in searchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string full;

            try
            {
                full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry));
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (!Directory.Exists(full) || result.Contains(full, StringComparer.Ordinal)) continue;

            if (IsWritableByCurrentUser(full)) result.Add(full);
        }

        return result;
    }

    public static bool IsWritableByCurrentUser(string directory)
    {
        if (OperatingSystem.IsWindows()) return false;

        UnixFileMode mode;

        try
        {
            mode = File.GetUnixFileMode(directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return false;
        }

        if ((mode & (UnixFileMode.OtherWrite | UnixFileMode.GroupWrite)) != 0) return true;

        if ((mode & UnixFileMode.UserWrite) == 0) return false;

        try
        {
            // Setting the mode it already has changes nothing, but the call fails unless this account owns the directory.
            File.SetUnixFileMode(directory, mode);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    ///     The content root, and for the macOS layout <c>$PREFIX/releases/&lt;release&gt;</c> also the whole <c>releases</c>
    ///     directory, the <c>current</c> link and <c>$PREFIX/var</c> (the restart trigger lives there).
    /// </summary>
    public static IEnumerable<string> InstallationPaths(string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(contentRoot)) yield break;

        string full;
        string real;

        try
        {
            full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(contentRoot));
            real = Path.TrimEndingDirectorySeparator(BuildIsolation.RealPath(full));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException
                                              or NotSupportedException)
        {
            yield break;
        }

        yield return full;

        var releases = Path.GetDirectoryName(real);

        if (releases is null ||
            !string.Equals(Path.GetFileName(releases), "releases", StringComparison.OrdinalIgnoreCase)) yield break;

        yield return releases;

        var prefix = Path.GetDirectoryName(releases);

        if (prefix is null) yield break;

        var current = Path.Combine(prefix, "current");
        if (Directory.Exists(current) || File.Exists(current)) yield return current;

        var var = Path.Combine(prefix, "var");
        if (Directory.Exists(var)) yield return var;
    }

    public static string? HomeDirectory()
    {
        try
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(home) || !Directory.Exists(home) ? null : Path.GetFullPath(home);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> AllowedDirectories(WalisSettings settings)
    {
        return SplitLines(settings.AllowedWorkingDirectories);
    }

    /// <summary>
    ///     Reopening a workspace below a sandbox-denied root requires an administrator's explicit allowlist.
    ///     Otherwise an operator could select the service account's .ssh directory or wwwroot as a workspace.
    ///     Managed workspaces are added to the allowlist by the worker, never by job input.
    /// </summary>
    public bool RequiresWorkspaceAllowlist(WalisSettings settings, Job job, string workspace)
    {
        return !job.TrustedUnsandboxed &&
               !BuildIsolation.IsDisjointFrom(workspace, SandboxDeniedPaths(settings));
    }

    /// <summary>
    ///     Whether a build must be refused when no isolation boundary can be applied. Independent of
    ///     <see cref="WalisSettings.IsolateBuilds" />: that one decides whether the sandbox is used where it exists, this one
    ///     decides what happens where it does not.
    /// </summary>
    public static bool RequiresSandbox(WalisSettings settings)
    {
        return !settings.AllowUnisolatedBuilds;
    }

    /// <summary>
    ///     The value <see cref="WalisSettings.AllowUnisolatedBuilds" /> gets on a database that has never stored settings.
    ///     A Production server that listens on a non-loopback address is reachable by others before anyone has reviewed the
    ///     isolation page, so it starts fail closed; development, tests and loopback-only servers keep the convenient default.
    ///     Settings already stored are never changed by this.
    /// </summary>
    public static bool AllowsUnisolatedBuildsByDefault(bool isProduction, string? listeningUrls)
    {
        return !(isProduction && ListensOnNonLoopback(listeningUrls));
    }

    /// <summary>
    ///     Reads the <c>urls</c> value (<c>--urls</c>, <c>ASPNETCORE_URLS</c>). Unset means Kestrel's localhost default.
    ///     <c>*</c>, <c>+</c>, <c>0.0.0.0</c>, <c>[::]</c> and any name other than localhost count as non-loopback.
    /// </summary>
    public static bool ListensOnNonLoopback(string? listeningUrls)
    {
        if (string.IsNullOrWhiteSpace(listeningUrls)) return false;

        foreach (var entry in listeningUrls.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            BindingAddress address;

            try
            {
                address = BindingAddress.Parse(entry);
            }
            catch (FormatException)
            {
                // An address Kestrel cannot bind is not an exposure; the server fails to start instead.
                continue;
            }

            if (address.IsUnixPipe || address.IsNamedPipe) continue;

            var host = address.Host.Trim('[', ']');

            if (host is "*" or "+") return true;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) continue;

            if (IPAddress.TryParse(host, out var ip))
            {
                if (!IPAddress.IsLoopback(ip)) return true;

                continue;
            }

            return true;
        }

        return false;
    }

    public static bool IsExternallyIsolatedRunner(WalisSettings settings, string runnerName)
    {
        return SplitLines(settings.ExternallyIsolatedRunners)
            .Contains(runnerName, StringComparer.OrdinalIgnoreCase);
    }

    private IEnumerable<string> DatabaseDirectories()
    {
        var fallback = $"Data Source={data.Combine("walis.db")}";
        var connectionString = configuration.GetConnectionString("Walis") ?? fallback;

        if (DatabaseProviders.Resolve(configuration["Database:Provider"], connectionString) !=
            DatabaseProvider.Sqlite) yield break;

        string dataSource;

        try
        {
            dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
        }
        catch (ArgumentException)
        {
            // The database factory rejects an invalid string at startup too; the isolation setting neither swallows nor guesses it here.
            yield break;
        }

        if (string.IsNullOrWhiteSpace(dataSource) ||
            dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase) ||
            dataSource.StartsWith("file::memory:", StringComparison.OrdinalIgnoreCase))
            yield break;

        string databasePath;

        if (Uri.TryCreate(dataSource, UriKind.Absolute, out var uri) && uri.IsFile)
            databasePath = uri.LocalPath;
        else
            databasePath = Path.IsPathRooted(dataSource)
                ? dataSource
                : Path.GetFullPath(dataSource, environment.ContentRootPath);

        if (Path.GetDirectoryName(Path.GetFullPath(databasePath)) is { } directory) yield return directory;
    }

    private static List<string> SplitLines(string? value)
    {
        return
        [
            .. (value ?? string.Empty)
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith('#'))
        ];
    }
}
