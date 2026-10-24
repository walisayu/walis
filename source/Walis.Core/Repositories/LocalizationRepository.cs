using Dapper;
using Walis.Core.Database;
using Walis.Core.Models;

namespace Walis.Core.Repositories;

public sealed class LocalizationRepository(IDbConnectionFactory connectionFactory) : ILocalizationRepository
{
    private readonly ISqlDialect _dialect = connectionFactory.Dialect;

    public async Task<IReadOnlyList<Locale>> ListLocalesAsync(CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        var locales = await connection.QueryAsync<Locale>(new CommandDefinition(
            "SELECT Code, Name, NativeName, IsEnabled, SortOrder, UpdatedAt FROM Locales ORDER BY SortOrder, Code;",
            cancellationToken: cancellationToken));
        return locales.AsList();
    }

    public async Task<Locale?> GetLocaleAsync(string code, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<Locale>(new CommandDefinition(
            "SELECT Code, Name, NativeName, IsEnabled, SortOrder, UpdatedAt FROM Locales WHERE Code = @Code;",
            new { Code = code },
            cancellationToken: cancellationToken));
    }

    public async Task<Locale> UpsertLocaleAsync(LocaleDraft draft, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(new CommandDefinition(
            _dialect.Upsert(
                "Locales",
                [
                    UpsertColumn.Parameter("Code"),
                    UpsertColumn.Parameter("Name"),
                    UpsertColumn.Parameter("NativeName"),
                    UpsertColumn.Parameter("IsEnabled"),
                    UpsertColumn.Parameter("SortOrder"),
                    UpsertColumn.Parameter("UpdatedAt")
                ],
                ["Code"],
                ["Name", "NativeName", "IsEnabled", "SortOrder", "UpdatedAt"]),
            new
            {
                Code = draft.Code.Trim(),
                Name = draft.Name.Trim(),
                NativeName = draft.NativeName.Trim(),
                draft.IsEnabled,
                draft.SortOrder,
                UpdatedAt = DateTime.UtcNow
            },
            cancellationToken: cancellationToken));

        return (await GetLocaleAsync(draft.Code.Trim(), cancellationToken))!;
    }

    public async Task<bool> DeleteLocaleAsync(string code, CancellationToken cancellationToken = default)
    {
        if (code == Locale.Source) return false;

        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Locales WHERE Code = @Code;",
            new { Code = code },
            cancellationToken: cancellationToken));
        return affected != 0;
    }

    public async Task<IReadOnlyList<TranslationEntry>> ListTranslationsAsync(string? locale,
        CancellationToken cancellationToken = default)
    {
        var sql = string.IsNullOrWhiteSpace(locale)
            ? "SELECT Locale, Source, Value, UpdatedAt FROM Translations ORDER BY Locale, Source;"
            : "SELECT Locale, Source, Value, UpdatedAt FROM Translations WHERE Locale = @Locale ORDER BY Source;";

        using var connection = connectionFactory.Create();
        var entries = await connection.QueryAsync<TranslationEntry>(new CommandDefinition(
            sql, new { Locale = locale }, cancellationToken: cancellationToken));
        return entries.AsList();
    }

    public async Task<TranslationEntry?> SaveTranslationAsync(TranslationDraft draft,
        CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();

        if (string.IsNullOrWhiteSpace(draft.Value))
        {
            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM Translations WHERE Locale = @Locale AND Source = @Source;",
                new { draft.Locale, draft.Source },
                cancellationToken: cancellationToken));
            return null;
        }

        var entry = new TranslationEntry
        {
            Locale = draft.Locale,
            Source = draft.Source,
            Value = draft.Value.Trim(),
            UpdatedAt = DateTime.UtcNow
        };

        await connection.ExecuteAsync(new CommandDefinition(
            _dialect.Upsert(
                "Translations",
                [
                    UpsertColumn.Parameter("Locale"),
                    UpsertColumn.Parameter("Source"),
                    UpsertColumn.Parameter("Value"),
                    UpsertColumn.Parameter("UpdatedAt")
                ],
                ["Locale", "Source"],
                ["Value", "UpdatedAt"]),
            entry,
            cancellationToken: cancellationToken));

        return entry;
    }

    public async Task<int> SaveTranslationsAsync(
        string locale,
        IReadOnlyDictionary<string, string> entries,
        CancellationToken cancellationToken = default)
    {
        if (entries.Count == 0) return 0;

        var now = DateTime.UtcNow;
        var rows = entries
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => new { Locale = locale, Source = pair.Key, Value = pair.Value.Trim(), UpdatedAt = now })
            .ToList();

        if (rows.Count == 0) return 0;

        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            _dialect.Upsert(
                "Translations",
                [
                    UpsertColumn.Parameter("Locale"),
                    UpsertColumn.Parameter("Source"),
                    UpsertColumn.Parameter("Value"),
                    UpsertColumn.Parameter("UpdatedAt")
                ],
                ["Locale", "Source"],
                ["Value", "UpdatedAt"]),
            rows,
            cancellationToken: cancellationToken));
    }

    public async Task<int> ClearTranslationsAsync(string locale, CancellationToken cancellationToken = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM Translations WHERE Locale = @Locale;",
            new { Locale = locale },
            cancellationToken: cancellationToken));
    }
}