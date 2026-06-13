using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class OtpVerification
{
    public Guid Id { get; set; }

    [MaxLength(10)]
    public string MobileNumber { get; set; } = string.Empty;

    [MaxLength(6)]
    public string OtpCode { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public bool IsUsed { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
}
