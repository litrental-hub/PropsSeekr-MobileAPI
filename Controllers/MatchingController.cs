using Microsoft.AspNetCore.Mvc;
using PropSeekr.Attributes;
using PropSeekr.DTOs.Matches;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/matching")]
public class MatchingController : ControllerBase
{
    private readonly IAutomatedMatchingService _matchingService;
    private readonly IUnlockService _unlockService;

    public MatchingController(IAutomatedMatchingService matchingService, IUnlockService unlockService)
    {
        _matchingService = matchingService;
        _unlockService = unlockService;
    }

    [HttpPost("run")]
    [RequireInternalServiceKey]
    public async Task<IActionResult> RunMatching([FromBody] RunMatchRequestDto request)
    {
        if (!request.ListingId.HasValue && !request.RequirementId.HasValue)
        {
            return BadRequest(new { success = false, message = "Either listing_id or requirement_id must be provided." });
        }

        try
        {
            IReadOnlyList<int> matchedIds = request.ListingId.HasValue
                ? await _matchingService.RunForListingAsync(request.ListingId.Value)
                : await _matchingService.RunForRequirementAsync(request.RequirementId!.Value);

            return Ok(new
            {
                success = true,
                message = $"Matching run completed. {matchedIds.Count} matches identified.",
                match_ids = matchedIds
            });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("expire-check")]
    [RequireInternalServiceKey]
    public async Task<IActionResult> ExpireCheck([FromQuery] int batchSize = 200)
    {
        batchSize = Math.Clamp(batchSize, 1, 500);
        var expiredMatchesCount = await _unlockService.ExpirePendingRequestsAsync(batchSize);
        return Ok(new
        {
            success = true,
            message = $"Completed expiration check. {expiredMatchesCount} requests expired without a credit deduction.",
            expired_count = expiredMatchesCount,
            batch_size = batchSize
        });
    }
}
