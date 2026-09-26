namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateMatchStatsRequest
{
    public Guid EventId { get; set; }
    public string? OpponentName { get; set; }
    public int? TeamScore { get; set; }
    public int? OpponentScore { get; set; }
    public string? Result { get; set; }
    public string? Venue { get; set; }
    public string? CompetitionName { get; set; }
    public decimal? PossessionPercent { get; set; }
    public int? TotalGoals { get; set; }
    public int? TotalAssists { get; set; }
    public int? ShotsOnTarget { get; set; }
    public int? TotalShots { get; set; }
    public int? PassesCompleted { get; set; }
    public int? PassesAttempted { get; set; }
    public decimal? PassAccuracy { get; set; }
    public int? Tackles { get; set; }
    public int? Interceptions { get; set; }
    public int? YellowCards { get; set; }
    public int? RedCards { get; set; }
    public string? Notes { get; set; }
    public List<CreatePlayerMatchStatsRequest> PlayerStats { get; set; } = new();
}

public class CreatePlayerMatchStatsRequest
{
    public Guid PlayerUserId { get; set; }
    public int? MinutesPlayed { get; set; }
    public int? Goals { get; set; }
    public int? Assists { get; set; }
    public int? ShotsOnTarget { get; set; }
    public int? TotalShots { get; set; }
    public int? PassesCompleted { get; set; }
    public int? PassesAttempted { get; set; }
    public decimal? PassAccuracy { get; set; }
    public int? Tackles { get; set; }
    public int? Interceptions { get; set; }
    public int? YellowCards { get; set; }
    public int? RedCards { get; set; }
    public decimal? Rating { get; set; }
    public string? Notes { get; set; }
}
