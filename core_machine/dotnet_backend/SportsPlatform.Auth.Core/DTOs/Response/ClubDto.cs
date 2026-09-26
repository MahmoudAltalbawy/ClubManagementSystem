namespace SportsPlatform.Auth.Core.DTOs.Response;

public class ClubDto
{
    public Guid ClubId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? LogoUrl { get; set; }
    public string? Location { get; set; }
    public double? LocationLatitude { get; set; }
    public double? LocationLongitude { get; set; }
    public Guid ManagerUserId { get; set; }
    public string ManagerName { get; set; } = string.Empty;
    public int MemberCount { get; set; }
    public int TeamCount { get; set; }
    public DateTime CreatedAt { get; set; }
}
