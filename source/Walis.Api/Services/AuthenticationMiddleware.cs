using System.Net;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Services;

/// <summary>
///     Once "admin sign-in" is enabled, every /api endpoint requires a valid session token;
///     the sign-in endpoints and the push webhook are exceptions (the webhook has its own secret validation).
/// </summary>
public sealed class AuthenticationMiddleware(RequestDelegate next)
{
    public const string CookieAuthenticationKey = "walis-cookie-authentication";

    private static readonly string[] AnonymousPaths =
    [
        "/api/auth/status",
        "/api/auth/setup",
        "/api/auth/login",
        "/api/auth/login/otp",
        "/api/webhooks/push"
    ];

    private static readonly string[] SelfServicePaths =
    [
        "/api/auth/logout",
        "/api/auth/logout-all",
        "/api/auth/password",
        "/api/auth/otp/setup",
        "/api/auth/otp/enable",
        "/api/auth/otp/disable",
        "/api/auth/otp/recovery"
    ];

    /// <summary>
    ///     Paths only admins may touch, not even for reading: accounts, key lists and audit records are reconnaissance material for an attacker.
    /// </summary>
    private static readonly string[] AdminOnly =
    [
        "/api/accounts",
        "/api/actions",
        "/api/api-keys",
        "/api/audit",
        "/api/backup",
        "/api/notifications",
        "/api/plugins",
        "/api/portability",
        "/api/security-center",
        "/metrics"
    ];

    /// <summary>
    ///     Readable by all, writable by admins only. Changing these affects where secrets flow or what gets executed:
    ///     system settings, locales, plugins, restore, AI providers (the key is sent to baseUrl),
    ///     notification channels (requests to arbitrary URLs), backups (including destination credentials), runners (who may claim jobs and receive secrets).
    /// </summary>
    private static readonly string[] OperatorWrites =
    [
        "/api/jobs",
        "/api/builds",
        "/api/environment-variables",
        "/api/ai/analyze",
        "/api/ai/review-script"
    ];

    public async Task InvokeAsync(
        HttpContext context,
        ISecurityRepository security,
        ISettingsRepository settings,
        IApiKeyRepository apiKeys,
        McpAccessStore mcpAccess)
    {
        var path = context.Request.Path.Value ?? string.Empty;

        // The MCP allowlist is decided on the peer address alone, before any credential is looked up, so an unlisted
        // address cannot probe keys against /mcp. RemoteIpAddress is already the client behind a trusted proxy.
        if (Matches(path, "/mcp") && !mcpAccess.IsAllowed(context.Connection.RemoteIpAddress))
        {
            await DenyAsync(context, StatusCodes.Status403Forbidden, new { message = "此來源位址不允許使用 MCP。" },
                AccessDeniedReason.McpSourceNotAllowed, "來源位址不在 MCP 白名單");
            return;
        }

        var credential = ReadToken(context.Request);
        var token = credential.Token;

        // A credential that was presented but did not authenticate is remembered so the eventual 401 can say which kind it was;
        // nothing is recorded yet, because the route may turn out to be anonymous.
        string? rejectedCredential = null;
        string? rejectedActor = null;

        if (!string.IsNullOrEmpty(token))
        {
            // Tokens starting with walis_ are API keys (for programs and AI agents); everything else is a signed-in session.
            if (!credential.FromCookie && ApiKeyRepository.TryParse(token, out var keyPrefix, out _))
            {
                if (await apiKeys.AuthenticateAsync(token, context.RequestAborted) is { } key)
                {
                    // A key pinned to source addresses is refused from anywhere else, whatever its audience or role.
                    if (!IsKeyAllowedFrom(key, context.Connection.RemoteIpAddress))
                    {
                        await DenyAsync(context, StatusCodes.Status403Forbidden, new { message = "此來源位址不允許使用這把金鑰。" },
                            AccessDeniedReason.ApiKeyAddressMismatch, "來源位址不在金鑰的允許清單", $"金鑰：{key.Name}");
                        return;
                    }

                    context.Items[AuditService.ActorKey] = $"金鑰：{key.Name}";
                    context.Items[RequestAccess.ApiKeyKey] = key;
                    context.Items[RequestAccess.SessionKey] = new AuthSession
                    {
                        Username = $"apikey:{key.Name}",
                        DisplayName = key.Name,
                        Role = key.Role,

                        // Machines cannot do one-time passwords; the key itself is the credential.
                        HasOtp = true
                    };
                }
                else
                {
                    // Unknown, disabled or expired: the repository does not say which, and neither does the response.
                    rejectedCredential = AccessDeniedReason.ApiKeyInvalid;
                    rejectedActor = $"金鑰：{keyPrefix}";
                }
            }
            else
            {
                var session = await security.GetSessionAsync(token, context.RequestAborted);

                // Each session belongs to one channel: a browser session is only honoured from the HttpOnly cookie (so it
                // can never be replayed as a CSRF-exempt bearer), and a CLI bearer session only from the header.
                if (session is not null && session.IsBearer == credential.FromCookie) session = null;

                if (session is not null)
                {
                    context.Items[AuditService.ActorKey] = session.Username;
                    context.Items[RequestAccess.SessionKey] = session;

                    if (credential.FromCookie) context.Items[CookieAuthenticationKey] = true;
                }
                else
                {
                    rejectedCredential = AccessDeniedReason.SessionInvalid;
                }
            }
        }

        // /mcp must be authenticated like /api: it can trigger builds and must not skip validation just because it is not under /api.
        var guarded = path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) ||
                      Matches(path, "/mcp") ||
                      Matches(path, "/metrics");

        // API responses may contain sessions, one-time-password secrets, build logs or environment variables.
        // Even with a misconfigured upstream proxy, none of that should land in a shared cache or the browser's disk cache.
        if (guarded)
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers.Pragma = "no-cache";
        }

        if (context.Items[CookieAuthenticationKey] is true &&
            RequiresCsrf(context.Request.Method, path) &&
            !SessionCookieService.HasValidCsrf(context.Request))
        {
            await DenyAsync(context, StatusCodes.Status403Forbidden, new { message = "CSRF 驗證失敗，請重新整理頁面後再試。" },
                AccessDeniedReason.CsrfFailed, "CSRF 標頭缺少或與 cookie 不符");
            return;
        }

        // Machine keys have their own audience. An MCP key must not be used against ordinary REST even with the admin role;
        // a runner key is likewise narrowed before the anonymous exceptions, so it cannot escape its binding through auth/webhook paths.
        if (RequestAccess.ApiKey(context) is { } presentedKey &&
            !IsApiKeyPathAllowed(presentedKey, context.Request.Method, path))
        {
            await DenyAsync(context, StatusCodes.Status403Forbidden, new { message = "這把金鑰不能存取這個端點。" },
                AccessDeniedReason.ApiKeyAudience, $"金鑰範圍 {presentedKey.Scope} 不允許這個端點");
            return;
        }

        if (!guarded || IsAnonymousPath(path))
        {
            await next(context);
            return;
        }

        var configuration = await settings.GetAsync(context.RequestAborted);

        if (context.Items[RequestAccess.SessionKey] is AuthSession current)
        {
            // These are self-service security operations every signed-in account must be able to perform. The exact paths are listed
            // so this is never relaxed back to all of /api/auth/** , which would silently expose sensitive auth endpoints added later.
            if (HttpMethods.IsPost(context.Request.Method) &&
                SelfServicePaths.Contains(path.TrimEnd('/'), StringComparer.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            // When every account must enable one-time passwords, those who have not can only use /api/auth/* (allowed above) to finish setup.
            // This gate must be on the backend: blocking only in the frontend is bypassed by calling the API directly with the token.
            if (configuration.RequireOtp && !current.HasOtp)
            {
                await DenyAsync(context, StatusCodes.Status403Forbidden,
                    new { message = "系統要求所有帳號都必須啟用一次性密碼，請先完成設定。", otpSetupRequired = true },
                    AccessDeniedReason.OtpRequired, "帳號尚未啟用系統要求的一次性密碼");
                return;
            }

            // A key bound to a runner may only use the runner routes, no matter how high its role.
            if (RequestAccess.RunnerKey(context) is { } runnerKey)
            {
                if (IsRunnerPath(runnerKey.RunnerName!, context.Request.Method, path))
                {
                    await next(context);
                    return;
                }

                await DenyAsync(context, StatusCodes.Status403Forbidden,
                    new { message = "這把金鑰只能給執行器認領與回報建置，不能存取其他 API。" },
                    AccessDeniedReason.RunnerKeyOutOfScope, $"執行器 {runnerKey.RunnerName} 的金鑰只能使用執行器路徑");
                return;
            }

            // The MCP transport's HTTP method is always POST, so it cannot be used as the "write" signal for role checks;
            // each tool authorizes the actual reads/triggers itself with CurrentRole + JobAccess.
            // The audience was already narrowed above in IsApiKeyPathAllowed; this only applies to authenticated machine keys.
            if (RequestAccess.ApiKey(context) is not null && Matches(path, "/mcp"))
            {
                await next(context);
                return;
            }

            if (IsAllowed(current.Role, context.Request.Method, path))
            {
                await next(context);
                return;
            }

            await DenyAsync(context, StatusCodes.Status403Forbidden, new { message = "這個帳號沒有執行此操作的權限。" },
                AccessDeniedReason.InsufficientRole, $"角色 {current.Role} 不足");
            return;
        }

        var unauthenticatedDetail = rejectedCredential switch
        {
            AccessDeniedReason.ApiKeyInvalid => "金鑰不存在、已停用或已過期",
            AccessDeniedReason.SessionInvalid => "session 無效、已過期或用在錯誤的通道",
            _ => "未提供憑證"
        };

        if (!configuration.RequireSignIn)
        {
            // Older versions allowed turning sign-in off. Public reads are kept for compatibility, but anonymous requests only ever get viewer permission
            // and no longer gain admin or write capability just for having no identity.
            if (IsAllowed(UserRole.Viewer, context.Request.Method, path))
            {
                await next(context);
                return;
            }

            await DenyAsync(context, StatusCodes.Status401Unauthorized,
                new { message = "寫入操作需要登入。請先建立管理帳號並登入。" },
                rejectedCredential ?? AccessDeniedReason.Unauthenticated, unauthenticatedDetail, rejectedActor);
            return;
        }

        await DenyAsync(context, StatusCodes.Status401Unauthorized, new { message = "請先登入。" },
            rejectedCredential ?? AccessDeniedReason.Unauthenticated, unauthenticatedDetail, rejectedActor);
    }

    /// <summary>
    ///     Every refusal goes through here so each one is counted and (coalesced) audited before the response is written. The
    ///     service is taken from the request scope rather than the constructor so the middleware keeps its single-argument shape.
    /// </summary>
    private static async Task DenyAsync(
        HttpContext context,
        int statusCode,
        object payload,
        string reason,
        string detail,
        string? actor = null)
    {
        if (context.RequestServices.GetService<SecurityEventService>() is { } securityEvents)
            await securityEvents.AccessDeniedAsync(context, reason, detail, actor, context.RequestAborted);

        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(payload, context.RequestAborted);
    }

    /// <summary>Viewers may only read; operators cannot change system settings, accounts, locales or plugins.</summary>
    public static bool IsAllowed(string role, string method, string path)
    {
        if (role == UserRole.Admin) return true;

        if (AdminOnly.Any(prefix => Matches(path, prefix))) return false;

        var readOnly = HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method);

        if (role == UserRole.Viewer) return readOnly;

        if (readOnly) return role == UserRole.Operator;

        // Write permission is an allowlist. If a new API is not classified, operators are denied by default,
        // unlike a denylist where a new sensitive endpoint would silently become writable.
        return role == UserRole.Operator &&
               OperatorWrites.Any(prefix => Matches(path, prefix));
    }

    /// <summary>
    ///     The routes a runner-bound key may use: query/register runners (only its own name, the endpoint checks again),
    ///     its own heartbeat and claim, and build reports (the endpoint checks the build is assigned to it).
    /// </summary>
    public static bool IsRunnerPath(string runnerName, string method, string path)
    {
        var escaped = Uri.EscapeDataString(runnerName);
        var trimmed = path.TrimEnd('/');

        if (HttpMethods.IsGet(method)) return trimmed.Equals("/api/runners", StringComparison.OrdinalIgnoreCase);

        if (!HttpMethods.IsPost(method)) return false;

        if (trimmed.Equals("/api/runners", StringComparison.OrdinalIgnoreCase)) return true;

        foreach (var name in new[] { runnerName, escaped })
            if (trimmed.Equals($"/api/runners/{name}/heartbeat", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Equals($"/api/runners/{name}/claim", StringComparison.OrdinalIgnoreCase))
                return true;

        // /api/builds/{id}/log, /result, /steps/{stepId}/log, /steps/{stepId}/result, /artifacts
        var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);

        return segments.Length is 4 or 6 &&
               segments[0].Equals("api", StringComparison.OrdinalIgnoreCase) &&
               segments[1].Equals("builds", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[2], out _) &&
               (segments.Length == 4
                   ? segments[3] is "log" or "result" or "artifacts"
                   : segments[3] == "steps" && Guid.TryParse(segments[4], out _) && segments[5] is "log" or "result");
    }

    public static bool IsApiKeyPathAllowed(ApiKey key, string method, string path)
    {
        if (key.IsRunnerScoped)
            return key.Scope == ApiKeyScope.Runner &&
                   IsRunnerPath(key.RunnerName!, method, path);

        // Scope is a security boundary stored in the database; corrupt, old or hand-written unknown values must be rejected,
        // never treated as "anything that is not mcp is the full api".
        if (!ApiKeyScope.IsValid(key.Scope) || key.Scope == ApiKeyScope.Runner) return false;

        // Human accounts, passwords, OTP and key lifecycle need an interactive admin identity;
        // otherwise a leaked admin key could create a permanent account or mint new credentials for itself.
        if (Matches(path, "/api/auth") || Matches(path, "/api/accounts") || Matches(path, "/api/api-keys"))
            return false;

        return key.Scope switch
        {
            // api is the existing full automation scope and by compatibility contract also includes MCP.
            ApiKeyScope.Api => true,
            // The stdio MCP bridge uses this key to call a handful of REST read/trigger endpoints; enumerate them exactly
            // so it never gains settings, accounts, environment variables or general API write capability through it.
            ApiKeyScope.Mcp => Matches(path, "/mcp") || IsMcpBridgePath(method, path),
            _ => false
        };
    }

    /// <summary>
    ///     A key with an address allowlist may only be presented from a listed address. An allowlist that holds no valid
    ///     entry (a hand-edited row) refuses everywhere rather than falling back to "any address".
    /// </summary>
    public static bool IsKeyAllowedFrom(ApiKey key, IPAddress? address)
    {
        if (string.IsNullOrWhiteSpace(key.AllowedAddresses)) return true;

        var ranges = IpAddressRange.Split(key.AllowedAddresses)
            .Select(entry => IpAddressRange.TryParse(entry, out var range) ? range : null)
            .OfType<IpAddressRange>()
            .ToArray();

        return IpAddressRange.AnyContains(ranges, address);
    }

    public static bool IsMcpBridgePath(string method, string path)
    {
        var segments = path.TrimEnd('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2 ||
            !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HttpMethods.IsGet(method))
            return (segments.Length == 2 &&
                    (segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) ||
                     segments[1].Equals("builds", StringComparison.OrdinalIgnoreCase))) ||
                   (segments.Length == 3 &&
                    (segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) ||
                     segments[1].Equals("builds", StringComparison.OrdinalIgnoreCase)) &&
                    Guid.TryParse(segments[2], out _));

        return HttpMethods.IsPost(method) &&
               segments.Length == 4 &&
               segments[1].Equals("jobs", StringComparison.OrdinalIgnoreCase) &&
               Guid.TryParse(segments[2], out _) &&
               segments[3].Equals("builds", StringComparison.OrdinalIgnoreCase);
    }

    private static bool Matches(string path, string prefix)
    {
        return path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
               path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAnonymousPath(string path)
    {
        var normalized = path.Length > 1 ? path.TrimEnd('/') : path;
        return AnonymousPaths.Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static bool RequiresCsrf(string method, string path)
    {
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) || HttpMethods.IsOptions(method)) return false;

        if (!path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) &&
            !path.StartsWith("/mcp", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.StartsWith("/api/webhooks/", StringComparison.OrdinalIgnoreCase)) return false;

        var normalized = path.TrimEnd('/');
        return !new[] { "/api/auth/setup", "/api/auth/login", "/api/auth/login/otp" }
            .Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }

    private static PresentedCredential ReadToken(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();

        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return new PresentedCredential(header["Bearer ".Length..].Trim(), false);

        if (request.Headers.TryGetValue("X-Walis-Token", out var token))
            return new PresentedCredential(token.ToString(), false);

        return request.Cookies.TryGetValue(SessionCookieService.SessionCookieName, out var cookie)
            ? new PresentedCredential(cookie, true)
            : default;
    }

    private readonly record struct PresentedCredential(string? Token, bool FromCookie);
}