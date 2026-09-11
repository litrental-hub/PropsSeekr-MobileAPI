using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class PostgreSqlFactAttribute : FactAttribute
{
    public PostgreSqlFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PROPSEEKR_TEST_DATABASE_URL")))
        {
            Skip = "Set PROPSEEKR_TEST_DATABASE_URL to run PostgreSQL integration tests.";
        }
    }
}

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class UnlockFlowIntegrationTests : IAsyncLifetime
{
    private readonly string? _connectionString = Environment.GetEnvironmentVariable("PROPSEEKR_TEST_DATABASE_URL");
    private AppDbContext? _db;
    private UnlockService? _service;

    public async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_connectionString)) return;
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, postgres => postgres.UseNetTopologySuite())
            .Options;
        _db = new AppDbContext(options);
        await CreateSchemaAsync(_db);
        _service = new UnlockService(_db, NullLogger<UnlockService>.Instance, new WalletAccountingService(_db));
    }

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.Database.ExecuteSqlRawAsync("DROP SCHEMA public CASCADE; CREATE SCHEMA public;");
            await _db.DisposeAsync();
        }
    }

    [PostgreSqlFact]
    public async Task DualConfirmation_RevealsOnce_AndDeductsExactlyOncePerBroker()
    {
        if (_service is null || _db is null) return;
        await SeedMatchAsync(_db, matchId: 100, firstBalance: 10, secondBalance: 10);

        var first = await _service.ConfirmMatchAsync(1, Confirmation(100, 1));
        Assert.Equal("pending_confirmation", first.State);
        Assert.Equal(0, await _db.Reveals.CountAsync());
        Assert.Equal(0, await _db.CreditTransactions.CountAsync());
        var pendingNotification = Assert.Single(await _db.BrokerNotifications.ToListAsync());
        Assert.Equal(2, pendingNotification.BrokerId);
        Assert.Equal("confirm_pending", pendingNotification.Type);
        using (var payload = JsonDocument.Parse(pendingNotification.PayloadJson!))
        {
            Assert.Equal(100, payload.RootElement.GetProperty("match_id").GetInt32());
            Assert.Equal(1, payload.RootElement.GetProperty("initiator_broker_id").GetInt32());
        }

        var originalDeadline = (await _db.MatchConnectionRequests.AsNoTracking()
            .SingleAsync(item => item.MatchId == 100)).ExpiresAt;
        var originalConfirmationDeadline = (await _db.MatchConfirmations.AsNoTracking()
            .SingleAsync(item => item.MatchId == 100)).WindowExpiresAt;
        var firstRetry = await _service.ConfirmMatchAsync(1, Confirmation(100, 1));
        Assert.Equal("pending_confirmation", firstRetry.State);
        Assert.Equal(1, await _db.BrokerNotifications.CountAsync());
        Assert.Equal(originalDeadline, (await _db.MatchConnectionRequests.AsNoTracking()
            .SingleAsync(item => item.MatchId == 100)).ExpiresAt);
        Assert.Equal(originalConfirmationDeadline, (await _db.MatchConfirmations.AsNoTracking()
            .SingleAsync(item => item.MatchId == 100)).WindowExpiresAt);

        var second = await _service.ConfirmMatchAsync(2, Confirmation(100, 2));
        Assert.Equal("revealed", second.State);
        Assert.Equal(1, await _db.Reveals.CountAsync());
        Assert.Equal(2, await _db.CreditTransactions.CountAsync());
        Assert.All(await _db.CreditWallets.ToListAsync(), wallet => Assert.Equal(9, wallet.FreeCreditsBalance));
        await _db.Entry(pendingNotification).ReloadAsync();
        Assert.NotNull(pendingNotification.ReadAt);
        Assert.Equal("read", pendingNotification.ChannelStatus);
        var acceptedNotification = Assert.Single(await _db.BrokerNotifications
            .Where(notification => notification.BrokerId == 1 && notification.Type == "confirm_accepted")
            .ToListAsync());
        Assert.Equal(pendingNotification.ConnectionRequestId, acceptedNotification.ConnectionRequestId);

        var retry = await _service.UnlockMatchAsync(1, new UnlockPropertyRequestDto { MatchId = 100 });
        Assert.True(retry.Success);
        Assert.Equal("Broker Two", retry.UnlockedContact?.OwnerName);
        Assert.Equal("+919222222222", retry.UnlockedContact?.OwnerMobile);
        Assert.Equal(9, retry.CreditsRemaining);
        Assert.Equal(1, await _db.Reveals.CountAsync());
        Assert.Equal(2, await _db.CreditTransactions.CountAsync());
    }

    [PostgreSqlFact]
    public async Task InsufficientCounterpartyCredit_WritesNoRevealOrLedgerRows()
    {
        if (_service is null || _db is null) return;
        await SeedMatchAsync(_db, matchId: 200, firstBalance: 10, secondBalance: 0);

        await _service.ConfirmMatchAsync(1, Confirmation(200, 1));
        var response = await _service.ConfirmMatchAsync(2, Confirmation(200, 2));

        Assert.Equal("confirmed", response.State);
        Assert.Contains("credit", response.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await _db.Reveals.CountAsync());
        Assert.Equal(0, await _db.CreditTransactions.CountAsync());
        var wallets = await _db.CreditWallets.OrderBy(w => w.BrokerId).ToListAsync();
        Assert.Equal(10, wallets[0].FreeCreditsBalance);
        Assert.Equal(0, wallets[1].FreeCreditsBalance);
    }

    [PostgreSqlFact]
    public async Task NonPartyCannotConfirmOrReveal()
    {
        if (_service is null || _db is null) return;
        await SeedMatchAsync(_db, matchId: 300, firstBalance: 10, secondBalance: 10);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.ConfirmMatchAsync(99, Confirmation(300, 99)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _service.UnlockMatchAsync(99, new UnlockPropertyRequestDto { MatchId = 300 }));
    }

    [PostgreSqlFact]
    public async Task ConcurrentRevealRetries_CreateOneRevealAndTwoLedgerRows()
    {
        if (_service is null || _db is null || string.IsNullOrWhiteSpace(_connectionString)) return;
        await SeedMatchAsync(_db, matchId: 400, firstBalance: 10, secondBalance: 10);
        await _db.Database.ExecuteSqlRawAsync("""
            UPDATE matches SET state = 'confirmed', status = 'CONFIRMED' WHERE matchid = 400;
            INSERT INTO match_connection_requests
                (match_id, requesting_broker_id, receiving_broker_id, listing_version, requirement_version, status, delivery_channel, delivery_status, created_at, expires_at)
            VALUES (400, 1, 2, 1, 1, 'pending', 'in_app', 'delivered', NOW(), NOW() + interval '4 hours');
            INSERT INTO match_confirmations
                (connection_request_id, match_id, broker_id, availability_confirmed, price_valid, price_negotiable, ready_to_connect, confirmed_at, window_expires_at, created_at)
            VALUES ((SELECT max(request_id) FROM match_connection_requests WHERE match_id = 400), 400, 1, true, true, false, true, NOW(), NOW() + interval '4 hours', NOW()),
                   ((SELECT max(request_id) FROM match_connection_requests WHERE match_id = 400), 400, 2, true, true, false, true, NOW(), NOW() + interval '4 hours', NOW());
            """);

        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_connectionString, postgres => postgres.UseNetTopologySuite())
            .Options;
        await using var secondDb = new AppDbContext(options);
        var secondService = new UnlockService(secondDb, NullLogger<UnlockService>.Instance, new WalletAccountingService(secondDb));

        var responses = await Task.WhenAll(
            _service.UnlockMatchAsync(1, new UnlockPropertyRequestDto { MatchId = 400 }),
            secondService.UnlockMatchAsync(2, new UnlockPropertyRequestDto { MatchId = 400 }));

        Assert.All(responses, response => Assert.True(response.Success));
        Assert.Equal(1, await _db.Reveals.CountAsync());
        Assert.Equal(2, await _db.CreditTransactions.CountAsync());
        Assert.All(await _db.CreditWallets.ToListAsync(), wallet => Assert.Equal(9, wallet.FreeCreditsBalance));
    }

    [PostgreSqlFact]
    public async Task ExpiredPendingRequest_ResetsConsentWithoutChargingOrDeletingEvidence()
    {
        if (_service is null || _db is null) return;
        await SeedMatchAsync(_db, matchId: 500, firstBalance: 10, secondBalance: 10);
        await _service.ConfirmMatchAsync(1, Confirmation(500, 1));
        await _db.Database.ExecuteSqlRawAsync("""
            UPDATE match_connection_requests SET expires_at = NOW() - interval '1 minute' WHERE match_id = 500;
            UPDATE match_confirmations SET window_expires_at = NOW() - interval '1 minute' WHERE match_id = 500;
            """);

        Assert.Equal(1, await _service.ExpirePendingRequestsAsync());
        Assert.Equal("matched", (await _db.Matches.AsNoTracking().SingleAsync(item => item.Id == 500)).State);
        Assert.Equal("expired", (await _db.MatchConnectionRequests.AsNoTracking().SingleAsync(item => item.MatchId == 500)).Status);
        var preservedEvidence = await _db.MatchConfirmations.AsNoTracking().SingleAsync(item => item.MatchId == 500);
        Assert.Null(preservedEvidence.ConfirmedAt);
        Assert.Empty(await _db.CreditTransactions.AsNoTracking().ToListAsync());
        Assert.Empty(await _db.Reveals.AsNoTracking().ToListAsync());
        Assert.Single(await _db.BrokerNotifications.AsNoTracking().Where(item => item.Type == "confirm_expired").ToListAsync());
    }

    private static MatchConfirmationRequestDto Confirmation(int matchId, int brokerId) => new()
    {
        MatchId = matchId,
        BrokerId = brokerId,
        AvailabilityConfirmed = true,
        PriceValid = true,
        PriceNegotiable = false,
        ReadyToConnect = true
    };

    private static async Task SeedMatchAsync(AppDbContext db, int matchId, int firstBalance, int secondBalance)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO brokers (brokerid, phone_number, name, confirmation_compliance_rate, visibility_penalty_flag)
            VALUES (1, '+919111111111', 'Broker One', 100, false),
                   (2, '+919222222222', 'Broker Two', 100, false);
            INSERT INTO "Users" ("Id", "BrokerId", "MobileNumber", "Email")
            VALUES ('11111111-1111-1111-1111-111111111111', 1, '9111111111', 'one@example.test'),
                   ('22222222-2222-2222-2222-222222222222', 2, '9222222222', 'two@example.test');
            INSERT INTO matches (matchid, listing_id, requirement_id, listing_broker_id, requirement_broker_id, match_score, status, state, created_at)
            VALUES ({matchId}, 10, 20, 1, 2, 95, 'matched', 'matched', NOW());
            INSERT INTO listings (listingid, broker_id, content_version, embedding_version, embedding_status, status, isavailable)
            VALUES (10, 1, 1, 1, 'completed', 'active', true);
            INSERT INTO requirements (requirementid, broker_id, content_version, embedding_version, embedding_status, status, isavailable)
            VALUES (20, 2, 1, 1, 'completed', 'active', true);
            INSERT INTO credit_wallets ("Id", broker_id, free_credits_balance, paid_credits_balance, created_at, updated_at)
            VALUES (1, 1, {firstBalance}, 0, NOW(), NOW()),
                   (2, 2, {secondBalance}, 0, NOW(), NOW());
            """);
    }

    private static Task CreateSchemaAsync(AppDbContext db) => db.Database.ExecuteSqlRawAsync("""
        DROP SCHEMA public CASCADE;
        CREATE SCHEMA public;

        CREATE TABLE brokers (
            brokerid integer PRIMARY KEY,
            phone_number text NOT NULL,
            name text NULL,
            response_score numeric NULL,
            status text NULL,
            created_at timestamptz NULL,
            last_active_at timestamptz NULL,
            confirmation_compliance_rate numeric NOT NULL DEFAULT 100,
            visibility_penalty_flag boolean NOT NULL DEFAULT false,
            visibility_penalty_expires_at timestamptz NULL,
            locality text NULL,
            brokerage_name text NULL
        );

        CREATE TABLE "Users" (
            "Id" uuid PRIMARY KEY,
            "BrokerId" integer NULL,
            "MobileNumber" text NOT NULL,
            "Email" text NULL
        );

        CREATE TABLE matches (
            matchid integer PRIMARY KEY,
            listing_id integer NOT NULL,
            requirement_id integer NOT NULL,
            listing_broker_id integer NOT NULL,
            requirement_broker_id integer NOT NULL,
            match_score numeric NULL,
            status text NULL,
            state text NOT NULL DEFAULT 'matched',
            created_at timestamptz NULL,
            status_updated_at timestamptz NULL,
            ai_status text NULL,
            ai_confidence_pct numeric NULL,
            ai_reasoning text NULL,
            ai_flags jsonb NULL,
            ai_validated_at timestamptz NULL
        );

        CREATE TABLE listings (
            listingid integer PRIMARY KEY,
            broker_id integer NOT NULL,
            content_version integer NOT NULL DEFAULT 1,
            embedding_version integer NULL,
            embedding_status varchar(20) NOT NULL DEFAULT 'queued',
            status text NULL,
            isavailable boolean NOT NULL DEFAULT true,
            expires_at timestamptz NULL,
            last_confirmed_at timestamptz NULL,
            freshness_updated_at timestamptz NULL,
            freshness_score integer NULL,
            freshness_category varchar(50) NULL
        );

        CREATE TABLE requirements (
            requirementid integer PRIMARY KEY,
            broker_id integer NOT NULL,
            content_version integer NOT NULL DEFAULT 1,
            embedding_version integer NULL,
            embedding_status varchar(20) NOT NULL DEFAULT 'queued',
            status text NULL,
            isavailable boolean NOT NULL DEFAULT true,
            expires_at timestamptz NULL,
            last_confirmed_at timestamptz NULL,
            freshness_updated_at timestamptz NULL,
            freshness_score integer NULL,
            freshness_category varchar(50) NULL
        );

        CREATE TABLE match_confirmations (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            connection_request_id bigint NOT NULL,
            match_id integer NOT NULL,
            broker_id integer NOT NULL,
            availability_confirmed boolean NULL,
            price_valid boolean NULL,
            price_negotiable boolean NULL,
            ready_to_connect boolean NULL,
            availability_date timestamptz NULL,
            confirmed_at timestamptz NULL,
            window_expires_at timestamptz NULL,
            created_at timestamptz NOT NULL,
            UNIQUE (connection_request_id, broker_id)
        );

        CREATE TABLE match_connection_requests (
            request_id bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            match_id integer NOT NULL,
            requesting_broker_id integer NOT NULL,
            receiving_broker_id integer NOT NULL,
            listing_version integer NOT NULL DEFAULT 1,
            requirement_version integer NOT NULL DEFAULT 1,
            status varchar(30) NOT NULL,
            delivery_channel varchar(20) NOT NULL,
            delivery_status varchar(30) NOT NULL,
            rejection_reason_code varchar(50) NULL,
            rejection_reason_text text NULL,
            created_at timestamptz NOT NULL,
            responded_at timestamptz NULL,
            expires_at timestamptz NOT NULL
        );

        CREATE TABLE reveals (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            match_id integer NOT NULL UNIQUE,
            connection_request_id bigint NULL,
            revealed_at timestamptz NOT NULL
        );

        CREATE TABLE credit_wallets (
            "Id" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            broker_id integer NOT NULL UNIQUE,
            free_credits_balance integer NOT NULL,
            paid_credits_balance integer NOT NULL,
            free_credits_reset_at timestamptz NULL,
            created_at timestamptz NOT NULL,
            updated_at timestamptz NOT NULL
        );

        CREATE TABLE credit_transactions (
            "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            broker_id integer NOT NULL,
            "Type" text NOT NULL,
            "Amount" integer NOT NULL,
            balance_after integer NOT NULL,
            reference_type text NULL,
            reference_id bigint NULL,
            reference_key varchar(100) NULL,
            "Notes" text NULL,
            "CreatedAt" timestamptz NOT NULL
        );

        CREATE UNIQUE INDEX ix_credit_transactions_payment_reference
            ON credit_transactions (broker_id, reference_type, reference_key)
            WHERE reference_key IS NOT NULL;

        CREATE TABLE notifications (
            "Id" bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            broker_id integer NOT NULL,
            connection_request_id bigint NULL,
            "Type" varchar(50) NOT NULL,
            "Channel" varchar(20) NOT NULL,
            payload jsonb NULL,
            channel_status varchar(20) NOT NULL,
            read_at timestamptz NULL,
            created_at timestamptz NOT NULL
        );
        """);
}
