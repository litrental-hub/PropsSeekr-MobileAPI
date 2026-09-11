using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public sealed class WalletAccountingService(AppDbContext db) : IWalletAccountingService
{
    private const int MonthlyFreeCredits = 10;

    public Task<List<CreditWallet>> LockAsync(IEnumerable<int> brokerIds, CancellationToken cancellationToken = default)
    {
        var ids = brokerIds.Distinct().OrderBy(id => id).ToArray();
        if (ids.Length == 0) return Task.FromResult(new List<CreditWallet>());
        return db.CreditWallets
            .FromSqlInterpolated($"SELECT * FROM credit_wallets WHERE broker_id = ANY({ids}) ORDER BY broker_id FOR UPDATE")
            .ToListAsync(cancellationToken);
    }

    public Task EnsureCurrentPeriodWalletAsync(int brokerId, DateTime now, CancellationToken cancellationToken = default)
    {
        var period = WalletPeriod.For(now);
        var notes = $"Initial verified registration grant for {period.Key}";

        // Existing imported wallets are deliberately preserved for explicit
        // reconciliation; only a newly inserted wallet receives this grant.
        return db.Database.ExecuteSqlInterpolatedAsync($"""
            WITH inserted_wallet AS (
                INSERT INTO credit_wallets (
                    broker_id, free_credits_balance, paid_credits_balance,
                    free_credits_reset_at, created_at, updated_at)
                VALUES ({brokerId}, 10, 0, {period.NextStartUtc}, {now}, {now})
                ON CONFLICT (broker_id) DO NOTHING
                RETURNING broker_id, free_credits_balance + paid_credits_balance AS balance_after
            )
            INSERT INTO credit_transactions (
                broker_id, "Type", "Amount", balance_after,
                free_credits_amount, paid_credits_amount, free_balance_after, paid_balance_after, period_key,
                reference_type, reference_key, "Notes", "CreatedAt")
            SELECT broker_id, 'grant', 10, balance_after,
                   10, 0, 10, 0, {period.Key},
                   'monthly_grant', {period.Key}, {notes}, {now}
            FROM inserted_wallet
            ON CONFLICT (broker_id, reference_type, reference_key)
                WHERE reference_key IS NOT NULL DO NOTHING
            """, cancellationToken);
    }

    public async Task<bool> SettleFreeCreditsAsync(CreditWallet wallet, DateTime now, CancellationToken cancellationToken = default)
    {
        if (!wallet.FreeCreditsResetAt.HasValue || wallet.FreeCreditsResetAt > now) return false;
        var period = WalletPeriod.For(now);
        var periodKey = period.Key;
        if (await db.CreditTransactions.AnyAsync(item => item.BrokerId == wallet.BrokerId &&
            item.ReferenceType == "monthly_grant" && item.ReferenceKey == periodKey, cancellationToken))
        {
            // Repair a stale scheduling marker without issuing a second grant.
            wallet.FreeCreditsResetAt = period.NextStartUtc;
            wallet.UpdatedAt = now;
            return false;
        }

        if (wallet.FreeCreditsBalance > 0)
        {
            var expired = wallet.FreeCreditsBalance;
            wallet.FreeCreditsBalance = 0;
            db.CreditTransactions.Add(Ledger(wallet, "expiry", expired, expired, 0,
                "monthly_expiry", null, periodKey, $"Unused free credits expired before {periodKey} grant", now, periodKey));
        }

        wallet.FreeCreditsBalance = MonthlyFreeCredits;
        wallet.FreeCreditsResetAt = period.NextStartUtc;
        wallet.UpdatedAt = now;
        db.CreditTransactions.Add(Ledger(wallet, "grant", MonthlyFreeCredits, MonthlyFreeCredits, 0,
            "monthly_grant", null, periodKey, $"Monthly free-credit grant for {periodKey}", now, periodKey));
        return true;
    }

    public CreditTransaction Debit(CreditWallet wallet, int amount, string referenceType, long? referenceId, string referenceKey, string reason, DateTime now, string transactionType = "deduct")
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        if (wallet.FreeCreditsBalance + wallet.PaidCreditsBalance < amount)
            throw new InvalidOperationException("Insufficient credits.");
        var freeUsed = Math.Min(wallet.FreeCreditsBalance, amount);
        var paidUsed = amount - freeUsed;
        wallet.FreeCreditsBalance -= freeUsed;
        wallet.PaidCreditsBalance -= paidUsed;
        wallet.UpdatedAt = now;
        var entry = Ledger(wallet, transactionType, amount, freeUsed, paidUsed, referenceType, referenceId, referenceKey, reason, now, null);
        db.CreditTransactions.Add(entry);
        return entry;
    }

    public CreditTransaction CreditPaid(CreditWallet wallet, int amount, string referenceType, long? referenceId, string referenceKey, string reason, DateTime now)
    {
        if (amount <= 0) throw new ArgumentOutOfRangeException(nameof(amount));
        wallet.PaidCreditsBalance += amount;
        wallet.UpdatedAt = now;
        var entry = Ledger(wallet, "purchase", amount, 0, amount, referenceType, referenceId, referenceKey, reason, now, null);
        db.CreditTransactions.Add(entry);
        return entry;
    }

    private static CreditTransaction Ledger(CreditWallet wallet, string type, int amount, int freeAmount, int paidAmount,
        string referenceType, long? referenceId, string referenceKey, string reason, DateTime now, string? periodKey) => new()
    {
        BrokerId = wallet.BrokerId,
        Type = type,
        Amount = amount,
        BalanceAfter = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance,
        FreeCreditsAmount = freeAmount,
        PaidCreditsAmount = paidAmount,
        FreeBalanceAfter = wallet.FreeCreditsBalance,
        PaidBalanceAfter = wallet.PaidCreditsBalance,
        PeriodKey = periodKey,
        ReferenceType = referenceType,
        ReferenceId = referenceId,
        ReferenceKey = referenceKey,
        Notes = reason,
        CreatedAt = now
    };
}
