using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class RecordAttendanceRequest
{
    [Required]
    public DateOnly InstanceDate { get; set; }

    [Required]
    public List<AttendanceEntryRequest> Records { get; set; } = new();
}
