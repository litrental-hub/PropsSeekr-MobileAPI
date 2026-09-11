namespace PropSeekr.DTOs.Auth;

public class RegisterResponseDto
{
    /// <summary>Present only when local development verification is explicitly bypassed.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Short-lived registration reference; it is not a user identity.</summary>
    public Guid? PendingRegistrationId { get; set; }

    public string Message { get; set; } = string.Empty;

    public bool VerificationRequired { get; set; }

    public string? VerificationChannel { get; set; }
}
