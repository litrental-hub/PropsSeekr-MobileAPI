namespace PropSeekr.DTOs.Auth;

public class OtpResponseDto
{
    public bool Success { get; set; } = true;
    public WidgetOtpSessionDto? Widget { get; set; }
    public string Status { get; set; } = "SUCCESS";

    public string Message { get; set; } = string.Empty;

    public int ExpiryMinutes { get; set; } = 5;

    public DateTime ExpiresAt { get; set; }
}
