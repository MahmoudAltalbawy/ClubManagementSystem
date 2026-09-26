using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class LineupController : ControllerBase
{
    private readonly ICoachingPlanService _svc;
    public LineupController(ICoachingPlanService svc) { _svc = svc; }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/lineups")]
    public async Task<IActionResult> Create(Guid clubId, Guid teamId, [FromBody] CreateLineupRequest request)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.CreateLineupAsync(clubId, teamId, uid.Value, request);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/lineups")]
    public async Task<IActionResult> GetAll(Guid clubId, Guid teamId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetTeamLineupsAsync(clubId, teamId, uid.Value));
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/lineups/{lineupId:guid}")]
    public async Task<IActionResult> Get(Guid clubId, Guid teamId, Guid lineupId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetLineupAsync(clubId, teamId, lineupId, uid.Value));
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/lineups/{lineupId:guid}")]
    public async Task<IActionResult> Update(Guid clubId, Guid teamId, Guid lineupId, [FromBody] UpdateLineupRequest request)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.UpdateLineupAsync(clubId, teamId, lineupId, uid.Value, request));
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/lineups/{lineupId:guid}")]
    public async Task<IActionResult> Delete(Guid clubId, Guid teamId, Guid lineupId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.DeleteLineupAsync(clubId, teamId, lineupId, uid.Value);
        return NoContent();
    }

    private Guid? GetUserId()
    {
        var c = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(c, out var p) ? p : null;
    }
}
