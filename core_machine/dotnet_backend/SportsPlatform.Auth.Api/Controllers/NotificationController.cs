using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SportsPlatform.Auth.Core.DTOs.Response;
using SportsPlatform.Auth.Core.Interfaces;

namespace SportsPlatform.Auth.Api.Controllers;

[ApiController]
[Authorize]
public class NotificationController : ControllerBase
{
    private readonly INotificationService _notifications;

    public NotificationController(INotificationService notifications)
    {
        _notifications = notifications;
    }

    [HttpGet("notifications")]
    public async Task<ActionResult<NotificationListDto>> GetAll([FromQuery] int page = 1, [FromQuery] int pageSize = 30, [FromQuery] bool unreadOnly = false)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(await _notifications.GetMyNotificationsAsync(userId.Value, page, pageSize, unreadOnly));
    }

    [HttpGet("notifications/unread-count")]
    public async Task<ActionResult<UnreadCountDto>> GetUnreadCount()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        return Ok(new UnreadCountDto { UnreadCount = await _notifications.GetUnreadCountAsync(userId.Value) });
    }

    [HttpPost("notifications/{notificationId:guid}/read")]
    public async Task<IActionResult> MarkRead(Guid notificationId)
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        await _notifications.MarkReadAsync(userId.Value, notificationId);
        return NoContent();
    }

    [HttpPost("notifications/read-all")]
    public async Task<IActionResult> MarkAllRead()
    {
        var userId = GetUserId();
        if (userId == null) return Unauthorized(new { error = "Invalid token." });
        await _notifications.MarkAllReadAsync(userId.Value);
        return NoContent();
    }

    private Guid? GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(claim, out var parsed) ? parsed : null;
    }
}
