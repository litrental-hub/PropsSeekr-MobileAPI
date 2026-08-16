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

    // Predefined pricing tiers to prevent price tampering
    private static readonly Dictionary<string, (int Credits, long PriceInPaise)> PricingTiers = new()
    {
        { "CREDITS_10", (10, 300000) },   // ₹3,000 (300,000 Paise)
        { "CREDITS_20", (20, 560000) },   // ₹5,600 (560,000 Paise)
        { "CREDITS_50", (50, 1250000) }   // ₹12,500 (1,250,000 Paise)
    };

    public RazorpayService(
        AppDbContext context,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<RazorpayService> logger)
    {
        _context = context;
        _httpClientFactory = httpClientFactory;
        _logger = logger;

        _keyId = RequireConfigurationValue(configuration, "Razorpay:KeyId");
        _keySecret = RequireConfigurationValue(configuration, "Razorpay:KeySecret");
        _webhookSecret = RequireConfigurationValue(configuration, "Razorpay:WebhookSecret");
    }

    public async Task<CreateOrderResponseDto> CreateOrderAsync(Guid userId, CreateOrderRequestDto request)
    {
        // 1. Validate package tier
        if (!PricingTiers.TryGetValue(request.TierId, out var tierDetails))
        {
            throw new ArgumentException($"Invalid subscription tier: {request.TierId}");
        }

        var (credits, priceInPaise) = tierDetails;
        var receipt = $"receipt_{Guid.NewGuid().ToString("N").Substring(0, 12)}";

        // 2. Prepare payload for Razorpay Orders API
        var orderPayload = new
        {
            amount = priceInPaise,
            currency = "INR",
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
            Currency = "INR",
            Receipt = receipt,
            Status = PaymentStatus.Pending.ToString(),
            TierId = request.TierId,
            CreditsAwarded = credits,
            Description = $"Purchase of {credits} credits"
        };

        _context.PaymentTransactions.Add(transaction);
        await _context.SaveChangesAsync();

        _logger.LogInformation("Saved pending payment transaction for User {UserId}, Order ID {OrderId}", userId, razorpayOrderId);

        // 6. Return response to mobile client
        return new CreateOrderResponseDto
        {
            RazorpayOrderId = razorpayOrderId,
            AmountInPaise = priceInPaise,
            Currency = "INR",
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

        if (!string.Equals(computedSignature, request.RazorpaySignature, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("Signature verification failed. Expected: {Expected}, Received: {Received}", computedSignature, request.RazorpaySignature);

            // Update transaction to Failed if found
            var failedTx = await _context.PaymentTransactions
                .FirstOrDefaultAsync(t => t.RazorpayOrderId == request.RazorpayOrderId && t.UserId == userId);
            if (failedTx != null && failedTx.Status == PaymentStatus.Pending.ToString())
            {
                failedTx.Status = PaymentStatus.Failed.ToString();
                failedTx.FailureReason = "Signature mismatch";
                failedTx.ModifiedDate = DateTime.UtcNow;
                await _context.SaveChangesAsync();
            }

            return new VerifyPaymentResponseDto
            {
                Success = false,
                Message = "Payment signature verification failed. The transaction is marked as failed."
            };
        }

        // 2. Update Database (Transaction Status & User Credits)
        var transaction = await _context.PaymentTransactions
            .FirstOrDefaultAsync(t => t.RazorpayOrderId == request.RazorpayOrderId && t.UserId == userId);

        if (transaction == null)
        {
            _logger.LogError("Payment verification succeeded but transaction record was not found for Order ID {OrderId}", request.RazorpayOrderId);
            throw new KeyNotFoundException("Transaction record not found.");
        }

        var user = await _context.Users.FindAsync(userId);
        if (user == null)
        {
            _logger.LogError("User {UserId} not found when applying payment credits.", userId);
            throw new KeyNotFoundException("User not found.");
        }

        // Handle idempotency (if webhook already set it to success)
        if (transaction.Status == PaymentStatus.Success.ToString())
        {
            _logger.LogInformation("Transaction {OrderId} was already processed successfully.", request.RazorpayOrderId);
            return new VerifyPaymentResponseDto
            {
                Success = true,
                Message = "Payment verified successfully.",
                NewBalance = user.Credits
            };
        }

        // Update transaction details
        transaction.RazorpayPaymentId = request.RazorpayPaymentId;
        transaction.RazorpaySignature = request.RazorpaySignature;
        transaction.Status = PaymentStatus.Success.ToString();
        transaction.ModifiedDate = DateTime.UtcNow;

        // Award Credits
        user.Credits += transaction.CreditsAwarded;
        user.ModifiedDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        _logger.LogInformation("Successfully verified payment. Awarded {Credits} credits to User {UserId}. New Balance: {NewBalance}", 
            transaction.CreditsAwarded, userId, user.Credits);

        return new VerifyPaymentResponseDto
        {
            Success = true,
            Message = "Payment verified successfully.",
            NewBalance = user.Credits
        };
    }

    public async Task ProcessWebhookEventAsync(string rawJson, string signatureHeader)
    {
        _logger.LogInformation("Processing Razorpay Webhook notification");

        // 1. Verify every webhook signature before parsing or processing its payload.
        if (string.IsNullOrEmpty(signatureHeader))
        {
            _logger.LogWarning("Webhook request is missing X-Razorpay-Signature header");
            throw new UnauthorizedAccessException("Missing webhook signature header");
        }

        var computedWebhookSig = ComputeHmacSha256(rawJson, _webhookSecret);
        if (!string.Equals(computedWebhookSig, signatureHeader, StringComparison.OrdinalIgnoreCase))
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

        // 3. Update Database (Idempotent Transaction Update)
        var transaction = await _context.PaymentTransactions
            .FirstOrDefaultAsync(t => t.RazorpayOrderId == orderId);

        if (transaction == null)
        {
            _logger.LogWarning("Webhook received for order ID {OrderId} but no corresponding database transaction was found.", orderId);
            return;
        }

        if (transaction.Status == PaymentStatus.Success.ToString())
        {
            _logger.LogInformation("Webhook skipped processing. Transaction {OrderId} is already in Success state.", orderId);
            return;
        }

        var user = await _context.Users.FindAsync(transaction.UserId);
        if (user == null)
        {
            _logger.LogError("Webhook processing failed: User {UserId} associated with transaction {OrderId} not found.", transaction.UserId, orderId);
            return;
        }

        if (eventType == "payment.captured" || eventType == "order.paid")
        {
            transaction.RazorpayPaymentId = paymentId;
            transaction.Status = PaymentStatus.Success.ToString();
            transaction.ModifiedDate = DateTime.UtcNow;

            user.Credits += transaction.CreditsAwarded;
            user.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Webhook applied: Transaction {OrderId} set to Success. Awarded {Credits} credits. New Balance: {Balance}", 
                orderId, transaction.CreditsAwarded, user.Credits);
        }
        else if (eventType == "payment.failed")
        {
            string errorCode = entityProp.TryGetProperty("error_code", out var codeProp) ? codeProp.GetString() ?? string.Empty : string.Empty;
            string errorDesc = entityProp.TryGetProperty("error_description", out var descProp) ? descProp.GetString() ?? string.Empty : string.Empty;

            transaction.RazorpayPaymentId = paymentId;
            transaction.Status = PaymentStatus.Failed.ToString();
            transaction.FailureReason = $"Razorpay Error: [{errorCode}] {errorDesc}";
            transaction.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            _logger.LogInformation("Webhook applied: Transaction {OrderId} set to Failed. Reason: {Reason}", orderId, transaction.FailureReason);
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

    private static string RequireConfigurationValue(IConfiguration configuration, string key)
    {
        return configuration[key] is { } value && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{key} configuration is missing.");
    }
}
