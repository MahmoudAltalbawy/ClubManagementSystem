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
public class EventPlanController : ControllerBase
{
    private readonly AppDbContext _db;

    public EventPlanController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/plans")]
    public async Task<IActionResult> GetEventPlans(Guid clubId, Guid teamId, Guid eventId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (!await CanViewTeamAsync(clubId, teamId, userId.Value)) return Forbid();

        var plans = await _db.EventPlans
            .Where(ep => ep.EventId == eventId && ep.Plan.TeamId == teamId && ep.Plan.DeletedAt == null)
            .OrderByDescending(ep => ep.CreatedAt)
            .Select(ep => new
            {
                ep.Plan.PlanId,
                ep.Plan.TeamId,
                ep.Plan.Title,
                ep.Plan.Description,
                ep.Plan.Content,
                Visibility = ep.Plan.Visibility.ToString(),
                CreatorName = ep.Plan.Creator.Name,
                ep.Plan.CreatedBy,
                ep.Plan.CreatedAt,
                ep.Plan.UpdatedAt,
            })
            .ToListAsync();

        return Ok(plans);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/plans/{planId:guid}")]
    public async Task<IActionResult> AttachEventPlan(Guid clubId, Guid teamId, Guid eventId, Guid planId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        if (role != RoleNameType.Coach && !await IsAdminAsync(userId.Value))
            return Forbid();

        var eventExists = await _db.Events.AnyAsync(e =>
            e.EventId == eventId && e.TeamId == teamId && e.DeletedAt == null);
        if (!eventExists) return NotFound(new { error = "Event not found." });

        var plan = await _db.CoachingPlans
            .FirstOrDefaultAsync(p => p.PlanId == planId && p.TeamId == teamId && p.DeletedAt == null);
        if (plan == null) return NotFound(new { error = "Plan not found." });
        if (plan.CreatedBy != userId.Value || plan.Visibility != PlanVisibility.TeamVisible)
            return BadRequest(new { error = "Only your team-visible plans can be attached to a match." });

        var existing = await _db.EventPlans
            .FirstOrDefaultAsync(ep => ep.EventId == eventId && ep.PlanId == planId);
        if (existing == null)
        {
            _db.EventPlans.Add(new EventPlan
            {
                EventPlanId = Guid.NewGuid(),
                EventId = eventId,
                PlanId = planId,
                LinkedByUserId = userId.Value,
                CreatedAt = DateTime.UtcNow,
            });
            await _db.SaveChangesAsync();
        }

        return Ok(new { message = "Plan attached." });
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/plans/{planId:guid}")]
    public async Task<IActionResult> DetachEventPlan(Guid clubId, Guid teamId, Guid eventId, Guid planId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var role = await GetTeamRoleAsync(teamId, userId.Value);
        if (role != RoleNameType.Coach && !await IsAdminAsync(userId.Value))
            return Forbid();

        var link = await _db.EventPlans
            .FirstOrDefaultAsync(ep => ep.EventId == eventId && ep.PlanId == planId);
        if (link == null) return NotFound(new { error = "Plan link not found." });

        _db.EventPlans.Remove(link);
        await _db.SaveChangesAsync();
        return NoContent();
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

    private async Task<bool> CanViewTeamAsync(Guid clubId, Guid teamId, Guid userId)
    {
        if (await IsAdminAsync(userId)) return true;
        if (await _db.TeamMemberships.AnyAsync(tm =>
                tm.TeamId == teamId && tm.UserId == userId && tm.Status == MembershipStatus.Active))
            return true;
        return await _db.ClubMemberships.AnyAsync(cm =>
            cm.ClubId == clubId && cm.UserId == userId && cm.Status == MembershipStatus.Active);
    }
}
