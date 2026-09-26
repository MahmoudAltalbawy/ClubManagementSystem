using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateFitnessRecordRequest
{
    public DateTime? TestDate { get; set; }

    [Range(0, 300)]
    public decimal? Height { get; set; }

    [Range(0, 500)]
    public decimal? Weight { get; set; }

    public decimal? Bmi { get; set; }
    public decimal? BodyFatPct { get; set; }
    public decimal? SpeedTestResult { get; set; }
    public decimal? EnduranceScore { get; set; }

    [MaxLength(100)]
    public string? CustomTestName { get; set; }

    public decimal? CustomTestResult { get; set; }
}
