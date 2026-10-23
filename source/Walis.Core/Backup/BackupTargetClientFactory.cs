namespace Walis.Core.Backup;

/// <summary>Picks the implementation for a Provider.</summary>
public sealed class BackupTargetClientFactory(IEnumerable<IBackupTargetClient> clients)
{
    private readonly Dictionary<string, IBackupTargetClient> _clients =
        clients.ToDictionary(client => client.Provider, StringComparer.Ordinal);

    public IBackupTargetClient Resolve(BackupTarget target)
    {
        return _clients.TryGetValue(target.Provider, out var client)
            ? client
            : throw new BackupTargetException($"不支援的目的地類型：{target.Provider}");
    }
}