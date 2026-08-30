using PropSeekr.Configuration;
using Xunit;

namespace PropSeekr.Tests;

public sealed class AwsSecretsConfigurationLoaderTests
{
    [Fact]
    public void ParseSecret_FlattensNestedConfiguration()
    {
        const string json = """
            {
              "ConnectionStrings": { "DefaultConnection": "Host=db;Password=test" },
              "Jwt": { "Key": "jwt-value" },
              "FileProcessor": {
                "GoogleServiceAccount": { "PrivateKey": "private-key" }
              }
            }
            """;

        var result = AwsSecretsConfigurationLoader.ParseSecret(json);

        Assert.Equal("Host=db;Password=test", result["ConnectionStrings:DefaultConnection"]);
        Assert.Equal("Host=db;Password=test", result["FileProcessor:DbConnectionString"]);
        Assert.Equal("jwt-value", result["Jwt:Key"]);
        Assert.Equal("private-key", result["FileProcessor:GoogleServiceAccount:PrivateKey"]);
    }

    [Fact]
    public void ParseSecret_MapsLegacyEnvironmentNamesToCanonicalConfiguration()
    {
        const string json = """
            {
              "DB_HOST": "database.internal",
              "DB_PASSWORD": "db-password",
              "JWT_KEY": "jwt-value",
              "RAZORPAY_KEY_SECRET": "payment-secret",
              "GOOGLE_PRIVATE_KEY": "private-key"
            }
            """;

        var result = AwsSecretsConfigurationLoader.ParseSecret(json);

        Assert.Equal("database.internal", result["FileProcessor:DbHost"]);
        Assert.Equal("db-password", result["FileProcessor:DbPassword"]);
        Assert.Equal("jwt-value", result["Jwt:Key"]);
        Assert.Equal("payment-secret", result["Razorpay:KeySecret"]);
        Assert.Equal("private-key", result["FileProcessor:GoogleServiceAccount:PrivateKey"]);
    }

    [Fact]
    public void ParseSecret_MapsStandardRdsSecretShape()
    {
        const string json = """
            {
              "host": "database.internal",
              "port": 5432,
              "dbname": "propseekr_v2",
              "username": "postgres",
              "password": "db-password"
            }
            """;

        var result = AwsSecretsConfigurationLoader.ParseSecret(json);

        Assert.Equal("database.internal", result["FileProcessor:DbHost"]);
        Assert.Equal("5432", result["FileProcessor:DbPort"]);
        Assert.Equal("propseekr_v2", result["FileProcessor:DbName"]);
        Assert.Equal("postgres", result["FileProcessor:DbUsername"]);
        Assert.Equal("db-password", result["FileProcessor:DbPassword"]);
    }

    [Fact]
    public void ParseSecret_MasksSecretValuesMissingFromAws()
    {
        var result = AwsSecretsConfigurationLoader.ParseSecret("{ \"Jwt\": { \"Key\": \"from-aws\" } }");

        Assert.Equal("from-aws", result["Jwt:Key"]);
        Assert.True(result.ContainsKey("Razorpay:KeySecret"));
        Assert.Null(result["Razorpay:KeySecret"]);
        Assert.Equal(bool.TrueString, result[AwsSecretsConfigurationLoader.SecretsLoadedKey]);
    }
}
