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

        // Check if there is an unlocked reveal between these two brokers
        var isUnlocked = await _dbContext.Reveals.AnyAsync(r =>
            (r.Match!.Listing!.BrokerId == callingBrokerId && r.Match.Requirement!.BrokerId == brokerId) ||
            (r.Match!.Requirement!.BrokerId == callingBrokerId && r.Match.Listing!.BrokerId == brokerId));

        var wallet = await _dbContext.CreditWallets.AsNoTracking().FirstOrDefaultAsync(w => w.BrokerId == brokerId);
        var freeCredits = wallet?.FreeCreditsBalance ?? 0;
        var paidCredits = wallet?.PaidCreditsBalance ?? 0;

        if (isUnlocked)
        {
            var details = new BrokerDetailsResponseDto
            {
                BrokerId = targetBroker.Id,
                Name = targetBroker.Name ?? "N/A",
                Phone = targetBroker.PhoneNumber,
                Locality = targetBroker.Locality ?? "N/A",
                BrokerageName = targetBroker.BrokerageName ?? "N/A",
                Status = targetBroker.Status ?? "active",
                ResponseScore = targetBroker.ResponseScore ?? 100.00m,
                ConfirmationComplianceRate = targetBroker.ConfirmationComplianceRate,
                VisibilityPenaltyFlag = targetBroker.VisibilityPenaltyFlag,
                FreeCreditsBalance = freeCredits,
                PaidCreditsBalance = paidCredits
            };
            return Ok(details);
        }
        else
        {
            // Mask the phone number to preserve privacy until unlocked
            var phone = targetBroker.PhoneNumber;
            var maskedPhone = phone.Length >= 5 ? phone.Substring(0, 5) + "XXXXX" : "XXXXX";

            var details = new BrokerDetailsResponseDto
            {
                BrokerId = targetBroker.Id,
                Name = targetBroker.Name ?? "N/A",
                Phone = maskedPhone,
                Locality = targetBroker.Locality ?? "N/A",
                BrokerageName = targetBroker.BrokerageName ?? "N/A",
                Status = targetBroker.Status ?? "active",
                ResponseScore = targetBroker.ResponseScore ?? 100.00m,
                ConfirmationComplianceRate = targetBroker.ConfirmationComplianceRate,
                VisibilityPenaltyFlag = targetBroker.VisibilityPenaltyFlag,
                FreeCreditsBalance = freeCredits,
                PaidCreditsBalance = paidCredits
            };
            return Ok(details);
        }
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

        broker.LastActiveAt = DateTime.UtcNow;

        _dbContext.Brokers.Update(broker);
        _dbContext.Users.Update(callingUser);
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
            PaidCreditsBalance = paidCredits
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
            free_credits_reset_at = resetDate
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
    public async Task<IActionResult> GetBrokerNotifications([FromRoute] int brokerId)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != brokerId)
        {
            return Unauthorized(new { message = "You can only view your own notifications." });
        }

        var dbNotifications = await _dbContext.BrokerNotifications
            .AsNoTracking()
            .Where(n => n.BrokerId == brokerId)
            .OrderByDescending(n => n.CreatedAt)
            .ToListAsync();

        var notifications = dbNotifications.Select(n => new
        {
            id = n.Id,
            type = n.Type,
            channel = n.Channel,
            payload = string.IsNullOrEmpty(n.PayloadJson) ? (object?)null : JsonSerializer.Deserialize<object>(n.PayloadJson, (JsonSerializerOptions?)null),
            channel_status = n.ChannelStatus,
            read_at = n.ReadAt,
            created_at = n.CreatedAt
        }).ToList();

        return Ok(new
        {
            notifications = notifications
        });
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
}
