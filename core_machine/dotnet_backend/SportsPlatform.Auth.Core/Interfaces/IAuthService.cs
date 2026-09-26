using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;

namespace SportsPlatform.Auth.Core.Interfaces;

public interface IAuthService
{
    Task<AuthResponse> RegisterLocalAsync(RegisterRequest request);
    Task<AuthResponse> LoginLocalAsync(LoginRequest request);
    Task<AuthResponse> HandleGoogleLoginAsync(string email, string googleId, string name, string? phone);
    Task<AuthResponse> CompleteGoogleProfileAsync(Guid userId, CompleteGoogleProfileRequest request);
    Task<AuthResponse> RefreshTokenAsync(string refreshToken);
    Task RevokeRefreshTokenAsync(string refreshToken);

    /// <summary>
    /// Start a "forgot password" reset for the given email. Generates a 6-digit
    /// OTP and emails it. Returns silently whether or not the account exists, to
    /// avoid leaking which emails are registered.
    /// </summary>
    Task RequestPasswordResetAsync(string email);

    /// <summary>Verify a reset OTP without consuming it (used to gate the new-password screen).</summary>
    Task<bool> VerifyResetCodeAsync(string email, string code);

    /// <summary>Complete a reset using the OTP code, setting a new password.</summary>
    Task ResetPasswordAsync(ResetPasswordRequest request);
}
