using System.ComponentModel.DataAnnotations;

namespace SportsPlatform.Auth.Core.DTOs.Request;

/// <summary>Step 1 - request a password reset for an email address.</summary>
public class ForgotPasswordRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}

/// <summary>Step 2 - verify the 6-digit OTP before showing the new-password form.</summary>
public class VerifyResetCodeRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    public string Code { get; set; } = string.Empty;
}

/// <summary>Step 3 - complete the reset with the OTP and the new password.</summary>
public class ResetPasswordRequest
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    /// <summary>6-digit OTP from the email.</summary>
    [Required]
    public string Code { get; set; } = string.Empty;

    [Required]
    [MinLength(8)]
    public string NewPassword { get; set; } = string.Empty;
}
