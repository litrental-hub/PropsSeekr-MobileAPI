using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Authorization;

public sealed class AppAttestationAuthorizationHandler : AuthorizationHandler<AppAttestationRequirement>
{
    private readonly IAppAttestationService _attestationService;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AppAttestationAuthorizationHandler> _logger;

    public AppAttestationAuthorizationHandler(IAppAttestationService attestationService, IConfiguration configuration, ILogger<AppAttestationAuthorizationHandler> logger)
    {
        _attestationService = attestationService; _configuration = configuration; _logger = logger;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AppAttestationRequirement requirement)
    {
        if (context.Resource is not HttpContext httpContext || !Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId)) return;
        var purpose = httpContext.GetEndpoint()?.Metadata.GetMetadata<AppAttestationPurposeAttribute>()?.Purpose;
        if (string.IsNullOrWhiteSpace(purpose)) return;
        if (!_configuration.GetValue<bool>("AppAttestation:Enabled"))
        {
            context.Succeed(requirement);
            return;
        }
        var mode = _configuration["AppAttestation:EnforcementMode"] ?? "Enforce";
        try
        {
            if (await _attestationService.ConsumeVerifiedRequestAsync(httpContext, userId, purpose, httpContext.RequestAborted)) { context.Succeed(requirement); return; }
            _logger.LogWarning("App attestation failed for {UserId}, {Endpoint}", userId, httpContext.Request.Path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "App attestation failed for {UserId}, {Endpoint}", userId, httpContext.Request.Path);
        }

        if (string.Equals(mode, "ReportOnly", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Allowing failed app attestation in ReportOnly mode for {UserId}, {Endpoint}", userId, httpContext.Request.Path);
            context.Succeed(requirement);
        }
    }
}
