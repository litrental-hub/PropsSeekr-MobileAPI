using System;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;
using PropSeekr.Services.Interfaces;
using PropSeekr.Models;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/requirements")]
public class RequirementsController : ControllerBase
{
    private readonly IRequirementService _requirementService;
    private readonly AppDbContext _dbContext;
    private readonly ILogger<RequirementsController> _logger;

    public RequirementsController(
        IRequirementService requirementService,
        AppDbContext dbContext,
        ILogger<RequirementsController> logger)
    {
        _requirementService = requirementService;
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpGet("mine")]
    [Authorize]
    public async Task<IActionResult> GetMyRequirements([FromQuery] PaginationDto pagination)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized(new { message = "Invalid user" });
            }

            var response = await _requirementService.GetMyRequirementsAsync(userId, pagination);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetMyRequirements");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateRequirement([FromBody] JsonElement json)
    {
        // Determine request type based on properties present in JSON
        if (json.TryGetProperty("broker_id", out _) || json.TryGetProperty("brokerId", out _))
        {
            // Legacy/Extraction flow - saves directly to legacy requirements table
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var reqDto = JsonSerializer.Deserialize<PropSeekr.DTOs.Matches.CreateRequirementRequestDto>(json.GetRawText(), options);
                
                if (reqDto == null || reqDto.BrokerId <= 0)
                {
                    return BadRequest(new { success = false, message = "Valid broker_id is required." });
                }

                var brokerExists = await _dbContext.Brokers.AnyAsync(b => b.Id == reqDto.BrokerId);
                if (!brokerExists)
                {
                    return NotFound(new { success = false, message = $"Broker ID {reqDto.BrokerId} not found." });
                }

                var requirement = new Requirement
                {
                    BrokerId = reqDto.BrokerId,
                    RequirementType = reqDto.RequirementType ?? "rent",
                    PropertyType = reqDto.PropertyType,
                    Budget = reqDto.Budget,
                    BudgetUnit = reqDto.BudgetUnit,
                    Size = reqDto.Size,
                    PreferredLocalityIds = reqDto.LocalityIds?.ToArray(),
                    Configurations = reqDto.Configurations?.ToArray(),
                    RawMessageText = reqDto.RawMessageText,
                    Source = reqDto.Source ?? "manual",
                    Status = reqDto.Status ?? "active",
                    ExpiresAt = reqDto.ExpiresAt ?? DateTime.UtcNow.AddDays(30),
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    FreshnessUpdatedAt = DateTime.UtcNow,
                    LastConfirmedAt = DateTime.UtcNow,
                    FurnishingPref = reqDto.FurnishingPref,
                    FacingPref = reqDto.FacingPref,
                    ContentHash = reqDto.ContentHash,
                    GroupName = reqDto.GroupName,
                    MessageDatetime = reqDto.MessageDatetime ?? DateTime.UtcNow,
                    BudgetType = reqDto.BudgetType,
                    City = reqDto.City,
                    PostedBy = reqDto.PostedBy ?? "BROKER"
                };

                _dbContext.Requirements.Add(requirement);
                await _dbContext.SaveChangesAsync();

                if (reqDto.ListingIds != null && reqDto.ListingIds.Any())
                {
                    var existingListIds = await _dbContext.Listings
                        .Where(l => reqDto.ListingIds.Contains(l.Id))
                        .Select(l => l.Id)
                        .ToListAsync();

                    foreach (var listId in existingListIds)
                    {
                        var map = new ListingRequirement
                        {
                            ListingId = listId,
                            RequirementId = requirement.Id,
                            CreatedAt = DateTime.UtcNow,
                            UpdatedAt = DateTime.UtcNow
                        };
                        _dbContext.ListingRequirements.Add(map);
                    }
                    await _dbContext.SaveChangesAsync();
                }

                return Ok(new
                {
                    success = true,
                    requirement_id = requirement.Id,
                    message = "Requirement created successfully."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error creating requirement in legacy table");
                return BadRequest(new { success = false, message = ex.Message });
            }
        }
        else
        {
            // Mobile app client flow - requires Authorize token and saves to PropertyRequests
            try
            {
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
                if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
                {
                    return Unauthorized(new { message = "Invalid user. Bearer token is required for mobile app requirements." });
                }

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var request = JsonSerializer.Deserialize<CreateRequirementRequestDto>(json.GetRawText(), options);
                if (request == null)
                {
                    return BadRequest(new { success = false, message = "Invalid payload" });
                }

                var response = await _requirementService.AddRequirementAsync(userId, request);
                return Ok(response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AddRequirement (mobile flow)");
                return BadRequest(new { success = false, message = ex.Message });
            }
        }
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetRequirementDetails([FromRoute] int id)
    {
        var requirement = await _dbContext.Requirements.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id);
        if (requirement == null)
        {
            return NotFound(new { success = false, message = "Requirement not found." });
        }

        var listings = await _dbContext.ListingRequirements.AsNoTracking()
            .Where(lr => lr.RequirementId == id)
            .Select(lr => new
            {
                listing_id = lr.ListingId,
                property_type = lr.Listing != null ? lr.Listing.PropertyType : null,
                locality = lr.Listing != null ? (lr.Listing.ProjectName ?? "N/A") : "N/A",
                price = lr.Listing != null ? lr.Listing.Price : null,
                status = lr.Listing != null ? lr.Listing.Status : null,
                match_status = lr.MatchStatus,
                match_score = lr.MatchScore
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            data = new
            {
                requirement_id = requirement.Id,
                broker_id = requirement.BrokerId,
                requirement_type = requirement.RequirementType,
                property_type = requirement.PropertyType,
                budget = requirement.Budget,
                budget_unit = requirement.BudgetUnit,
                size = requirement.Size,
                locality_ids = requirement.PreferredLocalityIds,
                configurations = requirement.Configurations,
                status = requirement.Status,
                source = requirement.Source,
                raw_message_text = requirement.RawMessageText,
                posted_by = requirement.PostedBy ?? "BROKER",
                created_at = requirement.CreatedAt,
                listings = listings
            }
        });
    }

    [HttpGet("my-requirements")]
    [Authorize]
    public async Task<IActionResult> GetMyRequirementsWithMetrics(
        [FromQuery] Guid userId,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var authUserId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        // Tenant Isolation Check
        if (userId != authUserId)
        {
            return Forbid("Access denied. Logged-in user ID does not match the requested userId.");
        }

        try
        {
            var response = await _requirementService.GetMyRequirementsWithMetricsAsync(authUserId, status, page, limit);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in GetMyRequirementsWithMetrics");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("add")]
    [Authorize]
    public async Task<IActionResult> AddNewGeofencedRequirement([FromBody] AddRequirementRequestDto request)
    {
        if (!TryGetCurrentUserId(out var authUserId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        // Tenant Isolation Check
        if (request.UserId != authUserId)
        {
            return Forbid("Access denied. Logged-in user ID does not match the request payload userId.");
        }

        if (!ModelState.IsValid)
        {
            return ValidationProblem(ModelState);
        }

        try
        {
            var response = await _requirementService.AddRequirementAsync(request);
            return CreatedAtAction(nameof(GetMyRequirementsWithMetrics), new { userId = authUserId, page = 1, limit = 20 }, response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error adding geofenced requirement");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (userIdClaim == null)
        {
            userId = Guid.Empty;
            return false;
        }
        return Guid.TryParse(userIdClaim.Value, out userId);
    }
}
