using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services;
using Xunit;

namespace PropSeekr.Tests;

public sealed class WalletAccountingServiceTests
{
    [Fact]
    public async Task Settlement_UsesIndiaMonthBoundary_PreservesPaidCredits_AndIsIdempotent()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var wallet = new CreditWallet
        {
            BrokerId = 42,
            FreeCreditsBalance = 3,
            PaidCreditsBalance = 7,
            FreeCreditsResetAt = new DateTime(2026, 9, 30, 18, 30, 0, DateTimeKind.Utc),
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        db.CreditWallets.Add(wallet);
        await db.SaveChangesAsync();
        var service = new WalletAccountingService(db);

        var beforeBoundary = await service.SettleFreeCreditsAsync(
            wallet, new DateTime(2026, 9, 30, 18, 29, 59, DateTimeKind.Utc));
        Assert.False(beforeBoundary);
        Assert.Equal(3, wallet.FreeCreditsBalance);

        var settled = await service.SettleFreeCreditsAsync(
            wallet, new DateTime(2026, 9, 30, 18, 30, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        Assert.True(settled);
        Assert.Equal(10, wallet.FreeCreditsBalance);
        Assert.Equal(7, wallet.PaidCreditsBalance);
        Assert.Equal(new DateTime(2026, 10, 31, 18, 30, 0, DateTimeKind.Utc), wallet.FreeCreditsResetAt);
        var entries = await db.CreditTransactions.OrderBy(item => item.Id).ToListAsync();
        Assert.Collection(entries,
            expiry =>
            {
                Assert.Equal("expiry", expiry.Type);
                Assert.Equal(3, expiry.Amount);
                Assert.Equal("2026-10", expiry.PeriodKey);
                Assert.Equal(7, expiry.BalanceAfter);
            },
            grant =>
            {
                Assert.Equal("grant", grant.Type);
                Assert.Equal(10, grant.Amount);
                Assert.Equal("2026-10", grant.PeriodKey);
                Assert.Equal(17, grant.BalanceAfter);
            });

        wallet.FreeCreditsResetAt = new DateTime(2026, 9, 30, 18, 30, 0, DateTimeKind.Utc);
        var replay = await service.SettleFreeCreditsAsync(
            wallet, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        await db.SaveChangesAsync();

        Assert.False(replay);
        Assert.Equal(2, await db.CreditTransactions.CountAsync());
        Assert.Equal(new DateTime(2026, 10, 31, 18, 30, 0, DateTimeKind.Utc), wallet.FreeCreditsResetAt);
    }

    [Fact]
    public async Task Debit_UsesFreeBeforePaid_AndRecordsAllocation()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new AppDbContext(options);
        var wallet = new CreditWallet { BrokerId = 7, FreeCreditsBalance = 1, PaidCreditsBalance = 4 };
        var service = new WalletAccountingService(db);

        var entry = service.Debit(wallet, 3, "adjustment_debit", null, "support-123",
            "Approved support adjustment", DateTime.UtcNow, "adjustment_debit");

        Assert.Equal(0, wallet.FreeCreditsBalance);
        Assert.Equal(2, wallet.PaidCreditsBalance);
        Assert.Equal(1, entry.FreeCreditsAmount);
        Assert.Equal(2, entry.PaidCreditsAmount);
        Assert.Equal(2, entry.BalanceAfter);
        Assert.Equal("adjustment_debit", entry.Type);
    }
}
