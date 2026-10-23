using System.Net.Mail;
using Microsoft.AspNetCore.Mvc;
using Walis.Api.Services;
using Walis.Core.Models;
using Walis.Core.Repositories;

namespace Walis.Api.Endpoints;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var notifications = app.MapGroup("/api/notifications").WithTags("Notifications");

        notifications.MapGet("/channels",
            async (INotificationRepository repository, CancellationToken cancellationToken) =>
                Results.Ok((await repository.ListChannelsAsync(cancellationToken)).Select(Present)));

        notifications.MapPost("/channels", async (
            NotificationChannelRequest request,
            INotificationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            if ((await repository.ListChannelsAsync(cancellationToken)).Count >= NotificationDispatcher.MaximumChannels)
                return Results.Conflict(new
                {
                    message = $"通知管道最多 {NotificationDispatcher.MaximumChannels} 個。"
                });

            var channel = await repository.CreateChannelAsync(request.ToDraft(), cancellationToken);

            // A channel decides where build summaries (and signed webhooks) are sent; the target itself is not copied into the trail.
            await audit.RecordAsync(AuditAction.Create, "channel", channel.Id.ToString(),
                $"新增通知管道 {channel.Name}（{channel.Kind}）", cancellationToken);

            return Results.Created($"/api/notifications/channels/{channel.Id}", Present(channel));
        }).WithMetadata(new RequestSizeLimitAttribute(32 * 1024));

        notifications.MapPut("/channels/{id:guid}", async (
            Guid id,
            NotificationChannelRequest request,
            INotificationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            // The list shows Slack and webhook URLs masked; sending that mask back means "keep the stored URL".
            var existing = await repository.GetChannelAsync(id, cancellationToken);
            if (existing is not null && request.Kind == existing.Kind &&
                NotificationRepository.IsSecretTarget(existing.Kind) &&
                string.Equals(request.Target?.Trim(), MaskTarget(existing.Target), StringComparison.Ordinal))
                request = request with { Target = existing.Target };

            var errors = Validate(request);
            if (errors.Count != 0) return Results.ValidationProblem(errors);

            var channel = await repository.UpdateChannelAsync(id, request.ToDraft(), cancellationToken);

            if (channel is not null)
                await audit.RecordAsync(AuditAction.Update, "channel", channel.Id.ToString(), $"更新通知管道 {channel.Name}",
                    cancellationToken);

            return channel is null ? Results.NotFound() : Results.Ok(Present(channel));
        }).WithMetadata(new RequestSizeLimitAttribute(32 * 1024));

        notifications.MapDelete("/channels/{id:guid}", async (
            Guid id,
            INotificationRepository repository,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            // Read first: the name is gone once the row is deleted, and a record saying only "deleted a channel" is useless.
            var channel = await repository.GetChannelAsync(id, cancellationToken);

            if (channel is null || !await repository.DeleteChannelAsync(id, cancellationToken)) return Results.NotFound();

            await audit.RecordAsync(AuditAction.Delete, "channel", id.ToString(),
                $"刪除通知管道 {channel.Name}（{channel.Kind}）", cancellationToken);

            return Results.NoContent();
        });

        notifications.MapPost("/channels/{id:guid}/test", async (
            Guid id,
            INotificationRepository repository,
            NotificationDispatcher dispatcher,
            AuditService audit,
            CancellationToken cancellationToken) =>
        {
            var channel = await repository.GetChannelAsync(id, cancellationToken);
            if (channel is null) return Results.NotFound();

            if (string.IsNullOrWhiteSpace(channel.Target)) return Results.Conflict(new { message = "請先填寫通知對象再發送測試。" });

            var sent = await dispatcher.SendTestAsync(channel, cancellationToken);

            await audit.RecordAsync(AuditAction.Run, "channel", channel.Id.ToString(),
                $"發送測試通知到管道 {channel.Name}（{channel.Kind}）", cancellationToken);

            return Results.Ok(sent);
        });

        notifications.MapGet("/log", async (
                int? limit,
                INotificationRepository repository,
                CancellationToken cancellationToken) =>
            Results.Ok(await repository.ListLogAsync(limit ?? 30, cancellationToken)));

        return app;
    }

    /// <summary>
    ///     Slack and webhook URLs are credentials: the API shows only <c>scheme://host/***</c>, like the delivery log does.
    ///     Email addresses are shown as they are.
    /// </summary>
    private static object Present(NotificationChannel channel)
    {
        var masked = NotificationRepository.IsSecretTarget(channel.Kind) && channel.Target.Length != 0;

        return new
        {
            channel.Id,
            channel.Kind,
            channel.Name,
            Target = masked ? MaskTarget(channel.Target) : channel.Target,
            TargetMasked = masked,
            channel.IsEnabled,
            channel.UpdatedAt
        };
    }

    public static string MaskTarget(string target)
    {
        return Uri.TryCreate(target, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Authority}/***" : "***";
    }

    private static Dictionary<string, string[]> Validate(NotificationChannelRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (!NotificationChannelKind.IsValid(request.Kind)) errors["kind"] = ["通知管道只能是 email、slack 或 webhook。"];

        if (string.IsNullOrWhiteSpace(request.Name))
            errors["name"] = ["請輸入管道名稱。"];
        else if (request.Name.Length > 200) errors["name"] = ["管道名稱不可超過 200 個字元。"];
        else if (AuditText.HasControlCharacters(request.Name)) errors["name"] = ["管道名稱不可包含控制字元。"];

        var target = request.Target?.Trim() ?? string.Empty;

        if (request.IsEnabled && target.Length == 0)
            errors["target"] = ["啟用前請先填寫通知對象。"];
        else if (target.Length > 2048)
            errors["target"] = ["通知對象過長。"];
        else if (target.Length != 0 && request.Kind != NotificationChannelKind.Email &&
                 (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
                  uri.Scheme is not ("http" or "https") ||
                  string.IsNullOrEmpty(uri.Host) ||
                  !string.IsNullOrEmpty(uri.UserInfo) ||
                  !string.IsNullOrEmpty(uri.Fragment)))
            errors["target"] = ["請輸入不帶帳密的 HTTP 或 HTTPS 完整網址。"];
        else if (target.Length != 0 && request.Kind == NotificationChannelKind.Email &&
                 (!MailAddress.TryCreate(target, out var address) ||
                  !string.Equals(address.Address, target, StringComparison.OrdinalIgnoreCase)))
            errors["target"] = ["請輸入有效的電子郵件位址。"];

        return errors;
    }
}

public sealed record NotificationChannelRequest(string? Kind, string? Name, string? Target, bool IsEnabled)
{
    public NotificationChannelDraft ToDraft()
    {
        return new NotificationChannelDraft(Kind!, Name!, Target ?? string.Empty, IsEnabled);
    }
}