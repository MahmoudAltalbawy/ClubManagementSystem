using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Interfaces;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Route("users")]
[Authorize]
public class UserController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _fileStorage;

    public UserController(AppDbContext db, IFileStorageService fileStorage)
    {
        _db = db;
        _fileStorage = fileStorage;
    }

    [HttpPut("me")]
    public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId.Value);
        if (user == null) return NotFound(new { error = "User not found." });

        if (request.Name != null) user.Name = request.Name;
        if (request.Username != null) user.Username = request.Username;
        if (request.Bio != null) user.Bio = request.Bio;
        if (request.Dob.HasValue) user.Dob = request.Dob;
        if (request.PhoneNumber != null) user.PhoneNumber = request.PhoneNumber;
        if (request.YearsOfExperience.HasValue) user.YearsOfExperience = request.YearsOfExperience;
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        return Ok(new UserInfoDto
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
        });
    }

    [HttpPost("me/profile-image")]
    public async Task<IActionResult> UploadProfileImage([FromForm] IFormFile? image)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (image == null || image.Length == 0)
            return BadRequest(new { error = "Profile image is required." });

        var user = await _db.Users.FirstOrDefaultAsync(u => u.UserId == userId.Value)
            ?? throw new InvalidOperationException("User not found.");

        var oldImage = user.ProfileImageUrl;
        user.ProfileImageUrl = await _fileStorage.SaveFileAsync(
            image.OpenReadStream(),
            image.FileName,
            "users");
        user.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync();

        if (!string.IsNullOrWhiteSpace(oldImage))
            await _fileStorage.DeleteFileAsync(oldImage);

        return Ok(new { profileImageUrl = user.ProfileImageUrl });
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}

