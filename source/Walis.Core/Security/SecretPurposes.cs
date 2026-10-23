namespace Walis.Core.Security;

/// <summary>
///     Where a stored secret lives, as <c>&lt;table&gt;.&lt;column&gt;</c>. The purpose is bound into the ciphertext as AES-GCM
///     associated data (<c>walis:&lt;purpose&gt;</c>), so a ciphertext copied from one column into another no longer decrypts:
///     without it, anyone with database write access could move an AI key into a secret environment variable of a job they can
///     run and have a build print it. Every repository names its column here; the scanner and the reset use the same names.
/// </summary>
public static class SecretPurposes
{
    public const string AiProviderApiKey = "AiProviders.ApiKey";
    public const string EnvironmentVariableValue = "EnvironmentVariables.Value";
    public const string BuildSecretParameters = "Builds.SecretParametersJson";
    public const string UserTotpSecret = "Users.TotpSecret";
    public const string NotificationChannelTarget = "NotificationChannels.Target";
    public const string SettingsWebhookSecret = "Settings.WebhookSecret";
    public const string SettingsNotificationSigningSecret = "Settings.NotificationSigningSecret";
    public const string SettingsSmtpPassword = "Settings.SmtpPassword";
    public const string SettingsBackupPassphrase = "Settings.BackupPassphrase";
    public const string SettingsBackupPostgresPassword = "Settings.BackupPostgresPassword";
    public const string BackupTargetSecretAccessKey = "Settings.BackupTargets.SecretAccessKey";
    public const string BackupTargetClientSecret = "Settings.BackupTargets.ClientSecret";
    public const string BackupTargetRefreshToken = "Settings.BackupTargets.RefreshToken";

    /// <summary>The associated-data bytes for a purpose; the fixed prefix keeps it apart from any other AAD Walis might use.</summary>
    public static byte[] AssociatedData(string purpose)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Any(char.IsWhiteSpace))
            throw new ArgumentException("機密用途必須是 <資料表>.<欄位> 形式，不可空白。", nameof(purpose));

        return System.Text.Encoding.UTF8.GetBytes("walis:" + purpose);
    }
}
