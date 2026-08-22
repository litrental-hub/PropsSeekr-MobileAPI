using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/matching")]
public class MatchingController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public MatchingController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost("run")]
    [AllowAnonymous] // Internal endpoint (called by ingestion/lambda workers)
    public async Task<IActionResult> RunMatching([FromBody] RunMatchRequestDto request)
    {
        if (!request.ListingId.HasValue && !request.RequirementId.HasValue)
        {
            return BadRequest(new { success = false, message = "Either listing_id or requirement_id must be provided." });
        }

        var matchedIds = new List<int>();

        if (request.ListingId.HasValue)
        {
            var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == request.ListingId.Value);
            if (listing == null)
            {
                return NotFound(new { success = false, message = $"Listing ID {request.ListingId.Value} not found." });
            }

            // Find matching requirements
            var matchingRequirements = await _dbContext.Requirements
                .Where(r => r.BrokerId != listing.BrokerId &&
                            r.PropertyType != null && listing.PropertyType != null &&
                            r.PropertyType.ToLower() == listing.PropertyType.ToLower() &&
                            r.Status == "active")
                .ToListAsync();

            // Filter compatible budgets manually to handle decimal formatting differences if needed
            var matchesToCreate = matchingRequirements.Where(r => 
                !r.Budget.HasValue || !listing.Price.HasValue || r.Budget.Value >= listing.Price.Value
            ).ToList();

            foreach (var req in matchesToCreate)
            {
                // Check duplicate
                var exists = await _dbContext.Matches.AnyAsync(m => m.ListingId == listing.Id && m.RequirementId == req.Id);
                if (!exists)
                {
                    var match = new Match
                    {
                        ListingId = listing.Id,
                        RequirementId = req.Id,
                        ListingBrokerId = listing.BrokerId,
                        RequirementBrokerId = req.BrokerId,
                        MatchScore = 95.00m,
                        State = "matched",
                        Status = "matched",
                        CreatedAt = DateTime.UtcNow,
                        StatusUpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.Matches.Add(match);
                    await _dbContext.SaveChangesAsync(); // Fetch match ID

                    matchedIds.Add(match.Id);

                    // Send Notifications
                    await CreateNotificationsForMatch(match);
                }
            }
        }
        else if (request.RequirementId.HasValue)
        {
            var req = await _dbContext.Requirements.FirstOrDefaultAsync(r => r.Id == request.RequirementId.Value);
            if (req == null)
            {
                return NotFound(new { success = false, message = $"Requirement ID {request.RequirementId.Value} not found." });
            }

            // Find matching listings
            var matchingListings = await _dbContext.Listings
                .Where(l => l.BrokerId != req.BrokerId &&
                            l.PropertyType != null && req.PropertyType != null &&
                            l.PropertyType.ToLower() == req.PropertyType.ToLower() &&
                            l.Status == "active")
                .ToListAsync();

            var matchesToCreate = matchingListings.Where(l =>
                !req.Budget.HasValue || !l.Price.HasValue || req.Budget.Value >= l.Price.Value
            ).ToList();

            foreach (var listing in matchesToCreate)
            {
                // Check duplicate
                var exists = await _dbContext.Matches.AnyAsync(m => m.ListingId == listing.Id && m.RequirementId == req.Id);
                if (!exists)
                {
                    var match = new Match
                    {
                        ListingId = listing.Id,
                        RequirementId = req.Id,
                        ListingBrokerId = listing.BrokerId,
                        RequirementBrokerId = req.BrokerId,
                        MatchScore = 95.00m,
                        State = "matched",
                        Status = "matched",
                        CreatedAt = DateTime.UtcNow,
                        StatusUpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.Matches.Add(match);
                    await _dbContext.SaveChangesAsync();

                    matchedIds.Add(match.Id);

                    // Send Notifications
                    await CreateNotificationsForMatch(match);
                }
            }
        }

        return Ok(new
        {
            success = true,
            message = $"Matching run completed. {matchedIds.Count} new matches identified.",
            match_ids = matchedIds
        });
    }

    [HttpPost("expire-check")]
    [AllowAnonymous] // Internal EventBridge cron integration
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
                match.Status = "expired";
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

    private async Task CreateNotificationsForMatch(Match match)
    {
        // 1. Notify Listing Broker
        var notifA = new BrokerNotification
        {
            BrokerId = match.ListingBrokerId,
            Type = "match_found",
            Channel = "in_app",
            PayloadJson = JsonSerializer.Serialize(new { match_id = match.Id, role = "listing" }),
            ChannelStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };

        // 2. Notify Requirement Broker
        var notifB = new BrokerNotification
        {
            BrokerId = match.RequirementBrokerId,
            Type = "match_found",
            Channel = "in_app",
            PayloadJson = JsonSerializer.Serialize(new { match_id = match.Id, role = "requirement" }),
            ChannelStatus = "pending",
            CreatedAt = DateTime.UtcNow
        };

        _dbContext.BrokerNotifications.Add(notifA);
        _dbContext.BrokerNotifications.Add(notifB);
        await _dbContext.SaveChangesAsync();
    }
}
