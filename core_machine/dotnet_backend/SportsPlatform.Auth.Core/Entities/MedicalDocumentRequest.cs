namespace SportsPlatform.Auth.Core.Entities;

public class MedicalDocumentRequest
{
    public Guid RequestId { get; set; }
    public Guid RecordId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string Status { get; set; } = "Pending";
    public Guid RequestedBy { get; set; }
    public Guid? UploadedBy { get; set; }
    public string? OriginalFileName { get; set; }
    public string? StoredFileName { get; set; }
    public string? ContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? UploadedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public MedicalRecord Record { get; set; } = null!;
    public User RequestedByUser { get; set; } = null!;
    public User? UploadedByUser { get; set; }
}
