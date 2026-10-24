using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class BackupEndpoints
{
    public static IEndpointRouteBuilder MapBackupEndpoints(this IEndpointRouteBuilder app)
    {
        var backup = app.MapGroup("/api/backup").WithTags("Backup");

        backup.MapGet("/", async (
            IBackupRepository repository,
            BackupService service,
            int? limit,
            CancellationToken cancellationToken) => Results.Ok(new
        {
            isRunning = service.IsRunning,
            defaultDirectory = service.DefaultDirectory,
            runs = await repository.ListRunsAsync(limit ?? 20, cancellationToken)
        }));

        backup.MapGet("/runs/{id:guid}", async (
            Guid id,
            IBackupRepository repository,
            CancellationToken cancellationToken) =>
        {
            var run = await repository.GetRunAsync(id, cancellationToken);
            return run is null ? Results.NotFound() : Results.Ok(run);
        });

        backup.MapPost("/run", async (
            BackupService service,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (service.IsRunning) return Results.Conflict(new { message = "已經有一個備份在執行中。" });

            // A backup may run for minutes, so the request does not wait; the UI polls for the result.
            // The record is written inside the request: once the response has gone out, the HttpContext (and with it the actor) may be gone.
            await audit.RecordAsync(AuditAction.Run, "backup", null, "手動執行備份", cancellationToken);

            _ = Task.Run(() => service.RunAsync("manual", CancellationToken.None), CancellationToken.None);

            return Results.Accepted("/api/backup");
        });

        backup.MapPost("/test", async (
            BackupTarget request,
            ISettingsRepository settings,
            BackupTargetClientFactory clients,
            CancellationToken cancellationToken) =>
        {
            if (!BackupProviders.IsValid(request.Provider))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["provider"] = ["不支援的目的地類型。"]
                });

            // Secret fields sent back from the form may be the mask string; swap in the real values before testing.
            var stored = await settings.GetAsync(cancellationToken);
            var merged = BackupSettingsProtector.Merge(
                new WalisSettings { BackupTargets = [request] },
                stored);

            try
            {
                await clients.Resolve(merged.BackupTargets[0]).TestAsync(merged.BackupTargets[0], cancellationToken);
                return Results.Ok(new { ok = true, message = "連線成功。" });
            }
            catch (BackupTargetException exception)
            {
                return Results.Ok(new { ok = false, message = exception.Message });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Results.Ok(new { ok = false, message = "連線測試失敗，請檢查目的地設定與服務紀錄。" });
            }
        }).WithMetadata(new RequestSizeLimitAttribute(64 * 1024));

        // Setup wizard: the authorization URL for the user to open themselves. Only builds a string, never connects out.
        backup.MapPost("/oauth/authorize-url", (OAuthAuthorizeRequest request) =>
        {
            if (!OAuthAuthorization.Supports(request.Provider))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["provider"] = ["這種目的地不需要 OAuth 授權。"] });
            }

            if ((request.ClientId?.Length ?? 0) > 1024 || (request.RedirectUri?.Length ?? 0) > 1024)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["clientId"] = ["Client ID 或 redirect URI 過長。"] });
            }

            var url = OAuthAuthorization.BuildAuthorizeUrl(request.Provider!, request.ClientId, request.RedirectUri);
            return url is null
                ? Results.ValidationProblem(new Dictionary<string, string[]> { ["clientId"] = ["請先填 Client ID。"] })
                : Results.Ok(new { url, redirectUri = string.IsNullOrWhiteSpace(request.RedirectUri) ? OAuthAuthorization.LocalhostRedirect : request.RedirectUri });
        }).WithMetadata(new RequestSizeLimitAttribute(16 * 1024));

        // Setup wizard: exchanges the authorization code the user pasted back for a refresh token. When editing an existing destination the secret may be the mask string,
        // so the real value is swapped in from the stored settings first. The code is single-use and never logged.
        backup.MapPost("/oauth/exchange", async (
            OAuthExchangeRequest request,
            ISettingsRepository settings,
            IBackupHttpClientFactory httpClients,
            CancellationToken cancellationToken) =>
        {
            if (!OAuthAuthorization.Supports(request.Provider))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["provider"] = ["這種目的地不需要 OAuth 授權。"] });
            }

            var clientSecret = request.ClientSecret;
            if (clientSecret == BackupTarget.MaskedValue && request.TargetId is { } targetId)
            {
                var stored = await settings.GetAsync(cancellationToken);
                clientSecret = stored.BackupTargets?.FirstOrDefault(target => target.Id == targetId)?.ClientSecret;
            }

            try
            {
                var refreshToken = await OAuthAuthorization.ExchangeCodeAsync(
                    httpClients, request.Provider!, request.ClientId, clientSecret, request.Code, request.RedirectUri, cancellationToken);
                return Results.Ok(new { ok = true, refreshToken, message = "已取得 refresh token。" });
            }
            catch (BackupTargetException exception)
            {
                return Results.Ok(new { ok = false, refreshToken = (string?)null, message = exception.Message });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                return Results.Ok(new { ok = false, refreshToken = (string?)null, message = "換取 refresh token 失敗，請檢查服務紀錄。" });
            }
        }).WithMetadata(new RequestSizeLimitAttribute(32 * 1024));

        // Which files can be restored: the local backups in the backup directory.
        backup.MapGet("/files", async (
            ISettingsRepository settings,
            BackupService service,
            CancellationToken cancellationToken) =>
        {
            var configuration = await settings.GetAsync(cancellationToken);
            var directory = string.IsNullOrWhiteSpace(configuration.BackupDirectory)
                ? service.DefaultDirectory
                : configuration.BackupDirectory.Trim();

            if (!Directory.Exists(directory)) return Results.Ok(new { directory, files = Array.Empty<object>() });

            var files = new DirectoryInfo(directory)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file => !BackupIntegrity.IsSidecar(file.Name) &&
                               (file.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(50)
                .Select(file => new { name = file.Name, sizeBytes = file.Length, modifiedAt = file.LastWriteTimeUtc })
                .ToList();

            return Results.Ok(new { directory, files });
        });

        // Restore: clears every table and writes back the backup content. Admin-only, enforced by the middleware.
        backup.MapPost("/restore", async (
            RestoreRequest request,
            ISettingsRepository settings,
            BackupService service,
            RestoreService restore,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.FileName))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["fileName"] = ["請指定要還原的備份檔。"]
                });

            // Accepts a file name only, never a path, so it cannot be pointed at files outside the backup directory.
            var requestedName = request.FileName.Trim();
            var fileName = Path.GetFileName(requestedName);
            if (fileName.Length is < 1 or > 255 || !string.Equals(fileName, requestedName, StringComparison.Ordinal))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["fileName"] = ["只能指定備份目錄內的單一檔名。"]
                });
            var configuration = await settings.GetAsync(cancellationToken);
            var directory = string.IsNullOrWhiteSpace(configuration.BackupDirectory)
                ? service.DefaultDirectory
                : configuration.BackupDirectory.Trim();

            var path = Path.Combine(directory, fileName);

            if (!File.Exists(path)) return Results.NotFound(new { message = $"備份目錄裡找不到 {fileName}。" });

            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                return Results.BadRequest(new { message = "備份檔不可為符號連結或 reparse point。" });

            var passphrase = string.IsNullOrEmpty(request.Passphrase)
                ? configuration.BackupPassphrase
                : request.Passphrase;

            if (passphrase?.Length > 1024)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["passphrase"] = ["通關密語不可超過 1,024 個字元。"]
                });

            try
            {
                var result = await restore.RestoreAsync(
                    path,
                    passphrase,
                    request.IncludeArtifacts ?? true,
                    request.Force ?? false,
                    cancellationToken);

                await audit.RecordAsync(
                    AuditAction.Update,
                    "backup",
                    null,
                    $"從 {fileName} 還原（{result.Rows} 列；稽核紀錄保留既有 {result.AuditRowsKept} 筆、補入 {result.AuditRowsAdded} 筆）",
                    cancellationToken);

                return Results.Ok(result);
            }
            catch (InvalidOperationException exception)
            {
                return Results.Conflict(new { message = exception.Message });
            }
            catch (InvalidDataException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
        });

        return app;
    }

    /// <summary>A restore request; an empty passphrase falls back to the one in the settings.</summary>
    public sealed record RestoreRequest(
        string? FileName,
        string? Passphrase,
        bool? IncludeArtifacts,
        bool? Force);
}

public sealed record OAuthAuthorizeRequest(string? Provider, string? ClientId, string? RedirectUri);

public sealed record OAuthExchangeRequest(
    string? Provider,
    string? ClientId,
    string? ClientSecret,
    string? Code,
    string? RedirectUri,
    Guid? TargetId);
