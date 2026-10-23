using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Walis.Core.Actions;

/// <summary>
///     Both ends of passing structured data between steps:
///     <list type="bullet">
///         <item>Producer: the step writes <c>name=value</c> (one per line) or a JSON object to <c>$WALIS_OUTPUT_FILE</c>.</item>
///         <item>Consumer: a later step writes <c>${{ steps.&lt;key&gt;.&lt;name&gt; }}</c> in a parameter value.</item>
///     </list>
///     References are compiled into the shell variable <c>${WALIS_STEP_&lt;key&gt;_&lt;name&gt;}</c> when the job is saved,
///     and at run time <c>BuildStepSequence</c> injects earlier steps' outputs as environment variables; the value is therefore never spliced into script text,
///     there is no injection problem, and the remote runner does not need to understand expressions.
/// </summary>
public static partial class StepExpressions
{
    public const string VariablePrefix = "WALIS_STEP_";
    public const string OutputFileVariable = "WALIS_OUTPUT_FILE";
    public const int MaximumOutputs = 100;
    public const int MaximumValueLength = 8_192;
    public const int MaximumKeyLength = 40;

    public static bool IsValidKey(string? key)
    {
        return !string.IsNullOrEmpty(key) && key.Length <= MaximumKeyLength && KeyPattern().IsMatch(key);
    }

    public static bool IsValidOutputName(string? name)
    {
        return !string.IsNullOrEmpty(name) && OutputNamePattern().IsMatch(name);
    }

    public static string VariableName(string key, string name)
    {
        return $"{VariablePrefix}{key}_{name}";
    }

    public static bool Contains(string? value)
    {
        return !string.IsNullOrEmpty(value) && value.Contains("${{", StringComparison.Ordinal) &&
               ExpressionPattern().IsMatch(value);
    }

    /// <summary>The (key, name) pairs referenced in the value, in order of appearance, not deduplicated.</summary>
    public static IEnumerable<(string Key, string Name)> References(string? value)
    {
        if (string.IsNullOrEmpty(value)) yield break;

        foreach (Match match in ExpressionPattern().Matches(value))
            yield return (match.Groups["key"].Value, match.Groups["name"].Value);
    }

    /// <summary>
    ///     Wraps a value with references as a string the shell can expand safely: literal parts single-quoted, references as <c>"${VAR}"</c>,
    ///     with both kinds of fragment simply adjacent (the shell joins adjacent strings into one word).
    /// </summary>
    public static string Quote(string value)
    {
        var builder = new StringBuilder();
        var index = 0;

        foreach (Match match in ExpressionPattern().Matches(value))
        {
            if (match.Index > index) builder.Append(ActionTemplate.QuoteLiteral(value[index..match.Index]));

            builder.Append("\"${").Append(VariableName(match.Groups["key"].Value, match.Groups["name"].Value)).Append("}\"");
            index = match.Index + match.Length;
        }

        if (index < value.Length) builder.Append(ActionTemplate.QuoteLiteral(value[index..]));

        return builder.Length == 0 ? "''" : builder.ToString();
    }

    /// <summary>For verbatim insertion (<c>{{{x}}}</c>) replaces references with <c>"${VAR}"</c> and leaves the other text untouched.</summary>
    public static string ExpandRaw(string value)
    {
        return ExpressionPattern().Replace(value,
            match => $"\"${{{VariableName(match.Groups["key"].Value, match.Groups["name"].Value)}}}\"");
    }

    /// <summary>
    ///     Parses what a step wrote to its output file. <c>name=value</c> one per line; when the whole content starts with <c>{</c> it is a JSON object,
    ///     scalars are taken literally and nested values are kept as JSON text. Broken entries are recorded in problems and skipped so the step does not fail.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ParseOutputs(string content, List<string> problems)
    {
        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        var trimmed = content.TrimStart();

        if (trimmed.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(trimmed, new JsonDocumentOptions { MaxDepth = 8 });
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    problems.Add("輸出檔的 JSON 必須是物件。");
                    return outputs;
                }

                foreach (var property in document.RootElement.EnumerateObject())
                {
                    var value = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        JsonValueKind.Null => string.Empty,
                        _ => property.Value.GetRawText()
                    };
                    Add(property.Name, value);
                }
            }
            catch (JsonException exception)
            {
                problems.Add($"輸出檔不是有效的 JSON：{exception.Message}");
            }

            return outputs;
        }

        foreach (var line in content.Split('\r', '\n'))
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                problems.Add($"輸出檔有一行不是 name=value：{Truncate(line)}");
                continue;
            }

            Add(line[..separator].Trim(), line[(separator + 1)..]);
        }

        return outputs;

        void Add(string name, string value)
        {
            if (!IsValidOutputName(name))
            {
                problems.Add($"輸出名稱「{Truncate(name)}」不合法，只能用英數字與底線。");
                return;
            }

            if (value.Length > MaximumValueLength)
            {
                problems.Add($"輸出「{name}」超過 {MaximumValueLength:N0} 個字元，已略過。");
                return;
            }

            if (!outputs.ContainsKey(name) && outputs.Count >= MaximumOutputs)
            {
                problems.Add($"輸出最多 {MaximumOutputs} 個，「{name}」已略過。");
                return;
            }

            outputs[name] = value;
        }
    }

    /// <summary>Outputs reported by a remote runner are filtered with the same rules.</summary>
    public static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string?>? outputs)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (outputs is null) return result;

        foreach (var (name, value) in outputs)
        {
            if (result.Count >= MaximumOutputs) break;
            if (IsValidOutputName(name) && (value?.Length ?? 0) <= MaximumValueLength) result[name] = value ?? string.Empty;
        }

        return result;
    }

    private static string Truncate(string value)
    {
        return value.Length <= 60 ? value : value[..60] + "…";
    }

    [GeneratedRegex(@"^[a-z][a-z0-9_]*$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$")]
    private static partial Regex OutputNamePattern();

    [GeneratedRegex(@"\$\{\{\s*steps\.(?<key>[a-z][a-z0-9_]*)\.(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex ExpressionPattern();
}
