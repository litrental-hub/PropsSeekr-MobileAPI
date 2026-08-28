using System;
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

        // If an API key is configured, enforce strict validation against incoming header
        if (!string.IsNullOrWhiteSpace(expectedKey))
        {
            if (!context.HttpContext.Request.Headers.TryGetValue(HeaderName, out var providedHeader) ||
                string.IsNullOrWhiteSpace(providedHeader) ||
                !string.Equals(expectedKey, providedHeader.ToString().Trim(), StringComparison.Ordinal))
            {
                context.Result = new UnauthorizedObjectResult(new
                {
                    success = false,
                    message = "Unauthorized internal service access. Valid X-Internal-Service-Key header is required."
                });
                return;
            }
        }

        await next();
    }
}

