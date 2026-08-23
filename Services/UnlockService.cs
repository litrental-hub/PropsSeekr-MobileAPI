using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class UnlockService : IUnlockService
{
    private const int CreditsPerReveal = 1;
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromHours(4);
    private readonly AppDbContext _dbContext;
    private readonly ILogger<UnlockService> _logger;

    public UnlockService(AppDbContext dbContext, ILogger<UnlockService> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<MatchConfirmationResponseDto> ConfirmMatchAsync(Guid _, MatchConfirmationRequestDto request)
    {
        var match = await _dbContext.Matches.SingleOrDefaultAsync(m => m.Id == request.MatchId)
            ?? throw new KeyNotFoundException("Match not found.");
        if (request.BrokerId != match.ListingBrokerId && request.BrokerId != match.RequirementBrokerId)
            throw new UnauthorizedAccessException("Broker is not a party to this match.");
        if (await _dbContext.Reveals.AnyAsync(r => r.MatchId == match.Id))
            return Response(match, "Contacts have already been revealed.", null);

        var now = DateTime.UtcNow;
        var confirmation = await _dbContext.MatchConfirmations
            .SingleOrDefaultAsync(c => c.MatchId == match.Id && c.BrokerId == request.BrokerId);
        if (confirmation is null)
        {
            confirmation = new MatchConfirmation { MatchId = match.Id, BrokerId = request.BrokerId, CreatedAt = now };
            _dbContext.MatchConfirmations.Add(confirmation);
        }
        confirmation.AvailabilityConfirmed = request.AvailabilityConfirmed;
        confirmation.PriceValid = request.PriceValid;
        confirmation.PriceNegotiable = request.PriceNegotiable;
        confirmation.ReadyToConnect = request.ReadyToConnect;
        confirmation.ConfirmedAt = now;
        confirmation.WindowExpiresAt = now.Add(ConfirmationWindow);
        match.State = "pending_confirmation";
        match.StatusUpdatedAt = now;
        await _dbContext.SaveChangesAsync();

        var parties = new[] { match.ListingBrokerId, match.RequirementBrokerId };
        var confirmations = await _dbContext.MatchConfirmations
            .Where(c => c.MatchId == match.Id && parties.Contains(c.BrokerId))
            .ToListAsync();
        var bothConfirmed = confirmations.Count == 2 && confirmations.All(c => c.ConfirmedAt is not null && c.WindowExpiresAt > now);
        if (!bothConfirmed)
            return Response(match, "Confirmation recorded. Waiting for counterparty.", confirmation.WindowExpiresAt);

        match.State = "confirmed";
        match.StatusUpdatedAt = now;
        await _dbContext.SaveChangesAsync();
        // Reveal is automatic after the second confirmation, as specified. It is idempotent by reveals.match_id.
        var result = await RevealAsync(match.Id);
        return Response(match, result.Success ? "Both brokers confirmed; contacts revealed." : result.Message, confirmation.WindowExpiresAt);
    }

    public Task<UnlockPropertyResponseDto> UnlockMatchAsync(Guid _, UnlockPropertyRequestDto request) => RevealAsync(request.MatchId);
    public Task<UnlockPropertyResponseDto> UnlockMatchAsync(int brokerId, UnlockPropertyRequestDto request) => RevealAsync(request.MatchId, brokerId);

    private async Task<UnlockPropertyResponseDto> RevealAsync(int matchId, int? callerBrokerId = null)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync();
        var match = await _dbContext.Matches.SingleOrDefaultAsync(m => m.Id == matchId)
            ?? throw new KeyNotFoundException("Match not found.");
        if (callerBrokerId.HasValue && callerBrokerId != match.ListingBrokerId && callerBrokerId != match.RequirementBrokerId)
            throw new UnauthorizedAccessException("Broker is not a party to this match.");
        var existing = await _dbContext.Reveals.SingleOrDefaultAsync(r => r.MatchId == matchId);
        if (existing is not null)
        {
            await transaction.CommitAsync();
            return new UnlockPropertyResponseDto { Success = true, Message = "Contact details already unlocked." };
        }
        if (!string.Equals(match.State, "confirmed", StringComparison.OrdinalIgnoreCase))
            return new UnlockPropertyResponseDto { Success = false, Message = "Both brokers must confirm before contacts are revealed." };

        var brokerIds = new[] { match.ListingBrokerId, match.RequirementBrokerId };
        var wallets = await _dbContext.CreditWallets.Where(w => brokerIds.Contains(w.BrokerId)).ToListAsync();
        if (wallets.Count != 2 || wallets.Any(w => w.FreeCreditsBalance + w.PaidCreditsBalance < CreditsPerReveal))
        {
            await transaction.RollbackAsync();
            return new UnlockPropertyResponseDto { Success = false, Message = "Both brokers need at least one credit to reveal this match." };
        }

        var revealedAt = DateTime.UtcNow;
        var reveal = new Reveal { MatchId = matchId, RevealedAt = revealedAt };
        _dbContext.Reveals.Add(reveal);
        foreach (var wallet in wallets)
        {
            if (wallet.FreeCreditsBalance > 0) wallet.FreeCreditsBalance--;
            else wallet.PaidCreditsBalance--;
            wallet.UpdatedAt = revealedAt;
            _dbContext.CreditTransactions.Add(new CreditTransaction
            {
                BrokerId = wallet.BrokerId, Type = "deduct", Amount = CreditsPerReveal,
                BalanceAfter = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance,
                ReferenceType = "reveal", Notes = $"Reveal for match {matchId}", CreatedAt = revealedAt
            });
        }
        await _dbContext.SaveChangesAsync();
        foreach (var ledger in _dbContext.ChangeTracker.Entries<CreditTransaction>().Select(e => e.Entity).Where(e => e.ReferenceType == "reveal" && e.ReferenceId is null)) ledger.ReferenceId = reveal.Id;
        await _dbContext.SaveChangesAsync();
        await transaction.CommitAsync();
        _logger.LogInformation("Revealed match {MatchId} and deducted one credit from each broker.", matchId);
        return new UnlockPropertyResponseDto { Success = true, Message = "Contacts revealed successfully.", CreditsRemaining = wallets.Min(w => w.FreeCreditsBalance + w.PaidCreditsBalance) };
    }

    public Task<bool> IsMatchRevealedAsync(int matchId, Guid _) => _dbContext.Reveals.AnyAsync(r => r.MatchId == matchId);
    public async Task<CreditWallet> GetWalletAsync(Guid userId) => throw new NotSupportedException("Wallet access requires a broker id in the broker schema.");
    public Task InitializeWalletAsync(Guid _, int __ = 10) => Task.CompletedTask;
    private static MatchConfirmationResponseDto Response(Match match, string message, DateTime? expiry) => new() { Success = true, Message = message, MatchId = match.Id, State = match.State, WindowExpiresAt = expiry, CreditsRequired = CreditsPerReveal };
}
