namespace Walis.Core.Backup;

/// <summary>
///     The upload implementation for each storage provider. Deliberately requires only four operations: test, upload, list and delete;
///     the retention logic lives in the caller so implementations need not repeat it.
/// </summary>
public interface IBackupTargetClient
{
    string Provider { get; }

    /// <summary>Tests whether the configuration works; throws an exception with the reason on failure.</summary>
    Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default);

    Task UploadAsync(BackupTarget target, string filePath, CancellationToken cancellationToken = default);

    /// <summary>Lists the backup files produced by Walis in the destination (names starting with walis-backup-).</summary>
    Task<IReadOnlyList<RemoteBackupFile>> ListAsync(BackupTarget target, CancellationToken cancellationToken = default);

    Task DeleteAsync(BackupTarget target, RemoteBackupFile file, CancellationToken cancellationToken = default);
}

/// <summary>Thrown when the destination settings are wrong or the remote returns an error; the message is shown directly in the admin UI.</summary>
public sealed class BackupTargetException(string message, Exception? inner = null)
    : Exception(message, inner);

public static class BackupNaming
{
    public const string Prefix = "walis-backup-";

    public static bool IsBackupFile(string name)
    {
        return name.Length is > 0 and <= 255 &&
               name.StartsWith(Prefix, StringComparison.Ordinal) &&
               !name.Any(character => char.IsControl(character) || character is '/' or '\\');
    }
}