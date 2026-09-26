using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SportsPlatform.Auth.Core.Entities;
using SportsPlatform.Auth.Core.Enums;
using SportsPlatform.Auth.Infrastructure.Data;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class CoachNoteController : ControllerBase
{
    private readonly AppDbContext _db;

    public CoachNoteController(AppDbContext db)
    {
        _db = db;
    }

    public record CoachNoteBody(string? Body);

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/notes")]
    public async Task<IActionResult> GetNotes(Guid clubId, Guid teamId, Guid eventId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value))
            return Forbid();

        var notes = await _db.CoachNotes
            .Where(n => n.EventId == eventId && n.TeamId == teamId)
            .OrderByDescending(n => n.CreatedAt)
            .Select(n => new
            {
                n.NoteId,
                n.EventId,
                n.TeamId,
                n.Body,
                AuthorUserId = n.AuthorUserId,
                AuthorName = n.AuthorUser.Name,
                AuthorRole = n.AuthorRole,
                AuthorAvatarUrl = n.AuthorUser.ProfileImageUrl,
                CanEdit = n.AuthorUserId == userId.Value,
                n.CreatedAt,
                n.UpdatedAt,
            })
            .ToListAsync();

        return Ok(notes);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/notes")]
    public async Task<IActionResult> CreateNote(Guid clubId, Guid teamId, Guid eventId, [FromBody] CoachNoteBody payload)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var body = payload?.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return BadRequest(new { error = "Note text is required." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        if (!CanWriteNotes(role) && !await IsClubManagerAsync(clubId, userId.Value))
            return Forbid();

        var now = DateTime.UtcNow;
        var note = new CoachNote
        {
            NoteId = Guid.NewGuid(),
            EventId = eventId,
            TeamId = teamId,
            AuthorUserId = userId.Value,
            AuthorRole = role?.ToString() ?? "ClubManager",
            Body = body,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.CoachNotes.Add(note);
        await _db.SaveChangesAsync();

        return Ok(await ProjectNoteAsync(note.NoteId, userId.Value));
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/notes/{noteId:guid}")]
    public async Task<IActionResult> UpdateNote(Guid clubId, Guid teamId, Guid eventId, Guid noteId, [FromBody] CoachNoteBody payload)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var body = payload?.Body?.Trim();
        if (string.IsNullOrEmpty(body)) return BadRequest(new { error = "Note text is required." });

        var note = await _db.CoachNotes
            .FirstOrDefaultAsync(n => n.NoteId == noteId && n.EventId == eventId && n.TeamId == teamId);
        if (note == null) return NotFound(new { error = "Note not found." });
        if (note.AuthorUserId != userId.Value)
            return Forbid();

        note.Body = body;
        note.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(await ProjectNoteAsync(note.NoteId, userId.Value));
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/notes/{noteId:guid}")]
    public async Task<IActionResult> DeleteNote(Guid clubId, Guid teamId, Guid eventId, Guid noteId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var note = await _db.CoachNotes
            .FirstOrDefaultAsync(n => n.NoteId == noteId && n.EventId == eventId && n.TeamId == teamId);
        if (note == null) return NotFound(new { error = "Note not found." });
        if (note.AuthorUserId != userId.Value)
            return Forbid();

        note.DeletedAt = DateTime.UtcNow;
        note.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        return Ok(new { message = "Note deleted." });
    }

    private async Task<object?> ProjectNoteAsync(Guid noteId, Guid callerUserId)
    {
        return await _db.CoachNotes
            .Where(n => n.NoteId == noteId)
            .Select(n => new
            {
                n.NoteId,
                n.EventId,
                n.TeamId,
                n.Body,
                AuthorUserId = n.AuthorUserId,
                AuthorName = n.AuthorUser.Name,
                AuthorRole = n.AuthorRole,
                AuthorAvatarUrl = n.AuthorUser.ProfileImageUrl,
                CanEdit = n.AuthorUserId == callerUserId,
                n.CreatedAt,
                n.UpdatedAt,
            })
            .FirstOrDefaultAsync();
    }

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

    private static bool CanWriteNotes(RoleNameType? role) =>
        role is RoleNameType.Coach or RoleNameType.TeamManager or RoleNameType.TeamAnalyst
            or RoleNameType.TeamDoctor or RoleNameType.FitnessCoach;
}
