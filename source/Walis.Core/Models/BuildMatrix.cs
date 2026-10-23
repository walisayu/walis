using System.Text.Json;

namespace Walis.Core.Models;

/// <summary>
///     A job's matrix configuration: run the same script over several variable combinations, e.g. two Node versions times two platforms.
///     Each combination becomes an independent build with the variables injected as environment variables.
///     The format is <c>{"NODE":["20","22"],"OS":["mac","linux"]}</c>.
/// </summary>
public static class BuildMatrix
{
    public const int MaximumJsonLength = 32_768;
    public const int MaximumAxes = 8;
    public const int MaximumValuesPerAxis = 20;
    public const int MaximumAxisNameLength = 64;
    public const int MaximumValueLength = 128;
    public const int MaximumCombinations = 50;
    public const int MaximumLabelLength = 200;

    /// <summary>Expands into every combination; returns an empty list when unset or malformed, and the caller treats it as an ordinary build.</summary>
    public static IReadOnlyList<Combination> Expand(string? json, int limit = MaximumCombinations)
    {
        var axes = Parse(json);

        if (axes.Count == 0) return [];

        IEnumerable<Dictionary<string, string>> combinations = [new()];

        foreach (var (name, values) in axes)
            combinations = combinations.SelectMany(current => values.Select(value =>
                new Dictionary<string, string>(current, StringComparer.Ordinal) { [name] = value }));

        // The cap is a safeguard: four axes with five values each is already 625 builds, which is rarely intended.
        return
        [
            .. combinations
                .Take(Math.Clamp(limit, 1, MaximumCombinations))
                .Select(values => new Combination(values))
        ];
    }

    public static IReadOnlyList<(string Name, IReadOnlyList<string> Values)> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];

        return TryParseAxes(json, out var axes, out _) ? axes : [];
    }

    /// <summary>Whether the configuration looks valid; used when validating a job on save.</summary>
    public static bool IsValid(string? json, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json)) return true;

        if (!TryParseAxes(json, out var axes, out error)) return false;

        long combinations = 1;
        foreach (var (_, values) in axes)
        {
            combinations *= values.Count;
            if (combinations > MaximumCombinations)
            {
                error = $"矩陣展開後最多 {MaximumCombinations} 組，請減少軸或選項。";
                return false;
            }
        }

        return true;
    }

    private static bool TryParseAxes(
        string json,
        out IReadOnlyList<(string Name, IReadOnlyList<string> Values)> axes,
        out string? error)
    {
        axes = [];
        error = null;

        if (json.Length > MaximumJsonLength)
        {
            error = $"矩陣設定不可超過 {MaximumJsonLength:N0} 個字元。";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions { MaxDepth = 8 });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "矩陣要是一個物件，例如 {\"NODE\":[\"20\",\"22\"]}。";
                return false;
            }

            var parsedAxes = new List<(string, IReadOnlyList<string>)>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (parsedAxes.Count >= MaximumAxes)
                {
                    error = $"矩陣最多 {MaximumAxes} 個軸。";
                    return false;
                }

                if (property.Name.Length is 0 or > MaximumAxisNameLength ||
                    !property.Name.All(character => char.IsLetterOrDigit(character) || character == '_'))
                {
                    error = $"矩陣軸名稱只能使用英數字與底線，且不可超過 {MaximumAxisNameLength} 個字元。";
                    return false;
                }

                if (!names.Add(property.Name))
                {
                    error = $"矩陣軸名稱不可重複：{property.Name}。";
                    return false;
                }

                if (property.Value.ValueKind != JsonValueKind.Array)
                {
                    error = $"矩陣軸 {property.Name} 必須是字串陣列。";
                    return false;
                }

                var values = new List<string>();
                foreach (var value in property.Value.EnumerateArray())
                {
                    if (values.Count >= MaximumValuesPerAxis)
                    {
                        error = $"每個矩陣軸最多 {MaximumValuesPerAxis} 個值。";
                        return false;
                    }

                    if (value.ValueKind != JsonValueKind.String ||
                        value.GetString() is not { } text ||
                        string.IsNullOrWhiteSpace(text) ||
                        text.Length > MaximumValueLength ||
                        text.Any(char.IsControl))
                    {
                        error = $"矩陣軸 {property.Name} 的值必須是非空白字串、不可含控制字元，且不可超過 {MaximumValueLength} 個字元。";
                        return false;
                    }

                    values.Add(text);
                }

                if (values.Count == 0)
                {
                    error = $"矩陣軸 {property.Name} 至少要有一個值。";
                    return false;
                }

                parsedAxes.Add((property.Name, values));
            }

            if (parsedAxes.Count == 0)
            {
                error = "矩陣至少要有一個軸，而且每個軸至少一個值。";
                return false;
            }

            // MatrixLabel is stored in a 200-character column shared across databases. Computing from the longest value per axis
            // guarantees every combination fits instead of failing at dispatch time through database truncation or an exception.
            var longestLabel = parsedAxes.Sum(axis =>
                                   axis.Item1.Length + 1 + axis.Item2.Max(value => value.Length)) +
                               Math.Max(0, parsedAxes.Count - 1) * 2;
            if (longestLabel > MaximumLabelLength)
            {
                error = $"矩陣組合標籤不可超過 {MaximumLabelLength} 個字元。";
                return false;
            }

            axes = parsedAxes;
            return true;
        }
        catch (JsonException)
        {
            error = "矩陣不是有效的 JSON。";
            return false;
        }
    }

    /// <summary>The variable combination for one build.</summary>
    public sealed record Combination(IReadOnlyDictionary<string, string> Values)
    {
        /// <summary>The display label, e.g. NODE=20, OS=mac.</summary>
        public string Label => string.Join(", ", Values.Select(pair => $"{pair.Key}={pair.Value}"));
    }
}