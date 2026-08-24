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

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/matches")]
public class MatchesController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IUnlockService _unlockService;
    private readonly IBrokerIdentityService _brokerIdentityService;

    public MatchesController(
        AppDbContext dbContext,
        IUnlockService unlockService,
        IBrokerIdentityService brokerIdentityService)
    {
        _dbContext = dbContext;
        _unlockService = unlockService;
        _brokerIdentityService = brokerIdentityService;
    }

    [HttpGet("{matchId}")]
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

        var scorePercent = (int)(match.MatchScore ?? 95);
        string matchQuality = "Excellent Match";
        if (scorePercent >= 95) matchQuality = "Excellent Match";
        else if (scorePercent >= 80) matchQuality = "Good Match";
        else matchQuality = "Fair Match";

        // Formatted Property
        var propertyFor = (listing?.ListingType?.Equals("RENT", StringComparison.OrdinalIgnoreCase) == true || 
                           listing?.ListingType?.Equals("RENTAL", StringComparison.OrdinalIgnoreCase) == true) ? "For Rent" : "For Sale";
        var propertyType = listing?.PropertyType ?? "Independent House";
        var propertyConfig = listing?.Configuration ?? "3BHK";
        var propertyPrice = FormatPrice(listing?.Price, listing?.PriceUnit, listing?.ListingType);
        var propertySize = FormatSize(listing?.Size);
        var propertyLocation = listing?.ProjectName ?? "Nipania";
        var propertyCity = listing?.City ?? "Indore";
        var propertyBrokerName = isConfirmed ? (match.ListingBroker?.Name ?? $"Broker {match.ListingBroker?.PhoneNumber}") : "Broker (Masked)";
        var propertyBrokerPhone = isConfirmed ? (match.ListingBroker?.PhoneNumber ?? "N/A") : "XXXXXXXXXX";
        var propertyGroupName = listing?.GroupName ?? "Whatsapp";
        var propertyMsgTime = listing?.MessageDatetime?.ToString("dd/MM/yyyy HH:mm") ?? "-";
        var propertyRawText = MaskRawText(listing?.RawMessageText, isConfirmed);

        // Formatted Buyer
        var buyerLookingFor = (requirement?.RequirementType?.Equals("RENT", StringComparison.OrdinalIgnoreCase) == true || 
                               requirement?.RequirementType?.Equals("RENTAL", StringComparison.OrdinalIgnoreCase) == true) ? "Wants to Rent" : "Wants to Buy";
        var buyerType = requirement?.PropertyType ?? "Independent House";
        var buyerBudget = FormatBudget(requirement?.Budget, requirement?.BudgetUnit, requirement?.RequirementType);
        var buyerSize = FormatSize(requirement?.Size);
        var buyerLocation = requirement?.City ?? "Nipania"; // Fallback to City or general locality
        var buyerCity = requirement?.City ?? "Indore";
        var buyerBrokerName = isConfirmed ? (match.RequirementBroker?.Name ?? $"Broker {match.RequirementBroker?.PhoneNumber}") : "Broker (Masked)";
        var buyerBrokerPhone = isConfirmed ? (match.RequirementBroker?.PhoneNumber ?? "N/A") : "XXXXXXXXXX";
        var buyerGroupName = requirement?.GroupName ?? "Whatsapp";
        var buyerMsgTime = requirement?.MessageDatetime?.ToString("dd/MM/yyyy HH:mm") ?? "-";
        var buyerRawText = MaskRawText(requirement?.RawMessageText, isConfirmed);

        // Match Details comparisons
        var locationComparison = "✅ Same area"; // Since we matched, default to same area
        var priceComparison = "✅ Within budget";
        if (listing?.Price.HasValue == true && requirement?.Budget.HasValue == true)
        {
            if (listing.Price.Value > requirement.Budget.Value)
            {
                priceComparison = "⚠️ Slightly over";
            }
        }
        var sizeComparison = "✅ Matches";
        if (listing?.Size.HasValue == true && requirement?.Size.HasValue == true)
        {
            var diff = Math.Abs(listing.Size.Value - requirement.Size.Value);
            if (diff > 0 && diff <= 200)
            {
                sizeComparison = "⚠️ Approximate";
            }
            else if (diff > 200)
            {
                sizeComparison = "❌ Size mismatch";
            }
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
    public async Task<IActionResult> RevealMatchContact([FromRoute] int matchId)
    {
        if (!TryGetCurrentUserId(out var userId))
            return Unauthorized(new { message = "Invalid authenticated user." });
        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
            return Unauthorized(new { message = "No broker profile is linked to this account." });

        try
        {
            var response = await _unlockService.UnlockMatchAsync(
                brokerId.Value,
                new UnlockPropertyRequestDto { MatchId = matchId });
            return response.Success ? Ok(response) : BadRequest(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid();
        }
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
        if (string.IsNullOrEmpty(text)) return string.Empty;
        if (isConfirmed) return text;
        return System.Text.RegularExpressions.Regex.Replace(text, @"\b\d{10}\b|\b\d{5}-\d{5}\b", "XXXXXXXXXX");
    }
}
