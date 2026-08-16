using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class TrustedAppInstance
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    [MaxLength(20)] public string Platform { get; set; } = string.Empty;
    [MaxLength(255)] public string KeyId { get; set; } = string.Empty;
    public string? PublicKeySpkiBase64 { get; set; }
    [MaxLength(50)] public string Status { get; set; } = "Trusted";
    [MaxLength(50)] public string? AppVersion { get; set; }
    [MaxLength(50)] public string? Environment { get; set; }
    public long AssertionCounter { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public bool IsRevoked { get; set; }
}
