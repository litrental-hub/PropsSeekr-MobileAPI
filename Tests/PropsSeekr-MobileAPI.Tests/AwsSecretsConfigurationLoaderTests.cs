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
              "MSG91_WIDGET_ID": "test-widget-id",
              "MSG91_WIDGET_TOKEN_AUTH": "test-client-token",
              "GOOGLE_PRIVATE_KEY": "private-key"
            }
            """;

        var result = AwsSecretsConfigurationLoader.ParseSecret(json);

        Assert.Equal("Host=database.internal;Password=db-password", result["ConnectionStrings:DefaultConnection"]);
        Assert.Equal("jwt-value", result["Jwt:Key"]);
        Assert.Equal("test-widget-id", result["Msg91:WidgetId"]);
        Assert.Equal("test-client-token", result["Msg91:WidgetTokenAuth"]);
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

    [Fact]
    public void ApplyNonSecretOverrides_ReplacesOnlyTheWidgetId()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Msg91:WidgetId"] = "secret-widget-id",
            ["Msg91:WidgetTokenAuth"] = "secret-token",
            ["Jwt:Key"] = "secret-jwt"
        };

        AwsSecretsConfigurationLoader.ApplyNonSecretOverrides(values, "  deployed-widget-id  ");

        Assert.Equal("deployed-widget-id", values["Msg91:WidgetId"]);
        Assert.Equal("secret-token", values["Msg91:WidgetTokenAuth"]);
        Assert.Equal("secret-jwt", values["Jwt:Key"]);
    }

    [Fact]
    public void ApplyNonSecretOverrides_IgnoresAnEmptyWidgetId()
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Msg91:WidgetId"] = "secret-widget-id"
        };

        AwsSecretsConfigurationLoader.ApplyNonSecretOverrides(values, "   ");

        Assert.Equal("secret-widget-id", values["Msg91:WidgetId"]);
    }
}
