using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Matches;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/user-matches")]
public class UserMatchesController : ControllerBase
{
    private readonly IUserMatchesService _userMatchesService;
    private readonly ILogger<UserMatchesController> _logger;

    public UserMatchesController(
        IUserMatchesService userMatchesService,
        ILogger<UserMatchesController> logger)
    {
        _userMatchesService = userMatchesService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetUserMatches()
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var response = await _userMatchesService.GetUserMatchesAsync(userId);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving user matches for user {UserId}", userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("unlock")]
    public async Task<IActionResult> UnlockProperty([FromBody] UnlockPropertyRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var response = await _userMatchesService.UnlockPropertyAsync(userId, request);
            if (!response.Success)
            {
                return BadRequest(response);
            }
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error unlocking property {PropertyId} for user {UserId}", request.PropertyRequestId, userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("unlocked")]
    public async Task<IActionResult> GetUnlockedProperties()
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var response = await _userMatchesService.GetUnlockedPropertiesAsync(userId);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error retrieving unlocked properties for user {UserId}", userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out userId);
    }
}
