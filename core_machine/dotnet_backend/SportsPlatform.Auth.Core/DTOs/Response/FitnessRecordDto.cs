namespace SportsPlatform.Auth.Core.DTOs.Response;

public class FitnessRecordDto
{
    public Guid FitnessId { get; set; }
    public Guid TeamId { get; set; }
    public Guid PlayerId { get; set; }
    public Guid PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public Guid? FitnessUserId { get; set; }
    public string? FitnessUserName { get; set; }
    public DateTime TestDate { get; set; }
    public decimal? Height { get; set; }
    public decimal? Weight { get; set; }
    public decimal? Bmi { get; set; }
    public decimal? BodyFatPct { get; set; }
    public decimal? SpeedTestResult { get; set; }
    public decimal? EnduranceScore { get; set; }
    public string? CustomTestName { get; set; }
    public decimal? CustomTestResult { get; set; }
}
