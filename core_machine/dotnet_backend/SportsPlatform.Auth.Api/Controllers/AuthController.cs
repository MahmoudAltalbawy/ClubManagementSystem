using System.Security.Claims;
using Google.Apis.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Route("auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IConfiguration _config;

    public AuthController(IAuthService authService, IConfiguration config)
    {
        _authService = authService;
        _config = config;
    }

    /// <summary>
    /// Register a new local user (email + password).
    /// Returns tokens immediately after account creation.
    /// </summary>
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request)
    {
        var result = await _authService.RegisterLocalAsync(request);
        return Ok(result);
    }

    /// <summary>
    /// Login with email + password.
    /// Returns membership-scoped claims and refresh tokens.
    /// </summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request)
    {
        var result = await _authService.LoginLocalAsync(request);
        return Ok(result);
    }

    /// <summary>
    /// Start the shared Google OAuth sign-in flow. The backend redirects to
    /// Google, then redirects to returnUrl with Equipex auth tokens.
    /// </summary>
    [HttpGet("google")]
    public IActionResult GoogleLogin([FromQuery] string? returnUrl = null)
    {
        if (!IsAllowedAuthReturnUrl(returnUrl))
            return BadRequest(new { error = "Invalid Google auth return URL." });

        var properties = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(GoogleAuthResult))
        };
        if (!string.IsNullOrWhiteSpace(returnUrl))
            properties.Items["returnUrl"] = returnUrl;

        return Challenge(properties, GoogleDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// Final OAuth endpoint after middleware processes Google's callback.
    /// </summary>
    [HttpGet("google/result")]
    public async Task<IActionResult> GoogleAuthResult()
    {
        var authenticateResult = await HttpContext.AuthenticateAsync(GoogleDefaults.AuthenticationScheme);
        var returnUrl = authenticateResult.Properties?.Items["returnUrl"];

        if (!authenticateResult.Succeeded)
            return Redirect(BuildGoogleConsoleRedirect(new AuthResponse
            {
                Message = "Google authentication failed."
            }, StatusCodes.Status401Unauthorized, returnUrl));

        var claims = authenticateResult.Principal?.Claims;
        var email = claims?.FirstOrDefault(c => c.Type == ClaimTypes.Email)?.Value;
        var googleId = claims?.FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value;
        var name = claims?.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value ?? "";
        var phone = claims?.FirstOrDefault(c => c.Type == ClaimTypes.MobilePhone)?.Value;

        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(googleId))
            return Redirect(BuildGoogleConsoleRedirect(new AuthResponse
            {
                Message = "Could not retrieve email or Google ID."
            }, StatusCodes.Status400BadRequest, returnUrl));

        var result = await _authService.HandleGoogleLoginAsync(email, googleId, name, phone);
        return Redirect(BuildGoogleConsoleRedirect(result, StatusCodes.Status200OK, returnUrl));
    }

    /// <summary>
    /// Native Flutter Google sign-in. The app obtains a Google ID token in-app,
    /// then the backend verifies it against Google:ClientId before issuing
    /// Equipex tokens.
    /// </summary>
    [HttpPost("google/mobile")]
    public async Task<IActionResult> GoogleMobileLogin([FromBody] GoogleMobileLoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IdToken))
            return BadRequest(new { error = "Google ID token is required." });

        var clientId = _config["Google:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId) || clientId == "PLACEHOLDER_CLIENT_ID")
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "Google ClientId is not configured." });

        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(
                request.IdToken,
                new GoogleJsonWebSignature.ValidationSettings
                {
                    Audience = [clientId]
                });

            var name = string.IsNullOrWhiteSpace(payload.Name)
                ? payload.Email
                : payload.Name;
            var result = await _authService.HandleGoogleLoginAsync(
                payload.Email,
                payload.Subject,
                name,
                null);

            return Ok(result);
        }
        catch (InvalidJwtException)
        {
            return Unauthorized(new { error = "Invalid Google ID token." });
        }
    }

    /// <summary>
    /// Complete Google sign-up profile (name + DOB).
    /// Phone and email are already known from Google.
    /// Requires a temporary JWT from the Google callback.
    /// </summary>
    [Authorize]
    [HttpPost("complete-google-profile")]
    public async Task<IActionResult> CompleteGoogleProfile([FromBody] CompleteGoogleProfileRequest request)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userIdClaim) || !Guid.TryParse(userIdClaim, out var userId))
            return Unauthorized(new { error = "Invalid token." });

        var result = await _authService.CompleteGoogleProfileAsync(userId, request);
        return Ok(result);
    }

    /// <summary>
    /// Refresh JWT using a valid refresh token.
    /// </summary>
    [HttpPost("refresh")]
    public async Task<IActionResult> RefreshToken([FromBody] RefreshTokenRequest request)
    {
        var result = await _authService.RefreshTokenAsync(request.RefreshToken);
        return Ok(result);
    }

    /// <summary>
    /// Start a "forgot password" reset. Emails a 6-digit OTP.
    /// Always returns 200 so the response doesn't reveal whether the email exists.
    /// </summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request)
    {
        await _authService.RequestPasswordResetAsync(request.Email);
        return Ok(new { message = "If an account exists for that email, a reset code has been sent." });
    }

    /// <summary>
    /// Verify a reset OTP without consuming it (gates the new-password screen).
    /// </summary>
    [HttpPost("verify-reset-code")]
    public async Task<IActionResult> VerifyResetCode([FromBody] VerifyResetCodeRequest request)
    {
        var valid = await _authService.VerifyResetCodeAsync(request.Email, request.Code);
        if (!valid)
            return BadRequest(new { error = "Invalid or expired code." });
        return Ok(new { message = "Code verified." });
    }

    /// <summary>
    /// Complete a reset using the OTP code, setting a new password.
    /// </summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
    {
        try
        {
            await _authService.ResetPasswordAsync(request);
            return Ok(new { message = "Your password has been reset. You can now log in." });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Logout - revoke the current refresh token.
    /// </summary>
    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] RefreshTokenRequest request)
    {
        await _authService.RevokeRefreshTokenAsync(request.RefreshToken);
        return Ok(new { message = "Logged out successfully." });
    }

    private string BuildGoogleConsoleRedirect(AuthResponse response, int statusCode, string? returnUrl = null)
    {
        var baseUri = string.IsNullOrWhiteSpace(returnUrl)
            ? UriHelper.BuildAbsolute(Request.Scheme, Request.Host, Request.PathBase, "/")
            : returnUrl;
        var query = new Dictionary<string, string?>
        {
            ["status"] = statusCode.ToString(),
            ["message"] = response.Message,
            ["requiresProfileCompletion"] = response.RequiresProfileCompletion.ToString().ToLowerInvariant(),
            ["accessToken"] = response.AccessToken,
            ["refreshToken"] = response.RefreshToken,
            ["email"] = response.User?.Email,
            ["name"] = response.User?.Name,
            ["userId"] = response.User?.UserId.ToString()
        };

        return QueryHelpers.AddQueryString(baseUri, query);
    }

    private bool IsAllowedAuthReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
            return true;

        if (!Uri.TryCreate(returnUrl, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme == "equipex" && uri.Host == "auth")
            return true;

        if ((uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
            (uri.Host == "localhost" || uri.Host == "127.0.0.1" || uri.Host == Request.Host.Host))
            return true;

        var allowedPrefixes = _config.GetSection("App:AllowedAuthReturnUrlPrefixes").Get<string[]>() ?? [];
        return allowedPrefixes.Any(prefix =>
            returnUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }
}
