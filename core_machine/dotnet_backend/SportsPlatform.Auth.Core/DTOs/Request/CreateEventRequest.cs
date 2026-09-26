using System.ComponentModel.DataAnnotations;
using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateEventRequest
{
    [Required]
    public Guid SeasonId { get; set; }

    [Required, MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    [Required]
    public EventType EventType { get; set; }

    [Required]
    public DateTime StartAt { get; set; }

    public DateTime? EndAt { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [Range(-90, 90)]
    public double? LocationLatitude { get; set; }

    [Range(-180, 180)]
    public double? LocationLongitude { get; set; }

    public string? Description { get; set; }

    [MaxLength(100)]
    public string? Timezone { get; set; } = "UTC";

    public string? RecurrenceRule { get; set; }

    public DateTime? RecurrenceEndDate { get; set; }
}
