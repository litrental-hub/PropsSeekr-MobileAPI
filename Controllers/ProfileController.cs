using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Profile;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/profile")]
public class ProfileController : ControllerBase
{
    private readonly IProfileService _profileService;

    public ProfileController(IProfileService profileService)
    {
        _profileService = profileService;
    }

    [HttpGet]
    public async Task<IActionResult> GetProfile()
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid authenticated user."
            });
        }

        try
        {
            var response = await _profileService.GetProfileAsync(userId);

            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpPut]
    public async Task<IActionResult> UpdateProfile(
        [FromBody] UpdateProfileRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid authenticated user."
            });
        }

        try
        {
            var response = await _profileService.UpdateProfileAsync(userId, request);

            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    [HttpPost("upload-photo")]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadPhoto(
        IFormFile file)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new
            {
                message = "Invalid authenticated user."
            });
        }

        try
        {
            var response = await _profileService.UploadPhotoAsync(userId, file);

            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new
            {
                message = ex.Message
            });
        }
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);

        return Guid.TryParse(userIdClaim, out userId);
    }
}
