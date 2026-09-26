namespace SportsPlatform.Auth.Core.Entities;

public class EventException
{
    public Guid EventExceptionId { get; set; }
    public Guid EventId { get; set; }
    public DateOnly OriginalDate { get; set; }
    public DateTime? NewStartAt { get; set; }
    public DateTime? NewEndAt { get; set; }
    public bool IsCancelled { get; set; }
    public string? Notes { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Event Event { get; set; } = null!;
    public User Creator { get; set; } = null!;
}
