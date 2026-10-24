using Walis.Core.Models;

namespace Walis.Core.Security;

/// <summary>
///     The secret fields inside the settings JSON. They are encrypted one by one so the rest of the payload stays readable
///     (and diffable in a backup); the list here is the single place that says which fields count as secrets.
/// </summary>
public static class SettingsSecrets
{
    /// <summary>Encrypts every secret field in place and returns the same instance.</summary>
    public static WalisSettings Protect(WalisSettings settings, ISecretProtector protector)
    {
        settings.WebhookSecret = protector.Protect(settings.WebhookSecret, SecretPurposes.SettingsWebhookSecret);
        settings.NotificationSigningSecret = protector.Protect(settings.NotificationSigningSecret, SecretPurposes.SettingsNotificationSigningSecret);
        settings.SmtpPassword = protector.Protect(settings.SmtpPassword, SecretPurposes.SettingsSmtpPassword);
        settings.BackupPassphrase = protector.Protect(settings.BackupPassphrase, SecretPurposes.SettingsBackupPassphrase);
        settings.BackupPostgresPassword = protector.Protect(settings.BackupPostgresPassword, SecretPurposes.SettingsBackupPostgresPassword);

        foreach (var target in settings.BackupTargets ?? [])
        {
            target.SecretAccessKey = protector.Protect(target.SecretAccessKey, SecretPurposes.BackupTargetSecretAccessKey);
            target.ClientSecret = protector.Protect(target.ClientSecret, SecretPurposes.BackupTargetClientSecret);
            target.RefreshToken = protector.Protect(target.RefreshToken, SecretPurposes.BackupTargetRefreshToken);
        }

        return settings;
    }

    /// <summary>Decrypts every secret field in place; a field that cannot be decrypted becomes null (unset).</summary>
    public static WalisSettings Unprotect(WalisSettings settings, ISecretProtector protector)
    {
        settings.WebhookSecret = protector.Unprotect(settings.WebhookSecret, SecretPurposes.SettingsWebhookSecret);
        settings.NotificationSigningSecret = protector.Unprotect(settings.NotificationSigningSecret, SecretPurposes.SettingsNotificationSigningSecret);
        settings.SmtpPassword = protector.Unprotect(settings.SmtpPassword, SecretPurposes.SettingsSmtpPassword);
        settings.BackupPassphrase = protector.Unprotect(settings.BackupPassphrase, SecretPurposes.SettingsBackupPassphrase);
        settings.BackupPostgresPassword = protector.Unprotect(settings.BackupPostgresPassword, SecretPurposes.SettingsBackupPostgresPassword);

        foreach (var target in settings.BackupTargets ?? [])
        {
            target.SecretAccessKey = protector.Unprotect(target.SecretAccessKey, SecretPurposes.BackupTargetSecretAccessKey);
            target.ClientSecret = protector.Unprotect(target.ClientSecret, SecretPurposes.BackupTargetClientSecret);
            target.RefreshToken = protector.Unprotect(target.RefreshToken, SecretPurposes.BackupTargetRefreshToken);
        }

        return settings;
    }

    /// <summary>Names the fields whose stored value cannot be decrypted, for the startup check and the reset action.</summary>
    public static IReadOnlyList<string> FindUndecryptable(WalisSettings settings, ISecretProtector protector)
    {
        var failures = new List<string>();

        Check("webhookSecret", settings.WebhookSecret, SecretPurposes.SettingsWebhookSecret);
        Check("notificationSigningSecret", settings.NotificationSigningSecret, SecretPurposes.SettingsNotificationSigningSecret);
        Check("smtpPassword", settings.SmtpPassword, SecretPurposes.SettingsSmtpPassword);
        Check("backupPassphrase", settings.BackupPassphrase, SecretPurposes.SettingsBackupPassphrase);
        Check("backupPostgresPassword", settings.BackupPostgresPassword, SecretPurposes.SettingsBackupPostgresPassword);

        foreach (var target in settings.BackupTargets ?? [])
        {
            Check($"backupTargets[{target.Name}].secretAccessKey", target.SecretAccessKey, SecretPurposes.BackupTargetSecretAccessKey);
            Check($"backupTargets[{target.Name}].clientSecret", target.ClientSecret, SecretPurposes.BackupTargetClientSecret);
            Check($"backupTargets[{target.Name}].refreshToken", target.RefreshToken, SecretPurposes.BackupTargetRefreshToken);
        }

        return failures;

        void Check(string name, string? value, string purpose)
        {
            if (!protector.TryUnprotect(value, purpose, out _)) failures.Add(name);
        }
    }

    /// <summary>Clears every secret field that cannot be decrypted, leaving the readable ones (still ciphertext) untouched.</summary>
    public static int ClearUndecryptable(WalisSettings settings, ISecretProtector protector)
    {
        var cleared = 0;

        settings.WebhookSecret = Clear(settings.WebhookSecret, SecretPurposes.SettingsWebhookSecret);
        settings.NotificationSigningSecret = Clear(settings.NotificationSigningSecret, SecretPurposes.SettingsNotificationSigningSecret);
        settings.SmtpPassword = Clear(settings.SmtpPassword, SecretPurposes.SettingsSmtpPassword);
        settings.BackupPassphrase = Clear(settings.BackupPassphrase, SecretPurposes.SettingsBackupPassphrase);
        settings.BackupPostgresPassword = Clear(settings.BackupPostgresPassword, SecretPurposes.SettingsBackupPostgresPassword);

        foreach (var target in settings.BackupTargets ?? [])
        {
            target.SecretAccessKey = Clear(target.SecretAccessKey, SecretPurposes.BackupTargetSecretAccessKey);
            target.ClientSecret = Clear(target.ClientSecret, SecretPurposes.BackupTargetClientSecret);
            target.RefreshToken = Clear(target.RefreshToken, SecretPurposes.BackupTargetRefreshToken);
        }

        return cleared;

        string? Clear(string? value, string purpose)
        {
            if (protector.TryUnprotect(value, purpose, out _)) return value;

            cleared++;
            return null;
        }
    }
}
