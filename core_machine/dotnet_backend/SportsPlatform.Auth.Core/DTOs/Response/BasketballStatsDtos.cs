namespace SportsPlatform.Auth.Core.DTOs.Response;

public class BasketballUploadPreviewDto
{
    public string FileName { get; set; } = string.Empty;
    public bool CanSave { get; set; }
    public string? Message { get; set; }
    public int RowCount { get; set; }
    public int PlayerCount { get; set; }
    public int TeamTotalCount { get; set; }
    public List<BasketballExtractedRowDto> Rows { get; set; } = new();
}

public class BasketballExtractedRowDto
{
    public string? Granularity { get; set; }
    public string? RowType { get; set; }
    public string? SourceFile { get; set; }
    public string? GameNo { get; set; }
    public string? GameDate { get; set; }
    public string? StartTime { get; set; }
    public string? Matchup { get; set; }
    public string? TeamCode { get; set; }
    public string? TeamName { get; set; }
    public int? TeamScore { get; set; }
    public string? OpponentName { get; set; }
    public int? OpponentScore { get; set; }
    public int? PlayerNo { get; set; }
    public string? PlayerName { get; set; }
    public string? Status { get; set; }
    public bool IsStarter { get; set; }
    public bool IsCaptain { get; set; }
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
    public int? Assists { get; set; }
    public int? Turnovers { get; set; }
    public int? Steals { get; set; }
    public int? Blocks { get; set; }
    public int? PersonalFouls { get; set; }
    public int? FoulsDrawn { get; set; }
    public int? Efficiency { get; set; }
    public int? Points { get; set; }
    public int? TeamOffReb { get; set; }
    public int? TeamDefReb { get; set; }
    public int? TeamReb { get; set; }
    public int? TeamPF { get; set; }
    public int? TeamFD { get; set; }
}

public class BasketballMatchStatsDto
{
    public Guid MatchStatsId { get; set; }
    public Guid TeamId { get; set; }
    public Guid EventId { get; set; }
    public string Category { get; set; } = "game";
    public string? OpponentName { get; set; }
    public int? TeamScore { get; set; }
    public int? OpponentScore { get; set; }
    public string? Result { get; set; }
    public string? Venue { get; set; }
    public string? CompetitionName { get; set; }
    public string? GameNo { get; set; }
    public string? Matchup { get; set; }
    public string? TwoPtMA { get; set; }
    public string? ThreePtMA { get; set; }
    public string? FtMA { get; set; }
    public int? OffensiveRebounds { get; set; }
    public int? DefensiveRebounds { get; set; }
    public int? TotalRebounds { get; set; }
    public int? Assists { get; set; }
    public int? Turnovers { get; set; }
    public int? Steals { get; set; }
    public int? Blocks { get; set; }
    public int? PersonalFouls { get; set; }
    public int? FoulsDrawn { get; set; }
    public int? Efficiency { get; set; }
    public int? Points { get; set; }
    public string? Minutes { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<BasketballPlayerStatsDto> PlayerStats { get; set; } = new();
}

public class BasketballPlayerStatsDto
{
    public Guid PlayerMatchStatsId { get; set; }
    public Guid? PlayerUserId { get; set; }
    public string? PlayerName { get; set; }
    public int? PlayerNo { get; set; }
    public string? Status { get; set; }
    public bool IsStarter { get; set; }
    public bool IsCaptain { get; set; }
    public string? Minutes { get; set; }
    public string? TwoPtMA { get; set; }
    public string? ThreePtMA { get; set; }
    public string? FtMA { get; set; }
    public int? OffensiveRebounds { get; set; }
    public int? DefensiveRebounds { get; set; }
    public int? TotalRebounds { get; set; }
    public int? Assists { get; set; }
    public int? Turnovers { get; set; }
    public int? Steals { get; set; }
    public int? Blocks { get; set; }
    public int? PersonalFouls { get; set; }
    public int? FoulsDrawn { get; set; }
    public int? Efficiency { get; set; }
    public int? Points { get; set; }
}

public class BasketballTeamAggregateDto
{
    public int TotalGames { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public int TotalPoints { get; set; }
    public int TotalRebounds { get; set; }
    public int TotalAssists { get; set; }
    public int TotalSteals { get; set; }
    public int TotalBlocks { get; set; }
    public int TotalTurnovers { get; set; }
    public string? TotalTwoPtMA { get; set; }
    public string? TotalThreePtMA { get; set; }
    public string? TotalFtMA { get; set; }
    public List<BasketballPlayerAggregateDto> PlayerLeaderboard { get; set; } = new();
}

public class BasketballPlayerAggregateDto
{
    public Guid? PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public int? PlayerNo { get; set; }
    public int GamesPlayed { get; set; }
    public int TotalPoints { get; set; }
    public int TotalRebounds { get; set; }
    public int TotalAssists { get; set; }
    public int TotalSteals { get; set; }
    public int TotalBlocks { get; set; }
    public int TotalTurnovers { get; set; }
    public int TotalEfficiency { get; set; }
    public string? TotalMinutes { get; set; }
    public string? TotalTwoPtMA { get; set; }
    public string? TotalThreePtMA { get; set; }
    public string? TotalFtMA { get; set; }
}
