using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Route("clubs/{clubId:guid}/teams/{teamId:guid}/invitations")]
[Authorize]
public class TeamInvitationController : ControllerBase
{
    private readonly IInvitationService _invitationService;

    public TeamInvitationController(IInvitationService invitationService)
    {
        _invitationService = invitationService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateTeamInvitation(
        Guid clubId,
        Guid teamId,
        [FromBody] CreateInvitationRequest request)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _invitationService.CreateTeamInvitationAsync(
            clubId,
            teamId,
            request,
            callerUserId.Value);

        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetTeamInvitations(Guid clubId, Guid teamId)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _invitationService.GetTeamInvitationsAsync(clubId, teamId, callerUserId.Value);
        return Ok(result);
    }

    [HttpDelete("{invitationId:guid}")]
    public async Task<IActionResult> CancelTeamInvitation(Guid invitationId)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null) return Unauthorized(new { error = "Invalid token." });

        await _invitationService.CancelInvitationAsync(invitationId, callerUserId.Value);
        return Ok(new { message = "Invitation cancelled successfully." });
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
