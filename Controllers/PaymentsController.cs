using Microsoft.AspNetCore.Mvc;

namespace PropSeekr.Controllers;

/// <summary>Retired duplicate payment surface. Use /api/v1/payment Razorpay endpoints.</summary>
[ApiController]
[Route("api/v1/payments")]
public sealed class PaymentsController : ControllerBase
{
    [HttpGet("{paymentId}")]
    [HttpPost("initiate")]
    [HttpPost("webhook")]
    public IActionResult Retired() => StatusCode(StatusCodes.Status410Gone, new
    {
        message = "This legacy payment endpoint is retired. Use /api/v1/payment/order, /verify, and /webhook."
    });
}
