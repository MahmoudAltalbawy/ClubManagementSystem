using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class UpdateMedicalClearanceRequest
{
    [Required]
    public bool IsCleared { get; set; }
}
