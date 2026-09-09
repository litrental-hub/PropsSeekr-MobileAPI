using PropSeekr.DTOs.Auth;

namespace PropSeekr.Services.Interfaces;

public interface IAuthService
{
    Task<RegisterResponseDto> RegisterAsync(RegisterRequestDto request);
    Task<LoginResponseDto> LoginAsync(LoginRequestDto request);
    Task<OtpResponseDto> SendOtpAsync(SendOtpRequestDto request);
    Task<OtpResponseDto> ResendOtpAsync(SendOtpRequestDto request);
    Task<VerifyOtpResponseDto> VerifyOtpAsync(VerifyOtpRequestDto request);
    Task<VerifyOtpResponseDto> VerifyWidgetOtpAsync(VerifyWidgetOtpRequestDto request, CancellationToken cancellationToken = default);
    Task<LogoutResponseDto> LogoutAsync();
}
