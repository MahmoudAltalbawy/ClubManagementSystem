using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class FitnessController : ControllerBase
{
    private readonly IFitnessService _fitnessService;

    public FitnessController(IFitnessService fitnessService)
    {
        _fitnessService = fitnessService;
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/fitness")]
    public async Task<IActionResult> CreateFitnessRecord(Guid clubId, Guid teamId, Guid playerUserId, [FromBody] CreateFitnessRecordRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _fitnessService.CreateFitnessRecordAsync(clubId, teamId, playerUserId, userId.Value, request);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/fitness")]
    public async Task<IActionResult> GetPlayerFitnessRecords(Guid clubId, Guid teamId, Guid playerUserId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _fitnessService.GetPlayerFitnessRecordsAsync(clubId, teamId, playerUserId, userId.Value);
        return Ok(result);
    }

    [HttpGet("players/me/fitness")]
    public async Task<IActionResult> GetMyFitnessRecords()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _fitnessService.GetMyFitnessRecordsAsync(userId.Value);
        return Ok(result);
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }
}
