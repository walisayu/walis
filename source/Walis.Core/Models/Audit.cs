using System.Globalization;

namespace Walis.Core.Models;

public static class AuditAction
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Delete = "delete";
    public const string Run = "run";
    public const string Cancel = "cancel";
    public const string Download = "download";
    public const string Export = "export";
    public const string Reveal = "reveal";
    public const string SignIn = "sign-in";
    public const string SignOut = "sign-out";
    public const string SignInFailed = "sign-in-failed";
    public const string LockedOut = "locked-out";

    /// <summary>A request refused by the authentication middleware, a job-level permission check or the webhook guard.</summary>
    public const string AccessDenied = "access-denied";

    /// <summary>An administrator account was created or an account was promoted; written even while auditing is off.</summary>
    public const string AdminGranted = "admin-granted";
}

/// <summary>How the recorded request ended; before this column existed only the successful path was ever recorded.</summary>
public static class AuditOutcome
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failed = "failed";
}

public sealed class AuditEntry
{
    public Guid Id { get; init; }
    public DateTime OccurredAt { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string? EntityId { get; init; }
    public string Summary { get; init; } = string.Empty;

    /// <summary>The client address as the server saw it (after the trusted forwarded headers); null for background work.</summary>
    public string? RemoteAddress { get; init; }

    public string Outcome { get; init; } = AuditOutcome.Success;

    /// <summary>Kestrel's request identifier, so an audit row can be matched with the server log line.</summary>
    public string? RequestId { get; init; }

    /// <summary>The job a build event belongs to, so a job's whole history can be queried without parsing the summary.</summary>
    public Guid? JobId { get; init; }
}

/// <summary>Filters for the paged audit query; every field is optional and the time range is half-open ([From, To)).</summary>
public sealed record AuditQuery(
    string? Actor = null,
    string? EntityType = null,
    string? EntityId = null,
    DateTime? From = null,
    DateTime? To = null,
    int Page = 1,
    int PageSize = AuditQuery.DefaultPageSize,
    string? Action = null,
    string? Outcome = null)
{
    public const int DefaultPageSize = 50;
    public const int MaximumPageSize = 200;
}

public sealed record AuditPage(IReadOnlyList<AuditEntry> Items, int Total, int Page, int PageSize);

public sealed record SecurityAuditMetrics(
    int SignInFailures,
    int Lockouts,
    int Denied,
    int SecretReveals);

public sealed record SecurityAuditSummary(
    SecurityAuditMetrics Metrics24h,
    IReadOnlyList<AuditEntry> RecentEvents);

/// <summary>
///     Cleans text before it is written to the audit table or a log line: a job name containing a newline or an escape sequence
///     must not forge a second record or clear the operator's terminal. Tabs are kept, every other control character becomes a space.
///     Unicode format characters (bidirectional overrides such as U+202E, isolates, zero-width marks) and the line/paragraph
///     separators are treated the same: they can reverse how a summary reads in the interface or a spreadsheet.
/// </summary>
public static class AuditText
{
    public const int MaximumSummaryLength = 2000;
    public const int MaximumActorLength = 200;
    public const int MaximumEntityIdLength = 120;
    public const int MaximumAddressLength = 64;
    public const int MaximumRequestIdLength = 64;

    /// <summary>
    ///     Whether a character must not reach a log line or an audit cell: C0/C1 controls (tab included), Unicode format
    ///     characters and the line/paragraph separators.
    /// </summary>
    public static bool IsDisallowed(char character)
    {
        if (char.IsControl(character)) return true;

        return CharUnicodeInfo.GetUnicodeCategory(character) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator
            or UnicodeCategory.ParagraphSeparator;
    }

    public static string Clean(string? value, int maximumLength)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var cleaned = value.Length > maximumLength ? value[..maximumLength] : value;

        if (!cleaned.Any(character => IsDisallowed(character) && character != '\t')) return cleaned;

        var buffer = cleaned.ToCharArray();
        for (var index = 0; index < buffer.Length; index++)
            if (IsDisallowed(buffer[index]) && buffer[index] != '\t')
                buffer[index] = ' ';

        return new string(buffer);
    }

    public static string? CleanOrNull(string? value, int maximumLength)
    {
        return string.IsNullOrEmpty(value) ? null : Clean(value, maximumLength);
    }

    /// <summary>
    ///     Whether a name contains a control character (newline, escape, null and the like) or a Unicode format/separator
    ///     character; such names are rejected on input.
    /// </summary>
    public static bool HasControlCharacters(string? value)
    {
        return value is not null && value.Any(IsDisallowed);
    }
}