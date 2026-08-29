using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PropSeekr.Controllers;

/// <summary>
/// Tombstone for PropertyRequests-backed inventory. Use /api/v1/listings and
/// /api/v1/requirements instead.
/// </summary>
[Authorize]
[ApiController]
[Route("api/v1/property-inventory")]
public class PropertyInventoryController : ControllerBase
{
    [HttpGet("my-listings")]
    public IActionResult GetMyPropertyListings() => Retired();

    [HttpPost("listings")]
    public IActionResult CreatePropertyListing() => Retired();

    private ObjectResult Retired() => StatusCode(StatusCodes.Status410Gone, new
    {
        message = "PropertyRequests-backed inventory is retired. Use canonical /api/v1/listings and /api/v1/requirements routes."
    });
}
