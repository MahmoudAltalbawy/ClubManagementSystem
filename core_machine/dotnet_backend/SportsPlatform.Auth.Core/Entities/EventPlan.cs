namespace SportsPlatform.Auth.Core.Entities;

public class EventPlan
{
    public Guid EventPlanId { get; set; }
    public Guid EventId { get; set; }
    public Guid PlanId { get; set; }
    public Guid LinkedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }

    public Event Event { get; set; } = null!;
    public CoachingPlan Plan { get; set; } = null!;
    public User LinkedByUser { get; set; } = null!;
}
