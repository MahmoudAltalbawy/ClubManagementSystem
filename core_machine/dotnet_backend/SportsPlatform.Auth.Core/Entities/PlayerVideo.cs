namespace SportsPlatform.Auth.Core.Entities;

/// <summary>
/// A performance/analysis video uploaded for a specific player and stored on the
/// server (served back for in-app playback). A player can have many videos.
/// Shown in the Videos section of the Player Profile.
/// </summary>
public class PlayerVideo
{
    public Guid VideoId { get; set; }

    /// <summary>The player (user) this video belongs to.</summary>
    public Guid PlayerUserId { get; set; }

    /// <summary>Team context the video was uploaded under (for scoping/permissions).</summary>
    public Guid TeamId { get; set; }

    public Guid AddedByUserId { get; set; }
    public string AddedByRole { get; set; } = string.Empty;

    /// <summary>Optional caption/title for the video.</summary>
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

    public User PlayerUser { get; set; } = null!;
    public User AddedByUser { get; set; } = null!;
}
