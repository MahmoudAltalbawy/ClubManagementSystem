namespace SportsPlatform.Auth.Core.DTOs.Response;

public class AuthResponse
{
    public string Message { get; set; } = string.Empty;
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public bool RequiresProfileCompletion { get; set; }
    public UserInfoDto? User { get; set; }
}

public class UserInfoDto
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string? PhoneNumber { get; set; }
    public DateOnly? Dob { get; set; }
    public string? Bio { get; set; }
    public int? YearsOfExperience { get; set; }
    public string? ProfileImageUrl { get; set; }
    public bool IsAdmin { get; set; }
    public List<string> Roles { get; set; } = new();
    public List<UserClubInfoDto> Clubs { get; set; } = new();
    public List<UserTeamInfoDto> Teams { get; set; } = new();
}

public class UserClubInfoDto
{
    public Guid ClubId { get; set; }
    public string ClubName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
}

public class UserTeamInfoDto
{
    public Guid TeamId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public Guid ClubId { get; set; }
    public string Role { get; set; } = string.Empty;
}
