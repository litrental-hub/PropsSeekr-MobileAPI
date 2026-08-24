using System.ComponentModel.DataAnnotations;

namespace PropSeekr.Models;

public class CreditTransaction
{
    public long Id { get; set; } // BIGSERIAL

    public int BrokerId { get; set; }
    public Broker? Broker { get; set; }

    // grant, purchase, deduct, refund, expiry
    public string Type { get; set; } = string.Empty;

    public int Amount { get; set; } // always positive; type gives direction

    public int BalanceAfter { get; set; }

    public string? ReferenceType { get; set; } // reveal, payment, dispute, monthly_grant
    public long? ReferenceId { get; set; }
    public string? ReferenceKey { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAt { get; set; }
}
