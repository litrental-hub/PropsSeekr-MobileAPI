using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace PropSeekr.Configuration;

/// <summary>
/// Loads the API's runtime secrets from one AWS Secrets Manager JSON secret.
/// The secret is intentionally a flat object of supported string key/value pairs.
/// </summary>
public static class AwsSecretsConfigurationLoader
{
    private const string SecretNameKey = "AWS:SecretsManagerConfigName";
    public const string SecretsLoadedKey = "AWS:SecretsLoaded";

    // This is the complete Secrets Manager contract. Keep it flat: do not add
    // nested objects, arrays, duplicate database fields, or unrelated values.
    private static readonly IReadOnlyDictionary<string, string[]> SecretKeyMappings =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["DB_CONNECTION_STRING"] = ["ConnectionStrings:DefaultConnection"],
            ["JWT_KEY"] = ["Jwt:Key"],
            ["RAZORPAY_KEY_ID"] = ["Razorpay:KeyId"],
            ["RAZORPAY_KEY_SECRET"] = ["Razorpay:KeySecret"],
            ["RAZORPAY_WEBHOOK_SECRET"] = ["Razorpay:WebhookSecret"],
            ["MSG91_AUTH_KEY"] = ["Msg91:AuthKey"],
            ["MSG91_OTP_TEMPLATE_ID"] = ["Msg91:OtpTemplateId"],
            ["INTERNAL_SERVICE_API_KEY"] = ["InternalService:ApiKey"],
            ["OPENAI_API_KEY"] = ["FileProcessor:OpenAiApiKey"],
            ["S3_BUCKET_NAME"] = ["FileProcessor:S3BucketName"],
            ["GOOGLE_MAPS_API_KEY"] = ["FileProcessor:GoogleMapsApiKey"],
            ["GOOGLE_SERVICE_ACCOUNT_TYPE"] = ["FileProcessor:GoogleServiceAccount:Type"],
            ["GOOGLE_CLOUD_PROJECT"] = ["FileProcessor:GoogleServiceAccount:ProjectId"],
            ["GOOGLE_PRIVATE_KEY_ID"] = ["FileProcessor:GoogleServiceAccount:PrivateKeyId"],
            ["GOOGLE_PRIVATE_KEY"] = ["FileProcessor:GoogleServiceAccount:PrivateKey"],
            ["GOOGLE_CLIENT_EMAIL"] = ["FileProcessor:GoogleServiceAccount:ClientEmail"],
            ["GOOGLE_CLIENT_ID"] = ["FileProcessor:GoogleServiceAccount:ClientId"]
        };

    private static readonly string[] SecretOnlyKeys = SecretKeyMappings.Values
        .SelectMany(keys => keys)
        .ToArray();

    public static void Load(WebApplicationBuilder builder)
    {
        var secretName = builder.Configuration[SecretNameKey];
        if (string.IsNullOrWhiteSpace(secretName))
        {
            if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException(
                    $"{SecretNameKey} must be configured outside Development. Runtime secrets must come from AWS Secrets Manager.");
            }

            return;
        }

        var secretString = FetchSecret(secretName, builder.Configuration);
        var values = ParseSecret(secretString);
        builder.Configuration.AddInMemoryCollection(values);

        Console.WriteLine("[Configuration] Runtime secrets loaded from AWS Secrets Manager.");
    }

    public static IReadOnlyDictionary<string, string?> ParseSecret(string secretString)
    {
        if (string.IsNullOrWhiteSpace(secretString))
            throw new InvalidOperationException("AWS Secrets Manager returned an empty SecretString.");

        try
        {
            using var document = JsonDocument.Parse(secretString);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException("The AWS configuration secret must be a flat JSON object.");

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!SecretKeyMappings.TryGetValue(property.Name, out var targets))
                    throw new InvalidOperationException($"Unsupported AWS secret key '{property.Name}'. Remove it or add an explicit application mapping.");

                if (property.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidOperationException($"AWS secret key '{property.Name}' must have a string value; arrays and objects are not supported.");

                var value = property.Value.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(value))
                    throw new InvalidOperationException($"AWS secret key '{property.Name}' cannot be empty.");

                foreach (var target in targets)
                    values[target] = value;
            }

            // Do not let a missing AWS secret silently fall back to appsettings
            // or an environment variable when Secrets Manager is configured.
            foreach (var key in SecretOnlyKeys)
                values.TryAdd(key, null);

            values[SecretsLoadedKey] = bool.TrueString;
            return values;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The AWS configuration secret is not valid JSON.", ex);
        }
    }

    private static string FetchSecret(string secretName, IConfiguration configuration)
    {
        const string tokenPath = "/var/run/awssmatoken";
        if (File.Exists(tokenPath))
        {
            try
            {
                var token = File.ReadAllText(tokenPath).Trim();
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("X-Aws-Parameters-Secrets-Token", token);

                var url = $"http://localhost:2773/secretsmanager/get?secretId={Uri.EscapeDataString(secretName)}";
                var response = client.GetAsync(url).GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode)
                {
                    var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    using var document = JsonDocument.Parse(content);
                    if (document.RootElement.TryGetProperty("SecretString", out var secretString))
                        return secretString.GetString()
                            ?? throw new InvalidOperationException("The Secrets Manager Agent returned an empty SecretString.");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Configuration] Secrets Manager Agent unavailable; falling back to the AWS SDK ({ex.GetType().Name}).");
            }
        }

        try
        {
            var region = configuration["AWS:Region"] ?? "ap-south-1";
            using IAmazonSecretsManager client =
                new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
            var response = client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretName })
                .GetAwaiter()
                .GetResult();

            return response.SecretString
                ?? throw new InvalidOperationException("AWS Secrets Manager returned a binary or empty secret; JSON SecretString is required.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Unable to load runtime configuration from AWS Secrets Manager. Verify the secret name, region, and ECS task-role permission.",
                ex);
        }
    }
}
