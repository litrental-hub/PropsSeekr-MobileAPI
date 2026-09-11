using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using PropSeekr.Data;
using PropSeekr.DTOs.Payment;
using PropSeekr.Models;
using PropSeekr.Services.Interfaces;

namespace PropSeekr.Services;

public class RazorpayService : IRazorpayService
{
    private readonly AppDbContext _context;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<RazorpayService> _logger;
    private readonly string _keyId;
    private readonly string _keySecret;
    private readonly string _webhookSecret;
    private readonly IWalletAccountingService _walletAccounting;

    public RazorpayService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RazorpayService> logger,
        IWalletAccountingService walletAccounting)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _walletAccounting = walletAccounting;

        _keyId = configuration["Razorpay:KeyId"] ?? throw new ArgumentNullException("Razorpay:KeyId config is missing");
        _keySecret = configuration["Razorpay:KeySecret"] ?? throw new ArgumentNullException("Razorpay:KeySecret config is missing");
        _webhookSecret = configuration["Razorpay:WebhookSecret"] ?? string.Empty;
    }

    public async Task<CreateOrderResponseDto> CreateOrderAsync(Guid userId, CreateOrderRequestDto request)
    {
        var now = DateTime.UtcNow;
        var activePacks = _context.CreditPacks.AsNoTracking().Where(pack => pack.Active &&
            pack.EffectiveFrom <= now && (!pack.EffectiveTo.HasValue || pack.EffectiveTo > now));
        CreditPack? pack;
        if (request.PackId.HasValue)
        {
            pack = await activePacks.SingleOrDefaultAsync(item => item.Id == request.PackId.Value);
        }
        else if (!string.IsNullOrWhiteSpace(request.TierId) &&
                 request.TierId.StartsWith("CREDITS_", StringComparison.OrdinalIgnoreCase) &&
                 int.TryParse(request.TierId["CREDITS_".Length..], out var legacyCredits))
        {
            var matches = await activePacks.Where(item => item.Credits == legacyCredits).Take(2).ToListAsync();
            pack = matches.Count == 1 ? matches[0] : null;
        }
        else
        {
            pack = null;
        }

        if (pack is null || pack.Credits <= 0 || pack.AmountInPaise <= 0 || string.IsNullOrWhiteSpace(pack.Currency))
            throw new ArgumentException("Select a valid active credit pack.");

        var credits = pack.Credits;
        var priceInPaise = pack.AmountInPaise;
        var receipt = $"receipt_{Guid.NewGuid().ToString("N").Substring(0, 12)}";

        // 2. Prepare payload for Razorpay Orders API
        var orderPayload = new
        {
            amount = priceInPaise,
            currency = pack.Currency,
            receipt = receipt
        };

        var jsonPayload = JsonSerializer.Serialize(orderPayload);
        var requestContent = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

        // 3. Make HTTP request to Razorpay
        var client = _httpClientFactory.CreateClient();
        
        // Basic Authentication header
        var authBytes = Encoding.ASCII.GetBytes($"{_keyId}:{_keySecret}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        _logger.LogInformation("Creating Razorpay Order. Amount: {Amount} paise, Receipt: {Receipt}", priceInPaise, receipt);

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsync("https://api.razorpay.com/v1/orders", requestContent);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to Razorpay API");
            throw new InvalidOperationException("Could not connect to payment gateway. Please try again later.", ex);
        }

        var responseContent = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError("Razorpay API returned error status {StatusCode}: {Content}", response.StatusCode, responseContent);
            throw new InvalidOperationException("Error communicating with payment gateway.");
        }

        // 4. Parse Razorpay Response
        using var doc = JsonDocument.Parse(responseContent);
        var root = doc.RootElement;
        
        if (!root.TryGetProperty("id", out var orderIdProp))
        {
            throw new InvalidOperationException("Razorpay response did not contain an order ID.");
        }

        var razorpayOrderId = orderIdProp.GetString()!;

        // 5. Store Payment Transaction in database
        var transaction = new PaymentTransaction
        {
            UserId = userId,
            RazorpayOrderId = razorpayOrderId,
            AmountInPaise = priceInPaise,
            Currency = pack.Currency,
            Receipt = receipt,
            Status = PaymentStatus.Pending.ToString(),
            TierId = $"{pack.Code}:v{pack.Version}:id{pack.Id}",
            CreditsAwarded = credits,
            Description = $"Purchase of {credits} credits"
        };

        _context.PaymentTransactions.Add(transaction);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Saved pending payment transaction for User {UserId}, Order ID {OrderId}", userId, razorpayOrderId);

        // 6. Return response to mobile client
        return new CreateOrderResponseDto
        {
            PackId = pack.Id,
            Credits = credits,
            RazorpayOrderId = razorpayOrderId,
            AmountInPaise = priceInPaise,
            Currency = pack.Currency,
            Receipt = receipt,
            KeyId = _keyId
        };
    }

    public async Task<VerifyPaymentResponseDto> VerifyPaymentSignatureAsync(Guid userId, VerifyPaymentRequestDto request)
    {
        _logger.LogInformation("Verifying signature for Order ID {OrderId}, Payment ID {PaymentId}", request.RazorpayOrderId, request.RazorpayPaymentId);

        // 1. Verify standard HMAC-SHA256 signature
        var payload = $"{request.RazorpayOrderId}|{request.RazorpayPaymentId}";
        var computedSignature = ComputeHmacSha256(payload, _keySecret);

        if (!SignaturesMatch(computedSignature, request.RazorpaySignature))
        {
            _logger.LogWarning("Signature verification failed for order {OrderId}.", request.RazorpayOrderId);

            // Update transaction to Failed if found
            await _context.PaymentTransactions
                .Where(t => t.RazorpayOrderId == request.RazorpayOrderId &&
                            t.UserId == userId &&
                            t.Status == PaymentStatus.Pending.ToString())
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(t => t.Status, PaymentStatus.Failed.ToString())
                    .SetProperty(t => t.FailureReason, "Signature mismatch")
                    .SetProperty(t => t.ModifiedDate, DateTime.UtcNow));

            return new VerifyPaymentResponseDto
            {
                Success = false,
                Message = "Payment signature verification failed. The transaction is marked as failed."
            };
        }

        // The payment row is the idempotency gate. Exactly one caller (mobile
        // verification or webhook) can claim it and credit the broker wallet.
        var transaction = await _context.PaymentTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.RazorpayOrderId == request.RazorpayOrderId && t.UserId == userId);

        if (transaction == null)
        {
            _logger.LogError("Payment verification succeeded but transaction record was not found for Order ID {OrderId}", request.RazorpayOrderId);
            throw new KeyNotFoundException("Transaction record not found.");
        }

        var user = await _context.Users.FirstOrDefaultAsync(item => item.Id == userId);
        if (user?.BrokerId is null)
        {
            throw new KeyNotFoundException("A broker wallet is not linked to this user.");
        }

        // Already-successful legacy transactions are not auto-credited here:
        // doing so could double-credit manually reconciled purchases. The
        // reconciliation report handles those rows explicitly.
        if (transaction.Status == PaymentStatus.Success.ToString())
        {
            return new VerifyPaymentResponseDto
            {
                Success = true,
                Message = "Payment verified successfully.",
                NewBalance = await GetWalletTotalAsync(user.BrokerId.Value)
            };
        }

        await VerifyCapturedPaymentWithProviderAsync(transaction, request.RazorpayPaymentId);

        await using var databaseTransaction = await _context.Database.BeginTransactionAsync();
        var claimed = await _context.PaymentTransactions
            .Where(item => item.Id == transaction.Id && item.Status != PaymentStatus.Success.ToString())
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.RazorpayPaymentId, request.RazorpayPaymentId)
                .SetProperty(item => item.RazorpaySignature, request.RazorpaySignature)
                .SetProperty(item => item.Status, PaymentStatus.Success.ToString())
                .SetProperty(item => item.FailureReason, (string?)null)
                .SetProperty(item => item.ModifiedDate, DateTime.UtcNow));

        var newBalance = claimed == 1
            ? await CreditWalletForPaymentAsync(user, transaction)
            : await GetWalletTotalAsync(user.BrokerId.Value);
        await databaseTransaction.CommitAsync();

        _logger.LogInformation(
            "Verified payment {OrderId}; wallet balance for broker {BrokerId} is {Balance}.",
            request.RazorpayOrderId,
            user.BrokerId.Value,
            newBalance);

        return new VerifyPaymentResponseDto
        {
            Success = true,
            Message = "Payment verified successfully.",
            NewBalance = newBalance
        };
    }

    public async Task ProcessWebhookEventAsync(string rawJson, string signatureHeader)
    {
        _logger.LogInformation("Processing Razorpay Webhook notification");

        // A missing server-side webhook secret must never turn this public
        // endpoint into an unsigned wallet-credit path.
        if (string.IsNullOrWhiteSpace(_webhookSecret))
            throw new InvalidOperationException("Razorpay webhook authentication is not configured.");

        if (string.IsNullOrWhiteSpace(signatureHeader))
        {
            _logger.LogWarning("Webhook request is missing X-Razorpay-Signature header");
            throw new UnauthorizedAccessException("Missing webhook signature header");
        }

        var computedWebhookSig = ComputeHmacSha256(rawJson, _webhookSecret);
        if (!SignaturesMatch(computedWebhookSig, signatureHeader))
        {
            _logger.LogWarning("Webhook signature verification failed.");
            throw new UnauthorizedAccessException("Invalid webhook signature");
        }

        // 2. Parse Webhook Event
        using var doc = JsonDocument.Parse(rawJson);
        var root = doc.RootElement;

        string eventType = root.TryGetProperty("event", out var evProp) ? evProp.GetString() ?? string.Empty : string.Empty;
        _logger.LogInformation("Webhook Event Type: {Event}", eventType);

        if (eventType != "payment.captured" && eventType != "payment.failed" && eventType != "order.paid")
        {
            _logger.LogInformation("Ignoring unhandled webhook event: {Event}", eventType);
            return;
        }

        // Extract payment/order entity details
        if (!root.TryGetProperty("payload", out var payloadProp) ||
            !payloadProp.TryGetProperty("payment", out var paymentProp) ||
            !paymentProp.TryGetProperty("entity", out var entityProp))
        {
            _logger.LogWarning("Invalid webhook payload structure");
            return;
        }

        string orderId = entityProp.TryGetProperty("order_id", out var orderIdProp) ? orderIdProp.GetString() ?? string.Empty : string.Empty;
        string paymentId = entityProp.TryGetProperty("id", out var payIdProp) ? payIdProp.GetString() ?? string.Empty : string.Empty;

        if (string.IsNullOrEmpty(orderId))
        {
            _logger.LogWarning("Webhook payment entity does not contain an order_id");
            return;
        }

        // 3. Update Database using the payment status as an atomic idempotency gate.
        var transaction = await _context.PaymentTransactions
            .AsNoTracking()
            .FirstOrDefaultAsync(t => t.RazorpayOrderId == orderId);

        if (transaction == null)
        {
            _logger.LogWarning("Webhook received for order ID {OrderId} but no corresponding database transaction was found.", orderId);
            return;
        }

        if (eventType == "payment.captured" || eventType == "order.paid")
        {
            var providerStatus = entityProp.TryGetProperty("status", out var statusProp)
                ? statusProp.GetString()
                : null;
            var providerCurrency = entityProp.TryGetProperty("currency", out var currencyProp)
                ? currencyProp.GetString()
                : null;
            long providerAmount = 0;
            var hasAmount = entityProp.TryGetProperty("amount", out var amountProp) && amountProp.TryGetInt64(out providerAmount);

            if (string.IsNullOrWhiteSpace(paymentId) ||
                !string.Equals(providerStatus, "captured", StringComparison.OrdinalIgnoreCase) ||
                !hasAmount || providerAmount != transaction.AmountInPaise ||
                !string.Equals(providerCurrency, transaction.Currency, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "Rejected inconsistent captured-payment webhook for order {OrderId}. Status={Status}, Amount={Amount}, Currency={Currency}.",
                    orderId,
                    providerStatus,
                    hasAmount ? providerAmount : (long?)null,
                    providerCurrency);
                throw new InvalidOperationException("Webhook payment details do not match the pending order.");
            }

            var user = await _context.Users.FirstOrDefaultAsync(item => item.Id == transaction.UserId);
            if (user?.BrokerId is null)
            {
                _logger.LogError("Webhook cannot credit order {OrderId}: no broker wallet is linked.", orderId);
                return;
            }

            await using var databaseTransaction = await _context.Database.BeginTransactionAsync();
            var claimed = await _context.PaymentTransactions
                .Where(item => item.Id == transaction.Id && item.Status != PaymentStatus.Success.ToString())
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.RazorpayPaymentId, paymentId)
                    .SetProperty(item => item.Status, PaymentStatus.Success.ToString())
                    .SetProperty(item => item.FailureReason, (string?)null)
                    .SetProperty(item => item.ModifiedDate, DateTime.UtcNow));
            if (claimed == 1)
            {
                await CreditWalletForPaymentAsync(user, transaction);
            }
            await databaseTransaction.CommitAsync();
        }
        else if (eventType == "payment.failed")
        {
            string errorCode = entityProp.TryGetProperty("error_code", out var codeProp) ? codeProp.GetString() ?? string.Empty : string.Empty;
            string errorDesc = entityProp.TryGetProperty("error_description", out var descProp) ? descProp.GetString() ?? string.Empty : string.Empty;

            var reason = $"Razorpay Error: [{errorCode}] {errorDesc}";
            await _context.PaymentTransactions
                .Where(item => item.Id == transaction.Id && item.Status != PaymentStatus.Success.ToString())
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.RazorpayPaymentId, paymentId)
                    .SetProperty(item => item.Status, PaymentStatus.Failed.ToString())
                    .SetProperty(item => item.FailureReason, reason)
                    .SetProperty(item => item.ModifiedDate, DateTime.UtcNow));
        }
    }

    private async Task<int> CreditWalletForPaymentAsync(User user, PaymentTransaction payment)
    {
        var brokerId = user.BrokerId
            ?? throw new KeyNotFoundException("A broker wallet is not linked to this user.");
        var referenceKey = payment.Id.ToString("N");

        // The payment status claim prevents duplicate calls. The ledger key is
        // a second database-enforced guard against accidental double crediting.
        if (await _context.CreditTransactions.AnyAsync(item =>
                item.BrokerId == brokerId &&
                item.ReferenceType == "payment" &&
                item.ReferenceKey == referenceKey))
        {
            return await GetWalletTotalAsync(brokerId);
        }

        var wallet = (await _walletAccounting.LockAsync([brokerId])).SingleOrDefault()
            ?? throw new KeyNotFoundException("Credit wallet not found.");
        var now = DateTime.UtcNow;
        await _walletAccounting.SettleFreeCreditsAsync(wallet, now);
        _walletAccounting.CreditPaid(wallet, payment.CreditsAwarded, "payment", null, referenceKey,
            $"Razorpay order {payment.RazorpayOrderId}", now);
        var total = wallet.FreeCreditsBalance + wallet.PaidCreditsBalance;

        user.ModifiedDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return total;
    }

    private async Task<int> GetWalletTotalAsync(int brokerId)
    {
        var wallet = await _context.CreditWallets.AsNoTracking()
            .SingleOrDefaultAsync(item => item.BrokerId == brokerId)
            ?? throw new KeyNotFoundException("Credit wallet not found.");
        return wallet.FreeCreditsBalance + wallet.PaidCreditsBalance;
    }

    private async Task VerifyCapturedPaymentWithProviderAsync(PaymentTransaction transaction, string paymentId)
    {
        if (string.IsNullOrWhiteSpace(paymentId))
            throw new ArgumentException("Razorpay payment ID is required.", nameof(paymentId));

        var client = _httpClientFactory.CreateClient();
        var authBytes = Encoding.ASCII.GetBytes($"{_keyId}:{_keySecret}");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync($"https://api.razorpay.com/v1/payments/{Uri.EscapeDataString(paymentId)}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to validate Razorpay payment {PaymentId} with the provider.", paymentId);
            throw new InvalidOperationException("Could not validate the payment with the payment gateway.", ex);
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Razorpay payment lookup failed for {PaymentId} with status {StatusCode}.",
                paymentId,
                response.StatusCode);
            throw new InvalidOperationException("The payment gateway did not confirm this payment.");
        }

        using var document = JsonDocument.Parse(responseContent);
        var payment = document.RootElement;
        var providerPaymentId = payment.TryGetProperty("id", out var idProperty) ? idProperty.GetString() : null;
        var providerOrderId = payment.TryGetProperty("order_id", out var orderProperty) ? orderProperty.GetString() : null;
        var providerStatus = payment.TryGetProperty("status", out var statusProperty) ? statusProperty.GetString() : null;
        var providerCurrency = payment.TryGetProperty("currency", out var currencyProperty) ? currencyProperty.GetString() : null;
        long providerAmount = 0;
        var hasAmount = payment.TryGetProperty("amount", out var amountProperty) && amountProperty.TryGetInt64(out providerAmount);

        if (!string.Equals(providerPaymentId, paymentId, StringComparison.Ordinal) ||
            !string.Equals(providerOrderId, transaction.RazorpayOrderId, StringComparison.Ordinal) ||
            !string.Equals(providerStatus, "captured", StringComparison.OrdinalIgnoreCase) ||
            !hasAmount || providerAmount != transaction.AmountInPaise ||
            !string.Equals(providerCurrency, transaction.Currency, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Razorpay payment {PaymentId} did not match order {OrderId} or was not captured.",
                paymentId,
                transaction.RazorpayOrderId);
            throw new InvalidOperationException("The payment gateway did not confirm a matching captured payment.");
        }
    }

    private static string ComputeHmacSha256(string message, string secret)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var messageBytes = Encoding.UTF8.GetBytes(message);

        using var hmac = new HMACSHA256(keyBytes);
        var hashBytes = hmac.ComputeHash(messageBytes);
        
        // Convert to lowercase hex string
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    private static bool SignaturesMatch(string expected, string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided)) return false;

        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var providedBytes = Encoding.ASCII.GetBytes(provided.Trim().ToLowerInvariant());
        return expectedBytes.Length == providedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
