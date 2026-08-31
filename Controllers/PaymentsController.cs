using System;
using System.IO;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using PropSeekr.Data;
using PropSeekr.DTOs.Payment;
using PropSeekr.Models;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/payments")]
public class PaymentsController : ControllerBase
{
    private readonly AppDbContext _dbContext;
    private readonly ILogger<PaymentsController> _logger;

    public PaymentsController(AppDbContext dbContext, ILogger<PaymentsController> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    [HttpPost("initiate")]
    [Obsolete("Use POST /api/v1/payment/order instead.")]
    public IActionResult InitiatePayment()
    {
        return StatusCode(StatusCodes.Status410Gone, new
        {
            success = false,
            message = "Legacy payment initiation is retired. Use canonical Razorpay routes at POST /api/v1/payment/order and POST /api/v1/payment/verify."
        });
    }

    [HttpPost("webhook")]
    [AllowAnonymous] // Callback endpoint from gateway
    public async Task<IActionResult> Webhook([FromBody] JsonElement json)
    {
        int paymentId = 0;
        string? status = null;
        string? gatewayTxnId = null;

        if (json.TryGetProperty("payment_id", out var pIdProp) && pIdProp.TryGetInt32(out var pId))
        {
            paymentId = pId;
        }
        else if (json.TryGetProperty("paymentId", out var pIdProp2) && pIdProp2.TryGetInt32(out var pId2))
        {
            paymentId = pId2;
        }

        if (json.TryGetProperty("status", out var statusProp))
        {
            status = statusProp.GetString();
        }

        if (json.TryGetProperty("gateway_txn_id", out var txnProp))
        {
            gatewayTxnId = txnProp.GetString();
        }

        if (paymentId == 0)
        {
            return BadRequest(new { success = false, message = "payment_id is required." });
        }

        using var transaction = await _dbContext.Database.BeginTransactionAsync();
        try
        {
            var payment = await _dbContext.Payments
                .Include(p => p.CreditPack)
                .FirstOrDefaultAsync(p => p.Id == paymentId);

            if (payment == null)
            {
                return NotFound(new { success = false, message = "Payment record not found." });
            }

            if (payment.Status == "success")
            {
                return Ok(new { success = true, message = "Payment already processed." });
            }

            if (!string.IsNullOrEmpty(status))
            {
                payment.Status = status.ToLower();
            }
            if (!string.IsNullOrEmpty(gatewayTxnId))
            {
                payment.GatewayTxnId = gatewayTxnId;
            }
            payment.UpdatedAt = DateTime.UtcNow;

            _dbContext.Payments.Update(payment);

            if (payment.Status == "success" && payment.CreditPack != null)
            {
                var wallet = await _dbContext.CreditWallets.FirstOrDefaultAsync(w => w.BrokerId == payment.BrokerId);
                if (wallet == null)
                {
                    wallet = new CreditWallet
                    {
                        BrokerId = payment.BrokerId,
                        FreeCreditsBalance = 0,
                        PaidCreditsBalance = payment.CreditPack.Credits,
                        CreatedAt = DateTime.UtcNow,
                        UpdatedAt = DateTime.UtcNow
                    };
                    _dbContext.CreditWallets.Add(wallet);
                }
                else
                {
                    wallet.PaidCreditsBalance += payment.CreditPack.Credits;
                    wallet.UpdatedAt = DateTime.UtcNow;
                    _dbContext.CreditWallets.Update(wallet);
                }

                await _dbContext.SaveChangesAsync();

                // Log ledger transaction
                var ledgerTx = new CreditTransaction
                {
                    BrokerId = payment.BrokerId,
                    Type = "purchase",
                    Amount = payment.CreditPack.Credits,
                    BalanceAfter = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance,
                    ReferenceType = "payment",
                    ReferenceId = payment.Id,
                    Notes = $"Purchase of credit pack: {payment.CreditPack.Name}",
                    CreatedAt = DateTime.UtcNow
                };
                _dbContext.CreditTransactions.Add(ledgerTx);

            }

            await _dbContext.SaveChangesAsync();
            await transaction.CommitAsync();

            return Ok(new { success = true, message = $"Payment status updated to '{payment.Status}' successfully." });
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync();
            _logger.LogError(ex, "Failed to process webhook for payment {PaymentId}", paymentId);
            return BadRequest(new { success = false, message = ex.Message });
        }
    }

    [HttpGet("{paymentId}")]
    [Authorize]
    public async Task<IActionResult> GetPaymentDetails([FromRoute] int paymentId)
    {
        var payment = await _dbContext.Payments
            .Include(p => p.CreditPack)
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == paymentId);

        if (payment == null)
        {
            return NotFound(new { success = false, message = "Payment not found." });
        }

        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        var callerUser = await _dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (callerUser == null || !callerUser.BrokerId.HasValue || callerUser.BrokerId.Value != payment.BrokerId)
        {
            return Unauthorized(new { message = "You can only view details of your own payments." });
        }

        return Ok(new
        {
            success = true,
            data = new
            {
                payment_id = payment.Id,
                broker_id = payment.BrokerId,
                credit_pack = payment.CreditPack == null ? null : new { id = payment.CreditPack.Id, name = payment.CreditPack.Name, credits = payment.CreditPack.Credits },
                amount = payment.Amount,
                currency = payment.Currency,
                gateway = payment.Gateway,
                gateway_txn_id = payment.GatewayTxnId,
                status = payment.Status,
                created_at = payment.CreatedAt,
                updated_at = payment.UpdatedAt
            }
        });
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        var userIdClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(userIdClaim, out userId);
    }
}
