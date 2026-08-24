using System;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.DTOs.Notifications;
using PropSeekr.Models;

namespace PropSeekr.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/brokers")]
public class BrokersController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public BrokersController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> RegisterBroker([FromBody] RegisterBrokerRequestDto request)
    {
        if (string.IsNullOrWhiteSpace(request.Phone))
        {
            return BadRequest(new { message = "Phone number is required." });
        }

        var existingBroker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.PhoneNumber == request.Phone);
        if (existingBroker != null)
        {
            return Conflict(new { message = "Broker with this phone number is already registered." });
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var broker = new Broker
            {
                Name = request.Name,
                PhoneNumber = request.Phone,
                Locality = request.Locality,
                BrokerageName = request.BrokerageName,
                Status = "active",
                CreditBalance = 10,
                CreatedAt = DateTime.UtcNow,
                LastActiveAt = DateTime.UtcNow
            };

            _dbContext.Brokers.Add(broker);
            await _dbContext.SaveChangesAsync();

            var wallet = new CreditWallet
            {
                BrokerId = broker.Id,
                FreeCreditsBalance = 10,
                PaidCreditsBalance = 0,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            _dbContext.CreditWallets.Add(wallet);
            await _dbContext.SaveChangesAsync();

            var grantTx = new CreditTransaction
            {
                BrokerId = broker.Id,
                Type = "grant",
                Amount = 10,
                BalanceAfter = 10,
                ReferenceType = "monthly_grant",
                Notes = "Initial broker registration free grant",
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.CreditTransactions.Add(grantTx);
            await _dbContext.SaveChangesAsync();

            await transaction.CommitAsync();

            var response = new RegisterBrokerResponseDto
            {
                BrokerId = broker.Id,
                FreeCreditsBalance = wallet.FreeCreditsBalance,
                Status = broker.Status ?? "active"
            };

            return Ok(response);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpGet("{brokerId}")]
    public async Task<IActionResult> GetBrokerDetails([FromRoute] int brokerId)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callingUser = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
        if (callingUser == null)
        {
            return NotFound(new { message = "Calling user profile not found." });
        }

        if (!callingUser.BrokerId.HasValue)
        {
            return BadRequest(new { message = "Your profile is not associated with a broker record." });
        }

        var callingBrokerId = callingUser.BrokerId.Value;

        var targetBroker = await _dbContext.Brokers.AsNoTracking().FirstOrDefaultAsync(b => b.Id == brokerId);
        if (targetBroker == null)
        {
            return NotFound(new { message = "Target broker not found." });
        }

        var isUnlocked = await _dbContext.Reveals.AnyAsync(r =>
            (r.Match!.Listing!.BrokerId == callingBrokerId && r.Match.Requirement!.BrokerId == brokerId) ||
            (r.Match!.Requirement!.BrokerId == callingBrokerId && r.Match.Listing!.BrokerId == brokerId));

        var targetUser = await _dbContext.Users.AsNoTracking().FirstOrDefaultAsync(u => u.BrokerId == brokerId);

        var wallet = await _dbContext.CreditWallets.AsNoTracking().FirstOrDefaultAsync(w => w.BrokerId == brokerId);
        var freeCredits = wallet?.FreeCreditsBalance ?? 0;
        var paidCredits = wallet?.PaidCreditsBalance ?? 0;

        var canViewPrivateDetails = callingBrokerId == brokerId || isUnlocked;
        var phone = targetBroker.PhoneNumber;
        var displayPhone = canViewPrivateDetails
            ? phone
            : phone.Length >= 5
                ? phone[..5] + "XXXXX"
                : "XXXXX";

        var details = new BrokerDetailsResponseDto
        {
            BrokerId = targetBroker.Id,
            Name = targetBroker.Name ?? "N/A",
            Phone = displayPhone,
            Locality = targetBroker.Locality ?? "N/A",
            BrokerageName = targetBroker.BrokerageName ?? "N/A",
            Status = targetBroker.Status ?? "active",
            ResponseScore = targetBroker.ResponseScore ?? 100.00m,
            ConfirmationComplianceRate = targetBroker.ConfirmationComplianceRate,
            VisibilityPenaltyFlag = targetBroker.VisibilityPenaltyFlag,
            FreeCreditsBalance = freeCredits,
            PaidCreditsBalance = paidCredits,
            Email = canViewPrivateDetails ? targetUser?.Email : null,
            CompanyGst = canViewPrivateDetails ? targetUser?.GSTNumber : null,
            CompanyAddress = canViewPrivateDetails ? targetUser?.AddressLine1 : null,
            ProfilePhotoUrl = canViewPrivateDetails ? targetUser?.ProfilePhotoUrl : null
        };
        return Ok(details);
    }

    [HttpPatch("{brokerId}")]
    public async Task<IActionResult> UpdateBrokerProfile([FromRoute] int brokerId, [FromBody] UpdateBrokerRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callingUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callingUser == null)
        {
            return NotFound(new { message = "Calling user profile not found." });
        }

        if (!callingUser.BrokerId.HasValue || callingUser.BrokerId.Value != brokerId)
        {
            return Unauthorized(new { message = "You can only update your own broker profile." });
        }

        var broker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.Id == brokerId);
        if (broker == null)
        {
            return NotFound(new { message = "Broker profile not found." });
        }

        // Apply changes
        if (request.Name != null)
        {
            broker.Name = request.Name;
            callingUser.Name = request.Name;
        }

        var phoneToUpdate = request.MobileNumber ?? request.Phone;
        if (phoneToUpdate != null)
        {
            // Verify unique phone number
            var existingPhone = await _dbContext.Brokers.AnyAsync(b => b.PhoneNumber == phoneToUpdate && b.Id != brokerId);
            if (existingPhone)
            {
                return Conflict(new { message = "Another broker is already registered with this phone number." });
            }
            broker.PhoneNumber = phoneToUpdate;
            callingUser.MobileNumber = phoneToUpdate;
        }

        if (request.Locality != null)
        {
            broker.Locality = request.Locality;
        }

        if (request.BrokerageName != null)
        {
            broker.BrokerageName = request.BrokerageName;
        }

        if (request.Email != null)
        {
            var normalizedEmail = request.Email.Trim();
            var emailInUse = await _dbContext.Users.AnyAsync(user =>
                user.Id != callingUser.Id && user.Email == normalizedEmail);
            if (emailInUse)
            {
                return Conflict(new { message = "Another account is already registered with this email address." });
            }

            callingUser.Email = normalizedEmail;
        }

        if (request.CompanyGst != null)
        {
            callingUser.GSTNumber = request.CompanyGst;
        }

        if (request.CompanyAddress != null)
        {
            callingUser.AddressLine1 = request.CompanyAddress;
        }

        if (request.ProfilePhotoUrl != null)
        {
            callingUser.ProfilePhotoUrl = request.ProfilePhotoUrl;
        }

        broker.LastActiveAt = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync();

        var wallet = await _dbContext.CreditWallets.AsNoTracking().FirstOrDefaultAsync(w => w.BrokerId == brokerId);
        var freeCredits = wallet?.FreeCreditsBalance ?? 0;
        var paidCredits = wallet?.PaidCreditsBalance ?? 0;

        var details = new BrokerDetailsResponseDto
        {
            BrokerId = broker.Id,
            Name = broker.Name ?? "N/A",
            Phone = broker.PhoneNumber,
            Locality = broker.Locality ?? "N/A",
            BrokerageName = broker.BrokerageName ?? "N/A",
            Status = broker.Status ?? "active",
            ResponseScore = broker.ResponseScore ?? 100.00m,
            ConfirmationComplianceRate = broker.ConfirmationComplianceRate,
            VisibilityPenaltyFlag = broker.VisibilityPenaltyFlag,
            FreeCreditsBalance = freeCredits,
            PaidCreditsBalance = paidCredits,
            Email = callingUser.Email,
            CompanyGst = callingUser.GSTNumber,
            CompanyAddress = callingUser.AddressLine1,
            ProfilePhotoUrl = callingUser.ProfilePhotoUrl
        };

        return Ok(details);
    }

    [HttpGet("{brokerId}/matches")]
    public async Task<IActionResult> GetBrokerMatches([FromRoute] int brokerId, [FromQuery] string? state)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != brokerId)
        {
            return Unauthorized(new { message = "You can only view matches for your own broker profile." });
        }

        var query = _dbContext.Matches
            .Include(m => m.Listing)
            .Include(m => m.Requirement)
            .Where(m => m.ListingBrokerId == brokerId || m.RequirementBrokerId == brokerId);

        if (!string.IsNullOrEmpty(state))
        {
            query = query.Where(m => m.State == state);
        }

        var matches = await query.ToListAsync();

        var matchResponses = matches.Select(m =>
        {
            object counterpartySummary;
            if (brokerId == m.ListingBrokerId)
            {
                // Counterparty is Requirement
                counterpartySummary = new
                {
                    locality = m.Requirement?.PropertyType ?? "Residential",
                    budget = m.Requirement?.Budget.HasValue == true ? $"{m.Requirement.Budget.Value} {m.Requirement.BudgetUnit ?? "INR"}" : "N/A"
                };
            }
            else
            {
                // Counterparty is Listing
                counterpartySummary = new
                {
                    locality = m.Listing?.ProjectName ?? "Vijay Nagar",
                    budget = m.Listing?.Price.HasValue == true ? $"{m.Listing.Price.Value}" : "N/A"
                };
            }

            return new
            {
                match_id = m.Id,
                state = m.State,
                counterparty_summary = counterpartySummary,
                created_at = m.CreatedAt
            };
        }).ToList();

        return Ok(new
        {
            matches = matchResponses
        });
    }

    [HttpGet("{brokerId}/wallet")]
    public async Task<IActionResult> GetBrokerWallet([FromRoute] int brokerId)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != brokerId)
        {
            return Unauthorized(new { message = "You can only query your own credit wallet." });
        }

        var wallet = await _dbContext.CreditWallets.AsNoTracking().FirstOrDefaultAsync(w => w.BrokerId == brokerId);
        if (wallet == null)
        {
            return NotFound(new { success = false, message = "Credit wallet not found." });
        }

        var resetDate = wallet.FreeCreditsResetAt ?? new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1).AddMonths(1);

        return Ok(new
        {
            free_credits_balance = wallet.FreeCreditsBalance,
            paid_credits_balance = wallet.PaidCreditsBalance,
            total_credits_balance = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance,
            free_credits_reset_at = resetDate,
            updated_at = wallet.UpdatedAt
        });
    }

    [HttpGet("{brokerId}/credit-transactions")]
    public async Task<IActionResult> GetBrokerCreditTransactions([FromRoute] int brokerId, [FromQuery] int page = 1, [FromQuery] int limit = 20)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != brokerId)
        {
            return Unauthorized(new { message = "You can only query your own credit transaction ledger." });
        }

        if (page < 1) page = 1;
        if (limit < 1 || limit > 100) limit = 20;

        var query = _dbContext.CreditTransactions
            .AsNoTracking()
            .Where(t => t.BrokerId == brokerId)
            .OrderByDescending(t => t.CreatedAt);

        var totalCount = await query.CountAsync();
        var transactions = await query
            .Skip((page - 1) * limit)
            .Take(limit)
            .Select(t => new
            {
                id = t.Id,
                type = t.Type,
                amount = t.Amount,
                balance_after = t.BalanceAfter,
                reference_type = t.ReferenceType,
                notes = t.Notes,
                created_at = t.CreatedAt
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            total_count = totalCount,
            page = page,
            limit = limit,
            transactions = transactions
        });
    }

    [HttpGet("{brokerId}/notifications")]
    public async Task<IActionResult> GetBrokerNotifications(
        [FromRoute] int brokerId,
        [FromQuery] int page = 1,
        [FromQuery] int limit = 20,
        [FromQuery] string filter = "ALL")
    {
        if (!await CallerOwnsBrokerAsync(brokerId))
        {
            return Unauthorized(new { message = "You can only view your own notifications." });
        }

        page = Math.Max(1, page);
        limit = Math.Clamp(limit, 1, 100);
        var normalizedFilter = filter.Trim().ToUpperInvariant();

        var query = _dbContext.BrokerNotifications
            .AsNoTracking()
            .Where(notification => notification.BrokerId == brokerId);

        query = normalizedFilter switch
        {
            "UNREAD" => query.Where(notification => notification.ReadAt == null),
            "MATCHES" => query.Where(notification => notification.Type == "match_found"),
            "BROKER_REQUESTS" => query.Where(notification =>
                notification.Type.StartsWith("confirm_")),
            _ => query
        };

        var totalCount = await query.CountAsync();
        var unreadCount = await _dbContext.BrokerNotifications
            .AsNoTracking()
            .CountAsync(notification => notification.BrokerId == brokerId && notification.ReadAt == null);
        var dbNotifications = await query
            .OrderByDescending(notification => notification.CreatedAt)
            .ThenByDescending(notification => notification.Id)
            .Skip((page - 1) * limit)
            .Take(limit)
            .ToListAsync();
        var requestIds = dbNotifications
            .Where(notification => notification.ConnectionRequestId.HasValue)
            .Select(notification => notification.ConnectionRequestId!.Value)
            .Distinct()
            .ToArray();
        var requestStatuses = await _dbContext.MatchConnectionRequests
            .AsNoTracking()
            .Where(request => requestIds.Contains(request.Id))
            .ToDictionaryAsync(request => request.Id, request => request.Status);

        var notifications = dbNotifications.Select(notification =>
        {
            var matchId = ReadMatchId(notification.PayloadJson);
            var apiType = ApiNotificationType(notification.Type);
            var content = NotificationContent(notification.Type);
            var requestId = notification.ConnectionRequestId;
            var actionStatus = requestId.HasValue && requestStatuses.TryGetValue(requestId.Value, out var status)
                ? status
                : null;
            return new
            {
                notificationId = notification.Id.ToString(),
                type = apiType,
                title = content.Title,
                message = content.Message,
                isRead = notification.ReadAt.HasValue,
                createdAt = notification.CreatedAt,
                channelStatus = notification.ChannelStatus,
                actionStatus,
                meta = new { matchId, requestId }
            };
        }).ToList();

        return Ok(new
        {
            success = true,
            unreadCount,
            totalCount,
            page,
            limit,
            data = notifications
        });
    }

    [HttpPatch("{brokerId}/notifications/{notificationId:long}/read")]
    public async Task<IActionResult> MarkBrokerNotificationRead(
        [FromRoute] int brokerId,
        [FromRoute] long notificationId)
    {
        if (!await CallerOwnsBrokerAsync(brokerId))
        {
            return Unauthorized(new { message = "You can only update your own notifications." });
        }

        var notification = await _dbContext.BrokerNotifications
            .SingleOrDefaultAsync(item => item.Id == notificationId && item.BrokerId == brokerId);
        if (notification is null)
        {
            return NotFound(new { success = false, message = "Notification not found." });
        }

        if (!notification.ReadAt.HasValue)
        {
            notification.ReadAt = DateTime.UtcNow;
            notification.ChannelStatus = "read";
            await _dbContext.SaveChangesAsync();
        }

        var unreadCount = await _dbContext.BrokerNotifications
            .CountAsync(item => item.BrokerId == brokerId && item.ReadAt == null);
        return Ok(new { success = true, notificationId = notification.Id.ToString(), unreadCount });
    }

    [HttpPost("{brokerId}/notifications/mark-all-read")]
    public async Task<IActionResult> MarkAllBrokerNotificationsRead([FromRoute] int brokerId)
    {
        if (!await CallerOwnsBrokerAsync(brokerId))
        {
            return Unauthorized(new { message = "You can only update your own notifications." });
        }

        var now = DateTime.UtcNow;
        var unread = await _dbContext.BrokerNotifications
            .Where(item => item.BrokerId == brokerId && item.ReadAt == null)
            .ToListAsync();
        foreach (var notification in unread)
        {
            notification.ReadAt = now;
            notification.ChannelStatus = "read";
        }
        await _dbContext.SaveChangesAsync();

        return Ok(new { success = true, unreadCount = 0 });
    }

    [HttpGet("{brokerId}/notification-preferences")]
    public async Task<IActionResult> GetBrokerNotificationPreferences([FromRoute] int brokerId)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != brokerId)
        {
            return Unauthorized(new { message = "You can only query your own notification preferences." });
        }

        var pref = await _dbContext.NotificationPreferences
            .FirstOrDefaultAsync(p => p.BrokerId == brokerId);

        if (pref == null)
        {
            pref = new NotificationPreference
            {
                BrokerId = brokerId,
                InAppEnabled = true,
                WhatsappEnabled = true,
                ReminderFrequencyCapHours = 4,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.NotificationPreferences.Add(pref);
            await _dbContext.SaveChangesAsync();
        }

        return Ok(new
        {
            whatsapp_enabled = pref.WhatsappEnabled,
            in_app_enabled = pref.InAppEnabled,
            reminder_frequency_cap_hours = pref.ReminderFrequencyCapHours
        });
    }

    [HttpPatch("{brokerId}/notification-preferences")]
    public async Task<IActionResult> UpdateBrokerNotificationPreferences([FromRoute] int brokerId, [FromBody] UpdateNotificationPreferencesRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != brokerId)
        {
            return Unauthorized(new { message = "You can only modify your own notification preferences." });
        }

        var pref = await _dbContext.NotificationPreferences
            .FirstOrDefaultAsync(p => p.BrokerId == brokerId);

        if (pref == null)
        {
            pref = new NotificationPreference
            {
                BrokerId = brokerId,
                InAppEnabled = true,
                WhatsappEnabled = true,
                ReminderFrequencyCapHours = 4,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.NotificationPreferences.Add(pref);
        }

        if (request.WhatsappEnabled.HasValue)
        {
            pref.WhatsappEnabled = request.WhatsappEnabled.Value;
        }
        if (request.InAppEnabled.HasValue)
        {
            pref.InAppEnabled = request.InAppEnabled.Value;
        }
        if (request.ReminderFrequencyCapHours.HasValue)
        {
            pref.ReminderFrequencyCapHours = request.ReminderFrequencyCapHours.Value;
        }
        pref.UpdatedAt = DateTime.UtcNow;

        _dbContext.NotificationPreferences.Update(pref);
        await _dbContext.SaveChangesAsync();

        return Ok(new
        {
            whatsapp_enabled = pref.WhatsappEnabled,
            in_app_enabled = pref.InAppEnabled,
            reminder_frequency_cap_hours = pref.ReminderFrequencyCapHours
        });
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out userId);
    }

    private async Task<bool> CallerOwnsBrokerAsync(int brokerId)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return false;
        }

        return await _dbContext.Users
            .AsNoTracking()
            .AnyAsync(user => user.Id == userId && user.BrokerId == brokerId);
    }

    private static int? ReadMatchId(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.TryGetProperty("match_id", out var matchId) && matchId.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ApiNotificationType(string type) => type switch
    {
        "match_found" => "MATCH",
        "confirm_pending" => "BROKER_UNLOCK",
        "confirm_accepted" => "BROKER_ACCEPTED",
        "confirm_rejected" => "BROKER_REJECTED",
        "confirm_expired_resend" => "BROKER_REQUEST",
        _ => "SYSTEM"
    };

    private static (string Title, string Message) NotificationContent(string type) => type switch
    {
        "match_found" => ("New Property Match", "A new property match is available. Open Matches to review it."),
        "confirm_pending" => ("Match Unlock Request", "Another broker wants to connect. Open the match to review and accept."),
        "confirm_accepted" => ("Connection Request Accepted", "Your request has been accepted. You can now connect with the other broker."),
        "confirm_rejected" => ("Connection Request Declined", "The other broker declined this connection request. No tokens were deducted."),
        "confirm_expired_resend" => ("Confirmation Window Expired", "The previous confirmation expired. Open the match to confirm again."),
        "confirm_expired_counterparty" => ("Unlock Request Expired", "The other broker did not confirm within the four-hour window."),
        _ => ("PropSeekr Update", "Open PropSeekr to view this update.")
    };
}
