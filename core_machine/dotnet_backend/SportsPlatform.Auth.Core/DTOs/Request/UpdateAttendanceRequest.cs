using System.ComponentModel.DataAnnotations;
using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class UpdateAttendanceRequest
{
    [Required]
    public DateOnly InstanceDate { get; set; }

    [Required]
    public AttendanceStatus Status { get; set; }

    public string? Notes { get; set; }
}
