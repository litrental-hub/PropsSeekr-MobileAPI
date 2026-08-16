using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using PropSeekr.DTOs.Payment;
using PropSeekr.Services.Interfaces;
using PropSeekr.Authorization;

namespace PropSeekr.Controllers;

[ApiController]
[Route("api/v1/payment")]
public class PaymentController : ControllerBase
{
    private readonly IRazorpayService _razorpayService;
    private readonly ILogger<PaymentController> _logger;
    private readonly ICurrentUserContext _currentUser;

    public PaymentController(
        IRazorpayService razorpayService,
        ILogger<PaymentController> logger,
        ICurrentUserContext currentUser)
    {
        _razorpayService = razorpayService;
        _logger = logger;
        _currentUser = currentUser;
    }

    [Authorize(Policy = "CustomerPolicy")]
    [Authorize(Policy = "AppAttestedSensitiveActionPolicy")]
    [AppAttestationPurpose("PaymentOrder")]
    [HttpPost("order")]
    public async Task<IActionResult> CreateOrder([FromBody] CreateOrderRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var response = await _razorpayService.CreateOrderAsync(userId, request);
            return Ok(response);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error creating payment order for user {UserId}", userId);
            return BadRequest(new { message = ex.Message });
        }
    }

    [Authorize(Policy = "CustomerPolicy")]
    [Authorize(Policy = "AppAttestedSensitiveActionPolicy")]
    [AppAttestationPurpose("PaymentVerify")]
    [HttpPost("verify")]
    public async Task<IActionResult> VerifyPayment([FromBody] VerifyPaymentRequestDto request)
    {
        if (!TryGetCurrentUserId(out var userId))
        {
            return Unauthorized(new { message = "Invalid authenticated user." });
        }

        try
        {
            var response = await _razorpayService.VerifyPaymentSignatureAsync(userId, request);
            if (!response.Success)
            {
                return BadRequest(response);
            }
            return Ok(response);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying payment signature for user {UserId}, Order {OrderId}", userId, request.RazorpayOrderId);
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("webhook")]
    public async Task<IActionResult> Webhook()
    {
        // 1. Retrieve the signature header
        Request.Headers.TryGetValue("X-Razorpay-Signature", out var signatureValues);
        var signatureHeader = signatureValues.ToString();

        // 2. Read raw request body as string
        using var reader = new StreamReader(Request.Body);
        var rawJson = await reader.ReadToEndAsync();

        try
        {
            await _razorpayService.ProcessWebhookEventAsync(rawJson, signatureHeader);
            return Ok(); // Razorpay expects a 200 OK status code to acknowledge receipt
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogWarning(ex, "Unauthorized webhook access attempt.");
            return Unauthorized(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing Razorpay webhook.");
            // We still return 200/Ok or 400 depending on flow, but standard is to return 200/400.
            // Returning BadRequest will prompt Razorpay to retry later.
            return BadRequest(new { message = "Webhook processing error." });
        }
    }

    private bool TryGetCurrentUserId(out Guid userId)
    {
        return _currentUser.TryGetLocalUserId(out userId);
    }
}
