using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface INotificationRealtimePublisher
{
    Task PublishAsync(Guid recipientUserId, NotificationDto notification, CancellationToken cancellationToken = default);
}
