using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Api.Services;
using SportsPlatform.Auth.Core.DTOs.Request;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
[Route("messages")]
public class MessagingController : ControllerBase
{
    private readonly IMessagingService _svc;
    private readonly IWebHostEnvironment _environment;
    private readonly Mp4StreamingOptimizer _mp4Optimizer;
    public MessagingController(IMessagingService svc, IWebHostEnvironment environment, Mp4StreamingOptimizer mp4Optimizer) { _svc = svc; _environment = environment; _mp4Optimizer = mp4Optimizer; }

    [HttpPost("conversations")]
    public async Task<IActionResult> CreateConversation([FromBody] CreateConversationRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.CreateConversationAsync(uid.Value, request);
        return Created("", result);
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations()
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetConversationsAsync(uid.Value));
    }

    [HttpGet("conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> GetMessages(Guid conversationId, [FromQuery] int page = 1, [FromQuery] int pageSize = 50)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _svc.GetMessagesAsync(conversationId, uid.Value, page, pageSize));
    }

    [HttpPost("conversations/{conversationId:guid}/messages")]
    public async Task<IActionResult> SendMessage(Guid conversationId, [FromBody] SendMessageRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.SendMessageAsync(conversationId, uid.Value, request);
        return Created("", result);
    }

    [HttpPost("conversations/{conversationId:guid}/read")]
    public async Task<IActionResult> MarkAsRead(Guid conversationId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.MarkAsReadAsync(conversationId, uid.Value);
        return NoContent();
    }

    [HttpPut("messages/{messageId:guid}")]
    public async Task<IActionResult> EditMessage(Guid messageId, [FromBody] EditMessageRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.EditMessageAsync(messageId, uid.Value, request);
        return Ok(result);
    }

    [HttpDelete("messages/{messageId:guid}")]
    public async Task<IActionResult> DeleteMessage(Guid messageId)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.DeleteMessageAsync(messageId, uid.Value);
        return NoContent();
    }

    [HttpPost("messages/{messageId:guid}/reactions")]
    public async Task<IActionResult> AddReaction(Guid messageId, [FromBody] SendReactionRequest request)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        var result = await _svc.AddReactionAsync(messageId, uid.Value, request);
        return Ok(result);
    }

    [HttpDelete("messages/{messageId:guid}/reactions/{emoji}")]
    public async Task<IActionResult> RemoveReaction(Guid messageId, string emoji)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        await _svc.RemoveReactionAsync(messageId, uid.Value, emoji);
        return NoContent();
    }

    [HttpPost("conversations/{conversationId:guid}/media")]
    public async Task<IActionResult> SendMediaMessage(Guid conversationId, [FromForm] IFormFile? file)
    {
        var uid = GetUserId(); if (uid == null) return Unauthorized(new { error = "Invalid token." });
        if (file == null || file.Length == 0) return BadRequest(new { error = "A media file is required." });

        await using var stream = file.OpenReadStream();
        var result = await _svc.SendMediaMessageAsync(
            conversationId, uid.Value, stream, file.FileName, file.ContentType, file.Length);
        await TryOptimizeUploadedVideoAsync(result.MediaUrl, file.FileName);
        return Created("", result);
    }

    private Guid? GetUserId() { var c = User.FindFirst(ClaimTypes.NameIdentifier)?.Value; return Guid.TryParse(c, out var p) ? p : null; }

    private async Task TryOptimizeUploadedVideoAsync(string? mediaUrl, string fileName)
    {
        if (string.IsNullOrWhiteSpace(mediaUrl) || string.IsNullOrWhiteSpace(_environment.WebRootPath))
            return;

        if (mediaUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return;

        var extension = Path.GetExtension(fileName);
        var isOptimizableVideo = extension.ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";
        if (!isOptimizableVideo)
            return;

        var relativePath = mediaUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        var webRoot = Path.GetFullPath(_environment.WebRootPath);
        var fullPath = Path.GetFullPath(Path.Combine(webRoot, relativePath));
        if (!fullPath.StartsWith(webRoot, StringComparison.OrdinalIgnoreCase))
            return;

        await _mp4Optimizer.OptimizeAsync(fullPath);
    }
}
