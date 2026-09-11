namespace PropSeekr.Configuration;

public static class RuntimeConfigurationValidator
{
    private static readonly string[] RequiredSecretKeys =
    [
        "ConnectionStrings:DefaultConnection",
        "Jwt:Key",
        "Jwt:Issuer",
        "Jwt:Audience",
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

        var missingKeys = RequiredSecretKeys
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .ToArray();
        if (missingKeys.Length == 0) return;

        throw new InvalidOperationException(
            $"Required runtime configuration is missing: {string.Join(", ", missingKeys)}.");
    }
}
