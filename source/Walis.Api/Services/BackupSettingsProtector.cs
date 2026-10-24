using System.Text.Json;
using Walis.Core.Backup;
using Walis.Core.Models;

namespace Walis.Api.Services;

/// <summary>
///     Secrets in the system settings (cloud keys and passphrases of backup destinations, SMTP passwords, webhook and notification signing secrets)
///     are always masked in API responses: settings are readable by everyone who signs in (the UI needs the time zone and site name),
///     so passwords must not go out with them. When the frontend sends the mask string back unchanged it means "not modified", and the stored value is kept.
/// </summary>
public static class BackupSettingsProtector
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static WalisSettings Mask(WalisSettings settings)
    {
        var copy = Clone(settings);

        copy.BackupPassphrase = MaskValue(copy.BackupPassphrase);
        copy.BackupPostgresPassword = MaskValue(copy.BackupPostgresPassword);
        copy.SmtpPassword = MaskValue(copy.SmtpPassword);
        copy.WebhookSecret = MaskValue(copy.WebhookSecret);
        copy.NotificationSigningSecret = MaskValue(copy.NotificationSigningSecret);
        copy.BackupTargets = [.. (copy.BackupTargets ?? []).Select(target => target.Masked())];

        return copy;
    }

    public static WalisSettings Merge(WalisSettings incoming, WalisSettings stored)
    {
        incoming.BackupTargets ??= [];
        stored.BackupTargets ??= [];
        incoming.BackupPassphrase = Restore(incoming.BackupPassphrase, stored.BackupPassphrase);
        incoming.BackupPostgresPassword = Restore(incoming.BackupPostgresPassword, stored.BackupPostgresPassword);
        incoming.SmtpPassword = Restore(incoming.SmtpPassword, stored.SmtpPassword);
        incoming.WebhookSecret = Restore(incoming.WebhookSecret, stored.WebhookSecret);
        incoming.NotificationSigningSecret =
            Restore(incoming.NotificationSigningSecret, stored.NotificationSigningSecret);

        foreach (var target in incoming.BackupTargets)
        {
            var previous = stored.BackupTargets.FirstOrDefault(item => item.Id == target.Id);

            if (previous is null) continue;

            target.SecretAccessKey = Restore(target.SecretAccessKey, previous.SecretAccessKey);
            target.ClientSecret = Restore(target.ClientSecret, previous.ClientSecret);
            target.RefreshToken = Restore(target.RefreshToken, previous.RefreshToken);
        }

        return incoming;
    }

    private static WalisSettings Clone(WalisSettings settings)
    {
        return JsonSerializer.Deserialize<WalisSettings>(JsonSerializer.Serialize(settings, Options), Options)!;
    }

    private static string? MaskValue(string? value)
    {
        return string.IsNullOrEmpty(value) ? value : BackupTarget.MaskedValue;
    }

    private static string? Restore(string? incoming, string? stored)
    {
        return incoming == BackupTarget.MaskedValue ? stored : incoming;
    }
}