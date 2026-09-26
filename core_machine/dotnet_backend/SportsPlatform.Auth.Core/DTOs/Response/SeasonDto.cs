namespace SportsPlatform.Auth.Core.DTOs.Response;

public class SeasonDto
{
    public Guid SeasonId { get; set; }
    public Guid? TeamId { get; set; }
    public string? TeamName { get; set; }
    public Guid? CreatedBy { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsCurrent { get; set; }
}
