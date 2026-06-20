namespace PropSeekr.DTOs.Auth;

public class AdminLoginResponseDto
{
    public string Token { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public Guid AdminId { get; set; }

    public string UserName { get; set; } = string.Empty;

    public string Role { get; set; } = "Admin";
}
