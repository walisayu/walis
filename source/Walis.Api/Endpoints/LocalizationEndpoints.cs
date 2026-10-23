using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class LocalizationEndpoints
{
    public static IEndpointRouteBuilder MapLocalizationEndpoints(this IEndpointRouteBuilder app)
    {
        var localization = app.MapGroup("/api/i18n").WithTags("Localization");

        // The frontend ships the default dictionary; this only provides the locale list and the strings that need overriding.
        localization.MapGet("/", async (ILocalizationRepository repository, CancellationToken cancellationToken) =>
        {
            var locales = await repository.ListLocalesAsync(cancellationToken);
            var entries = await repository.ListTranslationsAsync(null, cancellationToken);

            return Results.Ok(new
            {
                locales,
                overrides = entries
                    .GroupBy(entry => entry.Locale)
                    .ToDictionary(
                        group => group.Key,
                        group => group.ToDictionary(entry => entry.Source, entry => entry.Value))
            });
        });

        localization.MapPut("/locales/{code}", async (
            string code,
            LocaleRequest request,
            ILocalizationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (InvalidLocale(code) is { } localeError) return localeError;

            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.NativeName) ||
                request.Name.Length > 200 || request.NativeName.Length > 200 ||
                AuditText.HasControlCharacters(request.Name) || AuditText.HasControlCharacters(request.NativeName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["name"] = ["請輸入語系名稱與原生名稱（200 字元以內，不可含控制字元）。"]
                });

            var locale = await repository.UpsertLocaleAsync(
                new LocaleDraft(code, request.Name, request.NativeName, request.IsEnabled, request.SortOrder),
                cancellationToken);

            await audit.RecordAsync(AuditAction.Update, "locale", code,
                $"新增或更新語系 {code}（{request.Name}{(request.IsEnabled ? string.Empty : "、已停用")}）", cancellationToken);

            return Results.Ok(locale);
        });

        localization.MapDelete("/locales/{code}", async (
            string code,
            ILocalizationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (InvalidLocale(code) is { } localeError) return localeError;

            if (code == Locale.Source) return Results.Conflict(new { message = "來源語系不可刪除。" });

            if (!await repository.DeleteLocaleAsync(code, cancellationToken)) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "locale", code, $"刪除語系 {code}", cancellationToken);

            return Results.NoContent();
        });

        localization.MapPut("/translations", async (
            TranslationRequest request,
            ILocalizationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Locale) || string.IsNullOrWhiteSpace(request.Source))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["source"] = ["請提供語系與原文。"]
                });

            if (InvalidLocale(request.Locale) is { } localeError) return localeError;

            if (request.Locale == Locale.Source) return Results.Conflict(new { message = "來源語系不需要翻譯。" });

            var entry = await repository.SaveTranslationAsync(
                new TranslationDraft(request.Locale, request.Source, request.Value),
                cancellationToken);

            await audit.RecordAsync(
                entry is null ? AuditAction.Delete : AuditAction.Update,
                "translation",
                request.Locale,
                $"{(entry is null ? "清除" : "更新")} {request.Locale} 的翻譯：{request.Source}",
                cancellationToken);

            return Results.Ok(new { cleared = entry is null, entry });
        });

        localization.MapPut("/translations/{locale}", async (
            string locale,
            Dictionary<string, string> entries,
            ILocalizationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (InvalidLocale(locale) is { } localeError) return localeError;

            if (locale == Locale.Source) return Results.Conflict(new { message = "來源語系不需要翻譯。" });

            var saved = await repository.SaveTranslationsAsync(locale, entries, cancellationToken);

            await audit.RecordAsync(AuditAction.Update, "translation", locale, $"整批寫入 {locale} 的翻譯（{saved} 筆）",
                cancellationToken);

            return Results.Ok(new { saved });
        });

        localization.MapDelete("/translations/{locale}", async (
            string locale,
            ILocalizationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (InvalidLocale(locale) is { } localeError) return localeError;

            var cleared = await repository.ClearTranslationsAsync(locale, cancellationToken);

            await audit.RecordAsync(AuditAction.Delete, "translation", locale, $"清除 {locale} 的全部翻譯（{cleared} 筆）",
                cancellationToken);

            return Results.Ok(new { cleared });
        });

        return app;
    }

    /// <summary>A locale code is a BCP 47 tag; the stored columns and the audit EntityId are short, so the limit is enforced up front.</summary>
    public const int MaximumLocaleLength = 32;

    private static IResult? InvalidLocale(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > MaximumLocaleLength ||
            code.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["locale"] = [$"語系代碼需為 1 到 {MaximumLocaleLength} 個字元，且不可含空白或控制字元。"]
            });

        return null;
    }
}

public sealed record LocaleRequest(string? Name, string? NativeName, bool IsEnabled, int SortOrder);

public sealed record TranslationRequest(string? Locale, string? Source, string? Value);