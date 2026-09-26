namespace SportsPlatform.Auth.Core.Entities;

public class MatchLineupAnalysis
{
    public Guid LineupId { get; set; }
    public Guid ReportId { get; set; }
    public string TeamCode { get; set; } = string.Empty;
    public string LineupPlayers { get; set; } = string.Empty;
    public string TimeOnCourt { get; set; } = string.Empty;
    public int TimeSeconds { get; set; }
    public int PointsFor { get; set; }
    public int PointsAgainst { get; set; }
    public int ScoreDiff { get; set; }
    public decimal PointsPerMinute { get; set; }
    public int Rebounds { get; set; }
    public int Steals { get; set; }
    public int Turnovers { get; set; }
    public int Assists { get; set; }

    public MatchAnalysisReport Report { get; set; } = null!;
}
