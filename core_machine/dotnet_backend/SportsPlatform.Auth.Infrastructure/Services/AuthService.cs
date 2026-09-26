using System.Security.Cryptography;
using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;
using BCryptNet = BCrypt.Net.BCrypt;

namespace SportsPlatform.Auth.Infrastructure.Services;

public class AuthService : IAuthService
{
    // Password reset challenge lifetime, resend cooldown, and lockout settings.
    private static readonly TimeSpan ResetCodeLifetime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ResetCodeResendCooldown = TimeSpan.FromMinutes(1);
    private const int MaxResetAttempts = 5;
    private static readonly ConcurrentDictionary<string, PasswordResetChallenge> PasswordResetChallenges =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly AppDbContext _db;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly ILogger<AuthService> _logger;

    public AuthService(
        AppDbContext db,
        ITokenService tokenService,
        IEmailService emailService,
        ILogger<AuthService> logger)
    {
        _db = db;
        _tokenService = tokenService;
        _emailService = emailService;
        _logger = logger;
    }

    public async Task<AuthResponse> RegisterLocalAsync(RegisterRequest request)
    {
        if (!request.Dob.HasValue)
            throw new InvalidOperationException("Date of birth is required.");

        var emailExists = await _db.Users.AnyAsync(u => u.Email == request.Email);
        if (emailExists)
            throw new InvalidOperationException("An account with this email already exists.");

        if (!string.IsNullOrWhiteSpace(request.Username))
        {
            var usernameExists = await _db.Users.AnyAsync(u => u.Username == request.Username);
            if (usernameExists)
                throw new InvalidOperationException("This username is already taken.");
        }

        var now = DateTime.UtcNow;

        var user = new User
        {
            UserId = Guid.NewGuid(),
            Email = request.Email,
            Username = request.Username,
            Name = request.Name,
            PhoneNumber = request.PhoneNumber,
            Bio = request.Bio?.Trim(),
            Dob = request.Dob,
            IsAdmin = false,
            CreatedAt = now,
            UpdatedAt = now
        };

        _db.Users.Add(user);

        _db.UserAuthProviders.Add(new UserAuthProvider
        {
            Id = Guid.NewGuid(),
            UserId = user.UserId,
            Provider = AuthProviderType.Local,
            PasswordHash = BCryptNet.HashPassword(request.Password),
            IsVerified = true,
            CreatedAt = now,
            UpdatedAt = now
        });

        await _db.SaveChangesAsync();

        return await BuildAuthenticatedResponseAsync(user);
    }

    public async Task<AuthResponse> LoginLocalAsync(LoginRequest request)
    {
        var identifier = request.EmailOrPhone.Trim();

        // Determine whether the user is logging in with a phone number or email.
        var isPhone = identifier.StartsWith('+') || identifier.All(c => char.IsDigit(c) || c == '+');

        var user = isPhone
            ? await _db.Users
                .Include(u => u.AuthProviders)
                .FirstOrDefaultAsync(u => u.PhoneNumber == identifier)
            : await _db.Users
                .Include(u => u.AuthProviders)
                .FirstOrDefaultAsync(u => u.Email == identifier);

        if (user == null)
            throw new UnauthorizedAccessException("Invalid credentials.");

        var localAuth = user.AuthProviders.FirstOrDefault(a => a.Provider == AuthProviderType.Local);
        if (localAuth == null || string.IsNullOrEmpty(localAuth.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials.");

        if (!BCryptNet.Verify(request.Password, localAuth.PasswordHash))
            throw new UnauthorizedAccessException("Invalid credentials.");

        return await BuildAuthenticatedResponseAsync(user);
    }

    public async Task<AuthResponse> HandleGoogleLoginAsync(string email, string googleId, string name, string? phone)
    {
        var existingAuth = await _db.UserAuthProviders
            .Include(a => a.User)
            .FirstOrDefaultAsync(a =>
                a.Provider == AuthProviderType.Google &&
                a.ProviderUserId == googleId);

        if (existingAuth != null)
        {
            var existingUser = existingAuth.User;

            if (!existingAuth.IsVerified)
            {
                return await BuildProfileCompletionResponseAsync(existingUser);
            }

            return await BuildAuthenticatedResponseAsync(existingUser);
        }

        var userByEmail = await _db.Users
            .Include(u => u.AuthProviders)
            .FirstOrDefaultAsync(u => u.Email == email);

        var now = DateTime.UtcNow;
        var user = userByEmail ?? new User
        {
            UserId = Guid.NewGuid(),
            Email = email,
            Name = string.IsNullOrWhiteSpace(name) ? email : name,
            PhoneNumber = phone,
            CreatedAt = now,
            UpdatedAt = now
        };

        if (userByEmail == null)
        {
            _db.Users.Add(user);
        }
        else
        {
            user.UpdatedAt = now;
            if (string.IsNullOrWhiteSpace(user.Name) && !string.IsNullOrWhiteSpace(name))
                user.Name = name;
            if (string.IsNullOrWhiteSpace(user.PhoneNumber) && !string.IsNullOrWhiteSpace(phone))
                user.PhoneNumber = phone;
        }

        var needsProfileCompletion = string.IsNullOrWhiteSpace(user.Name) || !user.Dob.HasValue;

        _db.UserAuthProviders.Add(new UserAuthProvider
        {
            Id = Guid.NewGuid(),
            UserId = user.UserId,
            Provider = AuthProviderType.Google,
            ProviderUserId = googleId,
            ProviderIdentifier = email,
            IsVerified = !needsProfileCompletion,
            CreatedAt = now,
            UpdatedAt = now
        });

        await _db.SaveChangesAsync();

        if (needsProfileCompletion)
            return await BuildProfileCompletionResponseAsync(user);

        return await BuildAuthenticatedResponseAsync(user);
    }

    public async Task<AuthResponse> CompleteGoogleProfileAsync(Guid userId, CompleteGoogleProfileRequest request)
    {
        var user = await _db.Users
            .Include(u => u.AuthProviders)
            .FirstOrDefaultAsync(u => u.UserId == userId);

        if (user == null)
            throw new InvalidOperationException("User not found.");

        user.Name = request.Name;
        user.Dob = request.Dob;
        user.UpdatedAt = DateTime.UtcNow;

        var googleAuth = user.AuthProviders.FirstOrDefault(a => a.Provider == AuthProviderType.Google);
        if (googleAuth != null)
        {
            googleAuth.IsVerified = true;
            googleAuth.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();

        return await BuildAuthenticatedResponseAsync(user);
    }

    public async Task<AuthResponse> RefreshTokenAsync(string refreshToken)
    {
        var storedToken = await _tokenService.ValidateRefreshTokenAsync(refreshToken);
        if (storedToken == null)
            throw new UnauthorizedAccessException("Invalid or expired refresh token.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == storedToken.UserId);
        if (user == null)
            throw new UnauthorizedAccessException("User not found.");

        await _tokenService.RevokeRefreshTokenAsync(refreshToken);

        return await BuildAuthenticatedResponseAsync(user, "Token refreshed.");
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken)
    {
        await _tokenService.RevokeRefreshTokenAsync(refreshToken);
    }

    public async Task RequestPasswordResetAsync(string email)
    {
        var normalized = NormalizeEmail(email);

        var user = await _db.Users
            .Include(u => u.AuthProviders)
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalized);

        // Silently no-op for unknown emails so we don't reveal which emails
        // are registered. Existing Google-only accounts are allowed through:
        // ResetPasswordAsync will create the local password provider after the
        // user proves email ownership with this code.
        if (user == null)
        {
            _logger.LogInformation(
                "Password reset email skipped for {Email}: account was not found.",
                normalized);
            return;
        }

        var now = DateTime.UtcNow;

        if (PasswordResetChallenges.TryGetValue(normalized, out var current) &&
            current.LastSentAt.Add(ResetCodeResendCooldown) > now)
        {
            _logger.LogInformation(
                "Password reset email skipped for {Email}: resend cooldown is still active.",
                normalized);
            return;
        }

        var code = GenerateNumericOtp();

        PasswordResetChallenges[normalized] = new PasswordResetChallenge
        {
            UserId = user.UserId,
            CodeHash = BCryptNet.HashPassword(code),
            AttemptCount = 0,
            ExpiresAt = now.Add(ResetCodeLifetime),
            LastSentAt = now,
        };

        var minutes = (int)ResetCodeLifetime.TotalMinutes;

        var subject = "Reset your Equipex password";
        var body =
            $"We received a request to reset your password. " +
            $"Your verification code is {code}. It expires in {minutes} minutes. " +
            $"Enter this code in the Equipex app to choose a new password. " +
            $"If you didn't request this, you can ignore this email.";

        try
        {
            await _emailService.SendNotificationEmailAsync(user.Email, subject, body);
            _logger.LogInformation(
                "Password reset code email sent for user {UserId} to {Email}.",
                user.UserId,
                normalized);
        }
        catch
        {
            PasswordResetChallenges.TryRemove(normalized, out _);
            throw;
        }
    }

    public async Task<bool> VerifyResetCodeAsync(string email, string code)
    {
        var normalized = NormalizeEmail(email);
        var now = DateTime.UtcNow;

        var userId = await _db.Users
            .Where(u => u.Email.ToLower() == normalized)
            .Select(u => u.UserId)
            .FirstOrDefaultAsync();
        if (userId == Guid.Empty) return false;

        return TryVerifyPasswordResetChallenge(normalized, userId, code, now);
    }

    public async Task ResetPasswordAsync(ResetPasswordRequest request)
    {
        var normalized = NormalizeEmail(request.Email);
        var now = DateTime.UtcNow;

        var user = await _db.Users
            .Include(u => u.AuthProviders)
            .FirstOrDefaultAsync(u => u.Email.ToLower() == normalized);
        if (user == null)
            throw new InvalidOperationException("Invalid or expired reset request.");

        if (string.IsNullOrWhiteSpace(request.Code))
            throw new InvalidOperationException("A verification code is required.");

        if (!TryVerifyPasswordResetChallenge(normalized, user.UserId, request.Code, now))
            throw new InvalidOperationException("Invalid or expired reset request.");

        var localAuth = user.AuthProviders.FirstOrDefault(a => a.Provider == AuthProviderType.Local);
        if (localAuth == null)
        {
            localAuth = new UserAuthProvider
            {
                Id = Guid.NewGuid(),
                UserId = user.UserId,
                Provider = AuthProviderType.Local,
                IsVerified = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.UserAuthProviders.Add(localAuth);
        }

        localAuth.PasswordHash = BCryptNet.HashPassword(request.NewPassword);
        localAuth.UpdatedAt = now;

        user.UpdatedAt = now;

        // Revoke existing sessions so a leaked/old session can't outlive the reset.
        await _tokenService.RevokeAllUserTokensAsync(user.UserId);

        await _db.SaveChangesAsync();
        PasswordResetChallenges.TryRemove(normalized, out _);
    }

    private static string GenerateNumericOtp()
    {
        // 6-digit, zero-padded, cryptographically random.
        var value = RandomNumberGenerator.GetInt32(0, 1_000_000);
        return value.ToString("D6");
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static bool TryVerifyPasswordResetChallenge(
        string normalizedEmail,
        Guid userId,
        string? code,
        DateTime now)
    {
        if (string.IsNullOrWhiteSpace(code))
            return false;

        if (!PasswordResetChallenges.TryGetValue(normalizedEmail, out var challenge))
            return false;

        if (challenge.UserId != userId || challenge.ExpiresAt <= now)
        {
            PasswordResetChallenges.TryRemove(normalizedEmail, out _);
            return false;
        }

        if (challenge.AttemptCount >= MaxResetAttempts)
            return false;

        var ok = BCryptNet.Verify(code.Trim(), challenge.CodeHash);
        if (!ok)
            challenge.AttemptCount++;

        return ok;
    }

    private async Task<AuthResponse> BuildAuthenticatedResponseAsync(User user, string message = "Login successful.")
    {
        var profile = await BuildUserProfileAsync(user);
        var accessToken = _tokenService.GenerateAccessToken(
            user,
            profile.Roles,
            profile.Clubs,
            profile.Teams,
            profile.IsAdmin);
        var refreshToken = await _tokenService.GenerateRefreshTokenAsync(user.UserId);

        return new AuthResponse
        {
            Message = message,
            AccessToken = accessToken,
            RefreshToken = refreshToken.Token,
            ExpiresAt = refreshToken.ExpiresAt,
            RequiresProfileCompletion = false,
            User = profile
        };
    }

    private async Task<AuthResponse> BuildProfileCompletionResponseAsync(User user)
    {
        var profile = await BuildUserProfileAsync(user);
        var tempToken = _tokenService.GenerateAccessToken(
            user,
            profile.Roles,
            profile.Clubs,
            profile.Teams,
            profile.IsAdmin);

        return new AuthResponse
        {
            Message = "Please complete your profile to finish Google sign-in.",
            RequiresProfileCompletion = true,
            AccessToken = tempToken,
            User = profile
        };
    }

    private async Task<UserInfoDto> BuildUserProfileAsync(User user)
    {
        var ownedClubs = await _db.Clubs
            .Where(c => c.CreatedBy == user.UserId && c.DeletedAt == null)
            .Select(c => new UserClubInfoDto
            {
                ClubId = c.ClubId,
                ClubName = c.Name,
                Role = RoleNameType.ClubManager.ToString()
            })
            .ToListAsync();

        var ownedClubIds = ownedClubs.Select(c => c.ClubId).ToList();

        var clubMemberships = await _db.ClubMemberships
            .Include(cm => cm.Club)
            .Where(cm => cm.UserId == user.UserId && cm.Status == MembershipStatus.Active)
            .Select(cm => new UserClubInfoDto
            {
                ClubId = cm.ClubId,
                ClubName = cm.Club.Name,
                Role = cm.Role.ToString()
            })
            .ToListAsync();

        var teamMemberships = await _db.TeamMemberships
            .Include(tm => tm.Team)
            .Where(tm =>
                tm.UserId == user.UserId &&
                tm.Status == MembershipStatus.Active &&
                tm.Team.DeletedAt == null &&
                (!tm.Team.ClubId.HasValue || !ownedClubIds.Contains(tm.Team.ClubId.Value)))
            .Select(tm => new UserTeamInfoDto
            {
                TeamId = tm.TeamId,
                TeamName = tm.Team.TeamName,
                ClubId = tm.Team.ClubId ?? Guid.Empty,
                Role = tm.Role.ToString()
            })
            .ToListAsync();

        var ownedClubTeams = new List<UserTeamInfoDto>();
        if (ownedClubIds.Count > 0)
        {
            ownedClubTeams = await _db.Teams
                .Where(t =>
                    t.ClubId.HasValue &&
                    ownedClubIds.Contains(t.ClubId.Value) &&
                    t.DeletedAt == null)
                .Select(t => new UserTeamInfoDto
                {
                    TeamId = t.TeamId,
                    TeamName = t.TeamName,
                    ClubId = t.ClubId ?? Guid.Empty,
                    Role = RoleNameType.ClubManager.ToString()
                })
                .ToListAsync();
        }

        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (user.IsAdmin)
            roles.Add(RoleNameType.Admin.ToString());

        foreach (var club in ownedClubs)
            roles.Add(club.Role);

        foreach (var club in clubMemberships)
            roles.Add(club.Role);

        foreach (var team in teamMemberships)
            roles.Add(team.Role);

        return new UserInfoDto
        {
            UserId = user.UserId,
            Email = user.Email,
            Name = user.Name,
            Username = user.Username,
            PhoneNumber = user.PhoneNumber,
            Dob = user.Dob,
            Bio = user.Bio,
            YearsOfExperience = user.YearsOfExperience,
            ProfileImageUrl = user.ProfileImageUrl,
            IsAdmin = user.IsAdmin,
            Roles = roles.OrderBy(r => r).ToList(),
            Clubs = ownedClubs.Concat(clubMemberships)
                .GroupBy(c => new { c.ClubId, c.Role })
                .Select(g => g.First())
                .OrderBy(c => c.ClubName)
                .ToList(),
            Teams = ownedClubTeams
                .Concat(teamMemberships)
                .GroupBy(t => t.TeamId)
                .Select(g => g.First())
                .OrderBy(t => t.TeamName)
                .ToList()
        };
    }

    private sealed class PasswordResetChallenge
    {
        public Guid UserId { get; init; }
        public string CodeHash { get; init; } = string.Empty;
        public int AttemptCount { get; set; }
        public DateTime ExpiresAt { get; init; }
        public DateTime LastSentAt { get; init; }
    }
}
