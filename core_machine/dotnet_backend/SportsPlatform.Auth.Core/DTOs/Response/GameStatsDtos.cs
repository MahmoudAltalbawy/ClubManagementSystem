namespace SportsPlatform.Auth.Core.DTOs.Response;

public class MatchStatsDto
{
    public Guid MatchStatsId { get; set; }
    public Guid TeamId { get; set; }
    public Guid EventId { get; set; }
    public Guid SeasonId { get; set; }
    public string EventTitle { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime EventStartAt { get; set; }
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
    public string RecorderName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public bool HasRawPdf { get; set; }
    public string? RawPdfFileName { get; set; }
    public List<PlayerMatchStatsDto> PlayerStats { get; set; } = new();
}

public class MatchStatsSummaryDto
{
    public Guid MatchStatsId { get; set; }
    public Guid EventId { get; set; }
    public string EventTitle { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime EventStartAt { get; set; }
    public string? OpponentName { get; set; }
    public int? TeamScore { get; set; }
    public int? OpponentScore { get; set; }
    public string? Result { get; set; }
    public string? Venue { get; set; }
    public string? CompetitionName { get; set; }
    public string? Category { get; set; }
    public string? GameNo { get; set; }
    public string? Matchup { get; set; }
    public string? TwoPtMA { get; set; }
    public string? ThreePtMA { get; set; }
    public string? FtMA { get; set; }
    public int? OffensiveRebounds { get; set; }
    public int? DefensiveRebounds { get; set; }
    public int? TotalRebounds { get; set; }
    public int? BasketballAssists { get; set; }
    public int? Turnovers { get; set; }
    public int? Steals { get; set; }
    public int? Blocks { get; set; }
    public int? PersonalFouls { get; set; }
    public int? FoulsDrawn { get; set; }
    public int? Efficiency { get; set; }
    public int? Points { get; set; }
    public string? Minutes { get; set; }
    public int PlayerCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public class PlayerMatchStatsDto
{
    public Guid PlayerMatchStatsId { get; set; }
    public Guid MatchStatsId { get; set; }
    public Guid TeamId { get; set; }
    public Guid EventId { get; set; }
    public Guid SeasonId { get; set; }
    public Guid PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string EventTitle { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime EventStartAt { get; set; }
    public string? OpponentName { get; set; }
    public int? TeamScore { get; set; }
    public int? OpponentScore { get; set; }
    public string? Category { get; set; }
    public string? GameNo { get; set; }
    public string? Matchup { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? Status { get; set; }
    public int? PlayerNo { get; set; }
    public bool? IsStarter { get; set; }
    public bool? IsCaptain { get; set; }
    public int? GamesListed { get; set; }
    public int? GamesPlayed { get; set; }
    public int? Starts { get; set; }
    public string? Minutes { get; set; }
    public string? TwoPtMA { get; set; }
    public string? ThreePtMA { get; set; }
    public string? FtMA { get; set; }
    public int? OffensiveRebounds { get; set; }
    public int? DefensiveRebounds { get; set; }
    public int? TotalRebounds { get; set; }
    public int? BasketballAssists { get; set; }
    public int? Turnovers { get; set; }
    public int? Steals { get; set; }
    public int? Blocks { get; set; }
    public int? PersonalFouls { get; set; }
    public int? FoulsDrawn { get; set; }
    public int? Efficiency { get; set; }
    public int? Points { get; set; }
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

public class TeamStatsAggregateDto
{
    public int TotalEvents { get; set; }
    public int Matches { get; set; }
    public int Trainings { get; set; }
    public int Wins { get; set; }
    public int Draws { get; set; }
    public int Losses { get; set; }
    public int TotalGoals { get; set; }
    public int TotalAssists { get; set; }
    public int ShotsOnTarget { get; set; }
    public int TotalShots { get; set; }
    public int PassesCompleted { get; set; }
    public int PassesAttempted { get; set; }
    public decimal? AveragePossessionPercent { get; set; }
    public decimal? AveragePassAccuracy { get; set; }
    public int Tackles { get; set; }
    public int Interceptions { get; set; }
    public int YellowCards { get; set; }
    public int RedCards { get; set; }
    public List<PlayerStatsAggregateDto> PlayerLeaderboard { get; set; } = new();
}

public class PlayerStatsAggregateDto
{
    public Guid PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int EventsPlayed { get; set; }
    public int MinutesPlayed { get; set; }
    public int Goals { get; set; }
    public int Assists { get; set; }
    public int ShotsOnTarget { get; set; }
    public int TotalShots { get; set; }
    public int PassesCompleted { get; set; }
    public int PassesAttempted { get; set; }
    public decimal? AveragePassAccuracy { get; set; }
    public int Tackles { get; set; }
    public int Interceptions { get; set; }
    public int YellowCards { get; set; }
    public int RedCards { get; set; }
    public decimal? AverageRating { get; set; }
}

public class StatsUploadPreviewDto
{
    public string FileName { get; set; } = string.Empty;
    public bool CanSave { get; set; }
    public string? Message { get; set; }
    public CreateStatsPreviewDto? ParsedStats { get; set; }
}

public class CreateStatsPreviewDto
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
    public List<CreatePlayerStatsPreviewDto> PlayerStats { get; set; } = new();
}

public class CreatePlayerStatsPreviewDto
{
    public Guid PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
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
