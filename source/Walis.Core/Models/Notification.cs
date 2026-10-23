namespace Walis.Core.Models;

public static class NotificationChannelKind
{
    public const string Email = "email";
    public const string Slack = "slack";
    public const string Webhook = "webhook";

    public static bool IsValid(string? value)
    {
        return value is Email or Slack or Webhook;
    }
}

public static class NotificationEventKind
{
    public const string BuildFailed = "build.failed";
    public const string BuildRecovered = "build.recovered";
    public const string BuildSucceeded = "build.succeeded";
    public const string BuildTimedOut = "build.timeout";
    public const string RunnerOffline = "runner.offline";

    /// <summary>Lockouts, auditing switched off, administrator accounts created or promoted.</summary>
    public const string SecurityAlert = "security.alert";
}

public sealed class NotificationChannel
{
    public Guid Id { get; init; }
    public string Kind { get; init; } = NotificationChannelKind.Webhook;
    public string Name { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public bool IsEnabled { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed record NotificationChannelDraft(string Kind, string Name, string Target, bool IsEnabled);

public sealed class NotificationLogEntry
{
    public Guid Id { get; init; }
    public DateTime SentAt { get; init; }
    public string Event { get; init; } = string.Empty;
    public string ChannelKind { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public bool Succeeded { get; init; }
    public string? Detail { get; init; }
}

public sealed record NotificationMessage(string Event, string Title, string Body, string? Url)
{
    /// <summary>
    ///     Unique per event and identical for every channel and every resend of it, so receivers can drop duplicates.
    ///     Build events use "&lt;event&gt;:&lt;buildId&gt;"; everything else gets a fresh Guid.
    /// </summary>
    public string EventId { get; init; } = Guid.NewGuid().ToString();

    /// <summary>The build the event is about; webhook channels send it as a structured object, Slack and email ignore it.</summary>
    public Build? Build { get; init; }
}