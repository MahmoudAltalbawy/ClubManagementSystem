using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class CoachingPlanController : ControllerBase
{
    private readonly ICoachingPlanService _svc;
    public CoachingPlanController(ICoachingPlanService svc) { _svc = svc; }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/plans")]
    public async Task<IActionResult> Create(Guid clubId, Guid teamId, [FromBody] CreatePlanRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.CreatePlanAsync(clubId, teamId, uid.Value, request);
        return CreatedAtAction(nameof(Get), new { clubId, teamId, planId = result.PlanId }, result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/plans")]
    public async Task<IActionResult> GetAll(Guid clubId, Guid teamId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetTeamPlansAsync(clubId, teamId, uid.Value));
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/plans/{planId:guid}")]
    public async Task<IActionResult> Get(Guid clubId, Guid teamId, Guid planId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetPlanAsync(clubId, teamId, planId, uid.Value));
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/plans/{planId:guid}")]
    public async Task<IActionResult> Update(Guid clubId, Guid teamId, Guid planId, [FromBody] UpdatePlanRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.UpdatePlanAsync(clubId, teamId, planId, uid.Value, request));
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/plans/{planId:guid}")]
    public async Task<IActionResult> Delete(Guid clubId, Guid teamId, Guid planId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.DeletePlanAsync(clubId, teamId, planId, uid.Value);
        return NoContent();
    }

    private Guid? GetUserId() { var c = User.FindFirst(ClaimTypes.NameIdentifier)?.Value; return Guid.TryParse(c, out var p) ? p : null; }
}
