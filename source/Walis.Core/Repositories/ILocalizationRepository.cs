using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface ILocalizationRepository
{
    Task<IReadOnlyList<Locale>> ListLocalesAsync(CancellationToken cancellationToken = default);

    Task<Locale?> GetLocaleAsync(string code, CancellationToken cancellationToken = default);

    Task<Locale> UpsertLocaleAsync(LocaleDraft draft, CancellationToken cancellationToken = default);

    Task<bool> DeleteLocaleAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Gets translation overrides; returns all of them when no locale is given.</summary>
    Task<IReadOnlyList<TranslationEntry>> ListTranslationsAsync(string? locale,
        CancellationToken cancellationToken = default);

    /// <summary>Writes or clears a single translation override; an empty value restores the default dictionary's content.</summary>
    Task<TranslationEntry?> SaveTranslationAsync(TranslationDraft draft, CancellationToken cancellationToken = default);

    Task<int> SaveTranslationsAsync(string locale, IReadOnlyDictionary<string, string> entries,
        CancellationToken cancellationToken = default);

    Task<int> ClearTranslationsAsync(string locale, CancellationToken cancellationToken = default);
}