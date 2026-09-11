using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace PropSeekr.Models;

[Table("notifications")]
public class BrokerNotification
{
    [Key]
    public long Id { get; set; }

    [Column("broker_id")]
    public int BrokerId { get; set; }
    [ForeignKey("BrokerId")]
    public Broker? Broker { get; set; }

    [Column("connection_request_id")]
    public long? ConnectionRequestId { get; set; }
    public MatchConnectionRequest? ConnectionRequest { get; set; }

    [Required]
    [MaxLength(50)]
    public string Type { get; set; } = string.Empty; // match_found, confirm_pending, reminder, expiry_warning, credit_low

    [Required]
    [MaxLength(20)]
    public string Channel { get; set; } = "in_app"; // in_app, whatsapp

    [Column("payload", TypeName = "jsonb")]
    public string? PayloadJson { get; set; }

    [Column("channel_status")]
    [Required]
    [MaxLength(20)]
    public string ChannelStatus { get; set; } = "pending"; // pending, delivered, failed, read; read_at is the user-read authority

    [Column("read_at")]
    public DateTime? ReadAt { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
