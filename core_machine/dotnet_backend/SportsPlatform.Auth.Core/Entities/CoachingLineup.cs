using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.Entities;

public class CoachingLineup
{
    public Guid LineupId { get; set; }
    public Guid TeamId { get; set; }
    public Guid? EventId { get; set; }
    public Guid? SeasonId { get; set; }
    public Guid CreatedBy { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Formation { get; set; }
    public string? GameModel { get; set; }
    public string? TacticalNotes { get; set; }
    public PlanVisibility Visibility { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team Team { get; set; } = null!;
    public Event? Event { get; set; }
    public Season? Season { get; set; }
    public User Creator { get; set; } = null!;
    public ICollection<CoachingLineupPlayer> Players { get; set; } = new List<CoachingLineupPlayer>();
}
