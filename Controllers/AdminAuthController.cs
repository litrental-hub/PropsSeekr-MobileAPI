using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Auth;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v2/auth")]
public class AdminAuthController : ControllerBase
{
    private readonly IAuthService _authService;

    public AdminAuthController(IAuthService authService)
    {
        _authService = authService;
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AdminLoginRequestDto request)
    {
        try
        {
            var response = await _authService.AdminLoginAsync(request);

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
}
