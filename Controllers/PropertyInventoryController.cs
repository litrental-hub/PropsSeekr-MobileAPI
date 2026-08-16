using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Inventory;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize(Policy = "CustomerPolicy")]
[ApiController]
[Route("api/v1/property-inventory")]
public class PropertyInventoryController : ControllerBase
{
    private readonly IPropertyInventoryService _propertyInventoryService;
    private readonly ILogger<PropertyInventoryController> _logger;
    private readonly ICurrentUserContext _currentUser;

    public PropertyInventoryController(
        IPropertyInventoryService propertyInventoryService,
        ILogger<PropertyInventoryController> logger,
        ICurrentUserContext currentUser)
    {
        _propertyInventoryService = propertyInventoryService;
        _logger = logger;
        _currentUser = currentUser;
    }

    [HttpGet("my-listings")]
    public async Task<IActionResult> GetMyPropertyListings([FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var response = await _propertyInventoryService.GetMyPropertyListingsAsync(userId, page, limit);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch property listings for user {UserId}", userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("listings")]
    public async Task<IActionResult> CreatePropertyListing([FromBody] CreatePropertyListingRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var response = await _propertyInventoryService.CreatePropertyListingAsync(userId, request);
            return CreatedAtAction(nameof(GetMyPropertyListings), new { page = 1, limit = 20 }, response);
        }
        catch (ValidationException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create property listing for user {UserId}", userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        return _currentUser.TryGetLocalUserId(out userId);
    }
}
