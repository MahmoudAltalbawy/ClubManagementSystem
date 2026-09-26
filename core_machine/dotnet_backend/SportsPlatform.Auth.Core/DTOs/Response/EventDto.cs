namespace SportsPlatform.Auth.Core.DTOs.Response;

public class EventDto
{
    public Guid EventId { get; set; }
    public Guid TeamId { get; set; }
    public Guid SeasonId { get; set; }
    public string SeasonLabel { get; set; } = string.Empty;
    public string TeamName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public DateTime StartAt { get; set; }
    public DateTime? EndAt { get; set; }
    public string Timezone { get; set; } = "UTC";
    public string? Location { get; set; }
    public double? LocationLatitude { get; set; }
    public double? LocationLongitude { get; set; }
    public string? Description { get; set; }
    public string? RecurrenceRule { get; set; }
    public DateTime? RecurrenceEndDate { get; set; }
    public string CreatorName { get; set; } = string.Empty;
    public List<EventExceptionDto> Exceptions { get; set; } = new();
}
