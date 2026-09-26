namespace SportsPlatform.Auth.Core.Entities;

public class PlayerProfile
{
    public Guid PlayerId { get; set; }
    public Guid UserId { get; set; }
    public string? Position { get; set; }
    public int? JerseyNumber { get; set; }
    public decimal? Height { get; set; }
    public decimal? Weight { get; set; }
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = null!;
    public ICollection<Attendance> Attendances { get; set; } = new List<Attendance>();
}
