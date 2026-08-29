using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Attributes;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/matching")]
public class MatchingController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IAutomatedMatchingService _matchingService;

    public MatchingController(AppDbContext dbContext, IAutomatedMatchingService matchingService)
    {
        _dbContext = dbContext;
        _matchingService = matchingService;
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
                message = $"Matching run completed. {matchedIds.Count} new matches identified.",
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
    public async Task<IActionResult> ExpireCheck()
    {
        var expiredMatchesCount = 0;
        
        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            // Find all pending confirmations that have expired
            var expiredConfirmations = await _dbContext.MatchConfirmations
                .Include(c => c.Match)
                .Where(c => c.WindowExpiresAt < DateTime.UtcNow && c.Match != null && c.Match.State == "pending_confirmation")
                .ToListAsync();

            var matchesToProcess = expiredConfirmations
                .Select(c => c.Match)
                .Distinct()
                .ToList();

            foreach (var match in matchesToProcess)
            {
                if (match == null) continue;

                // Find both confirmations for this match
                var confirmations = await _dbContext.MatchConfirmations
                    .Where(c => c.MatchId == match.Id)
                    .ToListAsync();

                // Find which broker confirmed and which did not
                var listingConfirm = confirmations.FirstOrDefault(c => c.BrokerId == match.ListingBrokerId);
                var reqConfirm = confirmations.FirstOrDefault(c => c.BrokerId == match.RequirementBrokerId);

                // If one confirmed but other didn't, penalize the other and notify both
                if (listingConfirm != null && listingConfirm.ConfirmedAt != null
                    && (reqConfirm == null || reqConfirm.ConfirmedAt == null))
                {
                    // Penalize requirement broker
                    var broker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.Id == match.RequirementBrokerId);
                    if (broker != null)
                    {
                        broker.ConfirmationComplianceRate = Math.Max(0.00m, broker.ConfirmationComplianceRate - 5.00m);
                        _dbContext.Brokers.Update(broker);
                    }

                    // Notify the non-confirming broker: please re-confirm
                    _dbContext.BrokerNotifications.Add(new BrokerNotification
                    {
                        BrokerId = match.RequirementBrokerId,
                        Type = "confirm_expired_resend",
                        Channel = "in_app",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            match_id = match.Id,
                            message = "You did not confirm the match in time. The listing broker had confirmed. Please re-initiate contact to proceed.",
                            role = "requirement"
                        }),
                        ChannelStatus = "pending",
                        CreatedAt = DateTime.UtcNow
                    });

                    // Notify the confirming broker: window expired, counterparty didn't respond
                    _dbContext.BrokerNotifications.Add(new BrokerNotification
                    {
                        BrokerId = match.ListingBrokerId,
                        Type = "confirm_expired_counterparty",
                        Channel = "in_app",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            match_id = match.Id,
                            message = "The 4-hour confirmation window expired. The buyer broker did not confirm in time.",
                            role = "listing"
                        }),
                        ChannelStatus = "pending",
                        CreatedAt = DateTime.UtcNow
                    });
                }
                else if (reqConfirm != null && reqConfirm.ConfirmedAt != null
                         && (listingConfirm == null || listingConfirm.ConfirmedAt == null))
                {
                    // Penalize listing broker
                    var broker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.Id == match.ListingBrokerId);
                    if (broker != null)
                    {
                        broker.ConfirmationComplianceRate = Math.Max(0.00m, broker.ConfirmationComplianceRate - 5.00m);
                        _dbContext.Brokers.Update(broker);
                    }

                    // Notify the non-confirming broker: please re-confirm
                    _dbContext.BrokerNotifications.Add(new BrokerNotification
                    {
                        BrokerId = match.ListingBrokerId,
                        Type = "confirm_expired_resend",
                        Channel = "in_app",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            match_id = match.Id,
                            message = "You did not confirm the match in time. The buyer broker had confirmed. Please re-initiate contact to proceed.",
                            role = "listing"
                        }),
                        ChannelStatus = "pending",
                        CreatedAt = DateTime.UtcNow
                    });

                    // Notify the confirming broker: window expired, counterparty didn't respond
                    _dbContext.BrokerNotifications.Add(new BrokerNotification
                    {
                        BrokerId = match.RequirementBrokerId,
                        Type = "confirm_expired_counterparty",
                        Channel = "in_app",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            match_id = match.Id,
                            message = "The 4-hour confirmation window expired. The listing broker did not confirm in time.",
                            role = "requirement"
                        }),
                        ChannelStatus = "pending",
                        CreatedAt = DateTime.UtcNow
                    });
                }

                // Update match state
                match.State = "expired";
                match.StatusUpdatedAt = DateTime.UtcNow;
                _dbContext.Matches.Update(match);

                expiredMatchesCount++;
            }

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new
            {
                success = true,
                message = $"Completed expiration check. {expiredMatchesCount} matches marked as expired.",
                expired_count = expiredMatchesCount
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return BadRequest(new { success = false, message = ex.Message });
        }
    }
}
