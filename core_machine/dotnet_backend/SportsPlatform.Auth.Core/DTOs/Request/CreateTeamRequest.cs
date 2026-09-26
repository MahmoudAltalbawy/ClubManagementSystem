using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateTeamRequest
{
    [Required]
    [MaxLength(100)]
    public string TeamName { get; set; } = string.Empty;

    [Required]
    public Guid CategoryId { get; set; }

    [Required]
    [MaxLength(50)]
    public string SeasonLabel { get; set; } = string.Empty;

    [Required]
    public DateOnly? SeasonStartDate { get; set; }

    [Required]
    public DateOnly? SeasonEndDate { get; set; }

    public string? ImageUrl { get; set; }
}
