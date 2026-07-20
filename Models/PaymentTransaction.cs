using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public enum PaymentStatus
{
    Pending,
    Success,
    Failed
}

public class PaymentTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }
    public User? User { get; set; }

    [Required]
    [MaxLength(100)]
    public string RazorpayOrderId { get; set; } = string.Empty;

    [MaxLength(100)]
    public string? RazorpayPaymentId { get; set; }

    [MaxLength(255)]
    public string? RazorpaySignature { get; set; }

    public long AmountInPaise { get; set; }

    [Required]
    [MaxLength(10)]
    public string Currency { get; set; } = "INR";

    [Required]
    [MaxLength(100)]
    public string Receipt { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = PaymentStatus.Pending.ToString();

    [Required]
    [MaxLength(50)]
    public string TierId { get; set; } = string.Empty;

    public int CreditsAwarded { get; set; }

    [MaxLength(500)]
    public string? Description { get; set; }

    public string? FailureReason { get; set; }

    public DateTime CreatedDate { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedDate { get; set; } = DateTime.UtcNow;
}
