using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class AppAttestationChallenge
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    [MaxLength(20)] public string Platform { get; set; } = string.Empty;
    [MaxLength(50)] public string Purpose { get; set; } = string.Empty;
    [MaxLength(128)] public string Nonce { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    [MaxLength(64)] public string? VerifiedRequestHash { get; set; }
    [MaxLength(20)] public string? VerifiedPlatform { get; set; }
    public DateTime? VerifiedAt { get; set; }
    public DateTime? UsedAt { get; set; }
}
