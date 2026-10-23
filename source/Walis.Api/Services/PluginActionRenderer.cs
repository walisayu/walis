using Walis.Core.Actions;
using Walis.Core.Plugins;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>
///     The script source for actions: plugin-provided ones go through JavaScript, the rest through templates.
///     Plugins are loaded once up front so compilation needs no asynchronous queries.
/// </summary>
public sealed class PluginActionRenderer(
    IReadOnlyDictionary<string, Plugin> plugins,
    PluginHost host,
    IReadOnlyDictionary<string, string> variables) : IActionRenderer
{
    public string Render(ActionDefinition definition, IReadOnlyDictionary<string, string?> values)
    {
        if (definition.PluginId is null) return TemplateActionRenderer.Instance.Render(definition, values);

        if (!plugins.TryGetValue(definition.PluginId, out var plugin))
            throw new PluginException($"外掛 {definition.PluginId} 沒有安裝或已停用。");

        return host.Render(new PluginRenderRequest(plugin, values, variables)).Script;
    }
}

/// <summary>Creates a <see cref="PluginActionRenderer" />, taking care of loading the plugins.</summary>
public sealed class PluginActionRendererFactory(IPluginRepository plugins, PluginHost host)
{
    public async Task<IActionRenderer> CreateAsync(
        IReadOnlyDictionary<string, string>? variables = null,
        CancellationToken cancellationToken = default)
    {
        var enabled = await plugins.ListEnabledAsync(cancellationToken);

        return new PluginActionRenderer(
            enabled.ToDictionary(plugin => plugin.PluginId, StringComparer.Ordinal),
            host,
            variables ?? new Dictionary<string, string>());
    }
}