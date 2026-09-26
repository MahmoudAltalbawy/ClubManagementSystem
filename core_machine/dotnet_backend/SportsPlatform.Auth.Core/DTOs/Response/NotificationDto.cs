namespace SportsPlatform.Auth.Core.DTOs.Response;

public class NotificationDto
{
    public Guid NotificationId { get; set; }
    public Guid RecipientUserId { get; set; }
    public Guid? ActorUserId { get; set; }
    public string? ActorName { get; set; }
    public Guid? ClubId { get; set; }
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public string Type { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string DeliveryPolicy { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public string? TargetType { get; set; }
    public Guid? TargetId { get; set; }
    public string? TargetRoute { get; set; }
    public string? MetadataJson { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
    public bool IsRead => ReadAt.HasValue;
}

public class NotificationListDto
{
    public List<NotificationDto> Items { get; set; } = new();
    public int TotalCount { get; set; }
    public int UnreadCount { get; set; }
}

public class UnreadCountDto
{
    public int UnreadCount { get; set; }
}
