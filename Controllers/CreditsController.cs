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

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/credits")]
public class CreditsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<CreditsController> _logger;

    public CreditsController(AppDbContext dbContext, ILogger<CreditsController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpPost("grant-monthly")]
    [RequireInternalServiceKey]
    public async Task<IActionResult> GrantMonthlyCredits()
    {
        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var activeBrokers = await _dbContext.Brokers
                .Where(b => b.Status == "active")
                .ToListAsync();

            var count = 0;
            foreach (var broker in activeBrokers)
            {
                var wallet = await _dbContext.CreditWallets
                    .FirstOrDefaultAsync(w => w.BrokerId == broker.Id);

                if (wallet == null)
                {
                    wallet = new CreditWallet
                    {
                        BrokerId = broker.Id,
                        FreeCreditsBalance = 10,
                        PaidCreditsBalance = 0,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.CreditWallets.Add(wallet);
                }
                else
                {
                    wallet.FreeCreditsBalance = 10;
                    wallet.FreeCreditsResetAt = DateTime.UtcNow.AddMonths(1);
                    wallet.UpdatedAt = DateTime.UtcNow;
                    _dbContext.CreditWallets.Update(wallet);
                }

                // Grant transaction log
                var grantTx = new CreditTransaction
                {
                    BrokerId = broker.Id,
                    Type = "grant",
                    Amount = 10,
                    BalanceAfter = 10 + wallet.PaidCreditsBalance,
                    ReferenceType = "monthly_grant",
                    Notes = "Monthly reset of free credits to 10 balance",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.CreditTransactions.Add(grantTx);

                _dbContext.Brokers.Update(broker);

                count++;
            }

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new
            {
                success = true,
                message = $"Successfully reset monthly free credits to 10 for {count} active brokers.",
                reset_count = count
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
            .Where(cp => cp.Active)
            .OrderBy(cp => cp.Price)
            .Select(cp => new
            {
                id = cp.Id,
                name = cp.Name,
                credits = cp.Credits,
                price = cp.Price
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
        if (request.BrokerId <= 0 || request.Amount <= 0)
        {
            return BadRequest(new { success = false, message = "Valid broker_id and amount are required." });
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var wallet = await _dbContext.CreditWallets
                .FirstOrDefaultAsync(w => w.BrokerId == request.BrokerId);

            if (wallet == null)
            {
                return NotFound(new { success = false, message = "Credit wallet not found." });
            }

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

            // Perform deduction (Free first, then Paid)
            var amountToDeduct = request.Amount;
            if (wallet.FreeCreditsBalance >= amountToDeduct)
            {
                wallet.FreeCreditsBalance -= amountToDeduct;
            }
            else
            {
                amountToDeduct -= wallet.FreeCreditsBalance;
                wallet.FreeCreditsBalance = 0;
                wallet.PaidCreditsBalance -= amountToDeduct;
            }

            wallet.UpdatedAt = DateTime.UtcNow;
            _dbContext.CreditWallets.Update(wallet);

            // Log ledger entry
            var ledgerTx = new CreditTransaction
            {
                BrokerId = request.BrokerId,
                Type = "debit",
                Amount = request.Amount,
                BalanceAfter = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance,
                ReferenceType = "reveal",
                Notes = request.Notes ?? "Internal credit deduction",
                CreatedAt = DateTime.UtcNow
            };
            _dbContext.CreditTransactions.Add(ledgerTx);

            // Sync legacy broker credit balance column
            var broker = await _dbContext.Brokers.FirstOrDefaultAsync(b => b.Id == request.BrokerId);
            if (broker != null)
            {
                _dbContext.Brokers.Update(broker);
            }

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
