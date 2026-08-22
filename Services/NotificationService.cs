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
            var meta = !string.IsNullOrEmpty(notification.MetaJson)
                ? JsonSerializer.Deserialize<NotificationMetaDto>(notification.MetaJson)
                : null;

            if (meta != null && Guid.TryParse(meta.InitiatorUserId, out var initUserId) && 
                Guid.TryParse(meta.InitiatorPropertyRequestId, out var initPropId) && 
                Guid.TryParse(meta.TargetPropertyRequestId, out var targetPropId))
            {
                var userB = user; // Current user receiving the notification
                var userA = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == initUserId);

                if (userA == null)
                {
                    throw new KeyNotFoundException("Requesting user not found.");
                }

                if (userA.Credits < 1)
                {
                    throw new InvalidOperationException("The requesting user has insufficient tokens to complete the unlock.");
                }

                if (userB.Credits < 1)
                {
                    throw new InvalidOperationException("Insufficient tokens. You need at least 1 token to unlock this contact.");
                }

                using var transaction = await _dbContext.Database.BeginTransactionAsync();
                try
                {
                    // Deduct 1 credit from both
                    userA.Credits -= 1;
                    userB.Credits -= 1;

                    notification.IsContactUnlocked = true;

                    // Add unlock records for both
                    var unlockForUserA = new UnlockedProperty
                    {
                        UserId = initUserId,
                        PropertyRequestId = targetPropId,
                        UnlockedAt = DateTime.UtcNow
                    };

                    var unlockForUserB = new UnlockedProperty
                    {
                        UserId = userId,
                        PropertyRequestId = initPropId,
                        UnlockedAt = DateTime.UtcNow
                    };

                    _dbContext.UnlockedProperties.Add(unlockForUserA);
                    _dbContext.UnlockedProperties.Add(unlockForUserB);

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

                    await _dbContext.SaveChangesAsync();
                    await transaction.CommitAsync();
                }
                catch
                {
                    await transaction.RollbackAsync();
                    throw;
                }

                meta.BrokerPhone = userA.MobileNumber;
                meta.BrokerName = userA.Name;

                return new UnlockBrokerResponseDto
                {
                    Success = true,
                    Message = "Broker contact successfully unlocked. 1 Token debited.",
                    Id = notification.Id.ToString(),
                    TokensDebited = 1,
                    RemainingTokens = userB.Credits,
                    IsContactUnlocked = true,
                    Meta = meta
                };
            }
        }

        // Fallback to original single-sided unlock logic (for non-BROKER_UNLOCK type notifications)
        if (user.Credits < 1)
        {
            throw new InvalidOperationException("Insufficient tokens. You need at least 1 token to unlock this broker contact.");
        }

        using (var transaction = await _dbContext.Database.BeginTransactionAsync())
        {
            try
            {
                user.Credits -= 1;
                notification.IsContactUnlocked = true;

                await _dbContext.SaveChangesAsync();
                await transaction.CommitAsync();
            }
            catch
            {
                await transaction.RollbackAsync();
                throw;
            }
        }

        var responseMeta = !string.IsNullOrEmpty(notification.MetaJson)
            ? JsonSerializer.Deserialize<NotificationMetaDto>(notification.MetaJson)
            : new NotificationMetaDto();

        if (responseMeta != null && Guid.TryParse(responseMeta.BrokerId, out var targetId))
        {
            var broker = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == targetId);
            if (broker != null)
            {
                responseMeta.BrokerPhone = broker.MobileNumber;
                responseMeta.BrokerName = broker.Name;
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
            Meta = responseMeta
        };
    }}
