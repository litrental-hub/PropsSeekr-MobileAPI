using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace PropSeekr.Authentication;

public class JwtAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IConfiguration _configuration;

    public JwtAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorizationHeader = Request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(authorizationHeader) ||
            !authorizationHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var token = authorizationHeader["Bearer ".Length..].Trim();
        var secret = _configuration["Jwt:Secret"];

        if (string.IsNullOrWhiteSpace(secret))
        {
            return Task.FromResult(AuthenticateResult.Fail("JWT secret is not configured."));
        }

        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3)
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid token format."));
            }

            using var headerDocument = JsonDocument.Parse(Base64UrlDecode(parts[0]));
            if (!headerDocument.RootElement.TryGetProperty("alg", out var algorithm) ||
                algorithm.GetString() != "HS256")
            {
                return Task.FromResult(AuthenticateResult.Fail("Unsupported token algorithm."));
            }

            var unsignedToken = $"{parts[0]}.{parts[1]}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            var expectedSignature = hmac.ComputeHash(Encoding.UTF8.GetBytes(unsignedToken));
            var actualSignature = Base64UrlDecode(parts[2]);

            if (!CryptographicOperations.FixedTimeEquals(expectedSignature, actualSignature))
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid token signature."));
            }

            using var payloadDocument = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            var payload = payloadDocument.RootElement;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            if (!TryGetLong(payload, "exp", out var expiresAt) || expiresAt <= now)
            {
                return Task.FromResult(AuthenticateResult.Fail("Token has expired."));
            }

            if (TryGetLong(payload, "nbf", out var notBefore) && notBefore > now)
            {
                return Task.FromResult(AuthenticateResult.Fail("Token is not active yet."));
            }

            if (!ValidateConfiguredValue(payload, "iss", "Jwt:Issuer") ||
                !ValidateConfiguredValue(payload, "aud", "Jwt:Audience"))
            {
                return Task.FromResult(AuthenticateResult.Fail("Invalid token issuer or audience."));
            }

            var userId = GetString(payload, ClaimTypes.NameIdentifier) ?? GetString(payload, "sub");
            if (string.IsNullOrWhiteSpace(userId))
            {
                return Task.FromResult(AuthenticateResult.Fail("Token does not contain a user id."));
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.NameIdentifier, userId)
            };

            var mobileNumber = GetString(payload, ClaimTypes.MobilePhone) ?? GetString(payload, "mobile_number");
            if (!string.IsNullOrWhiteSpace(mobileNumber))
            {
                claims.Add(new Claim(ClaimTypes.MobilePhone, mobileNumber));
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, Scheme.Name);

            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
        catch (Exception ex)
        {
            return Task.FromResult(AuthenticateResult.Fail(ex.Message));
        }
    }

    private bool ValidateConfiguredValue(JsonElement payload, string claimName, string configurationKey)
    {
        var configuredValue = _configuration[configurationKey];

        return string.IsNullOrWhiteSpace(configuredValue) ||
               GetString(payload, claimName) == configuredValue;
    }

    private static bool TryGetLong(JsonElement payload, string claimName, out long value)
    {
        value = 0;

        return payload.TryGetProperty(claimName, out var element) &&
               element.TryGetInt64(out value);
    }

    private static string? GetString(JsonElement payload, string claimName)
    {
        return payload.TryGetProperty(claimName, out var element)
            ? element.GetString()
            : null;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        var padding = base64.Length % 4;

        if (padding > 0)
        {
            base64 = base64.PadRight(base64.Length + 4 - padding, '=');
        }

        return Convert.FromBase64String(base64);
    }
}
