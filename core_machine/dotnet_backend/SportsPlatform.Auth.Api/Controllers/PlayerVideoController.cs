using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using SportsPlatform.Auth.Api.Services;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class PlayerVideoController : ControllerBase
{
    // 500 MB upload cap for player videos.
    private const long MaxVideoBytes = 500L * 1024 * 1024;

    private static readonly string[] AllowedVideoExtensions =
    {
        ".mp4", ".mov", ".m4v", ".webm", ".mkv", ".avi", ".3gp",
    };

    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;
    private readonly Mp4StreamingOptimizer _mp4Optimizer;

    public PlayerVideoController(
        AppDbContext db,
        IFileStorageService storage,
        Mp4StreamingOptimizer mp4Optimizer)
    {
        _db = db;
        _storage = storage;
        _mp4Optimizer = mp4Optimizer;
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/videos")]
    public async Task<IActionResult> GetVideos(Guid clubId, Guid teamId, Guid playerUserId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value))
            return Forbid();

        var rows = await _db.PlayerVideos
            .Where(v => v.PlayerUserId == playerUserId && v.TeamId == teamId)
            .OrderByDescending(v => v.CreatedAt)
            .Select(v => new
            {
                v.VideoId,
                v.PlayerUserId,
                v.TeamId,
                v.Title,
                v.OriginalFileName,
                v.ContentType,
                v.FileSize,
                AddedByUserId = v.AddedByUserId,
                AddedByName = v.AddedByUser.Name,
                AddedByRole = v.AddedByRole,
                CanEdit = v.AddedByUserId == userId.Value,
                v.CreatedAt,
            })
            .ToListAsync();

        var result = rows.Select(v => new
        {
            v.VideoId,
            v.PlayerUserId,
            v.TeamId,
            v.Title,
            v.OriginalFileName,
            v.ContentType,
            v.FileSize,
            StreamPath = StreamPathFor(clubId, teamId, playerUserId, v.VideoId),
            v.AddedByUserId,
            v.AddedByName,
            v.AddedByRole,
            v.CanEdit,
            v.CreatedAt,
        });

        return Ok(result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/videos")]
    [RequestSizeLimit(MaxVideoBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxVideoBytes)]
    public async Task<IActionResult> AddVideo(
        Guid clubId, Guid teamId, Guid playerUserId,
        [FromForm] IFormFile? file, [FromForm] string? title)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        if (file == null || file.Length == 0)
            return BadRequest(new { error = "A video file is required." });
        if (file.Length > MaxVideoBytes)
            return BadRequest(new { error = "Video is too large. The maximum size is 500 MB." });

        var extension = Path.GetExtension(file.FileName);
        var isVideo = (file.ContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false)
            || AllowedVideoExtensions.Contains(extension.ToLowerInvariant());
        if (!isVideo)
            return BadRequest(new { error = "Only video files can be uploaded here." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        if (!CanManageVideos(role) && !await IsClubManagerAsync(clubId, userId.Value))
            return Forbid();

        var tempPath = Path.GetTempFileName();
        await using (var stream = new FileStream(tempPath, FileMode.Create))
        {
            await file.CopyToAsync(stream);
        }
        await _mp4Optimizer.OptimizeAsync(tempPath);
        
        await using var optimizedStream = new FileStream(tempPath, FileMode.Open);
        var storagePath = await _storage.SaveFileAsync(optimizedStream, file.FileName, "player-videos/$playerUserId", ResolveVideoContentType(file.ContentType, extension));
        optimizedStream.Close();
        System.IO.File.Delete(tempPath);
        var storedName = file.FileName;

        var cleanTitle = title?.Trim();
        if (string.IsNullOrEmpty(cleanTitle))
            cleanTitle = Path.GetFileNameWithoutExtension(file.FileName);
        if (string.IsNullOrEmpty(cleanTitle))
            cleanTitle = "Player video";

        var now = DateTime.UtcNow;
        var video = new PlayerVideo
        {
            VideoId = Guid.NewGuid(),
            PlayerUserId = playerUserId,
            TeamId = teamId,
            AddedByUserId = userId.Value,
            AddedByRole = role?.ToString() ?? "ClubManager",
            Title = cleanTitle,
            FileName = storedName,
            OriginalFileName = file.FileName,
            ContentType = ResolveVideoContentType(file.ContentType, extension),
            FileSize = file.Length,
            StoragePath = storagePath,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.PlayerVideos.Add(video);
        await _db.SaveChangesAsync();

        return Ok(await ProjectVideoAsync(video.VideoId, clubId, teamId, playerUserId, userId.Value));
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/videos/{videoId:guid}/stream")]
    public async Task<IActionResult> StreamVideo(Guid clubId, Guid teamId, Guid playerUserId, Guid videoId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value))
            return Forbid();

        var video = await _db.PlayerVideos
            .FirstOrDefaultAsync(v => v.VideoId == videoId && v.PlayerUserId == playerUserId && v.TeamId == teamId);
        if (video == null) return NotFound(new { error = "Video not found." });

        return Redirect(video.StoragePath);
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/videos/{videoId:guid}")]
    public async Task<IActionResult> DeleteVideo(Guid clubId, Guid teamId, Guid playerUserId, Guid videoId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var video = await _db.PlayerVideos
            .FirstOrDefaultAsync(v => v.VideoId == videoId && v.PlayerUserId == playerUserId);
        if (video == null) return NotFound(new { error = "Video not found." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        var isManager = await IsClubManagerAsync(clubId, userId.Value);
        if (video.AddedByUserId != userId.Value && !CanManageVideos(role) && !isManager)
            return Forbid();

        await _storage.DeleteFileAsync(video.StoragePath);
        _db.PlayerVideos.Remove(video);
        await _db.SaveChangesAsync();

        return Ok(new { message = "Video removed." });
    }

    private async Task<object?> ProjectVideoAsync(
        Guid videoId, Guid clubId, Guid teamId, Guid playerUserId, Guid callerUserId)
    {
        var v = await _db.PlayerVideos
            .Where(x => x.VideoId == videoId)
            .Select(x => new
            {
                x.VideoId,
                x.PlayerUserId,
                x.TeamId,
                x.Title,
                x.OriginalFileName,
                x.ContentType,
                x.FileSize,
                x.AddedByUserId,
                AddedByName = x.AddedByUser.Name,
                AddedByRole = x.AddedByRole,
                x.CreatedAt,
            })
            .FirstOrDefaultAsync();
        if (v == null) return null;

        return new
        {
            v.VideoId,
            v.PlayerUserId,
            v.TeamId,
            v.Title,
            v.OriginalFileName,
            v.ContentType,
            v.FileSize,
            StreamPath = StreamPathFor(clubId, teamId, playerUserId, v.VideoId),
            v.AddedByUserId,
            v.AddedByName,
            v.AddedByRole,
            CanEdit = v.AddedByUserId == callerUserId,
            v.CreatedAt,
        };
    }

    private static string StreamPathFor(Guid clubId, Guid teamId, Guid playerUserId, Guid videoId) =>
        $"/clubs/{clubId}/teams/{teamId}/players/{playerUserId}/videos/{videoId}/stream";

    // Players need a real video MIME type. Multipart uploads often arrive as
    // "application/octet-stream", which many players refuse to play, so prefer
    // a type derived from the file extension.
    private static string ResolveVideoContentType(string? uploadedContentType, string extension) =>
        extension.ToLowerInvariant() switch
        {
            ".mp4" or ".m4v" => "video/mp4",
            ".mov" => "video/quicktime",
            ".webm" => "video/webm",
            ".mkv" => "video/x-matroska",
            ".avi" => "video/x-msvideo",
            ".3gp" => "video/3gpp",
            _ => (uploadedContentType?.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ?? false)
                ? uploadedContentType!
                : "video/mp4",
        };

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }

    private async Task<RoleNameType?> GetTeamRoleAsync(Guid teamId, Guid userId)
    {
        return await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active)
            .Select(tm => (RoleNameType?)tm.Role)
            .FirstOrDefaultAsync();
    }

    private async Task<bool> CanViewTeamAsync(Guid? clubId, Guid teamId, Guid userId)
    {
        if (await _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin)) return true;
        if (await _db.TeamMemberships.AnyAsync(tm =>
                tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active))
            return true;
        return clubId.HasValue && await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId.Value && cm.UserId == userId && cm.Status == MembershipStatus.Active);
    }

    private Task<bool> IsClubManagerAsync(Guid clubId, Guid userId) =>
        _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId && cm.UserId == userId &&
            cm.Role == RoleNameType.ClubManager && cm.Status == MembershipStatus.Active);

    private static bool CanManageVideos(RoleNameType? role) =>
        role is RoleNameType.Coach or RoleNameType.TeamManager or RoleNameType.TeamAnalyst
            or RoleNameType.TeamDoctor or RoleNameType.FitnessCoach;
}
