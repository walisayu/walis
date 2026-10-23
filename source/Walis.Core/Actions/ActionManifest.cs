using System.Text.Json;
using System.Text.RegularExpressions;

namespace Walis.Core.Actions;

/// <summary>
///     Validates a third-party action's JSON definition. An action can only declare parameters and a template,
///     never carry code, so installing one is as risky as pasting a shell template.
/// </summary>
public static partial class ActionManifest
{
    public const int MaximumManifestLength = 262_144;
    public const int MaximumParameters = 64;
    public const int MaximumOptionsPerParameter = 100;
    public static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public static bool TryParse(string json, out ActionDefinition? definition, out IReadOnlyList<string> errors)
    {
        definition = null;
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(json) || json.Length > MaximumManifestLength)
        {
            errors = [$"動作定義不可為空，且不可超過 {MaximumManifestLength:N0} 個字元。"];
            return false;
        }

        ActionDefinition? parsed;

        try
        {
            parsed = JsonSerializer.Deserialize<ActionDefinition>(json, SerializerOptions);
        }
        catch (JsonException exception)
        {
            errors = [$"無法解析 JSON：{exception.Message}"];
            return false;
        }

        if (parsed is null)
        {
            errors = ["內容是空的。"];
            return false;
        }

        var id = parsed.Id ?? string.Empty;
        var name = parsed.Name ?? string.Empty;
        var template = parsed.Template ?? string.Empty;
        var parameters = parsed.Parameters ?? [];
        var category = parsed.Category ?? string.Empty;

        if (id.Length > 128 || !IdPattern().IsMatch(id))
            problems.Add("id 必須是最多 128 字元、像 team.deploy 這樣的小寫識別字，且至少包含一個點。");

        if (string.IsNullOrWhiteSpace(name))
            problems.Add("name 不可為空。");
        else if (name.Length > 200) problems.Add("name 不可超過 200 個字元。");

        if (string.IsNullOrWhiteSpace(template))
            problems.Add("template 不可為空。");
        else if (template.Length > 100_000) problems.Add("template 不可超過 100,000 個字元。");

        if ((parsed.Description?.Length ?? 0) > 10_000 ||
            (parsed.Category?.Length ?? 0) > 64 ||
            (parsed.Icon?.Length ?? 0) > 200 ||
            (parsed.Version?.Length ?? 0) > 64 ||
            (parsed.Author?.Length ?? 0) > 200)
            problems.Add("動作的說明、分類、圖示、版本或作者欄位過長。");

        if (BuiltInActions.All.Any(action => action.Id == id)) problems.Add("這個 id 與內建動作重複，請換一個。");

        var names = new HashSet<string>(StringComparer.Ordinal);

        if (parameters.Count > MaximumParameters) problems.Add($"動作最多 {MaximumParameters} 個參數。");

        foreach (var parameter in parameters.Take(MaximumParameters + 1))
        {
            if (parameter is null)
            {
                problems.Add("參數定義不可為空。");
                continue;
            }

            var parameterName = parameter.Name ?? string.Empty;

            if (parameterName.Length > 128 || !ParameterNamePattern().IsMatch(parameterName))
            {
                problems.Add("參數名稱只能使用英數字與底線，且不可超過 128 個字元。");
                continue;
            }

            if (!names.Add(parameterName)) problems.Add($"參數「{parameterName}」重複定義。");

            if (!ActionParameterType.IsValid(parameter.Type))
                problems.Add($"參數「{parameter.Name}」的型別 {parameter.Type} 不支援。");

            if ((parameter.Label?.Length ?? 0) > 200 ||
                (parameter.Type?.Length ?? 0) > 32 ||
                (parameter.Default?.Length ?? 0) > 4_096 ||
                (parameter.Placeholder?.Length ?? 0) > 4_096 ||
                (parameter.Help?.Length ?? 0) > 10_000)
                problems.Add($"參數「{parameter.Name}」的顯示或預設內容過長。");

            var options = parameter.Options ?? [];
            if (options.Count > MaximumOptionsPerParameter)
                problems.Add($"參數「{parameter.Name}」最多 {MaximumOptionsPerParameter} 個選項。");

            if (options.Take(MaximumOptionsPerParameter + 1).Any(option => option is null ||
                                                                           option.Value?.Length > 1_024 ||
                                                                           option.Label?.Length > 200))
                problems.Add($"參數「{parameter.Name}」的選項內容過長或格式不完整。");

            if (parameter.Type == ActionParameterType.Select && options.Count == 0)
                problems.Add($"參數「{parameter.Name}」是下拉選單，必須提供 options。");
        }

        var outputs = parsed.Outputs ?? [];
        if (outputs.Count > 32) problems.Add("動作最多宣告 32 個輸出。");
        var outputNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var output in outputs.Take(33))
        {
            if (output is null || !StepExpressions.IsValidOutputName(output.Name))
                problems.Add("輸出名稱只能使用英數字與底線，且不可超過 64 個字元。");
            else if (!outputNames.Add(output.Name)) problems.Add($"輸出「{output.Name}」重複宣告。");
            if ((output?.Description?.Length ?? 0) > 1_000) problems.Add("輸出的說明不可超過 1,000 個字元。");
        }

        foreach (var placeholder in template.Length <= 100_000 ? Placeholders(template) : [])
            if (!names.Contains(placeholder))
                problems.Add($"樣板用到未定義的參數「{placeholder}」。");

        errors = problems;

        if (problems.Count != 0) return false;

        definition = new ActionDefinition
        {
            Id = id,
            Name = name,
            Description = parsed.Description ?? string.Empty,
            Category = ActionCategory.All.Contains(category)
                ? category
                : ActionCategory.General,
            Icon = string.IsNullOrWhiteSpace(parsed.Icon) ? "cil-terminal" : parsed.Icon,
            Version = string.IsNullOrWhiteSpace(parsed.Version) ? "1.0.0" : parsed.Version,
            Author = parsed.Author ?? string.Empty,
            Parameters = parameters,
            Outputs = outputs,
            Template = template,
            IsBuiltIn = false
        };

        return true;
    }

    /// <summary>Extracts every parameter name used in the template, including conditional blocks.</summary>
    public static IReadOnlyList<string> Placeholders(string template)
    {
        var names = new List<string>();

        foreach (Match match in PlaceholderPattern().Matches(template))
        {
            var name = match.Groups["name"].Value.Trim();

            if (name.Length != 0 && !names.Contains(name)) names.Add(name);
        }

        return names;
    }

    /// <summary>Identifier rule: lowercase alphanumerics and dots, with at least one dot. Shared by plugins and actions.</summary>
    public static bool IsValidId(string? id)
    {
        return !string.IsNullOrWhiteSpace(id) && IdPattern().IsMatch(id);
    }

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex ParameterNamePattern();

    [GeneratedRegex(@"\{\{\{?(?:#(?:if|unless)\s+)?(?<name>[A-Za-z_][A-Za-z0-9_]*)\}?\}\}")]
    private static partial Regex PlaceholderPattern();
}