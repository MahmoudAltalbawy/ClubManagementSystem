namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateBasketballStatsRequest
{
    public Guid EventId { get; set; }
    public string Category { get; set; } = "game";       // "game" | "training"
    public string? OpponentName { get; set; }
    public int? TeamScore { get; set; }
    public int? OpponentScore { get; set; }
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
    public List<CreateBasketballPlayerStatsRequest> PlayerStats { get; set; } = new();
}

public class CreateBasketballPlayerStatsRequest
{
    public Guid? PlayerUserId { get; set; }
    public string? PlayerName { get; set; }
    public int? PlayerNo { get; set; }
    public string? Status { get; set; }              // "PLAYED" | "DNP"
    public bool IsStarter { get; set; }
    public bool IsCaptain { get; set; }
    public string? Minutes { get; set; }             // "mm:ss"
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
    public string? Notes { get; set; }
}

public class ConfirmBasketballUploadRequest
{
    public Guid EventId { get; set; }
    public string Category { get; set; } = "game";
    public List<BasketballExtractedRow> Rows { get; set; } = new();
}

public class BasketballExtractedRow
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
