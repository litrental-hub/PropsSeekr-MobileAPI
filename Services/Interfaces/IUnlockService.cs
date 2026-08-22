using PropSeekr.DTOs.Matches;
using PropSeekr.Models;

namespace PropSeekr.Services.Interfaces;

public interface IUnlockService
{
    /// <summary>
    /// Confirm match details before reveal (dual handshake).
    /// Both brokers must confirm within window period.
    /// </summary>
    Task<MatchConfirmationResponseDto> ConfirmMatchAsync(Guid userId, MatchConfirmationRequestDto request);

    /// <summary>
    /// Unlock/reveal contact details for confirmed match.
    /// Deducts credits and creates reveal record.
    /// </summary>
    Task<UnlockPropertyResponseDto> UnlockMatchAsync(Guid userId, UnlockPropertyRequestDto request);

    /// <summary>
    /// Check if a match has been revealed.
    /// </summary>
    Task<bool> IsMatchRevealedAsync(int matchId, Guid userId);

    /// <summary>
    /// Get user's credit wallet.
    /// </summary>
    Task<CreditWallet> GetWalletAsync(Guid userId);

    /// <summary>
    /// Initialize credit wallet for new user.
    /// </summary>
    Task InitializeWalletAsync(Guid userId, int freeCredits = 5);
}
