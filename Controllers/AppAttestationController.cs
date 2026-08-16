using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Attestation;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/attestation")]
[Authorize(Policy = "CustomerPolicy")]
public class AppAttestationController : ControllerBase
{
    private readonly IAppAttestationService _service;
    public AppAttestationController(IAppAttestationService service) => _service = service;

    [HttpPost("challenge")]
    public async Task<IActionResult> CreateChallenge([FromBody] CreateAttestationChallengeRequestDto request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        return Ok(await _service.CreateChallengeAsync(userId, request, cancellationToken));
    }

    [HttpPost("ios/enroll")]
    public async Task<IActionResult> EnrollIos([FromBody] EnrollAppAttestRequestDto request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        try { await _service.EnrollAppleAppAttestAsync(userId, request, cancellationToken); return NoContent(); }
        catch (InvalidOperationException) { return BadRequest(new { message = "App attestation enrollment failed." }); }
    }

    [HttpPost("android/verify")]
    public async Task<IActionResult> VerifyAndroid([FromBody] VerifyPlayIntegrityRequestDto request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        try { return Ok(await _service.VerifyPlayIntegrityAsync(userId, request, cancellationToken)); }
        catch (InvalidOperationException) { return StatusCode(StatusCodes.Status403Forbidden, new { message = "App attestation verification failed." }); }
    }

    [HttpPost("ios/assert")]
    public async Task<IActionResult> VerifyIosAssertion([FromBody] VerifyAppAttestAssertionRequestDto request, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return Unauthorized();
        try { return Ok(await _service.VerifyAppleAssertionAsync(userId, request, cancellationToken)); }
        catch (InvalidOperationException) { return StatusCode(StatusCodes.Status403Forbidden, new { message = "App attestation verification failed." }); }
    }
}
