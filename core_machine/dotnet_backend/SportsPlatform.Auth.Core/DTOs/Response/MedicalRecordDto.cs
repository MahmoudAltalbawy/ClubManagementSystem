namespace SportsPlatform.Auth.Core.DTOs.Response;

public class MedicalRecordDto
{
    public Guid RecordId { get; set; }
    public Guid TeamId { get; set; }
    public Guid PlayerId { get; set; }
    public Guid PlayerUserId { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public Guid? DoctorUserId { get; set; }
    public string? DoctorName { get; set; }
    public DateTime RecordDate { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? InjuryType { get; set; }
    public string? Diagnosis { get; set; }
    public DateOnly? ExpectedReturnDate { get; set; }
    public string? RecoveryTips { get; set; }
    public bool IsCleared { get; set; }
    public List<MedicalDocumentRequestDto> DocumentRequests { get; set; } = new();
}

public class MedicalDocumentRequestDto
{
    public Guid RequestId { get; set; }
    public Guid RecordId { get; set; }
    public string DocumentName { get; set; } = string.Empty;
    public string? Note { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? RequestedByName { get; set; }
    public string? UploadedByName { get; set; }
    public string? OriginalFileName { get; set; }
    public string? ContentType { get; set; }
    public long? FileSizeBytes { get; set; }
    public DateTime RequestedAt { get; set; }
    public DateTime? UploadedAt { get; set; }
    public string? DownloadUrl { get; set; }
}

public class MedicalDocumentDownloadDto
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = "application/octet-stream";
}
