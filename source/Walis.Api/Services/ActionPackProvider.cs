using Walis.Core.Actions;

namespace Walis.Api.Services;

/// <summary>
///     Loads action packs from the configured directories and caches them; <c>Walis:ActionPacks</c> may list several directories,
///     and <c>action-packs/</c> under the data directory is always checked too (cloning a reviewed pack repo there is the easiest setup).
///     Call <see cref="Reload"/> (or the API's reload) to re-read after the directory changes.
/// </summary>
public sealed class ActionPackProvider : IActionPackSource
{
    private readonly object _gate = new();
    private readonly IReadOnlyList<string> _roots;
    private readonly ILogger<ActionPackProvider> _logger;
    private IReadOnlyList<ActionPackInfo> _packs = [];
    private IReadOnlyList<ActionDefinition> _definitions = [];
    private string _fingerprint = string.Empty;

    public ActionPackProvider(IConfiguration configuration, DataDirectory data, ILogger<ActionPackProvider> logger)
    {
        _logger = logger;
        var configured = configuration.GetSection("Walis:ActionPacks").Get<string[]>() ?? [];
        _roots = [.. configured.Where(root => !string.IsNullOrWhiteSpace(root)), data.Combine("action-packs")];
        Reload();
    }

    public IReadOnlyList<string> Roots => _roots;

    public IReadOnlyList<ActionPackInfo> Packs
    {
        get
        {
            lock (_gate)
            {
                return _packs;
            }
        }
    }

    public IReadOnlyList<ActionDefinition> Definitions
    {
        get
        {
            lock (_gate)
            {
                return _definitions;
            }
        }
    }

    /// <summary>
    ///     Reloads only when the directory content changed (git pull, manual edits); the scheduler calls this every minute, so a pack update needs no API call.
    ///     The fingerprint looks only at file paths, sizes and modification times; the cost of a few hundred small JSON files is negligible.
    /// </summary>
    public bool ReloadIfChanged()
    {
        var fingerprint = Fingerprint(_roots);
        lock (_gate)
        {
            if (fingerprint == _fingerprint)
            {
                return false;
            }
        }

        Reload();
        return true;
    }

    private static string Fingerprint(IEnumerable<string> roots)
    {
        var builder = new System.Text.StringBuilder();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden
        };

        foreach (var root in roots.Where(Directory.Exists).OrderBy(root => root, StringComparer.Ordinal))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.json", options).OrderBy(file => file, StringComparer.Ordinal))
            {
                var info = new FileInfo(file);
                builder.Append(file).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            }
        }

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(builder.ToString())));
    }

    public IReadOnlyList<ActionPackInfo> Reload()
    {
        var fingerprint = Fingerprint(_roots);
        var packs = ActionPackLoader.LoadAll(_roots);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var definitions = new List<ActionDefinition>();

        // When two packs collide on an id the first loaded wins and the later one is recorded as an error, visible during review.
        var merged = new List<ActionPackInfo>();
        foreach (var pack in packs)
        {
            var errors = pack.Errors.ToList();
            var kept = new List<ActionDefinition>();
            foreach (var definition in pack.Definitions)
            {
                if (!seen.Add(definition.Id))
                {
                    errors.Add($"id「{definition.Id}」已被另一個套件使用，這一份略過。");
                    continue;
                }

                kept.Add(definition);
                definitions.Add(definition);
            }

            merged.Add(pack with { Definitions = kept, Errors = errors });
            foreach (var error in errors)
            {
                _logger.LogWarning("動作套件 {Pack}：{Error}", pack.Name, error);
            }
        }

        lock (_gate)
        {
            _packs = merged;
            _definitions = definitions;
            _fingerprint = fingerprint;
        }

        return merged;
    }
}
