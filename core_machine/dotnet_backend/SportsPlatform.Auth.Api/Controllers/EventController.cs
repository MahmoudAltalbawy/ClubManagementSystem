using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class EventController : ControllerBase
{
    private readonly IEventService _eventService;

    public EventController(IEventService eventService)
    {
        _eventService = eventService;
    }

    [HttpGet("seasons")]
    public async Task<IActionResult> GetSeasons()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.GetSeasonsAsync(userId.Value);
        return Ok(result);
    }

    [HttpGet("seasons/current")]
    public async Task<IActionResult> GetCurrentSeason()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.GetCurrentSeasonAsync(userId.Value);
        return Ok(result);
    }

    [HttpPost("seasons")]
    public async Task<IActionResult> CreateSeason([FromBody] CreateSeasonRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.CreateSeasonAsync(userId.Value, request);
        return Created($"/seasons/{result.SeasonId}", result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/seasons")]
    public async Task<IActionResult> GetTeamSeasons(Guid clubId, Guid teamId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.GetTeamSeasonsAsync(clubId, teamId, userId.Value);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/seasons/current")]
    public async Task<IActionResult> GetCurrentTeamSeason(Guid clubId, Guid teamId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.GetCurrentSeasonAsync(clubId, teamId, userId.Value);
        return Ok(result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/seasons")]
    public async Task<IActionResult> CreateTeamSeason(Guid clubId, Guid teamId, [FromBody] CreateSeasonRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.CreateSeasonAsync(clubId, teamId, userId.Value, request);
        return Created($"/clubs/{clubId}/teams/{teamId}/seasons/{result.SeasonId}", result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/events")]
    public async Task<IActionResult> CreateEvent(Guid clubId, Guid teamId, [FromBody] CreateEventRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.CreateEventAsync(clubId, teamId, userId.Value, request);
        return CreatedAtAction(nameof(GetEvent), new { clubId, teamId, eventId = result.EventId }, result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/events")]
    public async Task<IActionResult> GetTeamEvents(Guid clubId, Guid teamId, [FromQuery] DateTime? from, [FromQuery] DateTime? to)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.GetTeamEventsAsync(clubId, teamId, userId.Value, from, to);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}")]
    public async Task<IActionResult> GetEvent(Guid clubId, Guid teamId, Guid eventId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.GetEventAsync(clubId, teamId, eventId, userId.Value);
        return Ok(result);
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}")]
    public async Task<IActionResult> UpdateEvent(Guid clubId, Guid teamId, Guid eventId, [FromBody] UpdateEventRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.UpdateEventAsync(clubId, teamId, eventId, userId.Value, request);
        return Ok(result);
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}")]
    public async Task<IActionResult> DeleteEvent(Guid clubId, Guid teamId, Guid eventId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        await _eventService.DeleteEventAsync(clubId, teamId, eventId, userId.Value);
        return Ok(new { message = "Event deleted successfully." });
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/cancel-instance")]
    public async Task<IActionResult> CancelEventInstance(Guid clubId, Guid teamId, Guid eventId, [FromBody] CancelEventInstanceRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.CancelEventInstanceAsync(clubId, teamId, eventId, userId.Value, request);
        return Ok(result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/events/{eventId:guid}/reschedule-instance")]
    public async Task<IActionResult> RescheduleEventInstance(Guid clubId, Guid teamId, Guid eventId, [FromBody] RescheduleEventInstanceRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _eventService.RescheduleEventInstanceAsync(clubId, teamId, eventId, userId.Value, request);
        return Ok(result);
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }
}
