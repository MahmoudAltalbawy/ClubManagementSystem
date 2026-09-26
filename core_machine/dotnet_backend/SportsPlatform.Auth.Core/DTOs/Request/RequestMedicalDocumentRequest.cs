using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class RequestMedicalDocumentRequest
{
    [Required]
    [MaxLength(200)]
    public string DocumentName { get; set; } = string.Empty;

    public string? Note { get; set; }
}
