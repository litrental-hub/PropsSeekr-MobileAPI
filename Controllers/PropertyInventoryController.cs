using System;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Inventory;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/property-inventory")]
[Route("api/v1/properties")]
public class PropertyInventoryController : ControllerBase
{
    private readonly IPropertyInventoryService _propertyInventoryService;
    private readonly ILogger<PropertyInventoryController> _logger;

    public PropertyInventoryController(
        IPropertyInventoryService propertyInventoryService,
        ILogger<PropertyInventoryController> logger)
    {
        _propertyInventoryService = propertyInventoryService;
        _logger = logger;
    }

    [HttpGet("my-listings")]
    public async Task<IActionResult> GetMyPropertyListings(
        [FromQuery] Guid? userId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var authUserId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        // Tenant Isolation Check
        if (userId.HasValue && userId.Value != authUserId)
        {
            return Forbid("Access denied. Logged-in user ID does not match the requested userId.");
        }

        var targetUserId = userId ?? authUserId;

        try
        {
            var response = await _propertyInventoryService.GetMyPropertiesWithMetricsAsync(targetUserId, status, page, limit);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch property listings for user {UserId}", targetUserId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("add-listing")]
    public async Task<IActionResult> CreatePropertyListing([FromBody] AddPropertyRequestDto request)
    {
        if (!TryGetCurrentUserId(out var authUserId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        // Tenant Isolation Check
        if (request.UserId != authUserId)
        {
            return Forbid("Access denied. Logged-in user ID does not match the request payload userId.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var response = await _propertyInventoryService.AddPropertyAsync(request);
            return CreatedAtAction(nameof(GetMyPropertyListings), new { userId = authUserId, page = 1, limit = 20 }, response);
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create property listing for user {UserId}", authUserId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdatePropertyStatus(
        [FromRoute] Guid id,
        [FromQuery] Guid userId,
        [FromBody] StatusUpdateRequestDto body)
    {
        if (!TryGetCurrentUserId(out var authUserId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        // Tenant Isolation Check
        if (userId != authUserId)
        {
            return Forbid("Access denied. Logged-in user ID does not match the query parameter userId.");
        }

        if (body == null || string.IsNullOrWhiteSpace(body.Status))
        {
            return BadRequest(new { success = false, message = "Status value is required." });
        }

        try
        {
            var success = await _propertyInventoryService.UpdatePropertyStatusAsync(id, authUserId, body.Status);
            if (!success)
            {
                return NotFound(new { success = false, message = "Property listing not found or not owned by user." });
            }

            return Ok(new
            {
                success = true,
                message = $"Property status updated to {body.Status}.",
                id = id,
                status = body.Status,
                updatedAt = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update status for property {PropertyId}", id);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out userId);
    }
}

public class StatusUpdateRequestDto
{
    [Required]
    public string Status { get; set; } = string.Empty;
}
