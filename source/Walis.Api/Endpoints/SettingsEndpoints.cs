using System.Data.Common;
using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Core.Backup;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Scheduling;
using Walis.Core.Security;

namespace Walis.Api.Endpoints;

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder app)
    {
        var settings = app.MapGroup("/api/settings").WithTags("Settings");

        settings.MapGet("/", async (
            HttpContext context,
            ISettingsRepository repository,
            DataDirectory data,
            IConfiguration configuration,
            CancellationToken cancellationToken) =>
        {
            var value = await repository.GetAsync(cancellationToken);
            return Results.Ok(new
            {
                // Settings hold cloud keys, passphrases, SMTP passwords and webhook secrets; responses are always masked.
                // Even with passwords masked, backup paths, buckets, client ids and SMTP hosts are still sensitive topology;
                // non-admins receive only the few fields the UI and job editing need.
                settings = Present(value, RequestAccess.Role(context)),

                // The connection string may contain a database password: admins only, with the password redacted first.
                databasePath = RequestAccess.IsAdmin(context)
                    ? ResolveDatabasePath(data, configuration)
                    : null,
                version = WalisVersion.Display,

                // Lets the settings page say plainly when builds on this server run without a sandbox.
                sandboxAvailable = BuildIsolation.IsSandboxAvailable,
                timeZones = TimeZoneInfo.GetSystemTimeZones()
                    .Select(zone => zone.Id)
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray()
            });
        });

        settings.MapPut("/", async (
            WalisSettings request,
            HttpContext context,
            ISettingsRepository repository,
            CorsOriginStore corsOrigins,
            McpAccessStore mcpAccess,
            ISecurityRepository security,
            AuditService audit,
            SecurityEventService securityEvents,
            IReauthentication reauthentication,
            CancellationToken cancellationToken) =>
        {
            // The mask string means unchanged; swap the stored value back in before validating.
            var current = await repository.GetAsync(cancellationToken);
            request.BackupTargets ??= [];
            current.BackupTargets ??= [];
            request = BackupSettingsProtector.Merge(request, current);
            Normalize(request);
            StampIsolationAcknowledgement(request, current, RequestAccess.Session(context)?.Username);

            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            // A fresh install is fail-closed anyway; other settings must remain adjustable before any account exists.
            // Only switching from the old public mode to RequireSignIn requires confirming an admin account exists first.
            if (request.RequireSignIn && !current.RequireSignIn &&
                !await security.HasAnyUserAsync(cancellationToken))
                return Results.Conflict(new { message = "啟用登入前，請先建立一個管理帳號。" });

            // Turning sign-in or auditing off removes the protections that would notice a hijacked session; ask for the password.
            if (((current.RequireSignIn && !request.RequireSignIn) || (current.AuditLog && !request.AuditLog)) &&
                await reauthentication.RequireAsync(context, cancellationToken) is { } refused)
                return refused;

            // Recorded before the save, while the stored settings still have auditing on: switching it off must leave a trace.
            // The summaries name the changed fields only; values (SMTP passwords, secrets, cloud keys) never reach the audit table.
            await audit.RecordAsync(
                SettingsChangeDescriber.Describe(SettingsChangeDescriber.ChangedFields(current, request)),
                cancellationToken);

            foreach (var change in SettingsChangeDescriber.DescribeSecurityChanges(current, request))
                await audit.RecordAsync(change, cancellationToken);

            var saved = await repository.SaveAsync(request, cancellationToken);
            corsOrigins.Update(saved.CorsOrigins);
            mcpAccess.Update(saved.McpAllowedAddresses);

            if (current.AuditLog && !saved.AuditLog) await securityEvents.AuditDisabledAsync(cancellationToken);

            return Results.Ok(BackupSettingsProtector.Mask(saved));
        }).WithMetadata(new RequestSizeLimitAttribute(256 * 1024));

        // Secrets at rest: where the key comes from and which stored values the current key cannot read. GET /api/settings is
        // open to every signed-in role, so these two check for the admin themselves instead of relying on the path rules.
        settings.MapGet("/secrets", async (
            HttpContext context,
            SecretKeySource keySource,
            StoredSecretScanner scanner,
            CancellationToken cancellationToken) =>
        {
            if (!RequestAccess.IsAdmin(context)) return AdminOnly();

            var report = await scanner.ScanAsync(cancellationToken);
            return Results.Ok(PresentSecrets(keySource, report));
        });

        // Clears exactly the values the key cannot read, so they can be entered again; readable ones are left alone.
        settings.MapPost("/secrets/reset-undecryptable", async (
            HttpContext context,
            SecretKeySource keySource,
            StoredSecretScanner scanner,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (!RequestAccess.IsAdmin(context)) return AdminOnly();

            var cleared = await scanner.ResetUndecryptableAsync(cancellationToken);

            if (cleared.Count != 0)
                await audit.RecordAsync(AuditAction.Update, "settings", null,
                    $"清除 {cleared.Count} 筆無法解密的機密", cancellationToken);

            return Results.Ok(PresentSecrets(keySource, cleared));
        });

        return app;
    }

    private static IResult AdminOnly()
    {
        return Results.Json(new { message = "只有管理員可以查看或重設機密加密狀態。" }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static object PresentSecrets(SecretKeySource keySource, StoredSecretReport report)
    {
        return new
        {
            keySource = keySource.Description,
            keyPath = keySource.FilePath,
            undecryptable = report.Undecryptable.Select(item => new { item.Table, item.Item }),
            count = report.Count
        };
    }

    /// <summary>
    ///     Who accepted the unisolated-builds risk and when is recorded by the server, never taken from the request: the
    ///     client only says whether the switch is on. Turning the switch off clears the record so the explanation shows again.
    /// </summary>
    public static void StampIsolationAcknowledgement(WalisSettings request, WalisSettings current, string? actor)
    {
        if (!request.UnisolatedBuildsAcknowledged)
        {
            request.UnisolatedBuildsAcknowledgedAt = null;
            request.UnisolatedBuildsAcknowledgedBy = null;
            return;
        }

        if (current.UnisolatedBuildsAcknowledged && current.UnisolatedBuildsAcknowledgedAt is not null)
        {
            request.UnisolatedBuildsAcknowledgedAt = current.UnisolatedBuildsAcknowledgedAt;
            request.UnisolatedBuildsAcknowledgedBy = current.UnisolatedBuildsAcknowledgedBy;
            return;
        }

        request.UnisolatedBuildsAcknowledgedAt = DateTime.UtcNow;
        request.UnisolatedBuildsAcknowledgedBy = string.IsNullOrWhiteSpace(actor) ? null : actor.Trim();
    }

    /// <summary>Validation of backup settings; every destination needs a different set of fields.</summary>
    private static IEnumerable<(string Key, string Message)> ValidateBackup(WalisSettings settings)
    {
        if (settings.BackupEnabled && !CronExpression.TryParse(settings.BackupCron, out _, out var cronError))
            yield return ("backupCron", cronError!);

        var hasEnabledTarget = settings.BackupTargets.Any(target => !target.IsDisabled);

        if ((settings.BackupEnabled || hasEnabledTarget) && string.IsNullOrWhiteSpace(settings.BackupPassphrase))
            yield return ("backupPassphrase", "啟用備份目的地前必須設定加密通關密語。");

        if (!string.IsNullOrEmpty(settings.BackupPassphrase) &&
            settings.BackupPassphrase != BackupTarget.MaskedValue &&
            settings.BackupPassphrase.Length is < 16 or > 1024)
            yield return ("backupPassphrase", "備份通關密語需介於 16 到 1,024 個字元。");

        if (settings.BackupKeepDays is < 1 or > 3650) yield return ("backupKeepDays", "備份保留天數需介於 1 到 3650 天。");

        if (!string.IsNullOrWhiteSpace(settings.BackupDirectory) &&
            !Path.IsPathRooted(settings.BackupDirectory.Trim()))
            yield return ("backupDirectory", "備份路徑請使用絕對路徑。");

        if (settings.BackupDirectory?.Length > 4096 || settings.BackupPaths.Length > 32_768)
            yield return ("backupDirectory", "備份路徑設定過長。");

        if (settings.BackupTargets.Count > 20)
        {
            yield return ("backupTargets", "備份目的地最多 20 個。");
            yield break;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in settings.BackupTargets)
        {
            if (string.IsNullOrWhiteSpace(target.Name))
            {
                yield return ("backupTargets", "每個目的地都要有名稱。");
                continue;
            }

            if (target.Name.Length > 200 || target.Path.Length > 4096 ||
                TooLong(target.Bucket, 255) || TooLong(target.Region, 100) ||
                TooLong(target.Endpoint, 2048) || TooLong(target.AccessKeyId, 1024) ||
                TooLong(target.SecretAccessKey, 4096) || TooLong(target.ClientId, 1024) ||
                TooLong(target.ClientSecret, 4096) || TooLong(target.RefreshToken, 8192))
            {
                yield return ("backupTargets", $"{target.Name}：設定內容過長。");
                continue;
            }

            if (!names.Add(target.Name.Trim())) yield return ("backupTargets", $"目的地名稱重複：{target.Name}");

            if (!BackupProviders.IsValid(target.Provider))
            {
                yield return ("backupTargets", $"{target.Name} 的類型不支援。");
                continue;
            }

            foreach (var message in ValidateTarget(target)) yield return ("backupTargets", $"{target.Name}：{message}");
        }

        // Keep a specific message for remote targets even when scheduling is
        // disabled: a manual run is also required to be encrypted.
        var hasRemote = settings.BackupTargets
            .Any(target => !target.IsDisabled && target.Provider != BackupProviders.Local);

        if (hasRemote && string.IsNullOrWhiteSpace(settings.BackupPassphrase))
            yield return ("backupPassphrase", "有遠端目的地時必須設定加密通關密語。");

        if (settings.BackupPostgresEnabled && settings.BackupPostgresPort is < 1 or > 65535)
            yield return ("backupPostgresPort", "連接埠需介於 1 到 65535。");


        if (settings.BackupPostgresDatabases.Length > 32_768 ||
            TooLong(settings.BackupPostgresHost, 255) ||
            TooLong(settings.BackupPostgresUser, 1024) ||
            TooLong(settings.BackupPostgresPassword, 4096) ||
            TooLong(settings.BackupPgDumpPath, 4096))
            yield return ("backupPostgresDatabases", "PostgreSQL 備份設定過長。");
    }

    private static IEnumerable<string> ValidateTarget(BackupTarget target)
    {
        switch (target.Provider)
        {
            case BackupProviders.Local:
                if (string.IsNullOrWhiteSpace(target.Path) || !Path.IsPathRooted(target.Path.Trim()))
                    yield return "請填絕對路徑。";

                break;

            case BackupProviders.S3:
                if (string.IsNullOrWhiteSpace(target.Bucket)) yield return "請填 Bucket。";

                if (string.IsNullOrWhiteSpace(target.AccessKeyId) || string.IsNullOrWhiteSpace(target.SecretAccessKey))
                    yield return "請填 Access Key 與 Secret。";

                if (!string.IsNullOrWhiteSpace(target.Endpoint) &&
                    !TryHttpUri(target.Endpoint, true, out _))
                    yield return "Endpoint 必須是 HTTP 或 HTTPS 網址，且不可帶使用者資訊。";

                break;

            case BackupProviders.GoogleDrive:
                if (string.IsNullOrWhiteSpace(target.Path)) yield return "請填資料夾 ID。";

                goto case BackupProviders.Dropbox;

            case BackupProviders.Dropbox:
            case BackupProviders.OneDrive:
                if (string.IsNullOrWhiteSpace(target.ClientId) ||
                    string.IsNullOrWhiteSpace(target.ClientSecret) ||
                    string.IsNullOrWhiteSpace(target.RefreshToken))
                    yield return "請填 Client ID、Client Secret 與 Refresh Token。";

                break;
        }
    }

    private static string ResolveDatabasePath(DataDirectory data, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Walis");
        return string.IsNullOrWhiteSpace(connectionString)
            ? data.Combine("walis.db")
            : RedactConnectionString(connectionString);
    }

    /// <summary>Replaces password-like fields in the connection string with the mask; other fields (host, database name) are kept for recognition.</summary>
    public static string RedactConnectionString(string connectionString)
    {
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var key in builder.Keys.Cast<string>().ToArray())
            {
                var normalized = new string(key
                    .Where(character => !char.IsWhiteSpace(character) && character is not '-' and not '_')
                    .Select(char.ToLowerInvariant)
                    .ToArray());

                if (normalized is "password" or "pwd" or "passwd" or "secret" or "token" or
                    "accesstoken" or "apikey" or "clientsecret")
                    builder[key] = BackupTarget.MaskedValue;
            }

            return builder.ConnectionString;
        }
        catch (ArgumentException)
        {
            // On a malformed string never fall back to the raw text or split it yourself; a semicolon inside quotes/braces may be part of the password.
            return "（連線字串已設定，但格式無法安全顯示）";
        }
    }

    private static Dictionary<string, string[]> Validate(WalisSettings settings)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(settings.SiteName))
            errors["siteName"] = ["請輸入站台名稱。"];
        else if (settings.SiteName.Length > 200) errors["siteName"] = ["站台名稱不可超過 200 個字元。"];

        if (!string.IsNullOrWhiteSpace(settings.BaseUrl) &&
            (!TryHttpUri(settings.BaseUrl, false, out _) || settings.BaseUrl.Length > 2048))
            errors["baseUrl"] = ["請輸入不帶帳密的 HTTP 或 HTTPS 完整網址。"];

        if (settings.MaxConcurrentBuilds is < 1 or > 16) errors["maxConcurrentBuilds"] = ["同時建置數需介於 1 到 16。"];

        if (settings.DefaultTimeoutMinutes is < 1 or > 1440 || settings.LogRetentionDays is < 1 or > 3650)
            errors["buildLimits"] = ["建置逾時或紀錄保留天數超出允許範圍。"];

        if (settings.AuditRetentionDays is < WalisSettings.MinimumAuditRetentionDays or > WalisSettings.MaximumAuditRetentionDays)
            errors["auditRetentionDays"] =
            [
                $"稽核保留天數需介於 {WalisSettings.MinimumAuditRetentionDays} 到 {WalisSettings.MaximumAuditRetentionDays} 天。"
            ];

        if (settings.CorsOrigins.Length > 16_384 || !ValidCorsOrigins(settings.CorsOrigins))
            errors["corsOrigins"] = ["CORS 來源最多 32 個，且必須是不帶路徑與帳密的 HTTP/HTTPS origin。"];

        var validAddresses = IpAddressRange.TryParseList(settings.McpAllowedAddresses, out _, out var invalidAddress);
        if (settings.McpAllowedAddresses.Length > 8_192 || !validAddresses)
            errors["mcpAllowedAddresses"] =
            [
                invalidAddress is null
                    ? $"MCP 允許的來源位址最多 {IpAddressRange.MaximumEntries} 筆。"
                    : $"MCP 允許的來源位址最多 {IpAddressRange.MaximumEntries} 筆，每行一個 IPv4／IPv6 位址或 CIDR 範圍；無法解析：{invalidAddress}"
            ];

        if (!string.IsNullOrWhiteSpace(settings.WebhookSecret) &&
            settings.WebhookSecret != BackupTarget.MaskedValue &&
            settings.WebhookSecret.Length is < 32 or > 1024)
            errors["webhookSecret"] = ["Webhook 密鑰需介於 32 到 1,024 個字元。"];

        if (!string.IsNullOrWhiteSpace(settings.NotificationSigningSecret) &&
            settings.NotificationSigningSecret != BackupTarget.MaskedValue &&
            settings.NotificationSigningSecret.Length is < 32 or > 1024)
            errors["notificationSigningSecret"] = ["通知簽章密鑰需介於 32 到 1,024 個字元。"];

        if (settings.ExternallyIsolatedRunners.Length > 16_384 ||
            settings.SandboxDeniedPaths.Length > 32_768 ||
            settings.AllowedWorkingDirectories.Length > 32_768)
            errors["buildPaths"] = ["執行器或路徑設定過長。"];

        if (!TimeOnly.TryParse(settings.QuietHoursStart, out _) || !TimeOnly.TryParse(settings.QuietHoursEnd, out _))
            errors["quietHours"] = ["靜音時段請使用 HH:mm 格式。"];

        if (settings.TimeZone.Length > 200 || settings.Locale.Length > 50) errors["localization"] = ["時區或語系設定過長。"];

        if (settings.SmtpPort is < 1 or > 65535) errors["smtpPort"] = ["SMTP 連接埠需介於 1 到 65535。"];

        if (!string.IsNullOrWhiteSpace(settings.SmtpHost) &&
            (settings.SmtpHost.Length > 255 || !ValidSmtpHost(settings.SmtpHost)))
            errors["smtpHost"] = ["SMTP 主機名稱或 IP 位址格式無效。"];

        if (TooLong(settings.SmtpUser, 1024) || TooLong(settings.SmtpPassword, 4096))
            errors["smtpCredentials"] = ["SMTP 帳號或密碼過長。"];
        else if (string.IsNullOrWhiteSpace(settings.SmtpUser) != string.IsNullOrWhiteSpace(settings.SmtpPassword))
            errors["smtpCredentials"] = ["SMTP 帳號與密碼必須一起設定或一起留空。"];

        if (!string.IsNullOrWhiteSpace(settings.SmtpFrom) &&
            (settings.SmtpFrom.Length > 320 ||
             !MailAddress.TryCreate(settings.SmtpFrom, out var from) ||
             !string.Equals(from.Address, settings.SmtpFrom, StringComparison.OrdinalIgnoreCase)))
            errors["smtpFrom"] = ["SMTP 寄件人必須是有效的單一電子郵件位址。"];

        foreach (var (key, message) in ValidateBackup(settings)) errors[key] = [message];

        return errors;
    }

    private static object Present(WalisSettings settings, string role)
    {
        if (role == UserRole.Admin) return BackupSettingsProtector.Mask(settings);

        // The default working directory is a server path; only those who edit jobs (operator and above) need it.
        if (JobAccess.Rank(role) >= JobAccess.Rank(UserRole.Operator))
            return new
            {
                settings.SiteName,
                settings.BaseUrl,
                settings.TimeZone,
                settings.Locale,
                settings.DefaultTimeoutMinutes,
                settings.DefaultWorkingDirectory,
                settings.RequireSignIn,
                settings.RequireOtp
            };

        return new
        {
            settings.SiteName,
            settings.BaseUrl,
            settings.TimeZone,
            settings.Locale,
            settings.DefaultTimeoutMinutes,
            settings.RequireSignIn,
            settings.RequireOtp
        };
    }

    private static void Normalize(WalisSettings settings)
    {
        settings.SiteName ??= string.Empty;
        settings.BaseUrl ??= string.Empty;
        settings.TimeZone ??= string.Empty;
        settings.Locale ??= string.Empty;
        settings.CorsOrigins ??= string.Empty;
        settings.McpAllowedAddresses = string.Join('\n', IpAddressRange.Split(settings.McpAllowedAddresses));
        settings.ExternallyIsolatedRunners ??= string.Empty;
        settings.SandboxDeniedPaths ??= string.Empty;
        settings.AllowedWorkingDirectories ??= string.Empty;
        settings.BackupCron ??= string.Empty;
        settings.BackupPaths ??= string.Empty;
        settings.BackupPostgresHost ??= string.Empty;
        settings.BackupPostgresDatabases ??= string.Empty;
        settings.QuietHoursStart ??= string.Empty;
        settings.QuietHoursEnd ??= string.Empty;
        settings.SmtpHost = NullIfWhiteSpace(settings.SmtpHost);
        settings.SmtpFrom = NullIfWhiteSpace(settings.SmtpFrom);
        // Not trimmed: surrounding characters are part of the key the receiver must use.
        if (string.IsNullOrWhiteSpace(settings.NotificationSigningSecret)) settings.NotificationSigningSecret = null;
        settings.BackupTargets ??= [];

        foreach (var target in settings.BackupTargets)
        {
            target.Name ??= string.Empty;
            target.Provider ??= string.Empty;
            target.Path ??= string.Empty;
        }
    }

    private static bool TooLong(string? value, int maximum)
    {
        return value?.Length > maximum;
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool TryHttpUri(string value, bool allowImplicitHttps, out Uri? uri)
    {
        var candidate = value.Trim();
        if (allowImplicitHttps && !candidate.Contains("://", StringComparison.Ordinal))
            candidate = $"https://{candidate}";

        if (!Uri.TryCreate(candidate, UriKind.Absolute, out uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            uri = null;
            return false;
        }

        return true;
    }

    private static bool ValidSmtpHost(string value)
    {
        var host = value.Trim();
        if (host.Length == 0 || host.Any(char.IsControl) || host.Any(char.IsWhiteSpace) ||
            host.Contains('/') || host.Contains('\\'))
            return false;

        return IPAddress.TryParse(host.Trim('[', ']'), out _) ||
               Uri.CheckHostName(host) == UriHostNameType.Dns;
    }

    private static bool ValidCorsOrigins(string configured)
    {
        var values = configured.Split(
            ['\n', '\r', ','],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (values.Length > 32) return false;

        return values.All(value =>
            TryHttpUri(value, false, out var uri) &&
            uri!.AbsolutePath == "/" &&
            string.IsNullOrEmpty(uri.Query) &&
            string.IsNullOrEmpty(uri.Fragment) &&
            string.Equals(value.TrimEnd('/'), uri.GetLeftPart(UriPartial.Authority),
                StringComparison.OrdinalIgnoreCase));
    }
}