using Walis.Core.Models;

namespace Walis.Api.Services;

/// <summary>
///     Reads "who is this" from HttpContext. The authentication middleware puts the session and key into Items,
///     and endpoints access them through here; requests that did not authenticate get viewer permission only.
/// </summary>
public static class RequestAccess
{
    public const string SessionKey = "walis-session";
    public const string ApiKeyKey = "walis-api-key";

    public static AuthSession? Session(HttpContext context)
    {
        return context.Items[SessionKey] as AuthSession;
    }

    public static ApiKey? ApiKey(HttpContext context)
    {
        return context.Items[ApiKeyKey] as ApiKey;
    }

    /// <summary>
    ///     Without a session (the compatibility mode that allows anonymous reads) use viewer; when an authenticated identity's database role is
    ///     corrupt, keep it as an invalid empty value so JobAccess explicitly denies, never downgrade an unknown identity to a viewer that can still read data.
    /// </summary>
    public static string Role(HttpContext context)
    {
        var session = Session(context);
        if (session is null) return UserRole.Viewer;

        return UserRole.IsValid(session.Role) ? session.Role : string.Empty;
    }

    public static bool IsAdmin(HttpContext context)
    {
        return Role(context) == UserRole.Admin;
    }

    /// <summary>The key bound to a runner; null for an ordinary session or an unbound key.</summary>
    public static ApiKey? RunnerKey(HttpContext context)
    {
        return ApiKey(context) is { IsRunnerScoped: true } key ? key : null;
    }

    /// <summary>
    ///     Whether the caller may act as a given runner (claim, heartbeat, report).
    ///     The key bound to that runner may; an authenticated admin may; nobody else.
    ///     Claiming returns all of the job's environment variables including secrets, so ordinary operators must not touch it.
    /// </summary>
    public static bool CanActAsRunner(HttpContext context, string? runnerName)
    {
        if (RunnerKey(context) is { } key)
            return string.Equals(key.RunnerName, runnerName, StringComparison.OrdinalIgnoreCase);

        return IsAdmin(context);
    }
}