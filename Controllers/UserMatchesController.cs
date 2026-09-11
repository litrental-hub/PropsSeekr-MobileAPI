using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
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

    [HttpGet("matches/{matchId}/details")]
    [ProducesResponseType(typeof(MatchDetailResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMatchDetails([FromRoute] int matchId)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized(new { message = "Invalid authenticated user." });

        try
        {
            return Ok(await _userMatchesService.GetMatchDetailsAsync(userId, matchId, User.IsInRole("Admin")));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { success = false, message = ex.Message });
        }
    }

    [HttpGet("matches/{matchId}/media/{mediaId:long}")]
    public async Task<IActionResult> GetMatchMedia(
        [FromRoute] int matchId,
        [FromRoute] long mediaId,
        [FromServices] AppDbContext dbContext,
        [FromServices] IListingMediaStorage mediaStorage)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized(new { message = "Invalid authenticated user." });

        var brokerId = User.IsInRole("Admin")
            ? null
            : await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!User.IsInRole("Admin") && !brokerId.HasValue)
            return Unauthorized(new { message = "No broker profile is linked to this account." });

        var match = await dbContext.Matches.AsNoTracking()
            .Where(item => item.Id == matchId)
            .Select(item => new { item.ListingId, item.ListingBrokerId, item.RequirementBrokerId })
            .SingleOrDefaultAsync();
        if (match is null) return NotFound(new { success = false, message = "Match not found." });
        if (brokerId.HasValue && match.ListingBrokerId != brokerId && match.RequirementBrokerId != brokerId)
            return Forbid();

        if (!User.IsInRole("Admin") && brokerId != match.ListingBrokerId)
        {
            var preference = await dbContext.ListingDetails.AsNoTracking()
                .Where(detail => detail.ListingId == match.ListingId)
                .Select(detail => detail.PhotoSharingPreference)
                .SingleOrDefaultAsync();
            var revealed = await dbContext.Reveals.AsNoTracking().AnyAsync(item => item.MatchId == matchId);
            var canView = string.Equals(preference, "SHARE_FREELY", StringComparison.OrdinalIgnoreCase) ||
                          (string.Equals(preference, "ON_REQUEST", StringComparison.OrdinalIgnoreCase) && revealed);
            if (!canView) return Forbid();
        }

        var media = await dbContext.ListingMedia.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == mediaId && item.ListingId == match.ListingId);
        if (media is null) return NotFound(new { success = false, message = "Media not found." });

        var stream = await mediaStorage.OpenReadAsync(media.StoragePath, HttpContext.RequestAborted);
        if (stream is null) return NotFound(new { success = false, message = "Media file is unavailable." });

        Response.Headers.XContentTypeOptions = "nosniff";
        return File(stream, media.MimeType, enableRangeProcessing: media.MediaType == "video");
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

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out userId);
    }
}
