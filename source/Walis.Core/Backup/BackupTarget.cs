namespace Walis.Core.Backup;

/// <summary>The kind of backup destination; each needs a different set of fields.</summary>
public static class BackupProviders
{
    /// <summary>An absolute path on a local or external disk; a mounted network drive counts too.</summary>
    public const string Local = "local";

    /// <summary>S3-compatible storage, covering AWS S3, Cloudflare R2 and MinIO. Only needs keys, no app registration.</summary>
    public const string S3 = "s3";

    /// <summary>The next three use OAuth and require creating an app in each provider's console and obtaining a refresh token.</summary>
    public const string Dropbox = "dropbox";

    public const string GoogleDrive = "google-drive";
    public const string OneDrive = "onedrive";

    public static bool IsValid(string? value)
    {
        return value is Local or S3 or Dropbox or GoogleDrive or OneDrive;
    }

    public static bool UsesOAuth(string? value)
    {
        return value is Dropbox or GoogleDrive or OneDrive;
    }
}

/// <summary>One backup destination. Secret fields are masked in API responses.</summary>
public sealed class BackupTarget
{
    /// <summary>A masked field sent back means "unchanged", so the original value is kept.</summary>
    public const string MaskedValue = "********";

    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Display name.</summary>
    public string Name { get; set; } = string.Empty;

    public string Provider { get; set; } = BackupProviders.Local;

    public bool IsDisabled { get; set; }

    /// <summary>
    ///     Local: an absolute path. S3: a prefix under the bucket. Dropbox/OneDrive: a folder path.
    ///     Google Drive: a folder ID (Drive has no real paths).
    /// </summary>
    public string Path { get; set; } = string.Empty;

    // S3-compatible
    public string? Bucket { get; set; }

    /// <summary>The region for AWS; R2 always uses auto.</summary>
    public string? Region { get; set; }

    /// <summary>The endpoint for R2 or self-hosted S3; left empty for AWS it is built automatically.</summary>
    public string? Endpoint { get; set; }

    public string? AccessKeyId { get; set; }

    public string? SecretAccessKey { get; set; }

    // OAuth
    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    public string? RefreshToken { get; set; }

    /// <summary>Replaces secret fields with the mask string for API responses.</summary>
    public BackupTarget Masked()
    {
        return new BackupTarget
        {
            Id = Id,
            Name = Name,
            Provider = Provider,
            IsDisabled = IsDisabled,
            Path = Path,
            Bucket = Bucket,
            Region = Region,
            Endpoint = Endpoint,
            AccessKeyId = AccessKeyId,
            SecretAccessKey = Mask(SecretAccessKey),
            ClientId = ClientId,
            ClientSecret = Mask(ClientSecret),
            RefreshToken = Mask(RefreshToken)
        };
    }

    private static string? Mask(string? value)
    {
        return string.IsNullOrEmpty(value) ? value : MaskedValue;
    }
}

/// <summary>A backup file that already exists remotely, used to apply the retention days.</summary>
public sealed record RemoteBackupFile(string Id, string Name, DateTimeOffset ModifiedAt, long Size);