using System.Text.Json;
using Amazon;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;

namespace PropSeekr.Configuration;

/// <summary>
/// Loads the API's runtime secrets from one AWS Secrets Manager JSON secret.
/// The ECS task role supplies temporary AWS credentials through the SDK's
/// default credential chain; long-lived AWS access keys are never required.
/// </summary>
public static class AwsSecretsConfigurationLoader
{
    private const string SecretNameKey = "AWS:SecretsManagerConfigName";
    public const string SecretsLoadedKey = "AWS:SecretsLoaded";

    private static readonly string[] SecretOnlyKeys =
    [
        "ConnectionStrings:DefaultConnection",
        "Jwt:Key",
        "Razorpay:KeyId",
        "Razorpay:KeySecret",
        "Razorpay:WebhookSecret",
        "Msg91:AuthKey",
        "Msg91:OtpTemplateId",
        "InternalService:ApiKey",
        "FileProcessor:OpenAiApiKey",
        "FileProcessor:DbConnectionString",
        "FileProcessor:DbPassword",
        "FileProcessor:GoogleMapsApiKey",
        "FileProcessor:GoogleApiKey",
        "FileProcessor:GoogleServiceAccount:PrivateKeyId",
        "FileProcessor:GoogleServiceAccount:PrivateKey",
        "FileProcessor:GoogleServiceAccount:ClientEmail",
        "FileProcessor:GoogleServiceAccount:ClientId"
    ];

    private static readonly IReadOnlyDictionary<string, string[]> Aliases =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["ConnectionStrings:DefaultConnection"] = ["FileProcessor:DbConnectionString"],
            ["ConnectionString"] = ["ConnectionStrings:DefaultConnection", "FileProcessor:DbConnectionString"],
            ["DefaultConnection"] = ["ConnectionStrings:DefaultConnection", "FileProcessor:DbConnectionString"],
            ["DB_CONNECTION_STRING"] = ["ConnectionStrings:DefaultConnection", "FileProcessor:DbConnectionString"],
            ["DB_HOST"] = ["FileProcessor:DbHost"],
            ["DB_PORT"] = ["FileProcessor:DbPort"],
            ["DB_NAME"] = ["FileProcessor:DbName"],
            ["DB_USERNAME"] = ["FileProcessor:DbUsername"],
            ["DB_PASSWORD"] = ["FileProcessor:DbPassword"],
            ["host"] = ["FileProcessor:DbHost"],
            ["port"] = ["FileProcessor:DbPort"],
            ["dbname"] = ["FileProcessor:DbName"],
            ["username"] = ["FileProcessor:DbUsername"],
            ["password"] = ["FileProcessor:DbPassword"],
            ["JWT_KEY"] = ["Jwt:Key"],
            ["JWT_ISSUER"] = ["Jwt:Issuer"],
            ["JWT_AUDIENCE"] = ["Jwt:Audience"],
            ["RAZORPAY_KEY_ID"] = ["Razorpay:KeyId"],
            ["RAZORPAY_KEY_SECRET"] = ["Razorpay:KeySecret"],
            ["RAZORPAY_WEBHOOK_SECRET"] = ["Razorpay:WebhookSecret"],
            ["MSG91_AUTH_KEY"] = ["Msg91:AuthKey"],
            ["MSG91_OTP_TEMPLATE_ID"] = ["Msg91:OtpTemplateId"],
            ["INTERNAL_SERVICE_API_KEY"] = ["InternalService:ApiKey"],
            ["OPENAI_API_KEY"] = ["FileProcessor:OpenAiApiKey"],
            ["S3_BUCKET_NAME"] = ["FileProcessor:S3BucketName"],
            ["GOOGLE_MAPS_API_KEY"] = ["FileProcessor:GoogleMapsApiKey"],
            ["GOOGLE_API_KEY"] = ["FileProcessor:GoogleApiKey"],
            ["GOOGLE_SERVICE_ACCOUNT_TYPE"] = ["FileProcessor:GoogleServiceAccount:Type"],
            ["GOOGLE_CLOUD_PROJECT"] = ["FileProcessor:GoogleServiceAccount:ProjectId"],
            ["GOOGLE_PRIVATE_KEY_ID"] = ["FileProcessor:GoogleServiceAccount:PrivateKeyId"],
            ["GOOGLE_PRIVATE_KEY"] = ["FileProcessor:GoogleServiceAccount:PrivateKey"],
            ["GOOGLE_CLIENT_EMAIL"] = ["FileProcessor:GoogleServiceAccount:ClientEmail"],
            ["GOOGLE_CLIENT_ID"] = ["FileProcessor:GoogleServiceAccount:ClientId"],
            ["GOOGLE_AUTH_URI"] = ["FileProcessor:GoogleServiceAccount:AuthUri"],
            ["GOOGLE_TOKEN_URI"] = ["FileProcessor:GoogleServiceAccount:TokenUri"],
            ["GOOGLE_AUTH_PROVIDER_CERT_URL"] = ["FileProcessor:GoogleServiceAccount:AuthProviderX509CertUrl"],
            ["GOOGLE_CLIENT_CERT_URL"] = ["FileProcessor:GoogleServiceAccount:ClientX509CertUrl"],
            ["GOOGLE_UNIVERSE_DOMAIN"] = ["FileProcessor:GoogleServiceAccount:UniverseDomain"]
        };

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
                throw new InvalidOperationException("The AWS configuration secret must be a JSON object.");

            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            FlattenObject(document.RootElement, null, values);
            ApplyAliases(values);

            // Once AWS loading is enabled, a missing secret must not fall back
            // to an earlier appsettings or environment-variable provider.
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

    private static void FlattenObject(
        JsonElement element,
        string? prefix,
        IDictionary<string, string?> values)
    {
        foreach (var property in element.EnumerateObject())
        {
            var segment = property.Name.Replace("__", ":", StringComparison.Ordinal);
            var key = string.IsNullOrEmpty(prefix) ? segment : $"{prefix}:{segment}";

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                FlattenObject(property.Value, key, values);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                var index = 0;
                foreach (var item in property.Value.EnumerateArray())
                {
                    values[$"{key}:{index++}"] = ScalarValue(item);
                }
                continue;
            }

            values[key] = ScalarValue(property.Value)?.Trim();
        }
    }

    private static string? ScalarValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText()
    };

    private static void ApplyAliases(IDictionary<string, string?> values)
    {
        foreach (var (alias, targets) in Aliases)
        {
            if (!values.TryGetValue(alias, out var value) || string.IsNullOrWhiteSpace(value))
                continue;

            foreach (var target in targets)
            {
                if (!values.TryGetValue(target, out var existing) || string.IsNullOrWhiteSpace(existing))
                    values[target] = value;
            }
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
