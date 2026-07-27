using System.ComponentModel.DataAnnotations;

namespace PropSeekr.DTOs.Auth;

public class SendEmailOtpRequestDto
{
    [Required]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string Email { get; set; } = string.Empty;

    public string Purpose { get; set; } = "EmailVerification"; // EmailVerification, Login, PasswordReset
}

public class SendEmailOtpResponseDto
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = "If the email is valid, a verification code has been sent.";
}

public class VerifyEmailOtpRequestDto
{
    [Required]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string Email { get; set; } = string.Empty;

    [Required]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Verification code must be 6 digits")]
    public string Otp { get; set; } = string.Empty;

    public string Purpose { get; set; } = "EmailVerification";
}

public class VerifyEmailOtpResponseDto
{
    public bool Success { get; set; } = true;
    public string Message { get; set; } = "Email verification successful.";
    public string? Token { get; set; }
    public string? RefreshToken { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public AuthenticatedUserDto? User { get; set; }
}
