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
    private readonly IUnlockService _unlockService;
    private readonly ILogger<UserMatchesController> _logger;

    public UserMatchesController(
        IUserMatchesService userMatchesService,
        IUnlockService unlockService,
        ILogger<UserMatchesController> logger)
    {
        _userMatchesService = userMatchesService;
        _unlockService = unlockService;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetUserMatches([FromQuery] string? type, [FromQuery] string? transactionType)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var txType = type ?? transactionType;
            var response = await _userMatchesService.GetUserMatchesAsync(userId, txType);
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

    /// <summary>
    /// Confirm match pre-conditions before reveal (dual handshake step 1).
    /// Both brokers must confirm within window period.
    /// </summary>
    [HttpPost("matches/{matchId}/confirm")]
    public async Task<IActionResult> ConfirmMatch(int matchId, [FromBody] MatchConfirmationRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        if (matchId != request.MatchId)
        {
            return BadRequest(new { message = "MatchId mismatch." });
        }

        try
        {
            var response = await _unlockService.ConfirmMatchAsync(userId, request);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming match {MatchId} for user {UserId}", matchId, userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Reveal/unlock contact details for a confirmed match.
    /// Deducts credits and creates reveal record.
    /// </summary>
    [HttpPost("matches/{matchId}/reveal")]
    public async Task<IActionResult> RevealMatch(int matchId, [FromBody] UnlockPropertyRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        if (matchId != request.MatchId)
        {
            return BadRequest(new { message = "MatchId mismatch." });
        }

        try
        {
            var response = await _unlockService.UnlockMatchAsync(userId, request);
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
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error revealing match {MatchId} for user {UserId}", matchId, userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Legacy endpoint - replaced by /matches/{matchId}/reveal
    /// Kept for backward compatibility during migration.
    /// </summary>
    [HttpPost("unlock")]
    [Obsolete("Use POST /matches/{matchId}/reveal instead")]
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
            _logger.LogError(ex, "Error unlocking match {MatchId} for user {UserId}", request.MatchId, userId);
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

