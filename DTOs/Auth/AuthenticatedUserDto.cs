namespace PropSeekr.DTOs.Auth;

public class AuthenticatedUserDto
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string MobileNumber { get; set; } = string.Empty;

    public string? Email { get; set; }

    public bool IsMobileVerified { get; set; }
}
