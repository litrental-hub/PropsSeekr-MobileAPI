using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using PropSeekr.DTOs.Auth;
using PropSeekr.Services.Interfaces;
using System.Net.Sockets;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _authService;
    private readonly IEmailOtpService _emailOtpService;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        IAuthService authService,
        IEmailOtpService emailOtpService,
        ILogger<AuthController> logger)
    {
        _authService = authService;
        _emailOtpService = emailOtpService;
        _logger = logger;
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
        catch (Exception ex) when (IsDatabaseConnectivityFailure(ex))
        {
            _logger.LogError(ex, "Login failed because the database is temporarily unavailable.");
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new
            {
                message = "Login is temporarily unavailable. Please try again in a moment."
            });
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

    private static bool IsDatabaseConnectivityFailure(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or SocketException or TimeoutException)
                return true;
        }

        return false;
    }
}
