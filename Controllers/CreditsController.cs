using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Attributes;
using PropSeekr.Data;
using PropSeekr.DTOs.Matches;
using PropSeekr.Models;
using PropSeekr.Services;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/credits")]
public class CreditsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<CreditsController> _logger;
    private readonly IWalletAccountingService _walletAccounting;

    public CreditsController(AppDbContext dbContext, ILogger<CreditsController> logger, IWalletAccountingService walletAccounting)
    {
        _dbContext = dbContext;
        _logger = logger;
        _walletAccounting = walletAccounting;
    }

    [HttpPost("grant-monthly")]
    [RequireInternalServiceKey]
    public async Task<IActionResult> GrantMonthlyCredits([FromQuery] int batchSize = 200)
    {
        batchSize = Math.Clamp(batchSize, 1, 500);
        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var now = DateTime.UtcNow;
            var periodKey = WalletPeriod.For(now).Key;
            var dueBrokerIds = await _dbContext.CreditWallets
                .Where(wallet => wallet.FreeCreditsResetAt.HasValue && wallet.FreeCreditsResetAt <= now)
                .Where(wallet => _dbContext.Brokers.Any(broker => broker.Id == wallet.BrokerId &&
                    broker.Status != null && broker.Status.ToUpper() == "ACTIVE"))
                .Where(wallet => _dbContext.Users.Any(user => user.BrokerId == wallet.BrokerId &&
                    user.IsEmailVerified && user.IsMobileVerified))
                .OrderBy(wallet => wallet.BrokerId)
                .Select(wallet => wallet.BrokerId)
                .Take(batchSize)
                .ToListAsync();

            var wallets = await _walletAccounting.LockAsync(dueBrokerIds);
            var count = 0;
            foreach (var wallet in wallets)
            {
                if (await _walletAccounting.SettleFreeCreditsAsync(wallet, now)) count++;
            }

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new
            {
                success = true,
                message = $"Applied the {periodKey} free-credit grant to {count} active brokers.",
                reset_count = count,
                batch_size = batchSize,
                examined_count = wallets.Count
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to run monthly credits grant cron.");
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("~/api/v1/credit-packs")]
    [Authorize] // Expose to clients
    public async Task<IActionResult> GetCreditPacks()
    {
        var packs = await _dbContext.CreditPacks
            .AsNoTracking()
            .Where(cp => cp.Active && cp.EffectiveFrom <= DateTime.UtcNow &&
                (!cp.EffectiveTo.HasValue || cp.EffectiveTo > DateTime.UtcNow))
            .OrderBy(cp => cp.AmountInPaise)
            .Select(cp => new
            {
                id = cp.Id,
                name = cp.Name,
                code = cp.Code,
                version = cp.Version,
                credits = cp.Credits,
                price = cp.Price,
                amount_in_paise = cp.AmountInPaise,
                currency = cp.Currency
            })
            .ToListAsync();

        return Ok(new
        {
            success = true,
            packs = packs
        });
    }

    [HttpPost("deduct")]
    [RequireInternalServiceKey]
    public async Task<IActionResult> DeductCredits([FromBody] DeductCreditsRequestDto request)
    {
        if (request.BrokerId <= 0 || request.Amount <= 0 ||
            string.IsNullOrWhiteSpace(request.OperationKey) || string.IsNullOrWhiteSpace(request.Reason))
        {
            return BadRequest(new { success = false, message = "Valid broker_id, amount, operation_key, and reason are required." });
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var wallet = (await _walletAccounting.LockAsync(new[] { request.BrokerId })).SingleOrDefault();

            if (wallet == null)
            {
                return NotFound(new { success = false, message = "Credit wallet not found." });
            }

            var operationKey = request.OperationKey.Trim();
            var existing = await _dbContext.CreditTransactions.AsNoTracking()
                .SingleOrDefaultAsync(item =>
                    item.BrokerId == request.BrokerId &&
                    item.ReferenceType == "adjustment_debit" &&
                    item.ReferenceKey == operationKey);
            if (existing is not null)
            {
                await transaction.CommitAsync();
                return Ok(new
                {
                    success = true,
                    idempotent_replay = true,
                    broker_id = request.BrokerId,
                    free_credits_balance = wallet.FreeCreditsBalance,
                    paid_credits_balance = wallet.PaidCreditsBalance
                });
            }

            var now = DateTime.UtcNow;
            await _walletAccounting.SettleFreeCreditsAsync(wallet, now);
            var totalAvailable = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance;
            if (totalAvailable < request.Amount)
            {
                return BadRequest(new
                {
                    error = "insufficient_credits",
                    broker_id = request.BrokerId,
                    required = request.Amount,
                    available = totalAvailable
                });
            }

            _walletAccounting.Debit(wallet, request.Amount, "adjustment_debit", null,
                operationKey, request.Reason.Trim(), now, "adjustment_debit");

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new
            {
                success = true,
                broker_id = request.BrokerId,
                free_credits_balance = wallet.FreeCreditsBalance,
                paid_credits_balance = wallet.PaidCreditsBalance
            });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to deduct credits for broker {BrokerId}", request.BrokerId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }
}
