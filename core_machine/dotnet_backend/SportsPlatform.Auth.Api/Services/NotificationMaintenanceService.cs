using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Services;

public class NotificationMaintenanceService : BackgroundService
{
    private static readonly TimeSpan FinalizedInvitationRetention = TimeSpan.FromDays(30);
    private readonly IServiceScopeFactory _scopeFactory;

    public NotificationMaintenanceService(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait for database migrations to complete before first run
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var notifications = scope.ServiceProvider.GetRequiredService<INotificationService>();
        var invitations = scope.ServiceProvider.GetRequiredService<IInvitationService>();

        await notifications.CleanupOldNotificationsAsync(DateTime.UtcNow.AddDays(-60), cancellationToken);
        await invitations.CleanupFinalizedInvitationsAsync(
            DateTime.UtcNow.Subtract(FinalizedInvitationRetention),
            cancellationToken);
        await notifications.SendMedicalReturnDueRemindersAsync(cancellationToken);
    }
}
