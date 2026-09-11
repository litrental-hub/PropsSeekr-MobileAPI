using PropSeekr.Models;

namespace PropSeekr.Services.Interfaces;

public interface IWalletAccountingService
{
    Task<List<CreditWallet>> LockAsync(IEnumerable<int> brokerIds, CancellationToken cancellationToken = default);
    Task EnsureCurrentPeriodWalletAsync(int brokerId, DateTime now, CancellationToken cancellationToken = default);
    Task<bool> SettleFreeCreditsAsync(CreditWallet wallet, DateTime now, CancellationToken cancellationToken = default);
    CreditTransaction Debit(CreditWallet wallet, int amount, string referenceType, long? referenceId, string referenceKey, string reason, DateTime now, string transactionType = "deduct");
    CreditTransaction CreditPaid(CreditWallet wallet, int amount, string referenceType, long? referenceId, string referenceKey, string reason, DateTime now);
}
