using System.Text.Json;
using System.Text.RegularExpressions;

namespace Walis.Core.Actions;

/// <summary>
///     Action pack: a directory (usually a clone of another git repo) containing <c>pack.json</c> plus
///     <c>actions/**/*.json</c>, each JSON file being one action definition. Others contribute actions through PRs that a maintainer reviews and merges;
///     Walis only reads the directory and never writes back, so git always stays the source of truth for the pack.
/// </summary>
public sealed record ActionPackInfo(
    string Name,
    string Version,
    string Description,
    string Directory,
    IReadOnlyList<ActionDefinition> Definitions,
    IReadOnlyList<string> Errors);

/// <summary>Provides the currently loaded pack actions; the API implements it from the configured directories, tests can inject fakes.</summary>
public interface IActionPackSource
{
    IReadOnlyList<ActionPackInfo> Packs { get; }

    IReadOnlyList<ActionDefinition> Definitions { get; }
}

public sealed partial class ActionPackLoader
{
    public const string ManifestFileName = "pack.json";
    public const string ActionsFolder = "actions";
    public const int MaximumActionsPerPack = 1_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed class PackManifest
    {
        public string? Name { get; init; }
        public string? Version { get; init; }
        public string? Description { get; init; }
    }

    /// <summary>Loads one pack directory; every problem goes into Errors, broken actions are skipped and good ones still load.</summary>
    public static ActionPackInfo Load(string directory)
    {
        var errors = new List<string>();
        var definitions = new List<ActionDefinition>();
        var root = Path.GetFullPath(directory);
        var manifestPath = Path.Combine(root, ManifestFileName);

        PackManifest manifest = new();
        if (!File.Exists(manifestPath))
        {
            errors.Add($"缺少 {ManifestFileName}。");
        }
        else
        {
            try
            {
                manifest = JsonSerializer.Deserialize<PackManifest>(File.ReadAllText(manifestPath), JsonOptions) ?? new PackManifest();
            }
            catch (JsonException exception)
            {
                errors.Add($"{ManifestFileName} 不是有效的 JSON：{exception.Message}");
            }
        }

        var name = string.IsNullOrWhiteSpace(manifest.Name) ? Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar)) : manifest.Name.Trim();
        if (!PackNamePattern().IsMatch(name))
        {
            errors.Add($"套件名稱「{name}」只能用小寫英數、連字號與底線。");
        }

        var actionsRoot = Path.Combine(root, ActionsFolder);
        if (!Directory.Exists(actionsRoot))
        {
            errors.Add($"缺少 {ActionsFolder}/ 目錄。");
            return new ActionPackInfo(name, manifest.Version ?? "0.0.0", manifest.Description ?? string.Empty, root, definitions, errors);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = Directory.EnumerateFiles(actionsRoot, "*.json", new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden
        }).OrderBy(file => file, StringComparer.Ordinal).ToList();

        if (files.Count > MaximumActionsPerPack)
        {
            errors.Add($"套件最多 {MaximumActionsPerPack} 個動作。");
            files = files.Take(MaximumActionsPerPack).ToList();
        }

        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file);
            string json;
            try
            {
                json = File.ReadAllText(file);
            }
            catch (IOException exception)
            {
                errors.Add($"{relative}：讀取失敗（{exception.Message}）。");
                continue;
            }

            if (!ActionManifest.TryParse(json, out var definition, out var problems))
            {
                errors.AddRange(problems.Select(problem => $"{relative}：{problem}"));
                continue;
            }

            // The file name must equal the id so a PR reviewer can match them at a glance and two files can never fight over one id.
            var stem = Path.GetFileNameWithoutExtension(file);
            if (!string.Equals(stem, definition!.Id, StringComparison.Ordinal))
            {
                errors.Add($"{relative}：檔名要跟 id 一樣（{definition.Id}.json）。");
                continue;
            }

            if (!seen.Add(definition.Id))
            {
                errors.Add($"{relative}：id「{definition.Id}」在套件裡重複。");
                continue;
            }

            definitions.Add(new ActionDefinition
            {
                Id = definition.Id,
                Name = definition.Name,
                Description = definition.Description,
                Category = definition.Category,
                Icon = definition.Icon,
                Version = definition.Version,
                Author = definition.Author,
                Parameters = definition.Parameters,
                Template = definition.Template,
                IsBuiltIn = false,
                Source = $"pack:{name}"
            });
        }

        return new ActionPackInfo(name, manifest.Version ?? "0.0.0", manifest.Description ?? string.Empty, root, definitions, errors);
    }

    /// <summary>
    ///     Loads from several roots: a root with its own pack.json is one pack, otherwise each subdirectory with a pack.json is one.
    ///     Missing roots are skipped (configured but not cloned yet is a normal state).
    /// </summary>
    public static IReadOnlyList<ActionPackInfo> LoadAll(IEnumerable<string> roots)
    {
        var packs = new List<ActionPackInfo>();

        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Distinct(StringComparer.Ordinal))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            if (File.Exists(Path.Combine(root, ManifestFileName)))
            {
                packs.Add(Load(root));
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(root).OrderBy(child => child, StringComparer.Ordinal))
            {
                if (File.Exists(Path.Combine(child, ManifestFileName)))
                {
                    packs.Add(Load(child));
                }
            }
        }

        return packs;
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex PackNamePattern();
}
