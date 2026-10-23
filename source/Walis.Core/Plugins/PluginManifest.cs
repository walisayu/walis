using Walis.Core.Actions;

namespace Walis.Core.Plugins;

public static class PluginKinds
{
    /// <summary>Provides an action that can be dropped into a job flow and returns a shell fragment.</summary>
    public const string Action = "action";

    public static bool IsValid(string? value)
    {
        return string.Equals(value, Action, StringComparison.Ordinal);
    }
}

/// <summary>The capabilities the plugin declares it needs. Undeclared means unavailable, and declared still needs admin consent at install time.</summary>
public sealed class PluginPermissions
{
    /// <summary>Hosts the plugin may connect to, e.g. https://example.com; empty means no outbound access.</summary>
    public List<string> Http { get; set; } = [];

    /// <summary>Names of secrets the plugin may use. It never sees the values and can only ask for them to be put into request headers.</summary>
    public List<string> Secrets { get; set; } = [];

    /// <summary>Whether the key-value store is allowed.</summary>
    public bool Kv { get; set; }

    public bool RequiresApproval => Http.Count != 0 || Secrets.Count != 0 || Kv;
}

/// <summary>
///     The plugin's declaration data. The code is stored separately because it is usually long and the list page does not need it.
/// </summary>
public sealed class PluginManifest
{
    /// <summary>A lowercase identifier with at least one dot, e.g. acme.deploy.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Version { get; set; } = "1.0.0";
    public string Author { get; set; } = string.Empty;

    /// <summary>The host API version; not loaded when it differs from the current one.</summary>
    public int ApiVersion { get; set; } = 1;

    public string Kind { get; set; } = PluginKinds.Action;

    /// <summary>The category and icon shown in the action panel, reusing the action definition fields.</summary>
    public string Category { get; set; } = "general";

    public string Icon { get; set; } = "cil-code";

    /// <summary>Parameter declarations are pure data, so listing actions never has to run JS first.</summary>
    public List<ActionParameter> Parameters { get; set; } = [];

    public PluginPermissions Permissions { get; set; } = new();

    /// <summary>Converts to an action definition so the plugin appears directly in the flow editor's action panel.</summary>
    public ActionDefinition ToActionDefinition()
    {
        return new ActionDefinition
        {
            Id = Id,
            Name = Name,
            Description = Description,
            Category = Category,
            Icon = Icon,
            Version = Version,
            Author = Author,
            Parameters = Parameters,
            Template = string.Empty,
            PluginId = Id
        };
    }
}