namespace SportsPlatform.Auth.Core.DTOs.Response;

public class AttendanceDto
{
    public Guid AttendanceId { get; set; }
    public Guid EventId { get; set; }
    public DateOnly InstanceDate { get; set; }
    public Guid PlayerId { get; set; }
    public Guid PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public Guid? RecordedByUserId { get; set; }
    public string? RecordedByName { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? Notes { get; set; }
}
