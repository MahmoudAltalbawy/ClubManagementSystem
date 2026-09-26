using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Hubs;

[Authorize]
public class NotificationHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        var userId = GetUserId();
        if (userId.HasValue)
        {
            NotificationConnectionTracker.Add(userId.Value, Context.ConnectionId);
            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(userId.Value));
        }

        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var userId = GetUserId();
        if (userId.HasValue)
            NotificationConnectionTracker.Remove(userId.Value, Context.ConnectionId);

        await base.OnDisconnectedAsync(exception);
    }

    public static string GroupName(Guid userId) => $"user:{userId:N}";

    private Guid? GetUserId()
    {
        var claim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var parsed) ? parsed : null;
    }
}

public class NotificationConnectionTracker : IRealtimeConnectionTracker
{
    private static readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> Connections = new();

    public bool IsConnected(Guid userId) =>
        Connections.TryGetValue(userId, out var connections) && !connections.IsEmpty;

    public static void Add(Guid userId, string connectionId)
    {
        var userConnections = Connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, byte>());
        userConnections.TryAdd(connectionId, 0);
    }

    public static void Remove(Guid userId, string connectionId)
    {
        if (!Connections.TryGetValue(userId, out var userConnections))
            return;

        userConnections.TryRemove(connectionId, out _);
        if (userConnections.IsEmpty)
            Connections.TryRemove(userId, out _);
    }
}
