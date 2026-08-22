using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("disputes")]
public class Dispute
{
    [Key]
    public int Id { get; set; }

    [Column("broker_id")]
    public int BrokerId { get; set; }
    [ForeignKey("BrokerId")]
    public Broker? Broker { get; set; }

    [Column("transaction_id")]
    public long? TransactionId { get; set; }
    [ForeignKey("TransactionId")]
    public CreditTransaction? Transaction { get; set; }

    [Required]
    public string Reason { get; set; } = string.Empty;

    [Required]
    [MaxLength(20)]
    public string Status { get; set; } = "open"; // open, under_review, resolved, rejected

    [Column("resolution_type")]
    [MaxLength(30)]
    public string? ResolutionType { get; set; }

    [Column("resolved_amount")]
    public int? ResolvedAmount { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("resolved_at")]
    public DateTime? ResolvedAt { get; set; }
}
