using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PropSeekr.Data;
using PropSeekr.DTOs.Auth;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;
using Xunit;

namespace PropSeekr.Tests;

public sealed class PendingRegistrationTests
{
    [Fact]
    public async Task Register_StagesIdentityUntilBothOtpsAreVerified()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new AppDbContext(options);
        var service = new AuthService(
            db,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Key"] = "test-signing-key-of-adequate-length",
                ["Otp:ExpirationMinutes"] = "5"
            }).Build(),
            new NoopOtpDeliveryService(),
            new NoopEmailOtpService(),
            new NoopBrokerIdentityService(),
            new ProductionHostEnvironment());

        var response = await service.RegisterAsync(new RegisterRequestDto
        {
            Name = "Test Broker",
            Mobile = "9876543210",
            Email = "broker@example.com",
            Password = "SafePassword123!",
            AddressLine1 = "1 Test Street",
            City = "Indore",
            State = "Madhya Pradesh",
            Pincode = "452001",
            AadharNumber = "123456789012",
            PanCard = "ABCDE1234F"
        });

        Assert.True(response.VerificationRequired);
        Assert.Null(response.UserId);
        Assert.NotNull(response.PendingRegistrationId);
        Assert.Empty(db.Users);
        var pending = Assert.Single(db.PendingRegistrations);
        Assert.Equal("9876543210", pending.MobileNumber);
        Assert.Equal("broker@example.com", pending.Email);
    }

    private sealed class NoopOtpDeliveryService : IOtpDeliveryService
    {
        public bool IsConfigured => true;
        public Task SendOtpAsync(string mobileNumber, string otp, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResendOtpAsync(string mobileNumber, string otp, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopEmailOtpService : IEmailOtpService
    {
        public Task<SendEmailOtpResponseDto> SendEmailOtpAsync(SendEmailOtpRequestDto request, string? clientIp, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SendEmailOtpResponseDto());

        public Task<VerifyEmailOtpResponseDto> VerifyEmailOtpAsync(VerifyEmailOtpRequestDto request, string? clientIp, CancellationToken cancellationToken = default) =>
            Task.FromResult(new VerifyEmailOtpResponseDto());
    }

    private sealed class NoopBrokerIdentityService : IBrokerIdentityService
    {
        public Task<int?> GetBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public Task<int> GetOrCreateBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult(1);
    }

    private sealed class ProductionHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "PropSeekr.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
