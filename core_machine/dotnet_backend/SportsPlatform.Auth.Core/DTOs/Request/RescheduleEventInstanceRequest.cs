using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class RescheduleEventInstanceRequest
{
    [Required]
    public DateOnly OriginalDate { get; set; }

    [Required]
    public DateTime NewStartAt { get; set; }

    public DateTime? NewEndAt { get; set; }

    public string? Notes { get; set; }
}
