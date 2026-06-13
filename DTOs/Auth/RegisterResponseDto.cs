namespace PropSeekr.DTOs.Auth;

public class RegisterResponseDto
{
    public Guid UserId { get; set; }

    public string Message { get; set; } = string.Empty;
}