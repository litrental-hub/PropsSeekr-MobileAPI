using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class Msg91OtpDeliveryService : IOtpDeliveryService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<Msg91OtpDeliveryService> _logger;

    public Msg91OtpDeliveryService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<Msg91OtpDeliveryService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_configuration["Msg91:AuthKey"]) &&
        !string.IsNullOrWhiteSpace(_configuration["Msg91:OtpTemplateId"]);

    public string FormatMobileNumber(string mobileNumber)
    {
        var normalized = mobileNumber.Trim().TrimStart('+');

        if (normalized.Length == 10)
        {
            var countryCode = _configuration["Msg91:CountryCode"] ?? "91";
            return $"{countryCode.Trim().TrimStart('+')}{normalized}";
        }

        return normalized;
    }

    public async Task SendOtpAsync(string mobileNumber, string otpCode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogInformation("MSG91 is not configured. Local OTP code for {Mobile}: {Otp}", mobileNumber, otpCode);
            return;
        }

        var requestUri =
            $"https://control.msg91.com/api/v5/otp?template_id={Uri.EscapeDataString(_configuration["Msg91:OtpTemplateId"]!)}" +
            $"&mobile={Uri.EscapeDataString(FormatMobileNumber(mobileNumber))}" +
            $"&authkey={Uri.EscapeDataString(_configuration["Msg91:AuthKey"]!)}" +
            $"&otp={Uri.EscapeDataString(otpCode)}";

        using var response = await _httpClient.PostAsJsonAsync(requestUri, new { }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MSG91 Send OTP returned status {StatusCode}: {Response}", response.StatusCode, body);
        }
    }

    public async Task ResendOtpAsync(string mobileNumber, string otpCode, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            _logger.LogInformation("MSG91 is not configured. Local Resend OTP code for {Mobile}: {Otp}", mobileNumber, otpCode);
            return;
        }

        var retryType = _configuration["Msg91:RetryType"] ?? "text";
        var requestUri =
            $"https://control.msg91.com/api/v5/otp/retry?authkey={Uri.EscapeDataString(_configuration["Msg91:AuthKey"]!)}" +
            $"&retrytype={Uri.EscapeDataString(retryType)}" +
            $"&mobile={Uri.EscapeDataString(FormatMobileNumber(mobileNumber))}";

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("MSG91 Resend OTP returned status {StatusCode}: {Response}", response.StatusCode, body);
        }
    }
}
