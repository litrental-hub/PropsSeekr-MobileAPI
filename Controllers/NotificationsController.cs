using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PropSeekr.Data;
using PropSeekr.DTOs.Notifications;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/notifications")]
public class NotificationsController : ControllerBase
{
    private readonly INotificationService _notificationService;
    private readonly ILogger<NotificationsController> _logger;

    public NotificationsController(
        INotificationService notificationService,
        ILogger<NotificationsController> logger)
    {
        _notificationService = notificationService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetNotifications(
        [FromQuery] string userId,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string filter = "ALL")
    {
        if (!Guid.TryParse(userId, out var userGuid) || !ValidateUserAccess(userGuid))
        {
            return Forbid();
        }

        try
        {
            var response = await _notificationService.GetNotificationsAsync(userGuid, page, limit, filter);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve notifications for user {UserId}", userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPatch("{id}/read")]
    public async Task<IActionResult> MarkAsRead(
        [FromRoute] Guid id,
        [FromQuery] string userId)
    {
        if (!Guid.TryParse(userId, out var userGuid) || !ValidateUserAccess(userGuid))
        {
            return Forbid();
        }

        try
        {
            await _notificationService.MarkAsReadAsync(id, userGuid);
            
            // Re-fetch notifications list state to get updated unreadCount
            var listState = await _notificationService.GetNotificationsAsync(userGuid, 1, 1, "ALL");

            return Ok(new
            {
                success = true,
                message = "Notification successfully marked as read",
                id = id.ToString(),
                unreadCount = listState.UnreadCount
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark notification {NotificationId} as read for user {UserId}", id, userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPatch("{id:long}/read")]
    public async Task<IActionResult> MarkLegacyAsRead([FromRoute] long id)
    {
        var dbContext = HttpContext.RequestServices.GetRequiredService<AppDbContext>();
        
        var notif = await dbContext.BrokerNotifications.FirstOrDefaultAsync(n => n.Id == id);
        if (notif == null)
        {
            return NotFound(new { success = false, message = "Notification not found." });
        }

        var tokenUserIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(tokenUserIdStr, out var tokenUserId))
        {
            return Unauthorized();
        }

        var callerUser = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == tokenUserId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != notif.BrokerId)
        {
            return Forbid();
        }

        notif.ReadAt = DateTime.UtcNow;
        notif.ChannelStatus = "read";
        dbContext.BrokerNotifications.Update(notif);
        await dbContext.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "Notification successfully marked as read",
            id = id
        });
    }

    [HttpPost("mark-all-read")]
    public async Task<IActionResult> MarkAllRead([FromBody] MarkAllReadRequestDto request)
    {
        if (request == null || !Guid.TryParse(request.UserId, out var userGuid) || !ValidateUserAccess(userGuid))
        {
            return Forbid();
        }

        try
        {
            await _notificationService.MarkAllAsReadAsync(userGuid);
            return Ok(new
            {
                success = true,
                message = "All user notifications marked as read",
                unreadCount = 0
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark all notifications as read for user {UserId}", request.UserId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("{id}/unlock-broker")]
    public async Task<IActionResult> UnlockBroker(
        [FromRoute] Guid id,
        [FromQuery] string userId)
    {
        if (!Guid.TryParse(userId, out var userGuid) || !ValidateUserAccess(userGuid))
        {
            return Forbid();
        }

        try
        {
            var response = await _notificationService.UnlockBrokerContactAsync(id, userGuid);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            // Return user friendly validations (e.g. insufficient tokens)
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to unlock broker contact for notification {NotificationId} and user {UserId}", id, userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private bool ValidateUserAccess(Guid userId)
    {
        var tokenUserIdStr = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (Guid.TryParse(tokenUserIdStr, out var tokenUserId))
        {
            return tokenUserId == userId;
        }
        return false;
    }
}

public class MarkAllReadRequestDto
{
    public string UserId { get; set; } = string.Empty;
}
