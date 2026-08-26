using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Inventory;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/listings")]
public class ListingsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly IBrokerIdentityService _brokerIdentityService;
    private readonly IBrokerListingsService _brokerListingsService;
    private readonly IMatchingPipelineService _matchingPipeline;
    private readonly ILogger<ListingsController> _logger;

    public ListingsController(
        AppDbContext dbContext,
        IBrokerIdentityService brokerIdentityService,
        IBrokerListingsService brokerListingsService,
        IMatchingPipelineService matchingPipeline,
        ILogger<ListingsController> logger)
    {
        _dbContext = dbContext;
        _brokerIdentityService = brokerIdentityService;
        _brokerListingsService = brokerListingsService;
        _matchingPipeline = matchingPipeline;
        _logger = logger;
    }

    [HttpGet("mine")]
    [ProducesResponseType(typeof(GetBrokerListingsResponseDto), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetMyListings(
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string? transactionType = null,
        [FromQuery] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (page < 1 || limit is < 1 or > 100)
        {
            return BadRequest(new
            {
                success = false,
                message = "page must be at least 1 and limit must be between 1 and 100."
            });
        }

        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { success = false, message = "Invalid authenticated user." });
        }

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId, cancellationToken);
        if (!brokerId.HasValue)
        {
            return NotFound(new
            {
                success = false,
                code = "broker_profile_not_linked",
                message = "No broker profile is linked to this account."
            });
        }

        try
        {
            var response = await _brokerListingsService.GetMyListingsAsync(
                brokerId.Value,
                page,
                limit,
                transactionType,
                status,
                cancellationToken);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateListing([FromBody] CreateListingRequestDto request)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(userIdClaim, out var userId))
        {
            return Unauthorized(new { success = false, message = "Invalid authenticated user." });
        }

        var brokerId = await _brokerIdentityService.GetBrokerIdAsync(userId);
        if (!brokerId.HasValue)
        {
            return NotFound(new
            {
                success = false,
                code = "broker_profile_not_linked",
                message = "No broker profile is linked to this account."
            });
        }

        request.BrokerId = brokerId.Value;
        return await SaveListingInternal(request, request.Source ?? "manual");
    }

    [HttpPost("whatsapp-intake")]
    [AllowAnonymous] // Allow lambda integration to hit it without user bearer tokens
    public async Task<IActionResult> WhatsappIntake([FromBody] CreateListingRequestDto request)
    {
        return await SaveListingInternal(request, "whatsapp");
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetListingDetails([FromRoute] int id)
    {
        var listing = await _dbContext.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id);
        if (listing == null)
        {
            return NotFound(new { success = false, message = "Listing not found." });
        }

        var sizes = await _dbContext.ListingSizes.AsNoTracking()
            .Where(ls => ls.ListingId == id)
            .Select(ls => new { size_sqft = ls.SizeSqft, size_label = ls.SizeLabel })
            .ToListAsync();

        var requirements = await _dbContext.ListingRequirements.AsNoTracking()
            .Where(lr => lr.ListingId == id)
            .Select(lr => new
            {
                requirement_id = lr.RequirementId,
                requirement_type = lr.Requirement != null ? lr.Requirement.RequirementType : "rent",
                property_type = lr.Requirement != null ? lr.Requirement.PropertyType : null,
                budget = lr.Requirement != null ? lr.Requirement.Budget : null,
                budget_unit = lr.Requirement != null ? lr.Requirement.BudgetUnit : null,
                size = lr.Requirement != null ? lr.Requirement.Size : null,
                status = lr.Requirement != null ? lr.Requirement.Status : null,
                match_status = lr.MatchStatus,
                match_score = lr.MatchScore
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data = new
            {
                listing_id = listing.Id,
                broker_id = listing.BrokerId,
                property_type = listing.PropertyType,
                locality = listing.ProjectName ?? "N/A", // Map project_name back to locality
                price = listing.Price,
                status = listing.Status,
                source = listing.Source,
                raw_message_text = listing.RawMessageText,
                posted_by = listing.PostedBy ?? "BROKER",
                created_at = listing.CreatedAt,
                updated_at = listing.UpdatedAt,
                sizes = sizes,
                requirements = requirements
            }
        });
    }

    [HttpGet]
    public async Task<IActionResult> GetListings([FromQuery] string? postedBy, [FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (page <= 0) page = 1;
        if (limit <= 0) limit = 20;

        var query = _dbContext.Listings.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(postedBy))
        {
            var filter = postedBy.ToUpperInvariant();
            query = query.Where(l => l.PostedBy == filter);
        }

        var totalCount = await query.CountAsync();

        var listings = await query
            .OrderByDescending(l => l.CreatedAt)
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(l => new
            {
                listing_id = l.Id,
                broker_id = l.BrokerId,
                property_type = l.PropertyType,
                locality = l.ProjectName ?? "N/A",
                price = l.Price,
                status = l.Status,
                source = l.Source,
                posted_by = l.PostedBy ?? "BROKER",
                created_at = l.CreatedAt,
                requirement_count = _dbContext.ListingRequirements.Count(lr => lr.ListingId == l.Id)
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            total = totalCount,
            page = page,
            limit = limit,
            data = listings
        });
    }

    [HttpPatch("{id}")]
    public async Task<IActionResult> PatchListing([FromRoute] int id, [FromBody] CreateListingRequestDto request)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.Id == id);
        if (listing == null)
        {
            return NotFound(new { success = false, message = "Listing not found." });
        }

        // Apply fields if supplied in patch payload
        if (request.PropertyType != null)
        {
            listing.PropertyType = request.PropertyType;
        }

        if (request.Locality != null)
        {
            listing.ProjectName = request.Locality;
        }

        if (request.Price.HasValue)
        {
            listing.Price = request.Price;
        }

        if (request.Status != null)
        {
            listing.Status = request.Status;
        }

        // Reset freshness timestamps on update
        listing.FreshnessUpdatedAt = DateTime.UtcNow;
        listing.LastRefreshedAt = DateTime.UtcNow;
        listing.UpdatedAt = DateTime.UtcNow;

        _dbContext.Listings.Update(listing);

        // Update sizes if provided
        if (request.Sizes != null)
        {
            var existingSizes = await _dbContext.ListingSizes.Where(ls => ls.ListingId == id).ToListAsync();
            _dbContext.ListingSizes.RemoveRange(existingSizes);

            foreach (var size in request.Sizes)
            {
                var newSize = new ListingSize
                {
                    ListingId = listing.Id,
                    SizeSqft = size.SizeSqft,
                    SizeLabel = size.Bhk.HasValue ? $"{size.Bhk} BHK" : "Flat"
                };
                _dbContext.ListingSizes.Add(newSize);
            }
        }

        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            success = true,
            message = "Listing updated successfully.",
            listing_id = listing.Id
        });
    }

    private async Task<IActionResult> SaveListingInternal(CreateListingRequestDto request, string source)
    {
        if (request.BrokerId <= 0)
        {
            return BadRequest(new { success = false, message = "Valid broker_id is required." });
        }

        var brokerExists = await _dbContext.Brokers.AnyAsync(b => b.Id == request.BrokerId);
        if (!brokerExists)
        {
            return NotFound(new { success = false, message = $"Broker ID {request.BrokerId} not found." });
        }

        var normalizedListingType = request.ListingType?.Trim().ToUpperInvariant();
        if (normalizedListingType == "RENTAL") normalizedListingType = "RENT";
        if (normalizedListingType == "SALE") normalizedListingType = "SELL";
        if (source == "manual" && normalizedListingType is not ("RENT" or "SELL" or "LEASE"))
        {
            return BadRequest(new
            {
                success = false,
                message = "listing_type is required and must be RENT or SELL."
            });
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var listing = new Listing
            {
                BrokerId = request.BrokerId,
                MasterId = request.MasterId,
                Source = source,
                RawMessageText = BuildListingMatchText(request),
                ListingType = normalizedListingType ?? "SELL",
                PropertyType = request.PropertyType,
                Configuration = request.Configuration,
                Price = request.Price,
                PriceUnit = NormalizePriceUnit(request.PriceUnit),
                Size = request.Size,
                Furnishing = request.Furnishing,
                Facing = request.Facing,
                Status = request.Status ?? "active",
                ExpiresAt = request.MessageDatetime?.AddDays(30) ?? DateTime.UtcNow.AddDays(30),
                LastRefreshedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow,
                FreshnessUpdatedAt = DateTime.UtcNow,
                ProjectName = request.ProjectName ?? request.Locality,
                RoadInfo = request.RoadInfo,
                ContentHash = request.ContentHash,
                GroupName = request.GroupName,
                MessageDatetime = request.MessageDatetime ?? DateTime.UtcNow,
                PriceStatus = request.PriceStatus,
                City = request.City,
                PostedBy = request.PostedBy ?? "BROKER"
            };

            _dbContext.Listings.Add(listing);
            await _dbContext.SaveChangesAsync(); // Auto-generates listing ID

            if (request.Sizes != null && request.Sizes.Any())
            {
                foreach (var size in request.Sizes)
                {
                    var listingSize = new ListingSize
                    {
                        ListingId = listing.Id,
                        SizeSqft = size.SizeSqft,
                        SizeLabel = size.Bhk.HasValue ? $"{size.Bhk} BHK" : "Flat"
                    };
                    _dbContext.ListingSizes.Add(listingSize);
                }
                await _dbContext.SaveChangesAsync();
            }

            if (request.RequirementIds != null && request.RequirementIds.Any())
            {
                var existingReqIds = await _dbContext.Requirements
                    .Where(r => request.RequirementIds.Contains(r.Id))
                    .Select(r => r.Id)
                    .ToListAsync();

                foreach (var reqId in existingReqIds)
                {
                    var map = new ListingRequirement
                    {
                        ListingId = listing.Id,
                        RequirementId = reqId,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.ListingRequirements.Add(map);
                }
                await _dbContext.SaveChangesAsync();
            }

            await transaction.CommitAsync();

            IReadOnlyList<int> matches = [];
            try
            {
                await _matchingPipeline.TriggerForListingAsync(listing.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Embedding and matching pipeline failed to start for listing {ListingId}", listing.Id);
            }

            return Ok(new
            {
                success = true,
                listing_id = listing.Id,
                match_count = matches.Count,
                message = "Listing created successfully. Embedding and matching have started."
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private static string? NormalizePriceUnit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "TOTAL";
        return value.Trim().ToUpperInvariant() switch
        {
            "INR" or "TOTAL" => "TOTAL",
            "PER MONTH" or "PER_MONTH" => "PER_MONTH",
            "PER SQFT" or "PER_SQFT" => "PER_SQFT",
            _ => value.Trim().ToUpperInvariant()
        };
    }

    private static string BuildListingMatchText(CreateListingRequestDto request)
    {
        var parts = new[]
        {
            request.RawMessageText, request.ListingType, request.Configuration,
            request.PropertyType, request.ProjectName ?? request.Locality, request.City,
            request.Size.HasValue ? $"{request.Size.Value} sqft" : null,
            request.Price.HasValue ? $"price {request.Price.Value} {NormalizePriceUnit(request.PriceUnit)}" : null,
            request.Furnishing, request.Facing, request.RoadInfo
        };
        return string.Join(". ", parts.Where(value => !string.IsNullOrWhiteSpace(value)));
    }
}
