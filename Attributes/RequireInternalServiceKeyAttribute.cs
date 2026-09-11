using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace PropSeekr.Attributes;

/// <summary>
/// Requires an internal service key header (X-Internal-Service-Key) for internal endpoints,
/// preventing unauthorized public access to background processing, matching cron, credit grant/deduct,
/// and ingestion endpoints.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireInternalServiceKeyAttribute : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Internal-Service-Key";
    public const string ConfigKey = "InternalService:ApiKey";
    public const string EnvVarName = "INTERNAL_SERVICE_API_KEY";

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var configuration = context.HttpContext.RequestServices.GetRequiredService<IConfiguration>();

        var expectedKey = configuration[ConfigKey]?.Trim()
            ?? Environment.GetEnvironmentVariable(EnvVarName)?.Trim();

        // Internal endpoints must never become public because a deployment secret
        // was omitted. Treat a missing server-side key as an unavailable service.
        if (string.IsNullOrWhiteSpace(expectedKey))
        {
            context.Result = new ObjectResult(new
            {
                success = false,
                message = "Internal service authentication is unavailable."
            })
            {
                StatusCode = StatusCodes.Status503ServiceUnavailable
            };
            return;
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var providedHeader) ||
            string.IsNullOrWhiteSpace(providedHeader) ||
            !KeysMatch(expectedKey, providedHeader.ToString().Trim()))
        {
            context.Result = new UnauthorizedObjectResult(new
            {
                success = false,
                message = "Unauthorized internal service access. Valid X-Internal-Service-Key header is required."
            });
            return;
        }

        await next();
    }

    private static bool KeysMatch(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);

        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
