namespace SportsPlatform.Auth.Core.Entities;

public class Club
{
    public Guid ClubId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? Location { get; set; }
    public double? LocationLatitude { get; set; }
    public double? LocationLongitude { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User Creator { get; set; } = null!;
    public ICollection<ClubMembership> Memberships { get; set; } = new List<ClubMembership>();
    public ICollection<Team> Teams { get; set; } = new List<Team>();
    public ICollection<Invitation> Invitations { get; set; } = new List<Invitation>();
}
