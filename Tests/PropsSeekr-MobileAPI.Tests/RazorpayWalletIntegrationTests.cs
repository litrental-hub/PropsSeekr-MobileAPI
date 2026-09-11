using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PropSeekr.Data;
using PropSeekr.DTOs.Payment;
using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class RazorpayWalletIntegrationTests : IAsyncLifetime
{
    private const string KeySecret = "test_key_secret";
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("PROPSEEKR_TEST_DATABASE_URL");
    private AppDbContext? _db;
    private RazorpayService? _service;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, postgres => postgres.UseNetTopologySuite())
            .Options;
        _db = new AppDbContext(options);
        await CreateSchemaAsync(_db);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Razorpay:KeyId"] = "test_key_id",
            ["Razorpay:KeySecret"] = KeySecret,
            ["Razorpay:WebhookSecret"] = "test_webhook_secret"
        }).Build();
        _service = new RazorpayService(_db, new StubHttpClientFactory(), configuration, NullLogger<RazorpayService>.Instance);
    }

    public async Task DisposeAsync()
    {
        if (_db is null) return;
        await _db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
        await _db.DisposeAsync();
    }

    [PostgreSqlFact]
    public async Task VerifyPayment_CreditsWalletAndLedgerExactlyOnce()
    {
        if (_db is null || _service is null) return;
        var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var paymentId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        await SeedAsync(_db, userId, paymentId, "order_123", credits: 20);
        var request = new VerifyPaymentRequestDto
        {
            RazorpayOrderId = "order_123",
            RazorpayPaymentId = "pay_123",
            RazorpaySignature = Sign("order_123|pay_123", KeySecret)
        };

        var first = await _service.VerifyPaymentSignatureAsync(userId, request);
        var retry = await _service.VerifyPaymentSignatureAsync(userId, request);

        Assert.True(first.Success);
        Assert.True(retry.Success);
        Assert.Equal(25, first.NewBalance);
        Assert.Equal(25, retry.NewBalance);
        var wallet = await _db.CreditWallets.AsNoTracking().SingleAsync();
        Assert.Equal(5, wallet.FreeCreditsBalance);
        Assert.Equal(20, wallet.PaidCreditsBalance);
        var ledger = Assert.Single(await _db.CreditTransactions.AsNoTracking().ToListAsync());
        Assert.Equal("purchase", ledger.Type);
        Assert.Equal(20, ledger.Amount);
        Assert.Equal(25, ledger.BalanceAfter);
        Assert.Equal(paymentId.ToString("N"), ledger.ReferenceKey);
    }

    [PostgreSqlFact]
    public async Task InvalidSignature_DoesNotCreditWallet()
    {
        if (_db is null || _service is null) return;
        var userId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        await SeedAsync(_db, userId, Guid.NewGuid(), "order_invalid", credits: 10);

        var response = await _service.VerifyPaymentSignatureAsync(userId, new VerifyPaymentRequestDto
        {
            RazorpayOrderId = "order_invalid",
            RazorpayPaymentId = "pay_invalid",
            RazorpaySignature = "not-a-signature"
        });

        Assert.False(response.Success);
        Assert.Equal(5, (await _db.CreditWallets.AsNoTracking().SingleAsync()).FreeCreditsBalance);
        Assert.Empty(await _db.CreditTransactions.AsNoTracking().ToListAsync());
    }

    private static async Task SeedAsync(AppDbContext db, Guid userId, Guid paymentId, string orderId, int credits)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO brokers (brokerid, phone_number, name)
            VALUES (1, '+919111111111', 'Broker One');
            INSERT INTO "Users" ("Id", "BrokerId", "Name", "MobileNumber", "PasswordHash", "AadharNumber", "PanCard", "CreatedDate", "ModifiedDate")
            VALUES ({userId}, 1, 'Broker One', '9111111111', '', '', NOW(), NOW());
            INSERT INTO credit_wallets ("Id", broker_id, free_credits_balance, paid_credits_balance, created_at, updated_at)
            VALUES (1, 1, 5, 0, NOW(), NOW());
            INSERT INTO "PaymentTransactions" ("Id", "UserId", "RazorpayOrderId", "AmountInPaise", "Currency", "Receipt", "Status", "TierId", "CreditsAwarded", "CreatedDate", "ModifiedDate")
            VALUES ({paymentId}, {userId}, {orderId}, 560000, 'INR', 'receipt_test', 'Pending', 'CREDITS_20', {credits}, NOW(), NOW());
            """);
    }

    private static string Sign(string payload, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
    }

    private static Task CreateSchemaAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        DROP SCHEMA public CASCADE;
        CREATE SCHEMA public;

        CREATE TABLE brokers (
            brokerid integer PRIMARY KEY, phone_number text NOT NULL, name text NULL,
            response_score numeric NULL, status text NULL,
            created_at timestamptz NULL, last_active_at timestamptz NULL,
            confirmation_compliance_rate numeric NOT NULL DEFAULT 100,
            visibility_penalty_flag boolean NOT NULL DEFAULT false,
            visibility_penalty_expires_at timestamptz NULL, locality text NULL, brokerage_name text NULL
        );
        CREATE TABLE "Users" (
            "Id" uuid PRIMARY KEY, "BrokerId" integer NULL, "Name" text NOT NULL,
            "MobileNumber" text NOT NULL, "Email" text NULL, "PasswordHash" text NOT NULL,
            "AddressLine1" text NULL, "AddressLine2" text NULL, "City" text NULL, "State" text NULL,
            "Pincode" text NULL, "AadharNumber" text NOT NULL, "PanCard" text NOT NULL,
            "GSTNumber" text NULL, "ReraRegistrationNumber" text NULL, "ProfilePhotoUrl" text NULL,
            "IsMobileVerified" boolean NOT NULL DEFAULT false, "IsEmailVerified" boolean NOT NULL DEFAULT false,
            "CreatedDate" timestamptz NOT NULL, "ModifiedDate" timestamptz NOT NULL
        );
        CREATE TABLE "PaymentTransactions" (
            "Id" uuid PRIMARY KEY, "UserId" uuid NOT NULL, "RazorpayOrderId" varchar(100) NOT NULL,
            "RazorpayPaymentId" varchar(100) NULL, "RazorpaySignature" varchar(255) NULL,
            "AmountInPaise" bigint NOT NULL, "Currency" varchar(10) NOT NULL, "Receipt" varchar(100) NOT NULL,
            "Status" varchar(20) NOT NULL, "TierId" varchar(50) NOT NULL, "CreditsAwarded" integer NOT NULL,
            "Description" varchar(500) NULL, "FailureReason" text NULL,
            "CreatedDate" timestamptz NOT NULL, "ModifiedDate" timestamptz NOT NULL
        );
        CREATE TABLE credit_wallets (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, broker_id integer NOT NULL UNIQUE,
            free_credits_balance integer NOT NULL, paid_credits_balance integer NOT NULL,
            free_credits_reset_at timestamptz NULL, created_at timestamptz NOT NULL, updated_at timestamptz NOT NULL
        );
        CREATE TABLE credit_transactions (
            "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY, broker_id integer NOT NULL,
            "Type" text NOT NULL, "Amount" integer NOT NULL, balance_after integer NOT NULL,
            reference_type text NULL, reference_id bigint NULL, reference_key varchar(100) NULL,
            "Notes" text NULL, "CreatedAt" timestamptz NOT NULL
        );
        CREATE UNIQUE INDEX ix_credit_transactions_payment_reference
            ON credit_transactions (broker_id, reference_type, reference_key)
            WHERE reference_key IS NOT NULL;
        """);

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
