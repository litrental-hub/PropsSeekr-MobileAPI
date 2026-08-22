using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class Payment
{
    public int Id { get; set; }

    public int BrokerId { get; set; }

    public int? CreditPackId { get; set; }

    public decimal Amount { get; set; }

    [StringLength(3)]
    public string Currency { get; set; } = "INR";

    [StringLength(50)]
    public string? Gateway { get; set; }

    [StringLength(255)]
    public string? GatewayTransactionId { get; set; }

    // initiated, success, failed, refunded
    public string Status { get; set; } = "initiated";

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
