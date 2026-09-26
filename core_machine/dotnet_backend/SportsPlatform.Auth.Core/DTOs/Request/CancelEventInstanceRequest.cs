using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CancelEventInstanceRequest
{
    [Required]
    public DateOnly OriginalDate { get; set; }

    public string? Notes { get; set; }
}
