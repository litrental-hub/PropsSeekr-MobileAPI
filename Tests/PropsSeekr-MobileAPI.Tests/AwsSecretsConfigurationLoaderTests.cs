using PropSeekr.Configuration;
using Xunit;

namespace PropSeekr.Tests;

public sealed class AwsSecretsConfigurationLoaderTests
{
    [Fact]
    public void ParseSecret_MapsTheFlatSecretContract()
    {
        const string json = """
            {
              "DB_CONNECTION_STRING": "Host=database.internal;Password=db-password",
              "JWT_KEY": "jwt-value",
              "GOOGLE_PRIVATE_KEY": "private-key"
            }
            """;

        var result = AwsSecretsConfigurationLoader.ParseSecret(json);

        Assert.Equal("Host=database.internal;Password=db-password", result["ConnectionStrings:DefaultConnection"]);
        Assert.Equal("jwt-value", result["Jwt:Key"]);
        Assert.Equal("private-key", result["FileProcessor:GoogleServiceAccount:PrivateKey"]);
        Assert.Equal(bool.TrueString, result[AwsSecretsConfigurationLoader.SecretsLoadedKey]);
    }

    [Theory]
    [InlineData("{ \"host\": \"database.internal\" }")]
    [InlineData("{ \"DB_CONNECTION_STRING\": { \"value\": \"Host=db\" } }")]
    [InlineData("{ \"DB_CONNECTION_STRING\": [\"Host=db\"] }")]
    public void ParseSecret_RejectsLegacyAndNonFlatValues(string json)
    {
        Assert.Throws<InvalidOperationException>(() => AwsSecretsConfigurationLoader.ParseSecret(json));
    }

    [Fact]
    public void ParseSecret_MasksSecretValuesMissingFromAws()
    {
        var result = AwsSecretsConfigurationLoader.ParseSecret("{ \"JWT_KEY\": \"from-aws\" }");

        Assert.Equal("from-aws", result["Jwt:Key"]);
        Assert.True(result.ContainsKey("Razorpay:KeySecret"));
        Assert.Null(result["Razorpay:KeySecret"]);
    }
}
