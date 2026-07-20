using System.Text.Json;
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

    public async Task SendOtpAsync(string mobileNumber, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var requestUri =
            $"api/v5/otp?template_id={Uri.EscapeDataString(_configuration["Msg91:OtpTemplateId"]!)}" +
            $"&mobile={Uri.EscapeDataString(FormatMobileNumber(mobileNumber))}" +
            $"&authkey={Uri.EscapeDataString(_configuration["Msg91:AuthKey"]!)}";

        using var response = await _httpClient.PostAsJsonAsync(requestUri, new { }, cancellationToken);
        await EnsureSuccessfulMsg91ResponseAsync(response, "send OTP", cancellationToken);
    }

    public async Task ResendOtpAsync(string mobileNumber, CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var retryType = _configuration["Msg91:RetryType"] ?? "text";
        var requestUri =
            $"api/v5/otp/retry?authkey={Uri.EscapeDataString(_configuration["Msg91:AuthKey"]!)}" +
            $"&retrytype={Uri.EscapeDataString(retryType)}" +
            $"&mobile={Uri.EscapeDataString(FormatMobileNumber(mobileNumber))}";

        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        await EnsureSuccessfulMsg91ResponseAsync(response, "resend OTP", cancellationToken);
    }

    public async Task<bool> VerifyOtpAsync(
        string mobileNumber,
        string otp,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"api/v5/otp/verify?otp={Uri.EscapeDataString(otp)}&mobile={Uri.EscapeDataString(FormatMobileNumber(mobileNumber))}");
        request.Headers.Add("authkey", _configuration["Msg91:AuthKey"]!);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "MSG91 verify OTP failed with status {StatusCode}: {Response}",
                response.StatusCode,
                body);
            return false;
        }

        return IsSuccessResponse(body, allowVerifiedMessage: true);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException("MSG91 OTP settings are not configured.");
        }
    }

    private async Task EnsureSuccessfulMsg91ResponseAsync(
        HttpResponseMessage response,
        string action,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode || !IsSuccessResponse(body, allowVerifiedMessage: false))
        {
            _logger.LogWarning(
                "MSG91 {Action} failed with status {StatusCode}: {Response}",
                action,
                response.StatusCode,
                body);
            throw new Exception($"Failed to {action}.");
        }
    }

    private static bool IsSuccessResponse(string body, bool allowVerifiedMessage)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            if (root.TryGetProperty("type", out var type) &&
                string.Equals(type.GetString(), "success", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (allowVerifiedMessage &&
                root.TryGetProperty("message", out var message))
            {
                var value = message.GetString() ?? string.Empty;
                return value.Contains("verified", StringComparison.OrdinalIgnoreCase) ||
                    value.Contains("number_verified_successfully", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }
}
