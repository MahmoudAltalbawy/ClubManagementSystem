using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class UpdateMedicalRecordRequest
{
    public DateTime? RecordDate { get; set; }

    [MaxLength(200)]
    public string? InjuryType { get; set; }

    public string? Diagnosis { get; set; }

    public DateOnly? ExpectedReturnDate { get; set; }

    public string? RecoveryTips { get; set; }
}
