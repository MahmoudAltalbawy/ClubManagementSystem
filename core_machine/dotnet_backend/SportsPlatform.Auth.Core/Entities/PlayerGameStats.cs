namespace SportsPlatform.Auth.Core.Entities;

public class PlayerGameStats
{
    public Guid StatId { get; set; }
    public Guid TeamId { get; set; }
    public Guid PlayerUserId { get; set; }
    public Guid? EventId { get; set; }
    public Guid RecordedBy { get; set; }
    public DateOnly MatchDate { get; set; }
    public string? OpponentName { get; set; }
    public int? MinutesPlayed { get; set; }
    public int? Goals { get; set; }
    public int? Assists { get; set; }
    public int? YellowCards { get; set; }
    public int? RedCards { get; set; }
    public decimal? Rating { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team Team { get; set; } = null!;
    public User Player { get; set; } = null!;
    public Event? Event { get; set; }
    public User Recorder { get; set; } = null!;
}
