namespace PropSeekr.DTOs.Payment;

public class VerifyPaymentResponseDto
{
    public bool Success { get; set; }
    public string Message { get; set; } = string.Empty;
    public int NewBalance { get; set; }
}
