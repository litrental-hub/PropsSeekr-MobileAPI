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
    private readonly IBrokerIdentityService _brokerIdentityService;

    public UserMatchesController(
        IUserMatchesService userMatchesService,
        IUnlockService unlockService,
        ILogger<UserMatchesController> logger,
        IBrokerIdentityService brokerIdentityService)
    {
        _userMatchesService = userMatchesService;
        _unlockService = unlockService;
        _logger = logger;
        _brokerIdentityService = brokerIdentityService;
    }

    [HttpGet]
    public async Task<IActionResult> GetUserMatches(
        [FromQuery] string? type,
        [FromQuery] string? transactionType,
        [FromQuery] int? listingId,
        [FromQuery] int? requirementId,
        [FromQuery] int? matchId,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            if (listingId is <= 0)
            {
                return BadRequest(new { success = false, message = "listingId must be greater than zero." });
            }
            if (requirementId is <= 0)
            {
                return BadRequest(new { success = false, message = "requirementId must be greater than zero." });
            }
            if (matchId is <= 0)
            {
                return BadRequest(new { success = false, message = "matchId must be greater than zero." });
            }

            var txType = type ?? transactionType;
            var response = User.IsInRole("Admin")
                ? await _userMatchesService.GetAllMatchesAsync(txType, listingId, requirementId, matchId, page, limit)
                : await _userMatchesService.GetUserMatchesAsync(userId, txType, listingId, requirementId, matchId, page, limit);
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
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue) return Unauthorized(new { message = "No broker profile is linked to this account." });
        request.BrokerId = brokerId.Value;

        try
        {
            var response = await _unlockService.ConfirmMatchAsync(brokerId.Value, request);
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error confirming match {MatchId} for user {UserId}", matchId, userId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    /// <summary>
    /// Reject a pending connection request. Rejection never deducts credits.
    /// </summary>
    [HttpPost("matches/{matchId}/reject")]
    public async Task<IActionResult> RejectMatch(int matchId, [FromBody] MatchRejectionRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }
        if (matchId != request.MatchId)
        {
            return BadRequest(new { message = "MatchId mismatch." });
        }

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
        {
            return Unauthorized(new { message = "No broker profile is linked to this account." });
        }

        try
        {
            return Ok(await _unlockService.RejectMatchAsync(brokerId.Value, request));
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error rejecting match {MatchId} for user {UserId}", matchId, userId);
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
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue) return Unauthorized(new { message = "No broker profile is linked to this account." });

        try
        {
            var response = await _unlockService.UnlockMatchAsync(brokerId.Value, request);
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
            return StatusCode(StatusCodes.Status403Forbidden, new { message = ex.Message });
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
    public async Task<IActionResult> GetUnlockedProperties([FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var response = await _userMatchesService.GetUnlockedPropertiesAsync(userId, page, limit);
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
