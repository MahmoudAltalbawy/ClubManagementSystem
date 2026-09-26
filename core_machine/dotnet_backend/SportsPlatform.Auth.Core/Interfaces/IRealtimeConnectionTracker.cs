namespace SportsPlatform.Auth.Core.Interfaces;

public interface IRealtimeConnectionTracker
{
    bool IsConnected(Guid userId);
}
