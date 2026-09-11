using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class Msg91WidgetVerifier(HttpClient client, IConfiguration configuration) : IMsg91WidgetVerifier
{
    public async Task VerifyAsync(string accessToken, string expectedIdentifier, DateTime notBefore,
        CancellationToken cancellationToken = default)
    {
        var authKey = configuration["Msg91:AuthKey"];
        if (string.IsNullOrWhiteSpace(authKey))
            throw new InvalidOperationException("Mobile verification is not configured.");
        if (string.IsNullOrWhiteSpace(accessToken) || accessToken.Length > 8192)
            throw new InvalidOperationException("Invalid mobile verification result.");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            "https://api.msg91.com/api/v5/widget/verifyAccessToken");
        request.Headers.Add("authkey", authKey);
        request.Content = JsonContent.Create(new Dictionary<string, string> { ["access-token"] = accessToken });
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("Mobile verification could not be confirmed. Please try again.");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = body.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "success" ||
                !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
                message.GetString() != expectedIdentifier)
                throw new InvalidOperationException("Verify the same Indian mobile number used in PropSeekr.");

            // Only inspect claims AFTER MSG91 has authenticated this exact token.
            // Requiring issuance after our challenge prevents old, unredeemed proof
            // from promoting a later registration for the same mobile number.
            var token = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (!token.Payload.TryGetValue("iat", out var issuedValue) || !long.TryParse(issuedValue?.ToString(), out var issued) ||
                token.Payload.Expiration is not long expires ||
                issued < new DateTimeOffset(notBefore).ToUnixTimeSeconds() ||
                issued > now + 30 || expires <= now || expires <= issued)
                throw new InvalidOperationException("Mobile verification has expired. Please verify again.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or ArgumentException
            or SecurityTokenException or FormatException or OverflowException)
        {
            // Do not forward provider bodies, request headers or access tokens.
            throw new InvalidOperationException("Mobile verification could not be confirmed. Please try again.");
        }
    }
}
