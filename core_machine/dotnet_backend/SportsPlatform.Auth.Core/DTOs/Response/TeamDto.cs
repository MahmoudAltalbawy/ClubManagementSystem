namespace SportsPlatform.Auth.Core.DTOs.Response;

public class TeamDto
{
    public Guid TeamId { get; set; }
    public Guid? ClubId { get; set; }
    public string? ClubName { get; set; }
    public string? ClubLogoUrl { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public Guid CategoryId { get; set; }
    public Guid? CreatedBy { get; set; }
    public string? CreatorName { get; set; }
    public string? MyRole { get; set; }
    public int MemberCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<ManagerSummaryDto> Managers { get; set; } = new();
}
