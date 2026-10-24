using Walis.Api.Services;
using Walis.Core.Execution;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Endpoints;

/// <summary>
///     Aggregates security posture checks, privileged resource inventory, active sign-in lockouts and recent security events
///     for the administrator Security Center. Protected by the <c>/api/security-center</c> AdminOnly rule in
///     <see cref="AuthenticationMiddleware" />.
/// </summary>
public static class SecurityCenterEndpoints
{
    public static IEndpointRouteBuilder MapSecurityCenterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/security-center").WithTags("SecurityCenter");

        group.MapGet("/", async (
            IConfiguration configuration,
            ISettingsRepository settingsRepository,
            ISecurityRepository securityRepository,
            IJobRepository jobRepository,
            IPluginRepository pluginRepository,
            IApiKeyRepository apiKeyRepository,
            IRunnerRepository runnerRepository,
            INotificationRepository notificationRepository,
            IAuditRepository auditRepository,
            SecretKeySource keySource,
            StoredSecretScanner secretScanner,
            SignInThrottle throttle,
            CancellationToken cancellationToken) =>
        {
            var settings = await settingsRepository.GetAsync(cancellationToken);
            var users = await securityRepository.ListUsersAsync(cancellationToken);
            var jobs = await jobRepository.ListJobsAsync(cancellationToken);
            var plugins = await pluginRepository.ListAsync(cancellationToken);
            var apiKeys = await apiKeyRepository.ListAsync(cancellationToken);
            var runners = await runnerRepository.ListAsync(cancellationToken);
            var channels = await notificationRepository.ListChannelsAsync(cancellationToken);
            var secretsReport = await secretScanner.ScanAsync(cancellationToken);
            var activeLockouts = await throttle.ListActiveLockoutsAsync(cancellationToken);
            var auditSummary = await auditRepository.GetSecuritySummaryAsync(
                DateTime.UtcNow.AddHours(-24),
                100,
                cancellationToken);

            var checks = BuildChecks(
                settings,
                users,
                jobs,
                channels,
                keySource,
                secretsReport);

            var passedChecks = checks.Count(check => check.Status == "pass");
            var warnChecks = checks.Count(check => check.Status == "warn");
            var dangerChecks = checks.Count(check => check.Status == "danger");
            var overallStatus = dangerChecks > 0 || activeLockouts.Count > 0
                ? "danger"
                : warnChecks > 0
                    ? "warn"
                    : "good";

            var isolationMode = BuildIsolation.IsSandboxAvailable && settings.IsolateBuilds
                ? "sandboxed"
                : !settings.AllowUnisolatedBuilds
                    ? "strict-external"
                    : settings.UnisolatedBuildsAcknowledged
                        ? "unisolated-acknowledged"
                        : "unisolated";

            var privilegedJobs = jobs
                .Where(job => job.TrustedUnsandboxed || !job.DenyNetwork)
                .Select(job => new
                {
                    job.Id,
                    job.Name,
                    job.Project,
                    runner = job.RunnerName,
                    job.TrustedUnsandboxed,
                    job.DenyNetwork,
                    job.RunRole,
                    job.ViewRole,
                    job.IsDisabled
                })
                .ToArray();

            var trustedPlugins = plugins
                .Where(plugin => plugin.IsTrusted)
                .Select(plugin => new
                {
                    plugin.PluginId,
                    name = plugin.Manifest.Name,
                    version = plugin.Manifest.Version,
                    plugin.IsDisabled,
                    plugin.IsTrusted,
                    httpPermissions = plugin.Manifest.Permissions?.Http ?? [],
                    secretPermissions = plugin.Manifest.Permissions?.Secrets ?? [],
                    plugin.InstalledAt,
                    plugin.UpdatedAt
                })
                .ToArray();

            var now = DateTime.UtcNow;
            var riskyApiKeys = apiKeys
                .Where(key => !key.IsDisabled && !key.IsExpired)
                .Select(key =>
                {
                    var flags = new List<string>();
                    if (string.IsNullOrWhiteSpace(key.AllowedAddresses)) flags.Add("unrestricted-ip");
                    if (key.ExpiresAt is null) flags.Add("no-expiry");
                    if (key.Role == UserRole.Admin) flags.Add("admin-role");
                    if (key.LastUsedAt is null || key.LastUsedAt < now.AddDays(-30)) flags.Add("stale");

                    return new
                    {
                        key.Id,
                        key.Name,
                        key.Prefix,
                        key.Role,
                        key.Scope,
                        key.RunnerName,
                        key.AllowedAddresses,
                        key.ExpiresAt,
                        key.LastUsedAt,
                        key.CreatedAt,
                        riskFlags = flags
                    };
                })
                .Where(item => item.riskFlags.Contains("unrestricted-ip") ||
                               item.riskFlags.Contains("no-expiry") ||
                               item.riskFlags.Contains("admin-role"))
                .ToArray();

            var accountItems = users
                .Select(user => new
                {
                    user.Id,
                    user.Username,
                    user.DisplayName,
                    user.Role,
                    user.IsDisabled,
                    user.HasOtp,
                    user.TotpEnabledAt,
                    user.LastLoginAt,
                    user.CreatedAt
                })
                .ToArray();

            var externalRunners = new HashSet<string>(
                (settings.ExternallyIsolatedRunners ?? string.Empty)
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                StringComparer.OrdinalIgnoreCase);

            var runnerItems = runners
                .Select(runner => new
                {
                    runner.Id,
                    runner.Name,
                    runner.OperatingSystem,
                    runner.Status,
                    runner.IsLocal,
                    runner.IsPaused,
                    serverApprovedExternal = externalRunners.Contains(runner.Name)
                })
                .ToArray();

            return Results.Ok(new
            {
                posture = new
                {
                    status = overallStatus,
                    passedChecks,
                    warnChecks,
                    dangerChecks,
                    totalChecks = checks.Count,
                    sandboxAvailable = BuildIsolation.IsSandboxAvailable,
                    isolationMode,
                    requireHttps = SessionCookieService.RequiresHttps(configuration),
                    outboundAllowPrivateNetwork = OutboundHttpHandlers.AllowsPrivateNetwork(configuration),
                    checks
                },
                inventory = new
                {
                    privilegedJobs,
                    trustedPlugins,
                    riskyApiKeys,
                    accounts = accountItems,
                    runners = runnerItems
                },
                threats = new
                {
                    activeLockouts,
                    metrics24h = auditSummary.Metrics24h,
                    recentEvents = auditSummary.RecentEvents
                }
            });
        });

        group.MapDelete("/lockouts", async (
            string? key,
            HttpContext context,
            SignInThrottle throttle,
            AuditService audit,
            IReauthentication reauthentication,
            CancellationToken cancellationToken) =>
        {
            if (RequestAccess.ApiKey(context) is not null)
                return Results.Json(
                    new { message = "請用管理帳號登入後再解除登入鎖定。" },
                    statusCode: StatusCodes.Status403Forbidden);

            if (string.IsNullOrWhiteSpace(key) || key.Length > 256)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["key"] = ["請提供要解除鎖定的識別鍵。"]
                });

            if (await reauthentication.RequireAsync(context, cancellationToken) is { } refused) return refused;

            var unlocked = await throttle.UnlockAsync(key, cancellationToken);
            if (!unlocked) return Results.NotFound();

            await audit.RecordAsync(
                AuditAction.Update,
                "security",
                key.Trim(),
                $"手動解除登入鎖定 {key.Trim()}",
                cancellationToken);

            return Results.NoContent();
        });

        return app;
    }

    private static IReadOnlyList<SecurityPostureCheck> BuildChecks(
        WalisSettings settings,
        IReadOnlyList<WalisUser> users,
        IReadOnlyList<Job> jobs,
        IReadOnlyList<NotificationChannel> channels,
        SecretKeySource keySource,
        StoredSecretReport secretsReport)
    {
        var enabledUsers = users.Where(user => !user.IsDisabled).ToList();
        var otpBoundCount = enabledUsers.Count(user => user.HasOtp);
        var allOtpBound = enabledUsers.Count > 0 && otpBoundCount == enabledUsers.Count;

        var enabledChannelCount = channels.Count(channel => channel.IsEnabled);
        var alertReady = settings.NotifyOnSecurityAlert && enabledChannelCount > 0;

        var sandboxActive = (BuildIsolation.IsSandboxAvailable && settings.IsolateBuilds) ||
                            !settings.AllowUnisolatedBuilds;
        var hasWorkdirAllowlist = !string.IsNullOrWhiteSpace(settings.AllowedWorkingDirectories);

        var hasWebhookSecret = !string.IsNullOrWhiteSpace(settings.WebhookSecret);
        var hasSigningSecret = !string.IsNullOrWhiteSpace(settings.NotificationSigningSecret);
        var hasPushJobs = jobs.Any(job =>
            !job.IsDisabled && string.Equals(job.TriggerKind, TriggerMode.Push, StringComparison.OrdinalIgnoreCase));
        var hasWebhookChannels = channels.Any(channel =>
            channel.IsEnabled && string.Equals(channel.Kind, "webhook", StringComparison.OrdinalIgnoreCase));
        var webhookOk = (!hasPushJobs || hasWebhookSecret) && (!hasWebhookChannels || hasSigningSecret);

        var hasMcpAllowlist = !string.IsNullOrWhiteSpace(settings.McpAllowedAddresses);
        var backupReady = settings.BackupEnabled && !string.IsNullOrWhiteSpace(settings.BackupPassphrase);

        return
        [
            new SecurityPostureCheck(
                "auth-signin",
                settings.RequireSignIn ? "pass" : "danger",
                "身分與存取",
                "管理端強制登入（requireSignIn）",
                settings.RequireSignIn
                    ? "已開啟；匿名請求無法存取任何寫入端點、MCP 與 /metrics。"
                    : "管理端登入已關閉；建議立即啟用以保護 API 與建置權限。",
                "policies"),
            new SecurityPostureCheck(
                "auth-otp",
                settings.RequireOtp && allOtpBound ? "pass" : "warn",
                "身分與存取",
                "全體帳號雙因素驗證（OTP）覆蓋率",
                settings.RequireOtp && allOtpBound
                    ? $"已強制要求 OTP，且全部 {enabledUsers.Count} 個啟用帳號皆已完成綁定。"
                    : !settings.RequireOtp
                        ? $"尚未開啟「要求所有帳號啟用一次性密碼」（目前已綁定 {otpBoundCount} / {enabledUsers.Count} 個啟用帳號）。"
                        : $"系統已要求啟用 OTP，尚有 {enabledUsers.Count - otpBoundCount} 個啟用帳號未完成綁定（{otpBoundCount} / {enabledUsers.Count} 已綁定）。",
                !settings.RequireOtp ? "policies" : "inventory"),
            new SecurityPostureCheck(
                "notify-alert",
                alertReady ? "pass" : "warn",
                "身分與存取",
                "安全告警即時通知（securityAlert）",
                alertReady
                    ? $"已開啟安全告警通知，並連結 {enabledChannelCount} 個啟用中的通知管道。"
                    : !settings.NotifyOnSecurityAlert
                        ? "已停用安全告警通知；鎖定或關閉稽核時不會發送外部通知。"
                        : "尚未啟用任何通知管道；請至少設定一個通知管道以接收安全告警。",
                "notifications"),
            new SecurityPostureCheck(
                "build-sandbox",
                sandboxActive ? "pass" : settings.UnisolatedBuildsAcknowledged ? "warn" : "danger",
                "建置與執行邊界",
                "建置沙箱與執行隔離邊界",
                BuildIsolation.IsSandboxAvailable && settings.IsolateBuilds
                    ? $"本機已啟用作業系統沙箱（sandbox-exec）{(settings.SandboxDenyProcessControl ? "並禁止程序控制" : string.Empty)}{(settings.AllowUnisolatedBuilds ? string.Empty : "，且拒絕無邊界執行")}。"
                    : !settings.AllowUnisolatedBuilds
                        ? "已開啟「沒有隔離邊界時拒絕執行」，僅允許具備外部隔離的執行器執行建置。"
                        : settings.UnisolatedBuildsAcknowledged
                            ? $"目前允許未隔離建置（已由 {settings.UnisolatedBuildsAcknowledgedBy ?? "管理員"} 簽認風險）。"
                            : "目前建置腳本在無沙箱邊界下執行；建議開啟沙箱或要求外部隔離邊界。",
                "settings-build"),
            new SecurityPostureCheck(
                "build-workdir",
                hasWorkdirAllowlist ? "pass" : "warn",
                "建置與執行邊界",
                "允許的工作目錄白名單（allowedWorkingDirectories）",
                hasWorkdirAllowlist
                    ? "已設定允許的工作目錄白名單，限制任務可讀寫的主機路徑。"
                    : "尚未設定工作目錄白名單；建議限制自訂工作目錄的允許根路徑。",
                "settings-build"),
            new SecurityPostureCheck(
                "secret-encryption",
                secretsReport.Count == 0 ? "pass" : "danger",
                "機密與金鑰健康度",
                "資料庫靜態加密（AES-256-GCM）與金鑰健康度",
                secretsReport.Count == 0
                    ? $"金鑰來源：{keySource.Description}；所有已加密機密皆可正常解密。"
                    : $"金鑰來源：{keySource.Description}；偵測到 {secretsReport.Count} 筆機密無法以目前金鑰解密。",
                "policies"),
            new SecurityPostureCheck(
                "secret-mask",
                !settings.AuditLog ? "danger" : !settings.MaskSecrets ? "warn" : "pass",
                "機密與金鑰健康度",
                "操作稽核紀錄與建置輸出機密遮蔽",
                settings.AuditLog && settings.MaskSecrets
                    ? $"操作稽核已開啟（保留 {settings.AuditRetentionDays} 天），且建置輸出與 AI 提示詞自動遮蔽機密變體。"
                    : !settings.AuditLog
                        ? "操作稽核紀錄目前已關閉！"
                        : "建置輸出機密遮蔽已關閉，日誌可能顯示機密變數明文。",
                "policies"),
            new SecurityPostureCheck(
                "webhook-signing",
                webhookOk ? "pass" : "warn",
                "機密與金鑰健康度",
                "Webhook 驗證密鑰與通知簽章密鑰（Walis-Signature）",
                hasWebhookSecret && hasSigningSecret
                    ? "已同時設定推送 Webhook 驗證密鑰與通知 HMAC-SHA256 簽章密鑰。"
                    : webhookOk
                        ? $"Webhook 密鑰：{(hasWebhookSecret ? "已設定" : "未設定（目前無推送觸發任務）")} · 通知簽章密鑰：{(hasSigningSecret ? "已設定" : "未設定（目前無 Webhook 通知管道）")}。"
                        : "有啟用推送觸發任務或 Webhook 通知管道，但尚未設定對應的驗證／簽章密鑰。",
                "policies"),
            new SecurityPostureCheck(
                "net-mcp",
                hasMcpAllowlist ? "pass" : "warn",
                "網路與對外端點",
                "MCP 端點來源位址白名單（mcpAllowedAddresses）",
                hasMcpAllowlist
                    ? "已限制僅允許指定 IP／CIDR 位址連線 /mcp。"
                    : "目前未限制 /mcp 來源位址；建議限制為本機（127.0.0.1/32、::1/128）或受信任內網。",
                "fix-mcp"),
            new SecurityPostureCheck(
                "backup-encrypted",
                backupReady ? "pass" : "warn",
                "網路與對外端點",
                "定期加密備份排程",
                backupReady
                    ? $"已啟用排程備份（{settings.BackupCron}）並設定 AEAD 加密通關密語。"
                    : "尚未啟用自動排程備份或未設定加密通關密語。",
                "settings-backup")
        ];
    }
}

public sealed record SecurityPostureCheck(
    string Id,
    string Status,
    string Domain,
    string Title,
    string Detail,
    string Action);
