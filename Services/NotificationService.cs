using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Notifications;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class NotificationService : INotificationService
{
    private readonly AppDbContext _dbContext;

    public NotificationService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<NotificationListResponseDto> GetNotificationsAsync(Guid userId, int page, int limit, string filter)
    {
        var pageNumber = page > 0 ? page : 1;
        var limitSize = (limit > 0 && limit <= 100) ? limit : 20;
        var skip = (pageNumber - 1) * limitSize;

        var query = _dbContext.Notifications
            .AsNoTracking()
            .Where(n => n.UserId == userId);

        // Apply filters
        var normalizedFilter = filter?.Trim().ToUpperInvariant() ?? "ALL";
        switch (normalizedFilter)
        {
            case "UNREAD":
                query = query.Where(n => !n.IsRead);
                break;
            case "MATCHES":
                query = query.Where(n => n.Type == "MATCH");
                break;
            case "BROKER_REQUESTS":
                query = query.Where(n => n.Type == "BROKER_REQUEST" || n.Type == "BROKER_UNLOCK");
                break;
            default:
                // ALL or other unknown values gets everything
                break;
        }

        var totalCount = await query.CountAsync();
        var unreadCount = await _dbContext.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead);

        var notifications = await query
            .OrderByDescending(n => n.CreatedAt)
            .Skip(skip)
            .Take(limitSize)
            .ToListAsync();

        var responseData = new List<NotificationResponseDto>();

        foreach (var n in notifications)
        {
            var meta = !string.IsNullOrEmpty(n.MetaJson)
                ? JsonSerializer.Deserialize<NotificationMetaDto>(n.MetaJson)
                : null;

            if (meta != null)
            {
                if (n.RequiresTokenUnlock)
                {
                    if (n.IsContactUnlocked)
                    {
                        // Unmask and populate the actual broker's phone number
                        if (Guid.TryParse(meta.BrokerId, out var brokerGuid))
                        {
                            var broker = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == brokerGuid);
                            if (broker != null)
                            {
                                meta.BrokerPhone = broker.MobileNumber;
                                meta.BrokerName = broker.Name;
                            }
                        }
                    }
                    else
                    {
                        // Mask details
                        meta.BrokerPhone = null;
                    }
                }
            }

            responseData.Add(new NotificationResponseDto
            {
                Id = n.Id.ToString(),
                Type = n.Type,
                Title = n.Title,
                Body = n.Body,
                IsRead = n.IsRead,
                RequiresTokenUnlock = n.RequiresTokenUnlock,
                IsContactUnlocked = n.IsContactUnlocked,
                TokenCost = n.TokenCost,
                CreatedAt = n.CreatedAt,
                Meta = meta
            });
        }

        return new NotificationListResponseDto
        {
            Success = true,
            UserId = userId.ToString(),
            Page = pageNumber,
            Limit = limitSize,
            TotalCount = totalCount,
            UnreadCount = unreadCount,
            Data = responseData
        };
    }

    public async Task<bool> MarkAsReadAsync(Guid notificationId, Guid userId)
    {
        var notification = await _dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);

        if (notification == null)
        {
            throw new KeyNotFoundException("Notification not found or access denied.");
        }

        if (!notification.IsRead)
        {
            notification.IsRead = true;
            await _dbContext.SaveChangesAsync();
        }

        return true;
    }

    public async Task<bool> MarkAllAsReadAsync(Guid userId)
    {
        var unreadNotifications = await _dbContext.Notifications
            .Where(n => n.UserId == userId && !n.IsRead)
            .ToListAsync();

        if (unreadNotifications.Any())
        {
            foreach (var n in unreadNotifications)
            {
                n.IsRead = true;
            }
            await _dbContext.SaveChangesAsync();
        }

        return true;
    }
    public async Task<UnlockBrokerResponseDto> UnlockBrokerContactAsync(Guid notificationId, Guid userId)
    {
        var notification = await _dbContext.Notifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == userId);

        if (notification == null)
        {
            throw new KeyNotFoundException("Notification not found or access denied.");
        }

        if (!notification.RequiresTokenUnlock)
        {
            throw new InvalidOperationException("This notification contact does not require token unlocking.");
        }

        var user = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            throw new KeyNotFoundException("User not found.");
        }

        // If already unlocked, simply return the unmasked details without debiting again
        if (notification.IsContactUnlocked)
        {
            var meta = !string.IsNullOrEmpty(notification.MetaJson)
                ? JsonSerializer.Deserialize<NotificationMetaDto>(notification.MetaJson)
                : new NotificationMetaDto();

            if (meta != null && Guid.TryParse(meta.BrokerId, out var bId))
            {
                var broker = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == bId);
                if (broker != null)
                {
                    meta.BrokerPhone = broker.MobileNumber;
                    meta.BrokerName = broker.Name;
                }
            }

            return new UnlockBrokerResponseDto
            {
                Success = true,
                Message = "Broker contact details already unlocked.",
                Id = notification.Id.ToString(),
                TokensDebited = 0,
                RemainingTokens = user.Credits,
                IsContactUnlocked = true,
                Meta = meta
            };
        }

        // Mutual Unlock check if notification type is BROKER_UNLOCK
        if (notification.Type == "BROKER_UNLOCK")
        {
            // Parse metadata
            var meta = !string.IsNullOrEmpty(notification.MetaJson)
                ? JsonSerializer.Deserialize<NotificationMetaDto>(notification.MetaJson)
                : null;

            Guid initUserId;
            Guid initPropId;
            Guid targetPropId;

            if (meta == null || 
                !Guid.TryParse(meta.InitiatorUserId, out initUserId) || 
                !Guid.TryParse(meta.InitiatorPropertyRequestId, out initPropId) || 
                !Guid.TryParse(meta.TargetPropertyRequestId, out targetPropId))
            {
                throw new InvalidOperationException("Notification metadata is missing or invalid.");
            }

            var userB = user; // Current user receiving the notification
            var userA = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == initUserId);

            if (userA == null)
            {
                throw new KeyNotFoundException("Requesting user not found.");
            }

            var propA = await _dbContext.PropertyRequests.FindAsync(initPropId);
            var propB = await _dbContext.PropertyRequests.FindAsync(targetPropId);

            if (propA == null || propB == null)
            {
                throw new KeyNotFoundException("Property requests not found.");
            }

            var match = await GetOrCreateLegacyMatchAsync(propA, propB);
            var (brokerA, walletA) = await GetOrCreateLegacyBrokerAsync(userA);
            var (brokerB, walletB) = await GetOrCreateLegacyBrokerAsync(userB);

            // Check if the 4-hour window has expired
            var confirmationA = await _dbContext.MatchConfirmations.FirstOrDefaultAsync(c => c.MatchId == match.Id && c.BrokerId == brokerA.Id);
            if (confirmationA != null && confirmationA.WindowExpiresAt.HasValue && confirmationA.WindowExpiresAt.Value < DateTime.UtcNow)
            {
                using var dbTxEx = await _dbContext.Database.BeginTransactionAsync();
                try
                {
                    match.State = "matched";
                    match.Status = "matched";
                    match.StatusUpdatedAt = DateTime.UtcNow;

                    var confirmations = await _dbContext.MatchConfirmations.Where(c => c.MatchId == match.Id).ToListAsync();
                    _dbContext.MatchConfirmations.RemoveRange(confirmations);
                    await _dbContext.SaveChangesAsync();
                    await dbTxEx.CommitAsync();
                }
                catch
                {
                    await dbTxEx.RollbackAsync();
                }

                throw new InvalidOperationException("The 4-hour confirmation window has expired. Please try connecting again.");
            }

            var totalA = walletA.FreeCreditsBalance + walletA.PaidCreditsBalance;
            var totalB = walletB.FreeCreditsBalance + walletB.PaidCreditsBalance;

            if (totalA < 1)
            {
                throw new InvalidOperationException("The requesting user has insufficient tokens to complete the unlock.");
            }

            if (totalB < 1)
            {
                throw new InvalidOperationException("Insufficient tokens. You need at least 1 token to unlock this contact.");
            }

            using var transaction = await _dbContext.Database.BeginTransactionAsync();
            try
            {
                // Create confirmation for acceptor (current user)
                var confirmationB = await _dbContext.MatchConfirmations.FirstOrDefaultAsync(c => c.MatchId == match.Id && c.BrokerId == brokerB.Id);
                if (confirmationB == null)
                {
                    confirmationB = new MatchConfirmation
                    {
                        MatchId = match.Id,
                        BrokerId = brokerB.Id,
                        AvailabilityConfirmed = true,
                        PriceValid = true,
                        PriceNegotiable = false,
                        ReadyToConnect = true,
                        ConfirmedAt = DateTime.UtcNow,
                        WindowExpiresAt = null,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.MatchConfirmations.Add(confirmationB);
                }

                // Ensure initiator confirmation exists
                var initConfirmation = await _dbContext.MatchConfirmations.FirstOrDefaultAsync(c => c.MatchId == match.Id && c.BrokerId == brokerA.Id);
                if (initConfirmation == null)
                {
                    initConfirmation = new MatchConfirmation
                    {
                        MatchId = match.Id,
                        BrokerId = brokerA.Id,
                        AvailabilityConfirmed = true,
                        PriceValid = true,
                        PriceNegotiable = false,
                        ReadyToConnect = true,
                        ConfirmedAt = DateTime.UtcNow,
                        WindowExpiresAt = null,
                        CreatedAt = DateTime.UtcNow
                    };
                    _dbContext.MatchConfirmations.Add(initConfirmation);
                }

                // Update Match status
                match.State = "confirmed";
                match.Status = "confirmed";
                match.StatusUpdatedAt = DateTime.UtcNow;

                // Deduct credits from initiator (wallet A / userA)
                if (walletA.FreeCreditsBalance >= 1) walletA.FreeCreditsBalance -= 1;
                else walletA.PaidCreditsBalance -= 1;
                walletA.UpdatedAt = DateTime.UtcNow;
                userA.Credits = walletA.FreeCreditsBalance + walletA.PaidCreditsBalance;
                userA.ModifiedDate = DateTime.UtcNow;

                // Deduct credits from acceptor (wallet B / userB)
                if (walletB.FreeCreditsBalance >= 1) walletB.FreeCreditsBalance -= 1;
                else walletB.PaidCreditsBalance -= 1;
                walletB.UpdatedAt = DateTime.UtcNow;
                userB.Credits = walletB.FreeCreditsBalance + walletB.PaidCreditsBalance;
                userB.ModifiedDate = DateTime.UtcNow;

                // Create reveal entry
                var reveal = new Reveal
                {
                    MatchId = match.Id,
                    RevealedAt = DateTime.UtcNow
                };
                _dbContext.Reveals.Add(reveal);
                await _dbContext.SaveChangesAsync();

                // Create ledger transactions
                var txA = new CreditTransaction
                {
                    BrokerId = brokerA.Id,
                    Type = "deduct",
                    Amount = 1,
                    BalanceAfter = userA.Credits,
                    ReferenceType = "reveal",
                    ReferenceId = reveal.Id,
                    Notes = $"Unlocked contact details via handshake match {match.Id}",
                    CreatedAt = DateTime.UtcNow
                };
                var txB = new CreditTransaction
                {
                    BrokerId = brokerB.Id,
                    Type = "deduct",
                    Amount = 1,
                    BalanceAfter = userB.Credits,
                    ReferenceType = "reveal",
                    ReferenceId = reveal.Id,
                    Notes = $"Unlocked contact details via handshake match {match.Id}",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.CreditTransactions.AddRange(txA, txB);

                // Update freshness
                propA.LastConfirmedAt = DateTime.UtcNow;
                propA.FreshnessScore = 100;
                propA.FreshnessCategory = "Recently Confirmed";

                propB.LastConfirmedAt = DateTime.UtcNow;
                propB.FreshnessScore = 100;
                propB.FreshnessCategory = "Recently Confirmed";

                // Add unlock records
                var unlockA = new UnlockedProperty
                {
                    Id = Guid.NewGuid(),
                    UserId = userA.Id,
                    PropertyRequestId = targetPropId,
                    UnlockedAt = DateTime.UtcNow
                };
                var unlockB = new UnlockedProperty
                {
                    Id = Guid.NewGuid(),
                    UserId = userB.Id,
                    PropertyRequestId = initPropId,
                    UnlockedAt = DateTime.UtcNow
                };
                _dbContext.UnlockedProperties.AddRange(unlockA, unlockB);

                // Update notification status
                notification.IsContactUnlocked = true;

                // Update any other pending notifications for B
                var pendingNotifications = await _dbContext.Notifications
                    .Where(n => n.UserId == userId && n.Type == "BROKER_UNLOCK" && !n.IsContactUnlocked)
                    .ToListAsync();
                foreach (var n in pendingNotifications)
                {
                    n.IsContactUnlocked = true;
                }

                // Add success notification for User A
                var successNotificationForUserA = new Notification
                {
                    Id = Guid.NewGuid(),
                    UserId = initUserId,
                    Type = "MATCH_UNLOCKED",
                    Title = "Match Contact Unlocked",
                    Body = $"Congratulations! Contact details with {userB.Name} are now unlocked.",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.Notifications.Add(successNotificationForUserA);

                // Create success notifications for legacy table
                var legacySuccessNotificationA = new BrokerNotification
                {
                    BrokerId = brokerA.Id,
                    Type = "match_found",
                    Channel = "in_app",
                    PayloadJson = JsonSerializer.Serialize(new { matchId = match.Id, revealed = true }),
                    ChannelStatus = "pending",
                    CreatedAt = DateTime.UtcNow
                };
                var legacySuccessNotificationB = new BrokerNotification
                {
                    BrokerId = brokerB.Id,
                    Type = "match_found",
                    Channel = "in_app",
                    PayloadJson = JsonSerializer.Serialize(new { matchId = match.Id, revealed = true }),
                    ChannelStatus = "pending",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.BrokerNotifications.AddRange(legacySuccessNotificationA, legacySuccessNotificationB);

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }

            var responseMeta = (!string.IsNullOrEmpty(notification.MetaJson)
                ? JsonSerializer.Deserialize<NotificationMetaDto>(notification.MetaJson)
                : null) ?? new NotificationMetaDto();

            responseMeta.BrokerPhone = userA.MobileNumber;
            responseMeta.BrokerName = userA.Name;

            return new UnlockBrokerResponseDto
            {
                Success = true,
                Message = "Broker contact successfully unlocked. 1 Token debited.",
                Id = notification.Id.ToString(),
                TokensDebited = 1,
                RemainingTokens = userB.Credits,
                IsContactUnlocked = true,
                Meta = responseMeta
            };
        }

        // Fallback to original single-sided unlock logic (for non-BROKER_UNLOCK type notifications)
        var (brokerSingle, walletSingle) = await GetOrCreateLegacyBrokerAsync(user);
        var totalCreditsSingle = walletSingle.FreeCreditsBalance + walletSingle.PaidCreditsBalance;
        if (totalCreditsSingle < 1)
        {
            throw new InvalidOperationException("Insufficient tokens. You need at least 1 token to unlock this broker contact.");
        }

        using (var transaction = await _dbContext.Database.BeginTransactionAsync())
        {
            try
            {
                if (walletSingle.FreeCreditsBalance >= 1) walletSingle.FreeCreditsBalance -= 1;
                else walletSingle.PaidCreditsBalance -= 1;
                walletSingle.UpdatedAt = DateTime.UtcNow;

                user.Credits = walletSingle.FreeCreditsBalance + walletSingle.PaidCreditsBalance;
                user.ModifiedDate = DateTime.UtcNow;
                notification.IsContactUnlocked = true;

                var txLedger = new CreditTransaction
                {
                    BrokerId = brokerSingle.Id,
                    Type = "deduct",
                    Amount = 1,
                    BalanceAfter = user.Credits,
                    ReferenceType = "reveal",
                    Notes = $"Unlocked contact details via legacy notification {notification.Id}",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.CreditTransactions.Add(txLedger);

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        var responseMetaFallback = !string.IsNullOrEmpty(notification.MetaJson)
            ? JsonSerializer.Deserialize<NotificationMetaDto>(notification.MetaJson)
            : new NotificationMetaDto();

        if (responseMetaFallback != null && Guid.TryParse(responseMetaFallback.BrokerId, out var targetId))
        {
            var brokerObj = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId);
            if (brokerObj != null)
            {
                responseMetaFallback.BrokerPhone = brokerObj.MobileNumber;
                responseMetaFallback.BrokerName = brokerObj.Name;
            }
        }

        return new UnlockBrokerResponseDto
        {
            Success = true,
            Message = "Broker contact successfully unlocked. 1 Token debited.",
            Id = notification.Id.ToString(),
            TokensDebited = 1,
            RemainingTokens = user.Credits,
            IsContactUnlocked = true,
            Meta = responseMetaFallback
        };
    }

    private async Task<(Broker Broker, CreditWallet Wallet)> GetOrCreateLegacyBrokerAsync(User user)
    {
        var broker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.PhoneNumber == user.MobileNumber);
        if (broker == null)
        {
            broker = new Broker
            {
                PhoneNumber = user.MobileNumber,
                Name = user.Name,
                CreditBalance = user.Credits,
                Status = "active",
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow,
                ConfirmationComplianceRate = 100.00m,
                VisibilityPenaltyFlag = false
            };
            _dbContext.Brokers.Add(broker);
            await _dbContext.SaveChangesAsync();
        }

        var wallet = await _dbContext.CreditWallets.FirstOrDefaultAsync(w => w.BrokerId == broker.Id);
        if (wallet == null)
        {
            wallet = new CreditWallet
            {
                BrokerId = broker.Id,
                FreeCreditsBalance = user.Credits,
                PaidCreditsBalance = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.CreditWallets.Add(wallet);
            await _dbContext.SaveChangesAsync();
        }

        return (broker, wallet);
    }

    private async Task<Match> GetOrCreateLegacyMatchAsync(PropertyRequest targetProperty, PropertyRequest userProperty)
    {
        var userA = await _dbContext.Users.FindAsync(targetProperty.UserId);
        var userB = await _dbContext.Users.FindAsync(userProperty.UserId);
        if (userA == null || userB == null)
        {
            throw new KeyNotFoundException("Users not found.");
        }

        var (brokerA, _) = await GetOrCreateLegacyBrokerAsync(userA);
        var (brokerB, _) = await GetOrCreateLegacyBrokerAsync(userB);

        Listing legacyListing;
        Requirement legacyRequirement;

        if (string.Equals(targetProperty.ListingType, "SUPPLY", StringComparison.OrdinalIgnoreCase))
        {
            legacyListing = await GetOrCreateLegacyListingAsync(targetProperty, brokerA.Id);
            legacyRequirement = await GetOrCreateLegacyRequirementAsync(userProperty, brokerB.Id);
        }
        else
        {
            legacyListing = await GetOrCreateLegacyListingAsync(userProperty, brokerB.Id);
            legacyRequirement = await GetOrCreateLegacyRequirementAsync(targetProperty, brokerA.Id);
        }

        var match = await _dbContext.Matches.FirstOrDefaultAsync(m => m.ListingId == legacyListing.Id && m.RequirementId == legacyRequirement.Id);
        if (match == null)
        {
            match = new Match
            {
                ListingId = legacyListing.Id,
                RequirementId = legacyRequirement.Id,
                ListingBrokerId = legacyListing.BrokerId,
                RequirementBrokerId = legacyRequirement.BrokerId,
                MatchScore = 100,
                Status = "matched",
                State = "matched",
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.Matches.Add(match);
            await _dbContext.SaveChangesAsync();
        }

        return match;
    }

    private async Task<Listing> GetOrCreateLegacyListingAsync(PropertyRequest prop, int brokerId)
    {
        var listing = await _dbContext.Listings.FirstOrDefaultAsync(l => l.ContentHash == prop.Id.ToString());
        if (listing == null)
        {
            listing = new Listing
            {
                BrokerId = brokerId,
                RawMessageText = prop.Title,
                ListingType = "SUPPLY",
                PropertyType = prop.Category,
                Configuration = prop.PropertyTypesJson.Contains("2BHK") ? "2BHK" : "3BHK",
                Price = prop.BudgetMin,
                Status = "active",
                ContentHash = prop.Id.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Listings.Add(listing);
            await _dbContext.SaveChangesAsync();
        }
        return listing;
    }

    private async Task<Requirement> GetOrCreateLegacyRequirementAsync(PropertyRequest prop, int brokerId)
    {
        var requirement = await _dbContext.Requirements.FirstOrDefaultAsync(r => r.ContentHash == prop.Id.ToString());
        if (requirement == null)
        {
            requirement = new Requirement
            {
                BrokerId = brokerId,
                RawMessageText = prop.Title,
                RequirementType = "DEMAND",
                PropertyType = prop.Category,
                Budget = prop.BudgetMax,
                Status = "active",
                ContentHash = prop.Id.ToString(),
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.Requirements.Add(requirement);
            await _dbContext.SaveChangesAsync();
        }
        return requirement;
    }
}
