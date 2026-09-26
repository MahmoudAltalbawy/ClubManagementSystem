using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Route("clubs")]
[Authorize]
public class ClubController : ControllerBase
{
    private readonly IClubService _clubService;
    private readonly IInvitationService _invitationService;

    public ClubController(IClubService clubService, IInvitationService invitationService)
    {
        _clubService = clubService;
        _invitationService = invitationService;
    }

    [HttpPost]
    public async Task<IActionResult> CreateClub()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var (request, logo) = await ReadCreateClubRequestAsync();
        var result = logo == null
            ? await _clubService.CreateClubAsync(userId.Value, request)
            : await _clubService.CreateClubAsync(userId.Value, request, logo.OpenReadStream(), logo.FileName);
        return CreatedAtAction(nameof(GetClub), new { clubId = result.ClubId }, result);
    }

    [HttpPost("{clubId:guid}/logo")]
    public async Task<IActionResult> UpdateClubLogo(Guid clubId, [FromForm] IFormFile logo)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (logo == null || logo.Length == 0) return BadRequest(new { error = "Club logo is required." });

        var result = await _clubService.UpdateClubLogoAsync(clubId, userId.Value, logo.OpenReadStream(), logo.FileName);
        return Ok(result);
    }

    [HttpGet("my")]
    public async Task<IActionResult> GetMyClubs()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _clubService.GetMyClubsAsync(userId.Value);
        return Ok(result);
    }

    [HttpGet("{clubId:guid}")]
    public async Task<IActionResult> GetClub(Guid clubId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _clubService.GetClubAsync(clubId, userId.Value);
        return Ok(result);
    }

    [HttpDelete("{clubId:guid}")]
    public async Task<IActionResult> DeleteClub(Guid clubId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        await _clubService.DeleteClubAsync(clubId, userId.Value);
        return Ok(new { message = "Club deleted successfully." });
    }

    [HttpGet("{clubId:guid}/members")]
    public async Task<IActionResult> GetClubMembers(Guid clubId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _clubService.GetClubMembersAsync(clubId, userId.Value);
        return Ok(result);
    }

    [HttpDelete("{clubId:guid}/members/{userId:guid}")]
    public async Task<IActionResult> RemoveClubMember(Guid clubId, Guid userId)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null) return Unauthorized(new { error = "Invalid token." });

        await _clubService.RemoveClubMemberAsync(clubId, userId, callerUserId.Value);
        return Ok(new { message = "Club member removed successfully." });
    }

    [HttpPost("{clubId:guid}/invitations")]
    public async Task<IActionResult> CreateClubInvitation(Guid clubId, [FromBody] CreateInvitationRequest request)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _invitationService.CreateClubInvitationAsync(clubId, request, callerUserId.Value);
        return Ok(result);
    }

    [HttpGet("{clubId:guid}/invitations")]
    public async Task<IActionResult> GetClubInvitations(Guid clubId)
    {
        var callerUserId = GetCallerUserId();
        if (callerUserId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _invitationService.GetClubInvitationsAsync(clubId, callerUserId.Value);
        return Ok(result);
    }

    [HttpDelete("{clubId:guid}/invitations/{invitationId:guid}")]
    public async Task<IActionResult> CancelClubInvitation(Guid clubId, Guid invitationId)
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

    private async Task<(CreateClubRequest Request, IFormFile? Logo)> ReadCreateClubRequestAsync()
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            return (new CreateClubRequest
            {
                Name = form["name"].ToString(),
                Location = form["location"].ToString(),
                LocationLatitude = TryReadDouble(form["locationLatitude"].ToString()),
                LocationLongitude = TryReadDouble(form["locationLongitude"].ToString())
            }, form.Files["logo"]);
        }

        var request = await HttpContext.Request.ReadFromJsonAsync<CreateClubRequest>()
            ?? throw new InvalidOperationException("Club details are required.");
        return (request, null);
    }

    private static double? TryReadDouble(string value)
    {
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var result)
            ? result
            : null;
    }
}
