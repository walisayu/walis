namespace Walis.Api.Services;

/// <summary>
///     Walis's own data directory: the default SQLite, artifacts, build cache, and the staging areas for backup and restore all live here.
///     Defaults to <c>data</c> under the content root; a deployment may point <c>Walis:DataDirectory</c>
///     (environment variable <c>Walis__DataDirectory</c>) at a real directory outside the release so data is shared across versions.
///     It must be a real directory: the private-directory check at startup rejects symbolic links, so a link cannot be used for sharing.
/// </summary>
public sealed class DataDirectory(string root)
{
    public const string ConfigurationKey = "Walis:DataDirectory";

    public string Root { get; } = root;

    /// <summary>Where jobs without a working directory of their own get their workspace, one subdirectory per job id.</summary>
    public string WorkspacesRoot => Combine(Core.Execution.ManagedWorkspaces.DirectoryName);

    public string Combine(params string[] paths)
    {
        return Path.Combine([Root, .. paths]);
    }

    public static string Resolve(IHostEnvironment environment, IConfiguration configuration)
    {
        var configured = configuration[ConfigurationKey];
        return string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(environment.ContentRootPath, "data")
            : Path.GetFullPath(configured.Trim(), environment.ContentRootPath);
    }
}