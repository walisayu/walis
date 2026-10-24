namespace Walis.Core.Backup;

/// <summary>Writes to a local path; mounted network drives use this too.</summary>
public sealed class LocalTargetClient : IBackupTargetClient
{
    public string Provider => BackupProviders.Local;

    public Task TestAsync(BackupTarget target, CancellationToken cancellationToken = default)
    {
        var directory = Resolve(target);

        try
        {
            Directory.CreateDirectory(directory);

            // An existing directory is not proof it is writable; only actually writing a file counts.
            var probe = Path.Combine(directory, $".walis-write-test-{Guid.NewGuid():N}");
            using (PrivateFileSystem.CreatePrivateFile(probe))
            {
            }

            File.Delete(probe);
        }
        catch (Exception exception)
        {
            throw new BackupTargetException($"無法寫入 {directory}：{exception.Message}", exception);
        }

        return Task.CompletedTask;
    }

    public async Task UploadAsync(BackupTarget target, string filePath, CancellationToken cancellationToken = default)
    {
        var directory = Resolve(target);
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, Path.GetFileName(filePath));
        var temporary = Path.Combine(directory, $".walis-upload-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var source = File.OpenRead(filePath))
            await using (var output = PrivateFileSystem.CreatePrivateFile(temporary))
            {
                await source.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            // Rename replaces a pre-created symlink itself rather than following it to an arbitrary
            // file. Readers likewise never observe a partially copied backup.
            File.Move(temporary, destination, true);
        }
        catch
        {
            File.Delete(temporary);
            throw;
        }
    }

    public Task<IReadOnlyList<RemoteBackupFile>> ListAsync(
        BackupTarget target,
        CancellationToken cancellationToken = default)
    {
        var directory = Resolve(target);

        if (!Directory.Exists(directory)) return Task.FromResult<IReadOnlyList<RemoteBackupFile>>([]);

        var files = Directory
            .EnumerateFiles(directory, $"{BackupNaming.Prefix}*")
            .Select(path => new FileInfo(path))
            .Where(info => (info.Attributes & FileAttributes.ReparsePoint) == 0)
            .Select(info => new RemoteBackupFile(info.FullName, info.Name, info.LastWriteTimeUtc, info.Length))
            .ToList();

        return Task.FromResult<IReadOnlyList<RemoteBackupFile>>(files);
    }

    public Task DeleteAsync(
        BackupTarget target,
        RemoteBackupFile file,
        CancellationToken cancellationToken = default)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Resolve(target)));
        var candidate = Path.GetFullPath(file.Id);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!BackupNaming.IsBackupFile(file.Name) ||
            !string.Equals(Path.GetDirectoryName(candidate), directory, comparison) ||
            !string.Equals(Path.GetFileName(candidate), file.Name, comparison))
            throw new BackupTargetException("拒絕刪除目的地目錄外的檔案。");

        File.Delete(candidate);
        return Task.CompletedTask;
    }

    private static string Resolve(BackupTarget target)
    {
        var path = (target.Path ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) throw new BackupTargetException("本機目的地請填絕對路徑。");

        return path;
    }
}