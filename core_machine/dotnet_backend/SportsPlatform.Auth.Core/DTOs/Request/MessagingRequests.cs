using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateConversationRequest
{
    public List<Guid> ParticipantUserIds { get; set; } = new();
    public string? Name { get; set; }
    public bool IsGroup { get; set; }
}

public class SendMessageRequest
{
    public string Content { get; set; } = string.Empty;
    public string? MessageType { get; set; }
    public string? MediaUrl { get; set; }
    public string? MediaFileName { get; set; }
    [Range(-90, 90)]
    public double? LocationLatitude { get; set; }
    [Range(-180, 180)]
    public double? LocationLongitude { get; set; }
    [MaxLength(200)]
    public string? LocationLabel { get; set; }
}

public class EditMessageRequest
{
    public string Content { get; set; } = string.Empty;
}

public class SendReactionRequest
{
    public string Emoji { get; set; } = string.Empty;
}
