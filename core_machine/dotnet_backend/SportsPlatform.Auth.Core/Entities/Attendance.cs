using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.Entities;

public class Attendance
{
    public Guid AttendanceId { get; set; }
    public Guid EventId { get; set; }
    public DateOnly InstanceDate { get; set; }
    public Guid PlayerId { get; set; }
    public Guid? RecordedByUserId { get; set; }
    public AttendanceStatus Status { get; set; }
    public DateTime RecordedAt { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Event Event { get; set; } = null!;
    public PlayerProfile Player { get; set; } = null!;
    public User? RecordedByUser { get; set; }
}
