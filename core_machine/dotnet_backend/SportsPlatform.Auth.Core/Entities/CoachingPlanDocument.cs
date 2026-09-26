namespace SportsPlatform.Auth.Core.Entities;

public class CoachingPlanDocument
{
    public Guid DocumentId { get; set; }
    public Guid PlanId { get; set; }
    public Guid UploadedByUserId { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string UploadedByRole { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string StoragePath { get; set; } = string.Empty;
    public DateTime? DeletedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public CoachingPlan Plan { get; set; } = null!;
    public User UploadedByUser { get; set; } = null!;
}
