namespace PropSeekr.DTOs.Auth;

public class AuthenticatedUserDto
{
    public Guid Id { get; set; }
    public int? BrokerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string MobileNumber { get; set; } = string.Empty;
    public string? Email { get; set; }
    public bool IsMobileVerified { get; set; }
    public string Role { get; set; } = "User";
}

public class LoginResponseDto
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string Role { get; set; } = "User";
    public AuthenticatedUserDto User { get; set; } = new();
}
