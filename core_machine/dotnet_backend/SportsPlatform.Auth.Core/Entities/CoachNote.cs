namespace SportsPlatform.Auth.Core.Entities;

/// <summary>
/// A coach note attached to a game/event, shown in Game History Details.
/// Persisted so notes survive reopening, with real author identity.
/// </summary>
public class CoachNote
{
    public Guid NoteId { get; set; }
    public Guid EventId { get; set; }
    public Guid TeamId { get; set; }
    public Guid AuthorUserId { get; set; }
    public string AuthorRole { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Event Event { get; set; } = null!;
    public User AuthorUser { get; set; } = null!;
}
