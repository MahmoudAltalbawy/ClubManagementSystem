using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class AttendanceController : ControllerBase
{
    private readonly IAttendanceService _attendanceService;

    public AttendanceController(IAttendanceService attendanceService)
    {
        _attendanceService = attendanceService;
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/attendance")]
    public async Task<IActionResult> RecordAttendance(Guid clubId, Guid teamId, Guid eventId, [FromBody] RecordAttendanceRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _attendanceService.RecordAttendanceAsync(clubId, teamId, eventId, userId.Value, request);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/attendance")]
    public async Task<IActionResult> GetEventAttendance(Guid clubId, Guid teamId, Guid eventId, [FromQuery] DateOnly? instanceDate)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _attendanceService.GetEventAttendanceAsync(clubId, teamId, eventId, userId.Value, instanceDate);
        return Ok(result);
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/attendance/{playerUserId:guid}")]
    public async Task<IActionResult> UpdateAttendance(Guid clubId, Guid teamId, Guid eventId, Guid playerUserId, [FromBody] UpdateAttendanceRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _attendanceService.UpdateAttendanceAsync(clubId, teamId, eventId, playerUserId, userId.Value, request);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/attendance/me")]
    public async Task<IActionResult> GetMyAttendance(Guid clubId, Guid teamId, Guid eventId, [FromQuery] DateOnly? instanceDate)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _attendanceService.GetMyAttendanceAsync(clubId, teamId, eventId, userId.Value, instanceDate);
        return Ok(result);
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }
}
