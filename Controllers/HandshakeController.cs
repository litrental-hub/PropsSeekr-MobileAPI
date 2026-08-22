using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/matches")]
public class HandshakeController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<HandshakeController> _logger;

    public HandshakeController(AppDbContext dbContext, ILogger<HandshakeController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpPost("{matchId}/confirm")]
    public async Task<IActionResult> ConfirmMatch([FromRoute] int matchId, [FromBody] ConfirmMatchRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != request.BrokerId)
        {
            return Unauthorized(new { message = "You can only confirm matches for your own broker profile." });
        }

        var match = await _dbContext.Matches
            .Include(m => m.Listing)
            .Include(m => m.Requirement)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        if (match == null)
        {
            return NotFound(new { success = false, message = "Match not found." });
        }

        if (match.ListingBrokerId != request.BrokerId && match.RequirementBrokerId != request.BrokerId)
        {
            return Forbid();
        }

        // Prevent confirming an already expired match
        if (string.Equals(match.State, "expired", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { success = false, message = "This match has expired. Please request a fresh confirmation." });
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            // 4-hour confirmation window
            var windowExpiry = DateTime.UtcNow.AddHours(4);

            // 1. Upsert MatchConfirmation for calling broker
            var confirmation = await _dbContext.MatchConfirmations
                .FirstOrDefaultAsync(c => c.MatchId == matchId && c.BrokerId == request.BrokerId);

            if (confirmation == null)
            {
                confirmation = new MatchConfirmation
                {
                    MatchId = matchId,
                    BrokerId = request.BrokerId,
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.MatchConfirmations.Add(confirmation);
            }

            confirmation.AvailabilityConfirmed = request.AvailabilityConfirmed;
            confirmation.PriceValid = request.PriceValid;
            confirmation.PriceNegotiable = request.PriceNegotiable;
            confirmation.ReadyToConnect = request.ReadyToConnect;
            confirmation.ConfirmedAt = DateTime.UtcNow;
            confirmation.WindowExpiresAt = windowExpiry;

            await _dbContext.SaveChangesAsync();

            // 2. Check if counterparty has confirmed
            var counterpartyId = (request.BrokerId == match.ListingBrokerId)
                ? match.RequirementBrokerId
                : match.ListingBrokerId;

            var counterpartyConfirmation = await _dbContext.MatchConfirmations
                .FirstOrDefaultAsync(c => c.MatchId == matchId && c.BrokerId == counterpartyId);

            if (counterpartyConfirmation == null || counterpartyConfirmation.ConfirmedAt == null)
            {
                // First confirmation: transition state to pending_confirmation
                match.State = "pending_confirmation";
                match.Status = "pending_confirmation";
                match.StatusUpdatedAt = DateTime.UtcNow;

                // Check if the counterparty broker is registered as a User
                var isCounterpartyRegistered = await _dbContext.Users.AnyAsync(u => u.BrokerId == counterpartyId);
                if (isCounterpartyRegistered)
                {
                    // Notify the counterparty broker to confirm within 4 hours
                    var pendingNotif = new BrokerNotification
                    {
                        BrokerId = counterpartyId,
                        Type = "confirm_pending",
                        Channel = "in_app",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            match_id = match.Id,
                            message = "Your counterparty has confirmed the match. Please confirm within 4 hours to unlock contact details.",
                            window_expires_at = windowExpiry,
                            role = (counterpartyId == match.ListingBrokerId) ? "listing" : "requirement"
                        }),
                        ChannelStatus = "pending",
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.BrokerNotifications.Add(pendingNotif);
                }
                else
                {
                    // Retrieve phone number of the unregistered broker
                    var counterpartyBroker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.Id == counterpartyId);
                    var phone = counterpartyBroker?.PhoneNumber ?? "UNKNOWN";
                    
                    // Simulate WhatsApp Invitation
                    var invitationMsg = $"A property listing/requirement has been matched by another broker! They want to confirm the connection. To view details and confirm, please register here: https://pr-bcac82da45b24e66bd200af09afde77b.ecs.ap-south-1.on.aws/register?redirect=confirm&matchId={match.Id}";
                    
                    // Log WhatsApp Bypass to console/log
                    Console.WriteLine($"[WhatsApp Integration Bypass] Sending message to {phone}: {invitationMsg}");
                    _logger.LogInformation("[WhatsApp Integration Bypass] Sending message to {Phone}: {Message}", phone, invitationMsg);

                    // Add a pending notification in DB as well, so once they register they immediately see it
                    var pendingNotif = new BrokerNotification
                    {
                        BrokerId = counterpartyId,
                        Type = "confirm_pending",
                        Channel = "whatsapp",
                        PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                        {
                            match_id = match.Id,
                            message = "Your counterparty has confirmed the match. Please confirm within 4 hours to unlock contact details.",
                            window_expires_at = windowExpiry,
                            role = (counterpartyId == match.ListingBrokerId) ? "listing" : "requirement",
                            whatsapp_simulated_text = invitationMsg
                        }),
                        ChannelStatus = "sent",
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.BrokerNotifications.Add(pendingNotif);
                }

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new
                {
                    match_id = match.Id,
                    state = "pending_confirmation",
                    window_expires_at = windowExpiry
                });
            }
            else
            {
                // Second confirmation: Trigger Reveal/Handshake — both brokers confirmed
                var walletA = await _dbContext.CreditWallets.FirstOrDefaultAsync(w => w.BrokerId == match.ListingBrokerId);
                var walletB = await _dbContext.CreditWallets.FirstOrDefaultAsync(w => w.BrokerId == match.RequirementBrokerId);

                // Balance check — abort before touching anything if insufficient
                if (walletA == null || (walletA.FreeCreditsBalance + walletA.PaidCreditsBalance) < 1)
                {
                    return BadRequest(new
                    {
                        error = "insufficient_credits",
                        broker_id = match.ListingBrokerId,
                        required = 1,
                        available = walletA != null ? walletA.FreeCreditsBalance + walletA.PaidCreditsBalance : 0
                    });
                }

                if (walletB == null || (walletB.FreeCreditsBalance + walletB.PaidCreditsBalance) < 1)
                {
                    return BadRequest(new
                    {
                        error = "insufficient_credits",
                        broker_id = match.RequirementBrokerId,
                        required = 1,
                        available = walletB != null ? walletB.FreeCreditsBalance + walletB.PaidCreditsBalance : 0
                    });
                }

                // Deduct 1 credit from Listing Broker (free first, then paid)
                DeductWalletCredits(walletA);
                _dbContext.CreditTransactions.Add(new CreditTransaction
                {
                    BrokerId = match.ListingBrokerId,
                    Type = "debit",
                    Amount = 1,
                    BalanceAfter = walletA.FreeCreditsBalance + walletA.PaidCreditsBalance,
                    ReferenceType = "reveal",
                    Notes = $"1 credit deducted for contact unlock on match #{match.Id}",
                    CreatedAt = DateTime.UtcNow
                });
                _dbContext.CreditWallets.Update(walletA);

                // Deduct 1 credit from Requirement Broker
                DeductWalletCredits(walletB);
                _dbContext.CreditTransactions.Add(new CreditTransaction
                {
                    BrokerId = match.RequirementBrokerId,
                    Type = "debit",
                    Amount = 1,
                    BalanceAfter = walletB.FreeCreditsBalance + walletB.PaidCreditsBalance,
                    ReferenceType = "reveal",
                    Notes = $"1 credit deducted for contact unlock on match #{match.Id}",
                    CreatedAt = DateTime.UtcNow
                });
                _dbContext.CreditWallets.Update(walletB);

                // Create Reveal record
                var reveal = new Reveal
                {
                    MatchId = match.Id,
                    RevealedAt = DateTime.UtcNow
                };
                _dbContext.Reveals.Add(reveal);

                // Update Match state to confirmed
                match.State = "confirmed";
                match.Status = "confirmed";
                match.StatusUpdatedAt = DateTime.UtcNow;

                // Sync freshness timestamps
                if (match.Listing != null)
                {
                    match.Listing.LastConfirmedAt = DateTime.UtcNow;
                    _dbContext.Listings.Update(match.Listing);
                }
                if (match.Requirement != null)
                {
                    match.Requirement.LastConfirmedAt = DateTime.UtcNow;
                    _dbContext.Requirements.Update(match.Requirement);
                }

                // Notify BOTH brokers that contacts are now unlocked
                _dbContext.BrokerNotifications.Add(new BrokerNotification
                {
                    BrokerId = match.ListingBrokerId,
                    Type = "contact_unlocked",
                    Channel = "in_app",
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        match_id = match.Id,
                        message = "Both brokers confirmed! Contact details are now unlocked.",
                        role = "listing",
                        credits_deducted = 1,
                        remaining_free_credits = walletA.FreeCreditsBalance,
                        remaining_paid_credits = walletA.PaidCreditsBalance
                    }),
                    ChannelStatus = "pending",
                    CreatedAt = DateTime.UtcNow
                });

                _dbContext.BrokerNotifications.Add(new BrokerNotification
                {
                    BrokerId = match.RequirementBrokerId,
                    Type = "contact_unlocked",
                    Channel = "in_app",
                    PayloadJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        match_id = match.Id,
                        message = "Both brokers confirmed! Contact details are now unlocked.",
                        role = "requirement",
                        credits_deducted = 1,
                        remaining_free_credits = walletB.FreeCreditsBalance,
                        remaining_paid_credits = walletB.PaidCreditsBalance
                    }),
                    ChannelStatus = "pending",
                    CreatedAt = DateTime.UtcNow
                });

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();

                return Ok(new
                {
                    match_id = match.Id,
                    state = "confirmed",
                    window_expires_at = (DateTime?)null
                });
            }
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Error confirming match {MatchId}", matchId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{matchId}/confirmations")]
    public async Task<IActionResult> GetMatchConfirmations([FromRoute] int matchId)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue)
        {
            return Unauthorized(new { message = "Calling user profile is not linked to any broker." });
        }

        var brokerId = callerUser.BrokerId.Value;

        var match = await _dbContext.Matches.FirstOrDefaultAsync(m => m.Id == matchId);
        if (match == null)
        {
            return NotFound(new { success = false, message = "Match not found." });
        }

        // Access check
        if (match.ListingBrokerId != brokerId && match.RequirementBrokerId != brokerId)
        {
            return Forbid();
        }

        var confirmations = await _dbContext.MatchConfirmations
            .AsNoTracking()
            .Where(c => c.MatchId == matchId)
            .Select(c => new
            {
                broker_id = c.BrokerId,
                availability_confirmed = c.AvailabilityConfirmed,
                price_valid = c.PriceValid,
                price_negotiable = c.PriceNegotiable,
                ready_to_connect = c.ReadyToConnect,
                confirmed_at = c.ConfirmedAt
            })
            .ToListAsync();

        return Ok(new
        {
            match_id = matchId,
            confirmations = confirmations
        });
    }

    private void DeductWalletCredits(CreditWallet wallet)
    {
        if (wallet.FreeCreditsBalance >= 1)
        {
            wallet.FreeCreditsBalance -= 1;
        }
        else if (wallet.PaidCreditsBalance >= 1)
        {
            wallet.PaidCreditsBalance -= 1;
        }
        wallet.UpdatedAt = DateTime.UtcNow;
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out userId);
    }
}
