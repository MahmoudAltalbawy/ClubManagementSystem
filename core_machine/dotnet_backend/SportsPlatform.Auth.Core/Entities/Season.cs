namespace SportsPlatform.Auth.Core.Entities;

public class Season
{
    public Guid SeasonId { get; set; }
    public Guid? TeamId { get; set; }
    public Guid? CreatedBy { get; set; }
    public string Label { get; set; } = string.Empty;
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    public bool IsCurrent { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team? Team { get; set; }
    public User? Creator { get; set; }
    public ICollection<Event> Events { get; set; } = new List<Event>();
}
