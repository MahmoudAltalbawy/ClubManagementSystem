namespace SportsPlatform.Auth.Core.DTOs.Response;

public class AnnouncementDto
{
    public Guid AnnouncementId { get; set; }
    public Guid TeamId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string Priority { get; set; } = string.Empty;
    public string CreatorName { get; set; } = string.Empty;
    public string CreatorRole { get; set; } = string.Empty;
    public string? CreatorImageUrl { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
}
