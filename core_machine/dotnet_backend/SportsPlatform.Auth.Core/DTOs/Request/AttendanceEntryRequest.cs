using System.ComponentModel.DataAnnotations;
using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class AttendanceEntryRequest
{
    [Required]
    public Guid PlayerUserId { get; set; }

    [Required]
    public AttendanceStatus Status { get; set; }

    public string? Notes { get; set; }
}
