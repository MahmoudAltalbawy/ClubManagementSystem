namespace SportsPlatform.Auth.Core.DTOs.Response;

public class ConversationDto
{
    public Guid ConversationId { get; set; }
    public bool IsGroup { get; set; }
    public string? Name { get; set; }
    public List<ParticipantDto> Participants { get; set; } = new();
    public MessageDto? LastMessage { get; set; }
    public int UnreadCount { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class ParticipantDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public string? TeamName { get; set; }
}

public class MessageDto
{
    public Guid MessageId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderUserId { get; set; }
    public string SenderName { get; set; } = string.Empty;
    public string? SenderProfileImageUrl { get; set; }
    public string Content { get; set; } = string.Empty;
    public DateTime SentAt { get; set; }
    public bool IsRead { get; set; }
    public DateTime? EditedAt { get; set; }
    public bool IsDeleted { get; set; }
    public string MessageType { get; set; } = "text";
    public string? MediaUrl { get; set; }
    public string? MediaFileName { get; set; }
    public double? LocationLatitude { get; set; }
    public double? LocationLongitude { get; set; }
    public string? LocationLabel { get; set; }
    public List<MessageReactionDto> Reactions { get; set; } = new();
    public List<MessageSeenByDto> SeenBy { get; set; } = new();
    public int SeenByCount { get; set; }
    public int RequiredSeenCount { get; set; }
    public bool SeenByAll { get; set; }
}

public class MessageReactionDto
{
    public Guid ReactionId { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string Emoji { get; set; } = string.Empty;
}

public class MessageSeenByDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? ProfileImageUrl { get; set; }
    public DateTime ReadAt { get; set; }
}
