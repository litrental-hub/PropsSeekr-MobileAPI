using System;
using System.Collections.Generic;
using System.Linq;
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
[Route("api/v1/listings")]
public class ListingsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ListingsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost]
    public async Task<IActionResult> CreateListing([FromBody] CreateListingRequestDto request)
    {
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

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var listing = new Listing
            {
                BrokerId = request.BrokerId,
                MasterId = request.MasterId,
                Source = source,
                RawMessageText = request.RawMessageText,
                ListingType = request.ListingType ?? "SELL",
                PropertyType = request.PropertyType,
                Configuration = request.Configuration,
                Price = request.Price,
                PriceUnit = request.PriceUnit,
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

            return Ok(new
            {
                success = true,
                listing_id = listing.Id,
                message = "Listing created successfully."
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return BadRequest(new { success = false, message = ex.Message });
        }
    }
}
