namespace SportsPlatform.Auth.Core.Entities;

public class MatchAnalysisDocument
{
    public Guid DocumentId { get; set; }
    public Guid ReportId { get; set; }
    public string DocumentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;

    public MatchAnalysisReport Report { get; set; } = null!;
}
