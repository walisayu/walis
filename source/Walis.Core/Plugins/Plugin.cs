namespace Walis.Core.Plugins;

/// <summary>A plugin installed on this Walis instance.</summary>
public sealed record Plugin
{
    public Guid Id { get; init; }
    public string PluginId { get; init; } = string.Empty;
    public PluginManifest Manifest { get; init; } = new();
    public string Code { get; init; } = string.Empty;
    public bool IsDisabled { get; init; }

    /// <summary>Only trusted plugins may do more than produce shell; plugins are installed restricted by default.</summary>
    public bool IsTrusted { get; init; }

    public DateTime InstalledAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record PluginDraft(PluginManifest Manifest, string Code, bool IsTrusted);