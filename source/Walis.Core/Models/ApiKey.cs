namespace Walis.Core.Models;

public static class ApiKeyScope
{
    /// <summary>May use the general API and MCP according to its role; should only be issued to programs that clearly need to automate management.</summary>
    public const string Api = "api";

    /// <summary>May only call the MCP transport and the exact REST allowlist the stdio bridge needs. The safe default for ordinary AI-agent keys.</summary>
    public const string Mcp = "mcp";

    /// <summary>May only use the claim and report endpoints of the bound runner.</summary>
    public const string Runner = "runner";

    public static bool IsValid(string? value)
    {
        return value is Api or Mcp or Runner;
    }

    public static string Normalize(string? value, string? runnerName)
    {
        return !string.IsNullOrWhiteSpace(runnerName)
            ? Runner
            : value == Api
                ? Api
                : Mcp;
    }
}

/// <summary>
///     A long-lived token for programs and AI agents. Separate from user accounts: machines cannot do one-time passwords
///     and should not share someone's password; a key can be disabled on its own and shows its last use.
/// </summary>
public sealed record ApiKey
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;

    /// <summary>The token prefix, used for lookup; not a secret.</summary>
    public string Prefix { get; init; } = string.Empty;

    public string Role { get; init; } = UserRole.Operator;
    public string Scope { get; init; } = ApiKeyScope.Api;
    public bool IsDisabled { get; init; }
    public DateTime CreatedAt { get; init; }
    public string? CreatedBy { get; init; }
    public DateTime? ExpiresAt { get; init; }
    public DateTime? LastUsedAt { get; init; }

    /// <summary>
    ///     The bound runner name. When set, this key may only do what a runner does (claim, heartbeat, report),
    ///     and only under this name, so a leaked key cannot reach other runners' jobs and secrets.
    /// </summary>
    public string? RunnerName { get; init; }

    /// <summary>
    ///     Source addresses this key may be presented from, one address or CIDR range per line; null or empty means any.
    ///     Checked for every audience, so a runner key can be pinned to the runner's address.
    /// </summary>
    public string? AllowedAddresses { get; init; }

    public bool IsRunnerScoped => !string.IsNullOrWhiteSpace(RunnerName);

    public bool IsExpired => ExpiresAt is not null && ExpiresAt <= DateTime.UtcNow;
}

public sealed record ApiKeyDraft(
    string Name,
    string Role,
    DateTime? ExpiresAt,
    string? RunnerName = null,
    string? Scope = null,
    string? AllowedAddresses = null);

/// <summary>The response when a key is created; the plaintext appears this once only.</summary>
public sealed record ApiKeyIssued(ApiKey Key, string Token);