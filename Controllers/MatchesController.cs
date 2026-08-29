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
using PropSeekr.Services.Interfaces;
using PropSeekr.Services;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/matches")]
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IBrokerIdentityService _brokerIdentityService;

    public MatchesController(
        AppDbContext dbContext,
        IBrokerIdentityService brokerIdentityService)
    {
        _dbContext = dbContext;
        _brokerIdentityService = brokerIdentityService;
    }

    [HttpGet("{matchId}")]
    [Obsolete("Use GET /api/v1/user-matches/matches/{matchId}/details instead.")]
    public async Task<IActionResult> GetMatchDetails([FromRoute] int matchId)
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

        var match = await _dbContext.Matches
            .Include(m => m.Listing)
            .Include(m => m.Requirement)
            .Include(m => m.ListingBroker)
            .Include(m => m.RequirementBroker)
            .FirstOrDefaultAsync(m => m.Id == matchId);

        if (match == null)
        {
            return NotFound(new { success = false, message = "Match not found." });
        }

        // Access control check: Caller must be one of the two brokers in the match
        if (match.ListingBrokerId != brokerId && match.RequirementBrokerId != brokerId)
        {
            return Forbid();
        }

        // Confirmation alone must never expose contacts. A reveals row is the
        // sole authorization flag for unmasking counterparty information.
        var isConfirmed = await _dbContext.Reveals.AsNoTracking().AnyAsync(r => r.MatchId == match.Id);

        var listing = match.Listing;
        var requirement = match.Requirement;

        var isAvailable = (listing?.Status?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true || listing?.Status?.Equals("active", StringComparison.OrdinalIgnoreCase) == true) &&
                          (requirement?.Status?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true || requirement?.Status?.Equals("active", StringComparison.OrdinalIgnoreCase) == true);

        var scorePercent = (int)(match.MatchScore ?? 0);
        string matchQuality = "Excellent Match";
        if (scorePercent >= 95) matchQuality = "Excellent Match";
        else if (scorePercent >= 80) matchQuality = "Good Match";
        else matchQuality = "Fair Match";

        // Formatted Property
        var propertyFor = (listing?.ListingType?.Equals("RENT", StringComparison.OrdinalIgnoreCase) == true || 
                           listing?.ListingType?.Equals("RENTAL", StringComparison.OrdinalIgnoreCase) == true) ? "For Rent" : "For Sale";
        var propertyType = listing?.PropertyType ?? string.Empty;
        var propertyConfig = listing?.Configuration ?? string.Empty;
        var propertyPrice = FormatPrice(listing?.Price, listing?.PriceUnit, listing?.ListingType);
        var propertySize = FormatSize(listing?.Size);
        var propertyLocation = listing?.ProjectName ?? string.Empty;
        var propertyCity = listing?.City ?? string.Empty;
        var propertyBrokerName = isConfirmed ? (match.ListingBroker?.Name ?? $"Broker {match.ListingBroker?.PhoneNumber}") : "Broker (Masked)";
        var propertyBrokerPhone = isConfirmed ? (match.ListingBroker?.PhoneNumber ?? "N/A") : "XXXXXXXXXX";
        var propertyGroupName = listing?.GroupName ?? string.Empty;
        var propertyMsgTime = listing?.MessageDatetime?.ToString("dd/MM/yyyy HH:mm") ?? "-";
        var propertyRawText = MaskRawText(listing?.RawMessageText, isConfirmed);

        // Formatted Buyer
        var buyerLookingFor = (requirement?.RequirementType?.Equals("RENT", StringComparison.OrdinalIgnoreCase) == true || 
                               requirement?.RequirementType?.Equals("RENTAL", StringComparison.OrdinalIgnoreCase) == true) ? "Wants to Rent" : "Wants to Buy";
        var buyerType = requirement?.PropertyType ?? string.Empty;
        var buyerBudget = FormatBudget(requirement?.Budget, requirement?.BudgetUnit, requirement?.RequirementType);
        var buyerSize = FormatSize(requirement?.Size);
        var buyerLocation = requirement?.City ?? string.Empty;
        var buyerCity = requirement?.City ?? string.Empty;
        var buyerBrokerName = isConfirmed ? (match.RequirementBroker?.Name ?? $"Broker {match.RequirementBroker?.PhoneNumber}") : "Broker (Masked)";
        var buyerBrokerPhone = isConfirmed ? (match.RequirementBroker?.PhoneNumber ?? "N/A") : "XXXXXXXXXX";
        var buyerGroupName = requirement?.GroupName ?? string.Empty;
        var buyerMsgTime = requirement?.MessageDatetime?.ToString("dd/MM/yyyy HH:mm") ?? "-";
        var buyerRawText = MaskRawText(requirement?.RawMessageText, isConfirmed);

        // Match Details comparisons
        var locationComparison = !string.IsNullOrWhiteSpace(propertyCity) &&
                                 string.Equals(propertyCity, buyerCity, StringComparison.OrdinalIgnoreCase)
            ? "Same city"
            : "Not enough data";
        var priceComparison = "Not enough data";
        if (listing?.Price.HasValue == true && requirement?.Budget.HasValue == true)
        {
            priceComparison = listing.Price.Value > requirement.Budget.Value ? "Above budget" : "Within budget";
        }
        var sizeComparison = "Not enough data";
        if (listing?.Size.HasValue == true && requirement?.Size.HasValue == true)
        {
            var diff = Math.Abs(listing.Size.Value - requirement.Size.Value);
            sizeComparison = diff == 0 ? "Exact" : diff <= 200 ? "Approximate" : "Different";
        }

        // AI Verification fields
        var aiStatus = match.AiStatus ?? "PENDING";
        var aiConfidence = match.AiConfidencePct;
        var aiReasoning = match.AiReasoning ?? "";
        var aiFlags = new string[] { };
        if (!string.IsNullOrEmpty(match.AiFlagsJson))
        {
            try
            {
                aiFlags = System.Text.Json.JsonSerializer.Deserialize<string[]>(match.AiFlagsJson) ?? new string[] { };
            }
            catch {}
        }
        var aiValidatedAt = match.AiValidatedAt;

        var responseData = new
        {
            MatchId = match.Id,
            ListingId = match.ListingId,
            RequirementId = match.RequirementId,
            MatchQuality = matchQuality,
            ScorePercent = scorePercent,
            IsAvailable = isAvailable,
            State = match.State,

            Property = new
            {
                For = propertyFor,
                Type = propertyType,
                Config = propertyConfig,
                Price = propertyPrice,
                Size = propertySize,
                Location = propertyLocation,
                City = propertyCity,
                BrokerName = propertyBrokerName,
                BrokerPhone = propertyBrokerPhone,
                GroupName = propertyGroupName,
                MessageDateTime = propertyMsgTime,
                RawText = propertyRawText,
                IsAvailable = (listing?.Status?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true || listing?.Status?.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
            },

            Buyer = new
            {
                LookingFor = buyerLookingFor,
                Type = buyerType,
                Budget = buyerBudget,
                Size = buyerSize,
                Location = buyerLocation,
                City = buyerCity,
                BrokerName = buyerBrokerName,
                BrokerPhone = buyerBrokerPhone,
                GroupName = buyerGroupName,
                MessageDateTime = buyerMsgTime,
                RawText = buyerRawText,
                IsAvailable = (requirement?.Status?.Equals("ACTIVE", StringComparison.OrdinalIgnoreCase) == true || requirement?.Status?.Equals("active", StringComparison.OrdinalIgnoreCase) == true)
            },

            MatchDetails = new
            {
                Location = locationComparison,
                Price = priceComparison,
                Size = sizeComparison
            },

            AiVerification = new
            {
                Status = aiStatus,
                ConfidencePct = aiConfidence,
                Reasoning = aiReasoning,
                Flags = aiFlags,
                ValidatedAt = aiValidatedAt
            }
        };

        return new JsonResult(new { success = true, data = responseData }, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = null });
    }

    [HttpPost("{matchId}/reveal")]
    [Obsolete("Use POST /api/v1/user-matches/matches/{matchId}/reveal instead.")]
    public IActionResult RevealMatchContact([FromRoute] int matchId)
    {
        return StatusCode(StatusCodes.Status410Gone, new { message = "Direct reveal is retired. Confirm through POST /api/v1/user-matches/matches/{matchId}/confirm." });
    }

    [HttpGet("{matchId}/reveal")]
    public async Task<IActionResult> GetRevealStatus([FromRoute] int matchId)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized(new { message = "Invalid authenticated user." });
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
            return Unauthorized(new { message = "No broker profile is linked to this account." });
        var match = await _dbContext.Matches.AsNoTracking().SingleOrDefaultAsync(m => m.Id == matchId);
        if (match is null) return NotFound(new { success = false, message = "Match not found." });
        if (match.ListingBrokerId != brokerId && match.RequirementBrokerId != brokerId) return Forbid();

        var reveal = await _dbContext.Reveals.AsNoTracking().FirstOrDefaultAsync(r => r.MatchId == matchId);
        if (reveal == null)
        {
            return NotFound(new { success = false, message = "Reveal record not found." });
        }

        return Ok(new
        {
            success = true,
            match_id = matchId,
            revealed_at = reveal.RevealedAt
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

    private static string FormatPrice(decimal? price, string? unit, string? listingType)
    {
        if (!price.HasValue) return "Price on request";
        var type = listingType?.ToUpperInvariant() ?? "SELL";
        var rentSuffix = (type == "RENT" || type == "RENTAL") ? "/month" : "";
        return $"₹{price.Value:N0} {unit}{rentSuffix}".Trim();
    }

    private static string FormatBudget(decimal? budget, string? unit, string? reqType)
    {
        if (!budget.HasValue) return "Budget flexible";
        var type = reqType?.ToUpperInvariant() ?? "BUY";
        var rentSuffix = (type == "RENT" || type == "RENTAL") ? "/month" : "";
        return $"₹{budget.Value:N0} {unit}{rentSuffix}".Trim();
    }

    private static string FormatSize(decimal? size)
    {
        if (!size.HasValue) return "-";
        return $"{size.Value:N0} sqft";
    }

    private static string MaskRawText(string? text, bool isConfirmed)
    {
        return ContactRedaction.Redact(text, isConfirmed);
    }
}
