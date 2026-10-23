namespace Walis.Core.Models;

public static class UserRole
{
    /// <summary>Can manage accounts, settings and every resource.</summary>
    public const string Admin = "admin";

    /// <summary>Can manage jobs, run builds and environment variables, but cannot change system settings or accounts.</summary>
    public const string Operator = "operator";

    /// <summary>Read-only access.</summary>
    public const string Viewer = "viewer";

    public static bool IsValid(string? value)
    {
        return value is Admin or Operator or Viewer;
    }

    public static int Rank(string role)
    {
        return role switch
        {
            Admin => 3,
            Operator => 2,
            Viewer => 1,
            _ => 0
        };
    }
}

public sealed class WalisUser
{
    public Guid Id { get; init; }
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string PasswordHash { get; init; } = string.Empty;
    public string Role { get; init; } = UserRole.Admin;
    public bool IsDisabled { get; init; }
    public string? TotpSecret { get; init; }
    public DateTime? TotpEnabledAt { get; init; }
    public long? LastTotpCounter { get; init; }
    public string? RecoveryCodes { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime? LastLoginAt { get; init; }

    public bool HasOtp => !string.IsNullOrWhiteSpace(TotpSecret) && TotpEnabledAt is not null;
}

public sealed record UserDraft(string Username, string DisplayName, string Password, string Role);

public sealed record UserProfileDraft(string DisplayName, string Role, bool IsDisabled);

public sealed class AuthSession
{
    public string Token { get; init; } = string.Empty;
    public Guid UserId { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }
    public string Username { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Role { get; init; } = UserRole.Admin;

    /// <summary>Whether the user has enabled one-time passwords. Returned by the session query to avoid an extra lookup per request.</summary>
    public bool HasOtp { get; init; }

    /// <summary>
    ///     A session issued to a command-line client (<c>?bearer=true</c> at sign-in): shorter-lived, presented only in the
    ///     Authorization header, never as the browser cookie. Browser sessions are the opposite, so a token that leaks from one
    ///     channel is useless on the other.
    /// </summary>
    public bool IsBearer { get; init; }
}