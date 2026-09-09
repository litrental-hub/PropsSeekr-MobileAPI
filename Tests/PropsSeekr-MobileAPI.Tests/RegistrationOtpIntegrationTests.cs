using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PropSeekr.Data;
using PropSeekr.DTOs.Auth;
using PropSeekr.Models;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;
using Xunit;

namespace PropSeekr.Tests;

// SQL identifiers below are generated GUID schemas or fixed migration table
// names, never caller input; identifiers cannot be bound as SQL parameters.
#pragma warning disable EF1002

/// <summary>Each test owns a random schema; never resets public or uses appsettings.</summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class RegistrationOtpIntegrationTests : IAsyncLifetime
{
    private readonly string _schema = "registration_test_" + Guid.NewGuid().ToString("N");
    private RegistrationDbContext? _db;
    private AuthService _auth = null!;
    private EmailOtpService _emailOtp = null!;
    private readonly CapturingEmail _email = new();
    private readonly CapturingSms _sms = new();

    public async Task InitializeAsync()
    {
        var connection = Environment.GetEnvironmentVariable("PROPSEEKR_TEST_DATABASE_URL");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var builder = new NpgsqlConnectionStringBuilder(connection) { SearchPath = _schema };
        _db = new RegistrationDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(builder.ConnectionString).Options);
        await _db.Database.ExecuteSqlRawAsync($"CREATE SCHEMA {_schema}");
        await _db.Database.ExecuteSqlRawAsync(_db.Database.GenerateCreateScript());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "test-only-signing-key-never-used-outside-isolated-tests",
            ["Otp:ResendCooldownSeconds"] = "0"
        }).Build();
        _emailOtp = new EmailOtpService(_db, config, _email, NullLogger<EmailOtpService>.Instance);
        _auth = new AuthService(_db, config, _sms, _emailOtp,
            new BrokerIdentityService(_db), new ProductionEnvironment());
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        await _db.Database.ExecuteSqlRawAsync($"DROP SCHEMA {_schema} CASCADE");
        await _db.DisposeAsync();
    }

    [PostgreSqlFact]
    public async Task BothOtps_CreateOneUserAndWallet_AndMobileReplayIsRejected()
    {
        var pending = await _auth.RegisterAsync(Request());
        Assert.Null(pending.UserId);
        Assert.Empty(await _db!.Users.ToListAsync());
        await Assert.ThrowsAsync<Exception>(() => _auth.SendOtpAsync(new() { MobileNumber = "9876543210" }));

        var emailResult = await VerifyEmail();
        Assert.Null(emailResult.Token);
        Assert.Empty(await _db.Users.ToListAsync());
        await _auth.SendOtpAsync(new() { MobileNumber = "9876543210" });
        var result = await _auth.VerifyOtpAsync(new() { Mobile = "9876543210", Otp = _sms.Code });
        Assert.NotEmpty(result.Token);
        Assert.NotNull(result.BrokerId);
        Assert.Empty(await _db.PendingRegistrations.ToListAsync());
        var user = Assert.Single(await _db.Users.ToListAsync());
        Assert.True(user.IsMobileVerified && user.IsEmailVerified);
        Assert.Equal(result.BrokerId, user.BrokerId);
        Assert.Equal("Suite 2", user.AddressLine2);
        Assert.Equal("TEST-GST", user.GSTNumber);
        Assert.Equal("TEST-RERA", user.ReraRegistrationNumber);
        var wallet = Assert.Single(await _db.CreditWallets.ToListAsync());
        Assert.Equal(10, wallet.FreeCreditsBalance);
        Assert.Equal(0, wallet.PaidCreditsBalance);
        await Assert.ThrowsAsync<Exception>(() => _auth.VerifyOtpAsync(new() { Mobile = "9876543210", Otp = _sms.Code }));
        Assert.Single(await _db.CreditWallets.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task EmailDeliveryFailureAndSamePasswordRetry_KeepOnePendingIdentity()
    {
        _email.Fail = true;
        var first = await _auth.RegisterAsync(Request());
        Assert.True(first.VerificationRequired);
        Assert.Contains("could not be sent", first.Message);
        var original = Assert.Single(await _db!.PendingRegistrations.ToListAsync());
        var expiry = original.ExpiresAt;
        var wrongPassword = Request();
        wrongPassword.Password = "AnotherPassword123!";
        await Assert.ThrowsAsync<Exception>(() => _auth.RegisterAsync(wrongPassword));
        _email.Fail = false;
        var retry = await _auth.RegisterAsync(Request());
        Assert.Equal(first.PendingRegistrationId, retry.PendingRegistrationId);
        Assert.Equal(expiry, Assert.Single(await _db.PendingRegistrations.ToListAsync()).ExpiresAt);
        Assert.Empty(await _db.Users.ToListAsync());
        Assert.True((await VerifyEmail()).Success);
    }

    [PostgreSqlFact]
    public async Task MobileOtp_CannotAuthenticateInactiveOrEmailUnverifiedExistingUsers()
    {
        foreach (var inactive in new[] { true, false })
        {
            var mobile = inactive ? "9876543211" : "9876543212";
            _db!.Users.Add(new User
            {
                Id = Guid.NewGuid(), Name = "Legacy test", MobileNumber = mobile,
                IsActive = !inactive, IsEmailVerified = inactive, IsMobileVerified = true
            });
            _db.OtpVerifications.Add(new OtpVerification
            {
                Id = Guid.NewGuid(), MobileNumber = mobile, OtpCode = "123456",
                ExpiresAt = DateTime.UtcNow.AddMinutes(5)
            });
            await _db.SaveChangesAsync();
            await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.SendOtpAsync(new() { MobileNumber = mobile }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyOtpAsync(new() { Mobile = mobile, Otp = "123456" }));
            _db.ChangeTracker.Clear();
            Assert.False((await _db.OtpVerifications.SingleAsync(x => x.MobileNumber == mobile)).IsUsed);
        }
        Assert.Empty(await _db!.CreditWallets.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task EmailOtp_DoesNotIssueTokensForInactiveUnverifiedOrUnlinkedUsers()
    {
        foreach (var state in new[] { (Active: false, Mobile: true), (Active: true, Mobile: false), (Active: true, Mobile: true) })
        {
            var email = $"{state.Active}-{state.Mobile}@example.test".ToLowerInvariant();
            _db!.Users.Add(new User
            {
                Id = Guid.NewGuid(), Name = "Legacy test", Email = email,
                IsActive = state.Active, IsMobileVerified = state.Mobile
            });
            await _db.SaveChangesAsync();
            await _emailOtp.SendEmailOtpAsync(new() { Email = email, Purpose = "EmailVerification" }, null);
            var response = await VerifyEmail(email);
            Assert.True(response.Success);
            Assert.Null(response.Token);
            Assert.Null(response.RefreshToken);
        }
    }

    [PostgreSqlFact]
    public async Task VerifiedEmailLogin_PreservesRole_AndPasswordResetDoesNotAuthenticate()
    {
        _db!.Users.Add(new User
        {
            Id = Guid.NewGuid(), Name = "Test Admin", Email = "broker@example.test",
            IsActive = true, IsMobileVerified = true, IsEmailVerified = true, Role = "Admin"
        });
        await _db.SaveChangesAsync();
        await _emailOtp.SendEmailOtpAsync(new() { Email = "broker@example.test", Purpose = "Login" }, null);
        var response = await VerifyEmail(purpose: "Login");
        Assert.Equal("Admin", new JwtSecurityTokenHandler().ReadJwtToken(response.Token!).Claims
            .Single(claim => claim.Type == ClaimTypes.Role).Value);
        await _emailOtp.SendEmailOtpAsync(new() { Email = "broker@example.test", Purpose = "PasswordReset" }, null);
        Assert.Null((await VerifyEmail(purpose: "PasswordReset")).Token);
    }

    [PostgreSqlFact]
    public async Task EmailProofFromBeforeRegistration_CannotVerifyANewPendingIdentity()
    {
        await _auth.RegisterAsync(Request());
        var pending = await _db!.PendingRegistrations.SingleAsync();
        _db.EmailOtpRecords.Add(new EmailOtpRecord
        {
            Email = pending.Email, OtpHash = "old-proof", IsUsed = true,
            CreatedAt = pending.CreatedAt.AddMinutes(-1), UsedAt = DateTime.UtcNow,
            ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<Exception>(() => _auth.SendOtpAsync(new() { MobileNumber = pending.MobileNumber }));
        Assert.Empty(await _db.Users.ToListAsync());
    }

    private Task<VerifyEmailOtpResponseDto> VerifyEmail(string email = "broker@example.test", string purpose = "EmailVerification") =>
        _emailOtp.VerifyEmailOtpAsync(new() { Email = email, Purpose = purpose, Otp = _email.Code }, null);

    [PostgreSqlFact]
    public async Task MobileCodeIssuedBeforeRegistration_CannotCreateAccount()
    {
        await _auth.RegisterAsync(Request());
        await VerifyEmail();
        var pending = await _db!.PendingRegistrations.SingleAsync();
        _db.OtpVerifications.Add(new OtpVerification
        {
            Id = Guid.NewGuid(), MobileNumber = pending.MobileNumber, OtpCode = "123456",
            CreatedDate = pending.CreatedAt.AddMinutes(-1), ExpiresAt = DateTime.UtcNow.AddMinutes(5)
        });
        await _db.SaveChangesAsync();
        await Assert.ThrowsAsync<Exception>(() => _auth.VerifyOtpAsync(new() { Mobile = pending.MobileNumber, Otp = "123456" }));
        Assert.Empty(await _db.Users.ToListAsync());
        Assert.Empty(await _db.CreditWallets.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task LegacyRetirementGuard_RejectsNonemptyTablesWithoutDeletingHistory()
    {
        var operations = new PropSeekr.Migrations.RetireLegacyCompatibilityTables().UpOperations;
        foreach (var table in operations.OfType<Microsoft.EntityFrameworkCore.Migrations.Operations.DropTableOperation>())
            await _db!.Database.ExecuteSqlRawAsync($"CREATE TABLE \"{table.Name}\" (id integer)");
        var guard = Assert.IsType<Microsoft.EntityFrameworkCore.Migrations.Operations.SqlOperation>(operations[0]);
        await _db!.Database.ExecuteSqlRawAsync(guard.Sql);
        await _db.Database.ExecuteSqlRawAsync("INSERT INTO \"Notifications\" (id) VALUES (1)");
        await Assert.ThrowsAsync<PostgresException>(() => _db.Database.ExecuteSqlRawAsync(guard.Sql));
        Assert.Equal(1, await _db.Database.SqlQueryRaw<int>("SELECT count(*)::integer AS \"Value\" FROM \"Notifications\"").SingleAsync());
    }

    private static RegisterRequestDto Request() => new()
    {
        Name = "Test Broker", Mobile = "9876543210", Email = "broker@example.test",
        Password = "SafePassword123!", AddressLine1 = "1 Test Street", City = "Indore",
        State = "Madhya Pradesh", Pincode = "452001", AadharNumber = "123456789012", PanCard = "ABCDE1234F",
        AddressLine2 = "Suite 2", GstNumber = "TEST-GST", ReraRegistrationNumber = "TEST-RERA"
    };

    private sealed class CapturingEmail : IEmailService
    {
        public bool Fail { get; set; }
        public string Code { get; private set; } = "";
        public Task SendEmailAsync(string recipientEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken = default)
        {
            if (Fail) throw new InvalidOperationException("Simulated email delivery failure");
            Code = Regex.Match(textBody, @"\b\d{6}\b").Value;
            return Task.CompletedTask;
        }
    }

    private sealed class CapturingSms : IOtpDeliveryService
    {
        public bool IsConfigured => true;
        public string Code { get; private set; } = "";
        public Task SendOtpAsync(string mobileNumber, string otp, CancellationToken cancellationToken = default)
        { Code = otp; return Task.CompletedTask; }
        public Task ResendOtpAsync(string mobileNumber, string otp, CancellationToken cancellationToken = default) => SendOtpAsync(mobileNumber, otp, cancellationToken);
    }

    private sealed class ProductionEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "PropSeekr.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class RegistrationDbContext(DbContextOptions<AppDbContext> options) : AppDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            var retained = new[] { typeof(User), typeof(Broker), typeof(CreditWallet), typeof(OtpVerification), typeof(EmailOtpRecord), typeof(PendingRegistration) };
            foreach (var entity in builder.Model.GetEntityTypes().ToArray())
                if (!retained.Contains(entity.ClrType)) builder.Ignore(entity.ClrType);
        }
    }
}
