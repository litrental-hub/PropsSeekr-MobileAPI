namespace PropSeekr.DTOs.Auth;

public class OtpResponseDto
{
    public string Message { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }
}
