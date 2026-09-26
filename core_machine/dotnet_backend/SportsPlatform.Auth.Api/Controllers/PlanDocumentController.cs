using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class PlanDocumentController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IFileStorageService _storage;

    public PlanDocumentController(AppDbContext db, IFileStorageService storage)
    {
        _db = db;
        _storage = storage;
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/plans/{planId:guid}/documents")]
    public async Task<IActionResult> GetPlanDocuments(Guid clubId, Guid teamId, Guid planId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value)) return Forbid();

        var planExists = await _db.CoachingPlans.AnyAsync(p => p.PlanId == planId && p.TeamId == teamId);
        if (!planExists) return NotFound(new { error = "Plan not found." });

        var docs = await _db.CoachingPlanDocuments
            .Where(d => d.PlanId == planId)
            .OrderByDescending(d => d.CreatedAt)
            .Select(d => new
            {
                d.DocumentId,
                d.PlanId,
                FileName = d.OriginalFileName,
                d.ContentType,
                FileSizeBytes = d.FileSize,
                d.Description,
                UploadedBy = d.UploadedByUser.Name,
                d.UploadedByRole,
                UploadedAt = d.CreatedAt,
            })
            .ToListAsync();

        return Ok(docs);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/plans/{planId:guid}/documents")]
    public async Task<IActionResult> UploadPlanDocument(Guid clubId, Guid teamId, Guid planId, [FromForm] IFormFile? file, [FromForm] string? description)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "A file is required." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        var isAdmin = await IsAdminAsync(userId.Value);
        if (!isAdmin && !IsTeamStaff(role) && !await IsClubManagerAsync(clubId, userId.Value))
            return Forbid();

        var plan = await _db.CoachingPlans.FirstOrDefaultAsync(p => p.PlanId == planId && p.TeamId == teamId);
        if (plan == null) return NotFound(new { error = "Plan not found." });

        await using var stream = file.OpenReadStream();
        var storagePath = await _storage.SaveFileAsync(stream, file.FileName, "plans/$planId", file.ContentType);
        var storedName = file.FileName;

        var doc = new CoachingPlanDocument
        {
            DocumentId = Guid.NewGuid(),
            PlanId = planId,
            UploadedByUserId = userId.Value,
            FileName = storedName,
            OriginalFileName = file.FileName,
            ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
            Description = description?.Trim(),
            UploadedByRole = role?.ToString() ?? (isAdmin ? "Admin" : "ClubManager"),
            FileSize = file.Length,
            StoragePath = storagePath,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.CoachingPlanDocuments.Add(doc);
        await _db.SaveChangesAsync();

        var uploaderName = await _db.Users
            .Where(u => u.UserId == userId.Value)
            .Select(u => u.Name)
            .FirstOrDefaultAsync();

        return Ok(new
        {
            doc.DocumentId,
            doc.PlanId,
            FileName = doc.OriginalFileName,
            doc.ContentType,
            FileSizeBytes = doc.FileSize,
            doc.Description,
            UploadedBy = uploaderName,
            doc.UploadedByRole,
            UploadedAt = doc.CreatedAt,
        });
    }

    [HttpGet("plans/{planId:guid}/documents/{documentId:guid}/download")]
    public async Task<IActionResult> DownloadPlanDocument(Guid planId, Guid documentId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var doc = await _db.CoachingPlanDocuments
            .Include(d => d.Plan)
                .ThenInclude(p => p.Team)
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.PlanId == planId);
        if (doc == null) return NotFound(new { error = "Document not found." });
        if (!await CanViewTeamAsync(doc.Plan.Team.ClubId, doc.Plan.TeamId, userId.Value))
            return Forbid();

        if (doc.StoragePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            doc.StoragePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Redirect(doc.StoragePath);
        }

        var fullPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", doc.StoragePath.TrimStart('/'));
        if (!System.IO.File.Exists(fullPath))
            return NotFound(new { error = "File not found on server." });

        return PhysicalFile(fullPath, doc.ContentType, doc.OriginalFileName);
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/plans/{planId:guid}/documents/{documentId:guid}")]
    public async Task<IActionResult> DeletePlanDocument(Guid clubId, Guid teamId, Guid planId, Guid documentId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        if (!await IsAdminAsync(userId.Value) && !IsTeamStaff(role) && !await IsClubManagerAsync(clubId, userId.Value))
            return Forbid();

        var doc = await _db.CoachingPlanDocuments.FirstOrDefaultAsync(d => d.DocumentId == documentId && d.PlanId == planId);
        if (doc == null) return NotFound(new { error = "Document not found." });

        doc.DeletedAt = DateTime.UtcNow;
        doc.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Document deleted." });
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }

    private Task<bool> IsAdminAsync(Guid userId) =>
        _db.Users.AnyAsync(u => u.UserId == userId && u.IsAdmin);

    private async Task<RoleNameType?> GetTeamRoleAsync(Guid teamId, Guid userId)
    {
        return await _db.TeamMemberships
            .Where(tm => tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active)
            .Select(tm => (RoleNameType?)tm.Role)
            .FirstOrDefaultAsync();
    }

    private async Task<bool> CanViewTeamAsync(Guid? clubId, Guid teamId, Guid userId)
    {
        if (await IsAdminAsync(userId)) return true;
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

    private static bool IsTeamStaff(RoleNameType? role) =>
        role is RoleNameType.Coach or RoleNameType.TeamManager or RoleNameType.TeamAnalyst
            or RoleNameType.TeamDoctor or RoleNameType.FitnessCoach;
}
