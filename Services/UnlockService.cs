using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

/// <summary>
/// Canonical dual-confirmation and contact-reveal implementation. Every mutation
/// locks the match row so retries and concurrent calls cannot double-charge.
/// </summary>
public sealed class UnlockService : IUnlockService
{
    private const int CreditsPerReveal = 1;
    private static readonly TimeSpan ConfirmationWindow = TimeSpan.FromHours(4);
    private static readonly HashSet<string> AllowedRejectionReasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "PROPERTY_UNAVAILABLE",
        "PRICE_CHANGED",
        "CLIENT_REQUIREMENT_CLOSED",
        "ALREADY_CLOSED",
        "INCORRECT_MATCH",
        "OTHER"
    };
    private readonly AppDbContext _db;
    private readonly ILogger<UnlockService> _logger;

    public UnlockService(AppDbContext db, ILogger<UnlockService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<MatchConfirmationResponseDto> ConfirmMatchAsync(
        int brokerId,
        MatchConfirmationRequestDto request)
    {
        if (!request.AvailabilityConfirmed || !request.PriceValid || !request.ReadyToConnect)
        {
            throw new ArgumentException("Availability, price validity, and readiness to connect must be confirmed.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var match = await LockMatchAsync(request.MatchId)
            ?? throw new KeyNotFoundException("Match not found.");
        EnsureMatchParty(match, brokerId);

        if (await _db.Reveals.AnyAsync(r => r.MatchId == match.Id))
        {
            var unlocked = await BuildSuccessResponseAsync(match, brokerId, "Contact details already unlocked.");
            var completedRequest = await LatestConnectionRequestAsync(match.Id);
            await transaction.CommitAsync();
            return ConfirmationResponse(match, unlocked.Message, null, completedRequest, reveal: unlocked);
        }

        var now = DateTime.UtcNow;
        var connectionRequest = await _db.MatchConnectionRequests
            .Where(item => item.MatchId == match.Id &&
                           (item.Status == ConnectionRequestStatuses.Pending ||
                            item.Status == ConnectionRequestStatuses.CreditRequired))
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync();

        if (connectionRequest?.Status == ConnectionRequestStatuses.CreditRequired)
        {
            await transaction.CommitAsync();
            var creditRetry = await RevealAsync(match.Id, brokerId);
            if (!creditRetry.Success)
            {
                await MarkConnectionRequestCreditRequiredAsync(connectionRequest.Id);
            }
            return ConfirmationResponse(match, creditRetry.Message, null, connectionRequest, reveal: creditRetry);
        }

        if (connectionRequest is not null && connectionRequest.ExpiresAt <= now)
        {
            connectionRequest.Status = ConnectionRequestStatuses.Expired;
            connectionRequest.RespondedAt = now;
            connectionRequest = null;
        }

        if (connectionRequest is null)
        {
            if (await GetWalletBalanceAsync(brokerId) < CreditsPerReveal)
            {
                await transaction.RollbackAsync();
                return ConfirmationFailure(match, "insufficient_credits", "You need at least one token to request this connection.");
            }

            await ResetConfirmationsAsync(match.Id);
            var receivingBrokerId = CounterpartyBrokerId(match, brokerId);
            var counterpartyRegistered = await IsRegisteredBrokerAsync(receivingBrokerId);
            connectionRequest = new MatchConnectionRequest
            {
                MatchId = match.Id,
                RequestingBrokerId = brokerId,
                ReceivingBrokerId = receivingBrokerId,
                Status = ConnectionRequestStatuses.Pending,
                DeliveryChannel = counterpartyRegistered ? "in_app" : "whatsapp",
                DeliveryStatus = counterpartyRegistered ? "created" : "planned",
                CreatedAt = now,
                ExpiresAt = now.Add(ConfirmationWindow)
            };
            _db.MatchConnectionRequests.Add(connectionRequest);
            await UpsertConfirmationAsync(match.Id, brokerId, request, now);
            match.State = "pending_confirmation";
            match.StatusUpdatedAt = now;
            await _db.SaveChangesAsync();

            if (counterpartyRegistered)
            {
                AddCounterpartyConfirmationNotification(match, connectionRequest, now);
                await _db.SaveChangesAsync();
            }

            await transaction.CommitAsync();
            var message = counterpartyRegistered
                ? "Unlock request sent. Waiting for the other broker to accept."
                : "This broker is not registered on PropSeekr. WhatsApp notification delivery is planned but not active yet.";
            return ConfirmationResponse(match, message, connectionRequest.ExpiresAt, connectionRequest, counterpartyRegistered);
        }

        if (brokerId == connectionRequest.RequestingBrokerId)
        {
            await UpsertConfirmationAsync(match.Id, brokerId, request, now);
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return ConfirmationResponse(
                match,
                "Unlock request is already waiting for the other broker.",
                connectionRequest.ExpiresAt,
                connectionRequest,
                await IsRegisteredBrokerAsync(connectionRequest.ReceivingBrokerId));
        }

        if (brokerId != connectionRequest.ReceivingBrokerId)
        {
            throw new UnauthorizedAccessException("Only the receiving broker can accept this connection request.");
        }

        await UpsertConfirmationAsync(match.Id, brokerId, request, now);
        match.State = "pending_confirmation";
        match.StatusUpdatedAt = now;
        await _db.SaveChangesAsync();

        var parties = new[] { match.ListingBrokerId, match.RequirementBrokerId };
        var confirmations = await _db.MatchConfirmations
            .Where(c => c.MatchId == match.Id && parties.Contains(c.BrokerId))
            .ToListAsync();
        var validConfirmations = confirmations
            .Where(c => c.ConfirmedAt.HasValue &&
                        c.WindowExpiresAt > now &&
                        c.AvailabilityConfirmed == true &&
                        c.PriceValid == true &&
                        c.ReadyToConnect == true)
            .ToList();
        var bothConfirmed = parties.Distinct().Count() == 2 &&
                            validConfirmations.Select(c => c.BrokerId).Distinct().Count() == 2;
        var activeExpiry = validConfirmations.Select(c => c.WindowExpiresAt).Min();

        if (!bothConfirmed)
        {
            await transaction.CommitAsync();
            return ConfirmationResponse(
                match,
                "Confirmation recorded. Waiting for counterparty.",
                activeExpiry,
                connectionRequest,
                true);
        }

        match.State = "confirmed";
        match.StatusUpdatedAt = now;
        await MarkIncomingConfirmationNotificationsReadAsync(connectionRequest.Id, brokerId, now);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();

        // The reveal operation owns a separate atomic transaction for the reveal,
        // both wallet deductions, and both ledger rows.
        var reveal = await RevealAsync(match.Id, brokerId);
        if (!reveal.Success)
        {
            await MarkConnectionRequestCreditRequiredAsync(connectionRequest.Id);
        }
        return ConfirmationResponse(
            match,
            reveal.Success ? "Both brokers confirmed; contacts revealed." : reveal.Message,
            activeExpiry,
            connectionRequest,
            reveal: reveal);
    }

    public async Task<MatchRejectionResponseDto> RejectMatchAsync(
        int brokerId,
        MatchRejectionRequestDto request)
    {
        var normalizedReason = request.ReasonCode.Trim().ToUpperInvariant();
        if (!AllowedRejectionReasons.Contains(normalizedReason))
        {
            throw new ArgumentException("A valid rejection reason is required.");
        }
        if (normalizedReason == "OTHER" && string.IsNullOrWhiteSpace(request.ReasonText))
        {
            throw new ArgumentException("Please add rejection details when selecting Other.");
        }

        await using var transaction = await _db.Database.BeginTransactionAsync();
        var match = await LockMatchAsync(request.MatchId)
            ?? throw new KeyNotFoundException("Match not found.");
        EnsureMatchParty(match, brokerId);

        var connectionRequest = await _db.MatchConnectionRequests
            .Where(item => item.MatchId == match.Id &&
                           (!request.ConnectionRequestId.HasValue || item.Id == request.ConnectionRequestId.Value))
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync()
            ?? throw new KeyNotFoundException("Connection request not found.");

        if (connectionRequest.Status == ConnectionRequestStatuses.Rejected &&
            connectionRequest.ReceivingBrokerId == brokerId)
        {
            await transaction.CommitAsync();
            return RejectionResponse(match.Id, connectionRequest);
        }
        if (connectionRequest.Status != ConnectionRequestStatuses.Pending)
        {
            throw new InvalidOperationException($"A {connectionRequest.Status} request cannot be rejected.");
        }
        if (connectionRequest.ReceivingBrokerId != brokerId)
        {
            throw new UnauthorizedAccessException("Only the receiving broker can reject this connection request.");
        }

        var now = DateTime.UtcNow;
        connectionRequest.Status = ConnectionRequestStatuses.Rejected;
        connectionRequest.RejectionReasonCode = normalizedReason;
        connectionRequest.RejectionReasonText = request.ReasonText?.Trim();
        connectionRequest.RespondedAt = now;
        match.State = "matched";
        match.StatusUpdatedAt = now;
        await ResetConfirmationsAsync(match.Id);
        await MarkIncomingConfirmationNotificationsReadAsync(connectionRequest.Id, brokerId, now);
        await AddRequestOutcomeNotificationAsync(
            match,
            connectionRequest,
            "confirm_rejected",
            connectionRequest.RequestingBrokerId,
            normalizedReason,
            connectionRequest.RejectionReasonText,
            now);
        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return RejectionResponse(match.Id, connectionRequest);
    }

    public Task<UnlockPropertyResponseDto> UnlockMatchAsync(int brokerId, UnlockPropertyRequestDto request) =>
        RevealAsync(request.MatchId, brokerId);

    private void AddCounterpartyConfirmationNotification(
        Match match,
        MatchConnectionRequest connectionRequest,
        DateTime createdAt)
    {
        _db.BrokerNotifications.Add(new BrokerNotification
        {
            BrokerId = connectionRequest.ReceivingBrokerId,
            ConnectionRequestId = connectionRequest.Id,
            Type = "confirm_pending",
            Channel = "in_app",
            PayloadJson = ConfirmationNotificationPayload(match, connectionRequest),
            ChannelStatus = "pending",
            CreatedAt = createdAt
        });
    }

    private async Task MarkIncomingConfirmationNotificationsReadAsync(
        long connectionRequestId,
        int confirmingBrokerId,
        DateTime readAt)
    {
        var notifications = await _db.BrokerNotifications
            .Where(notification =>
                notification.BrokerId == confirmingBrokerId &&
                notification.Type == "confirm_pending" &&
                notification.ConnectionRequestId == connectionRequestId &&
                notification.ReadAt == null)
            .ToListAsync();

        foreach (var notification in notifications)
        {
            notification.ReadAt = readAt;
            notification.ChannelStatus = "read";
        }
    }

    private static int CounterpartyBrokerId(Match match, int brokerId) =>
        match.ListingBrokerId == brokerId ? match.RequirementBrokerId : match.ListingBrokerId;

    private static string ConfirmationNotificationPayload(
        Match match,
        MatchConnectionRequest connectionRequest) =>
        JsonSerializer.Serialize(new
        {
            match_id = match.Id,
            request_id = connectionRequest.Id,
            initiator_broker_id = connectionRequest.RequestingBrokerId,
            role = match.ListingBrokerId == connectionRequest.ReceivingBrokerId ? "listing" : "requirement"
        });

    private async Task UpsertConfirmationAsync(
        int matchId,
        int brokerId,
        MatchConfirmationRequestDto request,
        DateTime confirmedAt)
    {
        var confirmation = await _db.MatchConfirmations
            .SingleOrDefaultAsync(item => item.MatchId == matchId && item.BrokerId == brokerId);
        if (confirmation is null)
        {
            confirmation = new MatchConfirmation
            {
                MatchId = matchId,
                BrokerId = brokerId,
                CreatedAt = confirmedAt
            };
            _db.MatchConfirmations.Add(confirmation);
        }

        confirmation.AvailabilityConfirmed = request.AvailabilityConfirmed;
        confirmation.PriceValid = request.PriceValid;
        confirmation.PriceNegotiable = request.PriceNegotiable;
        confirmation.ReadyToConnect = request.ReadyToConnect;
        confirmation.ConfirmedAt = confirmedAt;
        confirmation.WindowExpiresAt = confirmedAt.Add(ConfirmationWindow);
    }

    private async Task ResetConfirmationsAsync(int matchId)
    {
        var confirmations = await _db.MatchConfirmations
            .Where(item => item.MatchId == matchId)
            .ToListAsync();
        foreach (var confirmation in confirmations)
        {
            confirmation.ConfirmedAt = null;
            confirmation.WindowExpiresAt = null;
            confirmation.AvailabilityConfirmed = null;
            confirmation.PriceValid = null;
            confirmation.PriceNegotiable = null;
            confirmation.ReadyToConnect = null;
        }
    }

    private Task<bool> IsRegisteredBrokerAsync(int brokerId) =>
        _db.Users.AsNoTracking().AnyAsync(user => user.BrokerId == brokerId);

    private Task<MatchConnectionRequest?> LatestConnectionRequestAsync(int matchId) =>
        _db.MatchConnectionRequests
            .AsNoTracking()
            .Where(item => item.MatchId == matchId)
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync();

    private async Task MarkConnectionRequestCreditRequiredAsync(long connectionRequestId)
    {
        var connectionRequest = await _db.MatchConnectionRequests
            .SingleOrDefaultAsync(item => item.Id == connectionRequestId);
        if (connectionRequest is null || connectionRequest.Status == ConnectionRequestStatuses.Accepted)
        {
            return;
        }

        connectionRequest.Status = ConnectionRequestStatuses.CreditRequired;
        connectionRequest.RespondedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task AddRequestOutcomeNotificationAsync(
        Match match,
        MatchConnectionRequest connectionRequest,
        string type,
        int recipientBrokerId,
        string? reasonCode,
        string? reasonText,
        DateTime createdAt)
    {
        if (await _db.BrokerNotifications.AnyAsync(notification =>
                notification.ConnectionRequestId == connectionRequest.Id &&
                notification.BrokerId == recipientBrokerId &&
                notification.Type == type))
        {
            return;
        }

        _db.BrokerNotifications.Add(new BrokerNotification
        {
            BrokerId = recipientBrokerId,
            ConnectionRequestId = connectionRequest.Id,
            Type = type,
            Channel = "in_app",
            ChannelStatus = "pending",
            PayloadJson = JsonSerializer.Serialize(new
            {
                match_id = match.Id,
                request_id = connectionRequest.Id,
                reason_code = reasonCode,
                reason_text = reasonText
            }),
            CreatedAt = createdAt
        });
    }

    private static MatchRejectionResponseDto RejectionResponse(
        int matchId,
        MatchConnectionRequest connectionRequest) => new()
        {
            Success = true,
            Message = "Connection request rejected. No tokens were deducted.",
            MatchId = matchId,
            ConnectionRequestId = connectionRequest.Id,
            ConnectionRequestStatus = ConnectionRequestStatuses.Rejected
        };

    private async Task<UnlockPropertyResponseDto> RevealAsync(int matchId, int callerBrokerId)
    {
        await using var transaction = await _db.Database.BeginTransactionAsync();
        var match = await LockMatchAsync(matchId)
            ?? throw new KeyNotFoundException("Match not found.");
        EnsureMatchParty(match, callerBrokerId);

        var existing = await _db.Reveals.SingleOrDefaultAsync(r => r.MatchId == matchId);
        if (existing is not null)
        {
            await FinalizeAcceptedConnectionRequestAsync(match, DateTime.UtcNow);
            var existingResponse = await BuildSuccessResponseAsync(match, callerBrokerId, "Contact details already unlocked.");
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
            return existingResponse;
        }

        var callerWalletBalance = await GetWalletBalanceAsync(callerBrokerId);
        if (!string.Equals(match.State, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            await transaction.RollbackAsync();
            return Failure(
                "confirmation_required",
                "Both brokers must confirm before contacts are revealed.",
                callerWalletBalance);
        }

        var brokerIds = new[] { match.ListingBrokerId, match.RequirementBrokerId };
        var wallets = await _db.CreditWallets
            .FromSqlInterpolated($@"SELECT * FROM credit_wallets
                                   WHERE broker_id = {match.ListingBrokerId}
                                      OR broker_id = {match.RequirementBrokerId}
                                   ORDER BY broker_id
                                   FOR UPDATE")
            .ToListAsync();
        if (wallets.Count != 2 || wallets.Any(w => TotalCredits(w) < CreditsPerReveal))
        {
            var currentCallerBalance = wallets.FirstOrDefault(w => w.BrokerId == callerBrokerId) is { } wallet
                ? TotalCredits(wallet)
                : 0;
            await transaction.RollbackAsync();
            return Failure(
                "insufficient_credits",
                "Both brokers need at least one credit to reveal this match.",
                currentCallerBalance);
        }

        var revealedAt = DateTime.UtcNow;
        var reveal = new Reveal { MatchId = matchId, RevealedAt = revealedAt };
        _db.Reveals.Add(reveal);
        await _db.SaveChangesAsync();

        foreach (var wallet in wallets)
        {
            DeductCredit(wallet);
            wallet.UpdatedAt = revealedAt;
            _db.CreditTransactions.Add(new CreditTransaction
            {
                BrokerId = wallet.BrokerId,
                Type = "deduct",
                Amount = CreditsPerReveal,
                BalanceAfter = TotalCredits(wallet),
                ReferenceType = "reveal",
                ReferenceId = reveal.Id,
                Notes = $"Reveal for match {matchId}",
                CreatedAt = revealedAt
            });
        }

        await FinalizeAcceptedConnectionRequestAsync(match, revealedAt);
        await _db.SaveChangesAsync();
        var response = await BuildSuccessResponseAsync(match, callerBrokerId, "Contacts revealed successfully.");
        await transaction.CommitAsync();
        _logger.LogInformation(
            "Revealed match {MatchId} and deducted one credit from brokers {ListingBrokerId} and {RequirementBrokerId}.",
            matchId,
            match.ListingBrokerId,
            match.RequirementBrokerId);
        return response;
    }

    private async Task FinalizeAcceptedConnectionRequestAsync(Match match, DateTime acceptedAt)
    {
        var connectionRequest = await _db.MatchConnectionRequests
            .Where(item => item.MatchId == match.Id &&
                           (item.Status == ConnectionRequestStatuses.Pending ||
                            item.Status == ConnectionRequestStatuses.CreditRequired))
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync();
        if (connectionRequest is null)
        {
            return;
        }

        connectionRequest.Status = ConnectionRequestStatuses.Accepted;
        connectionRequest.RespondedAt = acceptedAt;
        match.State = "revealed";
        match.StatusUpdatedAt = acceptedAt;
        await MarkIncomingConfirmationNotificationsReadAsync(
            connectionRequest.Id,
            connectionRequest.ReceivingBrokerId,
            acceptedAt);
        await AddRequestOutcomeNotificationAsync(
            match,
            connectionRequest,
            "confirm_accepted",
            connectionRequest.RequestingBrokerId,
            null,
            null,
            acceptedAt);
    }

    private async Task<UnlockPropertyResponseDto> BuildSuccessResponseAsync(
        Match match,
        int callerBrokerId,
        string message)
    {
        var counterpartyId = match.ListingBrokerId == callerBrokerId
            ? match.RequirementBrokerId
            : match.ListingBrokerId;
        var counterparty = await _db.Brokers.AsNoTracking().SingleAsync(b => b.Id == counterpartyId);
        var email = await _db.Users.AsNoTracking()
            .Where(u => u.BrokerId == counterpartyId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync();

        return new UnlockPropertyResponseDto
        {
            Success = true,
            Message = message,
            CreditsRemaining = await GetWalletBalanceAsync(callerBrokerId),
            UnlockedContact = new ContactDetailsDto
            {
                OwnerName = counterparty.Name ?? "Counterparty Broker",
                OwnerMobile = counterparty.PhoneNumber,
                OwnerEmail = email
            }
        };
    }

    private Task<Match?> LockMatchAsync(int matchId) =>
        _db.Matches
            .FromSqlInterpolated($"SELECT * FROM matches WHERE matchid = {matchId} FOR UPDATE")
            .SingleOrDefaultAsync();

    private static void EnsureMatchParty(Match match, int brokerId)
    {
        if (brokerId != match.ListingBrokerId && brokerId != match.RequirementBrokerId)
        {
            throw new UnauthorizedAccessException("Broker is not a party to this match.");
        }
    }

    private async Task<int> GetWalletBalanceAsync(int brokerId) =>
        await _db.CreditWallets
            .AsNoTracking()
            .Where(w => w.BrokerId == brokerId)
            .Select(w => w.FreeCreditsBalance + w.PaidCreditsBalance)
            .SingleOrDefaultAsync();

    private static int TotalCredits(CreditWallet wallet) =>
        wallet.FreeCreditsBalance + wallet.PaidCreditsBalance;

    private static void DeductCredit(CreditWallet wallet)
    {
        if (wallet.FreeCreditsBalance > 0) wallet.FreeCreditsBalance--;
        else wallet.PaidCreditsBalance--;
    }

    private static UnlockPropertyResponseDto Failure(string code, string message, int balance) => new()
    {
        Success = false,
        ErrorCode = code,
        Message = message,
        CreditsRemaining = balance,
        UnlockedContact = null
    };

    public Task<bool> IsMatchRevealedAsync(int matchId, Guid _) =>
        _db.Reveals.AnyAsync(r => r.MatchId == matchId);

    public Task<CreditWallet> GetWalletAsync(Guid _) =>
        throw new NotSupportedException("Wallet access requires an authenticated broker identity.");

    public Task InitializeWalletAsync(Guid _, int __ = 10) => Task.CompletedTask;

    private static MatchConfirmationResponseDto ConfirmationResponse(
        Match match,
        string message,
        DateTime? expiry,
        MatchConnectionRequest? connectionRequest,
        bool? counterpartyRegistered = null,
        UnlockPropertyResponseDto? reveal = null) => new()
        {
            Success = reveal?.Success ?? true,
            ErrorCode = reveal?.ErrorCode,
            Message = message,
            MatchId = match.Id,
            State = match.State,
            WindowExpiresAt = expiry,
            CreditsRequired = CreditsPerReveal,
            ConnectionRequestId = connectionRequest?.Id,
            ConnectionRequestStatus = connectionRequest?.Status,
            DeliveryChannel = connectionRequest?.DeliveryChannel,
            DeliveryStatus = connectionRequest?.DeliveryStatus,
            CounterpartyRegistered = counterpartyRegistered,
            IsRevealed = reveal?.Success == true && reveal.UnlockedContact is not null,
            CreditsRemaining = reveal?.CreditsRemaining,
            UnlockedContact = reveal?.UnlockedContact
        };

    private static MatchConfirmationResponseDto ConfirmationFailure(
        Match match,
        string errorCode,
        string message) => new()
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            MatchId = match.Id,
            State = match.State,
            CreditsRequired = CreditsPerReveal
        };
}
