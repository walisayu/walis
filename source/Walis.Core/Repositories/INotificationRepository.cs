using Walis.Core.Models;

namespace Walis.Core.Repositories;

public interface INotificationRepository
{
    Task<IReadOnlyList<NotificationChannel>> ListChannelsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationChannel>> ListEnabledChannelsAsync(CancellationToken cancellationToken = default);

    Task<NotificationChannel?> GetChannelAsync(Guid id, CancellationToken cancellationToken = default);

    Task<NotificationChannel> CreateChannelAsync(NotificationChannelDraft draft,
        CancellationToken cancellationToken = default);

    Task<NotificationChannel?> UpdateChannelAsync(Guid id, NotificationChannelDraft draft,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteChannelAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NotificationLogEntry>> ListLogAsync(int limit, CancellationToken cancellationToken = default);

    Task AppendLogAsync(NotificationLogEntry entry, CancellationToken cancellationToken = default);
}