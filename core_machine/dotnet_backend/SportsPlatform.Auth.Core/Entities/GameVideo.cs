namespace SportsPlatform.Auth.Core.Entities;

/// <summary>
/// A game video uploaded to an event and stored on the server (served back for
/// in-app playback). Shown in the GAME VIDEOS section of Game History Details.
/// </summary>
public class GameVideo
{
    public Guid VideoId { get; set; }
    public Guid EventId { get; set; }
    public Guid TeamId { get; set; }
    public Guid AddedByUserId { get; set; }
    public string AddedByRole { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>Server-side stored file name (a GUID + extension).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Original file name as uploaded by the user.</summary>
    public string OriginalFileName { get; set; } = string.Empty;

    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }

    /// <summary>Absolute path to the stored file on the server disk.</summary>
    public string StoragePath { get; set; } = string.Empty;

    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Event Event { get; set; } = null!;
    public User AddedByUser { get; set; } = null!;
}
