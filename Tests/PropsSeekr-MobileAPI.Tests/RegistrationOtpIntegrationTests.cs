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
    private IConfiguration _config = null!;
    private readonly VerifyingWidget _widget = new();

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
        _config = config;
        _emailOtp = new EmailOtpService(_db, config, _email, NullLogger<EmailOtpService>.Instance);
        _auth = new AuthService(_db, config, _sms, _emailOtp,
            new BrokerIdentityService(_db, new WalletAccountingService(_db)), new ProductionEnvironment(), _widget);
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

    private void EnableWidget()
    {
        _config["Msg91:WidgetEnabled"] = "true";
        _config["Msg91:WidgetId"] = "test-widget";
        _config["Msg91:WidgetTokenAuth"] = "test-client-token";
        _config["Msg91:AuthKey"] = "test-backend-key";
    }

    [PostgreSqlFact]
    public async Task WidgetRegistration_PreservesBothProofsAndRejectsReplayAcrossChallenges()
    {
        EnableWidget();
        await _auth.RegisterAsync(Request());
        await Assert.ThrowsAsync<Exception>(() => _auth.SendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true }));
        await VerifyEmail();
        var issued = await _auth.SendOtpAsync(new() { MobileNumber = "+919876543210", SupportsWidget = true });
        Assert.NotNull(issued.Widget);
        Assert.Equal("919876543210", issued.Widget.Identifier);
        Assert.Empty(await _db!.OtpVerifications.ToListAsync());
        Assert.Equal("", _sms.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyOtpAsync(new() { Mobile = "9876543210", Otp = "123456" }));
        var request = new VerifyWidgetOtpRequestDto { ChallengeId = issued.Widget.ChallengeId, AccessToken = "valid-proof" };
        var result = await _auth.VerifyWidgetOtpAsync(request);
        Assert.NotNull(result.BrokerId);
        Assert.Equal("User", result.Role);
        Assert.Empty(await _db.PendingRegistrations.ToListAsync());
        Assert.True(Assert.Single(await _db.Users.ToListAsync()).IsEmailVerified);
        Assert.Equal(10, Assert.Single(await _db.CreditWallets.ToListAsync()).FreeCreditsBalance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyWidgetOtpAsync(request));
        var next = await _auth.SendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyWidgetOtpAsync(new()
        { ChallengeId = next.Widget!.ChallengeId, AccessToken = "valid-proof" }));
        Assert.Single(await _db.CreditWallets.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task WidgetFailureOrExpiry_CannotPromoteRegistration()
    {
        EnableWidget();
        await _auth.RegisterAsync(Request());
        await VerifyEmail();
        var issued = await _auth.SendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyWidgetOtpAsync(new()
        { ChallengeId = issued.Widget!.ChallengeId, AccessToken = "wrong-phone-proof" }));
        Assert.Empty(await _db!.Users.ToListAsync());
        Assert.Null((await _db.WidgetOtpChallenges.AsNoTracking().SingleAsync()).ConsumedTokenHash);
        await _db.WidgetOtpChallenges.ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyWidgetOtpAsync(new()
        { ChallengeId = issued.Widget!.ChallengeId, AccessToken = "valid-proof" }));
        Assert.Empty(await _db.CreditWallets.ToListAsync());
    }

    [PostgreSqlFact]
    public async Task WidgetConcurrentProof_OnlyOneSessionAndWallet()
    {
        EnableWidget();
        await _auth.RegisterAsync(Request());
        await VerifyEmail();
        var first = await _auth.SendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true });
        var second = await _auth.SendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true });
        await using var otherDb = new RegistrationDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_db!.Database.GetConnectionString()).Options);
        var otherAuth = new AuthService(otherDb, _config, _sms, _emailOtp,
            new BrokerIdentityService(otherDb, new WalletAccountingService(otherDb)), new ProductionEnvironment(), _widget);
        static async Task<bool> Complete(AuthService auth, Guid challengeId)
        {
            try { await auth.VerifyWidgetOtpAsync(new() { ChallengeId = challengeId, AccessToken = "valid-proof" }); return true; }
            catch (InvalidOperationException) { return false; }
        }
        var results = await Task.WhenAll(Complete(_auth, first.Widget!.ChallengeId), Complete(otherAuth, second.Widget!.ChallengeId));
        Assert.Single(results, x => x);
        Assert.Single(await _db.Users.ToListAsync());
        Assert.Single(await _db.CreditWallets.ToListAsync());
        Assert.Equal(1, await _db.WidgetOtpChallenges.CountAsync(x => x.ConsumedTokenHash != null));
    }

    [PostgreSqlFact]
    public async Task WidgetAdmission_RejectsOldClientsOtherCountriesAndExcessChallenges()
    {
        EnableWidget();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.SendOtpAsync(new() { MobileNumber = "9876543210" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.SendOtpAsync(new() { MobileNumber = "449876543210", SupportsWidget = true }));
        await _auth.RegisterAsync(Request());
        await VerifyEmail();
        for (var i = 0; i < 5; i++)
            await _auth.SendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true });
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.ResendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true }));
        Assert.Equal(5, await _db!.WidgetOtpChallenges.CountAsync());
    }

    [PostgreSqlFact]
    public async Task WidgetRechecksActiveAccountBeforeIssuingSession()
    {
        EnableWidget();
        _db!.Users.Add(new User
        {
            Id = Guid.NewGuid(), Name = "Test Admin", MobileNumber = "9876543210",
            IsActive = true, IsEmailVerified = true, Role = "Admin"
        });
        await _db.SaveChangesAsync();
        var session = await _auth.SendOtpAsync(new() { MobileNumber = "9876543210", SupportsWidget = true });
        await _db.Users.ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        _db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyWidgetOtpAsync(new()
        { ChallengeId = session.Widget!.ChallengeId, AccessToken = "valid-proof" }));
        Assert.Null((await _db.WidgetOtpChallenges.AsNoTracking().SingleAsync()).ConsumedTokenHash);
        Assert.Empty(await _db.CreditWallets.ToListAsync());
    }

    private sealed class VerifyingWidget : IMsg91WidgetVerifier
    {
        public Task VerifyAsync(string accessToken, string expectedIdentifier, DateTime notBefore, CancellationToken cancellationToken = default)
        {
            if (accessToken != "valid-proof" || expectedIdentifier != "919876543210")
                throw new InvalidOperationException("Invalid widget proof");
            return Task.CompletedTask;
        }
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
        await Assert.ThrowsAsync<InvalidOperationException>(() => _auth.VerifyOtpAsync(new() { Mobile = pending.MobileNumber, Otp = "123456" }));
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
            var retained = new[] { typeof(User), typeof(Broker), typeof(CreditWallet), typeof(OtpVerification), typeof(EmailOtpRecord), typeof(PendingRegistration), typeof(WidgetOtpChallenge) };
            foreach (var entity in builder.Model.GetEntityTypes().ToArray())
                if (!retained.Contains(entity.ClrType)) builder.Ignore(entity.ClrType);
        }
    }
}
