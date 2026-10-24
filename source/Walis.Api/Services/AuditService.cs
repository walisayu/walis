using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Services;

/// <summary>
///     Who did it and from where, captured from the request. Background work that outlives the request (a manual backup in
///     <c>Task.Run</c>) takes a snapshot first, because <see cref="IHttpContextAccessor" /> is empty once the response has gone out.
/// </summary>
public sealed record AuditContext(string Actor, string? RemoteAddress, string? RequestId)
{
    public static readonly AuditContext System = new("system", null, null);
}

/// <summary>One record to write; the simple <see cref="AuditService.RecordAsync(string,string,string?,string,CancellationToken)" /> overload builds it.</summary>
public sealed record AuditEvent(string Action, string EntityType, string? EntityId, string Summary)
{
    public string Outcome { get; init; } = AuditOutcome.Success;

    /// <summary>Overrides the actor read from the request; sign-in records the account being signed in, not "system".</summary>
    public string? Actor { get; init; }

    public Guid? JobId { get; init; }

    /// <summary>A snapshot taken before the request ended; null reads the current request.</summary>
    public AuditContext? Context { get; init; }

    /// <summary>Written even while the audit setting is off; used for the record that turns auditing on or off.</summary>
    public bool Force { get; init; }
}

public sealed class AuditService(
    IAuditRepository audit,
    ISettingsRepository settings,
    IHttpContextAccessor accessor,
    ILogger<AuditService> logger)
{
    /// <summary>The current actor; recorded as system when sign-in is disabled.</summary>
    public const string ActorKey = "walis-actor";

    /// <summary>A record must never hang a request: the write gets this long, then it is logged as lost.</summary>
    public static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    public string Actor => accessor.HttpContext is { } context
        ? context.Items[ActorKey] as string ?? RequestAccess.Session(context)?.Username ?? "system"
        : "system";

    /// <summary>The client address after the trusted forwarded headers were applied; null when there is no request.</summary>
    public string? RemoteAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? RequestId => accessor.HttpContext?.TraceIdentifier;

    /// <summary>Snapshot of the request identity for work that continues after the response.</summary>
    public AuditContext Capture()
    {
        return new AuditContext(Actor, RemoteAddress, RequestId);
    }

    public Task RecordAsync(
        string action,
        string entityType,
        string? entityId,
        string summary,
        CancellationToken cancellationToken = default)
    {
        return RecordAsync(new AuditEvent(action, entityType, entityId, summary), cancellationToken);
    }

    /// <summary>
    ///     Writes the record. The caller's token is deliberately ignored: a client that disconnects right after the change was
    ///     saved must not take the audit row with it. A failure is logged as an error and never fails the request, since the change
    ///     itself has already happened.
    /// </summary>
    public async Task RecordAsync(AuditEvent @event, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        // The request identity is read before any await: once the response has gone out the HttpContext may be gone.
        var context = @event.Context ?? Capture();

        using var timeout = new CancellationTokenSource(WriteTimeout);

        try
        {
            if (!@event.Force)
            {
                var configuration = await settings.GetAsync(timeout.Token);

                if (!configuration.AuditLog) return;
            }

            await audit.AppendAsync(
                new AuditEntry
                {
                    Actor = AuditText.Clean(@event.Actor ?? context.Actor, AuditText.MaximumActorLength),
                    Action = @event.Action,
                    EntityType = @event.EntityType,
                    EntityId = AuditText.CleanOrNull(@event.EntityId, AuditText.MaximumEntityIdLength),
                    Summary = AuditText.Clean(@event.Summary, AuditText.MaximumSummaryLength),
                    RemoteAddress = AuditText.CleanOrNull(context.RemoteAddress, AuditText.MaximumAddressLength),
                    Outcome = @event.Outcome,
                    RequestId = AuditText.CleanOrNull(context.RequestId, AuditText.MaximumRequestIdLength),
                    JobId = @event.JobId
                },
                timeout.Token);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "稽核紀錄寫入失敗：{Action} {EntityType} {EntityId}（操作者 {Actor}）",
                @event.Action,
                @event.EntityType,
                AuditText.Clean(@event.EntityId, AuditText.MaximumEntityIdLength),
                AuditText.Clean(@event.Actor ?? context.Actor, AuditText.MaximumActorLength));
        }
    }
}