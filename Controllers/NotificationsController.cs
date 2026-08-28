using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PropSeekr.Controllers;

/// <summary>
/// Tombstone for the GUID/User.Credits notification model. Canonical notifications
/// are broker-owned and live at /api/v1/brokers/{brokerId}/notifications.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/notifications")]
public class NotificationsController : ControllerBase
{
    [HttpGet]
    public IActionResult GetNotifications() => Retired();

    [HttpPatch("{id}/read")]
    public IActionResult MarkAsRead([FromRoute] Guid id) => Retired();

    [HttpPost("mark-all-read")]
    public IActionResult MarkAllRead() => Retired();

    [HttpPost("{id}/unlock-broker")]
    public IActionResult UnlockBroker([FromRoute] Guid id) => Retired();

    private ObjectResult Retired() => StatusCode(StatusCodes.Status410Gone, new
    {
        message = "GUID notification and notification-based unlock routes are retired. Use /api/v1/brokers/{brokerId}/notifications and the canonical match confirmation flow."
    });
}
