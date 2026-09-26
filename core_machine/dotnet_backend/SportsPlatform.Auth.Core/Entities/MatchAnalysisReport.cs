namespace SportsPlatform.Auth.Core.Entities;

public class MatchAnalysisReport
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
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team? Team { get; set; }
    public ICollection<MatchLineupAnalysis> Lineups { get; set; } = new List<MatchLineupAnalysis>();
    public ICollection<MatchAnalysisDocument> Documents { get; set; } = new List<MatchAnalysisDocument>();
}
