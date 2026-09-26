using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

public class RegisterRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(6)]
    public string Password { get; set; } = string.Empty;

    [Required]
    [MinLength(2)]
    public string Name { get; set; } = string.Empty;

    [RegularExpression(@"^[a-zA-Z0-9_]{3,50}$", ErrorMessage = "Username must be 3-50 characters: letters, digits, underscore.")]
    public string? Username { get; set; }

    /// <summary>
    /// Optional phone in E.164 format: + followed by 7-15 digits (e.g. +201012345678).
    /// </summary>
    [RegularExpression(@"^\+[1-9]\d{6,14}$", ErrorMessage = "Phone must be in E.164 format (e.g. +201012345678).")]
    public string? PhoneNumber { get; set; }

    [MaxLength(500)]
    public string? Bio { get; set; }

    [Required]
    public DateOnly? Dob { get; set; }
}
