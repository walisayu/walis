namespace Walis.Core.Models;

/// <summary>
///     Job-level permissions. The site-wide role decides "may I touch this kind of thing";
///     this decides "may I touch this particular job", e.g. restricting the production deployment job so only admins can run it.
///     Without a valid role the lowest permission always applies.
/// </summary>
public static class JobAccess
{
    public static int Rank(string? role)
    {
        return role switch
        {
            UserRole.Admin => 2,
            UserRole.Operator => 1,
            UserRole.Viewer => 0,
            _ => 0
        };
    }

    public static bool CanView(string? role, Job job)
    {
        return UserRole.IsValid(role) && UserRole.IsValid(job.ViewRole) && Rank(role) >= Rank(job.ViewRole);
    }

    /// <summary>
    ///     Running is always at least an operator action: a viewer is read-only site-wide, so a stored RunRole of viewer
    ///     (from before the floor existed) is read as operator rather than letting a read-only key trigger builds through MCP.
    /// </summary>
    public static bool CanRun(string? role, Job job)
    {
        return UserRole.IsValid(role) && UserRole.IsValid(job.RunRole) &&
               Rank(role) >= Rank(NormalizeRunRole(job.RunRole));
    }

    /// <summary>
    ///     A trusted script may be runnable by operators, but only an administrator may change its code or environment.
    ///     Environment variables such as PATH and BASH_ENV can change what executes outside the sandbox.
    /// </summary>
    public static bool CanModify(string? role, Job job)
    {
        return CanRun(role, job) && (!job.TrustedUnsandboxed || role == UserRole.Admin);
    }

    /// <summary>Folds invalid values back to the defaults on save.</summary>
    public static string NormalizeViewRole(string? role)
    {
        return UserRole.IsValid(role) ? role! : UserRole.Viewer;
    }

    /// <summary>The run threshold has a floor of operator; viewer and invalid values both fold to operator.</summary>
    public static string NormalizeRunRole(string? role)
    {
        return UserRole.IsValid(role) && role != UserRole.Viewer ? role! : UserRole.Operator;
    }

    /// <summary>Whether a requested run role is acceptable on save: admin or operator only.</summary>
    public static bool IsValidRunRole(string? role)
    {
        return role is UserRole.Admin or UserRole.Operator;
    }
}
