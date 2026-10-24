using Walis.Core.Plugins;

namespace Walis.Core.Repositories;

public interface IPluginRepository
{
    Task<IReadOnlyList<Plugin>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns only enabled plugins; disabled ones must not affect the action list or builds.</summary>
    Task<IReadOnlyList<Plugin>> ListEnabledAsync(CancellationToken cancellationToken = default);

    Task<Plugin?> GetAsync(string pluginId, CancellationToken cancellationToken = default);

    Task<Plugin> InstallAsync(PluginDraft draft, CancellationToken cancellationToken = default);

    Task<Plugin?> SetDisabledAsync(string pluginId, bool disabled, CancellationToken cancellationToken = default);

    Task<Plugin?> SetTrustedAsync(string pluginId, bool trusted, CancellationToken cancellationToken = default);

    Task<bool> UninstallAsync(string pluginId, CancellationToken cancellationToken = default);
}