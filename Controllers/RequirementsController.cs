using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Requirements;
using PropSeekr.DTOs.Search;
using PropSeekr.Services.Interfaces;
using System.Security.Claims;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/requirements")]
public class RequirementsController : ControllerBase
{
    private readonly IRequirementService _requirementService;
    private readonly ILogger<RequirementsController> _logger;

    public RequirementsController(
        IRequirementService requirementService,
        ILogger<RequirementsController> logger)
    {
        _requirementService = requirementService;
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
    [Authorize]
    public async Task<IActionResult> AddRequirement([FromBody] CreateRequirementRequestDto request)
    {
        try
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier);
            if (userIdClaim == null || !Guid.TryParse(userIdClaim.Value, out var userId))
            {
                return Unauthorized(new { message = "Invalid user" });
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
            _logger.LogError(ex, "Error in AddRequirement");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }
}
