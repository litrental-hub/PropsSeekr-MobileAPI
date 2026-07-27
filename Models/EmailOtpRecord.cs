using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class EmailOtpRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(255)]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MaxLength(255)]
    public string OtpHash { get; set; } = string.Empty;

    [Required]
    [MaxLength(50)]
    public string Purpose { get; set; } = "EmailVerification"; // EmailVerification, Login, PasswordReset

    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public int AttemptCount { get; set; } = 0;
    public bool IsUsed { get; set; } = false;
    public DateTime? UsedAt { get; set; }

    [MaxLength(50)]
    public string? RequestIp { get; set; }
}
