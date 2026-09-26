namespace SportsPlatform.Auth.Core.Entities;

public class CoachingLineupPlayer
{
    public Guid LineupPlayerId { get; set; }
    public Guid LineupId { get; set; }
    public Guid PlayerUserId { get; set; }
    public string Position { get; set; } = string.Empty;
    public string Unit { get; set; } = "Starting";
    public int SortOrder { get; set; }
    public string? Instructions { get; set; }

    public CoachingLineup Lineup { get; set; } = null!;
    public User Player { get; set; } = null!;
}
