namespace SportsPlatform.Auth.Core.Entities;

public class PlayerTeam
{
    public Guid Id { get; set; }
    public Guid PlayerId { get; set; }
    public Guid TeamId { get; set; }
    public DateOnly JoinedDate { get; set; }
    public DateOnly? LeftDate { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public PlayerProfile Player { get; set; } = null!;
    public Team Team { get; set; } = null!;
}
