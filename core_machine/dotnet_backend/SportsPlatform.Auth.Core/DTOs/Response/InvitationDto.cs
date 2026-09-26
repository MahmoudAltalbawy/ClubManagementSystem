namespace SportsPlatform.Auth.Core.DTOs.Response;

public class InvitationDto
{
    public Guid InvitationId { get; set; }
    public string Token { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string ClubName { get; set; } = string.Empty;
    public string? TeamName { get; set; }
    public string? PlayerPosition { get; set; }
    public int? JerseyNumber { get; set; }
    public string InviterName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
}
