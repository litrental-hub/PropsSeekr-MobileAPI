using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1")]
public class ListingRequirementsController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public ListingRequirementsController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpPost("listings/{listingId}/requirements/{requirementId}")]
    public async Task<IActionResult> AddRelationship([FromRoute] int listingId, [FromRoute] int requirementId)
    {
        var listingExists = await _dbContext.Listings.AnyAsync(l => l.Id == listingId);
        if (!listingExists)
        {
            return NotFound(new { success = false, message = $"Listing ID {listingId} not found." });
        }

        var reqExists = await _dbContext.Requirements.AnyAsync(r => r.Id == requirementId);
        if (!reqExists)
        {
            return NotFound(new { success = false, message = $"Requirement ID {requirementId} not found." });
        }

        var existing = await _dbContext.ListingRequirements
            .FirstOrDefaultAsync(lr => lr.ListingId == listingId && lr.RequirementId == requirementId);

        if (existing != null)
        {
            return Ok(new { success = true, message = "Relationship already exists." });
        }

        var relationship = new ListingRequirement
        {
            ListingId = listingId,
            RequirementId = requirementId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        _dbContext.ListingRequirements.Add(relationship);
        await _dbContext.SaveChangesAsync();

        return Ok(new { success = true, message = "Relationship added successfully." });
    }

    [HttpDelete("listings/{listingId}/requirements/{requirementId}")]
    public async Task<IActionResult> RemoveRelationship([FromRoute] int listingId, [FromRoute] int requirementId)
    {
        var relationship = await _dbContext.ListingRequirements
            .FirstOrDefaultAsync(lr => lr.ListingId == listingId && lr.RequirementId == requirementId);

        if (relationship == null)
        {
            return NotFound(new { success = false, message = "Relationship not found." });
        }

        _dbContext.ListingRequirements.Remove(relationship);
        await _dbContext.SaveChangesAsync();

        return Ok(new { success = true, message = "Relationship removed successfully." });
    }

    [HttpGet("listings/{listingId}/requirements")]
    public async Task<IActionResult> GetRequirementsForListing([FromRoute] int listingId, [FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (page <= 0) page = 1;
        if (limit <= 0) limit = 20;

        var listingExists = await _dbContext.Listings.AnyAsync(l => l.Id == listingId);
        if (!listingExists)
        {
            return NotFound(new { success = false, message = $"Listing ID {listingId} not found." });
        }

        var query = _dbContext.ListingRequirements
            .AsNoTracking()
            .Where(lr => lr.ListingId == listingId)
            .Select(lr => lr.Requirement);

        var totalCount = await query.CountAsync();
        var requirements = await query
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();

        return Ok(new
        {
            success = true,
            total = totalCount,
            page = page,
            limit = limit,
            data = requirements
        });
    }

    [HttpGet("requirements/{requirementId}/listings")]
    public async Task<IActionResult> GetListingsForRequirement([FromRoute] int requirementId, [FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (page <= 0) page = 1;
        if (limit <= 0) limit = 20;

        var reqExists = await _dbContext.Requirements.AnyAsync(r => r.Id == requirementId);
        if (!reqExists)
        {
            return NotFound(new { success = false, message = $"Requirement ID {requirementId} not found." });
        }

        var query = _dbContext.ListingRequirements
            .AsNoTracking()
            .Where(lr => lr.RequirementId == requirementId)
            .Select(lr => lr.Listing);

        var totalCount = await query.CountAsync();
        var listings = await query
            .Skip((page - 1) * limit)
            .Take(limit)
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

    [HttpGet("listings/metrics")]
    public async Task<IActionResult> GetMetrics()
    {
        var totalListings = await _dbContext.Listings.CountAsync();
        var totalRequirements = await _dbContext.Requirements.CountAsync();
        var totalRelationships = await _dbContext.ListingRequirements.CountAsync();
        var totalMatches = await _dbContext.Matches.CountAsync();

        var companyListings = await _dbContext.Listings.CountAsync(l => l.PostedBy == "COMPANY");
        var brokerListings = totalListings - companyListings;

        double reqsPerListing = totalListings > 0 ? (double)totalRelationships / totalListings : 0.0;
        double listingsPerReq = totalRequirements > 0 ? (double)totalRelationships / totalRequirements : 0.0;
        double matchesPerListing = totalListings > 0 ? (double)totalMatches / totalListings : 0.0;
        double matchesPerReq = totalRequirements > 0 ? (double)totalMatches / totalRequirements : 0.0;

        return Ok(new
        {
            success = true,
            data = new
            {
                company_listing_count = companyListings,
                broker_listing_count = brokerListings,
                total_listing_count = totalListings,
                requirements_per_listing = Math.Round(reqsPerListing, 4),
                listings_per_requirement = Math.Round(listingsPerReq, 4),
                matches_per_listing = Math.Round(matchesPerListing, 4),
                matches_per_requirement = Math.Round(matchesPerReq, 4)
            }
        });
    }
}
