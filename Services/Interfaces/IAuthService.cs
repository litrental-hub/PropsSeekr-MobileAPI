using PropSeekr.DTOs.Auth;

namespace PropSeekr.Services.Interfaces;

public interface IAuthService
{
    Task<AdminLoginResponseDto> AdminLoginAsync(AdminLoginRequestDto request);
    Task<RegisterResponseDto> RegisterAsync(RegisterRequestDto request);
    Task<LoginResponseDto> LoginAsync(LoginRequestDto request);
    Task<OtpResponseDto> SendOtpAsync(SendOtpRequestDto request);
    Task<OtpResponseDto> ResendOtpAsync(SendOtpRequestDto request);
    Task<VerifyOtpResponseDto> VerifyOtpAsync(VerifyOtpRequestDto request);
    Task<LogoutResponseDto> LogoutAsync();
}
