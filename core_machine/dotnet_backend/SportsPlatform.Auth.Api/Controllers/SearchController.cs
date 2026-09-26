using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly ISearchService _search;

    public SearchController(ISearchService search)
    {
        _search = search;
    }

    [HttpGet("search")]
    public async Task<ActionResult<SearchResponseDto>> Search(
        [FromQuery] string q = "",
        [FromQuery] string type = "all",
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 30)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _search.SearchAsync(userId.Value, q, type, page, pageSize));
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var parsed) ? parsed : null;
    }
}
