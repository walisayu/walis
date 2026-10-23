using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Services;

/// <summary>
///     Step-up for the administrator actions whose effect outlives the session that performed them: creating an API key,
///     resetting another account's password or one-time password, granting the admin role, turning sign-in or auditing off, and
///     lifting a sign-in lockout. A hijacked browser session alone (a malicious extension, a machine left unlocked) must not be
///     enough for those; the account's current password is asked again.
/// </summary>
public interface IReauthentication
{
    /// <summary>Null when the request carries the caller's current password; otherwise the 403 (or 429) to return.</summary>
    Task<IResult?> RequireAsync(HttpContext context, CancellationToken cancellationToken);
}

public sealed class Reauthentication(ISecurityRepository security, SignInThrottle throttle) : IReauthentication
{
    /// <summary>The request header that carries the current password for the step-up.</summary>
    public const string HeaderName = "X-Walis-Reauth";

    /// <summary>The <c>code</c> of the 403 body, so the interface knows to ask for the password and retry.</summary>
    public const string Code = "reauth_required";

    public async Task<IResult?> RequireAsync(HttpContext context, CancellationToken cancellationToken)
    {
        // API keys never reach these endpoints (the middleware and the endpoints refuse them); a key has no password anyway.
        if (RequestAccess.Session(context) is not { } session) return Refuse("請用管理帳號登入後再執行這個操作。");

        var password = context.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(password)) return Refuse("這個操作需要再次輸入你的密碼。");

        if (password.Length > 256) return Refuse("密碼不正確。");

        var address = context.Connection.RemoteIpAddress?.ToString();

        // Counted like a sign-in: a stolen session cannot be used to guess the password through this header.
        if (await throttle.TryBeginAttemptAsync(session.Username, address, cancellationToken) is { } wait)
            return Results.Json(
                new { message = "嘗試次數過多，請稍後再試。", retryAfterSeconds = (int)Math.Ceiling(wait.TotalSeconds) },
                statusCode: StatusCodes.Status429TooManyRequests);

        var user = await security.GetUserAsync(session.UserId, cancellationToken);

        if (user is null || user.IsDisabled || !PasswordHasher.Verify(password, user.PasswordHash))
            return Refuse("密碼不正確。");

        await throttle.RecordSuccessAsync(session.Username, address, cancellationToken);
        return null;
    }

    public static IResult Refuse(string message)
    {
        return Results.Json(new { code = Code, message }, statusCode: StatusCodes.Status403Forbidden);
    }
}
