using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Route("invitations")]
[Authorize]
public class InvitationController : ControllerBase
{
    private readonly IInvitationService _invitationService;

    public InvitationController(IInvitationService invitationService)
    {
        _invitationService = invitationService;
    }

    [HttpGet("{token}")]
    public async Task<IActionResult> GetInvitation(string token)
    {
        var result = await _invitationService.GetInvitationAsync(token);
        return Ok(result);
    }

    [HttpGet("me")]
    public async Task<IActionResult> GetMyPendingInvitations()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _invitationService.GetMyPendingInvitationsAsync(userId.Value);
        return Ok(result);
    }

    [HttpPost("{token}/accept")]
    public async Task<IActionResult> AcceptInvitation(string token)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _invitationService.AcceptInvitationAsync(token, userId.Value);
        return Ok(result);
    }

    [HttpPost("{token}/deny")]
    public async Task<IActionResult> DenyInvitation(string token)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        await _invitationService.DenyInvitationAsync(token, userId.Value);
        return Ok(new { message = "Invitation denied successfully." });
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var userId) ? userId : null;
    }
}
