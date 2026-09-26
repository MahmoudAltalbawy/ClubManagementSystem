namespace SportsPlatform.Auth.Core.DTOs.Response;

public class MatchAnalysisReportDto
{
    public Guid ReportId { get; set; }
    public Guid? TeamId { get; set; }
    public string TeamCode { get; set; } = string.Empty;
    public string OpponentCode { get; set; } = string.Empty;
    public string OpponentName { get; set; } = string.Empty;
    public DateOnly MatchDate { get; set; }
    public string Competition { get; set; } = string.Empty;
    public string? Venue { get; set; }
    public string? GameNo { get; set; }
    public int TeamScore { get; set; }
    public int OpponentScore { get; set; }
    public string Result { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public List<MatchLineupAnalysisDto> TopLineups { get; set; } = new();
    public List<MatchAnalysisDocumentDto> Documents { get; set; } = new();
}

public class MatchLineupAnalysisDto
{
    public Guid LineupId { get; set; }
    public Guid ReportId { get; set; }
    public string TeamCode { get; set; } = string.Empty;
    public string LineupPlayers { get; set; } = string.Empty;
    public List<string> Players { get; set; } = new();
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
}

public class MatchAnalysisDocumentDto
{
    public Guid DocumentId { get; set; }
    public Guid ReportId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileUrl { get; set; } = string.Empty;
}

public class MatchAnalysisSummaryDto
{
    public int TotalMatches { get; set; }
    public int Wins { get; set; }
    public int Losses { get; set; }
    public decimal AverageScoreDiff { get; set; }
    public decimal AveragePointsFor { get; set; }
    public decimal AveragePointsAgainst { get; set; }
    public List<MatchLineupAnalysisDto> BestLineups { get; set; } = new();
}
