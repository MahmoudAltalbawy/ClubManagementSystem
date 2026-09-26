using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class GameStatsController : ControllerBase
{
    private readonly IGameStatsService _svc;

    public GameStatsController(IGameStatsService svc)
    {
        _svc = svc;
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/stats")]
    public async Task<IActionResult> Create(Guid clubId, Guid teamId, [FromBody] CreateMatchStatsRequest request)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.CreateStatsAsync(clubId, teamId, uid.Value, request);
        return CreatedAtAction(nameof(GetMatchStats), new { clubId, teamId, eventId = result.EventId }, result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/stats/upload")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> Upload(Guid clubId, Guid teamId, [FromForm] Guid eventId, [FromForm] IFormFile file)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "Upload a CSV or PDF file." });

        await using var stream = file.OpenReadStream();
        return Ok(await _svc.PreviewUploadAsync(clubId, teamId, uid.Value, eventId, file.FileName, stream));
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats")]
    public async Task<IActionResult> GetAggregates(Guid clubId, Guid teamId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetTeamAggregatesAsync(clubId, teamId, uid.Value));
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/matches")]
    public async Task<IActionResult> GetMatchHistory(Guid clubId, Guid teamId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetMatchHistoryAsync(clubId, teamId, uid.Value));
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/matches/{eventId:guid}")]
    public async Task<IActionResult> GetMatchStats(Guid clubId, Guid teamId, Guid eventId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetMatchStatsAsync(clubId, teamId, eventId, uid.Value));
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/stats/matches/{eventId:guid}")]
    public async Task<IActionResult> DeleteMatchStats(Guid clubId, Guid teamId, Guid eventId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.DeleteMatchStatsAsync(clubId, teamId, eventId, uid.Value);
        return NoContent();
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/players/{playerUserId:guid}")]
    public async Task<IActionResult> GetPlayerAggregate(Guid clubId, Guid teamId, Guid playerUserId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetPlayerAggregateAsync(clubId, teamId, playerUserId, uid.Value));
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/players/{playerUserId:guid}/matches")]
    public async Task<IActionResult> GetPlayerMatchHistory(Guid clubId, Guid teamId, Guid playerUserId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetPlayerMatchHistoryAsync(clubId, teamId, playerUserId, uid.Value));
    }

    // ── Basketball PDF extraction via extract.py ──

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/stats/basketball/extract")]
    [RequestSizeLimit(20_000_000)]
    public async Task<IActionResult> ExtractBasketballPdf(Guid clubId, Guid teamId, [FromForm] IFormFile file)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "Upload a PDF file." });

        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (ext != ".pdf") return BadRequest(new { error = "Only PDF files are supported for basketball extraction." });

        await using var stream = file.OpenReadStream();
        return Ok(await _svc.ExtractBasketballPdfAsync(clubId, teamId, uid.Value, file.FileName, stream));
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/stats/basketball")]
    public async Task<IActionResult> CreateBasketball(Guid clubId, Guid teamId, [FromBody] Core.DTOs.Request.CreateBasketballStatsRequest request)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.CreateBasketballStatsAsync(clubId, teamId, uid.Value, request);
        return Ok(result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/stats/basketball/confirm")]
    public async Task<IActionResult> ConfirmBasketballUpload(Guid clubId, Guid teamId, [FromBody] Core.DTOs.Request.ConfirmBasketballUploadRequest request)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.ConfirmBasketballUploadAsync(clubId, teamId, uid.Value, request);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/stats/basketball")]
    public async Task<IActionResult> GetBasketballAggregates(Guid clubId, Guid teamId)
    {
        var uid = GetUserId();
        if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetBasketballAggregatesAsync(clubId, teamId, uid.Value));
    }

    private Guid? GetUserId()
    {
        var c = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(c, out var p) ? p : null;
    }
}
