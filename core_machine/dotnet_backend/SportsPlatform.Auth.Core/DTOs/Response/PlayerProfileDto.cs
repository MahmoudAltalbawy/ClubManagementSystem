namespace SportsPlatform.Auth.Core.DTOs.Response;

public class PlayerProfileDto
{
    public Guid PlayerId { get; set; }
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? Bio { get; set; }
    public string? ProfileImageUrl { get; set; }
    public DateOnly? Dob { get; set; }
    public string? Position { get; set; }
    public int? JerseyNumber { get; set; }
    public decimal? Height { get; set; }
    public decimal? Weight { get; set; }
    public Guid? CurrentTeamId { get; set; }
    public string? CurrentTeamName { get; set; }
}
