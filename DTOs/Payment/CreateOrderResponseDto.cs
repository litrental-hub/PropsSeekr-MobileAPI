namespace PropSeekr.DTOs.Payment;

public class CreateOrderResponseDto
{
    public string RazorpayOrderId { get; set; } = string.Empty;
    public long AmountInPaise { get; set; }
    public string Currency { get; set; } = "INR";
    public string Receipt { get; set; } = string.Empty;
    public string KeyId { get; set; } = string.Empty;
}
