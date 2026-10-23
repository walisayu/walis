using System.Net;
using System.Security.Cryptography;
using System.Text;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Endpoints;

public static class AuthEndpoints
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(12);

    /// <summary>A CLI that signs in with <c>?bearer=true</c> gets its own short session; walis-runner signs in again when it expires.</summary>
    public static readonly TimeSpan BearerSessionLifetime = TimeSpan.FromHours(1);

    private static readonly string DummyPasswordHash = PasswordHasher.Hash("walis-dummy-password");

    /// <summary>How long the window between passing the password and entering the one-time password stays valid.</summary>
    private static readonly TimeSpan OtpChallengeLifetime = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth").WithTags("Auth");

        auth.MapGet("/status", async (
            ISecurityRepository security,
            ISettingsRepository settings,
            IHostEnvironment environment,
            BootstrapPolicy bootstrap,
            HttpContext context,
            CancellationToken cancellationToken) =>
        {
            var configuration = await settings.GetAsync(cancellationToken);
            var session = context.Items["walis-session"] as AuthSession;
            var user = session is null ? null : await security.GetUserAsync(session.UserId, cancellationToken);
            var hasUsers = await security.HasAnyUserAsync(cancellationToken);

            return Results.Ok(new
            {
                requireSignIn = configuration.RequireSignIn,
                requireOtp = configuration.RequireOtp,
                hasUsers,

                // Tells the setup screen whether to ask for the setup code; it says nothing about the code itself.
                setupCodeRequired = !hasUsers && !IsLocalRequest(context, environment, bootstrap),

                // Only while there is no administrator: the setup screen explains that builds without an isolation boundary
                // let anyone who can edit or run a job read every secret, and asks the first administrator to decide.
                unisolatedBuildsAllowed = !hasUsers && configuration.AllowUnisolatedBuilds,
                user = user is null
                    ? null
                    : new { user.Username, user.DisplayName, user.Role, hasOtp = user.HasOtp }
            });
        });

        auth.MapPost("/setup", async (
            CredentialsRequest request,
            bool? bearer,
            HttpContext context,
            IConfiguration configuration,
            IHostEnvironment environment,
            BootstrapPolicy bootstrap,
            ISecurityRepository security,
            ISettingsRepository settings,
            SetupCode setupCode,
            AuditService audit,
            SecurityEventService securityEvents,
            CancellationToken cancellationToken) =>
        {
            if (!CanBootstrap(context, configuration, environment, bootstrap, setupCode))
            {
                await securityEvents.SetupCodeRejectedAsync(request.Username, RemoteAddress(context), cancellationToken);
                return Results.Json(
                    new
                    {
                        message = "從其他機器進行首次設定需要設定碼。設定碼在伺服器啟動時印在主控台，" +
                                  $"也存在資料目錄的 {SetupCode.FileName}。"
                    },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            var errors = AccountEndpoints.ValidateCredentials(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var user = await security.TryCreateFirstAdminAsync(
                new UserDraft(request.Username!, request.DisplayName ?? request.Username!, request.Password!,
                    UserRole.Admin),
                cancellationToken);

            if (user is null) return Results.Conflict(new { message = "已經有帳號了，請直接登入。" });

            setupCode.Clear();

            // An old database may still hold RequireSignIn=false; revoke anonymous access as soon as the secure bootstrap completes.
            var currentSettings = await settings.GetAsync(cancellationToken);
            var settingsChanged = false;

            if (!currentSettings.RequireSignIn)
            {
                currentSettings.RequireSignIn = true;
                settingsChanged = true;
            }

            // The setup screen showed the isolation explanation and the first administrator answered it; accepting is
            // recorded with their name so the settings page can show who took the decision.
            if (request.AllowUnisolatedBuilds is { } allowUnisolated)
            {
                currentSettings.AllowUnisolatedBuilds = allowUnisolated;
                currentSettings.UnisolatedBuildsAcknowledged = allowUnisolated;
                SettingsEndpoints.StampIsolationAcknowledgement(currentSettings, new WalisSettings(), user.Username);
                settingsChanged = true;
            }

            if (settingsChanged) await settings.SaveAsync(currentSettings, cancellationToken);

            await audit.RecordAsync(
                new AuditEvent(AuditAction.Create, "user", user.Id.ToString(), $"建立第一個管理帳號 {user.Username}")
                {
                    Actor = user.Username
                },
                cancellationToken);

            return Results.Ok(await SignInAsync(context, security, user, bearer, false, cancellationToken));
        }).RequireRateLimiting("auth-ingress");

        auth.MapPost("/login", async (
            CredentialsRequest request,
            bool? bearer,
            HttpContext context,
            ISecurityRepository security,
            SignInThrottle throttle,
            AuditService audit,
            SecurityEventService securityEvents,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Username) ||
                request.Username.Length > 40 ||
                string.IsNullOrWhiteSpace(request.Password) ||
                request.Password.Length > AccountEndpoints.MaximumPasswordLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["username"] = ["請輸入帳號與密碼。"]
                });

            var address = RemoteAddress(context);

            // The account bucket is keyed by the account the database matched, not by the string sent: the lookup follows the
            // database collation (accent- and width-insensitive on MySQL and SQL Server), so "ádmin" and "ａdmin" reach "admin"
            // and must share its five attempts. Only a name that matches nothing is counted under what was typed.
            var user = await security.FindUserAsync(request.Username, cancellationToken);
            var account = user?.Username ?? request.Username;
            var attempt = await throttle.BeginAttemptAsync(account, address, cancellationToken);

            if (attempt.RetryAfter is { } wait) return TooManyAttempts(wait);

            var passwordMatches = PasswordHasher.Verify(request.Password, user?.PasswordHash ?? DummyPasswordHash);

            if (user is null || user.IsDisabled || !passwordMatches)
            {
                // The client gets one generic answer; the audit row (admin-only) keeps the real reason so a sweep across accounts is recognisable.
                var reason = user is null ? SecurityEventService.ReasonUnknownAccount
                    : user.IsDisabled ? SecurityEventService.ReasonDisabled
                    : SecurityEventService.ReasonWrongPassword;
                await securityEvents.SignInFailedAsync(request.Username, address, reason, cancellationToken);
                await throttle.ConfirmFailureAsync(attempt, account, address, cancellationToken);

                return Results.Json(new { message = "帳號或密碼不正確。" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // Hashes from before the iteration increase are rewritten while the plaintext is at hand; a concurrent password change
            // wins (the update only applies to the hash that was just verified).
            if (PasswordHasher.NeedsRehash(user.PasswordHash))
                await security.ChangePasswordIfCurrentAsync(user.Id, user.PasswordHash, request.Password, cancellationToken);

            if (user.HasOtp)
            {
                // Only the account reservation is handed back: the source keeps paying one attempt per challenge, so a known
                // password cannot be used to create challenges without limit.
                await throttle.CancelAttemptAsync(account, null, cancellationToken);

                // A correct password only passes the first gate; OTP failures are counted separately, and the account lock lifts only after both gates pass.
                return Results.Ok(new
                {
                    otpRequired = true,
                    challenge = await security.CreateOtpChallengeAsync(user.Id, OtpChallengeLifetime, cancellationToken)
                });
            }

            await throttle.RecordSuccessAsync(account, address, cancellationToken);

            var presented = await SignInAsync(context, security, user, bearer, false, cancellationToken);

            // No session existed when the request came in, so the actor is set explicitly: the row must say who signed in, not "system".
            await audit.RecordAsync(
                new AuditEvent(AuditAction.SignIn, "user", user.Id.ToString(),
                    presented.Token is null ? $"{user.Username} 登入" : $"{user.Username} 登入（取得 bearer 權杖）") { Actor = user.Username },
                cancellationToken);

            return Results.Ok(presented);
        }).RequireRateLimiting("auth-ingress");

        auth.MapPost("/login/otp", async (
            OtpRequest request,
            bool? bearer,
            HttpContext context,
            ISecurityRepository security,
            SignInThrottle throttle,
            AuditService audit,
            SecurityEventService securityEvents,
            CancellationToken cancellationToken) =>
        {
            var address = RemoteAddress(context);

            if (string.IsNullOrWhiteSpace(request.Challenge) || request.Challenge.Length > 128 ||
                string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64)
                return Results.Json(
                    new { message = "驗證流程或驗證碼無效。" },
                    statusCode: StatusCodes.Status401Unauthorized);

            if (await security.ConsumeOtpChallengeAsync(request.Challenge, cancellationToken) is not { } userId)
            {
                await securityEvents.OtpFailedAsync(null, address, SecurityEventService.ReasonExpiredChallenge, cancellationToken);
                return Results.Json(
                    new { message = "驗證流程已逾時，請重新登入。" },
                    statusCode: StatusCodes.Status401Unauthorized);
            }

            var user = await security.GetUserAsync(userId, cancellationToken);

            if (user is null || user.IsDisabled)
            {
                await securityEvents.OtpFailedAsync(user?.Username, address, SecurityEventService.ReasonDisabled, cancellationToken);
                return Results.Json(new { message = "帳號無法登入。" }, statusCode: StatusCodes.Status401Unauthorized);
            }

            // A six-digit code cannot survive unlimited guesses, so failures are counted together with the password gate; while locked no new challenge is issued either.
            var attempt = await throttle.BeginAttemptAsync(user.Username, address, cancellationToken);

            if (attempt.RetryAfter is { } wait) return TooManyAttempts(wait);

            var usedRecoveryCode = false;

            if (!await security.ConsumeTotpCodeAsync(user.Id, request.Code, cancellationToken))
            {
                if (string.IsNullOrWhiteSpace(request.Code) ||
                    !await security.ConsumeRecoveryCodeAsync(user.Id, request.Code, cancellationToken))
                {
                    await securityEvents.OtpFailedAsync(user.Username, address, SecurityEventService.ReasonWrongOtp, cancellationToken);
                    await throttle.ConfirmFailureAsync(attempt, user.Username, address, cancellationToken);

                    // The challenge was consumed; issue a new one so the user can try again.
                    return Results.Json(
                        new
                        {
                            message = "驗證碼不正確，請再試一次或改用備援碼。",
                            challenge = await security.CreateOtpChallengeAsync(
                                user.Id, OtpChallengeLifetime, cancellationToken)
                        },
                        statusCode: StatusCodes.Status401Unauthorized);
                }

                usedRecoveryCode = true;
            }

            await throttle.RecordSuccessAsync(user.Username, address, cancellationToken);

            var presented = await SignInAsync(context, security, user, bearer, usedRecoveryCode, cancellationToken);
            await audit.RecordAsync(
                new AuditEvent(
                    AuditAction.SignIn,
                    "user",
                    user.Id.ToString(),
                    usedRecoveryCode ? $"{user.Username} 以備援碼登入" : $"{user.Username} 通過一次性密碼登入")
                {
                    Actor = user.Username
                },
                cancellationToken);

            return Results.Ok(presented);
        }).RequireRateLimiting("auth-ingress");

        auth.MapPost("/logout", async (
            HttpContext context,
            ISecurityRepository security,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (context.Items["walis-session"] is AuthSession session)
            {
                await security.DeleteSessionAsync(session.Token, cancellationToken);
                await audit.RecordAsync(AuditAction.SignOut, "user", session.UserId.ToString(),
                    $"{session.Username} 登出", cancellationToken);
            }

            SessionCookieService.Clear(context);

            return Results.NoContent();
        });

        // Lost a laptop, left a session open somewhere: revokes every session of the signed-in account, this one included.
        auth.MapPost("/logout-all", async (
            HttpContext context,
            ISecurityRepository security,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (context.Items["walis-session"] is not AuthSession session)
                return Results.Json(new { message = "請先登入。" }, statusCode: StatusCodes.Status401Unauthorized);

            var revoked = await security.DeleteSessionsForUserAsync(session.UserId, null, cancellationToken);
            await audit.RecordAsync(AuditAction.SignOut, "user", session.UserId.ToString(),
                $"{session.Username} 登出所有裝置（撤銷 {revoked} 個工作階段）", cancellationToken);

            SessionCookieService.Clear(context);

            return Results.Ok(new { revoked });
        });

        auth.MapPost("/password", async (
            PasswordRequest request,
            HttpContext context,
            ISecurityRepository security,
            SignInThrottle throttle,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (context.Items["walis-session"] is not AuthSession session)
                return Results.Json(new { message = "請先登入。" }, statusCode: StatusCodes.Status401Unauthorized);

            if (string.IsNullOrWhiteSpace(request.NewPassword) ||
                request.NewPassword.Length < AccountEndpoints.MinimumPasswordLength || request.NewPassword.Length > AccountEndpoints.MaximumPasswordLength ||
                request.CurrentPassword?.Length > AccountEndpoints.MaximumPasswordLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["newPassword"] = [$"新密碼須為 {AccountEndpoints.MinimumPasswordLength} 到 {AccountEndpoints.MaximumPasswordLength} 個字元。"]
                });

            var user = await security.GetUserAsync(session.UserId, cancellationToken);
            var address = RemoteAddress(context);

            if (await throttle.TryBeginAttemptAsync(session.Username, address, cancellationToken) is { } wait)
                return TooManyAttempts(wait);

            if (user is null || !PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            if (!await security.ChangePasswordIfCurrentAsync(
                    user.Id,
                    user.PasswordHash,
                    request.NewPassword,
                    cancellationToken))
                return Results.Conflict(new
                {
                    message = "帳號憑證剛剛已被變更，請重新登入後再試。"
                });

            await throttle.RecordSuccessAsync(user.Username, address, cancellationToken);

            // A password change usually follows a suspected leak: every session goes, the current one included, because its token
            // may be what leaked. A browser gets a fresh cookie session so it stays signed in; a bearer client signs in again.
            await security.DeleteSessionsForUserAsync(user.Id, null, cancellationToken);

            if (context.Items[AuthenticationMiddleware.CookieAuthenticationKey] is true)
                SessionCookieService.Issue(context,
                    await security.CreateSessionAsync(user.Id, SessionLifetime, false, cancellationToken));
            await audit.RecordAsync(AuditAction.Update, "user", user.Id.ToString(), $"{user.Username} 變更密碼",
                cancellationToken);

            return Results.NoContent();
        });

        MapOtpEndpoints(auth);

        return app;
    }

    private static void MapOtpEndpoints(RouteGroupBuilder auth)
    {
        // Starting and finishing enrollment both re-verify the password, like turning it off does: a hijacked session must not be able
        // to bind its own authenticator to the account and lock the real owner out (enabling revokes the other sessions).
        auth.MapPost("/otp/setup", async (
            PasswordRequest request,
            HttpContext context,
            ISecurityRepository security,
            ISettingsRepository settings,
            SignInThrottle throttle,
            CancellationToken cancellationToken) =>
        {
            if (request.CurrentPassword?.Length > AccountEndpoints.MaximumPasswordLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            if (context.Items["walis-session"] is not AuthSession session)
                return Results.Json(new { message = "請先登入。" }, statusCode: StatusCodes.Status401Unauthorized);

            var user = await security.GetUserAsync(session.UserId, cancellationToken);

            if (user is null) return Results.NotFound();

            if (user.HasOtp) return Results.Conflict(new { message = "這個帳號已經設定過一次性密碼。" });

            var address = RemoteAddress(context);

            if (await throttle.TryBeginAttemptAsync(user.Username, address, cancellationToken) is { } wait)
                return TooManyAttempts(wait);

            if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            await throttle.RecordSuccessAsync(user.Username, address, cancellationToken);

            var configuration = await settings.GetAsync(cancellationToken);
            var secret = Totp.CreateSecret();

            // Not enabled yet; store the secret first so the user can verify it.
            if (!await security.BeginTotpEnrollmentAsync(user.Id, user.PasswordHash, secret, cancellationToken))
                return Results.Conflict(new { message = "帳號的安全設定已變更，請重新登入後再試。" });

            return Results.Ok(new
            {
                secret,
                uri = Totp.BuildUri(configuration.SiteName, user.Username, secret),
                digits = Totp.Digits,
                period = Totp.PeriodSeconds
            });
        });

        auth.MapPost("/otp/enable", async (
            OtpEnableRequest request,
            HttpContext context,
            ISecurityRepository security,
            SignInThrottle throttle,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 64)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["code"] = ["驗證碼格式不正確。"]
                });

            if (request.CurrentPassword?.Length > AccountEndpoints.MaximumPasswordLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            if (context.Items["walis-session"] is not AuthSession session)
                return Results.Json(new { message = "請先登入。" }, statusCode: StatusCodes.Status401Unauthorized);

            var user = await security.GetUserAsync(session.UserId, cancellationToken);

            if (user is null || string.IsNullOrWhiteSpace(user.TotpSecret))
                return Results.Conflict(new { message = "請先產生一次性密碼設定。" });

            if (user.HasOtp) return Results.Conflict(new { message = "這個帳號已經設定過一次性密碼。" });

            var address = RemoteAddress(context);

            if (await throttle.TryBeginAttemptAsync(user.Username, address, cancellationToken) is { } wait)
                return TooManyAttempts(wait);

            // The password is checked before the code so a wrong password never consumes a valid code's time step.
            if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            if (!Totp.TryVerify(user.TotpSecret, request.Code, out var counter))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["code"] = ["驗證碼不正確，請確認裝置時間是否正確。"]
                });

            var recoveryCodes = Totp.CreateRecoveryCodes();
            if (!await security.EnableTotpAsync(
                    user.Id, user.PasswordHash, user.TotpSecret, counter, recoveryCodes, cancellationToken))
                return Results.Conflict(new { message = "一次性密碼設定已變更，請重新確認設定。" });

            await throttle.RecordSuccessAsync(user.Username, address, cancellationToken);
            await security.DeleteSessionsForUserAsync(user.Id, session.Token, cancellationToken);
            await audit.RecordAsync(AuditAction.Update, "user", user.Id.ToString(), $"{user.Username} 啟用一次性密碼",
                cancellationToken);

            return Results.Ok(new { recoveryCodes });
        });

        auth.MapPost("/otp/disable", async (
            PasswordRequest request,
            HttpContext context,
            ISecurityRepository security,
            ISettingsRepository settings,
            SignInThrottle throttle,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (request.CurrentPassword?.Length > AccountEndpoints.MaximumPasswordLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            if (context.Items["walis-session"] is not AuthSession session)
                return Results.Json(new { message = "請先登入。" }, statusCode: StatusCodes.Status401Unauthorized);

            var configuration = await settings.GetAsync(cancellationToken);

            if (configuration.RequireOtp) return Results.Conflict(new { message = "系統要求所有帳號都必須啟用一次性密碼。" });

            var user = await security.GetUserAsync(session.UserId, cancellationToken);
            var address = RemoteAddress(context);

            if (await throttle.TryBeginAttemptAsync(session.Username, address, cancellationToken) is { } wait)
                return TooManyAttempts(wait);

            if (user is null || !PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            await throttle.RecordSuccessAsync(user.Username, address, cancellationToken);

            await security.SetTotpAsync(user.Id, null, null, null, cancellationToken);
            await security.DeleteSessionsForUserAsync(user.Id, session.Token, cancellationToken);
            await audit.RecordAsync(AuditAction.Update, "user", user.Id.ToString(), $"{user.Username} 停用一次性密碼",
                cancellationToken);

            return Results.NoContent();
        });

        auth.MapPost("/otp/recovery", async (
            PasswordRequest request,
            HttpContext context,
            ISecurityRepository security,
            SignInThrottle throttle,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            if (request.CurrentPassword?.Length > AccountEndpoints.MaximumPasswordLength)
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            if (context.Items["walis-session"] is not AuthSession session)
                return Results.Json(new { message = "請先登入。" }, statusCode: StatusCodes.Status401Unauthorized);

            var user = await security.GetUserAsync(session.UserId, cancellationToken);

            if (user is null || !user.HasOtp) return Results.Conflict(new { message = "尚未啟用一次性密碼。" });

            var address = RemoteAddress(context);

            if (await throttle.TryBeginAttemptAsync(user.Username, address, cancellationToken) is { } wait)
                return TooManyAttempts(wait);

            if (!PasswordHasher.Verify(request.CurrentPassword ?? string.Empty, user.PasswordHash))
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["currentPassword"] = ["目前的密碼不正確。"]
                });

            await throttle.RecordSuccessAsync(user.Username, address, cancellationToken);

            var recoveryCodes = Totp.CreateRecoveryCodes();
            await security.SetTotpAsync(user.Id, user.TotpSecret, user.TotpEnabledAt, recoveryCodes, cancellationToken);
            await audit.RecordAsync(AuditAction.Update, "user", user.Id.ToString(), $"{user.Username} 重新產生備援碼",
                cancellationToken);

            return Results.Ok(new { recoveryCodes });
        });
    }

    private static string? RemoteAddress(HttpContext context)
    {
        return context.Connection.RemoteIpAddress?.ToString();
    }

    /// <summary>
    ///     Whether the request comes from the machine Walis runs on, addressed as such. A loopback peer under any other host name
    ///     is a reverse proxy that did not forward the client address, or a page that rebound its own name to 127.0.0.1; neither
    ///     is the person sitting at this machine.
    /// </summary>
    private static bool IsLocalRequest(HttpContext context, IHostEnvironment environment, BootstrapPolicy bootstrap)
    {
        // The deployment may switch the shortcut off altogether (Security:RequireSetupCode) or, when the server is reachable from
        // other machines, demand that nothing forwarded the request and that the Host port is one Walis listens on.
        if (!bootstrap.AllowsLocalShortcut(context.Request)) return false;

        // TestServer has no physical socket, so RemoteIpAddress is null; only an explicit Test environment may pass.
        if (context.Connection.RemoteIpAddress is not { } address) return environment.IsEnvironment("Test");

        if (!IPAddress.IsLoopback(address)) return false;

        var host = context.Request.Host.Host;
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
               (IPAddress.TryParse(host.Trim('[', ']'), out var literal) && IPAddress.IsLoopback(literal));
    }

    private static bool CanBootstrap(
        HttpContext context,
        IConfiguration configuration,
        IHostEnvironment environment,
        BootstrapPolicy bootstrap,
        SetupCode setupCode)
    {
        if (IsLocalRequest(context, environment, bootstrap)) return true;

        var expected = configuration["Security:BootstrapToken"];
        var presented = context.Request.Headers["X-Walis-Bootstrap-Token"].ToString();

        if (setupCode.Matches(presented)) return true;

        if (string.IsNullOrWhiteSpace(expected) || expected.Length is < 32 or > 1024 ||
            string.IsNullOrEmpty(presented) || presented.Length > 1024)
            return false;

        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        return CryptographicOperations.FixedTimeEquals(
            expectedHash,
            presentedHash);
    }

    private static IResult TooManyAttempts(TimeSpan wait)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(wait.TotalMinutes));

        return Results.Json(
            new { message = $"登入失敗次數過多，請 {minutes} 分鐘後再試。", retryAfterSeconds = (int)wait.TotalSeconds },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>
    ///     Two kinds of sign-in. A browser gets the 12-hour HttpOnly cookie and no token in the body, so a script running in the
    ///     page (XSS, a rogue extension) cannot read a credential it could replay from elsewhere without CSRF checks.
    ///     A command-line client asks for <c>?bearer=true</c> and gets a separate one-hour bearer session in the JSON instead;
    ///     anything that looks like a browser (an Origin header, an HTML Accept, the CSRF header) never gets the token.
    /// </summary>
    private static async Task<SignInResponse> SignInAsync(
        HttpContext context,
        ISecurityRepository security,
        WalisUser user,
        bool? bearer,
        bool usedRecoveryCode,
        CancellationToken cancellationToken)
    {
        var account = new { user.Username, user.DisplayName, user.Role, hasOtp = user.HasOtp };

        if (bearer == true && !LooksLikeBrowser(context.Request))
        {
            var bearerSession = await security.CreateSessionAsync(user.Id, BearerSessionLifetime, true, cancellationToken);
            return new SignInResponse(bearerSession.Token, bearerSession.ExpiresAt, usedRecoveryCode, account);
        }

        var session = await security.CreateSessionAsync(user.Id, SessionLifetime, false, cancellationToken);
        SessionCookieService.Issue(context, session);
        return new SignInResponse(null, session.ExpiresAt, usedRecoveryCode, account);
    }

    /// <summary>The signals a browser page always sends and a CLI never does.</summary>
    public static bool LooksLikeBrowser(HttpRequest request)
    {
        return request.Headers.ContainsKey("Origin") ||
               request.Headers.ContainsKey(SessionCookieService.CsrfHeaderName) ||
               request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The sign-in body; <see cref="Token" /> is left out of the JSON entirely for browser sign-ins.</summary>
    private sealed record SignInResponse(
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        string? Token,
        DateTime ExpiresAt,
        bool UsedRecoveryCode,
        object User);
}

public sealed record CredentialsRequest(
    string? Username,
    string? DisplayName,
    string? Password,
    string? Role,

    /// <summary>Setup only: the first administrator's answer to the isolation explanation; null leaves the setting alone.</summary>
    bool? AllowUnisolatedBuilds = null);

public sealed record PasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record OtpRequest(string? Challenge, string? Code);

/// <summary>Finishing enrollment needs the authenticator code and, like turning it off, the account password.</summary>
public sealed record OtpEnableRequest(string? Code, string? CurrentPassword);