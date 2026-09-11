using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PropSeekr.Configuration;
using Xunit;

namespace PropSeekr.Tests;

public sealed class RuntimeConfigurationValidatorTests
{
    [Fact]
    public void Validate_DevelopmentWithoutSecrets_AllowsLocalStartup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        RuntimeConfigurationValidator.Validate(configuration, new TestEnvironment("Development"));
    }

    [Fact]
    public void Validate_LoadedSecretMissingSecurityValues_FailsStartup()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [AwsSecretsConfigurationLoader.SecretsLoadedKey] = bool.TrueString,
            ["ConnectionStrings:DefaultConnection"] = "Host=example"
        }).Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
            RuntimeConfigurationValidator.Validate(configuration, new TestEnvironment("Development")));
        Assert.Contains("InternalService:ApiKey", error.Message);
        Assert.Contains("Razorpay:WebhookSecret", error.Message);
    }

    private sealed class TestEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
