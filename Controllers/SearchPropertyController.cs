using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Search;
using PropSeekr.Services.Interfaces;
using System.Security.Claims;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/search")]
public class SearchPropertyController : ControllerBase
{
    private readonly ISearchPropertyService _searchPropertyService;
    private readonly ILogger<SearchPropertyController> _logger;
    private readonly ICurrentUserContext _currentUser;

    public SearchPropertyController(
        ISearchPropertyService searchPropertyService,
        ILogger<SearchPropertyController> logger,
        ICurrentUserContext currentUser)
    {
        _searchPropertyService = searchPropertyService;
        _logger = logger;
        _currentUser = currentUser;
    }

    [HttpPost("properties")]
    [Authorize(Policy = "CustomerPolicy")]
    public async Task<IActionResult> SearchProperties(
        [FromBody] SearchPropertyRequestDto request)
    {
        try
        {
            if (!_currentUser.TryGetLocalUserId(out var userId))
            {
                return Unauthorized(new { message = "Invalid user" });
            }

            var response = await _searchPropertyService.SearchPropertiesAsync(request, userId);
            return Ok(response);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error in SearchProperties: {ex.Message}");
            return BadRequest(new
            {
                success = false,
                message = ex.Message
            });
        }
    }
}
