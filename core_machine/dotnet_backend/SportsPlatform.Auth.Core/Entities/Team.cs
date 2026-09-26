namespace SportsPlatform.Auth.Core.Entities;

public class Team
{
    public Guid TeamId { get; set; }
    public Guid? ClubId { get; set; }
    public string TeamName { get; set; } = string.Empty;
    public string? ImageUrl { get; set; }
    public Guid CategoryId { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Club? Club { get; set; }
    public User? Creator { get; set; }
    public ICollection<TeamMembership> Memberships { get; set; } = new List<TeamMembership>();
    public ICollection<Invitation> Invitations { get; set; } = new List<Invitation>();
    public ICollection<Event> Events { get; set; } = new List<Event>();
    public ICollection<Season> Seasons { get; set; } = new List<Season>();
}
