namespace PropSeekr.Services.Interfaces;

public interface IOtpDeliveryService
{
    bool IsConfigured { get; }
    Task SendOtpAsync(string mobileNumber, string otpCode, CancellationToken cancellationToken = default);
    Task ResendOtpAsync(string mobileNumber, string otpCode, CancellationToken cancellationToken = default);
}
