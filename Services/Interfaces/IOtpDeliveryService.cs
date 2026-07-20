namespace PropSeekr.Services.Interfaces;

public interface IOtpDeliveryService
{
    bool IsConfigured { get; }

    string FormatMobileNumber(string mobileNumber);

    Task SendOtpAsync(string mobileNumber, CancellationToken cancellationToken = default);

    Task ResendOtpAsync(string mobileNumber, CancellationToken cancellationToken = default);

    Task<bool> VerifyOtpAsync(string mobileNumber, string otp, CancellationToken cancellationToken = default);
}
