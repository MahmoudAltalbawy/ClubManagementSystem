using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateClubRequest
{
    [Required]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? LogoUrl { get; set; }

    [MaxLength(200)]
    public string? Location { get; set; }

    [Range(-90, 90)]
    public double? LocationLatitude { get; set; }

    [Range(-180, 180)]
    public double? LocationLongitude { get; set; }
}
