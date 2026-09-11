namespace PropSeekr.Configuration;

public static class RuntimeConfigurationValidator
{
    private static readonly string[] RequiredStartupKeys =
    [
        "ConnectionStrings:DefaultConnection",
        "Jwt:Key",
        "Jwt:Issuer",
        "Jwt:Audience"
    ];

    private static readonly string[] ProtectedIntegrationKeys =
    [
        "InternalService:ApiKey",
        "Razorpay:KeyId",
        "Razorpay:KeySecret",
        "Razorpay:WebhookSecret"
    ];

    public static void Validate(IConfiguration configuration, IHostEnvironment environment)
    {
        var secretsLoaded = configuration.GetValue<bool>(AwsSecretsConfigurationLoader.SecretsLoadedKey);
        if ((environment.IsDevelopment() || environment.IsEnvironment("Testing")) && !secretsLoaded)
            return;

        var missingKeys = RequiredStartupKeys
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();
        if (missingKeys.Length > 0)
        {
            throw new InvalidOperationException(
                $"Required runtime configuration is missing: {string.Join(", ", missingKeys)}.");
        }

        var unavailableIntegrations = ProtectedIntegrationKeys
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();
        if (unavailableIntegrations.Length > 0)
        {
            Console.Error.WriteLine(
                $"[Configuration] Protected integrations are unavailable because configuration is missing: {string.Join(", ", unavailableIntegrations)}. Their endpoints remain fail-closed.");
        }
    }
}
