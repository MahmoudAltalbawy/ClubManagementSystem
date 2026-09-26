namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateNotificationRequest
{
    public Guid? ActorUserId { get; set; }
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Priority { get; set; } = "Normal";
    public string DeliveryPolicy { get; set; } = "RealtimeIfConnected";
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? TargetRoute { get; set; }
    public string? MetadataJson { get; set; }
    public string? UniqueKey { get; set; }
}
