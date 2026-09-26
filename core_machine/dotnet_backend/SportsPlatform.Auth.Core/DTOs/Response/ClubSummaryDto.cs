namespace SportsPlatform.Auth.Core.DTOs.Response;

public class ClubSummaryDto
{
    public Guid ClubId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? Location { get; set; }
    public double? LocationLatitude { get; set; }
    public double? LocationLongitude { get; set; }
    public string MyRole { get; set; } = string.Empty;
}
