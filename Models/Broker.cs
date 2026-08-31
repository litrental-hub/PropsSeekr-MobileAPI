using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("brokers")]
public class Broker
{
    [Key]
    [Column("brokerid")]
    public int Id { get; set; }

    [Column("phone_number")]
    [Required]
    [MaxLength(50)]
    public string PhoneNumber { get; set; } = string.Empty;

    [Column("name")]
    [MaxLength(255)]
    public string? Name { get; set; }

    [Column("response_score")]
    public decimal? ResponseScore { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string? Status { get; set; }

    [Column("created_at")]
    public DateTime? CreatedAt { get; set; }

    [Column("last_active_at")]
    public DateTime? LastActiveAt { get; set; }

    // New columns added by dual handshake schema
    [Column("confirmation_compliance_rate")]
    public decimal ConfirmationComplianceRate { get; set; } = 100.00m;

    [Column("visibility_penalty_flag")]
    public bool VisibilityPenaltyFlag { get; set; } = false;

    [Column("visibility_penalty_expires_at")]
    public DateTime? VisibilityPenaltyExpiresAt { get; set; }

    [Column("locality")]
    [MaxLength(255)]
    public string? Locality { get; set; }

    [Column("brokerage_name")]
    [MaxLength(255)]
    public string? BrokerageName { get; set; }
}
