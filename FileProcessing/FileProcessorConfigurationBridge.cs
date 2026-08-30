using Microsoft.Extensions.Configuration;

namespace PropSeekr.FileProcessing;

/// <summary>
/// Makes ASP.NET Core's FileProcessor configuration available to the unchanged
/// Lambda implementation, which reads these names from environment variables.
/// AWS Secrets Manager is added as the final configuration provider on the
/// server, so these bridged values are authoritative there.
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
        ("VertexLocation", "GOOGLE_CLOUD_LOCATION"),
        ("EmbeddingModel", "VERTEX_EMBEDDING_MODEL"),
        ("EmbeddingDimensions", "EMBEDDING_DIMENSIONS"),
        ("LocalBulkImportDirectory", "LOCAL_BULK_IMPORT_DIRECTORY"),
        ("LocalMode", "FILE_PROCESSOR_LOCAL_MODE"),
        ("GoogleServiceAccount:Type", "GOOGLE_SERVICE_ACCOUNT_TYPE"),
        ("GoogleServiceAccount:ProjectId", "GOOGLE_CLOUD_PROJECT"),
        ("GoogleServiceAccount:PrivateKeyId", "GOOGLE_PRIVATE_KEY_ID"),
        ("GoogleServiceAccount:PrivateKey", "GOOGLE_PRIVATE_KEY"),
        ("GoogleServiceAccount:ClientEmail", "GOOGLE_CLIENT_EMAIL"),
        ("GoogleServiceAccount:ClientId", "GOOGLE_CLIENT_ID"),
        ("GoogleServiceAccount:AuthUri", "GOOGLE_AUTH_URI"),
        ("GoogleServiceAccount:TokenUri", "GOOGLE_TOKEN_URI"),
        ("GoogleServiceAccount:AuthProviderX509CertUrl", "GOOGLE_AUTH_PROVIDER_CERT_URL"),
        ("GoogleServiceAccount:ClientX509CertUrl", "GOOGLE_CLIENT_CERT_URL"),
        ("GoogleServiceAccount:UniverseDomain", "GOOGLE_UNIVERSE_DOMAIN")
    };

    public static void Apply(IConfiguration configuration)
    {
        var awsSecretsLoaded = configuration.GetValue<bool>(
            PropSeekr.Configuration.AwsSecretsConfigurationLoader.SecretsLoadedKey);

        foreach (var (setting, environmentVariable) in Mappings)
        {
            var value = configuration[$"FileProcessor:{setting}"];

            if (!awsSecretsLoaded &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(environmentVariable)))
            {
                continue;
            }

            if (awsSecretsLoaded && string.IsNullOrWhiteSpace(value))
            {
                Environment.SetEnvironmentVariable(environmentVariable, null);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
                Environment.SetEnvironmentVariable(environmentVariable, value);
        }

        // The vendored processor constructs an AWS S3 client even for routes
        // that do not use S3. Give the AWS SDK the API's configured region so
        // embedding-only requests can initialize consistently outside AWS.
        var awsRegion = configuration["AWS:Region"];
        if (!string.IsNullOrWhiteSpace(awsRegion))
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_REGION")))
                Environment.SetEnvironmentVariable("AWS_REGION", awsRegion);
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION")))
                Environment.SetEnvironmentVariable("AWS_DEFAULT_REGION", awsRegion);
        }
    }
}
