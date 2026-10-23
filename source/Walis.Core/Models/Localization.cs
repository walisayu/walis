namespace Walis.Core.Models;

public sealed class Locale
{
    public const string Source = "zh-Hant";

    public string Code { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string NativeName { get; init; } = string.Empty;
    public bool IsEnabled { get; init; } = true;
    public int SortOrder { get; init; }
    public DateTime UpdatedAt { get; init; }

    public bool IsSource => Code == Source;
}

public sealed record LocaleDraft(string Code, string Name, string NativeName, bool IsEnabled, int SortOrder);

/// <summary>
///     A translation override entry. The frontend ships a default dictionary; this stores only the strings adjusted in the admin UI,
///     layered over the default dictionary at load time.
/// </summary>
public sealed class TranslationEntry
{
    public string Locale { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public DateTime UpdatedAt { get; init; }
}

public sealed record TranslationDraft(string Locale, string Source, string? Value);