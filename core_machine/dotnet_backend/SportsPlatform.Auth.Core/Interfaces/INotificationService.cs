using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface INotificationService
{
    Task<List<NotificationDto>> CreateForUsersAsync(IEnumerable<Guid> recipientUserIds, CreateNotificationRequest request, CancellationToken cancellationToken = default);
    Task<List<NotificationDto>> CreateForTeamAsync(Guid teamId, Guid actorUserId, CreateNotificationRequest request, bool excludeActor = true, CancellationToken cancellationToken = default);
    Task<NotificationListDto> GetMyNotificationsAsync(Guid userId, int page, int pageSize, bool unreadOnly, CancellationToken cancellationToken = default);
    Task<int> GetUnreadCountAsync(Guid userId, CancellationToken cancellationToken = default);
    Task MarkReadAsync(Guid userId, Guid notificationId, CancellationToken cancellationToken = default);
    Task MarkAllReadAsync(Guid userId, CancellationToken cancellationToken = default);
    Task CleanupOldNotificationsAsync(DateTime olderThanUtc, CancellationToken cancellationToken = default);
    Task SendMedicalReturnDueRemindersAsync(CancellationToken cancellationToken = default);
}
