using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.Entities;

public class Event
{
    public Guid EventId { get; set; }
    public Guid TeamId { get; set; }
    public Guid SeasonId { get; set; }
    public Guid CreatedBy { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Location { get; set; }
    public double? LocationLatitude { get; set; }
    public double? LocationLongitude { get; set; }
    public DateTime StartAt { get; set; }
    public DateTime? EndAt { get; set; }
    public EventType EventType { get; set; }
    public string Timezone { get; set; } = "UTC";
    public string? RecurrenceRule { get; set; }
    public DateTime? RecurrenceEndDate { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team Team { get; set; } = null!;
    public Season Season { get; set; } = null!;
    public User Creator { get; set; } = null!;
    public ICollection<EventException> Exceptions { get; set; } = new List<EventException>();
    public ICollection<Attendance> Attendances { get; set; } = new List<Attendance>();
}
