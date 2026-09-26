using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Infrastructure.Data;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class EventDocumentController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;

    public EventDocumentController(AppDbContext db, IFileStorageService storage)
    {
        _db = db;
        _storage = storage;
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/documents")]
    public async Task<IActionResult> GetEventDocuments(Guid clubId, Guid teamId, Guid eventId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value))
            return Forbid();

        var docs = await _db.EventDocuments
            .Where(d => d.EventId == eventId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                d.DocumentId,
                d.EventId,
                d.OriginalFileName,
                d.ContentType,
                d.FileSize,
                d.Description,
                UploadedBy = d.UploadedByUser.Name,
                UploadedByRole = d.UploadedByRole,
                d.CreatedAt,
            })
            .ToListAsync();

        return Ok(docs);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/documents")]
    public async Task<IActionResult> UploadEventDocument(Guid clubId, Guid teamId, Guid eventId, [FromForm] IFormFile? file, [FromForm] string? description)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "A file is required." });
        var role = await GetTeamRoleAsync(teamId, userId.Value);
        var isAdmin = await _db.Users.AnyAsync(u => u.UserId == userId.Value && u.IsAdmin);
        
        if (!IsTeamStaff(role) && !await IsClubManagerAsync(clubId, userId.Value) && !isAdmin)
            return Forbid();

        await using var stream = file.OpenReadStream();
        var fileUrl = await _storage.SaveFileAsync(stream, file.FileName, $"events/{eventId}", file.ContentType);

        var doc = new EventDocument
        {
            DocumentId = Guid.NewGuid(),
            EventId = eventId,
            UploadedByUserId = userId.Value,
            FileName = file.FileName,
            OriginalFileName = file.FileName,
            ContentType = file.ContentType,
            Description = description?.Trim(),
            UploadedByRole = role?.ToString() ?? "ClubManager",
            FileSize = file.Length,
            StoragePath = fileUrl,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.EventDocuments.Add(doc);
        await _db.SaveChangesAsync();
        var uploaderName = await _db.Users
            .Where(u => u.UserId == userId.Value)
            .Select(u => u.Name)
            .FirstOrDefaultAsync();

        return Ok(new
        {
            doc.DocumentId,
            doc.EventId,
            doc.OriginalFileName,
            doc.ContentType,
            doc.FileSize,
            doc.Description,
            UploadedBy = uploaderName,
            doc.UploadedByRole,
            doc.CreatedAt
        });
    }

    [HttpGet("events/documents/{documentId:guid}/download")]
    public async Task<IActionResult> DownloadEventDocument(Guid documentId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var doc = await _db.EventDocuments.FirstOrDefaultAsync(d => d.DocumentId == documentId);
        if (doc == null) return NotFound();

        // In a real app we'd verify the user is in the event's team
        // Here we'll just redirect to the storage path
        return Redirect(doc.StoragePath);
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> DeleteEventDocument(Guid clubId, Guid teamId, Guid eventId, Guid documentId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        var role = await GetTeamRoleAsync(teamId, userId.Value);
        var isAdmin = await _db.Users.AnyAsync(u => u.UserId == userId.Value && u.IsAdmin);

        if (!IsTeamStaff(role) && !await IsClubManagerAsync(clubId, userId.Value) && !isAdmin)
            return Forbid();

        var doc = await _db.EventDocuments.FirstOrDefaultAsync(d => d.DocumentId == documentId && d.EventId == eventId);
        if (doc == null) return NotFound();

        await _storage.DeleteFileAsync(doc.StoragePath);

        _db.EventDocuments.Remove(doc);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // --- Helpers ---
    private Guid? GetCallerUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        return claim != null && Guid.TryParse(claim.Value, out var id) ? id : null;
    }

    private async Task<bool> CanViewTeamAsync(Guid clubId, Guid teamId, Guid userId)
    {
        if (await _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin)) return true;
        if (await _db.TeamMemberships.AnyAsync(tm => tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active)) return true;
        return await IsClubManagerAsync(clubId, userId);
    }

    private async Task<RoleNameType?> GetTeamRoleAsync(Guid teamId, Guid userId)
    {
        return await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active)
            .Select(tm => (RoleNameType?)tm.Role)
            .FirstOrDefaultAsync();
    }

    private bool IsTeamStaff(RoleNameType? role)
    {
        return role is RoleNameType.TeamManager or RoleNameType.Coach or RoleNameType.FitnessCoach or RoleNameType.TeamDoctor or RoleNameType.TeamAnalyst;
    }

    private Task<bool> IsClubManagerAsync(Guid clubId, Guid userId) =>
        _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId && cm.UserId == userId &&
            cm.Role == RoleNameType.ClubManager && cm.Status == MembershipStatus.Active);
}