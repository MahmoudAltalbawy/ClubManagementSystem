using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class PlayerController : ControllerBase
{
    private readonly IPlayerService _playerService;

    public PlayerController(IPlayerService playerService)
    {
        _playerService = playerService;
    }

    [HttpGet("players/me/profile")]
    public async Task<IActionResult> GetMyProfile()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _playerService.GetMyProfileAsync(userId.Value);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/players")]
    public async Task<IActionResult> GetTeamPlayers(Guid clubId, Guid teamId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _playerService.GetTeamPlayersAsync(clubId, teamId, userId.Value);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/profile")]
    public async Task<IActionResult> GetPlayerProfile(Guid clubId, Guid teamId, Guid playerUserId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _playerService.GetPlayerProfileAsync(clubId, teamId, playerUserId, userId.Value);
        return Ok(result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/profile")]
    public async Task<IActionResult> UpsertPlayerProfile(
        Guid clubId,
        Guid teamId,
        Guid playerUserId,
        [FromBody] UpsertPlayerProfileRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _playerService.UpsertPlayerProfileAsync(
            clubId,
            teamId,
            playerUserId,
            userId.Value,
            request);

        return Ok(result);
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }
}
