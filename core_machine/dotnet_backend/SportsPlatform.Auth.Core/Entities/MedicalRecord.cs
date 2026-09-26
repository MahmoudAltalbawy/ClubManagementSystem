namespace SportsPlatform.Auth.Core.Entities;

public class MedicalRecord
{
    public Guid RecordId { get; set; }
    public Guid TeamId { get; set; }
    public Guid PlayerId { get; set; }
    public Guid? DoctorUserId { get; set; }
    public DateTime RecordDate { get; set; }
    public string? InjuryType { get; set; }
    public string? Diagnosis { get; set; }
    public DateOnly? ExpectedReturnDate { get; set; }
    public string? RecoveryTips { get; set; }
    public bool IsCleared { get; set; }
    public Guid? CreatedBy { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Team Team { get; set; } = null!;
    public PlayerProfile Player { get; set; } = null!;
    public User? DoctorUser { get; set; }
    public User? CreatedByUser { get; set; }
    public User? UpdatedByUser { get; set; }
    public ICollection<MedicalDocumentRequest> DocumentRequests { get; set; } = new List<MedicalDocumentRequest>();
}
