using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

/// <summary>
/// Backward-compatible route for older clients. Business logic is delegated to
/// the canonical unlock service used by /api/v1/user-matches.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/matches")]
public sealed class HandshakeController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IUnlockService _unlockService;
    private readonly IBrokerIdentityService _brokerIdentityService;

    public HandshakeController(
        AppDbContext db,
        IUnlockService unlockService,
        IBrokerIdentityService brokerIdentityService)
    {
        _db = db;
        _unlockService = unlockService;
        _brokerIdentityService = brokerIdentityService;
    }

    [HttpPost("{matchId}/confirm")]
    [Obsolete("Use POST /api/v1/user-matches/matches/{matchId}/confirm instead.")]
    public async Task<IActionResult> ConfirmMatch(int matchId, [FromBody] ConfirmMatchRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized(new { message = "Invalid authenticated user." });
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
            return Unauthorized(new { message = "No broker profile is linked to this account." });

        try
        {
            var response = await _unlockService.ConfirmMatchAsync(brokerId.Value, new MatchConfirmationRequestDto
            {
                MatchId = matchId,
                BrokerId = brokerId.Value,
                AvailabilityConfirmed = request.AvailabilityConfirmed,
                PriceValid = request.PriceValid,
                PriceNegotiable = request.PriceNegotiable,
                ReadyToConnect = request.ReadyToConnect
            });
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{matchId}/confirmations")]
    [Obsolete("Use GET /api/v1/user-matches/matches/{matchId}/details instead.")]
    public async Task<IActionResult> GetMatchConfirmations(int matchId)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized(new { message = "Invalid authenticated user." });
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
            return Unauthorized(new { message = "No broker profile is linked to this account." });

        var match = await _db.Matches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == matchId);
        if (match is null) return NotFound(new { success = false, message = "Match not found." });
        if (match.ListingBrokerId != brokerId && match.RequirementBrokerId != brokerId) return Forbid();

        var confirmations = await _db.MatchConfirmations
            .AsNoTracking()
            .Where(c => c.MatchId == matchId)
            .Select(c => new
            {
                broker_id = c.BrokerId,
                availability_confirmed = c.AvailabilityConfirmed,
                price_valid = c.PriceValid,
                price_negotiable = c.PriceNegotiable,
                ready_to_connect = c.ReadyToConnect,
                confirmed_at = c.ConfirmedAt,
                window_expires_at = c.WindowExpiresAt
            })
            .ToListAsync();

        return Ok(new { match_id = matchId, confirmations });
    }

    private bool TryGetCurrentUserId(out Guid userId) =>
        Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
}
