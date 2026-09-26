using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class MedicalController : ControllerBase
{
    private readonly IMedicalService _medicalService;
    private readonly IWebHostEnvironment _environment;

    public MedicalController(IMedicalService medicalService, IWebHostEnvironment environment)
    {
        _medicalService = medicalService;
        _environment = environment;
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/medical")]
    public async Task<IActionResult> CreateMedicalRecord(Guid clubId, Guid teamId, Guid playerUserId, [FromBody] CreateMedicalRecordRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _medicalService.CreateMedicalRecordAsync(clubId, teamId, playerUserId, userId.Value, request);
        return Ok(result);
    }

    [HttpPut("clubs/{clubId:guid}/teams/{teamId:guid}/medical/{recordId:guid}")]
    public async Task<IActionResult> UpdateMedicalRecord(Guid clubId, Guid teamId, Guid recordId, [FromBody] UpdateMedicalRecordRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _medicalService.UpdateMedicalRecordAsync(clubId, teamId, recordId, userId.Value, request);
        return Ok(result);
    }

    [HttpGet("clubs/{clubId:guid}/teams/{teamId:guid}/players/{playerUserId:guid}/medical")]
    public async Task<IActionResult> GetPlayerMedicalRecords(Guid clubId, Guid teamId, Guid playerUserId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _medicalService.GetPlayerMedicalRecordsAsync(clubId, teamId, playerUserId, userId.Value);
        return Ok(result);
    }

    [HttpDelete("clubs/{clubId:guid}/teams/{teamId:guid}/medical/{recordId:guid}")]
    public async Task<IActionResult> DeleteMedicalRecord(Guid clubId, Guid teamId, Guid recordId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        await _medicalService.DeleteMedicalRecordAsync(clubId, teamId, recordId, userId.Value);
        return NoContent();
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/medical/{recordId:guid}/delete")]
    public async Task<IActionResult> DeleteMedicalRecordViaPost(Guid clubId, Guid teamId, Guid recordId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        await _medicalService.DeleteMedicalRecordAsync(clubId, teamId, recordId, userId.Value);
        return NoContent();
    }

    [HttpPatch("clubs/{clubId:guid}/teams/{teamId:guid}/medical/{recordId:guid}/clearance")]
    public async Task<IActionResult> UpdateMedicalClearance(Guid clubId, Guid teamId, Guid recordId, [FromBody] UpdateMedicalClearanceRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _medicalService.UpdateMedicalClearanceAsync(clubId, teamId, recordId, userId.Value, request);
        return Ok(result);
    }

    [HttpPost("clubs/{clubId:guid}/teams/{teamId:guid}/medical/{recordId:guid}/document-requests")]
    public async Task<IActionResult> RequestMedicalDocument(Guid clubId, Guid teamId, Guid recordId, [FromBody] RequestMedicalDocumentRequest request)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _medicalService.RequestMedicalDocumentAsync(clubId, teamId, recordId, userId.Value, request);
        return Ok(result);
    }

    [HttpPost("players/me/medical/document-requests/{requestId:guid}/upload")]
    public async Task<IActionResult> UploadMedicalDocument(Guid requestId, [FromForm] IFormFile? file)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "A document file is required." });

        await using var stream = file.OpenReadStream();
        var result = await _medicalService.UploadMedicalDocumentAsync(
            requestId,
            userId.Value,
            stream,
            file.FileName,
            file.ContentType,
            file.Length);

        return Ok(result);
    }

    [HttpGet("medical/document-requests/{requestId:guid}/download")]
    public async Task<IActionResult> DownloadMedicalDocument(Guid requestId)
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var file = await _medicalService.GetMedicalDocumentDownloadAsync(requestId, userId.Value);
        return Redirect(file.FilePath);
    }

    [HttpGet("players/me/medical")]
    public async Task<IActionResult> GetMyMedicalRecords()
    {
        var userId = GetCallerUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });

        var result = await _medicalService.GetMyMedicalRecordsAsync(userId.Value);
        return Ok(result);
    }

    private Guid? GetCallerUserId()
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(userIdClaim, out var parsed) ? parsed : null;
    }
}
