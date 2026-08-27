using Microsoft.Extensions.Configuration;

namespace PropSeekr.FileProcessing;

/// <summary>
/// Makes ASP.NET Core's FileProcessor configuration available to the unchanged
/// Lambda implementation, which reads these names from environment variables.
/// Deployment environment variables still take precedence.
/// </summary>
public static class FileProcessorConfigurationBridge
{
    private static readonly (string Setting, string EnvironmentVariable)[] Mappings =
    {
        ("OpenAiApiKey", "OPENAI_API_KEY"),
        ("DbConnectionString", "DB_CONNECTION_STRING"),
        ("DbHost", "DB_HOST"),
        ("DbPort", "DB_PORT"),
        ("DbName", "DB_NAME"),
        ("DbUsername", "DB_USERNAME"),
        ("DbPassword", "DB_PASSWORD"),
        ("S3BucketName", "S3_BUCKET_NAME"),
        ("GoogleMapsApiKey", "GOOGLE_MAPS_API_KEY"),
        ("GoogleApiKey", "GOOGLE_API_KEY"),
        ("PrimaryLlm", "PRIMARY_LLM"),
        ("GoogleServiceAccount:Type", "GEMINI_API_KEY"),
        ("GoogleServiceAccount:ProjectId", "project_id"),
        ("GoogleServiceAccount:PrivateKeyId", "private_key_id"),
        ("GoogleServiceAccount:PrivateKey", "private_key"),
        ("GoogleServiceAccount:ClientEmail", "client_email"),
        ("GoogleServiceAccount:ClientId", "client_id"),
        ("GoogleServiceAccount:AuthUri", "auth_uri"),
        ("GoogleServiceAccount:TokenUri", "token_uri"),
        ("GoogleServiceAccount:AuthProviderX509CertUrl", "auth_provider_x509_cert_url"),
        ("GoogleServiceAccount:ClientX509CertUrl", "client_x509_cert_url"),
        ("GoogleServiceAccount:UniverseDomain", "universe_domain")
    };

    public static void Apply(IConfiguration configuration)
    {
        foreach (var (setting, environmentVariable) in Mappings)
        {
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
                continue;

            var value = configuration[$"FileProcessor:{setting}"];
            if (!string.IsNullOrWhiteSpace(value))
                Environment.SetEnvironmentVariable(environmentVariable, value);
        }
    }
}
