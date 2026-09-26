namespace SportsPlatform.Auth.Core.DTOs.Response;

public class TeamMemberDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public string? ProfileImageUrl { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? Position { get; set; }
    public int? JerseyNumber { get; set; }
    public bool IsInjured { get; set; }
    public string? InjuryType { get; set; }
    public DateTime? JoinedAt { get; set; }
}
