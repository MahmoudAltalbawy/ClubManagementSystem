namespace SportsPlatform.Auth.Core.Entities;

public class FitnessRecord
{
    public Guid FitnessId { get; set; }
    public Guid TeamId { get; set; }
    public Guid PlayerId { get; set; }
    public Guid? FitnessUserId { get; set; }
    public DateTime TestDate { get; set; }
    public decimal? Height { get; set; }
    public decimal? Weight { get; set; }
    public decimal? Bmi { get; set; }
    public decimal? BodyFatPct { get; set; }
    public decimal? SpeedTestResult { get; set; }
    public decimal? EnduranceScore { get; set; }
    public string? CustomTestName { get; set; }
    public decimal? CustomTestResult { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team Team { get; set; } = null!;
    public PlayerProfile Player { get; set; } = null!;
    public User? FitnessUser { get; set; }
    public User? CreatedByUser { get; set; }
    public User? UpdatedByUser { get; set; }
}
