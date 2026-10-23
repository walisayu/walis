namespace Walis.Core.Actions;

public static class ActionParameterType
{
    public const string Text = "text";
    public const string Multiline = "multiline";
    public const string Number = "number";
    public const string Boolean = "boolean";
    public const string Select = "select";
    public const string Path = "path";

    public static bool IsValid(string? value)
    {
        return value is Text or Multiline or Number or Boolean or Select or Path;
    }
}

public sealed class ActionParameter
{
    public string Name { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public string Type { get; init; } = ActionParameterType.Text;
    public bool Required { get; init; }
    public string? Default { get; init; }
    public string? Placeholder { get; init; }
    public string? Help { get; init; }

    /// <summary>The choices when type is select.</summary>
    public IReadOnlyList<ActionOption> Options { get; init; } = [];
}

public sealed record ActionOption(string Value, string Label);

/// <summary>Outputs the action declares it writes to <c>$WALIS_OUTPUT_FILE</c>; only for the UI and docs, not enforced at run time.</summary>
public sealed record ActionOutput(string Name, string Description);

/// <summary>
///     An action that can be dropped into a job flow. The definition itself is pure data:
///     built-in actions live in <see cref="BuiltInActions" />, custom actions are installed into the database as JSON in the same shape.
/// </summary>
public sealed class ActionDefinition
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string Category { get; init; } = "general";
    public string Icon { get; init; } = "cil-terminal";
    public string Version { get; init; } = "1.0.0";
    public string Author { get; init; } = string.Empty;
    public IReadOnlyList<ActionParameter> Parameters { get; init; } = [];

    /// <summary>The outputs this action produces; later steps can reference them with <c>${{ steps.&lt;key&gt;.&lt;name&gt; }}</c>.</summary>
    public IReadOnlyList<ActionOutput> Outputs { get; init; } = [];

    /// <summary>The template that renders the shell fragment; syntax in <see cref="ActionTemplate" />. Empty for plugin-provided actions.</summary>
    public string Template { get; init; } = string.Empty;

    /// <summary>The plugin id when provided by a plugin; the script then comes from the plugin's render function instead of a template.</summary>
    public string? PluginId { get; init; }

    /// <summary>Source: null for built-in and database-installed actions, <c>pack:&lt;pack name&gt;</c> for actions from an action pack.</summary>
    public string? Source { get; init; }

    public bool IsBuiltIn { get; set; }
}

public static class ActionCategory
{
    public const string General = "general";
    public const string File = "file";
    public const string Git = "git";
    public const string Node = "node";
    public const string Dotnet = "dotnet";
    public const string Container = "container";
    public const string Deploy = "deploy";
    public const string Check = "check";
    public const string Python = "python";
    public const string Java = "java";
    public const string Go = "go";
    public const string Rust = "rust";
    public const string Ruby = "ruby";
    public const string Php = "php";
    public const string Database = "database";
    public const string Cloud = "cloud";
    public const string Kubernetes = "kubernetes";
    public const string Test = "test";
    public const string Security = "security";
    public const string Notify = "notify";
    public const string Archive = "archive";

    public static readonly IReadOnlyList<string> All =
    [
        General, File, Git, Node, Dotnet, Python, Java, Go, Rust, Ruby, Php,
        Container, Kubernetes, Database, Cloud, Deploy, Test, Check, Security, Notify, Archive
    ];
}