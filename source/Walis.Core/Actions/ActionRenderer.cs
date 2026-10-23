namespace Walis.Core.Actions;

/// <summary>
///     Turns an action and its parameters into a shell fragment. Built-in and custom actions use templates; plugin-provided actions use JavaScript,
///     with the API layer swapping in an implementation that can run plugins, so the compiler itself never needs to know plugins exist.
/// </summary>
public interface IActionRenderer
{
    string Render(ActionDefinition definition, IReadOnlyDictionary<string, string?> values);
}

public sealed class TemplateActionRenderer : IActionRenderer
{
    public static readonly TemplateActionRenderer Instance = new();

    public string Render(ActionDefinition definition, IReadOnlyDictionary<string, string?> values)
    {
        return ActionTemplate.Render(definition.Template, values.ToDictionary(pair => pair.Key, pair => pair.Value));
    }
}