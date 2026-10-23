using System.Net.Sockets;
using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Walis.Core.Models;
using Walis.Core.Repositories;
using Walis.Core.Security;

namespace Walis.Api.Services;

public sealed class NotificationDispatcher(
    INotificationRepository notifications,
    ISettingsRepository settings,
    IHttpClientFactory httpClientFactory,
    ILogger<NotificationDispatcher> logger,
    IConfiguration? appConfiguration = null)
{
    public const int MaximumChannels = 100;
    private const int MaximumPayloadBytes = 256 * 1024;
    private const int MaximumDetailCharacters = 1000;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task DispatchBuildCompletedAsync(
        Job job,
        Build build,
        string? previousStatus,
        bool timedOut,
        CancellationToken cancellationToken)
    {
        var eventKind = (build.Status, previousStatus, timedOut) switch
        {
            (_, _, true) => NotificationEventKind.BuildTimedOut,
            (BuildStatus.Failed, _, _) => NotificationEventKind.BuildFailed,
            (BuildStatus.Succeeded, BuildStatus.Failed, _) => NotificationEventKind.BuildRecovered,
            (BuildStatus.Succeeded, _, _) => NotificationEventKind.BuildSucceeded,
            _ => null
        };

        if (eventKind is null) return;

        var configuration = await settings.GetAsync(cancellationToken);

        if (!job.NotifyOnFailure &&
            eventKind is NotificationEventKind.BuildFailed or NotificationEventKind.BuildTimedOut) return;

        var title = eventKind switch
        {
            NotificationEventKind.BuildFailed => $"建置失敗：{job.Name} #{build.Number}",
            NotificationEventKind.BuildTimedOut => $"建置逾時：{job.Name} #{build.Number}",
            NotificationEventKind.BuildRecovered => $"建置恢復：{job.Name} #{build.Number}",
            _ => $"建置成功：{job.Name} #{build.Number}"
        };

        var body = new StringBuilder()
            .AppendLine($"任務：{job.Name}")
            .AppendLine($"專案：{job.Project ?? "—"}")
            .AppendLine($"分支：{job.Branch}")
            .AppendLine($"執行器：{build.RunnerName ?? "—"}")
            .AppendLine($"觸發：{build.TriggeredBy}")
            .AppendLine($"耗時：{FormatDuration(build.DurationSeconds)}")
            .AppendLine($"結束碼：{build.ExitCode?.ToString() ?? "—"}")
            .ToString();

        var url = string.IsNullOrWhiteSpace(configuration.BaseUrl)
            ? null
            : $"{configuration.BaseUrl.TrimEnd('/')}/#/builds/{build.Id}";

        await DispatchAsync(new NotificationMessage(eventKind, title, body, url)
        {
            // A build finishes once, so the event kind plus the build id is unique and stays the same if it is sent again.
            EventId = $"{eventKind}:{build.Id}",
            Build = build
        }, cancellationToken);
    }

    public async Task DispatchAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        var configuration = await settings.GetAsync(cancellationToken);

        if (!configuration.IsEventEnabled(message.Event)) return;

        var zone = TimeZoneResolver.Resolve(configuration.TimeZone);
        var localNow = TimeOnly.FromDateTime(TimeZoneResolver.ToLocal(DateTime.UtcNow, zone));
        // Security alerts are the one thing the quiet hours must not swallow: a lockout at 3 a.m. is exactly the one to see.
        var urgent = message.Event is NotificationEventKind.BuildFailed or NotificationEventKind.BuildTimedOut
            or NotificationEventKind.SecurityAlert;

        if (!urgent && configuration.IsQuietHour(localNow)) return;

        var channels = await notifications.ListEnabledChannelsAsync(cancellationToken);
        if (channels.Count > MaximumChannels)
            logger.LogWarning(
                "啟用的通知管道有 {Count} 個，只處理前 {Maximum} 個以避免單一事件耗盡資源。",
                channels.Count,
                MaximumChannels);

        foreach (var channel in channels.Take(MaximumChannels))
            await SendAsync(channel, message, configuration, cancellationToken);
    }

    public async Task<NotificationLogEntry> SendTestAsync(NotificationChannel channel,
        CancellationToken cancellationToken)
    {
        var configuration = await settings.GetAsync(cancellationToken);
        var message = new NotificationMessage(
            "test",
            $"{configuration.SiteName} 測試通知",
            "這是一封由 Walis 發出的測試通知，收到代表管道設定正確。",
            configuration.BaseUrl);

        return await SendAsync(channel, message, configuration, cancellationToken);
    }

    private async Task<NotificationLogEntry> SendAsync(
        NotificationChannel channel,
        NotificationMessage message,
        WalisSettings configuration,
        CancellationToken cancellationToken)
    {
        var succeeded = false;
        string? detail = null;

        try
        {
            (succeeded, detail) = channel.Kind switch
            {
                NotificationChannelKind.Slack => await SendSlackAsync(channel, message, cancellationToken),
                NotificationChannelKind.Webhook => await SendWebhookAsync(channel, message, configuration,
                    cancellationToken),
                NotificationChannelKind.Email => await SendEmailAsync(channel, message, configuration,
                    appConfiguration is not null && OutboundHttpHandlers.AllowsPrivateNetwork(appConfiguration),
                    cancellationToken),
                _ => (false, $"不支援的通知管道：{channel.Kind}")
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            detail = SanitizeDetail(exception.Message, configuration.SmtpPassword,
                configuration.NotificationSigningSecret, channel.Target);
            logger.LogWarning("發送 {Kind} 通知失敗：{Error}", channel.Kind, detail);
        }

        var entry = new NotificationLogEntry
        {
            Id = Guid.NewGuid(),
            SentAt = DateTime.UtcNow,
            Event = message.Event,
            ChannelKind = channel.Kind,
            Target = PresentTarget(channel),
            Succeeded = succeeded,
            Detail = SanitizeDetail(detail, configuration.SmtpPassword, configuration.NotificationSigningSecret,
                channel.Target)
        };

        await notifications.AppendLogAsync(entry, cancellationToken);
        return entry;
    }

    private async Task<(bool Succeeded, string? Detail)> SendSlackAsync(
        NotificationChannel channel,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            text = $"*{message.Title}*\n```{message.Body}```{(message.Url is null ? string.Empty : $"\n{message.Url}")}"
        };
        return await PostJsonAsync(channel.Target, payload, cancellationToken);
    }

    private async Task<(bool Succeeded, string? Detail)> SendWebhookAsync(
        NotificationChannel channel,
        NotificationMessage message,
        WalisSettings configuration,
        CancellationToken cancellationToken)
    {
        var payload = new WebhookPayload(
            message.Event,
            message.EventId,
            message.Title,
            message.Body,
            message.Url,
            DateTime.UtcNow,
            message.Build is null ? null : WebhookBuild.From(message.Build));

        return await PostJsonAsync(
            channel.Target,
            payload,
            cancellationToken,
            string.IsNullOrEmpty(configuration.NotificationSigningSecret)
                ? null
                : (configuration.NotificationSigningSecret, message.EventId));
    }

    /// <param name="signing">When given, the exact bytes sent are signed with the secret and the event id goes into a header.</param>
    private async Task<(bool Succeeded, string? Detail)> PostJsonAsync(
        string target,
        object payload,
        CancellationToken cancellationToken,
        (string Secret, string EventId)? signing = null)
    {
        if (target.Length > 2048 ||
            !Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return (false, "目標必須是不帶帳密的 HTTP 或 HTTPS 網址。");

        var json = JsonSerializer.Serialize(payload, SerializerOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes) return (false, "通知內容超過 256 KiB 上限。");

        using var client = httpClientFactory.CreateClient("notifications");
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

        if (signing is { } sign)
        {
            request.Headers.TryAddWithoutValidation(NotificationSignature.SignatureHeader,
                NotificationSignature.Create(sign.Secret, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), json));
            request.Headers.TryAddWithoutValidation(NotificationSignature.EventIdHeader, sign.EventId);
        }

        // We only need the status. ResponseContentRead would buffer an arbitrary endpoint's entire
        // response body and let it exhaust process memory.
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        return response.IsSuccessStatusCode
            ? (true, $"HTTP {(int)response.StatusCode}")
            : (false, $"HTTP {(int)response.StatusCode}");
    }

    private static async Task<(bool Succeeded, string? Detail)> SendEmailAsync(
        NotificationChannel channel,
        NotificationMessage message,
        WalisSettings configuration,
        bool allowPrivateNetwork,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuration.SmtpHost) || string.IsNullOrWhiteSpace(configuration.SmtpFrom))
            return (false, "尚未設定 SMTP 主機或寄件人，未實際寄出。");

        // SmtpClient is not an HttpClient, so the guarded connect callback does not apply. Resolve here and refuse unless every
        // address is public: otherwise an administrator credential could use "send test" to probe internal hosts and ports
        // (and send SMTP AUTH there). SmtpClient resolves again, so a rebinding DNS answer is still possible; every address of the
        // first answer must be public, which keeps the common cases (literal IPs, localhost, internal names) out.
        if (!allowPrivateNetwork)
        {
            var host = configuration.SmtpHost.Trim().Trim('[', ']');
            IPAddress[] addresses;

            try
            {
                addresses = IPAddress.TryParse(host, out var literal)
                    ? [literal]
                    : await Dns.GetHostAddressesAsync(host, cancellationToken);
            }
            catch (SocketException exception)
            {
                return (false, $"無法解析 SMTP 主機：{exception.Message}");
            }

            if (addresses.Length == 0 || !addresses.All(OutboundHttpHandlers.IsPublicAddress))
                return (false, $"不允許連線到私有網段：{host}（需要時設定 Security:OutboundAllowPrivateNetwork=true）");
        }

        using var client = new SmtpClient(configuration.SmtpHost, configuration.SmtpPort)
        {
            EnableSsl = configuration.SmtpUseSsl,
            // Never offer the service account's ambient NTLM/Kerberos credentials to an
            // administrator-configured SMTP host.
            UseDefaultCredentials = false,
            Credentials = string.IsNullOrWhiteSpace(configuration.SmtpUser)
                ? null
                : new NetworkCredential(configuration.SmtpUser, configuration.SmtpPassword),
            Timeout = 10_000
        };

        using var mail = new MailMessage(configuration.SmtpFrom, channel.Target)
        {
            Subject = message.Title,
            Body = message.Url is null ? message.Body : $"{message.Body}\n{message.Url}"
        };

        await client.SendMailAsync(mail, cancellationToken);
        return (true, "已寄出");
    }

    private static string FormatDuration(double? seconds)
    {
        return seconds is null
            ? "—"
            : TimeSpan.FromSeconds(seconds.Value).ToString(@"hh\:mm\:ss");
    }

    private static string PresentTarget(NotificationChannel channel)
    {
        if (channel.Kind == NotificationChannelKind.Email ||
            !Uri.TryCreate(channel.Target, UriKind.Absolute, out var uri))
            return channel.Target;

        // Slack and generic webhook URLs commonly carry credentials in their path or query.
        return $"{uri.Scheme}://{uri.Authority}/***";
    }

    private static string? SanitizeDetail(string? detail, params string?[] secrets)
    {
        if (string.IsNullOrEmpty(detail)) return detail;

        var result = SecretRedactor.Redact(detail, secrets);

        result = new string(result.Select(character => char.IsControl(character) ? ' ' : character).ToArray());
        return result.Length <= MaximumDetailCharacters
            ? result
            : $"{result[..MaximumDetailCharacters]}…";
    }
}

/// <summary>
///     The webhook body. The first five fields are the original format and must stay as they are for existing receivers;
///     eventId and build were added later. Build is omitted for events that are not about a build.
/// </summary>
internal sealed record WebhookPayload(
    string Event,
    string EventId,
    string Title,
    string Body,
    string? Url,
    DateTime SentAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    WebhookBuild? Build);

internal sealed record WebhookBuild(
    Guid Id,
    Guid JobId,
    string JobName,
    int Number,
    string Status,
    string? ExternalRef,
    string? MatrixLabel,
    DateTime? StartedAt,
    DateTime? FinishedAt,
    int? ExitCode)
{
    public static WebhookBuild From(Build build)
    {
        return new WebhookBuild(build.Id, build.JobId, build.JobName, build.Number, build.Status, build.ExternalRef,
            build.MatrixLabel, build.StartedAt, build.FinishedAt, build.ExitCode);
    }
}

/// <summary>
///     Signing of outgoing webhook notifications: <c>Walis-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c> where
///     v1 = HMAC-SHA256(secret, "&lt;t&gt;.&lt;raw body&gt;"). Putting the timestamp under the MAC lets receivers reject old replays.
/// </summary>
public static class NotificationSignature
{
    public const string SignatureHeader = "Walis-Signature";
    public const string EventIdHeader = "Walis-Event-Id";

    public static string Create(string secret, long timestamp, string body)
    {
        return $"t={timestamp},v1={Compute(secret, timestamp, body)}";
    }

    public static string Compute(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}"));
        return Convert.ToHexStringLower(mac);
    }
}
