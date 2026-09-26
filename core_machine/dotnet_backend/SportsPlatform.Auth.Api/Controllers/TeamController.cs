using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Route("clubs/{clubId:guid}/teams")]
[Authorize]
public class TeamController : ControllerBase
{
    private readonly ITeamService _teamService;

    public TeamController(ITeamService teamService)
    {
        _teamService = teamService;
    }

    [AllowAnonymous]
    [HttpGet("/teams/categories")]
    public async Task<IActionResult> GetCategories()
    {
        var categories = await _teamService.GetTeamCategoriesAsync();
        return Ok(categories);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTeam(Guid clubId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var (request, image) = await ReadCreateTeamRequestAsync();
        var result = image == null
            ? await _teamService.CreateTeamAsync(clubId, userId.Value, request)
            : await _teamService.CreateTeamAsync(clubId, userId.Value, request, image.OpenReadStream(), image.FileName);
        return CreatedAtAction(nameof(GetTeam), new { clubId, teamId = result.TeamId }, result);
    }

    [HttpGet("/teams/my")]
    public async Task<IActionResult> GetMyTeams()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _teamService.GetMyTeamsAsync(userId.Value);
        return Ok(result);
    }

    [HttpGet]
    public async Task<IActionResult> GetClubTeams(Guid clubId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _teamService.GetClubTeamsAsync(clubId, userId.Value);
        return Ok(result);
    }

    [HttpGet("{teamId:guid}")]
    public async Task<IActionResult> GetTeam(Guid clubId, Guid teamId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _teamService.GetTeamAsync(clubId, teamId, userId.Value);
        return Ok(result);
    }

    [HttpGet("{teamId:guid}/members")]
    public async Task<IActionResult> GetTeamMembers(Guid clubId, Guid teamId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _teamService.GetTeamMembersAsync(clubId, teamId, userId.Value);
        return Ok(result);
    }

    [HttpDelete("{teamId:guid}/members/{memberUserId:guid}")]
    public async Task<IActionResult> RemoveTeamMember(Guid clubId, Guid teamId, Guid memberUserId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        await _teamService.RemoveTeamMemberAsync(clubId, teamId, memberUserId, userId.Value);
        return Ok(new { message = "Team member removed successfully." });
    }

    [HttpDelete("{teamId:guid}")]
    public async Task<IActionResult> DeleteTeam(Guid clubId, Guid teamId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        await _teamService.DeleteTeamAsync(clubId, teamId, userId.Value);
        return Ok(new { message = "Team deleted successfully." });
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }

    private async Task<(CreateTeamRequest Request, IFormFile? Image)> ReadCreateTeamRequestAsync()
    {
        if (Request.HasFormContentType)
        {
            var form = await Request.ReadFormAsync();
            return (new CreateTeamRequest
            {
                TeamName = form["teamName"].ToString(),
                CategoryId = Guid.TryParse(form["categoryId"].ToString(), out var categoryId) ? categoryId : Guid.Empty,
                SeasonLabel = form["seasonLabel"].ToString(),
                SeasonStartDate = DateOnly.TryParse(form["seasonStartDate"].ToString(), out var startDate) ? startDate : null,
                SeasonEndDate = DateOnly.TryParse(form["seasonEndDate"].ToString(), out var endDate) ? endDate : null,
            }, form.Files["image"]);
        }

        var request = await HttpContext.Request.ReadFromJsonAsync<CreateTeamRequest>()
            ?? throw new InvalidOperationException("Team details are required.");
        return (request, null);
    }
}
