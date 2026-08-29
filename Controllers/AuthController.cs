using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Auth;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IEmailOtpService _emailOtpService;

    public AuthController(
        IAuthService authService,
        IEmailOtpService emailOtpService)
    {
        _authService = authService;
        _emailOtpService = emailOtpService;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
    {
        try
        {
            var response = await _authService.RegisterAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
    {
        try
        {
            var response = await _authService.LoginAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("send-email-otp")]
    public async Task<IActionResult> SendEmailOtp([FromBody] SendEmailOtpRequestDto request)
    {
        try
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var response = await _emailOtpService.SendEmailOtpAsync(request, clientIp);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("verify-email-otp")]
    public async Task<IActionResult> VerifyEmailOtp([FromBody] VerifyEmailOtpRequestDto request)
    {
        try
        {
            var clientIp = HttpContext.Connection.RemoteIpAddress?.ToString();
            var response = await _emailOtpService.VerifyEmailOtpAsync(request, clientIp);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpPost("send-otp")]
    public async Task<IActionResult> SendOtp([FromBody] SendOtpRequestDto request)
    {
        try
        {
            var response = await _authService.SendOtpAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("verify-otp")]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequestDto request)
    {
        try
        {
            var response = await _authService.VerifyOtpAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("resend-otp")]
    public async Task<IActionResult> ResendOtp([FromBody] SendOtpRequestDto request)
    {
        try
        {
            var response = await _authService.ResendOtpAsync(request);
            return Ok(response);
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize]
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        var response = await _authService.LogoutAsync();
        return Ok(response);
    }

    [HttpPost("refresh")]
    public IActionResult RefreshToken()
    {
        return StatusCode(StatusCodes.Status410Gone, new
        {
            success = false,
            message = "Refresh token endpoint is retired. Access tokens are single-use session tokens; please authenticate via login or OTP."
        });
    }
}
