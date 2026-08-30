using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using PropSeekr.Data;
using PropSeekr.DTOs.Auth;
using PropSeekr.Models;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;
using Xunit;

namespace PropSeekr.Tests;

/// <summary>
/// Uses isolated, temporary rows only. It must be explicitly enabled through
/// PROPSEEKR_TEST_DATABASE_URL and does not reset or alter shared schema data.
/// </summary>
public sealed class UnifiedLoginDatabaseTests
{
    [PostgreSqlFact]
    public async Task UnifiedLogin_UsesPersistedRoleAndEmitsMatchingJwtClaim()
    {
        var connectionString = Environment.GetEnvironmentVariable("PROPSEEKR_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        var suffix = Guid.NewGuid().ToString("N");
        var aadhar = $"9{DateTime.UtcNow.Ticks}"[^12..];
        var password = "DevOnly-Test-Pass-2026";
        var admin = new User
        {
            Id = Guid.NewGuid(),
            UserName = $"role-test-admin-{suffix}".ToLowerInvariant(),
            Name = "Role Test Admin",
            PasswordHash = CreatePasswordHash(password),
            IsActive = true,
            Role = "Admin"
        };
        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = "Role Test User",
            MobileNumber = suffix[..10],
            Email = $"role-test-{suffix}@example.test",
            AadharNumber = aadhar,
            PanCard = $"T{suffix[..9].ToUpperInvariant()}",
            PasswordHash = CreatePasswordHash(password),
            Role = "User",
            IsMobileVerified = true,
            IsEmailVerified = true
        };

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, postgres => postgres.UseNetTopologySuite())
            .Options;

        await using var db = new AppDbContext(options);
        db.Users.Add(admin);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Jwt:Key"] = "Development-only JWT key used exclusively by the database integration test.",
                    ["Jwt:Issuer"] = "PropSeekrTests",
                    ["Jwt:Audience"] = "PropSeekrTests",
                    ["Jwt:ExpiresMinutes"] = "60"
                })
                .Build();
            var service = new AuthService(
                db,
                configuration,
                new NoopOtpDeliveryService(),
                new NoopEmailOtpService(),
                new NoopBrokerIdentityService(),
                new TestHostEnvironment());

            var adminResponse = await service.LoginAsync(new LoginRequestDto
            {
                Identifier = admin.UserName,
                Password = password
            });
            var userResponse = await service.LoginAsync(new LoginRequestDto
            {
                Identifier = user.Email!,
                Password = password
            });

            Assert.Equal("Admin", adminResponse.Role);
            Assert.Equal("Admin", adminResponse.User.Role);
            Assert.Equal("Admin", ReadRole(adminResponse.Token));
            Assert.Equal("User", userResponse.Role);
            Assert.Equal("User", userResponse.User.Role);
            Assert.Equal("User", ReadRole(userResponse.Token));

        }
        finally
        {
            db.Users.Remove(admin);
            db.Users.Remove(user);
            await db.SaveChangesAsync();
        }
    }

    private static string ReadRole(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(c => c.Type == ClaimTypes.Role).Value;

    private static string CreatePasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, 100000, HashAlgorithmName.SHA256, 32);
        return $"PBKDF2-SHA256$100000${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private sealed class NoopOtpDeliveryService : IOtpDeliveryService
    {
        public bool IsConfigured => false;
        public Task SendOtpAsync(string mobileNumber, string otpCode, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResendOtpAsync(string mobileNumber, string otpCode, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopBrokerIdentityService : IBrokerIdentityService
    {
        public Task<int?> GetBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) => Task.FromResult<int?>(null);
        public Task<int> GetOrCreateBrokerIdAsync(Guid userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopEmailOtpService : IEmailOtpService
    {
        public Task<SendEmailOtpResponseDto> SendEmailOtpAsync(SendEmailOtpRequestDto request, string? clientIp, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SendEmailOtpResponseDto());

        public Task<VerifyEmailOtpResponseDto> VerifyEmailOtpAsync(VerifyEmailOtpRequestDto request, string? clientIp, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "PropSeekr.Tests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
