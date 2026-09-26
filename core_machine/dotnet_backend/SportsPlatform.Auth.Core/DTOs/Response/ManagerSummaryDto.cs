namespace SportsPlatform.Auth.Core.DTOs.Response;

/// <summary>
/// Lightweight manager info returned inside TeamDto.
/// </summary>
public class ManagerSummaryDto
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Email { get; set; }
}
