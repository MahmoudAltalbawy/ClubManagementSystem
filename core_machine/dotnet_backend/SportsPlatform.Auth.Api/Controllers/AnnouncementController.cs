using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class AnnouncementController : ControllerBase
{
    private readonly IAnnouncementService _svc;
    public AnnouncementController(IAnnouncementService svc) { _svc = svc; }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/announcements")]
    public async Task<IActionResult> Create(Guid clubId, Guid teamId, [FromBody] CreateAnnouncementRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.CreateAnnouncementAsync(clubId, teamId, uid.Value, request);
        return CreatedAtAction(nameof(GetAll), new { clubId, teamId }, result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/announcements")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> CreateWithImage(Guid clubId, Guid teamId, [FromForm] CreateAnnouncementRequest request, [FromForm] IFormFile? image)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = image == null
            ? await _svc.CreateAnnouncementAsync(clubId, teamId, uid.Value, request)
            : await _svc.CreateAnnouncementAsync(clubId, teamId, uid.Value, request, image.OpenReadStream(), image.FileName);
        return CreatedAtAction(nameof(GetAll), new { clubId, teamId }, result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/announcements")]
    public async Task<IActionResult> GetAll(Guid clubId, Guid teamId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetTeamAnnouncementsAsync(clubId, teamId, uid.Value));
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/announcements/{announcementId:guid}")]
    public async Task<IActionResult> Update(Guid clubId, Guid teamId, Guid announcementId, [FromBody] UpdateAnnouncementRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.UpdateAnnouncementAsync(clubId, teamId, announcementId, uid.Value, request));
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/announcements/{announcementId:guid}")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UpdateWithImage(Guid clubId, Guid teamId, Guid announcementId, [FromForm] UpdateAnnouncementRequest request, [FromForm] IFormFile? image)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = image == null
            ? await _svc.UpdateAnnouncementAsync(clubId, teamId, announcementId, uid.Value, request)
            : await _svc.UpdateAnnouncementAsync(clubId, teamId, announcementId, uid.Value, request, image.OpenReadStream(), image.FileName);
        return Ok(result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/announcements/{announcementId:guid}/update")]
    public async Task<IActionResult> UpdateViaPost(Guid clubId, Guid teamId, Guid announcementId, [FromBody] UpdateAnnouncementRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.UpdateAnnouncementAsync(clubId, teamId, announcementId, uid.Value, request));
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/announcements/{announcementId:guid}/update")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UpdateViaPostWithImage(Guid clubId, Guid teamId, Guid announcementId, [FromForm] UpdateAnnouncementRequest request, [FromForm] IFormFile? image)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = image == null
            ? await _svc.UpdateAnnouncementAsync(clubId, teamId, announcementId, uid.Value, request)
            : await _svc.UpdateAnnouncementAsync(clubId, teamId, announcementId, uid.Value, request, image.OpenReadStream(), image.FileName);
        return Ok(result);
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/announcements/{announcementId:guid}")]
    public async Task<IActionResult> Delete(Guid clubId, Guid teamId, Guid announcementId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.DeleteAnnouncementAsync(clubId, teamId, announcementId, uid.Value);
        return NoContent();
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/announcements/{announcementId:guid}/delete")]
    public async Task<IActionResult> DeleteViaPost(Guid clubId, Guid teamId, Guid announcementId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.DeleteAnnouncementAsync(clubId, teamId, announcementId, uid.Value);
        return NoContent();
    }

    private Guid? GetUserId() { var c = User.FindFirst(ClaimTypes.NameIdentifier)?.Value; return Guid.TryParse(c, out var p) ? p : null; }
}
