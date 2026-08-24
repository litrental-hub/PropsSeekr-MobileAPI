namespace PropSeekr.DTOs.Auth;

public class VerifyOtpResponseDto
{
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public Guid UserId { get; set; }

    public int? BrokerId { get; set; }

    public string UserName { get; set; } = string.Empty;
}
