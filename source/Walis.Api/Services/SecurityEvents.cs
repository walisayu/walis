using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Services;

/// <summary>
///     Counters for /metrics. They live in memory and start at zero on every restart, and each node of a cluster counts only
///     what it saw itself; the audit log is the durable record, these exist so an external monitor can alert on a rate.
/// </summary>
public sealed class SecurityMetrics
{
    private readonly ConcurrentDictionary<string, long> _accessDenied = new(StringComparer.Ordinal);
    private long _lockouts;
    private long _signInFailures;

    public long SignInFailures => Interlocked.Read(ref _signInFailures);

    public long Lockouts => Interlocked.Read(ref _lockouts);

    /// <summary>Every refusal by reason, including the ones the audit log coalesced; known reasons are listed even at zero.</summary>
    public IReadOnlyDictionary<string, long> AccessDenied
    {
        get
        {
            var snapshot = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var reason in AccessDeniedReason.All) snapshot[reason] = 0;
            foreach (var entry in _accessDenied) snapshot[entry.Key] = entry.Value;
            return snapshot;
        }
    }

    public void CountSignInFailure()
    {
        Interlocked.Increment(ref _signInFailures);
    }

    public void CountLockout()
    {
        Interlocked.Increment(ref _lockouts);
    }

    public void CountAccessDenied(string reason)
    {
        _accessDenied.AddOrUpdate(AccessDeniedReason.Label(reason), 1, (_, count) => count + 1);
    }
}

/// <summary>
///     The <c>reason</c> label of <c>walis_access_denied_total</c> and the EntityId of an <c>access-denied</c> audit row. Values
///     are fixed identifiers (lower-case ASCII and underscores) so the metric series stay bounded.
/// </summary>
public static class AccessDeniedReason
{
    public const string McpSourceNotAllowed = "mcp_source_not_allowed";
    public const string ApiKeyAddressMismatch = "api_key_address_mismatch";

    /// <summary>A token in API key format that matched no key, or a key that is disabled or expired.</summary>
    public const string ApiKeyInvalid = "api_key_invalid";

    public const string ApiKeyAudience = "api_key_audience";
    public const string CsrfFailed = "csrf_failed";
    public const string OtpRequired = "otp_required";
    public const string RunnerKeyOutOfScope = "runner_key_out_of_scope";
    public const string InsufficientRole = "insufficient_role";

    /// <summary>A session token or cookie that matched no live session (or the wrong channel for its kind).</summary>
    public const string SessionInvalid = "session_invalid";

    /// <summary>A protected route reached with no credential at all.</summary>
    public const string Unauthenticated = "unauthenticated";

    /// <summary>The caller may see the job but its role is below the job's RunRole.</summary>
    public const string JobRunRole = "job_run_role";

    public const string WebhookUnauthorized = "webhook_unauthorized";
    public const string WebhookReplay = "webhook_replay";
    public const string WebhookRateLimited = "webhook_rate_limited";

    public static readonly IReadOnlyList<string> All =
    [
        McpSourceNotAllowed, ApiKeyAddressMismatch, ApiKeyInvalid, ApiKeyAudience, CsrfFailed, OtpRequired,
        RunnerKeyOutOfScope, InsufficientRole, SessionInvalid, Unauthenticated, JobRunRole,
        WebhookUnauthorized, WebhookReplay, WebhookRateLimited
    ];

    /// <summary>Folds an unexpected value to a safe Prometheus label so a typo in a caller cannot break the exposition format.</summary>
    public static string Label(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return "other";

        var builder = new StringBuilder(reason.Length);
        foreach (var character in reason.Take(64))
            builder.Append(character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' ? character
                : character is >= 'A' and <= 'Z' ? char.ToLowerInvariant(character) : '_');

        return builder.ToString();
    }
}

/// <summary>An account or a source address just reached the failure limit and is locked for <see cref="Duration" />.</summary>
public sealed record SignInLockout(string Account, string? Address, bool AddressLocked, TimeSpan Duration);

/// <summary>Where <see cref="SignInThrottle" /> reports a lockout; the throttle itself knows nothing about audit or notifications.</summary>
public interface ISignInLockoutSink
{
    Task LockedOutAsync(SignInLockout lockout, CancellationToken cancellationToken);
}

/// <summary>
///     The one place authentication failures, refused requests and other security-relevant events are turned into an audit row,
///     a warning in the server log, a metrics increment and (for the serious ones) a notification. Every input that came from the
///     client, the submitted account name above all, is cleaned and capped before it is stored anywhere.
///     <para>
///         Security rows are written with <see cref="AuditEvent.Force" />: turning the audit log off only silences ordinary
///         operation records, never the evidence of an attack. Refusals from one actor (or address) for one reason are written
///         once a minute per node and the metric counts every one; security alerts go out at most once per kind every five
///         minutes per node, the next alert carrying the number that was held back.
///     </para>
/// </summary>
public sealed class SecurityEventService(
    AuditService audit,
    NotificationDispatcher notifications,
    SecurityMetrics metrics,
    ISecurityRepository security,
    ILogger<SecurityEventService> logger) : ISignInLockoutSink
{
    public const string ReasonWrongPassword = "密碼不正確";
    public const string ReasonDisabled = "帳號已停用";
    public const string ReasonUnknownAccount = "帳號不存在";
    public const string ReasonWrongOtp = "一次性密碼不正確";
    public const string ReasonExpiredChallenge = "一次性密碼驗證流程已逾時或無效";
    public const string ReasonSetupCode = "設定碼不正確或未提供";

    public const string AnonymousActor = "anonymous";

    public const string AlertKindLockout = "lockout";
    public const string AlertKindAuditDisabled = "audit-disabled";
    public const string AlertKindAdminGranted = "admin-granted";

    /// <summary>One audit row per (actor or address, reason) within this window; the metric still counts every event.</summary>
    public static readonly TimeSpan AuditCoalesceWindow = TimeSpan.FromMinutes(1);

    /// <summary>One notification per alert kind within this window; the next one names how many were held back.</summary>
    public static readonly TimeSpan AlertCoalesceWindow = TimeSpan.FromMinutes(5);

    /// <summary>Caps the coalescing table so a flood of forged actors cannot grow it without bound; beyond it the oldest entries go.</summary>
    private const int MaximumCoalesceEntries = 4096;

    private const int MaximumPathLength = 300;

    private readonly Dictionary<string, AlertWindow> _alertWindows = new(StringComparer.Ordinal);
    private readonly object _alertGate = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentDenials = new(StringComparer.Ordinal);

    /// <summary>Replaceable so tests can move the coalescing windows along without sleeping.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>
    ///     The submitted account name becomes the actor, so an attacker's string is stored. It is reduced to what a username may
    ///     contain anyway (no control characters) and capped at the column length; an empty result is recorded as "（空白）".
    /// </summary>
    public static string SanitizeActor(string? submitted)
    {
        var cleaned = AuditText.Clean(submitted?.Trim(), AuditText.MaximumActorLength).Trim();
        return cleaned.Length == 0 ? "（空白）" : cleaned;
    }

    /// <summary>
    ///     The actor for an account that does not exist. A password typed into the username field by mistake must not land in
    ///     the audit table or the server log, so only the first three characters, the length and a short hash (to recognise the
    ///     same string coming back) are kept.
    /// </summary>
    public static string MaskUnknownActor(string? submitted)
    {
        var trimmed = submitted?.Trim() ?? string.Empty;
        if (trimmed.Length == 0) return "（空白）";

        var prefix = AuditText.Clean(InputSanitizer.SafeTruncate(trimmed, 3), 3);
        var ellipsis = trimmed.Length > prefix.Length ? "…" : string.Empty;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(trimmed)))[..8].ToLowerInvariant();

        return $"未知帳號（{prefix}{ellipsis}，長度 {trimmed.Length}，#{hash}）";
    }

    public Task SignInFailedAsync(string? submittedUsername, string? address, string reason, CancellationToken cancellationToken)
    {
        // Only a name that matched no account is masked: for a real account the name is the point of the record.
        var actor = reason == ReasonUnknownAccount ? MaskUnknownActor(submittedUsername) : SanitizeActor(submittedUsername);
        return RecordDeniedAsync(AuditAction.SignInFailed, actor, address, $"登入失敗：{reason}", null, cancellationToken);
    }

    public Task OtpFailedAsync(string? username, string? address, string reason, CancellationToken cancellationToken)
    {
        return RecordDeniedAsync(AuditAction.SignInFailed, SanitizeActor(username), address, $"一次性密碼驗證失敗：{reason}", null,
            cancellationToken);
    }

    /// <summary>
    ///     Anonymous and unthrottled at the endpoint, so the row is coalesced per address and minute; the submitted name is masked
    ///     because no account exists yet to compare it with.
    /// </summary>
    public Task SetupCodeRejectedAsync(string? submittedUsername, string? address, CancellationToken cancellationToken)
    {
        var cleanAddress = AuditText.CleanOrNull(address, AuditText.MaximumAddressLength) ?? "unknown";
        return RecordDeniedAsync(AuditAction.SignInFailed, MaskUnknownActor(submittedUsername), address,
            $"首次設定被拒絕：{ReasonSetupCode}", $"setup-code|{cleanAddress}", cancellationToken);
    }

    public async Task LockedOutAsync(SignInLockout lockout, CancellationToken cancellationToken)
    {
        var exists = await AccountExistsAsync(lockout.Account, cancellationToken);
        var actor = exists ? SanitizeActor(lockout.Account) : MaskUnknownActor(lockout.Account);
        var address = AuditText.CleanOrNull(lockout.Address, AuditText.MaximumAddressLength);
        var minutes = Math.Max(1, (int)Math.Ceiling(lockout.Duration.TotalMinutes));
        var summary = lockout.AddressLocked
            ? $"來源 {address ?? "（未知）"} 連續登入失敗達上限，鎖定 {minutes} 分鐘（最後嘗試的帳號：{actor}）"
            : $"帳號 {actor} 連續登入失敗達上限，鎖定 {minutes} 分鐘";

        metrics.CountLockout();
        logger.LogWarning("登入鎖定：{Summary}", summary);

        await audit.RecordAsync(new AuditEvent(AuditAction.LockedOut, "user", null, summary)
        {
            Actor = actor,
            Outcome = AuditOutcome.Denied,
            Force = true,
            Context = new AuditContext(actor, address ?? audit.RemoteAddress, audit.RequestId)
        }, cancellationToken);

        // Guessing at names that do not exist is noise for the on-call channel; the audit row above still keeps it. A locked
        // source address is reported whatever name it tried last.
        if (exists || lockout.AddressLocked) await AlertAsync(AlertKindLockout, "登入鎖定", summary, cancellationToken);
    }

    /// <summary>Auditing was switched off by an administrator; the (forced) record itself is written by the settings endpoint beforehand.</summary>
    public Task AuditDisabledAsync(CancellationToken cancellationToken)
    {
        var summary = $"{audit.Actor} 關閉了稽核紀錄（來源 {audit.RemoteAddress ?? "（未知）"}）";
        logger.LogWarning("安全告警：{Summary}", summary);
        return AlertAsync(AlertKindAuditDisabled, "稽核紀錄已關閉", summary, cancellationToken);
    }

    /// <summary>
    ///     A new administrator account, or an existing account promoted to administrator. The accounts endpoint writes its ordinary
    ///     row; this one is forced so the grant stays on record even while auditing is off.
    /// </summary>
    public async Task AdministratorGrantedAsync(string username, bool created, CancellationToken cancellationToken)
    {
        var target = AuditText.Clean(username, AuditText.MaximumActorLength);
        var summary = created
            ? $"{audit.Actor} 建立了管理員帳號 {target}（來源 {audit.RemoteAddress ?? "（未知）"}）"
            : $"{audit.Actor} 將帳號 {target} 升為管理員（來源 {audit.RemoteAddress ?? "（未知）"}）";
        logger.LogWarning("安全告警：{Summary}", summary);

        await audit.RecordAsync(new AuditEvent(AuditAction.AdminGranted, "user", target, summary) { Force = true }, cancellationToken);
        await AlertAsync(AlertKindAdminGranted, created ? "新增管理員帳號" : "帳號升為管理員", summary, cancellationToken);
    }

    /// <summary>
    ///     A request was refused: by the authentication middleware, a job-level permission check or the webhook guard. The actor is
    ///     the signed-in account, the key (<c>金鑰：名稱</c>, or <c>金鑰：前綴</c> when the key did not authenticate) or
    ///     <see cref="AnonymousActor" />; the summary is method, path (no query string) and the reason. Never throws: a refusal
    ///     must still be answered when the audit table is down.
    /// </summary>
    public async Task AccessDeniedAsync(
        HttpContext context,
        string reason,
        string detail,
        string? actor = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var label = AccessDeniedReason.Label(reason);
            metrics.CountAccessDenied(label);

            var who = AuditText.Clean(actor ?? DescribeActor(context), AuditText.MaximumActorLength);
            var address = AuditText.CleanOrNull(context.Connection.RemoteIpAddress?.ToString(), AuditText.MaximumAddressLength);
            var path = AuditText.Clean(context.Request.Path.Value, MaximumPathLength);
            var summary = $"{context.Request.Method} {path}：{detail}";
            var coalesceKey = $"{label}|{(who == AnonymousActor ? address ?? "unknown" : who)}";

            if (!ShouldRecord(coalesceKey))
            {
                logger.LogDebug("存取被拒（已合併）：{Summary}（{Actor}，來源 {Address}）", summary, who, address ?? "（未知）");
                return;
            }

            logger.LogWarning("存取被拒：{Summary}（{Actor}，來源 {Address}）", summary, who, address ?? "（未知）");

            await audit.RecordAsync(new AuditEvent(AuditAction.AccessDenied, "security", label, summary)
            {
                Actor = who,
                Outcome = AuditOutcome.Denied,
                Force = true,
                Context = new AuditContext(who, address, context.TraceIdentifier)
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "存取被拒事件記錄失敗：{Reason}", reason);
        }
    }

    private static string DescribeActor(HttpContext context)
    {
        if (context.Items[AuditService.ActorKey] is string actor && actor.Length != 0) return actor;

        return RequestAccess.Session(context)?.Username is { Length: > 0 } username ? username : AnonymousActor;
    }

    private async Task RecordDeniedAsync(
        string action,
        string actor,
        string? address,
        string summary,
        string? coalesceKey,
        CancellationToken cancellationToken)
    {
        var cleanAddress = AuditText.CleanOrNull(address, AuditText.MaximumAddressLength);

        metrics.CountSignInFailure();

        if (coalesceKey is not null && !ShouldRecord(coalesceKey))
        {
            logger.LogDebug("{Summary}（已合併；帳號 {Account}，來源 {Address}）", summary, actor, cleanAddress ?? "（未知）");
            return;
        }

        logger.LogWarning("{Summary}（帳號 {Account}，來源 {Address}）", summary, actor, cleanAddress ?? "（未知）");

        await audit.RecordAsync(new AuditEvent(action, "user", null, summary)
        {
            Actor = actor,
            Outcome = AuditOutcome.Denied,
            Force = true,
            Context = new AuditContext(actor, cleanAddress ?? audit.RemoteAddress, audit.RequestId)
        }, cancellationToken);
    }

    /// <summary>A lookup failure is treated as "exists": better one notification too many than a missed lockout of a real account.</summary>
    private async Task<bool> AccountExistsAsync(string account, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(account) || account.Length > 200) return false;

        try
        {
            return await security.FindUserAsync(account, cancellationToken) is not null;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "確認鎖定帳號是否存在時失敗，視為存在。");
            return true;
        }
    }

    /// <summary>True when no row was written for this key within <see cref="AuditCoalesceWindow" />; marks the key when it is.</summary>
    private bool ShouldRecord(string key)
    {
        var now = Clock.GetUtcNow();
        var record = false;

        _recentDenials.AddOrUpdate(key,
            _ =>
            {
                record = true;
                return now;
            },
            (_, last) =>
            {
                if (now - last < AuditCoalesceWindow) return last;

                record = true;
                return now;
            });

        if (_recentDenials.Count > MaximumCoalesceEntries) Prune(now);

        return record;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var entry in _recentDenials)
            if (now - entry.Value >= AuditCoalesceWindow) _recentDenials.TryRemove(entry.Key, out _);

        // Still full after dropping the expired ones: a flood of distinct actors. Forgetting them means a few duplicate rows,
        // which is better than letting the table grow with the attack.
        if (_recentDenials.Count > MaximumCoalesceEntries) _recentDenials.Clear();
    }

    /// <summary>
    ///     A notification must never break the request that triggered it; delivery failures are already logged by the dispatcher.
    ///     At most one alert per kind every <see cref="AlertCoalesceWindow" />; the next one names how many were held back.
    /// </summary>
    private async Task AlertAsync(string kind, string title, string body, CancellationToken cancellationToken)
    {
        int suppressed;

        lock (_alertGate)
        {
            var now = Clock.GetUtcNow();

            if (_alertWindows.TryGetValue(kind, out var window) && now - window.SentAt < AlertCoalesceWindow)
            {
                window.Suppressed++;
                logger.LogInformation("安全告警已合併（{Kind}，本時段第 {Count} 次）：{Title}", kind, window.Suppressed, title);
                return;
            }

            suppressed = window?.Suppressed ?? 0;
            _alertWindows[kind] = new AlertWindow { SentAt = now };
        }

        if (suppressed > 0) body = $"{body}\n期間另有 {suppressed} 次同類事件未逐一通知。";

        try
        {
            await notifications.DispatchAsync(
                new NotificationMessage(NotificationEventKind.SecurityAlert, $"安全告警：{title}", body, null),
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "安全告警通知發送失敗：{Title}", title);
        }
    }

    private sealed class AlertWindow
    {
        public DateTimeOffset SentAt { get; init; }
        public int Suppressed { get; set; }
    }
}
