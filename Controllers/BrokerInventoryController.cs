using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

/// <summary>
/// Inventory read endpoints for the mobile application's one-parent-to-many-matches UI.
/// The authenticated account is the sole ownership boundary; no broker id is accepted
/// from the client.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/inventory")]
public sealed class BrokerInventoryController : ControllerBase
{
    private readonly IBrokerInventoryService _inventoryService;

    public BrokerInventoryController(IBrokerInventoryService inventoryService) => _inventoryService = inventoryService;

    [HttpGet("my-listings")]
    public async Task<IActionResult> GetMyListings([FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var userId)) return Unauthorized(new { message = "Invalid authenticated user." });
        return Ok(await _inventoryService.GetMyListingsWithMatchesAsync(userId, page, limit));
    }

    [HttpGet("my-requirements")]
    public async Task<IActionResult> GetMyRequirements([FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var userId)) return Unauthorized(new { message = "Invalid authenticated user." });
        return Ok(await _inventoryService.GetMyRequirementsWithMatchesAsync(userId, page, limit));
    }

    private bool TryGetCurrentUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
