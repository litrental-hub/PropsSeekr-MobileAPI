using PropSeekr.DTOs.Auth;

namespace PropSeekr.Services.Interfaces;

public interface IEmailOtpService
{
    Task<SendEmailOtpResponseDto> SendEmailOtpAsync(SendEmailOtpRequestDto request, string? clientIp, CancellationToken cancellationToken = default);
    Task<VerifyEmailOtpResponseDto> VerifyEmailOtpAsync(VerifyEmailOtpRequestDto request, string? clientIp, CancellationToken cancellationToken = default);
}
