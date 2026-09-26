namespace SportsPlatform.Auth.Core.Entities;

public class AppNotification
{
    public Guid NotificationId { get; set; }
    public Guid RecipientUserId { get; set; }
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
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public DateTime? EmailSentAt { get; set; }

    public User RecipientUser { get; set; } = null!;
    public User? ActorUser { get; set; }
    public Club? Club { get; set; }
    public Team? Team { get; set; }
}
