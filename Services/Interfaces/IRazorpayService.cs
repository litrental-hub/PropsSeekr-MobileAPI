using PropSeekr.DTOs.Payment;

namespace PropSeekr.Services.Interfaces;

public interface IRazorpayService
{
    /// <summary>
    /// Creates a Razorpay Order based on the selected package tier and records it in the database.
    /// </summary>
    Task<CreateOrderResponseDto> CreateOrderAsync(Guid userId, CreateOrderRequestDto request);

    /// <summary>
    /// Verifies the payment signature sent by the mobile app client and marks the transaction as successful.
    /// </summary>
    Task<VerifyPaymentResponseDto> VerifyPaymentSignatureAsync(Guid userId, VerifyPaymentRequestDto request);

    /// <summary>
    /// Safely parses and verifies Razorpay webhook event payload, modifying payment statuses and credits idempotently.
    /// </summary>
    Task ProcessWebhookEventAsync(string rawJson, string signatureHeader);
}
