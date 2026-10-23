using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Walis.Core.Models;

/// <summary>Build parameter types, matching Jenkins's string/choice/booleanParam/text/password.</summary>
public static class BuildParameterTypes
{
    public const string String = "string";
    public const string Choice = "choice";
    public const string Boolean = "boolean";
    public const string Text = "text";

    /// <summary>The value is treated as a secret: masked in logs and never returned in API responses.</summary>
    public const string Password = "password";

    public static bool IsValid(string? value)
    {
        return value is String or Choice or Boolean or Text or Password;
    }
}

/// <summary>One build parameter declared by a job; the value is decided at trigger time and injected into the script as an environment variable.</summary>
public sealed class BuildParameterDefinition
{
    public string Name { get; init; } = string.Empty;
    public string? Label { get; init; }
    public string Type { get; init; } = BuildParameterTypes.String;
    public string? Default { get; init; }
    public string? Description { get; init; }

    /// <summary>The allowed values of a choice parameter; the first is the default.</summary>
    public List<string> Options { get; init; } = [];

    [JsonIgnore] public bool IsSecret => Type == BuildParameterTypes.Password;
}

/// <summary>
///     Build parameter declarations (stored in <c>Jobs.ParametersJson</c>) and trigger-time values (stored in <c>Builds.ParametersJson</c>).
///     Parameters not given at trigger time get their defaults, and unknown names are always rejected so parameters cannot be used to inject arbitrary environment variables.
/// </summary>
public static partial class BuildParameters
{
    public const int MaximumCount = 50;
    public const int MaximumJsonLength = 32_768;
    public const int MaximumValueLength = 4_096;
    public const int MaximumOptions = 100;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { MaxDepth = 8 };

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex NamePattern();

    /// <summary>The same floor as secret environment variables: shorter values are not masked reliably.</summary>
    public const int MinimumSecretLength = 4;

    public static IReadOnlyList<BuildParameterDefinition> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<BuildParameterDefinition>>(json, SerializerOptions)?
                       .Where(definition => definition is not null)
                       .ToList()
                   ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string? Serialize(IReadOnlyList<BuildParameterDefinition> definitions)
    {
        return definitions.Count == 0 ? null : JsonSerializer.Serialize(definitions, SerializerOptions);
    }

    public static bool IsValid(string? json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json)) return true;

        if (json.Length > MaximumJsonLength)
        {
            error = $"參數設定不可超過 {MaximumJsonLength:N0} 個字元。";
            return false;
        }

        List<BuildParameterDefinition>? definitions;
        try
        {
            definitions = JsonSerializer.Deserialize<List<BuildParameterDefinition>>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            error = "參數設定不是有效的 JSON 陣列。";
            return false;
        }

        if (definitions is null) return true;

        if (definitions.Count > MaximumCount)
        {
            error = $"參數最多 {MaximumCount} 個。";
            return false;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in definitions)
        {
            if (definition is null || !NamePattern().IsMatch(definition.Name))
            {
                error = "參數名稱只能用英數字與底線、以字母或底線開頭，最多 64 個字元。";
                return false;
            }

            if (definition.Name.StartsWith("WALIS_", StringComparison.OrdinalIgnoreCase))
            {
                error = $"參數名稱 {definition.Name} 不可以 WALIS_ 開頭，那是保留給系統的。";
                return false;
            }

            if (!names.Add(definition.Name))
            {
                error = $"參數 {definition.Name} 重複。";
                return false;
            }

            if (!BuildParameterTypes.IsValid(definition.Type))
            {
                error = $"參數 {definition.Name} 的型別只能是 string、choice、boolean、text 或 password。";
                return false;
            }

            if ((definition.Label?.Length ?? 0) > 200 || (definition.Description?.Length ?? 0) > 2_000 ||
                (definition.Default?.Length ?? 0) > MaximumValueLength)
            {
                error = $"參數 {definition.Name} 的標籤、說明或預設值過長。";
                return false;
            }

            if (definition.Type == BuildParameterTypes.Choice)
            {
                if (definition.Options.Count is 0 or > MaximumOptions ||
                    definition.Options.Any(option => option is null || option.Length > MaximumValueLength))
                {
                    error = $"參數 {definition.Name} 是選項型別，需要 1 到 {MaximumOptions} 個選項。";
                    return false;
                }

                if (definition.Default is not null &&
                    !definition.Options.Contains(definition.Default, StringComparer.Ordinal))
                {
                    error = $"參數 {definition.Name} 的預設值不在選項裡。";
                    return false;
                }
            }
            else if (definition.Options.Count != 0)
            {
                error = $"只有選項型別的參數可以設定選項（{definition.Name}）。";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    ///     Matches the values given at trigger time against the declarations: fills defaults for missing ones, rejects unknown names and values outside the choices.
    ///     The returned dictionary can be injected directly as environment variables.
    /// </summary>
    public static bool TryResolve(
        IReadOnlyList<BuildParameterDefinition> definitions,
        IReadOnlyDictionary<string, string>? supplied,
        out Dictionary<string, string> values,
        out string? error)
    {
        values = new Dictionary<string, string>(StringComparer.Ordinal);
        error = null;
        var known = definitions.ToDictionary(definition => definition.Name, StringComparer.Ordinal);

        foreach (var (name, value) in supplied ?? new Dictionary<string, string>())
        {
            if (!known.ContainsKey(name))
            {
                error = $"任務沒有宣告參數 {name}。";
                return false;
            }

            if (value is null || value.Length > MaximumValueLength)
            {
                error = $"參數 {name} 的值不可為空或超過 {MaximumValueLength:N0} 個字元。";
                return false;
            }
        }

        foreach (var definition in definitions)
        {
            var given = supplied is not null && supplied.TryGetValue(definition.Name, out var raw) ? raw : null;

            switch (definition.Type)
            {
                case BuildParameterTypes.Boolean:
                {
                    var text = given ?? definition.Default ?? "false";
                    if (!bool.TryParse(text, out var flag))
                    {
                        error = $"參數 {definition.Name} 只能是 true 或 false。";
                        return false;
                    }

                    values[definition.Name] = flag ? "true" : "false";
                    break;
                }

                case BuildParameterTypes.Choice:
                {
                    var choice = given ?? definition.Default ?? definition.Options[0];
                    if (!definition.Options.Contains(choice, StringComparer.Ordinal))
                    {
                        error = $"參數 {definition.Name} 的值不在選項裡。";
                        return false;
                    }

                    values[definition.Name] = choice;
                    break;
                }

                default:
                {
                    var text = given ?? definition.Default ?? string.Empty;

                    // A password value becomes a masking pattern for the whole log; one or two characters would turn every
                    // occurrence of that character into ***, so short values are refused like secret variables are.
                    if (definition.Type == BuildParameterTypes.Password && text.Length is > 0 and < MinimumSecretLength)
                    {
                        error = $"參數 {definition.Name} 是密碼型參數，值至少要 {MinimumSecretLength} 個字元。";
                        return false;
                    }

                    values[definition.Name] = text;
                    break;
                }
            }
        }

        return true;
    }

    public static IReadOnlyDictionary<string, string> ParseValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, string>();

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json, SerializerOptions)
                   ?? new Dictionary<string, string>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, string>();
        }
    }

    public static string? SerializeValues(IReadOnlyDictionary<string, string> values)
    {
        return values.Count == 0 ? null : JsonSerializer.Serialize(values, SerializerOptions);
    }

    /// <summary>Password-type values are secrets: masked in logs and never returned to the UI.</summary>
    public static IReadOnlyList<string> SecretValues(
        IReadOnlyList<BuildParameterDefinition> definitions,
        IReadOnlyDictionary<string, string> values)
    {
        return definitions
            .Where(definition => definition.IsSecret)
            .Select(definition => values.GetValueOrDefault(definition.Name))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!)
            .ToList();
    }

    /// <summary>Values for the UI or logs: secrets replaced by the mask string.</summary>
    public static Dictionary<string, string> Mask(
        IReadOnlyList<BuildParameterDefinition> definitions,
        IReadOnlyDictionary<string, string> values)
    {
        var secrets = definitions.Where(definition => definition.IsSecret).Select(definition => definition.Name)
            .ToHashSet(StringComparer.Ordinal);
        return values.ToDictionary(
            pair => pair.Key,
            pair => secrets.Contains(pair.Key) && pair.Value.Length != 0 ? "********" : pair.Value,
            StringComparer.Ordinal);
    }
}