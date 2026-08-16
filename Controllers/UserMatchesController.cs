using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Matches;
using PropSeekr.Services.Interfaces;
using PropSeekr.Authorization;

namespace PropSeekr.Controllers;

[Authorize(Policy = "CustomerPolicy")]
[ApiController]
[Route("api/v1/user-matches")]
public class UserMatchesController : ControllerBase
{
    private readonly IUserMatchesService _userMatchesService;
    private readonly ILogger<UserMatchesController> _logger;
    private readonly ICurrentUserContext _currentUser;

    public UserMatchesController(
        IUserMatchesService userMatchesService,
        ILogger<UserMatchesController> logger,
        ICurrentUserContext currentUser)
    {
        _userMatchesService = userMatchesService;
        _logger = logger;
        _currentUser = currentUser;
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
    [Authorize(Policy = "AppAttestedSensitiveActionPolicy")]
    [AppAttestationPurpose("PropertyUnlock")]
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
        return _currentUser.TryGetLocalUserId(out userId);
    }
}
