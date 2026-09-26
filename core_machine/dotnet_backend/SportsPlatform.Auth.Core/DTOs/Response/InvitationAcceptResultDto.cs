namespace SportsPlatform.Auth.Core.DTOs.Response;

public class InvitationAcceptResultDto
{
    public string Message { get; set; } = string.Empty;
    public Guid ClubId { get; set; }
    public Guid? TeamId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string ClubName { get; set; } = string.Empty;
    public string? TeamName { get; set; }
}
