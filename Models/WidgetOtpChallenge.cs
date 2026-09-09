using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

// No provider access token or OTP is persisted. Keep consumed hashes to reject replay.
public class WidgetOtpChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(10)] public string MobileNumber { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public Guid? PendingRegistrationId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    [MaxLength(64)] public string? ConsumedTokenHash { get; set; }
}
