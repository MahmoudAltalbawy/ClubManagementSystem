using System.ComponentModel.DataAnnotations;
using SportsPlatform.Auth.Core.Enums;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class CreateInvitationRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public RoleNameType RoleName { get; set; }

    [MaxLength(50)]
    public string? PlayerPosition { get; set; }

    [Range(0, 999)]
    public int? JerseyNumber { get; set; }
}
