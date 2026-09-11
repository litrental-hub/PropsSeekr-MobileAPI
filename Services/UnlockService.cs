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
    private readonly IWalletAccountingService _walletAccounting;

    public UnlockService(AppDbContext db, ILogger<UnlockService> logger, IWalletAccountingService walletAccounting)
    {
        _db = db;
        _logger = logger;
        _walletAccounting = walletAccounting;
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
        var candidate = await _db.Matches.AsNoTracking().SingleOrDefaultAsync(item => item.Id == request.MatchId)
            ?? throw new KeyNotFoundException("Match not found.");
        EnsureMatchParty(candidate, brokerId);
        if (!await _db.Reveals.AnyAsync(item => item.MatchId == candidate.Id))
            await LockValidInventoryAsync(candidate, DateTime.UtcNow);
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

        if (connectionRequest is not null && connectionRequest.ExpiresAt <= now)
        {
            connectionRequest.Status = ConnectionRequestStatuses.Expired;
            connectionRequest.RespondedAt = now;
            match.State = "matched";
            match.Status = "MATCHED";
            match.StatusUpdatedAt = now;
            await ResetConfirmationsAsync(connectionRequest.Id);
            connectionRequest = null;
        }

        if (connectionRequest is not null)
        {
            var currentInventory = await LockValidInventoryAsync(match, now);
            if (currentInventory is null ||
                currentInventory.ListingVersion != connectionRequest.ListingVersion ||
                currentInventory.RequirementVersion != connectionRequest.RequirementVersion)
            {
                connectionRequest.Status = ConnectionRequestStatuses.Expired;
                connectionRequest.RespondedAt = now;
                match.State = "matched";
                match.Status = "INVALIDATED";
                match.StatusUpdatedAt = now;
                await ResetConfirmationsAsync(connectionRequest.Id);
                await _db.SaveChangesAsync();
                await transaction.CommitAsync();
                return ConfirmationFailure(match, "inventory_changed", "The listing or requirement changed. Review the new match before confirming again.");
            }
        }

        if (connectionRequest?.Status == ConnectionRequestStatuses.CreditRequired)
        {
            var creditRetry = await RevealAsync(match.Id, brokerId);
            await transaction.CommitAsync();
            return ConfirmationResponse(match, creditRetry.Message, null, connectionRequest, reveal: creditRetry);
        }

        if (connectionRequest is null)
        {
            if (await GetWalletBalanceAsync(brokerId) < CreditsPerReveal)
            {
                await transaction.RollbackAsync();
                return ConfirmationFailure(match, "insufficient_credits", "You need at least one token to request this connection.");
            }

            var inventory = await LockValidInventoryAsync(match, now);
            if (inventory is null)
            {
                await transaction.RollbackAsync();
                return ConfirmationFailure(match, "inventory_changed", "This listing or requirement is no longer available for connection.");
            }
            var receivingBrokerId = CounterpartyBrokerId(match, brokerId);
            var counterpartyRegistered = await IsRegisteredBrokerAsync(receivingBrokerId);
            connectionRequest = new MatchConnectionRequest
            {
                MatchId = match.Id,
                RequestingBrokerId = brokerId,
                ReceivingBrokerId = receivingBrokerId,
                ListingVersion = inventory.ListingVersion,
                RequirementVersion = inventory.RequirementVersion,
                Status = ConnectionRequestStatuses.Pending,
                DeliveryChannel = counterpartyRegistered ? "in_app" : "whatsapp",
                DeliveryStatus = counterpartyRegistered ? "created" : "planned",
                CreatedAt = now,
                ExpiresAt = now.Add(ConfirmationWindow)
            };
            _db.MatchConnectionRequests.Add(connectionRequest);
            await _db.SaveChangesAsync();
            await UpsertConfirmationAsync(connectionRequest.Id, match.Id, brokerId, request, now, connectionRequest.ExpiresAt);
            await MarkPartyInventoryConfirmedAsync(match, brokerId, now);
            match.State = "pending_confirmation";
            match.Status = "PENDING_CONFIRMATION";
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
            await UpsertConfirmationAsync(connectionRequest.Id, match.Id, brokerId, request, now, connectionRequest.ExpiresAt);
            await MarkPartyInventoryConfirmedAsync(match, brokerId, now);
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

        await UpsertConfirmationAsync(connectionRequest.Id, match.Id, brokerId, request, now, connectionRequest.ExpiresAt);
        await MarkPartyInventoryConfirmedAsync(match, brokerId, now);
        match.State = "pending_confirmation";
        match.Status = "PENDING_CONFIRMATION";
        match.StatusUpdatedAt = now;
        await _db.SaveChangesAsync();

        var parties = new[] { match.ListingBrokerId, match.RequirementBrokerId };
        var confirmations = await _db.MatchConfirmations
            .Where(c => c.ConnectionRequestId == connectionRequest.Id && parties.Contains(c.BrokerId))
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
        match.Status = "CONFIRMED";
        match.StatusUpdatedAt = now;
        await MarkIncomingConfirmationNotificationsReadAsync(connectionRequest.Id, brokerId, now);
        await _db.SaveChangesAsync();
        var reveal = await RevealAsync(match.Id, brokerId);
        await transaction.CommitAsync();
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
        match.Status = "MATCHED";
        match.StatusUpdatedAt = now;
        await ResetConfirmationsAsync(connectionRequest.Id);
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

    public async Task<int> ExpirePendingRequestsAsync(int batchSize = 200)
    {
        batchSize = Math.Clamp(batchSize, 1, 500);
        var now = DateTime.UtcNow;
        await using var transaction = await _db.Database.BeginTransactionAsync();
        var overdueMatchIds = await _db.MatchConnectionRequests
            .AsNoTracking()
            .Where(request =>
                (request.Status == ConnectionRequestStatuses.Pending ||
                 request.Status == ConnectionRequestStatuses.CreditRequired) &&
                request.ExpiresAt <= now)
            .OrderBy(request => request.MatchId)
            .Select(request => request.MatchId)
            .Distinct()
            .Take(batchSize)
            .ToListAsync();

        var expiredCount = 0;
        foreach (var matchId in overdueMatchIds)
        {
            var match = await LockMatchAsync(matchId);
            if (match is null) continue;

            var request = await _db.MatchConnectionRequests
                .Where(item => item.MatchId == matchId &&
                    (item.Status == ConnectionRequestStatuses.Pending ||
                     item.Status == ConnectionRequestStatuses.CreditRequired) &&
                    item.ExpiresAt <= now)
                .OrderByDescending(item => item.Id)
                .FirstOrDefaultAsync();
            if (request is null) continue;

            request.Status = ConnectionRequestStatuses.Expired;
            request.RespondedAt = now;
            match.State = "matched";
            match.Status = "MATCHED";
            match.StatusUpdatedAt = now;
            await ResetConfirmationsAsync(request.Id);
            await AddRequestOutcomeNotificationAsync(
                match,
                request,
                "confirm_expired",
                request.RequestingBrokerId,
                null,
                null,
                now);
            expiredCount++;
        }

        await _db.SaveChangesAsync();
        await transaction.CommitAsync();
        return expiredCount;
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
        long connectionRequestId,
        int matchId,
        int brokerId,
        MatchConfirmationRequestDto request,
        DateTime confirmedAt,
        DateTime attemptExpiresAt)
    {
        var confirmation = await _db.MatchConfirmations
            .SingleOrDefaultAsync(item => item.ConnectionRequestId == connectionRequestId && item.BrokerId == brokerId);
        if (confirmation is null)
        {
            confirmation = new MatchConfirmation
            {
                ConnectionRequestId = connectionRequestId,
                MatchId = matchId,
                BrokerId = brokerId,
                CreatedAt = confirmedAt
            };
            _db.MatchConfirmations.Add(confirmation);
        }
        else if (confirmation.ConfirmedAt.HasValue)
        {
            // A retry in the same attempt is idempotent and cannot refresh or
            // rewrite the consent proof captured by the original confirmation.
            return;
        }

        confirmation.AvailabilityConfirmed = request.AvailabilityConfirmed;
        confirmation.PriceValid = request.PriceValid;
        confirmation.PriceNegotiable = request.PriceNegotiable;
        confirmation.ReadyToConnect = request.ReadyToConnect;
        confirmation.AvailabilityDate = request.AvailabilityDate;
        confirmation.ConfirmedAt = confirmedAt;
        confirmation.WindowExpiresAt = attemptExpiresAt;
    }

    private async Task ResetConfirmationsAsync(long connectionRequestId)
    {
        var confirmations = await _db.MatchConfirmations
            .Where(item => item.ConnectionRequestId == connectionRequestId)
            .ToListAsync();
        foreach (var confirmation in confirmations)
        {
            confirmation.ConfirmedAt = null;
            confirmation.WindowExpiresAt = null;
            confirmation.AvailabilityConfirmed = null;
            confirmation.PriceValid = null;
            confirmation.PriceNegotiable = null;
            confirmation.ReadyToConnect = null;
            confirmation.AvailabilityDate = null;
        }
    }

    private async Task MarkPartyInventoryConfirmedAsync(Match match, int brokerId, DateTime confirmedAt)
    {
        if (brokerId == match.ListingBrokerId)
        {
            await _db.Listings
                .Where(item => item.Id == match.ListingId && item.BrokerId == brokerId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastConfirmedAt, confirmedAt)
                    .SetProperty(item => item.FreshnessUpdatedAt, confirmedAt)
                    .SetProperty(item => item.FreshnessScore, 100)
                    .SetProperty(item => item.FreshnessCategory, "Recently Confirmed"));
        }

        if (brokerId == match.RequirementBrokerId)
        {
            await _db.Requirements
                .Where(item => item.Id == match.RequirementId && item.BrokerId == brokerId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.LastConfirmedAt, confirmedAt)
                    .SetProperty(item => item.FreshnessUpdatedAt, confirmedAt)
                    .SetProperty(item => item.FreshnessScore, 100)
                    .SetProperty(item => item.FreshnessCategory, "Recently Confirmed"));
        }
    }

    private async Task<InventoryVersionSnapshot?> LockValidInventoryAsync(Match match, DateTime now)
    {
        // All connection transitions use listing -> requirement -> match -> wallet order.
        // The match is already locked by callers; these row locks bind consent to
        // the exact inventory versions and serialize against relevant edits.
        var listingLocked = await _db.Database
            .SqlQuery<int>($"SELECT listingid AS \"Value\" FROM listings WHERE listingid = {match.ListingId} FOR UPDATE")
            .SingleOrDefaultAsync();
        var requirementLocked = await _db.Database
            .SqlQuery<int>($"SELECT requirementid AS \"Value\" FROM requirements WHERE requirementid = {match.RequirementId} FOR UPDATE")
            .SingleOrDefaultAsync();
        if (listingLocked == 0 || requirementLocked == 0) return null;

        var listing = await _db.Listings.AsNoTracking()
            .Where(item => item.Id == match.ListingId)
            .Select(item => new { item.ContentVersion, item.EmbeddingVersion, item.EmbeddingStatus, item.IsAvailable, item.Status, item.ExpiresAt })
            .SingleAsync();
        var requirement = await _db.Requirements.AsNoTracking()
            .Where(item => item.Id == match.RequirementId)
            .Select(item => new { item.ContentVersion, item.EmbeddingVersion, item.EmbeddingStatus, item.IsAvailable, item.Status, item.ExpiresAt })
            .SingleAsync();

        var listingReady = listing.IsAvailable &&
            string.Equals(listing.Status ?? "active", "active", StringComparison.OrdinalIgnoreCase) &&
            (!listing.ExpiresAt.HasValue || listing.ExpiresAt > now) &&
            listing.EmbeddingVersion == listing.ContentVersion && listing.EmbeddingStatus == "completed";
        var requirementReady = requirement.IsAvailable &&
            string.Equals(requirement.Status ?? "active", "active", StringComparison.OrdinalIgnoreCase) &&
            (!requirement.ExpiresAt.HasValue || requirement.ExpiresAt > now) &&
            requirement.EmbeddingVersion == requirement.ContentVersion && requirement.EmbeddingStatus == "completed";
        return listingReady && requirementReady
            ? new InventoryVersionSnapshot(listing.ContentVersion, requirement.ContentVersion)
            : null;
    }

    private sealed record InventoryVersionSnapshot(int ListingVersion, int RequirementVersion);

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
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync() : null;
        if (ownsTransaction)
        {
            var candidate = await _db.Matches.AsNoTracking().SingleOrDefaultAsync(item => item.Id == matchId)
                ?? throw new KeyNotFoundException("Match not found.");
            EnsureMatchParty(candidate, callerBrokerId);
            if (!await _db.Reveals.AnyAsync(item => item.MatchId == matchId))
                await LockValidInventoryAsync(candidate, DateTime.UtcNow);
        }
        var match = await LockMatchAsync(matchId)
            ?? throw new KeyNotFoundException("Match not found.");
        EnsureMatchParty(match, callerBrokerId);

        var existing = await _db.Reveals.SingleOrDefaultAsync(r => r.MatchId == matchId);
        if (existing is not null)
        {
            await FinalizeAcceptedConnectionRequestAsync(match, DateTime.UtcNow);
            var existingResponse = await BuildSuccessResponseAsync(match, callerBrokerId, "Contact details already unlocked.");
            await _db.SaveChangesAsync();
            if (ownsTransaction) await transaction!.CommitAsync();
            return existingResponse;
        }

        var callerWalletBalance = await GetWalletBalanceAsync(callerBrokerId);
        if (!string.Equals(match.State, "confirmed", StringComparison.OrdinalIgnoreCase))
        {
            if (ownsTransaction) await transaction!.RollbackAsync();
            return Failure(
                "confirmation_required",
                "Both brokers must confirm before contacts are revealed.",
                callerWalletBalance);
        }

        var now = DateTime.UtcNow;
        var activeRequest = await _db.MatchConnectionRequests
            .Where(item => item.MatchId == match.Id &&
                (item.Status == ConnectionRequestStatuses.Pending ||
                 item.Status == ConnectionRequestStatuses.CreditRequired))
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync();
        var currentInventory = await LockValidInventoryAsync(match, now);
        var inventoryMatchesAttempt = activeRequest is not null && currentInventory is not null &&
            activeRequest.ListingVersion == currentInventory.ListingVersion &&
            activeRequest.RequirementVersion == currentInventory.RequirementVersion;
        var partyIds = new[] { match.ListingBrokerId, match.RequirementBrokerId };
        var confirmations = activeRequest is null
            ? []
            : await _db.MatchConfirmations
                .Where(item => item.ConnectionRequestId == activeRequest.Id && partyIds.Contains(item.BrokerId))
                .ToListAsync();
        var bothValid = partyIds.Distinct().Count() == 2 &&
                        confirmations.Count(item =>
                            item.ConfirmedAt.HasValue &&
                            item.WindowExpiresAt > now &&
                            item.AvailabilityConfirmed == true &&
                            item.PriceValid == true &&
                            item.ReadyToConnect == true) == 2;

        if (activeRequest is null || activeRequest.ExpiresAt <= now || !bothValid || !inventoryMatchesAttempt)
        {
            if (activeRequest is not null && activeRequest.ExpiresAt <= now)
            {
                activeRequest.Status = ConnectionRequestStatuses.Expired;
                activeRequest.RespondedAt = now;
            }
            match.State = "matched";
            match.Status = "MATCHED";
            match.StatusUpdatedAt = now;
            if (activeRequest is not null) await ResetConfirmationsAsync(activeRequest.Id);
            await _db.SaveChangesAsync();
            if (ownsTransaction) await transaction!.CommitAsync();
            return Failure(
                inventoryMatchesAttempt ? "confirmation_expired" : "inventory_changed",
                inventoryMatchesAttempt
                    ? "Both brokers must confirm within the active confirmation window."
                    : "The listing or requirement changed. Review the new match before confirming again.",
                callerWalletBalance);
        }

        var brokerIds = new[] { match.ListingBrokerId, match.RequirementBrokerId };
        var wallets = await _walletAccounting.LockAsync(brokerIds);
        foreach (var wallet in wallets)
            await _walletAccounting.SettleFreeCreditsAsync(wallet, now);
        if (wallets.Count != 2 || wallets.Any(w => TotalCredits(w) < CreditsPerReveal))
        {
            var currentCallerBalance = wallets.FirstOrDefault(w => w.BrokerId == callerBrokerId) is { } wallet
                ? TotalCredits(wallet)
                : 0;
            activeRequest.Status = ConnectionRequestStatuses.CreditRequired;
            activeRequest.RespondedAt = null;
            await _db.SaveChangesAsync();
            if (ownsTransaction) await transaction!.CommitAsync();
            return Failure(
                "insufficient_credits",
                "Both brokers need at least one credit to reveal this match.",
                currentCallerBalance);
        }

        var revealedAt = DateTime.UtcNow;
        var reveal = new Reveal { MatchId = matchId, ConnectionRequestId = activeRequest.Id, RevealedAt = revealedAt };
        _db.Reveals.Add(reveal);
        await _db.SaveChangesAsync();

        foreach (var wallet in wallets)
        {
            _walletAccounting.Debit(wallet, CreditsPerReveal, "reveal", reveal.Id,
                reveal.Id.ToString(), $"Reveal for match {matchId}", revealedAt);
        }

        await FinalizeAcceptedConnectionRequestAsync(match, revealedAt);
        await _db.SaveChangesAsync();
        var response = await BuildSuccessResponseAsync(match, callerBrokerId, "Contacts revealed successfully.");
        if (ownsTransaction) await transaction!.CommitAsync();
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
        match.Status = "REVEALED";
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
            ListingVersion = connectionRequest?.ListingVersion,
            RequirementVersion = connectionRequest?.RequirementVersion,
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
