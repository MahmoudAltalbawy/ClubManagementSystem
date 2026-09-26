namespace SportsPlatform.Auth.Core.Entities;

public class User
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Username { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? PhoneNumber { get; set; }
    public DateOnly? Dob { get; set; }
    public string? Bio { get; set; }
    public int? YearsOfExperience { get; set; }
    public string? ProfileImageUrl { get; set; }
    public bool IsAdmin { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // Navigation properties
    public ICollection<UserAuthProvider> AuthProviders { get; set; } = new List<UserAuthProvider>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public Club? CreatedClub { get; set; }
    public ICollection<ClubMembership> ClubMemberships { get; set; } = new List<ClubMembership>();
    public ICollection<TeamMembership> TeamMemberships { get; set; } = new List<TeamMembership>();
}
