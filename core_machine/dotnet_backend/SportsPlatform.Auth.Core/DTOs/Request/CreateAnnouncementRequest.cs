namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateAnnouncementRequest
{
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public string Priority { get; set; } = "Normal";
}
