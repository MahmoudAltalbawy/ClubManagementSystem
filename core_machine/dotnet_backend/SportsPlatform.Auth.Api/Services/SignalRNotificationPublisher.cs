using Microsoft.AspNetCore.SignalR;
using SportsPlatform.Auth.Api.Hubs;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Services;

public class SignalRNotificationPublisher : INotificationRealtimePublisher
{
    private readonly IHubContext<NotificationHub> _hubContext;

    public SignalRNotificationPublisher(IHubContext<NotificationHub> hubContext)
    {
        _hubContext = hubContext;
    }

    public Task PublishAsync(Guid recipientUserId, NotificationDto notification, CancellationToken cancellationToken = default)
    {
        return _hubContext.Clients
            .Group(NotificationHub.GroupName(recipientUserId))
            .SendAsync("notificationReceived", notification, cancellationToken);
    }
}
