using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class UpsertPlayerProfileRequest
{
    [Required]
    [MaxLength(50)]
    public string Position { get; set; } = string.Empty;

    [Required]
    [Range(0, 999)]
    public int? JerseyNumber { get; set; }

    [Range(0, 300)]
    public decimal? Height { get; set; }

    [Range(0, 500)]
    public decimal? Weight { get; set; }
}
